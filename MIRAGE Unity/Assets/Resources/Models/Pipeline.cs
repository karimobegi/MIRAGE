using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ByteTrackCSharp;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Additional mediated viewpoint rendered from the same segmentation, tracking, and depth pass
/// (for example, an asymmetric passenger viewpoint in a Dual-Viewpoint CAVE).
/// </summary>
[Serializable]
public sealed class SecondaryViewpoint
{
    public string Name = "Secondary Viewpoint";
    public Transform PostProcessorsRoot;
    public RectTransform CPUPostProcessingContainer;
    public RawImage OutputImage;
    public RawImage PostProcessingOutputImage;
    public RawImage ImagePostprocessingOutputImage;
    public RawImage InpaintingOutputImage;
    public InpaintingRunner InpaintingModel;

    [NonSerialized] internal CPUPostProcessor[] CpuPostProcessors = Array.Empty<CPUPostProcessor>();
    [NonSerialized] internal PostProcessor[] GpuPostProcessors = Array.Empty<PostProcessor>();
    [NonSerialized] internal RenderTexture PostProcessingOverlay;
    [NonSerialized] internal RenderTexture ImagePostProcessingOverlay;
    [NonSerialized] internal RenderTexture PresentedInpainting;
    [NonSerialized] internal bool OverlayCleared = true;
    [NonSerialized] internal bool ImageOverlayCleared = true;
    [NonSerialized] internal bool InpaintingCleared = true;
}

/// <summary>
/// Processes an immutable camera snapshot. Segmentation and depth may overlap,
/// but their buffers remain unchanged until all dependent effects finish.
/// The previous complete frame stays on screen during the next inference.
/// </summary>
public class Pipeline : MonoBehaviour
{
    public bool RunAsSequence;
    [Tooltip("Keep GPU->CPU metadata readbacks and ByteTrack active even when all CPU post-processors (Icon, Info, Bounds, Transform) are off.")]
    public bool ForceCpuMetadataReadback;
    [Tooltip("Skip running the depth model on frames where no GPU/CPU post-processor or inpainting selection is active.")]
    public bool AutoSkipDepthWhenIdle;
    public RawImage OutputImage;
    public RawImage PostProcessingOutputImage;
    public RawImage ImagePostprocessingOutputImage;
    public RectTransform CPUPostProcessingContainer;
    public TMP_Text segmentationTimeText;
    public TMP_Text depthEstimationTimeText;
    public TMP_Text inpaintingTimeText;
    public TMP_Text postProcessingTimeText;

    [SerializeField] private CameraInput sourceInput;
    [SerializeField] private Transform postProcessorsRoot;
    [SerializeField] private SecondaryViewpoint[] secondaryViewpoints = Array.Empty<SecondaryViewpoint>();
    [SerializeField] private YOLOSegmentationRunner segmentationModel;
    [SerializeField] private DepthEstimationRunner depthModel;
    [SerializeField] private InpaintingRunner inpaintingModel;
    public InpaintingRunner ActiveInpaintingModel => inpaintingModel;
    public CameraInput ActiveSourceInput => sourceInput != null ? sourceInput : CameraInput.Instance;
    public SecondaryViewpoint[] SecondaryViewpoints => secondaryViewpoints;

    public void SetSourceInput(CameraInput input) { sourceInput = input; }
    public void SetPostProcessorsRoot(Transform root) { postProcessorsRoot = root; }
    public void SetSecondaryViewpoints(SecondaryViewpoint[] viewpoints)
    {
        secondaryViewpoints = viewpoints ?? Array.Empty<SecondaryViewpoint>();
    }

    public long PublishedFrameId { get; private set; } = -1;
    public double PublishedCaptureTime { get; private set; }
    // Source arrival to graphics-queue publication, not physical display latency.
    public double LastFrameLatencyMilliseconds { get; private set; }
    public long PublishedFrameCount { get; private set; }

