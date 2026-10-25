using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InpaintingToggle : MonoBehaviour
{
    InpaintingRunner inpaintingRunner;

    void Start()
    {
        var pipeline = FindAnyObjectByType<Pipeline>();
        inpaintingRunner = pipeline != null ? pipeline.ActiveInpaintingModel : FindAnyObjectByType<InpaintingRunner>();
    }
    
    public void OnToggle(bool value)
    {
        if (inpaintingRunner == null) return;
        if (value)
        {
            inpaintingRunner.IsEnabled = true;
        }
        else
        {
            inpaintingRunner.IsEnabled = false;
        }

    }
}
