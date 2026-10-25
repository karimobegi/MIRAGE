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
            // Pipeline resets at the next frame boundary; never release a buffer
            // while an in-flight GPU reduction or inpainting stage still uses it.
        }

    }
}
