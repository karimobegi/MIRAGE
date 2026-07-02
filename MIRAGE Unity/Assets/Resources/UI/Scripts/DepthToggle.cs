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