    private CPUPostProcessor[] cpuPostProcessors;
    private PostProcessor[] gpuPostProcessors;
    private RenderTexture postProcessingOverlay, imagePostProcessingOverlay;
    private RenderTexture processingFrame, presentedFrame, presentedInpainting;
    private IBenchmarkManager benchmarkManager;
    private BYTETracker tracker;
    private readonly List<ByteTrackCSharp.Object> trackInputs = new List<ByteTrackCSharp.Object>(64);
    public List<LabelledSTrack> labelledTracks = new List<LabelledSTrack>();
    private bool overlayCleared = true;
    private bool imageOverlayCleared = true;
    private bool presentedInpaintingCleared = true;
    private bool wasTrackingCpuObjects;
    private long lastSourceVersion = -1;
    private long lastConsumedFrame = -1;
    private CameraInput lastSource;
    private double depthStartedAt;
    private int settingsRevision;

    public void NotifySettingsChanged()
    {
        settingsRevision++;
        // Paused recordings still need one new result when their effects change.
        lastConsumedFrame = -1;
    }

    private void Awake()
    {
        if (segmentationModel == null) segmentationModel = FindAnyObjectByType<YOLOSegmentationRunner>();
        if (depthModel == null) depthModel = FindAnyObjectByType<DepthEstimationRunner>();
        if (inpaintingModel == null)
        {
            var candidates = FindObjectsByType<InpaintingRunner>(FindObjectsSortMode.None)
                .Where(r => r.enabled).ToArray();
            if (candidates.Length == 1) inpaintingModel = candidates[0];
            else if (candidates.Length > 1)
                Debug.LogError("Assign the desired Inpainting Model on Pipeline when multiple runners are enabled.", this);
        }
    }

