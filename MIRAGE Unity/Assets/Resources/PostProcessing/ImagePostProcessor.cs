using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 
/// GPU PostProcessor class that handles the post-processing effects applied to the segmentation output.
/// 
/// A variant of the <see cref="PostProcessor"/> that manipulates the input image based on the segmentation output.
/// 
/// To make the <see cref="PostProcessingSetting"/> class reusable, we pass shader-specific settings as RGBA values in the <see cref="Color"/> variable.
/// 
/// For setting the Class Settings in Editor, these are the custom parameters: (assuming the color is in RGBA 0-255 format)
/// For the Kuwahara Segmentation Shader:
/// Red: Filter Radius (1-5)
/// Green: Number of Sectors (4-16)
/// Blue: Strength (0-1) * 255
/// 
/// Author: J-Britten
/// </summary>
public class ImagePostProcessor : PostProcessor, IEffectHandler
{
    
    public override void Initialize(SegmentationRunner r, DepthEstimationRunner d, RenderTexture Output) {
        base.Initialize(r, d, Output);
        Shader.SetInt("inputWidth", segmentationRunner.ImageWidth);
        Shader.SetInt("inputHeight", segmentationRunner.ImageHeight);

    }

    /// <summary>
    /// Execute the shader
    /// </summary>
    /// <param name="Input">The current input frame</param>
    public virtual void ExecuteShader(RenderTexture Input) {
        
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
        if (classSettingsBuffer != null) {
            classSettingsBuffer.Release();
            classSettingsBuffer = null;
        }
    }

}
