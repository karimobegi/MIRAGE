using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;


public enum YoloOutputFormat
{
    Auto = 0,
    DenseAnchors = 1,
    EndToEnd = 2
}

/// <summary>
/// YOLO Segmentation: Model downloaded from https://docs.ultralytics.com/tasks/segment/#models
/// Supports both Ultralytics YOLO27 and YOLO26 (as well as YOLO11/YOLOv8) ONNX segmentation exports:
/// 
/// 1. End-to-End / NMS-free exports (default `end2end=True` in YOLO26/YOLO27, and native query-based
///    Transformer heads in YOLO27m-seg / YOLO27l-seg):
///    - Input shape: (1, 3, H, W) BCHW (e.g., 640x640 or 800x800)
///    - Output 0: (1, MaxDet, 6 + NumProtos), e.g. (1, 300, 38) -> [x1, y1, x2, y2, score, class_id, 32 mask coefs]
///    - Output 1: (1, NumProtos, H/4, W/4), e.g. (1, 32, 160, 160) or (1, 32, 200, 200)
///
/// 2. Dense one-to-many anchor exports (`end2end=False`, e.g., YOLO26s-seg or YOLO27n/s-seg dense):
///    - Output 0: (1, 4 + YOLOClasses + NumProtos, NumAnchors), e.g. (1, 116, 8400) -> [cx, cy, w, h, classes..., coefs...]
///    - Output 1: (1, NumProtos, H/4, W/4)
///
/// Author: J-Britten (updated for YOLO27 / NMS-free End-to-End ONNX compatibility)
/// </summary>
public class YOLOSegmentationRunner : SegmentationRunner
{
#region Variables
    
    /// <summary>
    /// By which factor YOLO downscales the mask output when static prototype dimensions are unavailable. Default: 4
    /// </summary>
    [HideInInspector]
    public int MaskScalingFactor = 4;

    [Tooltip("Auto-detects Dense (end2end=False, e.g. 1x116x8400) vs End-to-End NMS-free (end2end=True / YOLO27m/l-seg, e.g. 1x300x38) ONNX outputs.")]
    public YoloOutputFormat OutputFormat = YoloOutputFormat.Auto;

    [Min(1)]
    [Tooltip("Number of prototype mask channels in output1 (32 for standard Ultralytics YOLO26/YOLO27 segmentation models).")]
    public int MaskPrototypeCount = 32;

    [HideInInspector]
    public YoloOutputFormat ActiveOutputFormat { get; private set; } = YoloOutputFormat.DenseAnchors;
    
    [Tooltip("Enabled if the Output shape should be clipped to match the original Image input ratio. " +
             "If disabled, the output will be a square image with padding. The original aspect ratio will be preserved.")]
    public bool ClipPadding = true;

    public int YOLOClasses = 80; //replace this with count from text asset later labels
    
    /// <summary>
    /// Intersection Over Union (IoU) threshold for Non-Maximum Suppression (NMS). 
    /// Lower values result in fewer detections by eliminating overlapping boxes, useful for reducing duplicates.
    /// </summary>
    public float iouThreshold = 0.7f;
    
    /// <summary>
    /// Sets the minimum confidence threshold for detections. 
    /// Objects detected with confidence below this threshold will be disregarded. 
    /// Adjusting this value can help reduce false positives.
    /// </summary>
    public float scoreThreshold = 0.25f;

    /// <summary>
    /// Threshold for the mask values. Default: 0.25
    /// We use this to remove noise from the mask
    /// </summary>
    public float maskThreshold = 0.25f;
    public Texture2D InputTexture;
    public RawImage InputImage; //Image to display the input texture

    //Input Tensor
    private Tensor<float> inputTensor;

    //Output Tensors
    private Tensor<int> labelIDTensor; // N, 1 (label IDs)
    private Tensor<float> scoresTensor; // N, 1 (scores)
    private Tensor<float> maskTensor; // N, 160, 160, 1 (masks)
    private Tensor<float> bboxTensor; // N, 4 (coords are centerX, centerY, width, height)

