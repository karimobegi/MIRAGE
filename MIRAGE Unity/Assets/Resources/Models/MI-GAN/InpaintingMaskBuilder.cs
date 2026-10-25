using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Shared class/depth selection and mask preparation for inpainting backends.
/// Only compact per-object bounds are read back; segmentation pixels stay on GPU.
/// The textures and PixelBounds use bottom-left coordinates.
/// </summary>
public sealed class InpaintingMaskBuilder : IDisposable
{
    public RenderTexture RawMask { get; private set; }
    public RenderTexture AlphaMask { get; private set; }
    public int MaskWidth { get; private set; }
    public int MaskHeight { get; private set; }
    public bool HasTargets => PixelBounds.Count != 0;
    public List<Rect> PixelBounds { get; } = new List<Rect>();

    private int capacity;
    private readonly ComputeShader validator;
    private readonly ComputeShader maskShader;
    private readonly ComputeShader processing;
    private ComputeBuffer validObjects;
    private ComputeBuffer boundsBuffer;
    private ComputeBuffer dummyDepth;
    private ComputeBuffer settingsBuffer;
    private InpaintingSettingStruct[] settingsData;
    private uint[] boundsData;
    private RenderTexture scratch;
    private RenderTexture dilated;
    private readonly int validateKernel;
    private readonly int initBoundsKernel;
    private readonly int maskKernel;
    private bool disposed;

    public InpaintingMaskBuilder(int maxObjects)
    {
        capacity = Mathf.Clamp(maxObjects, 1, 4096);
        validator = LoadShader("Models/MI-GAN/ObjectValidator");
        maskShader = LoadShader("Models/MI-GAN/CreateMask");
        processing = LoadShader("Models/MI-GAN/InpaintingProcessing");
        validateKernel = validator.FindKernel("ValidateObjects");
        initBoundsKernel = maskShader.FindKernel("InitBounds");
        maskKernel = maskShader.FindKernel("CreateMask");
        AllocateBuffers(capacity);
    }

    public IEnumerator Build(YOLOSegmentationRunner yolo, ComputeBuffer depth,
        InpaintingSetting[] settings, bool useDepthFilter, int dilationPixels, int featherPixels)
    {
        if (disposed) throw new ObjectDisposedException(nameof(InpaintingMaskBuilder));
        if (yolo == null || yolo.OutputWidth <= 0 || yolo.OutputHeight <= 0)
            throw new ArgumentException("A prepared segmentation runner is required.", nameof(yolo));
        EnsureTextures(yolo.OutputWidth, yolo.OutputHeight);
        PixelBounds.Clear();
        int count = Mathf.Max(0, yolo.NumObjDetected);
        if (count > 4096) throw new InvalidOperationException("Inpainting supports at most 4096 detected objects per frame.");
        // Other runners can use a larger detection capacity. Grow compact
        // metadata buffers instead of silently omitting their selected objects.
        if (count > capacity) AllocateBuffers(Mathf.Min(4096, Mathf.Max(count, capacity * 2)));
        if (count == 0 || settings == null || settings.Length == 0 || yolo.OutputBuffer == null ||
            yolo.ClassIdsBuffer == null || (useDepthFilter && (depth == null || depth.count < count)))
        {
            ClearTexture(RawMask);
            ClearTexture(AlphaMask);
            yield break;
        }

        UpdateSettings(settings);
        validator.SetInt("NumObjects", count);
        validator.SetInt("MaxObjects", capacity);
        validator.SetInt("UseDepthFilter", useDepthFilter ? 1 : 0);
        validator.SetBuffer(validateKernel, "ClassIds", yolo.ClassIdsBuffer);
        validator.SetBuffer(validateKernel, "DepthBuffer", useDepthFilter ? depth : dummyDepth);
        validator.SetBuffer(validateKernel, "ValidObjects", validObjects);
        validator.Dispatch(validateKernel, Mathf.CeilToInt(capacity / 64f), 1, 1);

        maskShader.SetInt("MaskWidth", MaskWidth);
        maskShader.SetInt("MaskHeight", MaskHeight);
        maskShader.SetInt("NumObjects", count);
        maskShader.SetInt("MaxObjects", capacity);
        maskShader.SetBuffer(initBoundsKernel, "ObjectBounds", boundsBuffer);
        maskShader.Dispatch(initBoundsKernel, Mathf.CeilToInt(capacity / 64f), 1, 1);
        maskShader.SetBuffer(maskKernel, "ValidObjects", validObjects);
        maskShader.SetBuffer(maskKernel, "YOLOMaskBuffer", yolo.OutputBuffer);
        maskShader.SetBuffer(maskKernel, "ObjectBounds", boundsBuffer);
        maskShader.SetTexture(maskKernel, "RawMask", RawMask);
        maskShader.Dispatch(maskKernel, Mathf.CeilToInt(MaskWidth / 8f), Mathf.CeilToInt(MaskHeight / 8f), 1);

        // Snapshot the selected masks before yielding. Subsequent model updates
        // cannot change this run's mask or object bounds.
        ProcessMask("DilateHorizontal", RawMask, scratch, Mathf.Clamp(dilationPixels, 0, 16));
        ProcessMask("DilateVertical", scratch, dilated, Mathf.Clamp(dilationPixels, 0, 16));
        ProcessMask("FeatherHorizontal", dilated, scratch, Mathf.Clamp(featherPixels, 0, 16));
        ProcessMask("FeatherVertical", scratch, AlphaMask, Mathf.Clamp(featherPixels, 0, 16));

        if (SystemInfo.supportsAsyncGPUReadback)
        {
            var request = AsyncGPUReadback.Request(boundsBuffer, count * 5 * sizeof(uint), 0);
            while (!request.done) yield return null;
            if (request.hasError)
            {
                ClearTexture(AlphaMask);
                Debug.LogWarning("Inpainting mask bounds readback failed; skipping this frame.");
                yield break;
            }
            var data = request.GetData<uint>();
            for (int i = 0; i < data.Length; i++) boundsData[i] = data[i];
        }
        else
        {
            // Compatibility fallback reads only five integers per object.
            boundsBuffer.GetData(boundsData, 0, 0, count * 5);
        }

        for (int i = 0; i < count; i++)
        {
            int offset = i * 5;
            if (boundsData[offset + 4] == 0) continue;
            PixelBounds.Add(Rect.MinMaxRect(boundsData[offset], boundsData[offset + 1],
                boundsData[offset + 2] + 1, boundsData[offset + 3] + 1));
        }
    }