    private void Start()
    {
        // Every runner Awake has finished before we access its prepared buffers.
        if (segmentationModel == null || !segmentationModel.IsPrepared || depthModel == null)
        {
            Debug.LogError("Pipeline needs a prepared segmentation model and a depth runner.", this);
            enabled = false;
            return;
        }
        if (depthModel.OutputWidth != segmentationModel.OutputWidth || depthModel.OutputHeight != segmentationModel.OutputHeight)
        {
            Debug.LogError("Depth and segmentation output dimensions must match for per-object depth.", this);
            enabled = false;
            return;
        }
        postProcessingOverlay = CreateTexture(segmentationModel.OutputWidth, segmentationModel.OutputHeight, "MIRAGE effects");
        imagePostProcessingOverlay = CreateTexture(segmentationModel.ImageWidth, segmentationModel.ImageHeight, "MIRAGE image effects");
        processingFrame = CreateTexture(segmentationModel.ImageWidth, segmentationModel.ImageHeight, "MIRAGE processing frame");
        presentedFrame = CreateTexture(segmentationModel.ImageWidth, segmentationModel.ImageHeight, "MIRAGE presented frame");
        if (inpaintingModel != null)
        {
            inpaintingModel.PublishOutputToUI = false;
            presentedInpainting = CreateTexture(inpaintingModel.OutputWidth, inpaintingModel.OutputHeight, "MIRAGE published inpainting");
            if (inpaintingModel.OutputImage != null) inpaintingModel.OutputImage.texture = presentedInpainting;
        }
        if (secondaryViewpoints == null) secondaryViewpoints = Array.Empty<SecondaryViewpoint>();
        var secondaryRoots = secondaryViewpoints
            .Where(viewpoint => viewpoint != null && viewpoint.PostProcessorsRoot != null)
            .Select(viewpoint => viewpoint.PostProcessorsRoot)
            .ToArray();

        cpuPostProcessors = DiscoverProcessors<CPUPostProcessor>(postProcessorsRoot, secondaryRoots);
        foreach (var processor in cpuPostProcessors)
            processor.Initialize(segmentationModel, depthModel, CPUPostProcessingContainer, this);
        gpuPostProcessors = DiscoverProcessors<PostProcessor>(postProcessorsRoot, secondaryRoots);
        foreach (var processor in gpuPostProcessors)
            processor.Initialize(segmentationModel, depthModel,
                processor is ImagePostProcessor ? imagePostProcessingOverlay : postProcessingOverlay);

        foreach (var viewpoint in secondaryViewpoints)
        {
            if (viewpoint == null) continue;
            string viewName = string.IsNullOrWhiteSpace(viewpoint.Name) ? "secondary" : viewpoint.Name;
            viewpoint.PostProcessingOverlay = CreateTexture(segmentationModel.OutputWidth, segmentationModel.OutputHeight, $"MIRAGE effects ({viewName})");
            viewpoint.ImagePostProcessingOverlay = CreateTexture(segmentationModel.ImageWidth, segmentationModel.ImageHeight, $"MIRAGE image effects ({viewName})");
            if (viewpoint.InpaintingModel != null && viewpoint.InpaintingModel != inpaintingModel)
            {
                viewpoint.InpaintingModel.PublishOutputToUI = false;
                viewpoint.PresentedInpainting = CreateTexture(viewpoint.InpaintingModel.OutputWidth, viewpoint.InpaintingModel.OutputHeight, $"MIRAGE inpainting ({viewName})");
                if (viewpoint.InpaintingModel.OutputImage != null) viewpoint.InpaintingModel.OutputImage.texture = viewpoint.PresentedInpainting;
                if (viewpoint.InpaintingOutputImage != null) viewpoint.InpaintingOutputImage.texture = viewpoint.PresentedInpainting;
            }
            else if (viewpoint.InpaintingOutputImage != null && (viewpoint.InpaintingModel == inpaintingModel || viewpoint.InpaintingModel == null))
            {
                viewpoint.InpaintingOutputImage.texture = viewpoint.InpaintingModel == inpaintingModel ? presentedInpainting : null;
            }
            viewpoint.CpuPostProcessors = viewpoint.PostProcessorsRoot != null
                ? viewpoint.PostProcessorsRoot.GetComponentsInChildren<CPUPostProcessor>(false)
                : Array.Empty<CPUPostProcessor>();
            foreach (var processor in viewpoint.CpuPostProcessors)
                processor.Initialize(segmentationModel, depthModel, viewpoint.CPUPostProcessingContainer, this);

            viewpoint.GpuPostProcessors = viewpoint.PostProcessorsRoot != null
                ? viewpoint.PostProcessorsRoot.GetComponentsInChildren<PostProcessor>(false)
                : Array.Empty<PostProcessor>();
            foreach (var processor in viewpoint.GpuPostProcessors)
                processor.Initialize(segmentationModel, depthModel,
                    processor is ImagePostProcessor ? viewpoint.ImagePostProcessingOverlay : viewpoint.PostProcessingOverlay);
        }

        var manager = FindAnyObjectByType<BenchmarkManager>();
        benchmarkManager = manager != null ? manager : new NullBenchmarkManager();
        benchmarkManager.SetParallelMode(!RunAsSequence);
        ResetTracking();
        StartCoroutine(RunPipeline());
    }

