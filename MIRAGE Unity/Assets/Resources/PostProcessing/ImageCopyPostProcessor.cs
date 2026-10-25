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
        Shader = Resources.Load<ComputeShader>("PostProcessing/Shaders/CopySegmentationShader");
        
        base.Initialize(r, d, Output);
        
        Shader.SetInt("inputWidth", segmentationRunner.ImageWidth);
        Shader.SetInt("inputHeight", segmentationRunner.ImageHeight);



        OutputTexture = new RenderTexture(segmentationRunner.ImageWidth, segmentationRunner.ImageHeight, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
        OutputTexture.enableRandomWrite = true;
        OutputTexture.Create();
        Shader.SetTexture(kernelHandle, "OutputTexture", OutputTexture);
    }

    /// <summary>
    /// Execute the shader
    /// </summary>
    /// <param name="Input">The current input frame</param>
    public override void ExecuteShader(RenderTexture Input) {
        
        if(!IsRunning) return;
       if (depthEstimationRunner.ObjectDepthBuffer == null) return;
       Shader.SetInt("_UseDepthFilter", depthEstimationRunner.IsEnabled && depthEstimationRunner.IsPrepared ? 1 : 0);
       Shader.SetInt("_DepthObjectCapacity", depthEstimationRunner.MaxObjects);
       Shader.SetBuffer(kernelHandle, "outputArray", segmentationRunner.OutputBuffer);
       Shader.SetTexture(kernelHandle, "InputTexture", Input);
       Shader.SetBuffer(kernelHandle, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);   
       Shader.Dispatch(kernelHandle, Mathf.CeilToInt(segmentationRunner.ImageWidth / 8f), Mathf.CeilToInt(segmentationRunner.ImageHeight / 8f), 1);
    }

    private void OnDestroy() {
        if (OutputTexture != null) { OutputTexture.Release(); Destroy(OutputTexture); }
        if (classSettingsBuffer != null) {
            classSettingsBuffer.Release();
            classSettingsBuffer = null;
        }
    }

}
