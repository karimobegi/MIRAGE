#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Mirage.Validation
{
    /// <summary>
    /// Executes real compute kernels against synthetic inputs; no model weights
    /// are needed. Run with a graphics device (do not use -nographics).
    /// </summary>
    public static class InpaintingGpuValidation
    {
        private static readonly List<UnityEngine.Object> objects = new List<UnityEngine.Object>();
        private static readonly List<ComputeBuffer> buffers = new List<ComputeBuffer>();

        [MenuItem("MIRAGE/Validate/Inpainting GPU smoke tests")]
        public static void Run()
        {
            if (!SystemInfo.supportsComputeShaders)
                throw new InvalidOperationException("Inpainting GPU validation requires a compute-capable graphics device.");
            try
            {
                ValidateSelectionAndMask();
                ValidateCropAndComposite();
                ValidateMotionAndRejection();
                Debug.Log("PASS: inpainting GPU selection, bounds/orientation, morphology, crop NCHW/sRGB, straight alpha, translated history, and disocclusion/scene-cut rejection.");
            }
            finally
            {
                foreach (var buffer in buffers) buffer.Release();
                buffers.Clear();
                foreach (var value in objects)
                {
                    if (value is RenderTexture texture) texture.Release();
                    UnityEngine.Object.DestroyImmediate(value);
                }
                objects.Clear();
            }
        }

        private static void ValidateSelectionAndMask()
        {
            const int width = 16, height = 8, capacity = 4;
            var validator = Shader("Models/MI-GAN/ObjectValidator");
            var maskShader = Shader("Models/MI-GAN/CreateMask");
            var processing = Shader("Models/MI-GAN/InpaintingProcessing");
            var classes = Buffer(capacity, sizeof(uint), new uint[] { 0, 2, 0, 1 });
            var depths = Buffer(capacity, sizeof(float), new[] { 10f, 100f, float.NaN, 10f });
            var settings = Buffer(1, sizeof(int) + 2 * sizeof(float), new[] { new InpaintingSettingStruct(0, 5, 20) });
            var valid = Buffer(capacity, sizeof(uint));
            int validate = validator.FindKernel("ValidateObjects");
            validator.SetInt("NumObjects", capacity);
            validator.SetInt("MaxObjects", capacity);
            validator.SetInt("NumSelectedClasses", 1);
            validator.SetBuffer(validate, "ClassIds", classes);
            validator.SetBuffer(validate, "DepthBuffer", depths);
            validator.SetBuffer(validate, "SelectedClasses", settings);
            validator.SetBuffer(validate, "ValidObjects", valid);
            validator.SetInt("UseDepthFilter", 1);
            validator.Dispatch(validate, 1, 1, 1);
            var flags = new uint[capacity];
            valid.GetData(flags);
            Require(flags[0] == 1 && flags[1] == 0 && flags[2] == 0 && flags[3] == 0, "Class/depth/NaN selection failed.");
            validator.SetInt("UseDepthFilter", 0);
            validator.Dispatch(validate, 1, 1, 1);
            valid.GetData(flags);
            Require(flags[0] == 1 && flags[2] == 1 && flags[1] == 0, "Depth-disabled class-only selection failed.");
            validator.SetInt("UseDepthFilter", 1);
            validator.Dispatch(validate, 1, 1, 1);

            var instances = new Vector2Int[width * height];
            for (int i = 0; i < instances.Length; i++) instances[i] = new Vector2Int(-1, -1);
            for (int y = 1; y <= 3; y++)
            for (int x = 2; x <= 4; x++) instances[y * width + x] = new Vector2Int(0, 0);
            for (int y = 1; y <= 3; y++)
            for (int x = 10; x <= 12; x++) instances[y * width + x] = new Vector2Int(1, 2);
            instances[0] = new Vector2Int(65535, 0); // malformed ID must not index ValidObjects
            var instanceBuffer = Buffer(instances.Length, 2 * sizeof(int), instances);
            var bounds = Buffer(capacity * 5, sizeof(uint));
            var raw = Texture(width, height, RenderTextureFormat.RFloat);
            int init = maskShader.FindKernel("InitBounds"), create = maskShader.FindKernel("CreateMask");
            maskShader.SetInt("MaskWidth", width);
            maskShader.SetInt("MaskHeight", height);
            maskShader.SetInt("MaxObjects", capacity);
            maskShader.SetInt("NumObjects", capacity);
            maskShader.SetBuffer(init, "ObjectBounds", bounds);
            maskShader.Dispatch(init, 1, 1, 1);
            maskShader.SetBuffer(create, "ObjectBounds", bounds);
            maskShader.SetBuffer(create, "ValidObjects", valid);
            maskShader.SetBuffer(create, "YOLOMaskBuffer", instanceBuffer);
            maskShader.SetTexture(create, "RawMask", raw);
            maskShader.Dispatch(create, 2, 1, 1);
            var records = new uint[capacity * 5];
            bounds.GetData(records);
            Require(records[0] == 2 && records[1] == 4 && records[2] == 4 && records[3] == 6 && records[4] == 9,
                "Per-object bounds or top-left to bottom-left conversion failed.");
            Require(records[9] == 0, "An unselected object contributed mask bounds.");
            var rawPixels = Read(raw);
            Near(rawPixels[5 * width + 3].r, 1, "Selected mask pixel");
            Near(rawPixels[2 * width + 3].r, 0, "Mask vertical orientation");
            Near(rawPixels[7 * width].r, 0, "Invalid object ID");

            var scratch = Texture(width, height, RenderTextureFormat.RFloat);
            var dilated = Texture(width, height, RenderTextureFormat.RFloat);
            var alpha = Texture(width, height, RenderTextureFormat.RFloat);
            Morphology(processing, "DilateHorizontal", raw, scratch, raw, 1);
            Morphology(processing, "DilateVertical", scratch, dilated, raw, 1);
            Morphology(processing, "FeatherHorizontal", dilated, scratch, raw, 1);
            Morphology(processing, "FeatherVertical", scratch, alpha, raw, 1);
            var pixels = Read(alpha);
            Near(pixels[5 * width + 3].r, 1, "Feathering revealed the original silhouette");
            Require(pixels[5 * width].r > 0 && pixels[5 * width].r < 1, "Expanded boundary was not feathered.");
            Near(pixels[15].r, 0, "Mask morphology leaked into unrelated pixels");
        }

        private static void ValidateCropAndComposite()
        {
            const int width = 32, height = 16, modelWidth = 4, modelHeight = 4;
            var shader = Shader("Models/MI-GAN/InpaintingProcessing");
            var frame = Image(width, height, (x, y) => new Color(x / 31f, y / 15f, 0.25f, 1));
            var alpha = Image(width, height, (x, y) => new Color(0.25f, 0, 0, 1));
            var imageTensor = Buffer(3 * modelWidth * modelHeight, sizeof(float));
            var holeTensor = Buffer(modelWidth * modelHeight, sizeof(float));
            int prepare = shader.FindKernel("PrepareCrop");
            shader.SetInt("ModelWidth", modelWidth);
            shader.SetInt("ModelHeight", modelHeight);
            shader.SetInt("InputIsLinear", 1);
            shader.SetVector("CropRect", new Vector4(0, 0, 0.5f, 1));
            shader.SetTexture(prepare, "Frame", frame);
            shader.SetTexture(prepare, "AlphaMask", alpha);
            shader.SetBuffer(prepare, "ImageTensor", imageTensor);
            shader.SetBuffer(prepare, "HoleTensor", holeTensor);
            shader.Dispatch(prepare, 1, 1, 1);
            var rgb = new float[48];
            var holes = new float[16];
            imageTensor.GetData(rgb);
            holeTensor.GetData(holes);
            Near(rgb[1], LinearToSRGB(5.5f / 31), "NCHW red crop coordinate", 0.003f);
            Near(rgb[16 + 1], LinearToSRGB(13.5f / 15), "NCHW green top-row orientation", 0.003f);
            Near(rgb[32 + 1], LinearToSRGB(0.25f), "NCHW blue/gamma", 0.003f);
            foreach (float hole in holes) Near(hole, 1, "Binary model hole mask");

            var generated = Image(modelWidth, modelHeight, (x, y) => new Color(0.5f, 0.25f, 0.75f, 1));
            var overlay = Texture(width, height, RenderTextureFormat.ARGBFloat);
            int composite = shader.FindKernel("CompositeCrop");
            shader.SetInt("Width", width);
            shader.SetInt("Height", height);
            shader.SetTexture(composite, "AlphaMask", alpha);
            shader.SetTexture(composite, "CropResult", generated);
            shader.SetTexture(composite, "Overlay", overlay);
            shader.Dispatch(composite, 4, 2, 1);
            var pixels = Read(overlay);
            Color color = pixels[8 * width + 8];
            Near(color.r, SRGBToLinear(0.5f), "Predicted fill gamma decoding");
            Near(color.g, SRGBToLinear(0.25f), "Straight-alpha RGB must not be premultiplied");
            Near(color.b, SRGBToLinear(0.75f), "Predicted fill blue channel");
            Near(color.a, 0.25f, "Composite alpha");
            Near(pixels[8 * width + 24].a, 0, "Composite escaped its crop");
        }

        private static void ValidateMotionAndRejection()
        {
            const int width = 64, height = 32, shift = 2;
            var shader = Shader("Models/MI-GAN/InpaintingProcessing");
            var random = new System.Random(107);
            var background = new Color[width * height];
            for (int i = 0; i < background.Length; i++) background[i] = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), 1);
            var previous = Image(width, height, (x, y) => background[y * width + x]);
            var current = Image(width, height, (x, y) => background[y * width + Mathf.Max(0, x - shift)]);
            var previousMask = Image(width, height, (x, y) => new Color(x >= 26 && x < 30 && y >= 14 && y < 18 ? 1 : 0, 0, 0, 1));
            var currentMask = Image(width, height, (x, y) => new Color(x >= 28 && x < 32 && y >= 14 && y < 18 ? 1 : 0, 0, 0, 1));
            var previousFill = Image(width, height, (x, y) => new Color(0.1f, 0.6f, 0.2f, x >= 26 && x < 30 && y >= 14 && y < 18 ? 1 : 0));
            var flow = Texture(8, 4, RenderTextureFormat.ARGBFloat);
            var warped = Texture(width, height, RenderTextureFormat.ARGBFloat);
            var stats = Buffer(2, sizeof(uint));
            int motion = shader.FindKernel("EstimateMotion");
            int reset = shader.FindKernel("ResetReuseStats");
            int warp = shader.FindKernel("WarpHistory");
            shader.SetInt("MotionWidth", width);
            shader.SetInt("MotionHeight", height);
            shader.SetInt("FlowWidth", flow.width);
            shader.SetInt("FlowHeight", flow.height);
            shader.SetInt("SearchRadius", 4);
            shader.SetFloat("MaxPhotometricError", 0.01f);
            shader.SetFloat("MinMotionUniqueness", 0.001f);
            shader.SetTexture(motion, "Frame", current);
            shader.SetTexture(motion, "PreviousFrame", previous);
            shader.SetTexture(motion, "AlphaMask", currentMask);
            shader.SetTexture(motion, "PreviousMask", previousMask);
            shader.SetTexture(motion, "FlowResult", flow);
            shader.SetInt("Width", width);
            shader.SetInt("Height", height);
            shader.SetTexture(warp, "AlphaMask", currentMask);
            shader.SetTexture(warp, "Flow", flow);
            shader.SetTexture(warp, "PreviousOverlay", previousFill);
            shader.SetTexture(warp, "WarpedOverlay", warped);
            shader.SetBuffer(reset, "ReuseStats", stats);
            shader.SetBuffer(warp, "ReuseStats", stats);
            shader.Dispatch(motion, 1, 1, 1);
            shader.Dispatch(reset, 1, 1, 1);
            shader.Dispatch(warp, 8, 4, 1);
            var counts = new uint[2];
            stats.GetData(counts);
            Require(counts[0] == 16 && counts[1] == 0, "Known translated background/mask was not reused.");
            var pixels = Read(warped);
            Near(pixels[15 * width + 29].g, 0.6f, "Motion-compensated fill sampling");
            Near(pixels[15 * width + 29].a, 1, "Motion-compensated alpha");

            var expandedMask = Image(width, height, (x, y) => new Color(x >= 28 && x < 33 && y >= 14 && y < 18 ? 1 : 0, 0, 0, 1));
            shader.SetTexture(motion, "AlphaMask", expandedMask);
            shader.SetTexture(warp, "AlphaMask", expandedMask);
            shader.Dispatch(motion, 1, 1, 1);
            shader.Dispatch(reset, 1, 1, 1);
            shader.Dispatch(warp, 8, 4, 1);
            stats.GetData(counts);
            Require(counts[0] == 20 && counts[1] >= 4, "Newly uncovered pixels must reject cached fill.");

            var cut = Image(width, height, (x, y) => Color.black);
            shader.SetTexture(motion, "Frame", cut);
            shader.SetTexture(motion, "AlphaMask", currentMask);
            shader.SetTexture(warp, "AlphaMask", currentMask);
            shader.Dispatch(motion, 1, 1, 1);
            shader.Dispatch(reset, 1, 1, 1);
            shader.Dispatch(warp, 8, 4, 1);
            stats.GetData(counts);
            Require(counts[0] == 16 && counts[1] == 16, "Scene-cut photometric errors must reject all cached fill.");

            // A fractional warp crosses the straight-alpha mask edge. Cached
            // colour must retain its intensity instead of blending with black.
            var edgeMask = Image(width, height, (x, y) => new Color(x == 29 && y == 15 ? 0.5f : 0, 0, 0, 1));
            var fractionalFlow = Image(8, 4, (x, y) => new Color(-3.5f / width, 0, 0, 1));
            shader.SetTexture(warp, "AlphaMask", edgeMask);
            shader.SetTexture(warp, "Flow", fractionalFlow);
            shader.Dispatch(reset, 1, 1, 1);
            shader.Dispatch(warp, 8, 4, 1);
            stats.GetData(counts);
            Require(counts[0] == 1 && counts[1] == 0, "Fractional mask-edge warp was rejected unexpectedly.");
            pixels = Read(warped);
            Near(pixels[15 * width + 29].g, 0.6f, "Fractional history edge acquired a dark fringe");
            Near(pixels[15 * width + 29].a, 0.5f, "Fractional history edge alpha");
        }

        private static void Morphology(ComputeShader shader, string name, RenderTexture source, RenderTexture result, RenderTexture original, int radius)
        {
            int kernel = shader.FindKernel(name);
            shader.SetInt("Width", source.width);
            shader.SetInt("Height", source.height);
            shader.SetInt("Radius", radius);
            shader.SetTexture(kernel, "MaskSource", source);
            shader.SetTexture(kernel, "MaskResult", result);
            if (name == "FeatherVertical") shader.SetTexture(kernel, "OriginalMask", original);
            shader.Dispatch(kernel, Mathf.CeilToInt(source.width / 8f), Mathf.CeilToInt(source.height / 8f), 1);
        }

        private static ComputeShader Shader(string path)
        {
            var shader = InpaintingMaskBuilder.LoadShader(path);
            objects.Add(shader);
            return shader;
        }

        private static ComputeBuffer Buffer(int count, int stride, Array data = null)
        {
            var buffer = new ComputeBuffer(count, stride);
            buffers.Add(buffer);
            if (data != null) buffer.SetData(data);
            return buffer;
        }

        private static RenderTexture Texture(int width, int height, RenderTextureFormat format)
        {
            var texture = InpaintingMaskBuilder.CreateTexture(width, height, format, "Inpainting GPU validation");
            objects.Add(texture);
            return texture;
        }

        private static Texture2D Image(int width, int height, Func<int, int, Color> pixel)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = pixel(x, y);
            texture.SetPixels(pixels);
            texture.Apply();
            objects.Add(texture);
            return texture;
        }

        private static Color[] Read(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var format = source.format == RenderTextureFormat.RFloat ? TextureFormat.RFloat : TextureFormat.RGBAFloat;
            var texture = new Texture2D(source.width, source.height, format, false, true);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                texture.Apply();
                return texture.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static float LinearToSRGB(float value) => value <= 0.0031308f ? 12.92f * value : 1.055f * Mathf.Pow(value, 1 / 2.4f) - 0.055f;
        private static float SRGBToLinear(float value) => value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Inpainting GPU validation: " + message);
        }
        private static void Near(float actual, float expected, string message, float tolerance = 0.001f)
        {
            Require(!float.IsNaN(actual) && Mathf.Abs(actual - expected) <= tolerance,
                message + ": expected " + expected + ", got " + actual);
        }
    }
}
#endif
