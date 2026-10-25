using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>Camera/video input with frame arrival timestamps and discontinuity IDs.</summary>
public class CameraInput : MonoBehaviour
{
    public static CameraInput Instance;
    public int RequestedWidth = 1280;
    public int RequestedHeight = 720;
    public int FPS = 30;
    public bool startOnAwake = true;
    [SerializeField] private string selectedCameraDeviceName;
    private WebCamTexture webcamTexture;
    public bool UseDebugVideoInput;
    public VideoPlayer VideoPlayer;
    public RawImage VideoPanel;
    public RenderTexture CurrentFrame;

    public long FrameId { get; private set; }
    public long SourceVersion { get; private set; }
    public double FrameTimestamp { get; private set; }
    public bool HasFrame { get; private set; }
    private long previousVideoFrame = -1;

    private void Awake()
    {
        Instance = this;
        if (UseDebugVideoInput) StartVideo();
        else if (startOnAwake) StartCamera();
    }

    public void StartVideo()
    {
        StopCamera();
        DetachVideo();
        if (VideoPlayer == null)
        {
            Debug.LogError("Assign a VideoPlayer before starting video input.", this);
            return;
        }
        ReleaseFrame();
        UseDebugVideoInput = true;
        NotifySourceDiscontinuity();
        AllocateFrame(Mathf.Max(1, RequestedWidth), Mathf.Max(1, RequestedHeight));
        VideoPlayer.targetTexture = CurrentFrame;
        VideoPlayer.sendFrameReadyEvents = true;
        VideoPlayer.frameReady += OnVideoFrame;
        VideoPlayer.seekCompleted += OnSeekCompleted;
        if (VideoPanel != null) VideoPanel.texture = CurrentFrame;
        VideoPlayer.Play();
    }

    public void StartCamera()
    {
        if (webcamTexture != null && webcamTexture.isPlaying) return;
        DetachVideo();
        if (VideoPlayer != null) VideoPlayer.Stop();
        if (webcamTexture != null) Destroy(webcamTexture);
        ReleaseFrame();
        UseDebugVideoInput = false;
        NotifySourceDiscontinuity();
        webcamTexture = string.IsNullOrEmpty(selectedCameraDeviceName)
            ? new WebCamTexture(RequestedWidth, RequestedHeight, FPS)
            : new WebCamTexture(selectedCameraDeviceName, RequestedWidth, RequestedHeight, FPS);
        webcamTexture.Play();
        // Device dimensions often start at 16x16. Allocate on its first real frame.
    }

    public void StopCamera()
    {
        if (webcamTexture == null) return;
        webcamTexture.Stop();
        Destroy(webcamTexture);
        webcamTexture = null;
        ReleaseFrame();
        NotifySourceDiscontinuity();
        if (VideoPanel != null) VideoPanel.texture = null;
    }

    /// <summary>Call after external seeks or input replacement to invalidate temporal history.</summary>
    public void NotifySourceDiscontinuity()
    {
        SourceVersion++;
        previousVideoFrame = -1;
        HasFrame = false;
    }

    private void OnSeekCompleted(VideoPlayer player)
    {
        NotifySourceDiscontinuity();
        // A paused seek may produce no subsequent frameReady callback. The
        // completed seek's target texture is already available for a snapshot.
        if (CurrentFrame != null && player.frame >= 0)
        {
            previousVideoFrame = player.frame;
            MarkFrame();
        }
    }

    private void OnVideoFrame(VideoPlayer player, long frameIndex)
    {
        double maximumGap = Mathf.Max(2, FPS) * 2.0;
        if (frameIndex == previousVideoFrame) return;
        if (previousVideoFrame >= 0 && (frameIndex < previousVideoFrame || frameIndex - previousVideoFrame > maximumGap))
            NotifySourceDiscontinuity();
        previousVideoFrame = frameIndex;
        MarkFrame();
    }

    private void Update()
    {
        if (webcamTexture == null || !webcamTexture.isPlaying || !webcamTexture.didUpdateThisFrame ||
            webcamTexture.width <= 16 || webcamTexture.height <= 16) return;
        if (CurrentFrame == null || CurrentFrame.width != webcamTexture.width || CurrentFrame.height != webcamTexture.height)
        {
            ReleaseFrame();
            AllocateFrame(webcamTexture.width, webcamTexture.height);
            NotifySourceDiscontinuity();
            if (VideoPanel != null) VideoPanel.texture = CurrentFrame;
        }
        Graphics.Blit(webcamTexture, CurrentFrame);
        MarkFrame();
    }

    private void MarkFrame()
    {
        FrameId++;
        FrameTimestamp = Time.realtimeSinceStartupAsDouble;
        HasFrame = true;
    }

    private void AllocateFrame(int width, int height)
    {
        CurrentFrame = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "MIRAGE camera input" };
        CurrentFrame.Create();
    }

    private void ReleaseFrame()
    {
        if (CurrentFrame == null) return;
        if (VideoPlayer != null && VideoPlayer.targetTexture == CurrentFrame) VideoPlayer.targetTexture = null;
        CurrentFrame.Release();
        Destroy(CurrentFrame);
        CurrentFrame = null;
    }

    private void DetachVideo()
    {
        if (VideoPlayer == null) return;
        VideoPlayer.frameReady -= OnVideoFrame;
        VideoPlayer.seekCompleted -= OnSeekCompleted;
    }

    private void OnDestroy()
    {
        DetachVideo();
        if (webcamTexture != null)
        {
            webcamTexture.Stop();
            Destroy(webcamTexture);
        }
        ReleaseFrame();
        if (Instance == this) Instance = null;
    }
}
