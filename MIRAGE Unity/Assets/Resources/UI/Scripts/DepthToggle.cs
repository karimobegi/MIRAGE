using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DepthToggle : MonoBehaviour
{
    
    private DepthEstimationRunner depthEstimationRunner;
    void Start()
    {
        depthEstimationRunner = FindObjectOfType<DepthEstimationRunner>();
    }
    public void OnToggle(bool value)
    {
        Debug.Log($"DepthToggle.OnToggle called with: {value}, Frame: {Time.frameCount}");Debug.Log($"OnToggle({value}), Frame: {Time.frameCount}\n{System.Environment.StackTrace}");
        if (value)
        {
            depthEstimationRunner.IsEnabled = true;
        }
        else
        {
            depthEstimationRunner.IsEnabled = false;
            depthEstimationRunner.ResetOutput();
        }

    }
}
