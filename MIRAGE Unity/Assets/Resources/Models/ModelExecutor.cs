using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Script to quickly execute a models on a set of images
/// 
/// 
/// </summary>
public class ModelExecutor : MonoBehaviour
{

    public Texture2D[] DebugInput;

    public bool LiveInput = false;
    private ModelRunner modelRunner;
    // Start is called before the first frame update
    void Start()
    {
        modelRunner = FindAnyObjectByType<ModelRunner>();
        if(!LiveInput) {
           // Debug.Log(DebugInput.width + " " + DebugInput.height);
            StartCoroutine(modelRunner.RunModel(DebugInput));
        }
    }

    // Update is called once per frame
    void Update()
    {
        if(LiveInput) {
            if(!modelRunner.IsRunning) StartCoroutine(modelRunner.RunModel(DebugVideoPlayerInput.Instance.CurrentFrame));
        }
    }
}