    public Tensor<float> BBoxTensor {
        get {
            return bboxTensor;
        }
    }

    public override int NumObjDetected => numDetections;
    //Output Data (CPU)
    public float[] BBoxes {
        get {
            return bboxData;
        }
    }
    public int[] LabelIDs {
        get {
            return labelIDData;
        }
    }

    public float[] Scores {
        get {
            return scoresData;
        }
    }

    private int[] labelIDData;
    private float[] bboxData;
    private float[] scoresData;

    

    //Pre Processing Variables
    private TextureTransform inputTextureTransform;

    /// <summary>
    /// This shader takes the masks tensor in form of a buffer as input
    /// It takes the NxWxHx1 and turns it into a 1xWxH where each pixel has the ID of the mask that most likely belongs to
    /// </summary>
    private ComputeShader instanceSegmentationShader;
    private int instanceSegmentationKernel;

    /// <summary>
    /// Number of detected objects each iteration, is used for internal calculations
    /// </summary>
    private int numDetections = 0;


    /// <summary>
    /// Shader that is used to visualize everything YOLO detected
    /// </summary>
    private ComputeShader visualizationShader;
    private RenderTexture visualizationTexture;
    private int visualizationKernel;

    /// <summary>
    /// Variables used to work around a bug when copying data from the GPU to CPU memory, see <see cref="YOLOSegmentationRunner.TransferDataToCPU"/>
    /// </summary>
    private ComputeShader toCPUShader;
    private int extractLabelIDsKernel;
    private ComputeBuffer labelIDsOutputBuffer, bboxOutputBuffer, scoresOutputBuffer;
    private AsyncGPUReadbackRequest labelsReadback, boxesReadback, scoresReadback;
    private bool metadataPending;
    private int clearMaskKernel;

    /// <summary>
    /// When false (e.g., GPU-only pipeline effects with no active CPU post-processors),
    /// compact GPU buffers (ClassIdsBuffer, BBoxBuffer, ScoresBuffer) are still populated on the GPU
    /// without issuing GPU->CPU AsyncGPUReadback stalls.
    /// </summary>
    [NonSerialized]
    public bool RequireCpuMetadataReadback = true;

    public ComputeBuffer ClassIdsBuffer { get => labelIDsOutputBuffer; }

    public ComputeBuffer BBoxBuffer { get => bboxOutputBuffer; }

    public ComputeBuffer ScoresBuffer { get => scoresOutputBuffer; }

#endregion


#region Model Execution

    public override IEnumerator RunModel(params Texture[] inputs) {
        if (InputImage != null) InputImage.texture = inputs[0];
        TextureConverter.ToTensor(inputs[0], inputTensor, inputTextureTransform);
        schedule = worker.ScheduleIterable(inputTensor);

        yield return RunInference();
    }

    protected override bool RequestsDone() {
        return !metadataPending || (labelsReadback.done && boxesReadback.done && scoresReadback.done);
    }

    protected override void PeekOutput() {

        labelIDTensor = worker.PeekOutput("output_0") as Tensor<int>; // N, 1 (label IDs)
        scoresTensor = worker.PeekOutput("output_1") as Tensor<float>; // N, 1 (scores)
        bboxTensor = worker.PeekOutput("output_2") as Tensor<float>; // N, 4 (coords are centerX, centerY, width, height)
        maskTensor = worker.PeekOutput("output_3") as Tensor<float>;  // N , 160, 160 ,1      (160,160) by default, adjusted to output width and height for this

        numDetections = Math.Min(labelIDTensor.shape[0], MaxObjects);
        CreateInstanceSegmentationMask();
        TransferDataToCPU();
    }

