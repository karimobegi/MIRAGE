using System;
using System.Collections;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// GPU MI-GAN inpainting using contextual object/cluster crops. Inputs and
/// masks remain on the GPU; the output is a full-frame straight RGBA overlay.
/// </summary>
public class MIGANRunner : InpaintingRunner, IEffectHandler
{
    public EffectType EffectType => EffectType.Inpainting;
    public InpaintingSetting[] SelectedClasses = Array.Empty<InpaintingSetting>();
    public RawImage MaskImage;
    public RawImage DebugOutputImage;

    [Header("Contextual crops")]
    [Range(0, 1)] public float ContextFraction = 0.35f;
    [Min(0)] public int MinimumContextPixels = 48;
    [Tooltip("One inference per frame by default. Higher budgets improve detail for separated targets at additional GPU cost.")]
    [Range(1, 16)] public int MaximumCropsPerFrame = 1;
    [Tooltip("Mask expansion in segmentation-mask pixels.")]
    [Range(0, 16)] public int DilationPixels = 3;
    [Tooltip("Soft outer edge in segmentation-mask pixels. The original silhouette stays opaque.")]
    [Range(0, 16)] public int FeatherPixels = 2;

    [Header("Motion-compensated history (experimental)")]
    [Tooltip("Reuse only when visible-background block matching and complete mask coverage pass. Otherwise run MI-GAN.")]
    public bool EnableTemporalReuse = false;
    [Range(1, 8)] public int MaximumReusedFrames = 2;
    [Range(0.02f, 1)] public float MaximumReuseSeconds = 0.2f;
    [Range(128, 512)] public int MotionResolution = 256;
    [Range(2, 8)] public int MotionSearchRadius = 4;
    [Range(0.001f, 0.2f)] public float MaximumPhotometricError = 0.035f;
    [Range(0, 0.05f)] public float MinimumMotionUniqueness = 0.003f;
    public bool LastRunReusedHistory { get; private set; }
    public int LastCropCount { get; private set; }

    private InpaintingMaskBuilder masks;
    private ComputeShader processing;
    private int prepareCropKernel, compositeKernel, motionKernel, resetStatsKernel, warpKernel;
    private Tensor<float> inputTensor, inputMaskTensor;
    // PeekOutput is borrowed from worker and must never be disposed here.
    private Tensor<float> outputTensor;
    private RenderTexture cropResult, directMask;
    private RenderTexture previousFrame, previousOverlay, previousMask, flow, warpedOverlay;
    private ComputeBuffer reuseStats;
    private readonly List<Rect> crops = new List<Rect>();
    private int preparedWidth, preparedHeight, lastSourceWidth, lastSourceHeight, settingsHash;
    private bool historyValid, disposed;
    private int reusedFrames, historyGeneration;
    private double lastModelRefreshTime;

    protected override void PrepareModel()
    {
        preparedWidth = InputWidth;
        preparedHeight = InputHeight;
        masks = new InpaintingMaskBuilder(MaxObjects);
        processing = InpaintingMaskBuilder.LoadShader("Models/MI-GAN/InpaintingProcessing");
        prepareCropKernel = processing.FindKernel("PrepareCrop");
        compositeKernel = processing.FindKernel("CompositeCrop");
        motionKernel = processing.FindKernel("EstimateMotion");
        resetStatsKernel = processing.FindKernel("ResetReuseStats");
        warpKernel = processing.FindKernel("WarpHistory");
        inputTensor = new Tensor<float>(new TensorShape(1, 3, InputHeight, InputWidth));
        inputMaskTensor = new Tensor<float>(new TensorShape(1, 1, InputHeight, InputWidth));
        cropResult = InpaintingMaskBuilder.CreateTexture(InputWidth, InputHeight, RenderTextureFormat.ARGB32, "MI-GAN crop result");
        EnsureOutputTexture();

        var model = ModelLoader.Load(ModelAsset);
        var graph = new FunctionalGraph();
        var image = graph.AddInput(DataType.Float, new DynamicTensorShape(1, 3, InputHeight, InputWidth));
        var hole = graph.AddInput(DataType.Float, new DynamicTensorShape(1, 1, InputHeight, InputWidth));
        // The repository's MI-GAN export expects 1=known, 0=hole. Our public
        // masks use 1=remove, and the crop kernel already makes them binary.
        var result = Functional.Forward(model, image, 1 - Functional.Ceil(hole));
        runtimeModel = graph.Compile(result[0]);
        settingsHash = ComputeSettingsHash();
    }

