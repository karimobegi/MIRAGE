using System;
using System.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// DA3-Small relative depth. The documented ONNX wrapper expects ImageNet-normalized RGB.
/// Dense depth stays on the GPU; only one float per object is read asynchronously.
/// </summary>
public class DepthAnythingRunner : DepthEstimationRunner
{
    // Retained so existing scene camera settings survive serialization.
    public float SensorWidthPX = 1280f;
    public float FocalLengthMM = 3.67f;
    public float SensorWidthMM = 5.7f;

    [Tooltip("DA3-Small is relative. Select Metres only for a metric model or independently validated calibration; this setting alone does not recover metric scale.")]
    public DepthValueUnits OutputUnits = DepthValueUnits.Relative;
    [Tooltip("Explicit conversion applied to object depths: value * scale + offset. Leave 1/0 for relative DA3 output.")]
    public float DepthScale = 1f;
    public float DepthOffset = 0f;
    [Min(0.0001f)]
    [Tooltip("Reject non-positive, non-finite or larger raw model depths before reduction.")]
    public float MaximumValidDepth = 1000000f;
    [Tooltip("Required by the documented DA3 ONNX export. Disable only if normalization is already embedded in the model.")]
    public bool NormalizeInput = true;

    public RawImage InputImage;
    public override bool UsesMetricDepth => OutputUnits == DepthValueUnits.Metres;
    public override ComputeBuffer ObjectDepthBuffer => objectDepthBuffer;
    public override float[] DepthData => objectDepths;

    private ComputeBuffer objectDepthBuffer;
    private ComputeBuffer partialDepthBuffer;
    private float[] objectDepths;
    private float[] invalidDepths;
    private TextureTransform toTensor;
    private Tensor<float> inputTensor;
    // Borrowed from worker.PeekOutput; the worker owns and disposes these tensors.
    private Tensor<float> outputTensor;
    private Tensor<float> textureTensor;
    private ComputeShader objectDepthCompute;
    private int partialKernel;
    private int finalizeKernel;
    private int groupsX;
    private int groupsY;
    private int objectCapacity;
    private bool depthReadbackPending;
    private bool hasDepthReadback;
    private AsyncGPUReadbackRequest depthReadback;
    private int outputGeneration;