    protected override void ReadOutput()
    {
        Array.Clear(labelIDData, 0, labelIDData.Length);
        Array.Clear(bboxData, 0, bboxData.Length);
        Array.Clear(scoresData, 0, scoresData.Length);
        if (!metadataPending) return;
        metadataPending = false;
        if (labelsReadback.hasError || boxesReadback.hasError || scoresReadback.hasError)
        {
            numDetections = 0;
            ClearInstanceMask();
            Debug.LogError("YOLO metadata GPU readback failed; this frame has no detections.", this);
            return;
        }
        labelsReadback.GetData<int>().CopyTo(labelIDData);
        boxesReadback.GetData<float>().CopyTo(bboxData);
        scoresReadback.GetData<float>().CopyTo(scoresData);
    }
#endregion

#region Model Preparation
    protected override void PrepareModel()
    {
        CalculateScaleAndPadding();
        int[] padding = new int[] { 0, pad_w, 0, pad_h };

        inputTextureTransform = new TextureTransform()
            .SetDimensions(scaledWidth, scaledHeight, 3)
            .SetChannelSwizzle(ChannelSwizzle.RGBA)
            .SetTensorLayout(TensorLayout.NCHW);
        inputTensor = new Tensor<float>(new TensorShape(1, 3, scaledHeight, scaledWidth));

        var model = ModelLoader.Load(ModelAsset);
        string assetName = ModelAsset != null ? ModelAsset.name : null;

        // Probe symbolic output metadata once so Auto mode can distinguish Dense (1, 116, 8400)
        // from End-to-End / YOLO27 NMS-free (1, 300, 38) and read prototype mask dimensions (e.g. 160x160 or 200x200).
        var probeGraph = new FunctionalGraph();
        var probeInput = probeGraph.AddInput(
            DataType.Float,
            scaledHeight > 0 && scaledWidth > 0
                ? new DynamicTensorShape(1, 3, scaledHeight, scaledWidth)
                : new DynamicTensorShape(1, 3, -1, -1));
        probeInput = Functional.Pad(probeInput, padding, paddingValue);
        var probeOutputs = Functional.Forward(model, probeInput);
        string boxesDesc = probeOutputs != null && probeOutputs.Length > 0 && probeOutputs[0] != null ? probeOutputs[0].ToString() : null;
        string protosDesc = probeOutputs != null && probeOutputs.Length > 1 && probeOutputs[1] != null ? probeOutputs[1].ToString() : null;

        ResolveOutputLayout(
            boxesDesc,
            protosDesc,
            assetName,
            YOLOClasses,
            MaskPrototypeCount,
            OutputFormat,
            out YoloOutputFormat resolvedFormat,
            out bool transposeEndToEnd,
            out int protoCount,
            out int protoHeight,
            out int protoWidth);

        try
        {
            runtimeModel = CompileSegmentationGraph(model, padding, resolvedFormat, transposeEndToEnd, protoCount, protoHeight, protoWidth);
            ActiveOutputFormat = resolvedFormat;
        }
        catch (Exception primaryError) when (OutputFormat == YoloOutputFormat.Auto)
        {
            var fallbackFormat = resolvedFormat == YoloOutputFormat.EndToEnd
                ? YoloOutputFormat.DenseAnchors
                : YoloOutputFormat.EndToEnd;
            Debug.LogWarning($"YOLO: Auto format '{resolvedFormat}' failed graph compilation ({primaryError.Message}); retrying '{fallbackFormat}'.", this);
            runtimeModel = CompileSegmentationGraph(model, padding, fallbackFormat, false, protoCount, protoHeight, protoWidth);
            ActiveOutputFormat = fallbackFormat;
        }

        PreparePostProcessing();
    }

