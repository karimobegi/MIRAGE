using System;
using System.IO;
using Mirage.Diagnostics;
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
    public string LastInputError { get; private set; }
    public string SelectedCameraDeviceName => selectedCameraDeviceName;
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
            LastInputError = "No video player is configured. Ask the person preparing this project to add one.";
            Debug.LogError("Assign a VideoPlayer before starting video input.", this);
            return;
        }
        ReleaseFrame();
        UseDebugVideoInput = true;
        NotifySourceDiscontinuity();
        AllocateFrame(Mathf.Max(1, RequestedWidth), Mathf.Max(1, RequestedHeight));
        VideoPlayer.targetTexture = CurrentFrame;
        VideoPlayer.aspectRatio = VideoAspectRatio.FitInside;
        VideoPlayer.sendFrameReadyEvents = true;
        VideoPlayer.frameReady += OnVideoFrame;
        VideoPlayer.seekCompleted += OnSeekCompleted;
        VideoPlayer.errorReceived += OnVideoError;
        VideoPlayer.prepareCompleted += OnVideoPrepared;
        LastInputError = null;
        if (VideoPanel != null) VideoPanel.texture = CurrentFrame;
        VideoPlayer.Play();
        MirageDiagnostics.Record("input.video.started", "Video input started.");
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
        LastInputError = null;
        MirageDiagnostics.Record("input.camera.started", "Camera input started.");
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

    /// <summary>Select a local recording without changing the Unity scene.</summary>
    public bool TryStartVideoFile(string path, out string error)
    {
        error = null;
        bool sourceChanged = false;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new ArgumentException("Choose an existing video file.");
            if (VideoPlayer == null)
                throw new InvalidOperationException("This scene has no video player. Ask the project maintainer to configure it.");
            string fullPath = Path.GetFullPath(path);
            VideoPlayer.Stop();
            sourceChanged = true;
            VideoPlayer.source = VideoSource.Url;
            VideoPlayer.url = new Uri(fullPath).AbsoluteUri;
            StartVideo();
            MirageDiagnostics.Record("input.video.selected", "A recording was selected.", new { file = Path.GetFileName(fullPath) });
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException ||
            exception is UnauthorizedAccessException || exception is InvalidOperationException || exception is NotSupportedException)
        {
            error = exception.Message;
            if (sourceChanged) LastInputError = error;
            MirageDiagnostics.Record("input.video.failed", "The recording could not be opened.", new { reason = exception.Message }, "error");
            return false;
        }
    }

    public bool TryStartCamera(string deviceName, out string error)
    {
        error = null;
        var devices = WebCamTexture.devices;
        bool available = false;
        foreach (var device in devices)
            if (device.name == deviceName) available = true;
        if (!available)
        {
            error = "The selected camera is unavailable. Refresh the list and check its connection.";
            return false;
        }
        try
        {
            StopCamera();
            selectedCameraDeviceName = deviceName;
            StartCamera();
            return true;
        }
        catch (Exception exception)
        {
            StopCamera();
            error = LastInputError = "The camera could not start. Check its connection and close other apps using it.";
            MirageDiagnostics.Record("input.camera.failed", error, new { reason = exception.Message }, "error");
            return false;
        }
    }

    public void StopInput()
    {
        StopCamera();
        if (VideoPlayer != null) VideoPlayer.Stop();
        NotifySourceDiscontinuity();
        LastInputError = null;
        MirageDiagnostics.Record("input.stopped", "Input was stopped.");
    }

    public void PauseOrResumeVideo()
    {
        if (!UseDebugVideoInput || VideoPlayer == null) return;
        if (VideoPlayer.isPlaying) VideoPlayer.Pause();
        else VideoPlayer.Play();
        MirageDiagnostics.Record("input.video.playback", VideoPlayer.isPlaying ? "Video resumed." : "Video paused.");
    }

    public void RestartVideo()
    {
        if (!UseDebugVideoInput || VideoPlayer == null) return;
        NotifySourceDiscontinuity();
        VideoPlayer.time = 0;
        VideoPlayer.Play();
    }

    private void OnVideoPrepared(VideoPlayer player) { LastInputError = null; }

    private void OnVideoError(VideoPlayer player, string message)
    {
        LastInputError = "The recording could not be decoded. Try an H.264 MP4 file or select another recording.";
        NotifySourceDiscontinuity();
        MirageDiagnostics.Record("input.video.decode_failed", LastInputError, new { decoderMessage = message }, "error");
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
        VideoPlayer.errorReceived -= OnVideoError;
        VideoPlayer.prepareCompleted -= OnVideoPrepared;
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
