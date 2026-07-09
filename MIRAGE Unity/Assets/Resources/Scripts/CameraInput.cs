using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

#if UNITY_ANDROID
using Meta.XR;
#endif

/// <summary>
/// Class that handles the input for the pipeline
/// 
/// It can either use a webcam or a video player as input.
/// On Quest 3 standalone builds, uses the Passthrough Camera API instead of WebCamTexture.
/// 
/// Author: J-Britten
/// Modified: K-Obegi (PCA integration for Quest 3)
/// </summary>
public class CameraInput : MonoBehaviour
{
    /// <summary>
    /// Singleton
    /// </summary>
    public static CameraInput Instance;

    /** Camera Settings **/
    public int RequestedWidth = 1280;
    public int RequestedHeight = 720;
    public int FPS = 30;
    public bool startOnAwake = true;

    [SerializeField]
    private string selectedCameraDeviceName;
    private WebCamTexture webcamTexture;

    /** Debug Video Settings **/
    public bool UseDebugVideoInput;
    public VideoPlayer VideoPlayer;

    public RawImage VideoPanel; 
    public RenderTexture CurrentFrame;

    // PCA fields — only used on Quest 3 standalone builds
    #if UNITY_ANDROID
    [SerializeField]
    private PassthroughCameraAccess passthroughCameraAccess;
    private Texture pcaTexture;
    private bool pcaReady = false;
    #endif

    void Awake()
    {
        Debug.Log($"CameraInput Awake: UseDebugVideoInput={UseDebugVideoInput}, startOnAwake={startOnAwake}");
        Instance = this;

        if (UseDebugVideoInput)
        {
            StartVideo();
        }
        else if (startOnAwake)
        {
            StartCamera();
        }
    }

    public void StartVideo()
    {
        CurrentFrame = new CustomRenderTexture(RequestedWidth, RequestedHeight, RenderTextureFormat.ARGB32);
        VideoPlayer.targetTexture = CurrentFrame;
        VideoPanel.texture = CurrentFrame;
        VideoPlayer.Play();
    }

    public void StartCamera()
    {
        Debug.Log("CameraInput StartCamera called");
        #if UNITY_ANDROID && !UNITY_EDITOR
        // === Quest 3 standalone path: use Passthrough Camera API ===
        StartCoroutine(StartPCA());
        #else
        // === PC path: use WebCamTexture (unchanged) ===
        if (webcamTexture != null) return;

        webcamTexture = string.IsNullOrEmpty(selectedCameraDeviceName)
            ? new WebCamTexture(RequestedWidth, RequestedHeight, FPS)
            : new WebCamTexture(selectedCameraDeviceName, RequestedWidth, RequestedHeight, FPS);

        webcamTexture.Play();
        CurrentFrame = new RenderTexture(webcamTexture.width, webcamTexture.height, 0);
        CurrentFrame.Create();

        if (VideoPanel != null)
        {
            VideoPanel.texture = webcamTexture;
        }
        else
        {
            Debug.LogWarning("No RawImage assigned to display the camera feed.");
        }
        #endif
    }

    #if UNITY_ANDROID && !UNITY_EDITOR
    private IEnumerator StartPCA()
    {
        // Request the two permissions PCA requires
        OVRPermissionsRequester.Request(new[]
        {
            OVRPermissionsRequester.Permission.Scene,
            OVRPermissionsRequester.Permission.PassthroughCameraAccess
        });

        // Check that the component is assigned
        if (passthroughCameraAccess == null)
        {
            Debug.LogError("PassthroughCameraAccess component not assigned in CameraInput.");
            yield break;
        }

        Debug.Log("Waiting for PassthroughCameraAccess to start...");

        // Wait until the PCA is ready and streaming frames
        while (!passthroughCameraAccess.IsPlaying)
        {
            yield return null;
        }

        Debug.Log("PassthroughCameraAccess is playing.");

        // Get the texture that PCA updates each frame
        pcaTexture = passthroughCameraAccess.GetTexture();

        // Create the CurrentFrame RenderTexture to match PCA resolution
        CurrentFrame = new RenderTexture(pcaTexture.width, pcaTexture.height, 0);
        CurrentFrame.Create();

        if (VideoPanel != null)
        {
            VideoPanel.texture = CurrentFrame;
        }

        pcaReady = true;
        Debug.Log($"PCA started: {pcaTexture.width}x{pcaTexture.height}");
    }
    #endif

    public void StopCamera()
    {
        #if UNITY_ANDROID && !UNITY_EDITOR
        pcaReady = false;
        pcaTexture = null;
        #else
        if (webcamTexture == null) return;
        webcamTexture.Stop();
        #endif

        if (CurrentFrame != null)
        {
            CurrentFrame.Release();
            Destroy(CurrentFrame);
        }

        if (VideoPanel != null)
        {
            VideoPanel.texture = null;
            VideoPanel.material.mainTexture = null;
        }
    }

    void Update()
    {
        #if UNITY_ANDROID && !UNITY_EDITOR
        // Quest 3: blit PCA texture into CurrentFrame
        if (pcaReady && pcaTexture != null)
        {
            Graphics.Blit(pcaTexture, CurrentFrame);
        }
        #else
        // PC: blit WebCamTexture into CurrentFrame
        if (webcamTexture != null && webcamTexture.isPlaying)
        {
            Graphics.Blit(webcamTexture, CurrentFrame);
        }
        #endif
    }
}