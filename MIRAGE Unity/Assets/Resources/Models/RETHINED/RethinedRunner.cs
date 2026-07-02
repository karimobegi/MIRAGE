using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework.Constraints;
using Unity.InferenceEngine;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;



public class RETHINEDRunner : InpaintingRunner, IEffectHandler
{   

#region Variables

    public EffectType EffectType => EffectType.Inpainting;

    /// <summary>
    /// Classes for Inpainting
    /// </summary>
    public InpaintingSetting[] SelectedClasses;

    private InpaintingSettingStruct[] selectedClassesStructs = new InpaintingSettingStruct[] { };

    public RawImage MaskImage;
    public RawImage DebugOutputImage;

    private Unity.InferenceEngine.TextureTransform imgTransform, maskTransform;

    private Unity.InferenceEngine.Tensor<float> inputTensor;
    private Unity.InferenceEngine.Tensor<float> inputMaskTensor;
    private Unity.InferenceEngine.Tensor<float> outputTensor;

    /// <summary>
    /// Shader that validates the objects detected by YOLO
    /// </summary>
    private ComputeShader objectValidator;
    /// <summary>
    /// Shader that creates the mask based on the yolo output and verified objects
    /// </summary>
    private ComputeShader createMaskShader;
    private int objectValidatorKernel;
    private int createMaskKernel;
    private ComputeBuffer validObjectsBuffer, settingsBuffer;

    private int createMaskThreadGroups;

    private RenderTexture maskOutputTexture;

#endregion
#region Model Preparation
    protected override void PrepareModel()
    {
        imgTransform = new TextureTransform().SetDimensions(InputWidth, InputHeight, 3);
        maskTransform = new TextureTransform().SetDimensions(InputWidth, InputHeight, 1);

        PreparePreProcessing();

        var model = Unity.InferenceEngine.ModelLoader.Load(ModelAsset);

        var graph = new Unity.InferenceEngine.FunctionalGraph();

        var inputImg = graph.AddInput(Unity.InferenceEngine.DataType.Float, new Unity.InferenceEngine.DynamicTensorShape(1, 3, -1, -1));
        var inputMsk = graph.AddInput(Unity.InferenceEngine.DataType.Float, new Unity.InferenceEngine.DynamicTensorShape(1, 1, -1, -1));

        inputMsk = Unity.InferenceEngine.Functional.Interpolate(inputMsk, new int[] { InputHeight, InputWidth }, mode: "nearest");
        var ceil = Unity.InferenceEngine.Functional.Ceil(inputMsk);

        // RETHINED expects 1 = missing, 0 = known — no inversion needed
        // Pre-masking is handled inside the ONNX wrapper
        var outputs = Unity.InferenceEngine.Functional.Forward(model, inputImg, ceil);
        var op = outputs[0];

        op = op * inputMsk;

        var alpha = Unity.InferenceEngine.Functional.Concat(new[] { op, inputMsk }, 1);
        runtimeModel = graph.Compile(alpha, ceil);

        // Initialize output textures once to avoid recreating them every frame
        PrepareOutputTextures();
    }

    private void PrepareOutputTextures()
    {
        // Create output texture with proper format and size
        if (outputTexture != null) outputTexture.Release();
        outputTexture = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32);
        outputTexture.enableRandomWrite = true;
        outputTexture.Create();

        // Create mask output texture
        if (maskOutputTexture != null) maskOutputTexture.Release();
        maskOutputTexture = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.R8);
        maskOutputTexture.enableRandomWrite = true;
        maskOutputTexture.Create();
    }

    private void PreparePreProcessing()
    {
        objectValidator = Resources.Load<ComputeShader>("Models/MI-GAN/ObjectValidator");
        createMaskShader = Resources.Load<ComputeShader>("Models/MI-GAN/CreateMask");
        objectValidatorKernel = objectValidator.FindKernel("ValidateObjects");
        validObjectsBuffer = new ComputeBuffer(256, sizeof(uint));
        objectValidator.SetBuffer(objectValidatorKernel, "ValidObjects", validObjectsBuffer);
        UpdateClasses();

        inputMaskTensor = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 1, ImageHeight, ImageWidth));

        createMaskKernel = createMaskShader.FindKernel("CreateMask");

        createMaskThreadGroups = Mathf.CeilToInt((ImageWidth * ImageHeight) / 64f);
        createMaskShader.SetBuffer(createMaskKernel, "ValidObjects", validObjectsBuffer);
        createMaskShader.SetBuffer(createMaskKernel, "MaskBuffer", Unity.InferenceEngine.ComputeTensorData.Pin(inputMaskTensor).buffer);
        createMaskShader.SetInt("NumMaskPositions", ImageWidth * ImageHeight);
    }

    public void UpdateClasses()
    {
        if (SelectedClasses == null || SelectedClasses.Length == 0)
        {
            selectedClassesStructs = new InpaintingSettingStruct[] { new InpaintingSetting(-2, 0, 100).ToStruct() };
        }
        else
        {
            selectedClassesStructs = SelectedClasses.Select(x => x.ToStruct()).ToArray();
        }

        if (settingsBuffer != null) settingsBuffer.Release();

        settingsBuffer = new ComputeBuffer(selectedClassesStructs.Length, sizeof(int) + sizeof(float) * 2);
        settingsBuffer.SetData(selectedClassesStructs);
        objectValidator.SetBuffer(objectValidatorKernel, "SelectedClasses", settingsBuffer);
        objectValidator.SetInt("NumSelectedClasses", selectedClassesStructs.Length);
    }

    public void UpdateClasses(List<EffectSetting> classes)
    {
        SelectedClasses = classes.Cast<InpaintingSetting>().ToArray();
        UpdateClasses();
    }

