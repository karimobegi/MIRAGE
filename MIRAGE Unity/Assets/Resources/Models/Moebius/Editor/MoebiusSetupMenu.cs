#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Undoable backend selection for the selected scene Pipeline.</summary>
public static class MoebiusSetupMenu
{
    private const string MoebiusMenu = "MIRAGE/Inpainting/Use Moebius on selected Pipeline";
    private const string MiganMenu = "MIRAGE/Inpainting/Use MI-GAN on selected Pipeline";

    private static Pipeline SelectedPipeline()
    {
        return Selection.activeGameObject == null ? null : Selection.activeGameObject.GetComponentInParent<Pipeline>();
    }

    [MenuItem(MoebiusMenu, true)]
    [MenuItem(MiganMenu, true)]
    private static bool CanSwitch() => !EditorApplication.isPlayingOrWillChangePlaymode && SelectedPipeline() != null;

    [MenuItem(MoebiusMenu)]
    private static void UseMoebius()
    {
        Pipeline pipeline = SelectedPipeline();
        MIGANRunner migan = FindSceneRunner<MIGANRunner>(pipeline);
        if (migan == null) return;
        MoebiusRunner moebius = FindSceneRunner<MoebiusRunner>(pipeline, false);
        if (moebius == null) moebius = Undo.AddComponent<MoebiusRunner>(migan.gameObject);
        Configure(pipeline, pipeline.ActiveInpaintingModel ?? migan, moebius);
        Debug.Log("Moebius selected. Start Models/Moebius/moebius.py serve before Play.", moebius);
    }

    [MenuItem(MiganMenu)]
    private static void UseMigan()
    {
        Pipeline pipeline = SelectedPipeline();
        MIGANRunner migan = FindSceneRunner<MIGANRunner>(pipeline);
        if (migan == null) return;
        Configure(pipeline, pipeline.ActiveInpaintingModel ?? migan, migan);
        Debug.Log("MI-GAN selected.", migan);
    }

    private static T FindSceneRunner<T>(Pipeline pipeline, bool required = true) where T : InpaintingRunner
    {
        if (pipeline == null) return null;
        if (pipeline.ActiveInpaintingModel is T active) return active;
        T[] candidates = UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(runner => runner.gameObject.scene == pipeline.gameObject.scene).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1 || required)
            Debug.LogError("Select a Pipeline with an assigned backend and exactly one " + typeof(T).Name + " in its scene.", pipeline);
        return null;
    }

    private static void Configure(Pipeline pipeline, InpaintingRunner from, InpaintingRunner target)
    {
        Undo.SetCurrentGroupName("Switch MIRAGE inpainting backend");
        Undo.RecordObject(target, "Configure inpainting backend");
        target.ImageWidth = from.ImageWidth;
        target.ImageHeight = from.ImageHeight;
        target.InputWidth = from.InputWidth;
        target.InputHeight = from.InputHeight;
        target.OutputWidth = from.OutputWidth;
        target.OutputHeight = from.OutputHeight;
        target.MaxObjects = from.MaxObjects;
        target.OutputImage = from.OutputImage;
        InpaintingSetting[] sourceClasses = from is MIGANRunner migan ? migan.SelectedClasses :
            from is MoebiusRunner moebius ? moebius.SelectedClasses : Array.Empty<InpaintingSetting>();
        InpaintingSetting[] classes = (sourceClasses ?? Array.Empty<InpaintingSetting>())
            .Where(setting => setting != null)
            .Select(setting => new InpaintingSetting(setting.ClassID, setting.MinRange, setting.MaxRange)).ToArray();
        if (target is MIGANRunner targetMigan) targetMigan.SelectedClasses = classes;
        if (target is MoebiusRunner targetMoebius) targetMoebius.SelectedClasses = classes;

        foreach (InpaintingRunner runner in UnityEngine.Object.FindObjectsByType<InpaintingRunner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (runner.gameObject.scene != pipeline.gameObject.scene) continue;
            Undo.RecordObject(runner, "Select inpainting backend");
            runner.enabled = runner == target;
            runner.IsEnabled = runner == target;
            EditorUtility.SetDirty(runner);
        }
        SerializedObject serialized = new SerializedObject(pipeline);
        serialized.FindProperty("inpaintingModel").objectReferenceValue = target;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        EditorSceneManager.MarkSceneDirty(pipeline.gameObject.scene);
        Selection.activeGameObject = pipeline.gameObject;
        EditorGUIUtility.PingObject(target);
    }
}
#endif