    private IEnumerator RunPipeline()
    {
        while (enabled)
        {
            var source = ActiveSourceInput;
            if (source == null || !source.HasFrame || source.CurrentFrame == null ||
                (source == lastSource && source.SourceVersion == lastSourceVersion && source.FrameId == lastConsumedFrame))
            {
                yield return null;
                continue;
            }
            if (source != lastSource || source.SourceVersion != lastSourceVersion)
            {
                lastSource = source;
                lastSourceVersion = source.SourceVersion;
                ResetTracking();
                depthModel.ResetOutput();
                if (inpaintingModel != null) inpaintingModel.ResetHistory();
                foreach (var viewpoint in secondaryViewpoints)
                {
                    if (viewpoint?.InpaintingModel != null && viewpoint.InpaintingModel != inpaintingModel)
                        viewpoint.InpaintingModel.ResetHistory();
                }
            }
            long frameId = source.FrameId;
            long sourceVersion = source.SourceVersion;
            double capturedAt = source.FrameTimestamp;
            int frameSettingsRevision = settingsRevision;
            lastConsumedFrame = frameId;
            Graphics.Blit(source.CurrentFrame, processingFrame);
            bool needCpuMetadata = ForceCpuMetadataReadback || HasActiveCpuPostProcessors();
            segmentationModel.RequireCpuMetadataReadback = needCpuMetadata;
            depthModel.RequireCpuDepthReadback = needCpuMetadata;

            segmentationModel.SetInputFrame(frameId, capturedAt);
            depthModel.SetInputFrame(frameId, capturedAt);
            bool skipIdleDepth = AutoSkipDepthWhenIdle && !needCpuMetadata && !HasAnyActiveGpuOrInpaintingEffect();
            bool useDepth = depthModel.IsEnabled && depthModel.IsPrepared && !skipIdleDepth;
            depthModel.ActiveThisFrame = useDepth;
            if (!useDepth) depthModel.ResetOutput();
            benchmarkManager.StartIteration();

            if (RunAsSequence)
            {
                yield return RunSegmentation(processingFrame);
                if (useDepth) yield return RunDepth(processingFrame);
            }
            else
            {
                bool segmentationDone = false, depthDone = !useDepth;
                StartCoroutine(Complete(RunSegmentation(processingFrame), () => segmentationDone = true));
                if (useDepth) StartCoroutine(Complete(RunDepth(processingFrame), () => depthDone = true));
                while (!segmentationDone || !depthDone) yield return null;
            }

            // No next segmentation starts while downstream models read its buffers.
            if (segmentationModel.OutputFrameId != frameId)
            {
                benchmarkManager.EndIteration();
                yield return null;
                continue;
            }
            if (useDepth)
            {
                if (depthModel.OutputFrameId == frameId)
                    yield return depthModel.CalculateObjectDepthsAsync(segmentationModel.OutputBuffer, segmentationModel.NumObjDetected);
                else depthModel.ResetOutput();
                SetTiming(depthEstimationTimeText, "Depth including aligned object reduction", depthStartedAt);
                benchmarkManager.EndDepthEstimation();
            }
            if (needCpuMetadata)
            {
                if (!wasTrackingCpuObjects)
                {
                    ResetTracking();
                    wasTrackingCpuObjects = true;
                }
                UpdateTracks();
            }
            else if (wasTrackingCpuObjects)
            {
                ResetTracking();
                wasTrackingCpuObjects = false;
            }

            bool didInpaint = inpaintingModel != null && inpaintingModel.IsEnabled && inpaintingModel.IsPrepared && inpaintingModel.HasActiveSelection;
            if (didInpaint)
            {
                inpaintingModel.SetInputFrame(frameId, capturedAt);
                inpaintingModel.UseDepthFilter = useDepth;
                benchmarkManager.StartInpainting();
                double startedAt = Time.realtimeSinceStartupAsDouble;
                yield return inpaintingModel.RunModel(processingFrame, segmentationModel, depthModel.ObjectDepthBuffer);
                SetTiming(inpaintingTimeText, "Inpainting", startedAt);
                benchmarkManager.EndInpainting();
                didInpaint = inpaintingModel.OutputFrameId == frameId;
            }

            foreach (var viewpoint in secondaryViewpoints)
            {
                var secondaryInpaint = viewpoint?.InpaintingModel;
                if (secondaryInpaint == null || secondaryInpaint == inpaintingModel || !secondaryInpaint.IsEnabled ||
                    !secondaryInpaint.IsPrepared || !secondaryInpaint.HasActiveSelection)
                    continue;
                secondaryInpaint.SetInputFrame(frameId, capturedAt);
                secondaryInpaint.UseDepthFilter = useDepth;
                yield return secondaryInpaint.RunModel(processingFrame, segmentationModel, depthModel.ObjectDepthBuffer);
            }

            // A seek/source switch invalidates the whole in-flight frame.
            if (source != ActiveSourceInput || source.SourceVersion != sourceVersion || settingsRevision != frameSettingsRevision)
            {
                benchmarkManager.EndIteration();
                yield return null;
                continue;
            }
            var previous = presentedFrame;
            presentedFrame = processingFrame;
            processingFrame = previous;
            if (presentedInpainting != null)
            {
                if (didInpaint && inpaintingModel.OutputTexture != null)
                {
                    Graphics.Blit(inpaintingModel.OutputTexture, presentedInpainting);
                    presentedInpaintingCleared = false;
                }
                else if (!presentedInpaintingCleared)
                {
                    Clear(presentedInpainting);
                    presentedInpaintingCleared = true;
                }
                if (inpaintingModel.OutputImage != null) inpaintingModel.OutputImage.texture = presentedInpainting;
            }
            foreach (var viewpoint in secondaryViewpoints)
            {
                if (viewpoint == null) continue;
                var secondaryInpaint = viewpoint.InpaintingModel;
                if (viewpoint.PresentedInpainting != null)
                {
                    bool secondaryDidInpaint = secondaryInpaint != null && secondaryInpaint.IsEnabled &&
                        secondaryInpaint.IsPrepared && secondaryInpaint.HasActiveSelection &&
                        secondaryInpaint.OutputFrameId == frameId && secondaryInpaint.OutputTexture != null;
                    if (secondaryDidInpaint)
                    {
                        Graphics.Blit(secondaryInpaint.OutputTexture, viewpoint.PresentedInpainting);
                        viewpoint.InpaintingCleared = false;
                    }
                    else if (!viewpoint.InpaintingCleared)
                    {
                        Clear(viewpoint.PresentedInpainting);
                        viewpoint.InpaintingCleared = true;
                    }
                    if (secondaryInpaint.OutputImage != null) secondaryInpaint.OutputImage.texture = viewpoint.PresentedInpainting;
                    if (viewpoint.InpaintingOutputImage != null) viewpoint.InpaintingOutputImage.texture = viewpoint.PresentedInpainting;
                }
                else if (viewpoint.InpaintingOutputImage != null && secondaryInpaint != null && secondaryInpaint == inpaintingModel)
                {
                    viewpoint.InpaintingOutputImage.texture = presentedInpainting;
                }
            }
            PostProcessing(presentedFrame);
            PublishedFrameId = frameId;
            PublishedCaptureTime = capturedAt;
            LastFrameLatencyMilliseconds = (Time.realtimeSinceStartupAsDouble - capturedAt) * 1000.0;
            PublishedFrameCount++;
            benchmarkManager.EndIteration();
            yield return null;
        }
    }

