[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

# Compile the actual production crop implementation with only managed geometry
# stand-ins. This runs without Unity, ONNX weights, or a graphics device.
$taskStubs = @'
using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine {
    public struct Vector2 {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
    public struct Rect {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) {
            this.x = x; this.y = y; this.width = width; this.height = height;
        }
        public float xMin { get { return x; } }
        public float yMin { get { return y; } }
        public float xMax { get { return x + width; } }
        public float yMax { get { return y + height; } }
        public Vector2 center { get { return new Vector2(x + width / 2, y + height / 2); } }
        public static Rect MinMaxRect(float a, float b, float c, float d) { return new Rect(a, b, c - a, d - b); }
        public bool Overlaps(Rect other) {
            return other.xMax > xMin && other.xMin < xMax && other.yMax > yMin && other.yMin < yMax;
        }
    }
    public static class Mathf {
        public static float Max(float a, float b) { return Math.Max(a, b); }
        public static int Max(int a, int b) { return Math.Max(a, b); }
        public static float Min(float a, float b) { return Math.Min(a, b); }
        public static float Clamp(float value, float min, float max) { return Math.Min(max, Math.Max(min, value)); }
    }
}
public static class InpaintingGeometryChecks {
    public static string Run() {
        var random = new Random(2718);
        int cases = 0;
        foreach (int width in new[] { 64, 640, 1920 })
        foreach (int height in new[] { 64, 360, 1080 })
        foreach (float aspect in new[] { 0.5f, 1f, 1.5f, 2f }) {
            for (int trial = 0; trial < 50; trial++) {
                const int maskWidth = 640, maskHeight = 360;
                int limit = random.Next(1, 5), count = random.Next(0, 30);
                var bounds = new List<Rect>();
                var crops = new List<Rect>();
                for (int j = 0; j < count; j++) {
                    float x = (float)random.NextDouble() * (maskWidth - 1);
                    float y = (float)random.NextDouble() * (maskHeight - 1);
                    float w = Math.Max(1, (float)random.NextDouble() * (maskWidth - x));
                    float h = Math.Max(1, (float)random.NextDouble() * (maskHeight - y));
                    bounds.Add(new Rect(x, y, w, h));
                }
                InpaintingCropUtility.BuildRegions(bounds, maskWidth, maskHeight, width, height,
                    aspect, 0.35f, 48, 6, limit, crops);
                Check(bounds, crops, maskWidth, maskHeight, width, height, aspect, limit);
                cases++;
            }
        }

        // Clustering two diagonal crops can enclose/intersect other crops.
        // Those new overlaps must also merge rather than use last-writer fill.
        var corners = new List<Rect> {
            new Rect(0, 0, 1, 1), new Rect(10, 10, 1, 1),
            new Rect(0, 10, 1, 1), new Rect(10, 0, 1, 1)
        };
        var clustered = new List<Rect>();
        InpaintingCropUtility.BuildRegions(corners, 100, 100, 100, 100, 1, 0, 0, 0, 2, clustered);
        Check(corners, clustered, 100, 100, 100, 100, 1, 2);
        if (clustered.Count != 1) throw new Exception("Crop-cap clustering did not merge newly intersecting crops.");

        return "PASS " + cases + " randomized actual-source crop cases and crop-cluster regression: target coverage, aspect, crop cap, no overlaps, and empty inputs.";
    }

    private static void Check(List<Rect> bounds, List<Rect> crops, int maskWidth, int maskHeight,
        int frameWidth, int frameHeight, float aspect, int limit) {
        if (crops.Count > limit) throw new Exception("Crop cap exceeded.");
        if (bounds.Count == 0 && crops.Count != 0) throw new Exception("Empty mask generated a crop.");
        for (int i = 0; i < crops.Count; i++) {
            Rect crop = crops[i];
            float pixelAspect = crop.width * frameWidth / (crop.height * frameHeight);
            if (Math.Abs(pixelAspect - aspect) > 0.0001) throw new Exception("Aspect ratio changed.");
            for (int j = i + 1; j < crops.Count; j++)
                if (crop.Overlaps(crops[j])) throw new Exception("Contextual crops overlap after clustering.");
        }
        foreach (Rect bound in bounds) {
            float minX = bound.x / maskWidth, minY = bound.y / maskHeight;
            float maxX = bound.xMax / maskWidth, maxY = bound.yMax / maskHeight;
            bool covered = false;
            foreach (Rect crop in crops) {
                if (crop.xMin <= minX + 0.00001 && crop.yMin <= minY + 0.00001 &&
                    crop.xMax >= maxX - 0.00001 && crop.yMax >= maxY - 0.00001) covered = true;
            }
            if (!covered) throw new Exception("Selected object omitted or clipped.");
        }
    }
}
'@
$taskPath = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources/Models/MI-GAN/InpaintingCropUtility.cs'
$taskSource = Get-Content -LiteralPath $taskPath -Raw
$taskSource = [regex]::Replace($taskSource, '(?m)^using .+;\r?\n', '')
# Isolate stand-ins from the runtime harness so both scripts can run in the
# same PowerShell session without defining UnityEngine types twice.
$taskCompilation = "namespace Mirage.InpaintingGeometryValidation {`n" + $taskStubs + [Environment]::NewLine + $taskSource + "`n}"
Add-Type -TypeDefinition $taskCompilation -Language CSharp
[Mirage.InpaintingGeometryValidation.InpaintingGeometryChecks]::Run()