    public void UpdateClasses()
    {
        if (SelectedClasses == null) SelectedClasses = Array.Empty<InpaintingSetting>();
        ResetHistory();
    }

    public void UpdateClasses(List<EffectSetting> classes)
    {
        if (classes == null) SelectedClasses = Array.Empty<InpaintingSetting>();
        else
        {
            var selected = new List<InpaintingSetting>();
            foreach (var setting in classes)
                if (setting is InpaintingSetting inpainting) selected.Add(inpainting);
            SelectedClasses = selected.ToArray();
        }
        UpdateClasses();
    }

    public override IEnumerator RunModel(Texture input, YOLOSegmentationRunner yolo, ComputeBuffer depthBuffer)
    {
        ValidateInput(input);
        LastRunReusedHistory = false;
        LastCropCount = 0;
        int currentSettings = ComputeSettingsHash();
        if (currentSettings != settingsHash)
        {
            ResetHistory();
            settingsHash = currentSettings;
        }
        if (!EnableTemporalReuse && previousFrame != null) ReleaseHistoryTextures();
        if (!IsEnabled)
        {
            ClearOutput();
            yield break;
        }
        if (EnableTemporalReuse) EnsureHistoryTextures();
        int runHistoryGeneration = historyGeneration;

        // This copies selection/masks before its first yield. Pipeline supplies
        // an immutable camera snapshot for the complete inference operation.
        yield return masks.Build(yolo, depthBuffer, SelectedClasses, UseDepthFilter, DilationPixels, FeatherPixels);
        if (!masks.HasTargets)
        {
            ClearOutput();
            yield break;
        }

        if (MaskImage != null) MaskImage.texture = masks.AlphaMask;
        if (EnableTemporalReuse)
        {
            yield return TryReuseHistory(input, masks.AlphaMask);
        }
        if (!LastRunReusedHistory)
        {
            InpaintingCropUtility.BuildRegions(masks.PixelBounds, masks.MaskWidth, masks.MaskHeight,
                OutputWidth, OutputHeight, (float)InputWidth / InputHeight, ContextFraction,
                MinimumContextPixels, DilationPixels + FeatherPixels + 1, MaximumCropsPerFrame, crops);
            yield return RunCrops(input, masks.AlphaMask);
            reusedFrames = 0;
            lastModelRefreshTime = Time.realtimeSinceStartupAsDouble;
        }

        if (EnableTemporalReuse && runHistoryGeneration == historyGeneration && currentSettings == ComputeSettingsHash())
            StoreHistory(input, masks.AlphaMask);
        PublishOutput();
    }

    public override IEnumerator RunModel(Texture input, ComputeBuffer mask, ComputeBuffer depthBuffer)
    {
        // A raw instance buffer does not carry its dimensions or class-ID
        // buffer. Require the typed overload instead of guessing those contracts.
        throw new NotSupportedException("Pass the YOLOSegmentationRunner, or use the image/removal-mask texture overload.");
    }

    /// <summary>Standalone diagnostic input: image and white=remove mask.</summary>
    public override IEnumerator RunModel(params Texture[] inputs)
    {
        if (inputs == null || inputs.Length < 2 || inputs[1] == null)
            throw new ArgumentException("MI-GAN requires an image and a white=remove mask.");
        ValidateInput(inputs[0]);
        ResetHistory();
        if (directMask == null || directMask.width != OutputWidth || directMask.height != OutputHeight)
        {
            InpaintingMaskBuilder.ReleaseTexture(directMask);
            directMask = InpaintingMaskBuilder.CreateTexture(OutputWidth, OutputHeight, RenderTextureFormat.RFloat, "MI-GAN diagnostic mask");
        }
        Graphics.Blit(inputs[1], directMask);
        crops.Clear();
        Rect region = InpaintingCropUtility.FitAspect(new Rect(0, 0, OutputWidth, OutputHeight),
            OutputWidth, OutputHeight, (float)InputWidth / InputHeight);
        crops.Add(new Rect(region.x / OutputWidth, region.y / OutputHeight, region.width / OutputWidth, region.height / OutputHeight));
        yield return RunCrops(inputs[0], directMask);
        if (MaskImage != null) MaskImage.texture = directMask;
        PublishOutput();
    }