    private IEnumerator RunSegmentation(Texture frame)
    {
        benchmarkManager.StartSegmentation();
        double start = Time.realtimeSinceStartupAsDouble;
        yield return segmentationModel.RunModel(frame);
        SetTiming(segmentationTimeText, "Segmentation", start);
        benchmarkManager.EndSegmentation();
    }

    private IEnumerator RunDepth(Texture frame)
    {
        benchmarkManager.StartDepthEstimation();
        depthStartedAt = Time.realtimeSinceStartupAsDouble;
        yield return depthModel.RunModel(frame);
        // Reduction/readback closes this interval after both independent models finish.
    }

    private static IEnumerator Complete(IEnumerator stage, Action done)
    {
        try { yield return stage; }
        finally { done(); }
    }

    private void ResetTracking()
    {
        tracker = new BYTETracker(o => new LabelledSTrack(o.rect, o.prob, o.label, o.detection));
        labelledTracks.Clear();
    }

    private void UpdateTracks()
    {
        int count = segmentationModel.NumObjDetected;
        trackInputs.Clear();
        if (trackInputs.Capacity < count) trackInputs.Capacity = count;
        var boxes = segmentationModel.BBoxes;
        var labels = segmentationModel.LabelIDs;
        var scores = segmentationModel.Scores;
        for (int i = 0; i < count; i++)
        {
            float w = boxes[i * 4 + 2], h = boxes[i * 4 + 3];
            trackInputs.Add(new ByteTrackCSharp.Object(new ByteTrackCSharp.Rect(boxes[i * 4] - w / 2f,
                boxes[i * 4 + 1] - h / 2f, w, h), labels[i], scores[i], i));
        }
        var updated = tracker.update(trackInputs);
        labelledTracks.Clear();
        for (int i = 0; i < updated.Count; i++)
        {
            if (updated[i] is LabelledSTrack labelled)
                labelledTracks.Add(labelled);
        }
    }

