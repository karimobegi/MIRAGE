#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Mirage.Usability;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Repeatable preparation and a dedicated operator build, separate from benchmark startup scenes.</summary>
public static class MirageDesktopPreparation
{
    private const string DesktopScene = "Assets/Resources/Scenes/Pipeline/Desktop UI.unity";

    [MenuItem("MIRAGE/Prepare desktop/1. Open Desktop UI")]
    private static void OpenDesktop()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(DesktopScene);
    }

    [MenuItem("MIRAGE/Prepare desktop/2. Apply desktop defaults")]
    private static void ApplyDefaults()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var pipeline = UnityEngine.Object.FindAnyObjectByType<Pipeline>();
        if (pipeline == null) { Show("Open Desktop UI before applying defaults."); return; }
        var yolo = InScene<YOLOSegmentationRunner>(pipeline);
        var depth = InScene<DepthAnythingRunner>(pipeline);
        var migan = InScene<MIGANRunner>(pipeline);
        if (yolo == null || depth == null || migan == null)
        {
            Show("This scene needs one YOLO, Depth Anything and MI-GAN runner. Open Desktop UI and try again.");
            return;
        }
        Undo.SetCurrentGroupName("Prepare MIRAGE desktop defaults");
        Configure(yolo, 640, 640, 640, 360);
        Configure(depth, 504, 504, 640, 360);
        Configure(migan, 512, 512, 1280, 720);
        yolo.ClipPadding = true;
        depth.NormalizeInput = true;
        depth.OutputUnits = DepthValueUnits.Relative;
        depth.DepthScale = 1;
        depth.DepthOffset = 0;
        migan.MaximumCropsPerFrame = 1;
        migan.EnableTemporalReuse = false;
        foreach (var runner in UnityEngine.Object.FindObjectsByType<InpaintingRunner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (runner.gameObject.scene != pipeline.gameObject.scene) continue;
            Undo.RecordObject(runner, "Choose live inpainting backend");
            runner.enabled = runner == migan;
            runner.IsEnabled = false;
            EditorUtility.SetDirty(runner);
        }
        depth.IsEnabled = false;
        var serialized = new SerializedObject(pipeline);
        serialized.FindProperty("segmentationModel").objectReferenceValue = yolo;
        serialized.FindProperty("depthModel").objectReferenceValue = depth;
        serialized.FindProperty("inpaintingModel").objectReferenceValue = migan;
        serialized.ApplyModifiedProperties();
        Undo.RecordObject(pipeline, "Configure synchronized desktop pipeline");
        pipeline.RunAsSequence = false;
        var camera = InScene<CameraInput>(pipeline);
        if (camera != null)
        {
            Undo.RecordObject(camera, "Configure desktop input size");
            camera.RequestedWidth = 1280;
            camera.RequestedHeight = 720;
            EditorUtility.SetDirty(camera);
        }
        EditorUtility.SetDirty(pipeline);
        EditorSceneManager.MarkSceneDirty(pipeline.gameObject.scene);
        Show("Desktop dimensions and MI-GAN defaults applied. Assign the matching YOLO26, DA3-Small-504 and custom MI-GAN ONNX assets, then save the scene. These defaults do not download or validate model weights.");
    }

    private static void Configure(ModelRunner runner, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        Undo.RecordObject(runner, "Configure model dimensions");
        runner.ImageWidth = 1280;
        runner.ImageHeight = 720;
        runner.InputWidth = inputWidth;
        runner.InputHeight = inputHeight;
        runner.OutputWidth = outputWidth;
        runner.OutputHeight = outputHeight;
        runner.UpdateRate = 1;
        runner.enabled = true;
        EditorUtility.SetDirty(runner);
    }

    [MenuItem("MIRAGE/Prepare desktop/3. Check setup")]
    private static void CheckSetup()
    {
        var issues = MirageSetupReport.Check(false);
        var text = issues.Count == 0 ? "Scene checks passed. Run the GPU smoke tests and a recorded-video session before building." :
            string.Join("\n\n", issues.Select(issue => issue.Message + "\n" + issue.Remedy + " [" + issue.Code + "]"));
        Show(text);
    }

    [MenuItem("MIRAGE/Build/Windows desktop package")]
    private static void BuildDesktop()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var pipeline = UnityEngine.Object.FindAnyObjectByType<Pipeline>();
        if (pipeline == null || pipeline.gameObject.scene.path != DesktopScene)
        {
            Show("Open and prepare Desktop UI first. This command builds that scene as the operator startup scene.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (pipeline.gameObject.scene.isDirty) { Show("Save Desktop UI before building."); return; }
        var issues = MirageSetupReport.Check(false).Where(issue => issue.Blocking).ToList();
        var yolo = InScene<YOLOSegmentationRunner>(pipeline);
        var depth = InScene<DepthAnythingRunner>(pipeline);
        var migan = InScene<MIGANRunner>(pipeline);
        if (yolo == null || depth == null || migan == null || yolo.ModelAsset == null || depth.ModelAsset == null || migan.ModelAsset == null)
        {
            Show("The operator package needs the three assigned ONNX assets: YOLO26 segmentation, DA3-Small and the custom MI-GAN export. Follow Getting started, then run this command again.");
            return;
        }
        if (pipeline.ActiveInpaintingModel != migan || !migan.enabled)
        {
            Show("Select MI-GAN before creating the live operator package. Moebius remains an optional separate setup.");
            return;
        }
        if (issues.Count != 0) { Show(string.Join("\n\n", issues.Select(issue => issue.Message + "\n" + issue.Remedy))); return; }
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
        {
            Show("Install Windows Build Support for this Unity version in Unity Hub, then try again.");
            return;
        }
        string folder = EditorUtility.SaveFolderPanel("Choose a folder for the complete MIRAGE Windows package", "", "MIRAGE-Windows");
        if (string.IsNullOrEmpty(folder)) return;
        string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) { Show("Choose an output folder outside Assets."); return; }
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { DesktopScene }, locationPathName = Path.Combine(folder, "MIRAGE.exe"),
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded) { Show("The build did not succeed. Check the Unity Console and resolve its errors before sharing a package."); return; }
        string docs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Docs"));
        if (Directory.Exists(docs))
        {
            string destination = Path.Combine(folder, "Docs");
            Directory.CreateDirectory(destination);
            foreach (string name in new[] { "GETTING_STARTED.md", "OPERATOR_GUIDE.md", "RELEASE_CHECKLIST.md", "LOGGING.md" })
                if (File.Exists(Path.Combine(docs, name))) File.Copy(Path.Combine(docs, name), Path.Combine(destination, name), true);
        }
        string repository = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        foreach (string relative in new[] { "README.md", "LICENSE", "Models/Moebius/README.md", "Tests/README.md", "images/teaser_mirage.png" })
        {
            string source = Path.Combine(repository, relative);
            string destination = Path.Combine(folder, relative);
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }
        File.WriteAllText(Path.Combine(folder, "START-HERE.txt"),
            "MIRAGE\r\n\r\nKeep this entire folder together. Start MIRAGE.exe.\r\n" +
            "Open Help & setup (F1), choose a recording in Input, then load Outline people.\r\n" +
            "Press Esc to apply changes in the effects panel. Save presets for future sessions.\r\n" +
            "If something fails, use Status & support > Export support ZIP.\r\n" +
            "See Docs/OPERATOR_GUIDE.md for the step-by-step tutorial.\r\n" +
            "Maintainer and Python commands in the other guides run from a source checkout, not this player folder.\r\n");
        EditorUtility.RevealInFinder(folder);
        Show("Desktop package built. Test this entire folder on a clean target computer before distributing it. Model/license review and hardware validation remain maintainer release checks.");
    }

    private static T InScene<T>(Pipeline pipeline) where T : MonoBehaviour
    {
        return UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(item => item.gameObject.scene == pipeline.gameObject.scene);
    }

    private static void Show(string text) { EditorUtility.DisplayDialog("MIRAGE desktop setup", text, "OK"); }
}
#endif
