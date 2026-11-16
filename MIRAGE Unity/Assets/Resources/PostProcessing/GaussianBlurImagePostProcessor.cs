using UnityEngine;
using Unity.InferenceEngine;

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
    private bool blurKernelsReady;

    public override void Initialize(SegmentationRunner r, DepthEstimationRunner d, RenderTexture Output)
    {
        // Load the Gaussian blur shader instead of the default one
        if (Shader == null)
            Shader = Resources.Load<ComputeShader>("PostProcessing/Shaders/GaussianBlurSegmentationShader");

        if (temporaryRT != null)
        {
            temporaryRT.Release();
            Destroy(temporaryRT);
        }
        // Create temporary render texture for intermediate results
        temporaryRT = new RenderTexture(r.ImageWidth, r.ImageHeight, 0, RenderTextureFormat.ARGB32);
        temporaryRT.enableRandomWrite = true;
        temporaryRT.Create();
        output = Output;
        blurKernelsReady = false;

        base.Initialize(r, d, Output);
        var activeShader = ActiveShader;
        if (activeShader == null) return;

        horizontalKernel = activeShader.FindKernel("HorizontalBlur");
        verticalKernel = activeShader.FindKernel("VerticalBlur");
        blurKernelsReady = true;
        activeShader.SetBuffer(horizontalKernel, "selectedClasses", classSettingsBuffer);
        activeShader.SetBuffer(verticalKernel, "selectedClasses", classSettingsBuffer);
    }

    public override void UpdateClasses() {
        base.UpdateClasses();
        var activeShader = ActiveShader;
        if (!blurKernelsReady || activeShader == null) return;
        activeShader.SetBuffer(horizontalKernel, "selectedClasses", classSettingsBuffer);
        activeShader.SetBuffer(verticalKernel, "selectedClasses", classSettingsBuffer);
    }


    public override void ExecuteShader(RenderTexture Input)
    {
        if (!IsRunning || !blurKernelsReady) return;
        if (depthEstimationRunner.ObjectDepthBuffer == null) return;
        var activeShader = ActiveShader;
        if (activeShader == null) return;
        activeShader.SetInt("_UseDepthFilter", depthEstimationRunner.IsDepthFilteringActive ? 1 : 0);
        activeShader.SetInt("_DepthObjectCapacity", depthEstimationRunner.MaxObjects);

        int groupsX = Mathf.CeilToInt(segmentationRunner.ImageWidth / 8f);
        int groupsY = Mathf.CeilToInt(segmentationRunner.ImageHeight / 8f);

        // Set common buffers and parameters
        activeShader.SetBuffer(horizontalKernel, "outputArray", segmentationRunner.OutputBuffer);
        activeShader.SetBuffer(horizontalKernel, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);
        activeShader.SetTexture(horizontalKernel, "InputTexture", Input);
        activeShader.SetTexture(horizontalKernel, "TempResult", temporaryRT);

        // Horizontal pass
        activeShader.Dispatch(horizontalKernel, groupsX, groupsY, 1);

        // Vertical pass writes directly to Result (already cleared by Pipeline before post-processing)
        activeShader.SetBuffer(verticalKernel, "outputArray", segmentationRunner.OutputBuffer);
        activeShader.SetBuffer(verticalKernel, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);
        activeShader.SetTexture(verticalKernel, "InputTexture", Input);
        activeShader.SetTexture(verticalKernel, "TempResult", temporaryRT);
        activeShader.SetTexture(verticalKernel, "Result", output);
        activeShader.Dispatch(verticalKernel, groupsX, groupsY, 1);
    }

    protected override void OnDestroy()
    {
        if (temporaryRT != null)
        {
            temporaryRT.Release();
            Destroy(temporaryRT);
            temporaryRT = null;
        }
        base.OnDestroy();
    }
}