    private bool HasActiveCpuPostProcessors()
    {
        if (cpuPostProcessors != null)
        {
            for (int i = 0; i < cpuPostProcessors.Length; i++)
                if (cpuPostProcessors[i] != null && cpuPostProcessors[i].enabled && cpuPostProcessors[i].IsRunning)
                    return true;
        }
        if (secondaryViewpoints != null)
        {
            for (int v = 0; v < secondaryViewpoints.Length; v++)
            {
                var cpu = secondaryViewpoints[v]?.CpuPostProcessors;
                if (cpu == null) continue;
                for (int i = 0; i < cpu.Length; i++)
                    if (cpu[i] != null && cpu[i].enabled && cpu[i].IsRunning)
                        return true;
            }
        }
        return false;
    }

    private bool HasAnyActiveGpuOrInpaintingEffect()
    {
        if (inpaintingModel != null && inpaintingModel.IsEnabled && inpaintingModel.IsPrepared && inpaintingModel.HasActiveSelection)
            return true;
        if (gpuPostProcessors != null)
        {
            for (int i = 0; i < gpuPostProcessors.Length; i++)
                if (gpuPostProcessors[i] != null && gpuPostProcessors[i].enabled && gpuPostProcessors[i].IsRunning)
                    return true;
        }
        if (secondaryViewpoints != null)
        {
            for (int v = 0; v < secondaryViewpoints.Length; v++)
            {
                var vp = secondaryViewpoints[v];
                if (vp == null) continue;
                if (vp.InpaintingModel != null && vp.InpaintingModel.IsEnabled && vp.InpaintingModel.IsPrepared && vp.InpaintingModel.HasActiveSelection)
                    return true;
                if (vp.GpuPostProcessors == null) continue;
                for (int i = 0; i < vp.GpuPostProcessors.Length; i++)
                    if (vp.GpuPostProcessors[i] != null && vp.GpuPostProcessors[i].enabled && vp.GpuPostProcessors[i].IsRunning)
                        return true;
            }
        }
        return false;
    }

    private void PostProcessing(RenderTexture frame)
    {
        benchmarkManager.StartPostProcessing();
        double start = Time.realtimeSinceStartupAsDouble;
        ExecuteViewpointPostProcessing(frame, postProcessingOverlay, imagePostProcessingOverlay,
            ref overlayCleared, ref imageOverlayCleared,
            gpuPostProcessors, cpuPostProcessors, OutputImage, PostProcessingOutputImage, ImagePostprocessingOutputImage);
        foreach (var viewpoint in secondaryViewpoints)
        {
            if (viewpoint == null) continue;
            ExecuteViewpointPostProcessing(frame, viewpoint.PostProcessingOverlay, viewpoint.ImagePostProcessingOverlay,
                ref viewpoint.OverlayCleared, ref viewpoint.ImageOverlayCleared,
                viewpoint.GpuPostProcessors, viewpoint.CpuPostProcessors,
                viewpoint.OutputImage, viewpoint.PostProcessingOutputImage, viewpoint.ImagePostprocessingOutputImage);
        }
        SetTiming(postProcessingTimeText, "Post processing submission", start);
        benchmarkManager.EndPostProcessing();
    }

