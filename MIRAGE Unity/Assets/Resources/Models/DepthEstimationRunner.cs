using System.Collections;
using UnityEngine;

public enum DepthValueUnits
{
    Relative = 0,
    Metres = 1
}

/// <summary>Common contract for depth models. Unavailable values are NaN.</summary>
public abstract class DepthEstimationRunner : ModelRunner
{
    public abstract ComputeBuffer ObjectDepthBuffer { get; }
    public abstract float[] DepthData { get; }
    public virtual bool UsesMetricDepth => false;
    public virtual string DepthUnitLabel => UsesMetricDepth ? "m" : "relative";

    public bool TryGetDepth(int detectionIndex, out float depth)
    {
        var values = DepthData;
        depth = float.NaN;
        if (values == null || detectionIndex < 0 || detectionIndex >= values.Length)
            return false;
        float value = values[detectionIndex];
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
            return false;
        depth = value;
        return true;
    }

    public abstract void CalculateObjectDepths(ComputeBuffer segmentationBuffer, int numObjects);
    /// <summary>Wait for compact object depths before publishing a frame's results.</summary>
    public virtual IEnumerator CalculateObjectDepthsAsync(ComputeBuffer segmentationBuffer, int numObjects)
    {
        CalculateObjectDepths(segmentationBuffer, numObjects);
        yield break;
    }
    public abstract void ResetOutput();
}
