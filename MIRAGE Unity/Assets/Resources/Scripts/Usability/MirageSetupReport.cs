using System.Collections.Generic;
using UnityEngine;

namespace Mirage.Usability
{
    public sealed class MirageSetupIssue
    {
        public string Code;
        public string Message;
        public string Remedy;
        public bool Blocking;
    }

    /// <summary>Shared, read-only checks for the operator panel and release preflight.</summary>
    public static class MirageSetupReport
    {
        public static List<MirageSetupIssue> Check(bool requirePreparedModels = true)
        {
            var issues = new List<MirageSetupIssue>();
            if (!SystemInfo.supportsComputeShaders)
                Add(issues, "GPU_COMPUTE_UNAVAILABLE", "This graphics device cannot run MIRAGE's image processing.",
                    "Use a computer with compute-shader support and its graphics driver installed.", true);

            var pipeline = Object.FindAnyObjectByType<Pipeline>();
            if (pipeline == null)
            {
                Add(issues, "PIPELINE_MISSING", "No MIRAGE pipeline is configured in this scene.",
                    "Ask the maintainer to prepare the Desktop UI scene and build it as the startup scene.", true);
                return issues;
            }
            var segmentation = Object.FindAnyObjectByType<YOLOSegmentationRunner>();
            var depth = Object.FindAnyObjectByType<DepthEstimationRunner>();
            CheckModel(issues, segmentation, "Object detection", true, requirePreparedModels);
            if (depth == null)
                Add(issues, "DEPTH_RUNNER_MISSING", "The depth component is missing.",
                    "The maintainer needs to restore the depth component, even if distance filtering is off.", true);
            else CheckModel(issues, depth, "Depth estimation", depth.IsEnabled, requirePreparedModels);

            var inpainting = pipeline.ActiveInpaintingModel;
            if (inpainting != null && inpainting.IsEnabled)
                CheckModel(issues, inpainting, "Object removal", true, requirePreparedModels);
            if (inpainting is MoebiusRunner moebius)
            {
                Add(issues, "MOEBIUS_QUALITY_MODE", "Moebius quality mode is selected. Image updates can take several seconds.",
                    "Start the local Moebius service before using Remove. Choose MI-GAN in Edit mode for the live workflow.", false);
                if (moebius.IsEnabled && !string.IsNullOrEmpty(moebius.LastError))
                    Add(issues, "MOEBIUS_UNAVAILABLE", "The local Moebius service could not return a result.",
                        "Start the prepared local Moebius service and retry, or ask the maintainer to select MI-GAN for the live workflow.", true);
            }

            if (segmentation != null && depth != null &&
                (segmentation.OutputWidth != depth.OutputWidth || segmentation.OutputHeight != depth.OutputHeight))
                Add(issues, "OUTPUT_GRID_MISMATCH", "Object detection and depth use different image sizes.",
                    "The maintainer should set both output grids to the same width and height, such as 640 by 360.", true);
            if (segmentation != null && (segmentation.ImageWidth <= 0 || segmentation.ImageHeight <= 0))
                Add(issues, "INPUT_SIZE_MISSING", "The pipeline's image size has not been configured.",
                    "The maintainer should set the original image width and height before starting.", true);
            if (depth != null && depth.IsEnabled && !depth.UsesMetricDepth)
                Add(issues, "RELATIVE_DEPTH", "Depth values are relative; they are not distances in metres.",
                    "Use the full range while learning the effects. Metric ranges require a separately validated metric setup.", false);

            var input = CameraInput.Instance != null ? CameraInput.Instance : Object.FindAnyObjectByType<CameraInput>();
            if (input == null)
                Add(issues, "INPUT_COMPONENT_MISSING", "No video or camera input is configured.",
                    "The maintainer should add CameraInput to this scene.", true);
            else
            {
                if (!string.IsNullOrEmpty(input.LastInputError))
                    Add(issues, "INPUT_FAILED", input.LastInputError, "Choose another recording or refresh the camera list in Input.", true);
                else if (requirePreparedModels && !input.HasFrame)
                    Add(issues, "INPUT_WAITING", "Waiting for the first video or camera frame.",
                        "Open Input and choose a recording or a connected camera. A recording can take a moment to open.", false);
                if (input.VideoPlayer == null)
                    Add(issues, "VIDEO_PLAYER_MISSING", "This scene has no video player.",
                        "Camera input can still work. Ask the maintainer to add a video player for recordings.", false);
                if (!input.UseDebugVideoInput && input.HasFrame && input.CurrentFrame != null && segmentation != null &&
                    segmentation.ImageWidth > 0 && segmentation.ImageHeight > 0 &&
                    Mathf.Abs((float)input.CurrentFrame.width / input.CurrentFrame.height - (float)segmentation.ImageWidth / segmentation.ImageHeight) > 0.02f)
                    Add(issues, "CAMERA_ASPECT_MISMATCH", "The camera's picture proportions differ from this prepared setup.",
                        "Use the maintainer's configured camera mode, or ask them to match the pipeline image size to this camera before comparing effects.", false);
            }
            return issues;
        }

        private static void CheckModel(List<MirageSetupIssue> issues, ModelRunner runner, string label,
            bool required, bool prepared)
        {
            if (runner == null)
            {
                if (required) Add(issues, "MODEL_COMPONENT_MISSING", label + " is missing.",
                    "The maintainer should restore this model component in the Desktop UI scene.", true);
                return;
            }
            if (!required) return;
            if (!(runner is MoebiusRunner) && runner.ModelAsset == null)
                Add(issues, "MODEL_ASSET_MISSING", label + " has no installed model.",
                    "Ask the maintainer to import and assign the matching ONNX model, following Getting started.", true);
            else if (prepared && !runner.IsPrepared)
                Add(issues, "MODEL_NOT_READY", label + " could not start.",
                    "Export a support ZIP. The maintainer should check model compatibility and configured dimensions.", true);
            if (runner.InputWidth <= 0 || runner.InputHeight <= 0 || runner.OutputWidth <= 0 || runner.OutputHeight <= 0)
                Add(issues, "MODEL_SIZE_INVALID", label + " has an invalid image size.",
                    "The maintainer should configure positive input and output dimensions for this model.", true);
        }

        private static void Add(List<MirageSetupIssue> issues, string code, string message, string remedy, bool blocking)
        {
            issues.Add(new MirageSetupIssue { Code = code, Message = message, Remedy = remedy, Blocking = blocking });
        }
    }
}
