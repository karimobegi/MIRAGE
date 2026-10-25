using System.Collections;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;
using System.Runtime.InteropServices;
using System.Linq;

/// <summary>
/// GPU PostProcessor class that handles the post-processing effects applied to the segmentation output.
/// 
/// This script uses the segmentation mask and depth estimation output.
/// 
/// See the <see cref="ColoredMaskSegmentationShader"/> for a shader code reference.
/// 
/// If the shader is supposed to manipulate the original input image, refer to the <see cref="ImagePostProcessor"/> 
/// 
/// Author: J-Britten
/// </summary>
public class PostProcessor : MonoBehaviour, IEffectHandler
{

#region Variables
    /// <summary>
    /// The post-processing shader
    /// </summary>
    public ComputeShader Shader;

    /// <summary>
    /// The settings for the post-processing effect.
    /// </summary>
    public PostProcessorSetting[] ClassSettings = new PostProcessorSetting[] {};

    protected PostProcessorSettingStruct[] classSettingsStructs = new PostProcessorSettingStruct[] {};
    /// <summary>
    /// Shader kernel
    /// </summary>
    protected int kernelHandle;

    /// <summary>
    /// The segmentation runner that provides the segmentation output.
    /// </summary>
    protected SegmentationRunner segmentationRunner;

    /// <summary>
    /// The depth estimation runner that provides the depth estimation output.
    /// </summary>

    protected DepthEstimationRunner depthEstimationRunner;

    /// <summary>
    /// The compute buffer that stores the selected classes for the post-processing effect.
    /// </summary>
    protected ComputeBuffer classSettingsBuffer;

    [SerializeField]
    protected EffectType m_EffectType;
    public EffectType EffectType { get => m_EffectType;}

    public bool IsRunning = false;

#endregion
#region Effect Initialization
    public virtual void Initialize(SegmentationRunner s, DepthEstimationRunner d, RenderTexture Output) {
        segmentationRunner = s;
        depthEstimationRunner = d;

        int outputWidth = segmentationRunner.OutputWidth;
        int outputHeight = segmentationRunner.OutputHeight;

       // Set up the computer shader, all these variables sty the same
        kernelHandle = Shader.FindKernel("CSMain");
        Shader.SetTexture(kernelHandle, "Result", Output);
        Shader.SetInt("width", outputWidth);
        Shader.SetInt("height", outputHeight);

       // Shader.SetBuffer(kernelHandle, "selectedClasses", selectedClassesBuffer);
        UpdateClasses();
    }
    public virtual void UpdateClasses() {
        IsRunning = ClassSettings.Length > 0;
        if(ClassSettings.Length == 0) //We have this in here to ensure that the buffer is initialized and this doesnt crash
        {              
            classSettingsStructs = new PostProcessorSettingStruct[] {new PostProcessorSetting(-2, Color.black).ToStruct()};
        } else {
            classSettingsStructs = ClassSettings.Select(x => x.ToStruct()).ToArray();
        }
   
        if(classSettingsBuffer != null) {
            classSettingsBuffer.Release();
            classSettingsBuffer = null;
        }

        int stride = sizeof(int) + 2 * sizeof(float) + 4 * sizeof(float);
        classSettingsBuffer = new ComputeBuffer(classSettingsStructs.Length, stride);
        classSettingsBuffer.SetData(classSettingsStructs);
                
        Shader.SetInt("selectedClassesCount", classSettingsStructs.Length);
        Shader.SetBuffer(kernelHandle, "selectedClasses", classSettingsBuffer);
    }

    public void UpdateClasses(List<EffectSetting> classes)
    {
        ClassSettings = classes.Cast<PostProcessorSetting>().ToArray();

        UpdateClasses();
    }

#endregion

#region Shader Execution
    public void ExecuteShader() {
        if(ClassSettings.Length == 0 || !IsRunning) return;
        if(depthEstimationRunner.ObjectDepthBuffer == null) return;
        Shader.SetInt("_UseDepthFilter", depthEstimationRunner.IsEnabled && depthEstimationRunner.IsPrepared ? 1 : 0);
        Shader.SetInt("_DepthObjectCapacity", depthEstimationRunner.MaxObjects);
        Shader.SetBuffer(kernelHandle, "outputArray", segmentationRunner.OutputBuffer);
        Shader.SetBuffer(kernelHandle, "objectDepths", depthEstimationRunner.ObjectDepthBuffer);   
        Shader.Dispatch(kernelHandle, Mathf.CeilToInt(segmentationRunner.OutputWidth / 8f), Mathf.CeilToInt(segmentationRunner.OutputHeight / 8f), 1);
    }

#endregion
    private void OnDestroy() {
        if (classSettingsBuffer != null) {
            classSettingsBuffer.Release();
            classSettingsBuffer = null;
        }
    }

}