#endregion
#region Model Execution
    public override IEnumerator RunModel(Texture input, ComputeBuffer mask, ComputeBuffer depthBuffer)
    {
        throw new NotImplementedException();
    }

    public override IEnumerator RunModel(Texture input, YOLOSegmentationRunner yolo, ComputeBuffer depthBuffer)
    {
        if (yolo.NumObjDetected == 0) yield break;

        // Run object validator first
        objectValidator.SetBuffer(objectValidatorKernel, "ClassIds", yolo.ClassIdsBuffer);
        objectValidator.SetBuffer(objectValidatorKernel, "DepthBuffer", depthBuffer);
        objectValidator.SetInt("NumObjects", yolo.NumObjDetected);

        int threadGroups = Mathf.CeilToInt(yolo.NumObjDetected / 64f);
        objectValidator.Dispatch(objectValidatorKernel, threadGroups, 1, 1);

        // Now run the CreateMask shader
        createMaskShader.SetBuffer(createMaskKernel, "YOLOMaskBuffer", yolo.OutputBuffer);
        createMaskShader.Dispatch(createMaskKernel, createMaskThreadGroups, 1, 1);

        inputTensor = Unity.InferenceEngine.TextureConverter.ToTensor(input, imgTransform);
        schedule = worker.ScheduleIterable(inputTensor, inputMaskTensor);
        // Run the inpainting model
        yield return RunInference();
    }

    /// <summary>
    /// Basic run model without any additional filtering
    /// </summary>
    /// <param name="inputs">Input Texture, Mask Texture</param>
    /// <returns></returns>
    public override IEnumerator RunModel(params Texture[] inputs)
    {
        inputTensor = Unity.InferenceEngine.TextureConverter.ToTensor(inputs[0], imgTransform);
        inputMaskTensor = Unity.InferenceEngine.TextureConverter.ToTensor(inputs[1], maskTransform);

        schedule = worker.ScheduleIterable(inputTensor, inputMaskTensor);

        yield return RunInference();
    }

    protected override bool RequestsDone()
    {
        return outputTensor.IsReadbackRequestDone();
    }

    protected override void PeekOutput()
    {
        outputTensor = worker.PeekOutput(0) as Unity.InferenceEngine.Tensor<float>;
        outputTensor.ReadbackRequest();
    }

    protected override void ReadOutput()
    {
        // Use new RenderToTexture API with pre-created textures
        var outputTransform = new TextureTransform();
        Unity.InferenceEngine.TextureConverter.RenderToTexture(outputTensor, outputTexture, outputTransform);

        if (MaskImage != null)
        {
            var maskTransform = new TextureTransform();
            Unity.InferenceEngine.TextureConverter.RenderToTexture(inputMaskTensor, maskOutputTexture, maskTransform);
            MaskImage.texture = maskOutputTexture;
        }

        if (DebugOutputImage != null)
        {
            DebugOutputImage.texture = outputTexture;
        }
        UpdateOutputImage();
        DisposeOutput();
    }

#endregion
    public override void DisposeOutput()
    {
        if (outputTensor != null) outputTensor.Dispose();
        if (inputTensor != null) inputTensor.Dispose();
    }

    void OnDestroy()
    {
        DisposeOutput();
        if (worker != null) worker.Dispose();
        if (outputTexture != null) outputTexture.Release();
        if (maskOutputTexture != null) maskOutputTexture.Release();
        if (validObjectsBuffer != null) validObjectsBuffer.Release();
        if (settingsBuffer != null) settingsBuffer.Release();
    }
}