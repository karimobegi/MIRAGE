using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Deterministic crop geometry, independent of GPU/model execution.</summary>
public static class InpaintingCropUtility
{
    /// <summary>
    /// Makes contextual object/cluster crops with the model's aspect ratio.
    /// Rectangles may extend outside the image; edge-clamped padding preserves
    /// geometry even when a square model processes a wide camera image.
    /// Nearby crops are merged to avoid seams and duplicate model evaluations.
    /// </summary>
    public static void BuildRegions(IList<Rect> maskBounds, int maskWidth, int maskHeight,
        int frameWidth, int frameHeight, float modelAspect, float contextFraction,
        float minimumContextPixels, int maskMarginPixels, int maximumCrops, List<Rect> regions)
    {
        if (regions == null) throw new ArgumentNullException(nameof(regions));
        regions.Clear();
        if (maskBounds == null || maskWidth <= 0 || maskHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) return;
        modelAspect = Mathf.Max(0.0001f, modelAspect);
        maximumCrops = Mathf.Max(1, maximumCrops);
        float scaleX = (float)frameWidth / maskWidth;
        float scaleY = (float)frameHeight / maskHeight;
        float margin = Mathf.Max(0, maskMarginPixels) * Mathf.Max(scaleX, scaleY);
        foreach (var bounds in maskBounds)
        {
            if (bounds.width <= 0 || bounds.height <= 0) continue;
            var frameBounds = new Rect(bounds.x * scaleX, bounds.y * scaleY, bounds.width * scaleX, bounds.height * scaleY);
            float context = Mathf.Max(Mathf.Max(0, minimumContextPixels),
                Mathf.Max(frameBounds.width, frameBounds.height) * Mathf.Max(0, contextFraction)) + margin;
            var padded = Rect.MinMaxRect(frameBounds.xMin - context, frameBounds.yMin - context,
                frameBounds.xMax + context, frameBounds.yMax + context);
            regions.Add(FitAspect(padded, frameWidth, frameHeight, modelAspect));
        }

        MergeOverlaps(regions, frameWidth, frameHeight, modelAspect);

        // Never silently omit selected objects when the evaluation budget is
        // reached: cluster the least costly pair until every target fits.
        while (regions.Count > maximumCrops)
        {
            int bestA = 0, bestB = 1;
            float bestCost = float.PositiveInfinity;
            for (int i = 0; i < regions.Count; i++)
            for (int j = i + 1; j < regions.Count; j++)
            {
                Rect union = FitAspect(Union(regions[i], regions[j]), frameWidth, frameHeight, modelAspect);
                float cost = union.width * union.height - regions[i].width * regions[i].height - regions[j].width * regions[j].height;
                if (cost >= bestCost) continue;
                bestCost = cost;
                bestA = i;
                bestB = j;
            }
            regions[bestA] = FitAspect(Union(regions[bestA], regions[bestB]), frameWidth, frameHeight, modelAspect);
            regions.RemoveAt(bestB);
            // The new contextual cluster can intersect a third crop even when
            // its two members did not. Merge again before choosing another pair.
            MergeOverlaps(regions, frameWidth, frameHeight, modelAspect);
        }

        for (int i = 0; i < regions.Count; i++)
        {
            Rect region = regions[i];
            regions[i] = new Rect(region.x / frameWidth, region.y / frameHeight,
                region.width / frameWidth, region.height / frameHeight);
        }
    }

    public static Rect FitAspect(Rect bounds, int width, int height, float aspect)
    {
        aspect = Mathf.Max(0.0001f, aspect);
        float cropWidth = Mathf.Max(bounds.width, bounds.height * aspect);
        float cropHeight = cropWidth / aspect;
        float x = bounds.center.x - cropWidth * 0.5f;
        float y = bounds.center.y - cropHeight * 0.5f;
        // Move padding to available image context while retaining the aspect.
        x = cropWidth <= width ? Mathf.Clamp(x, 0, width - cropWidth) : (width - cropWidth) * 0.5f;
        y = cropHeight <= height ? Mathf.Clamp(y, 0, height - cropHeight) : (height - cropHeight) * 0.5f;
        return new Rect(x, y, cropWidth, cropHeight);
    }

    private static Rect Union(Rect a, Rect b)
    {
        return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
    }

    private static void MergeOverlaps(List<Rect> regions, int width, int height, float aspect)
    {
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < regions.Count && !merged; i++)
            for (int j = i + 1; j < regions.Count; j++)
            {
                if (!regions[i].Overlaps(regions[j])) continue;
                regions[i] = FitAspect(Union(regions[i], regions[j]), width, height, aspect);
                regions.RemoveAt(j);
                merged = true;
                break;
            }
        } while (merged);
    }
}
