using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

[Obsolete("This class is deprecated, use CameraInput instead")]
public class DebugVideoPlayerInput : MonoBehaviour
{

    public static DebugVideoPlayerInput Instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        Instance = null;
    }
    public VideoPlayer videoPlayer;
    // Start is called before the first frame update

    public int Width = 1280;
    public int Height = 720;
    public RawImage VideoPanel;
    public CustomRenderTexture CurrentFrame;

    void Awake()
    {
        Instance = this;
        CurrentFrame = new CustomRenderTexture(Width,Height,RenderTextureFormat.ARGB32);
        videoPlayer = GetComponent<VideoPlayer>();

        videoPlayer.targetTexture = CurrentFrame;
        VideoPanel.texture = CurrentFrame;
        videoPlayer.Play();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

}
