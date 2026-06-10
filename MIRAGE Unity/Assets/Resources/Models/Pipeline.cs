using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ByteTrackCSharp;
using System.Linq;
using System.Collections.Generic;

/// <summary>
/// Main Script that initializes and executes the pipeline
/// 
/// Author: J-Britten
/// </summary>
public class Pipeline : MonoBehaviour
{
    public bool RunAsSequence = false;

    public RawImage OutputImage;
    public RawImage PostProcessingOutputImage;
    public RawImage ImagePostprocessingOutputImage;

    public RectTransform CPUPostProcessingContainer;
    public TMP_Text segmentationTimeText;
    public TMP_Text depthEstimationTimeText;
    public TMP_Text inpaintingTimeText;
    public TMP_Text postProcessingTimeText;

    private YOLOSegmentationRunner segmentationModel;
    private DepthEstimationRunner depthModel;
    private InpaintingRunner inpaintingModel;

    private CPUPostProcessor[] cpuPostProcessors; 
    
    private PostProcessor[] gpuPostProcessors;
    private RenderTexture postProcessingOverlay;
    private RenderTexture imagePostProcessingOverlay;
    private RenderTexture inputTexture { get => CameraInput.Instance.CurrentFrame; }

    // Benchmark integration
    private IBenchmarkManager benchmarkManager;

    private BYTETracker tracker;
    public List<LabelledSTrack> labelledTracks;

#region Initialization
    void Awake()
    {     
       InitializePipeline();
    }

    void Start()
    {
        if (RunAsSequence)
        {
            benchmarkManager.SetParallelMode(false);
            StartCoroutine(RunSequentialPipeline());
        } else {
            benchmarkManager.SetParallelMode(true);
            StartCoroutine(RunSegmentation());
            StartCoroutine(RunDepthEstimation());
            StartCoroutine(RunInpainting());
            StartCoroutine(RunPostProcessing());
        }
    }

