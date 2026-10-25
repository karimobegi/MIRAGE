using UnityEngine;
using Unity.InferenceEngine;
using Unity.VisualScripting;

/// <summary>
/// ImagePostProcessor that applies Gaussian Blur
/// 
/// For easier compatibility with the <see cref="PostProcessorSetting"/>
/// the RADIUS of the gaussian blur is passed in the  <see cref="PostProcessorSetting.color.r"/> channel
/// The SIGMA value is passed in the <see cref="PostProcessorSetting.color.g"/> channel
/// 
/// Assuming the color values are set to 0 to 255 range:
/// For RADIUS, enter a value between 0 and 30 (more is possible but not recommended)
/// 
/// For SIGMA, enter a value between 0 and 16 (more is possible but not recommended)
/// 
///  Author: J-Britten
/// </summary>
public class GaussianBlurImagePostProcessor : ImagePostProcessor
{
    private RenderTexture output;
    private RenderTexture temporaryRT;
    private int horizontalKernel;
    private int verticalKernel;
    private int combineKernel;

    public override void Initialize(SegmentationRunner r, DepthEstimationRunner d, RenderTexture Output)
    {
        // Load the Gaussian blur shader instead of the default one
        Shader = Resources.Load<ComputeShader>("PostProcessing/Shaders/GaussianBlurSegmentationShader");

        // Get all kernel handles
        horizontalKernel = Shader.FindKernel("HorizontalBlur");
        verticalKernel = Shader.FindKernel("VerticalBlur");
        
        // Create temporary render texture for intermediate results
        temporaryRT = new RenderTexture(r.ImageWidth, r.ImageHeight, 0, RenderTextureFormat.ARGB32);
        temporaryRT.enableRandomWrite = true;
        temporaryRT.Create();
        output = Output;

        base.Initialize(r, d, Output);
       
        combineKernel = kernelHandle;
    }

    public override void UpdateClasses() {
       base.UpdateClasses();
        Shader.SetBuffer(horizontalKernel, "selectedClasses", classSettingsBuffer);
        Shader.SetBuffer(verticalKernel, "selectedClasses", classSettingsBuffer);
    }


    public override void ExecuteShader(RenderTexture Input)
    {

        if (!IsRunning) return;
        if (depthEstimationRunner.ObjectDepthBuffer == null) return;
        Shader.SetInt("_UseDepthFilter", depthEstimationRunner.IsEnabled && depthEstimationRunner.IsPrepared ? 1 : 0);
        Shader.SetInt("_DepthObjectCapacity", depthEstimationRunner.MaxObjects);

        // Set common buffers and parameters
        Shader.SetBuffer(horizontalKernel, "outputArray", segmentationRunner.OutputBuffer);
        Shader.SetBuffer(horizontalKernel, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);
        Shader.SetTexture(horizontalKernel, "InputTexture", Input);
        Shader.SetTexture(horizontalKernel, "TempResult", temporaryRT);

        // Horizontal pass
        Shader.Dispatch(horizontalKernel,
            Mathf.CeilToInt(segmentationRunner.ImageWidth / 8f),
            Mathf.CeilToInt(segmentationRunner.ImageHeight / 8f),
            1);

        // Set up vertical pass
        Shader.SetBuffer(verticalKernel, "outputArray", segmentationRunner.OutputBuffer);
        Shader.SetBuffer(verticalKernel, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);
        Shader.SetTexture(verticalKernel, "InputTexture", Input);
        Shader.SetTexture(verticalKernel, "TempResult", temporaryRT);
        Shader.SetTexture(verticalKernel, "Result", output);

        // Vertical pass
        Shader.Dispatch(verticalKernel,
            Mathf.CeilToInt(segmentationRunner.ImageWidth / 8f),
            Mathf.CeilToInt(segmentationRunner.ImageHeight / 8f),
            1);

        // Final combine pass
        Shader.SetTexture(combineKernel, "Result", output);
        Shader.Dispatch(combineKernel,
            Mathf.CeilToInt(segmentationRunner.ImageWidth / 8f),
            Mathf.CeilToInt(segmentationRunner.ImageHeight / 8f),
            1);
    }

    void OnDestroy()
    {
        if (classSettingsBuffer != null)
        {
            classSettingsBuffer.Release();
            classSettingsBuffer = null;
        }
        if (temporaryRT != null)
        {
            temporaryRT.Release();
            Destroy(temporaryRT);
            temporaryRT = null;
        }
    }
}
