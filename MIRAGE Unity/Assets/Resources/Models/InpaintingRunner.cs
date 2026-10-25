using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Abstract class for Inpainting Models
/// 
/// Author: J-Britten
/// </summary>
public abstract class InpaintingRunner : ModelRunner
{
    // Disabled depth estimation means class-only effect selection.
    public bool UseDepthFilter { get; set; } = true;

    /// <summary>Discard cached fills when switching input, seeking, or changing models.</summary>
    public virtual void ResetHistory() { }

    /// <summary>
    /// Run the model with the given input and mask as a buffer
    /// </summary>
    /// <param name="input"></param>
    /// <param name="mask"></param>
    /// <param name="depthBuffer"></param>
    /// <returns></returns>
    public abstract IEnumerator RunModel(Texture input, ComputeBuffer mask, ComputeBuffer depthBuffer);

    public abstract IEnumerator RunModel(Texture input, YOLOSegmentationRunner yolo, ComputeBuffer depthBuffer);
}
