using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

/// <summary>
/// Native Moebius through the local Python sidecar (Models/Moebius/moebius.py).
/// This quality mode is asynchronous and intentionally permits only one request.
/// Output is a full-frame straight-alpha overlay, just like the MI-GAN runner.
/// </summary>
public class MoebiusRunner : InpaintingRunner, IEffectHandler
{
    public EffectType EffectType => EffectType.Inpainting;
    public InpaintingSetting[] SelectedClasses = Array.Empty<InpaintingSetting>();

    [Tooltip("Start python moebius.py serve first. Only loopback HTTP endpoints are accepted.")]
    public string ServerUrl = "http://127.0.0.1:8765";
    [Min(1)] public int RequestTimeoutSeconds = 120;
    [Min(0)] public float RetryDelaySeconds = 5;
    public string LastError { get; private set; }
    public float LastRoundTripMilliseconds { get; private set; }
    public float LastNativePipelineMilliseconds { get; private set; }

    protected override bool UsesInferenceEngine => false;

    private InpaintingMaskBuilder maskBuilder;
    private UnityWebRequest activeRequest;
    private Texture2D uploadImage;
    private Texture2D uploadMask;
    private Texture2D receivedOverlay;
    private float nextAttempt;
    private bool disposed;
    private int historyGeneration;
    private static readonly byte[] LinearToSrgb = BuildSrgbLookup();

    [Serializable] private class RequestBody
    {
        public long frame_id;
        public string image;
        public string mask;
    }

    [Serializable] private class ResponseBody
    {
        public long frame_id;
        public string overlay;
        public string error;
        public TimingBody timings;
    }

    [Serializable] private class TimingBody
    {
        public float native_pipeline_ms;
        public float total_ms;
        public float server_total_ms;
    }

    protected override void Awake()
    {
        // The sidecar has a 512px latent input; the overlay retains camera size.
        if (InputWidth <= 0) InputWidth = 512;
        if (InputHeight <= 0) InputHeight = 512;
        if (OutputWidth <= 0) OutputWidth = ImageWidth > 0 ? ImageWidth : 1280;
        if (OutputHeight <= 0) OutputHeight = ImageHeight > 0 ? ImageHeight : 720;
        base.Awake();
    }

    protected override void PrepareModel()
    {
        maskBuilder = new InpaintingMaskBuilder(Mathf.Max(1, MaxObjects));
        EnsureOutput(OutputWidth, OutputHeight);
    }