    protected override void PrepareModel()
    {
        objectCapacity = Mathf.Max(1, MaxObjects);
        toTensor = new TextureTransform().SetDimensions(InputWidth, InputHeight, 3).SetTensorLayout(TensorLayout.NCHW);
        inputTensor = new Tensor<float>(new TensorShape(1, 3, InputHeight, InputWidth));
        pad_w = Mathf.CeilToInt(InputWidth / 14f) * 14 - InputWidth;
        pad_h = Mathf.CeilToInt(InputHeight / 14f) * 14 - InputHeight;
        var model = ModelLoader.Load(ModelAsset);
        var graph = new FunctionalGraph();
        var input = graph.AddInput(DataType.Float, new DynamicTensorShape(1, 3, InputHeight, InputWidth));
        if (NormalizeInput)
        {
            // DA3 input_processor.py: ImageNet RGB mean/std, not included in the ONNX wrapper.
            var mean = Functional.Constant(new TensorShape(1, 3, 1, 1), new[] { 0.485f, 0.456f, 0.406f });
            var std = Functional.Constant(new TensorShape(1, 3, 1, 1), new[] { 0.229f, 0.224f, 0.225f });
            input = (input - mean) / std;
        }
        input = Functional.Pad(input, new[] { 0, pad_w, 0, pad_h }, paddingValue);
        var output = Functional.Forward(model, input)[0];
        var unpaddedDepth = output[.., .., 0..InputHeight, 0..InputWidth];
        // Reduction indexes depth and segmentation with exactly the same pixel grid.
        var alignedDepth = Functional.Interpolate(unpaddedDepth, new[] { OutputHeight, OutputWidth }, mode: "linear");
        runtimeModel = graph.Compile(alignedDepth, alignedDepth / 4f);

        outputTexture = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            name = "MIRAGE depth visualization"
        };
        outputTexture.Create();
        EnsureObjectDepthBuffer();
        groupsX = Mathf.CeilToInt(OutputWidth / 8f);
        groupsY = Mathf.CeilToInt(OutputHeight / 8f);
        partialDepthBuffer = new ComputeBuffer(checked(groupsX * groupsY * objectCapacity), sizeof(float) * 4);
        objectDepthCompute = Instantiate(Resources.Load<ComputeShader>("Models/Depth/ObjectDepthCalculator"));
        partialKernel = objectDepthCompute.FindKernel("ObjectDepthPartial");
        finalizeKernel = objectDepthCompute.FindKernel("ObjectDepthFinalize");
    }

    public override IEnumerator RunModel(params Texture[] inputs)
    {
        if (!IsPrepared || inputs == null || inputs.Length == 0 || inputs[0] == null)
            yield break;
        if (InputImage != null)
            InputImage.texture = inputs[0];
        DisposeOutput();
        TextureConverter.ToTensor(inputs[0], inputTensor, toTensor);
        schedule = worker.ScheduleIterable(inputTensor);
        yield return RunInference();
    }

    protected override void PeekOutput()
    {
        outputTensor = worker.PeekOutput(0) as Tensor<float>;
        textureTensor = worker.PeekOutput(1) as Tensor<float>;
    }

    // Subsequent consumers are GPU commands on the same queue. No dense readback.
    protected override bool RequestsDone() => true;
    protected override void ReadOutput()
    {
        if (textureTensor != null && OutputImage != null)
        {
            TextureConverter.RenderToTexture(textureTensor, outputTexture);
            UpdateOutputImage();
        }
    }

    public override void CalculateObjectDepths(ComputeBuffer segmentationBuffer, int numObjects)
    {
        StartCoroutine(CalculateObjectDepthsAsync(segmentationBuffer, numObjects));
    }

    public override IEnumerator CalculateObjectDepthsAsync(ComputeBuffer segmentationBuffer, int numObjects)
    {
        while (depthReadbackPending)
            yield return null;
        int generation = outputGeneration;
        if (!IsPrepared || numObjects <= 0 || outputTensor == null || segmentationBuffer == null
            || segmentationBuffer.count != checked(OutputWidth * OutputHeight)
            || !IsFinite(DepthScale) || DepthScale <= 0 || !IsFinite(DepthOffset)
            || !IsFinite(MaximumValidDepth) || MaximumValidDepth <= 0)
        {
            ResetOutput();
            yield break;
        }
        int count = Mathf.Clamp(numObjects, 0, objectCapacity);
        objectDepthCompute.SetInt("ImageWidth", OutputWidth);
        objectDepthCompute.SetInt("ImageHeight", OutputHeight);
        objectDepthCompute.SetInt("GroupsX", groupsX);
        objectDepthCompute.SetInt("TileCount", groupsX * groupsY);
        objectDepthCompute.SetInt("ObjectCapacity", objectCapacity);
        objectDepthCompute.SetInt("NumObjects", count);
        objectDepthCompute.SetFloat("DepthScale", DepthScale);
        objectDepthCompute.SetFloat("DepthOffset", DepthOffset);
        objectDepthCompute.SetFloat("MaximumValidDepth", MaximumValidDepth);
        objectDepthCompute.SetBuffer(partialKernel, "SegmentationBuffer", segmentationBuffer);
        objectDepthCompute.SetBuffer(partialKernel, "DepthBuffer", ComputeTensorData.Pin(outputTensor).buffer);
        objectDepthCompute.SetBuffer(partialKernel, "PartialDepths", partialDepthBuffer);
        objectDepthCompute.Dispatch(partialKernel, groupsX, groupsY, 1);
        objectDepthCompute.SetBuffer(finalizeKernel, "PartialDepths", partialDepthBuffer);
        objectDepthCompute.SetBuffer(finalizeKernel, "ObjectDepthBuffer", objectDepthBuffer);
        objectDepthCompute.Dispatch(finalizeKernel, Mathf.CeilToInt(objectCapacity / 64f), 1, 1);
        depthReadback = AsyncGPUReadback.Request(objectDepthBuffer);
        hasDepthReadback = true;
        depthReadbackPending = true;
        try
        {
            while (!depthReadback.done)
                yield return null;
            if (generation == outputGeneration)
            {
                if (depthReadback.hasError)
                {
                    ResetOutput();
                    Debug.LogWarning("Object-depth readback failed; distances are unavailable.", this);
                }
                else
                {
                    depthReadback.GetData<float>().CopyTo(objectDepths);
                }
            }
        }
        finally { depthReadbackPending = false; }
    }

    public override void ResetOutput()
    {
        outputGeneration++;
        // A disabled/unassigned depth model still supplies a valid compact binding
        // so GPU postprocessors can perform class-only selection.
        EnsureObjectDepthBuffer();
        Array.Copy(invalidDepths, objectDepths, objectCapacity);
        objectDepthBuffer.SetData(invalidDepths);
    }

    private void EnsureObjectDepthBuffer()
    {
        if (objectDepthBuffer != null)
            return;
        objectCapacity = Mathf.Max(1, MaxObjects);
        objectDepthBuffer = new ComputeBuffer(objectCapacity, sizeof(float));
        invalidDepths = new float[objectCapacity];
        for (int i = 0; i < invalidDepths.Length; i++)
            invalidDepths[i] = float.NaN;
        objectDepths = (float[])invalidDepths.Clone();
        objectDepthBuffer.SetData(invalidDepths);
    }

    public override void DisposeOutput()
    {
        // PeekOutput is worker-owned; release references without disposing it.
        outputTensor = null;
        textureTensor = null;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private void OnDestroy()
    {
        StopAllCoroutines();
        if (hasDepthReadback && !depthReadback.done)
            depthReadback.WaitForCompletion();
        DisposeOutput();
        worker?.Dispose();
        inputTensor?.Dispose();
        partialDepthBuffer?.Release();
        objectDepthBuffer?.Release();
        if (outputTexture != null)
        {
            outputTexture.Release();
            Destroy(outputTexture);
        }
        if (objectDepthCompute != null)
            Destroy(objectDepthCompute);
    }
}