    private static void ExecuteViewpointPostProcessing(
        RenderTexture frame,
        RenderTexture overlay,
        RenderTexture imageOverlay,
        ref bool overlayCleared,
        ref bool imageOverlayCleared,
        PostProcessor[] gpuProcessors,
        CPUPostProcessor[] cpuProcessors,
        RawImage outputTarget,
        RawImage overlayTarget,
        RawImage imageOverlayTarget)
    {
        bool needOverlay = false;
        bool needImageOverlay = false;
        if (gpuProcessors != null)
        {
            for (int i = 0; i < gpuProcessors.Length; i++)
            {
                var processor = gpuProcessors[i];
                if (processor == null || !processor.enabled || !processor.IsRunning) continue;
                if (processor is ImagePostProcessor)
                {
                    if (!(processor is ImageCopyPostProcessor)) needImageOverlay = true;
                }
                else if (processor.ClassSettings != null && processor.ClassSettings.Length > 0)
                {
                    needOverlay = true;
                }
            }
        }

        if (overlay != null)
        {
            if (needOverlay)
            {
                Clear(overlay);
                overlayCleared = false;
            }
            else if (!overlayCleared)
            {
                Clear(overlay);
                overlayCleared = true;
            }
        }

        if (imageOverlay != null)
        {
            if (needImageOverlay)
            {
                Clear(imageOverlay);
                imageOverlayCleared = false;
            }
            else if (!imageOverlayCleared)
            {
                Clear(imageOverlay);
                imageOverlayCleared = true;
            }
        }

        if (gpuProcessors != null && (needOverlay || needImageOverlay || HasRunningImageCopy(gpuProcessors)))
        {
            foreach (var processor in gpuProcessors)
            {
                if (processor == null || !processor.enabled || !processor.IsRunning) continue;
                if (processor is ImagePostProcessor imageProcessor) imageProcessor.ExecuteShader(frame);
                else processor.ExecuteShader();
            }
        }
        if (cpuProcessors != null)
        {
            foreach (var processor in cpuProcessors)
                if (processor != null && processor.enabled && processor.IsRunning) processor.Execute();
        }
        if (outputTarget != null) outputTarget.texture = frame;
        if (overlayTarget != null) overlayTarget.texture = overlay;
        if (imageOverlayTarget != null) imageOverlayTarget.texture = imageOverlay;
    }

    private static bool HasRunningImageCopy(PostProcessor[] gpuProcessors)
    {
        for (int i = 0; i < gpuProcessors.Length; i++)
            if (gpuProcessors[i] is ImageCopyPostProcessor copy && copy.enabled && copy.IsRunning)
                return true;
        return false;
    }

    private static T[] DiscoverProcessors<T>(Transform root, Transform[] excludedRoots) where T : MonoBehaviour
    {
        if (root != null) return root.GetComponentsInChildren<T>(false);
        var discovered = FindObjectsByType<T>(FindObjectsSortMode.None);
        if (excludedRoots == null || excludedRoots.Length == 0) return discovered;
        return discovered.Where(item => !excludedRoots.Any(excluded => item.transform != null && item.transform.IsChildOf(excluded))).ToArray();
    }

    private static void SetTiming(TMP_Text text, string stage, double start)
    {
        if (text != null) text.text = $"{stage}: {(Time.realtimeSinceStartupAsDouble - start) * 1000.0:F1} ms";
    }

    private static RenderTexture CreateTexture(int width, int height, string label)
    {
        var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            { name = label, enableRandomWrite = true };
        texture.Create();
        Clear(texture);
        return texture;
    }

    private static void Clear(RenderTexture texture)
    {
        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
    }

    private void OnDestroy()
    {
        StopAllCoroutines();
        if (inpaintingModel != null) inpaintingModel.PublishOutputToUI = true;
        foreach (var texture in new[] { processingFrame, presentedFrame, presentedInpainting, postProcessingOverlay, imagePostProcessingOverlay })
        {
            if (texture == null) continue;
            texture.Release();
            Destroy(texture);
        }
        if (secondaryViewpoints == null) return;
        foreach (var viewpoint in secondaryViewpoints)
        {
            if (viewpoint == null) continue;
            if (viewpoint.InpaintingModel != null && viewpoint.InpaintingModel != inpaintingModel)
                viewpoint.InpaintingModel.PublishOutputToUI = true;
            foreach (var texture in new[] { viewpoint.PresentedInpainting, viewpoint.PostProcessingOverlay, viewpoint.ImagePostProcessingOverlay })
            {
                if (texture == null) continue;
                texture.Release();
                Destroy(texture);
            }
        }
    }
}
