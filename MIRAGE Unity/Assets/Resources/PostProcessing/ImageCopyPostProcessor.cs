using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Helper Post Procssor class taht copies only the segmentation output to a new texture, filtered based on the valid objects
///
/// 
/// author: J-Britten
/// </summary>
public class ImageCopyPostProcessor : ImagePostProcessor, IEffectHandler
{

    public RenderTexture OutputTexture;   
    public override void Initialize(SegmentationRunner r, DepthEstimationRunner d, RenderTexture Output) {
        if (Shader == null)
            Shader = Resources.Load<ComputeShader>("PostProcessing/Shaders/CopySegmentationShader");
        
        base.Initialize(r, d, Output);
        var activeShader = ActiveShader;
        if (activeShader == null) return;
        
        activeShader.SetInt("inputWidth", segmentationRunner.ImageWidth);
        activeShader.SetInt("inputHeight", segmentationRunner.ImageHeight);

        if (OutputTexture != null) {
            OutputTexture.Release();
            Destroy(OutputTexture);
        }
        OutputTexture = new RenderTexture(segmentationRunner.ImageWidth, segmentationRunner.ImageHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
        OutputTexture.enableRandomWrite = true;
        OutputTexture.Create();
        activeShader.SetTexture(kernelHandle, "OutputTexture", OutputTexture);
    }

    /// <summary>
    /// Execute the shader
    /// </summary>
    /// <param name="Input">The current input frame</param>
    public override void ExecuteShader(RenderTexture Input) {
       if(!IsRunning) return;
       if (depthEstimationRunner.ObjectDepthBuffer == null) return;
       var activeShader = ActiveShader;
       if (activeShader == null) return;
       activeShader.SetInt("_UseDepthFilter", depthEstimationRunner.IsDepthFilteringActive ? 1 : 0);
       activeShader.SetInt("_DepthObjectCapacity", depthEstimationRunner.MaxObjects);
       activeShader.SetBuffer(kernelHandle, "outputArray", segmentationRunner.OutputBuffer);
       activeShader.SetTexture(kernelHandle, "InputTexture", Input);
       activeShader.SetBuffer(kernelHandle, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);   
       activeShader.Dispatch(kernelHandle, Mathf.CeilToInt(segmentationRunner.ImageWidth / 8f), Mathf.CeilToInt(segmentationRunner.ImageHeight / 8f), 1);
    }

    protected override void OnDestroy() {
        if (OutputTexture != null) { OutputTexture.Release(); Destroy(OutputTexture); OutputTexture = null; }
        base.OnDestroy();
    }

}
