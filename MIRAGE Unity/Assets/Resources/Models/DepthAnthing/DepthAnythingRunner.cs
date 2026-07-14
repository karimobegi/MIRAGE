using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

/// <summary>
/// Depth Anything Depth Estimation
/// using Depth Anything V3 (DA3-Small): https://github.com/ByteDance-Seed/Depth-Anything-3
/// 
/// ONNX export via: https://github.com/devin-lai/Depth-Anything-3-Onnx
/// 
/// Author: J-Britten
/// </summary>
public class DepthAnythingRunner : DepthEstimationRunner
{   
#region Variables
    /// <summary>
    /// Camera parameters — unused with DA3-Small (relative depth),
    /// retained for potential future metric depth integration.
    /// </summary>
    public float SensorWidthPX = 1280f; //this should match the OutputWidth 
    public float FocalLengthMM = 3.67f;

    public float SensorWidthMM = 5.7f;

    public override ComputeBuffer ObjectDepthBuffer {get => objectDepthBuffer;}

    private ComputeBuffer objectDepthBuffer;
    public override float[] DepthData => objectDepths;

    private float[] objectDepths;
    
    public RawImage InputImage;
    private Unity.InferenceEngine.TextureTransform toTensor, toTexture;

    private Unity.InferenceEngine.Tensor<float> inputTensor;

    private Unity.InferenceEngine.Tensor<float> outputTensor, textureTensor;

    private ComputeShader objectDepthCompute;
    private int objectDepthKernel;

    private int threadGroupsX, threadGroupsY;

#endregion

#region Model Preparation
    protected override void PrepareModel()
    {
        toTensor = new TextureTransform().SetDimensions(InputWidth, InputHeight, 3);
        
        pad_w = Mathf.CeilToInt(InputWidth / 14.0f) * 14 - InputWidth;
        pad_h = Mathf.CeilToInt(InputHeight / 14.0f) * 14 - InputHeight;
        
        int[] padding = new int[] {0, pad_w, 0, pad_h};
        var model = Unity.InferenceEngine.ModelLoader.Load(ModelAsset);

        var graph = new Unity.InferenceEngine.FunctionalGraph();

        var input = graph.AddInput(model, 0);

        input = Unity.InferenceEngine.Functional.Pad(input, padding, paddingValue);

        var outputs = Unity.InferenceEngine.Functional.Forward(model, input);
        var output = outputs[0]; // (batch, 1, H_padded, W_padded)

        // Explicitly slice all 4 dims — two dots for the two leading dims (batch, channel)
        var slicedOutput = output[.., .., 0..InputHeight, 0..InputWidth];

        // DA3 outputs relative depth — normalize for display only
        var depthTexture = slicedOutput / 4f;
            
        runtimeModel = graph.Compile(slicedOutput, depthTexture);

        objectDepthBuffer = new ComputeBuffer(MaxObjects, sizeof(float));
        objectDepths = new float[MaxObjects];
        objectDepthBuffer.SetData(objectDepths);

        objectDepthCompute = Resources.Load<ComputeShader>("Models/Depth/ObjectDepthCalculator");
        objectDepthKernel = objectDepthCompute.FindKernel("ObjectDepth");
        objectDepthCompute.SetInt("ImageWidth", OutputWidth);
        objectDepthCompute.SetInt("ImageHeight", OutputHeight);
        objectDepthCompute.SetFloat("FocalLengthScale", 1.0f);

        threadGroupsX = Mathf.CeilToInt(OutputWidth / 8.0f);
        threadGroupsY = Mathf.CeilToInt(OutputHeight / 8.0f);
    }
#endregion
#region Model Execution
    public override IEnumerator RunModel(params Texture[] inputs)
    {
        modelRunning = true;
        if(InputImage != null) InputImage.texture = inputs[0];
        inputTensor = Unity.InferenceEngine.TextureConverter.ToTensor(inputs[0], toTensor);

        schedule = worker.ScheduleIterable(inputTensor);

        yield return RunInference();
    }

    protected override void PeekOutput()
    {
        DisposeOutput();

        outputTensor = worker.PeekOutput(0) as Unity.InferenceEngine.Tensor<float>;
        textureTensor = worker.PeekOutput(1) as Unity.InferenceEngine.Tensor<float>;
        outputTensor.ReadbackRequest();
        textureTensor.ReadbackRequest();
 
        inputTensor.Dispose();
    }

    protected override void ReadOutput()
    {
        
        outputTexture = Unity.InferenceEngine.TextureConverter.ToTexture(textureTensor);
        UpdateOutputImage();
    }

    protected override bool RequestsDone()
    {
        return outputTensor.IsReadbackRequestDone() && textureTensor.IsReadbackRequestDone();
    }


    /// <summary>
    /// Calculate the depths of the objects in the scene
    /// </summary>
    /// <param name="segmentationBuffer"></param>
    /// <param name="numObjects"></param>
    public override void CalculateObjectDepths(ComputeBuffer segmentationBuffer, int numObjects)
    {
        if(outputTensor == null) return;
        objectDepthCompute.SetBuffer(objectDepthKernel, "SegmentationBuffer", segmentationBuffer);
        objectDepthCompute.SetBuffer(objectDepthKernel, "DepthBuffer", Unity.InferenceEngine.ComputeTensorData.Pin(outputTensor).buffer);
        objectDepthCompute.SetBuffer(objectDepthKernel, "ObjectDepthBuffer", objectDepthBuffer);

        objectDepthCompute.SetInt("MaxObjectID", numObjects);
        objectDepthCompute.Dispatch(objectDepthKernel, threadGroupsX, threadGroupsY, 1);
        objectDepthBuffer.GetData(objectDepths);

    }
#endregion
#region Disposing
    public override void ResetOutput()
    {
        if(objectDepthBuffer != null) objectDepthBuffer.Release();
        objectDepthBuffer = new ComputeBuffer(MaxObjects, sizeof(float));

        objectDepths = new float[MaxObjects];
        objectDepthBuffer.SetData(objectDepths);
    }

    public override void DisposeOutput()
    {
        if(outputTensor != null) outputTensor.Dispose();
        if(textureTensor != null) textureTensor.Dispose();
      //  if(outputTexture != null) outputTexture.Release();
    }

    void OnDestroy()
    {
        if (outputTexture != null)
            outputTexture.Release();
        if (worker != null) worker.Dispose();
        DisposeOutput();
        if (objectDepthBuffer != null)
            objectDepthBuffer.Release();
        if(inputTensor != null) inputTensor.Dispose();
        
    }

#endregion    



}