    private bool ValidEndpoint()
    {
        return Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri uri) &&
               uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    private void EnsureOutput(int width, int height)
    {
        if (outputTexture != null && outputTexture.width == width && outputTexture.height == height) return;
        ReleaseTexture(ref outputTexture);
        outputTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            name = "Moebius Overlay", enableRandomWrite = true
        };
        outputTexture.Create();
        OutputWidth = width;
        OutputHeight = height;
        ClearOutput();
        UpdateOutputImage();
    }

    private void ClearOutput()
    {
        if (outputTexture == null) return;
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = outputTexture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
    }

    public override IEnumerator RunModel(Texture input, YOLOSegmentationRunner yolo, ComputeBuffer depthBuffer)
    {
        if (disposed || !IsEnabled || modelRunning || input == null || yolo == null) yield break;
        if (Time.realtimeSinceStartup < nextAttempt) yield break;
        modelRunning = true;
        long frameId = InputFrameId;
        try
        {
            // Snapshot the GPU mask before the network request; later YOLO passes
            // may reuse their own buffers while this runner waits on Python.
            yield return maskBuilder.Build(yolo, depthBuffer, SelectedClasses, UseDepthFilter, 0, 0);
            EnsureOutput(input.width, input.height);
            if (!maskBuilder.HasTargets)
            {
                ClearOutput();
                OutputFrameId = frameId;
                yield break;
            }
            yield return Send(input, maskBuilder.RawMask, frameId);
        }
        finally { modelRunning = false; }
    }

    public override IEnumerator RunModel(params Texture[] inputs)
    {
        if (disposed || !IsEnabled || modelRunning) yield break;
        if (inputs == null || inputs.Length < 2 || inputs[0] == null || inputs[1] == null)
            throw new ArgumentException("Moebius requires an image and a white-to-remove mask texture.");
        if (Time.realtimeSinceStartup < nextAttempt) yield break;
        modelRunning = true;
        try
        {
            EnsureOutput(inputs[0].width, inputs[0].height);
            yield return Send(inputs[0], inputs[1], InputFrameId);
        }
        finally { modelRunning = false; }
    }

    public override IEnumerator RunModel(Texture input, ComputeBuffer mask, ComputeBuffer depthBuffer)
    {
        throw new NotSupportedException("Use the YOLO overload, or RunModel(image, maskTexture), for Moebius.");
    }

    private IEnumerator Send(Texture image, Texture mask, long frameId)
    {
        if (!ValidEndpoint())
        {
            Fail("Moebius ServerUrl must be an HTTP loopback address.");
            yield break;
        }
        float start = Time.realtimeSinceStartup;
        int generation = historyGeneration;
        RenderTexture imageCopy = RenderTexture.GetTemporary(image.width, image.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        RenderTexture maskCopy = RenderTexture.GetTemporary(image.width, image.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(image, imageCopy);
            Graphics.Blit(mask, maskCopy);
            AsyncGPUReadbackRequest imageRead = AsyncGPUReadback.Request(imageCopy, 0, TextureFormat.RGBA32);
            AsyncGPUReadbackRequest maskRead = AsyncGPUReadback.Request(maskCopy, 0, TextureFormat.RGBA32);
            while (!imageRead.done || !maskRead.done) yield return null;
            if (imageRead.hasError || maskRead.hasError)
            {
                Fail("GPU readback failed for Moebius image/mask.");
                yield break;
            }
            EnsureCpuTexture(ref uploadImage, image.width, image.height);
            EnsureCpuTexture(ref uploadMask, image.width, image.height);
            uploadImage.LoadRawTextureData(imageRead.GetData<byte>());
            uploadMask.LoadRawTextureData(maskRead.GetData<byte>());
            // RFloat masks may sample as (r,0,0,1), so explicitly serialize
            // red as grayscale rather than relying on RGB luminance conversion.
            var maskPixels = uploadMask.GetRawTextureData<Color32>();
            for (int index = 0; index < maskPixels.Length; index++)
            {
                byte value = maskPixels[index].r;
                maskPixels[index] = new Color32(value, value, value, 255);
            }
            // Readback bytes from the linear render target need sRGB encoding
            // for the image model. The binary mask must remain linear.
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                var pixels = uploadImage.GetRawTextureData<Color32>();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color32 pixel = pixels[index];
                    pixels[index] = new Color32(LinearToSrgb[pixel.r], LinearToSrgb[pixel.g], LinearToSrgb[pixel.b], pixel.a);
                }
            }
            uploadImage.Apply(false);
            uploadMask.Apply(false);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(imageCopy);
            RenderTexture.ReleaseTemporary(maskCopy);
        }
        RequestBody body = new RequestBody
        {
            frame_id = frameId,
            image = Convert.ToBase64String(uploadImage.EncodeToPNG()),
            mask = Convert.ToBase64String(uploadMask.EncodeToPNG())
        };
        byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));
        using (UnityWebRequest request = new UnityWebRequest(ServerUrl.TrimEnd('/') + "/inpaint", "POST"))
        {
            activeRequest = request;
            request.uploadHandler = new UploadHandlerRaw(data);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Max(1, RequestTimeoutSeconds);
            request.SetRequestHeader("Content-Type", "application/json");
            yield return request.SendWebRequest();
            activeRequest = null;
            if (disposed || !IsEnabled || generation != historyGeneration) yield break;
            if (request.result != UnityWebRequest.Result.Success)
            {
                Fail("Moebius sidecar: " + request.error);
                yield break;
            }
            ResponseBody response = null;
            try { response = JsonUtility.FromJson<ResponseBody>(request.downloadHandler.text); }
            catch (Exception error) { LastError = error.Message; }
            if (response == null || response.frame_id != frameId || string.IsNullOrEmpty(response.overlay))
            {
                Fail(response?.error ?? "Invalid Moebius response or frame ID.");
                yield break;
            }
            bool decoded = false;
            try
            {
                // PNG RGB is sRGB; Unity converts it to the project's linear space
                // during sampling. Alpha is always linear.
                if (receivedOverlay == null) receivedOverlay = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                decoded = receivedOverlay.LoadImage(Convert.FromBase64String(response.overlay), false);
            }
            catch (Exception error) { LastError = error.Message; }
            if (!decoded || receivedOverlay.width != image.width || receivedOverlay.height != image.height)
            {
                Fail("Moebius returned an invalid overlay size.");
                yield break;
            }
            Graphics.Blit(receivedOverlay, outputTexture);
            OutputFrameId = frameId;
            LastRoundTripMilliseconds = (Time.realtimeSinceStartup - start) * 1000;
            LastNativePipelineMilliseconds = response.timings?.native_pipeline_ms ?? 0;
            LastError = null;
            UpdateOutputImage();
        }
    }

    private void Fail(string message)
    {
        LastError = message;
        nextAttempt = Time.realtimeSinceStartup + RetryDelaySeconds;
        ClearOutput();
        Debug.LogWarning(message, this);
    }

    private static void EnsureCpuTexture(ref Texture2D texture, int width, int height)
    {
        if (texture != null && texture.width == width && texture.height == height) return;
        if (texture != null) Destroy(texture);
        texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
    }

    private static byte[] BuildSrgbLookup()
    {
        var values = new byte[256];
        for (int index = 0; index < values.Length; index++)
            values[index] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(index / 255f) * 255), 0, 255);
        return values;
    }

    public void UpdateClasses(List<EffectSetting> classes)
    {
        SelectedClasses = classes?.OfType<InpaintingSetting>().ToArray() ?? Array.Empty<InpaintingSetting>();
        UpdateClasses();
    }

    public void UpdateClasses() { ResetHistory(); }
    public override void ResetHistory() { historyGeneration++; ClearOutput(); OutputFrameId = -1; }
    protected override void PeekOutput() { }
    protected override bool RequestsDone() => true;
    protected override void ReadOutput() { }
    public override void DisposeOutput() { }

    private void OnDisable()
    {
        activeRequest?.Abort();
        ResetHistory();
    }

    private static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
        texture = null;
    }

    private void OnDestroy()
    {
        disposed = true;
        activeRequest?.Abort();
        maskBuilder?.Dispose();
        ReleaseTexture(ref outputTexture);
        if (uploadImage != null) Destroy(uploadImage);
        if (uploadMask != null) Destroy(uploadMask);
        if (receivedOverlay != null) Destroy(receivedOverlay);
    }
}