    private void UpdateSettings(InpaintingSetting[] settings)
    {
        bool changed = settingsData == null || settingsData.Length != settings.Length;
        if (changed)
        {
            settingsData = new InpaintingSettingStruct[settings.Length];
            settingsBuffer?.Release();
            settingsBuffer = new ComputeBuffer(settings.Length, sizeof(int) + sizeof(float) * 2);
        }
        for (int i = 0; i < settings.Length; i++)
        {
            var setting = settings[i] == null ? new InpaintingSettingStruct(-1, 0, 0) : settings[i].ToStruct();
            if (settingsData[i].ClassID != setting.ClassID ||
                !settingsData[i].MinRange.Equals(setting.MinRange) || !settingsData[i].MaxRange.Equals(setting.MaxRange))
                changed = true;
            settingsData[i] = setting;
        }
        if (changed) settingsBuffer.SetData(settingsData);
        validator.SetBuffer(validateKernel, "SelectedClasses", settingsBuffer);
        validator.SetInt("NumSelectedClasses", settings.Length);
    }

    private void AllocateBuffers(int count)
    {
        validObjects?.Release();
        boundsBuffer?.Release();
        dummyDepth?.Release();
        capacity = count;
        validObjects = new ComputeBuffer(capacity, sizeof(uint));
        boundsBuffer = new ComputeBuffer(capacity * 5, sizeof(uint));
        dummyDepth = new ComputeBuffer(capacity, sizeof(float));
        dummyDepth.SetData(new float[capacity]);
        boundsData = new uint[capacity * 5];
    }

    private void EnsureTextures(int width, int height)
    {
        if (RawMask != null && width == MaskWidth && height == MaskHeight) return;
        ReleaseTextures();
        MaskWidth = width;
        MaskHeight = height;
        RawMask = CreateTexture(width, height, RenderTextureFormat.RFloat, "Inpainting selected mask");
        AlphaMask = CreateTexture(width, height, RenderTextureFormat.RFloat, "Inpainting feathered alpha");
        scratch = CreateTexture(width, height, RenderTextureFormat.RFloat, "Inpainting mask scratch");
        dilated = CreateTexture(width, height, RenderTextureFormat.RFloat, "Inpainting dilated mask");
    }

    private void ProcessMask(string name, RenderTexture source, RenderTexture destination, int radius)
    {
        int kernel = processing.FindKernel(name);
        processing.SetInt("Width", MaskWidth);
        processing.SetInt("Height", MaskHeight);
        processing.SetInt("Radius", radius);
        processing.SetTexture(kernel, "MaskSource", source);
        processing.SetTexture(kernel, "MaskResult", destination);
        if (name == "FeatherVertical") processing.SetTexture(kernel, "OriginalMask", RawMask);
        processing.Dispatch(kernel, Mathf.CeilToInt(MaskWidth / 8f), Mathf.CeilToInt(MaskHeight / 8f), 1);
    }

    public static ComputeShader LoadShader(string path)
    {
        var asset = Resources.Load<ComputeShader>(path);
        if (asset == null) throw new InvalidOperationException("Missing inpainting compute shader: " + path);
        // Resources.Load returns a shared asset. Each runner needs private bindings.
        return UnityEngine.Object.Instantiate(asset);
    }

    public static RenderTexture CreateTexture(int width, int height, RenderTextureFormat format, string name)
    {
        var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
        {
            name = name,
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.Create();
        ClearTexture(texture);
        return texture;
    }

    public static void ClearTexture(RenderTexture texture)
    {
        if (texture == null) return;
        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
    }

    public static void ReleaseTexture(RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        DestroyObject(texture);
    }

    private static void DestroyObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(value);
        else UnityEngine.Object.DestroyImmediate(value);
    }

    private void ReleaseTextures()
    {
        ReleaseTexture(RawMask);
        ReleaseTexture(AlphaMask);
        ReleaseTexture(scratch);
        ReleaseTexture(dilated);
        RawMask = AlphaMask = scratch = dilated = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        validObjects.Release();
        boundsBuffer.Release();
        dummyDepth.Release();
        settingsBuffer?.Release();
        ReleaseTextures();
        DestroyObject(validator);
        DestroyObject(maskShader);
        DestroyObject(processing);
    }
}