    private IEnumerator RunCrops(Texture input, RenderTexture alphaMask)
    {
        InpaintingMaskBuilder.ClearTexture(outputTexture);
        LastCropCount = crops.Count;
        foreach (var crop in crops)
        {
            var rect = new Vector4(crop.x, crop.y, crop.width, crop.height);
            processing.SetInt("InputIsLinear", QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
            processing.SetInt("ModelWidth", InputWidth);
            processing.SetInt("ModelHeight", InputHeight);
            processing.SetVector("CropRect", rect);
            processing.SetTexture(prepareCropKernel, "Frame", input);
            processing.SetTexture(prepareCropKernel, "AlphaMask", alphaMask);
            processing.SetBuffer(prepareCropKernel, "ImageTensor", ComputeTensorData.Pin(inputTensor).buffer);
            processing.SetBuffer(prepareCropKernel, "HoleTensor", ComputeTensorData.Pin(inputMaskTensor).buffer);
            processing.Dispatch(prepareCropKernel, Mathf.CeilToInt(InputWidth / 8f), Mathf.CeilToInt(InputHeight / 8f), 1);
            schedule = worker.ScheduleIterable(inputTensor, inputMaskTensor);
            yield return RunInference();

            processing.SetInt("Width", OutputWidth);
            processing.SetInt("Height", OutputHeight);
            processing.SetVector("CropRect", rect);
            processing.SetTexture(compositeKernel, "AlphaMask", alphaMask);
            processing.SetTexture(compositeKernel, "CropResult", cropResult);
            processing.SetTexture(compositeKernel, "Overlay", outputTexture);
            processing.Dispatch(compositeKernel, Mathf.CeilToInt(OutputWidth / 8f), Mathf.CeilToInt(OutputHeight / 8f), 1);
        }
    }

    protected override void PeekOutput()
    {
        outputTensor = worker.PeekOutput(0) as Tensor<float>;
        if (outputTensor == null) throw new InvalidOperationException("MI-GAN must return a float RGB image tensor.");
    }

    // No dense output readback: tensor-to-texture and compositing are GPU work
    // queued after inference on the same graphics queue.
    protected override bool RequestsDone() => true;

    protected override void ReadOutput()
    {
        TextureConverter.RenderToTexture(outputTensor, cropResult,
            new TextureTransform().SetCoordOrigin(CoordOrigin.TopLeft));
    }

    public override void DisposeOutput()
    {
        // Inputs are reused until destruction. The worker owns PeekOutput.
        outputTensor = null;
    }

    private IEnumerator TryReuseHistory(Texture input, RenderTexture alphaMask)
    {
        if (!historyValid || reusedFrames >= Mathf.Max(1, MaximumReusedFrames) ||
            Time.realtimeSinceStartupAsDouble - lastModelRefreshTime >= Mathf.Max(0.02f, MaximumReuseSeconds))
            yield break;
        int generation = historyGeneration;
        int reuseSettings = ComputeSettingsHash();

        int motionWidth = Mathf.Clamp(MotionResolution, 128, 512);
        int motionHeight = Mathf.Max(1, Mathf.RoundToInt(motionWidth * (float)OutputHeight / OutputWidth));
        processing.SetInt("MotionWidth", motionWidth);
        processing.SetInt("MotionHeight", motionHeight);
        processing.SetInt("FlowWidth", flow.width);
        processing.SetInt("FlowHeight", flow.height);
        processing.SetInt("SearchRadius", Mathf.Clamp(MotionSearchRadius, 2, 8));
        processing.SetFloat("MaxPhotometricError", Mathf.Clamp(MaximumPhotometricError, 0.001f, 0.2f));
        processing.SetFloat("MinMotionUniqueness", Mathf.Clamp(MinimumMotionUniqueness, 0, 0.05f));
        processing.SetTexture(motionKernel, "Frame", input);
        processing.SetTexture(motionKernel, "PreviousFrame", previousFrame);
        processing.SetTexture(motionKernel, "AlphaMask", alphaMask);
        processing.SetTexture(motionKernel, "PreviousMask", previousMask);
        processing.SetTexture(motionKernel, "FlowResult", flow);
        processing.Dispatch(motionKernel, Mathf.CeilToInt(flow.width / 8f), Mathf.CeilToInt(flow.height / 8f), 1);
        processing.SetBuffer(resetStatsKernel, "ReuseStats", reuseStats);
        processing.Dispatch(resetStatsKernel, 1, 1, 1);
        processing.SetInt("Width", OutputWidth);
        processing.SetInt("Height", OutputHeight);
        processing.SetTexture(warpKernel, "AlphaMask", alphaMask);
        processing.SetTexture(warpKernel, "Flow", flow);
        processing.SetTexture(warpKernel, "PreviousOverlay", previousOverlay);
        processing.SetTexture(warpKernel, "WarpedOverlay", warpedOverlay);
        processing.SetBuffer(warpKernel, "ReuseStats", reuseStats);
        processing.Dispatch(warpKernel, Mathf.CeilToInt(OutputWidth / 8f), Mathf.CeilToInt(OutputHeight / 8f), 1);

        // Two counters decide whether every target pixel has a trustworthy
        // motion-compensated fill. Any disocclusion triggers fresh inference.
        uint targetPixels, rejectedPixels;
        if (SystemInfo.supportsAsyncGPUReadback)
        {
            var request = AsyncGPUReadback.Request(reuseStats);
            while (!request.done) yield return null;
            if (request.hasError) yield break;
            var stats = request.GetData<uint>();
            targetPixels = stats[0];
            rejectedPixels = stats[1];
        }
        else
        {
            var stats = new uint[2];
            reuseStats.GetData(stats);
            targetPixels = stats[0];
            rejectedPixels = stats[1];
        }
        if (generation != historyGeneration || reuseSettings != ComputeSettingsHash() ||
            Time.realtimeSinceStartupAsDouble - lastModelRefreshTime >= Mathf.Max(0.02f, MaximumReuseSeconds) ||
            targetPixels == 0 || rejectedPixels != 0) yield break;
        Graphics.Blit(warpedOverlay, outputTexture);
        reusedFrames++;
        LastRunReusedHistory = true;
    }

    private void StoreHistory(Texture input, RenderTexture alphaMask)
    {
        Graphics.Blit(input, previousFrame);
        Graphics.Blit(outputTexture, previousOverlay);
        Graphics.Blit(alphaMask, previousMask);
        historyValid = true;
    }

    private void EnsureHistoryTextures()
    {
        int motionWidth = Mathf.Clamp(MotionResolution, 128, 512);
        int motionHeight = Mathf.Max(1, Mathf.RoundToInt(motionWidth * (float)OutputHeight / OutputWidth));
        int flowWidth = Mathf.CeilToInt(motionWidth / 8f);
        int flowHeight = Mathf.CeilToInt(motionHeight / 8f);
        if (previousFrame != null && previousFrame.width == OutputWidth && previousFrame.height == OutputHeight &&
            flow != null && flow.width == flowWidth && flow.height == flowHeight) return;
        ReleaseHistoryTextures();
        previousFrame = InpaintingMaskBuilder.CreateTexture(OutputWidth, OutputHeight, RenderTextureFormat.ARGB32, "MI-GAN previous input");
        previousOverlay = InpaintingMaskBuilder.CreateTexture(OutputWidth, OutputHeight, RenderTextureFormat.ARGB32, "MI-GAN previous fill");
        previousMask = InpaintingMaskBuilder.CreateTexture(OutputWidth, OutputHeight, RenderTextureFormat.RFloat, "MI-GAN previous alpha");
        warpedOverlay = InpaintingMaskBuilder.CreateTexture(OutputWidth, OutputHeight, RenderTextureFormat.ARGB32, "MI-GAN warped fill");
        flow = InpaintingMaskBuilder.CreateTexture(flowWidth, flowHeight, RenderTextureFormat.ARGBFloat, "MI-GAN backward motion/confidence");
        reuseStats = new ComputeBuffer(2, sizeof(uint));
        ResetHistory();
    }

    private void EnsureOutputTexture()
    {
        if (outputTexture != null && outputTexture.width == OutputWidth && outputTexture.height == OutputHeight) return;
        InpaintingMaskBuilder.ReleaseTexture(outputTexture);
        outputTexture = InpaintingMaskBuilder.CreateTexture(OutputWidth, OutputHeight, RenderTextureFormat.ARGB32, "MI-GAN full-frame overlay");
        ResetHistory();
    }

    private void ValidateInput(Texture input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (InputWidth != preparedWidth || InputHeight != preparedHeight)
            throw new InvalidOperationException("MI-GAN tensor dimensions changed at runtime. Restart the runner with matching model dimensions.");
        if (lastSourceWidth != input.width || lastSourceHeight != input.height)
        {
            lastSourceWidth = input.width;
            lastSourceHeight = input.height;
            ResetHistory();
        }
        EnsureOutputTexture();
    }

    public override void ResetHistory()
    {
        historyGeneration++;
        historyValid = false;
        reusedFrames = 0;
        LastRunReusedHistory = false;
    }

    private void ClearOutput()
    {
        ResetHistory();
        InpaintingMaskBuilder.ClearTexture(outputTexture);
        PublishOutput();
    }

    private void PublishOutput()
    {
        OutputFrameId = InputFrameId;
        UpdateOutputImage();
        if (PublishOutputToUI && DebugOutputImage != null) DebugOutputImage.texture = outputTexture;
    }

    private int ComputeSettingsHash()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ContextFraction.GetHashCode();
            hash = hash * 31 + MinimumContextPixels;
            hash = hash * 31 + MaximumCropsPerFrame;
            hash = hash * 31 + DilationPixels;
            hash = hash * 31 + FeatherPixels;
            hash = hash * 31 + UseDepthFilter.GetHashCode();
            hash = hash * 31 + EnableTemporalReuse.GetHashCode();
            hash = hash * 31 + MaximumReusedFrames;
            hash = hash * 31 + MaximumReuseSeconds.GetHashCode();
            hash = hash * 31 + MotionResolution;
            hash = hash * 31 + MotionSearchRadius;
            hash = hash * 31 + MaximumPhotometricError.GetHashCode();
            hash = hash * 31 + MinimumMotionUniqueness.GetHashCode();
            if (SelectedClasses != null)
                foreach (var setting in SelectedClasses)
                {
                    if (setting == null) continue;
                    hash = hash * 31 + setting.ClassID;
                    hash = hash * 31 + setting.MinRange.GetHashCode();
                    hash = hash * 31 + setting.MaxRange.GetHashCode();
                }
            return hash;
        }
    }

    private void ReleaseHistoryTextures()
    {
        InpaintingMaskBuilder.ReleaseTexture(previousFrame);
        InpaintingMaskBuilder.ReleaseTexture(previousOverlay);
        InpaintingMaskBuilder.ReleaseTexture(previousMask);
        InpaintingMaskBuilder.ReleaseTexture(flow);
        InpaintingMaskBuilder.ReleaseTexture(warpedOverlay);
        previousFrame = previousOverlay = previousMask = flow = warpedOverlay = null;
        reuseStats?.Release();
        reuseStats = null;
        ResetHistory();
    }

    private void OnDisable()
    {
        ResetHistory();
        InpaintingMaskBuilder.ClearTexture(outputTexture);
    }

    private void OnDestroy()
    {
        if (disposed) return;
        disposed = true;
        worker?.Dispose();
        worker = null;
        outputTensor = null;
        inputTensor?.Dispose();
        inputMaskTensor?.Dispose();
        masks?.Dispose();
        InpaintingMaskBuilder.ReleaseTexture(outputTexture);
        InpaintingMaskBuilder.ReleaseTexture(cropResult);
        InpaintingMaskBuilder.ReleaseTexture(directMask);
        outputTexture = cropResult = directMask = null;
        ReleaseHistoryTextures();
        if (processing != null) Destroy(processing);
    }
}