    private Model CompileSegmentationGraph(
        Model model,
        int[] padding,
        YoloOutputFormat format,
        bool transposeEndToEnd,
        int protoCount,
        int protoHeight,
        int protoWidth)
    {
        int scaleFactor = Mathf.Max(1, MaskScalingFactor);
        int internalMaskWidth = protoWidth > 0 ? protoWidth : Mathf.Max(1, InputWidth / scaleFactor);
        int internalMaskHeight = protoHeight > 0 ? protoHeight : Mathf.Max(1, InputHeight / scaleFactor);
        int pad_h_scaled = InputHeight > 0 ? Mathf.RoundToInt((float)pad_h * internalMaskHeight / InputHeight) : 0;
        int pad_w_scaled = InputWidth > 0 ? Mathf.RoundToInt((float)pad_w * internalMaskWidth / InputWidth) : 0;

        var graph = new FunctionalGraph();
        var input = graph.AddInput(
            DataType.Float,
            scaledHeight > 0 && scaledWidth > 0
                ? new DynamicTensorShape(1, 3, scaledHeight, scaledWidth)
                : new DynamicTensorShape(1, 3, -1, -1));

        input = Functional.Pad(input, padding, paddingValue);
        var outputs = Functional.Forward(model, input);
        var boxes_scores = outputs[0];
        var maskPrototypes = outputs[1];

        FunctionalTensor coords;
        FunctionalTensor labelIDs;
        FunctionalTensor usedScores;
        FunctionalTensor mask_coefs;

        if (format == YoloOutputFormat.EndToEnd)
        {
            // End-to-End / NMS-free layout (YOLO27m-seg, YOLO27l-seg, or YOLO26/YOLO27 exported with end2end=True):
            // outputs[0] is (1, MaxDet, 6 + protoCount), where columns are [x1, y1, x2, y2, score, class_id, mask_coefs...].
            var cornersToCenter = Functional.Constant(new TensorShape(4, 4), new float[]
            {
                0.5f,  0f,   -1f,  0f,
                0f,    0.5f,  0f, -1f,
                0.5f,  0f,    1f,  0f,
                0f,    0.5f,  0f,  1f
            });

            var detections = boxes_scores[0, .., ..];
            if (transposeEndToEnd)
                detections = Functional.Transpose(detections, 0, 1);

            var boxCorners = detections[.., 0..4]; // (Q, 4) in [x1, y1, x2, y2]
            var boxCoords = Functional.MatMul(boxCorners, cornersToCenter); // (Q, 4) in [cx, cy, w, h]
            var scores = Functional.Reshape(detections[.., 4..5], new int[] { -1 }); // (Q)
            var classIDs = Functional.Int(Functional.Reshape(detections[.., 5..6], new int[] { -1 }) + Functional.Constant(0.5f)); // (Q)

            // One-to-one / embedded-NMS heads have already suppressed duplicate detections; iouThreshold = 1.0f
            // preserves overlapping distinct instances while filtering out zero-padded / sub-threshold rows into dynamic N indices.
            float effectiveScoreThreshold = Mathf.Max(scoreThreshold, 1e-5f);
            var indices = Functional.NMS(boxCorners, scores, 1.0f, effectiveScoreThreshold);
            var indicesUnsqueezed = Functional.Unsqueeze(indices, -1);

            coords = Functional.Gather(boxCoords, 0, Functional.BroadcastTo(indicesUnsqueezed, new int[] { 4 }));
            labelIDs = Functional.Gather(classIDs, 0, indices);
            usedScores = Functional.Gather(scores, 0, indices);

            int coefEnd = 6 + protoCount;
            var rawCoefs = detections[.., 6..coefEnd];
            mask_coefs = Functional.Gather(rawCoefs, 0, Functional.BroadcastTo(indicesUnsqueezed, new int[] { protoCount }));
        }
        else
        {
            // Dense anchor layout (end2end=False, e.g. (1, 116, 8400)): [cx, cy, w, h, classes..., mask_coefs...]
            var centerToCorners = Functional.Constant(new TensorShape(4, 4), new float[]
            {
                 1f,    0f,    1f,   0f,
                 0f,    1f,    0f,   1f,
                -0.5f,  0f,    0.5f, 0f,
                 0f,   -0.5f,  0f,   0.5f
            });

            int classUpper = 4 + Mathf.Max(1, YOLOClasses);
            var boxCoords = Functional.Transpose(boxes_scores[0, 0..4, ..], 0, 1);
            var allScores = boxes_scores[0, 4..classUpper, ..];
            var scores = Functional.ReduceMax(allScores, 0);
            var classIDs = Functional.ArgMax(allScores, 0);
            var boxCorners = Functional.MatMul(boxCoords, centerToCorners);

            var indices = Functional.NMS(boxCorners, scores, iouThreshold, scoreThreshold);
            var indicesUnsqueezed = Functional.Unsqueeze(indices, -1);

            coords = Functional.Gather(boxCoords, 0, Functional.BroadcastTo(indicesUnsqueezed, new int[] { 4 }));
            labelIDs = Functional.Gather(classIDs, 0, indices);
            usedScores = Functional.Gather(scores, 0, indices);

            var rawCoefs = Functional.Transpose(boxes_scores[0, classUpper.., ..], 0, 1);
            mask_coefs = Functional.Gather(rawCoefs, 0, Functional.BroadcastTo(indicesUnsqueezed, new int[] { protoCount }));
        }

        // Combine prototype masks via a single 2D GEMM ((N+1, P) x (P, H*W)) instead of a 4D broadcast
        // Mul + ReduceSum over (N, P, H, W), cutting intermediate VRAM traffic by ~32x.
        // Prepending one zero row guarantees M >= 1 even on N == 0 frames before MatMul and Interpolate.
        var dummyCoef = Functional.Constant(new TensorShape(1, protoCount), new float[protoCount]);
        var paddedCoefs = Functional.Concat(new FunctionalTensor[] { dummyCoef, mask_coefs }, 0);
        var flatPrototypes = Functional.Reshape(maskPrototypes, new int[] { protoCount, -1 });
        var upscalingTensor = Functional.MatMul(paddedCoefs, flatPrototypes);
        upscalingTensor = Functional.Reshape(upscalingTensor, new int[] { -1, 1, internalMaskHeight, internalMaskWidth });

        if (ClipPadding)
        {
            int clippedH = Mathf.Max(1, internalMaskHeight - pad_h_scaled);
            int clippedW = Mathf.Max(1, internalMaskWidth - pad_w_scaled);
            upscalingTensor = upscalingTensor[.., .., 0..clippedH, 0..clippedW];
        }

        upscalingTensor = Functional.Interpolate(upscalingTensor, new int[] { OutputHeight, OutputWidth }, mode: "linear");
        var masks = upscalingTensor[1..];
        masks = Functional.Sigmoid(masks);
        masks = Functional.Where(
            Functional.Greater(masks, Functional.Constant(maskThreshold)),
            masks,
            Functional.Constant(0.0f));

        return graph.Compile(new FunctionalTensor[] { labelIDs, usedScores, coords, masks });
    }

