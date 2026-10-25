using System;
using System.Linq;
using UnityEngine;

namespace Mirage.Diagnostics
{
    /// <summary>Samples plain metadata on Unity's main thread, at bounded intervals.</summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class MirageDiagnosticsRuntime : MonoBehaviour
    {
        private static MirageDiagnosticsRuntime active;
        private bool ownsSession;
        private double nextSample, nextManifest;
        private double previousSample, sourceChangedAt, publicationChangedAt;
        private long previousSourceFrame = -1, previousPublishedCount;
        private bool wasInputStalled, wasProcessingStalled;
        private Pipeline pipeline;

        private void Awake()
        {
            if (active != null && active != this) { Destroy(this); return; }
            active = this;
            ownsSession = true;
            MirageDiagnostics.Initialize();
            previousSample = sourceChangedAt = publicationChangedAt = Time.realtimeSinceStartupAsDouble;
            nextSample = previousSample + 1;
            nextManifest = previousSample + 1;
        }

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextSample)
            {
                Sample(now);
                nextSample = now + 5;
            }
            if (now >= nextManifest)
            {
                CaptureManifest();
                nextManifest = now + 30;
            }
        }

        private void Sample(double now)
        {
            if (pipeline == null) pipeline = FindAnyObjectByType<Pipeline>();
            CameraInput source = CameraInput.Instance;
            long frame = source != null ? source.FrameId : -1;
            long count = pipeline != null ? pipeline.PublishedFrameCount : 0;
            if (frame != previousSourceFrame) sourceChangedAt = now;
            if (count != previousPublishedCount) publicationChangedAt = now;
            bool input = source != null && source.HasFrame;
            bool videoPaused = source != null && source.UseDebugVideoInput && source.VideoPlayer != null && !source.VideoPlayer.isPlaying;
            bool qualityMode = pipeline != null && pipeline.ActiveInpaintingModel is MoebiusRunner
                && pipeline.ActiveInpaintingModel.enabled && pipeline.ActiveInpaintingModel.IsEnabled;
            double processingTimeout = qualityMode ? Math.Max(30, ((MoebiusRunner)pipeline.ActiveInpaintingModel).RequestTimeoutSeconds + 5) : 10;
            var snapshot = new MirageHealthSnapshot
            {
                SourceType = source == null ? "none" : source.UseDebugVideoInput ? "video" : "camera",
                SourceState = source == null ? "No input configured" : !input ? "Waiting for input"
                    : videoPaused ? "Video paused or stopped" : "Receiving input",
                HasInput = input,
                SourceFrameId = frame,
                PublishedFrameId = pipeline != null ? pipeline.PublishedFrameId : -1,
                PublishedFrameCount = count,
                PublishedFramesPerSecond = previousSample < now ? Math.Max(0, count - previousPublishedCount) / (now - previousSample) : 0,
                FrameAgeSeconds = pipeline != null && pipeline.PublishedFrameId >= 0 ? Math.Max(0, now - pipeline.PublishedCaptureTime) : -1,
                LatestPublicationLatencyMilliseconds = pipeline != null && pipeline.PublishedFrameId >= 0
                    ? pipeline.LastFrameLatencyMilliseconds : -1,
                InputStalled = input && !videoPaused && now - sourceChangedAt > 10,
                ProcessingStalled = input && !videoPaused && pipeline != null && pipeline.enabled
                    && now - publicationChangedAt > processingTimeout
            };
            if (source != null && !string.IsNullOrEmpty(source.LastInputError)) snapshot.SourceState = source.LastInputError;
            if (snapshot.InputStalled != wasInputStalled)
                MirageDiagnostics.Record(snapshot.InputStalled ? "INPUT_STALLED" : "INPUT_RECOVERED",
                    snapshot.InputStalled ? "No new input image has arrived recently. Check the camera connection or video playback."
                        : "Input images are arriving again.", severity: snapshot.InputStalled ? "warning" : "info");
            if (snapshot.ProcessingStalled != wasProcessingStalled)
                MirageDiagnostics.Record(snapshot.ProcessingStalled ? "PROCESSING_STALLED" : "PROCESSING_RECOVERED",
                    snapshot.ProcessingStalled ? "Input is available, but no processed image has been published recently. Check model readiness."
                        : "Processed images are being published again.", severity: snapshot.ProcessingStalled ? "warning" : "info");
            MirageDiagnostics.SetHealth(snapshot);
            MirageDiagnostics.Record("HEALTH_SAMPLE", "Session health sample.", MirageDiagnostics.GetHealthSnapshot());
            previousSourceFrame = frame;
            previousPublishedCount = count;
            previousSample = now;
            wasInputStalled = snapshot.InputStalled;
            wasProcessingStalled = snapshot.ProcessingStalled;
        }

        private static void CaptureManifest()
        {
            var runners = FindObjectsByType<ModelRunner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            CameraInput source = CameraInput.Instance;
            var manifest = new
            {
                schemaVersion = SessionLogWriter.SchemaVersion,
                sessionId = MirageDiagnostics.SessionId,
                capturedUtc = DateTime.UtcNow.ToString("O"),
                application = new { product = Application.productName, version = Application.version, unity = Application.unityVersion, platform = Application.platform.ToString() },
                hardware = new { gpu = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(), gpuMemoryMb = SystemInfo.graphicsMemorySize,
                    cpu = SystemInfo.processorType, cpuCount = SystemInfo.processorCount, systemMemoryMb = SystemInfo.systemMemorySize },
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                input = new { type = source == null ? "none" : source.UseDebugVideoInput ? "video" : "camera",
                    width = source != null && source.CurrentFrame != null ? source.CurrentFrame.width : 0,
                    height = source != null && source.CurrentFrame != null ? source.CurrentFrame.height : 0 },
                models = runners.Select(r => new
                {
                    runner = r.GetType().Name, componentEnabled = r.enabled, effectEnabled = r.IsEnabled, prepared = r.IsPrepared,
                    backend = r is MoebiusRunner ? "Local Python HTTP" : "Unity GPUCompute",
                    model = r.ModelAsset != null ? r.ModelAsset.name : r is MoebiusRunner ? "Moebius Places2 (local service)" : "Unassigned",
                    inputWidth = r.InputWidth, inputHeight = r.InputHeight, outputWidth = r.OutputWidth, outputHeight = r.OutputHeight,
                    schedulingRate = r.UpdateRate
                }).ToArray(),
                capturePolicy = "Metadata and diagnostic messages only. No camera images, video or audio are captured, and no participant/device identifiers are added. Unity error messages can contain local paths; exported support bundles redact absolute paths.",
                retention = new { markedSessions = 10, logFilesPerSession = 3, maximumLogFileBytes = 5 * 1024 * 1024 },
                timingDefinition = "Source arrival to graphics-queue publication; not physical camera-to-display latency."
            };
            MirageDiagnostics.SetManifest(manifest);
        }

        private void OnApplicationQuit() { if (ownsSession) MirageDiagnostics.Shutdown(); }
        private void OnDestroy()
        {
            if (!ownsSession || active != this) return;
            active = null;
            MirageDiagnostics.Shutdown();
        }
    }
}