    /// <summary>
    /// Initialize the pipeline
    /// </summary>
    private void InitializePipeline()
    {
        //Find the models
        segmentationModel = FindAnyObjectByType<YOLOSegmentationRunner>();
        inpaintingModel = FindAnyObjectByType<InpaintingRunner>();
        depthModel = FindAnyObjectByType<DepthEstimationRunner>();

        //Setup render textures
        postProcessingOverlay = new RenderTexture(segmentationModel.OutputWidth, segmentationModel.OutputHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
        postProcessingOverlay.enableRandomWrite = true;
        postProcessingOverlay.Create();

        imagePostProcessingOverlay = new RenderTexture(segmentationModel.ImageWidth, segmentationModel.ImageHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
        imagePostProcessingOverlay.enableRandomWrite = true;
        imagePostProcessingOverlay.Create();

        //Initialize postprocessors
        if (segmentationModel is YOLOSegmentationRunner)
        {

            cpuPostProcessors = FindObjectsByType<CPUPostProcessor>(FindObjectsSortMode.None);
            foreach (var processor in cpuPostProcessors)
            {
                processor.Initialize(segmentationModel as YOLOSegmentationRunner, depthModel, CPUPostProcessingContainer);
            }
        }

        gpuPostProcessors = FindObjectsByType<PostProcessor>(FindObjectsSortMode.None);
        foreach (var processor in FindObjectsByType<PostProcessor>(FindObjectsSortMode.None))
        {

            if (processor is ImagePostProcessor)
            {
                var imageProcessor = processor as ImagePostProcessor;
                imageProcessor.Initialize(segmentationModel, depthModel, imagePostProcessingOverlay);
            }
            else
            {
                processor.Initialize(segmentationModel, depthModel, postProcessingOverlay);
            }
        }        // Initialize Benchmark Manager
        var benchmarkManagerComponent = FindAnyObjectByType<BenchmarkManager>();
        benchmarkManager = benchmarkManagerComponent != null ? benchmarkManagerComponent : new NullBenchmarkManager(); //Null Object Pattern

        tracker = new BYTETracker(obj => new LabelledSTrack(obj.rect, obj.prob, obj.label));
        labelledTracks = new List<LabelledSTrack>();
    }
#endregion

#region Object Tracking Function
    private void getTracks(){
        float[] bboxes = segmentationModel.BBoxes;
        int[] labelIDs = segmentationModel.LabelIDs;
        float[] scores = segmentationModel.Scores; 
        List<ByteTrackCSharp.Object> objects = new List<ByteTrackCSharp.Object>();

        for(int i = 0; i < segmentationModel.NumObjDetected; i++){
            float cx = bboxes[i * 4 + 0];
            float cy = bboxes[i * 4 + 1];
            float w  = bboxes[i * 4 + 2];
            float h  = bboxes[i * 4 + 3];
            var rect = new ByteTrackCSharp.Rect(cx - w / 2f, cy - h / 2f, w, h);

            int label = labelIDs[i];
            float score = scores[i];
            objects.Add(new ByteTrackCSharp.Object(rect, label, score));
            }

            List<STrack> tracks = tracker.update(objects);
            labelledTracks = tracks.Cast<LabelledSTrack>().ToList();
    }
#endregion

#region Parallel Execution
    private IEnumerator RunSegmentation()
    {
        while (true)
        {
            // Start segmentation timing
            benchmarkManager.StartSegmentation();

            float startTime = Time.realtimeSinceStartup;
            yield return segmentationModel.RunModel(inputTexture);

            getTracks();

            float endTime = Time.realtimeSinceStartup;
            float deltaTime = endTime - startTime;

            // End segmentation timing
            benchmarkManager.EndSegmentation();

            segmentationTimeText.text = "Segmentation Time: " + deltaTime.ToString("F4") + "s\nFPS: " + (1.0f / deltaTime).ToString("F2");
        }
    }

    private IEnumerator RunDepthEstimation()
    {

        while (true)
        {
            if (depthModel.IsEnabled)
            {
                // Start depth estimation timing
                benchmarkManager.StartDepthEstimation();

                float startTime = Time.realtimeSinceStartup;
                yield return depthModel.RunModel(inputTexture);
                depthModel.CalculateObjectDepths(segmentationModel.OutputBuffer, segmentationModel.NumObjDetected);
                float endTime = Time.realtimeSinceStartup;
                float deltaTime = endTime - startTime;

                // End depth estimation timing
                benchmarkManager.EndDepthEstimation();

                depthEstimationTimeText.text = "Depth Estimation Time: " + deltaTime.ToString("F4") + "s\nFPS: " + (1.0f / deltaTime).ToString("F2");
            }
            else
            {
                yield return null;
            }
        }
    }

    private IEnumerator RunInpainting()
    {
        while (true)
        {
            if (inpaintingModel.IsEnabled)
            {
                // Start inpainting timing
                benchmarkManager.StartInpainting();

                float startTime = Time.realtimeSinceStartup;
                yield return inpaintingModel.RunModel(inputTexture, segmentationModel as YOLOSegmentationRunner, depthModel.ObjectDepthBuffer);
                float endTime = Time.realtimeSinceStartup;
                float deltaTime = endTime - startTime;

                // End inpainting timing
                benchmarkManager.EndInpainting();

                inpaintingTimeText.text = "Inpainting Time: " + deltaTime.ToString("F4") + "s\nFPS: " + (1.0f / deltaTime).ToString("F2");
            }
            else
            {
                yield return null;
            }
        }
    }    private IEnumerator RunPostProcessing()
    {
        while (true)
        {
            PostProcessing();
            yield return null;
        }
    }

#endregion

#region Sequential Execution
    float inferenceTime;    private IEnumerator RunSequentialPipeline()
    {
        while (true)
        {
            // Start iteration timing for benchmark
            benchmarkManager.StartIteration();
            
            RenderTexture.active = null;  // Reset active render texture
            inferenceTime = Time.realtimeSinceStartup;

            // Segmentation
            benchmarkManager.StartSegmentation();
            yield return StartCoroutine(segmentationModel.RunModel(inputTexture));
            benchmarkManager.EndSegmentation();
            getTracks();

            // Depth Estimation
            if (depthModel.IsEnabled)
            {
                benchmarkManager.StartDepthEstimation();
                yield return StartCoroutine(depthModel.RunModel(inputTexture));
                depthModel.CalculateObjectDepths(segmentationModel.OutputBuffer, segmentationModel.NumObjDetected);
                benchmarkManager.EndDepthEstimation();
            }

            // Inpainting
            if (inpaintingModel.IsEnabled)
            {
                benchmarkManager.StartInpainting();
                yield return StartCoroutine(inpaintingModel.RunModel(inputTexture, segmentationModel as YOLOSegmentationRunner, depthModel.ObjectDepthBuffer));
                benchmarkManager.EndInpainting();
            }            // Post Processing
            PostProcessing();
            
            segmentationModel.DisposeOutput();
            var speed = Time.realtimeSinceStartup - inferenceTime;
            segmentationTimeText.text = "Inference Speed: " + speed.ToString("F4") + "s\n" + "FPS: " + (1.0f / speed).ToString("F2");
            
            // End iteration timing for benchmark
            benchmarkManager.EndIteration();
        }
    }
#endregion


    /// <summary>
    /// Execute Post Processing effects
    /// </summary>
    private void PostProcessing()
    {
        // Start post-processing timing
        benchmarkManager.StartPostProcessing();

        float startTime = Time.realtimeSinceStartup;
        RenderTexture.active = null;
        RenderTexture.active = postProcessingOverlay;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = imagePostProcessingOverlay;
        GL.Clear(true, true, Color.clear);

        foreach (var processor in gpuPostProcessors)
        {   
            if (processor is ImagePostProcessor) {
                var imageProcessor = processor as ImagePostProcessor;
                imageProcessor.ExecuteShader(inputTexture);
            }
            else {
                processor.ExecuteShader();
            }
        }


        foreach (var processor in cpuPostProcessors)
        {
            processor.Execute();
        }


        OutputImage.texture = inputTexture;
        PostProcessingOutputImage.texture = postProcessingOverlay;
        ImagePostprocessingOutputImage.texture = imagePostProcessingOverlay;

        float endTime = Time.realtimeSinceStartup;
        float deltaTime = endTime - startTime;
        postProcessingTimeText.text = "Post Processing Time: " + deltaTime.ToString("F4") + "s\nFPS: " + (1.0f / deltaTime).ToString("F2");

        // End post-processing timing
        benchmarkManager.EndPostProcessing();
    }

}