    public static void ResolveOutputLayout(
        string boxesDescriptor,
        string prototypesDescriptor,
        string modelAssetName,
        int configuredClasses,
        int configuredPrototypes,
        YoloOutputFormat requestedFormat,
        out YoloOutputFormat resolvedFormat,
        out bool transposeEndToEnd,
        out int protoCount,
        out int protoHeight,
        out int protoWidth)
    {
        protoCount = Mathf.Max(1, configuredPrototypes);
        protoHeight = 0;
        protoWidth = 0;
        transposeEndToEnd = false;

        int[] protoDims = ParseShapeDimensions(prototypesDescriptor);
        if (protoDims.Length >= 4)
        {
            if (protoDims[1] > 0) protoCount = protoDims[1];
            if (protoDims[2] > 0 && protoDims[3] > 0)
            {
                protoHeight = protoDims[2];
                protoWidth = protoDims[3];
            }
        }

        int[] boxDims = ParseShapeDimensions(boxesDescriptor);
        int expectedEndToEndCols = 6 + protoCount;
        int expectedDenseChannels = 4 + Mathf.Max(1, configuredClasses) + protoCount;

        if (requestedFormat == YoloOutputFormat.EndToEnd)
        {
            resolvedFormat = YoloOutputFormat.EndToEnd;
            if (boxDims.Length >= 3)
            {
                transposeEndToEnd = boxDims[1] == expectedEndToEndCols && boxDims[2] != expectedEndToEndCols;
                if (!transposeEndToEnd && protoDims.Length < 4 && boxDims[2] > 6 && boxDims[2] != expectedDenseChannels)
                    protoCount = boxDims[2] - 6;
            }
            return;
        }

        if (requestedFormat == YoloOutputFormat.DenseAnchors)
        {
            resolvedFormat = YoloOutputFormat.DenseAnchors;
            return;
        }

        if (boxDims.Length >= 3)
        {
            int d1 = boxDims[1];
            int d2 = boxDims[2];
            if (d2 == expectedEndToEndCols && d1 != expectedDenseChannels)
            {
                resolvedFormat = YoloOutputFormat.EndToEnd;
                return;
            }
            if (d1 == expectedDenseChannels)
            {
                resolvedFormat = YoloOutputFormat.DenseAnchors;
                return;
            }
            if (d1 == expectedEndToEndCols && d2 != expectedEndToEndCols)
            {
                resolvedFormat = YoloOutputFormat.EndToEnd;
                transposeEndToEnd = true;
                return;
            }
        }

        if (!string.IsNullOrEmpty(modelAssetName))
        {
            string lower = modelAssetName.ToLowerInvariant();
            if (lower.Contains("e2e") || lower.Contains("end2end") || lower.Contains("nmsfree") ||
                lower.Contains("yolo27m") || lower.Contains("yolo27l"))
            {
                resolvedFormat = YoloOutputFormat.EndToEnd;
                return;
            }
        }

        resolvedFormat = YoloOutputFormat.DenseAnchors;
    }

    private static int[] ParseShapeDimensions(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor)) return Array.Empty<int>();
        int close = descriptor.LastIndexOf(')');
        if (close <= 0) return Array.Empty<int>();
        int open = descriptor.LastIndexOf('(', close);
        if (open < 0 || close <= open + 1) return Array.Empty<int>();

        string[] parts = descriptor.Substring(open + 1, close - open - 1).Split(',');
        if (parts.Length < 2) return Array.Empty<int>();
        var dims = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            dims[i] = int.TryParse(token, out int value) ? value : -1;
        }
        return dims;
    }

#endregion

#region Post Processing
    private void PreparePostProcessing()
    {   
        PrepareToCPUShader();
        OutputBuffer = new ComputeBuffer(OutputWidth * OutputHeight, sizeof(int) * 2);
        labelIDsOutputBuffer = new ComputeBuffer(MaxObjects, sizeof(int));
        bboxOutputBuffer = new ComputeBuffer(MaxObjects * 4, sizeof(float));
        scoresOutputBuffer = new ComputeBuffer(MaxObjects, sizeof(float));
        bboxData = new float[MaxObjects * 4];
        labelIDData = new int[MaxObjects];
        scoresData = new float[MaxObjects];

        instanceSegmentationShader = Instantiate(Resources.Load<ComputeShader>("Models/YOLO/InstanceSegmentationShader"));
        instanceSegmentationKernel = instanceSegmentationShader.FindKernel("InstanceSegmentation");
        clearMaskKernel = instanceSegmentationShader.FindKernel("ClearInstanceMask");
        instanceSegmentationShader.SetInt("MaskWidth", OutputWidth);
        instanceSegmentationShader.SetInt("MaskHeight", OutputHeight);

        var boxScale = new Vector4((float)OutputWidth / (ClipPadding ? scaledWidth : InputWidth),
            (float)OutputHeight / (ClipPadding ? scaledHeight : InputHeight), 0, 0);
        instanceSegmentationShader.SetVector("Scale", boxScale);
        toCPUShader.SetVector("Scale", boxScale);

        if (PublishOutputToUI && OutputImage != null)
        {
            EnsureVisualizationResources();
        }
    }

    private void EnsureVisualizationResources()
    {
        if (visualizationShader == null)
        {
            visualizationShader = Instantiate(Resources.Load<ComputeShader>("Models/YOLO/InstanceSegmentationVisualizerShader"));
            visualizationKernel = visualizationShader.FindKernel("VisualizeSegmentation");
            visualizationShader.SetInt("TextureWidth", OutputWidth);
            visualizationShader.SetInt("TextureHeight", OutputHeight);
            visualizationShader.SetBool("ShowObjects", false);
        }
        if (visualizationTexture == null)
        {
            visualizationTexture = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32);
            visualizationTexture.enableRandomWrite = true;
            visualizationTexture.Create();
        }
    }

    private void CreateInstanceSegmentationMask()
    {
        if (numDetections == 0)
        {
            ClearInstanceMask();
            return;
        }
        
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "InputMasks", ComputeTensorData.Pin(maskTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "BoundingBoxes", ComputeTensorData.Pin(bboxTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "LabelIDs", ComputeTensorData.Pin(labelIDTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "Scores", ComputeTensorData.Pin(scoresTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "OutputBuffer", OutputBuffer);
        instanceSegmentationShader.SetInt("NumMasks", numDetections);
        
        instanceSegmentationShader.Dispatch(instanceSegmentationKernel, 
            Mathf.CeilToInt(OutputWidth / 8f), 
            Mathf.CeilToInt(OutputHeight / 8f), 
            1);

        if (PublishOutputToUI && OutputImage != null)
        {
            EnsureVisualizationResources();
            visualizationShader.SetBuffer(visualizationKernel, "InstanceBuffer", OutputBuffer);
            visualizationShader.SetTexture(visualizationKernel, "OutputTexture", visualizationTexture);
            visualizationShader.Dispatch(visualizationKernel, 
                Mathf.CeilToInt(OutputWidth / 8f), 
                Mathf.CeilToInt(OutputHeight / 8f), 
                1);
            OutputImage.texture = visualizationTexture;
        }
    }

    private void ClearInstanceMask()
    {
        instanceSegmentationShader.SetBuffer(clearMaskKernel, "OutputBuffer", OutputBuffer);
        instanceSegmentationShader.Dispatch(clearMaskKernel, Mathf.CeilToInt(OutputWidth / 8f), Mathf.CeilToInt(OutputHeight / 8f), 1);
        if (PublishOutputToUI && OutputImage != null)
        {
            EnsureVisualizationResources();
            var previous = RenderTexture.active;
            RenderTexture.active = visualizationTexture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
            OutputImage.texture = visualizationTexture;
        }
    }

    /// <summary>
    /// Sentis struggles transferring data from YOLO to the CPU, presumably because even though
    /// the shapes are Nx1 and Nx4 for the LabelIDs and bounding boxes, the actual sizes are Nx1x160x160xM for an unknown reason
    /// It appears to be a bug though.
    /// 
    /// 
    /// This method executes a compute shader which only copies the first "numObjects" (*4) values to new ComputeBuffers which 
    /// then are copied to the CPU to work around this issue
    /// 
    /// </summary>
    private void TransferDataToCPU()
    {
        if (numDetections <= 0)
        {
            metadataPending = false;
            return;
        }

        // Always populate compact GPU buffers (ClassIdsBuffer is read directly on GPU by InpaintingMaskBuilder).
        toCPUShader.SetBuffer(extractLabelIDsKernel, "LabelIDsInput", ComputeTensorData.Pin(labelIDTensor).buffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "BBoxInput", ComputeTensorData.Pin(bboxTensor).buffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "ScoresInput", ComputeTensorData.Pin(scoresTensor).buffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "LabelIDsOutput", labelIDsOutputBuffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "BBoxOutput", bboxOutputBuffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "ScoresOutput", scoresOutputBuffer);
        toCPUShader.SetInt("NumDetections", numDetections);
        toCPUShader.Dispatch(extractLabelIDsKernel, numDetections, 1, 1);

        if (!RequireCpuMetadataReadback)
        {
            metadataPending = false;
            return;
        }

        metadataPending = true;
        labelsReadback = AsyncGPUReadback.Request(labelIDsOutputBuffer);
        boxesReadback = AsyncGPUReadback.Request(bboxOutputBuffer);
        scoresReadback = AsyncGPUReadback.Request(scoresOutputBuffer);
    }

    private void PrepareToCPUShader()
    {
        toCPUShader = Instantiate(Resources.Load<ComputeShader>("Models/YOLO/ToCPU"));
        extractLabelIDsKernel = toCPUShader.FindKernel("ToCPU");
    }

#endregion
    void OnDestroy()
    {
        // Complete only our outstanding compact requests before releasing their buffers.
        if (metadataPending)
        {
            labelsReadback.WaitForCompletion();
            boxesReadback.WaitForCompletion();
            scoresReadback.WaitForCompletion();
        }
        if (worker != null) worker.Dispose();
        if (inputTensor != null) inputTensor.Dispose();
        if (OutputBuffer != null)
        {
            OutputBuffer.Dispose();
        }
        if (visualizationTexture != null)
        {
            visualizationTexture.Release();
            Destroy(visualizationTexture);
        }
        if (labelIDsOutputBuffer != null)
        {
            labelIDsOutputBuffer.Dispose();
        }
        if (scoresOutputBuffer != null)
        {
            scoresOutputBuffer.Dispose();
        }
        if (bboxOutputBuffer != null) bboxOutputBuffer.Dispose();
        if (instanceSegmentationShader != null) Destroy(instanceSegmentationShader);
        if (visualizationShader != null) Destroy(visualizationShader);
        if (toCPUShader != null) Destroy(toCPUShader);
        //if (bboxData.IsCreated) bboxData.Dispose();
        //if (labelIDData.IsCreated) labelIDData.Dispose();
        //if (scoresData.IsCreated) scoresData.Dispose();
    }

    public override void DisposeOutput()
    {
        // PeekOutput tensors belong to Worker and remain valid until its next run.
       // if (bboxData.IsCreated) bboxData.Dispose();
       // if (labelIDData.IsCreated) labelIDData.Dispose();
       // if (scoresData.IsCreated) scoresData.Dispose();

         
 //       if (labelIDsOutputBuffer != null) labelIDsOutputBuffer.Dispose();
 //       if(bboxOutputBuffer != null)bboxOutputBuffer.Dispose();
 //       if (scoresOutputBuffer != null) scoresOutputBuffer.Dispose();

    }

    private void OnValidate()
    {
        // Calculate aspect ratios
        float inputAspect = (float)InputWidth / InputHeight;
        float outputAspect = (float)OutputWidth / OutputHeight;

        // If aspect ratios don't match (with small float comparison tolerance)
        ClipPadding = !Mathf.Approximately(inputAspect, outputAspect);
    
        float imageAspect = (float) ImageWidth / ImageHeight;
        bool aspectMatch = Mathf.Approximately(imageAspect, outputAspect);
        if(ClipPadding && !aspectMatch) {
            Debug.LogWarning("YOLO: Output Aspect Ratio does not match the Image Aspect Ratio but will be clipped. This will result in a distorted output image" );
        } else if(aspectMatch) {
            Debug.LogWarning("YOLO: Output Aspect Ratio matches the Image Aspect Ratio! Image will be clipped correctly" );
        }
    }

}
