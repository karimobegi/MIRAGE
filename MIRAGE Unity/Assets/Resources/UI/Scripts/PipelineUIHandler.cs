using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Apply effect changes and save/load portable, named operator presets.</summary>
public class PipelineUIHandler : MonoBehaviour
{
    public static PipelineUIHandler Instance;
    public List<ClassSetting> ClassSettings;
    public List<Toggle> LayerSettings;
    public Transform CheckBoxContainer;
    public GameObject CheckBoxPrefab;
    public Transform ClassContainer;
    public GameObject ClassPanelPrefab;
    public List<IEffectHandler> effectHandlers = new List<IEffectHandler>();
    public Toggle depth;
    public Toggle inpainting;
    public bool RestoreLastSession = true;

    public bool IsReady { get; private set; }
    public string LastPresetError { get; private set; }
    public string ActivePresetName { get; private set; }
    public string PresetDirectory => Path.Combine(Application.persistentDataPath, "MIRAGE", "Presets");

    private readonly List<ClassPanel> classPanels = new List<ClassPanel>();
    private readonly List<ToggleClass> classToggles = new List<ToggleClass>();
    private readonly Dictionary<EffectType, List<EffectSetting>> effectSettings = new Dictionary<EffectType, List<EffectSetting>>();
    private readonly Dictionary<string, MiragePreset> starterPresets = new Dictionary<string, MiragePreset>(StringComparer.OrdinalIgnoreCase);
    private MiragePresetStore store;
    private DepthEstimationRunner depthRunner;
    private InpaintingRunner inpaintingRunner;
    private YOLOSegmentationRunner segmentationRunner;
    private EffectSelectorPanel effectSelector;

    private void Awake()
    {
        Instance = this;
        store = new MiragePresetStore(PresetDirectory);
        if (ClassSettings == null) ClassSettings = new List<ClassSetting>();
        if (LayerSettings == null) LayerSettings = new List<Toggle>();
        foreach (EffectType type in Enum.GetValues(typeof(EffectType)))
            effectSettings[type] = new List<EffectSetting>();
        if (CheckBoxPrefab == null || CheckBoxContainer == null) return;
        foreach (var setting in ClassSettings)
        {
            var checkbox = Instantiate(CheckBoxPrefab, CheckBoxContainer).GetComponent<ToggleClass>();
            if (checkbox == null) continue;
            if (checkbox.text != null) checkbox.text.text = setting.ClassName;
            checkbox.ClassSetting = setting;
            classToggles.Add(checkbox);
        }
    }

    private IEnumerator Start()
    {
        // A lifecycle barrier, not a timing guess: all scene Start methods run
        // before restoring settings into prepared processors and slider controls.
        yield return null;
        DiscoverControls();
        IsReady = true;
        SeedStarterPresets();
        if (RestoreLastSession && store.HasLastSession)
        {
            if (store.TryLoadLast(out MiragePreset last, out string error))
                TryApplyPreset(last, false);
            else SetError(error);
        }
    }

    private void DiscoverControls()
    {
        effectHandlers = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(behaviour => behaviour.gameObject.scene == gameObject.scene && behaviour.gameObject.activeInHierarchy)
            .OfType<IEffectHandler>().ToList();
        segmentationRunner = FindAnyObjectByType<YOLOSegmentationRunner>();
        depthRunner = FindAnyObjectByType<DepthEstimationRunner>();
        var pipeline = FindAnyObjectByType<Pipeline>();
        inpaintingRunner = pipeline != null ? pipeline.ActiveInpaintingModel :
            FindObjectsByType<InpaintingRunner>(FindObjectsSortMode.None).FirstOrDefault(runner => runner.enabled);
        effectSelector = FindObjectsByType<EffectSelectorPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(selector => selector.gameObject.scene == gameObject.scene);
        if (depth == null)
        {
            var toggle = FindObjectsByType<DepthToggle>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (toggle != null) depth = toggle.GetComponent<Toggle>();
        }
        if (inpainting == null)
        {
            var toggle = FindObjectsByType<InpaintingToggle>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (toggle != null) inpainting = toggle.GetComponent<Toggle>();
        }
    }

    public void ToggleClass(bool value, ClassSetting setting)
    {
        if (setting.ClassID == null) return;
        string key = MiragePresetStore.ClassKey(setting.ClassID);
        var existing = classPanels.FirstOrDefault(panel => MiragePresetStore.ClassKey(panel.ClassSetting.ClassID) == key);
        if (value)
        {
            if (existing != null || ClassPanelPrefab == null || ClassContainer == null) return;
            var panel = Instantiate(ClassPanelPrefab, ClassContainer).GetComponent<ClassPanel>();
            if (panel == null) return;
            panel.Initialize(setting);
            classPanels.Add(panel);
        }
        else if (existing != null)
        {
            classPanels.Remove(existing);
            existing.gameObject.SetActive(false);
            Destroy(existing.gameObject);
        }
    }

    public void ToggleUI(bool value)
    {
        if (!value) OnSave();
        else gameObject.SetActive(true);
        SyncVisibilityState();
    }

    /// <summary>Legacy scene event: Apply changes, remember them, and close settings.</summary>
    public void OnSave() { ApplyChanges(true); }

    public bool ApplyChanges(bool closeSettings = false)
    {
        if (!Ready()) return false;
        MiragePreset preset = Capture("Last session");
        if (!ValidateForScene(preset, out string error)) return SetError(error);
        if (!ApplyData(preset)) return false;
        if (!store.TrySaveLast(preset, out error))
            return SetError("Changes were applied, but could not be remembered: " + error);
        ActivePresetName = null;
        LastPresetError = null;
        if (closeSettings) gameObject.SetActive(false);
        SyncVisibilityState();
        return true;
    }

    public bool SavePreset(string name)
    {
        if (!Ready()) return false;
        if (!MiragePresetStore.TryNormalizeName(name, out string valid, out string error)) return SetError(error);
        MiragePreset preset = Capture(valid);
        if (!ValidateForScene(preset, out error)) return SetError(error);
        if (!ApplyData(preset)) return false;
        if (!store.TrySave(valid, preset, out error)) return SetError("Changes were applied. " + error);
        ActivePresetName = valid;
        LastPresetError = null;
        if (!store.TrySaveLast(preset, out error))
            Debug.LogWarning("[MIRAGE PRESET_LAST_SESSION] Named preset was saved; the last-session copy could not be updated. " + error, this);
        Debug.Log("[MIRAGE PRESET_SAVED] Preset saved: " + valid, this);
        return true;
    }

    public bool SavePreset(string name, out string error)
    {
        bool result = SavePreset(name);
        error = LastPresetError;
        return result;
    }

    public bool LoadPreset(string name)
    {
        if (!Ready()) return false;
        MiragePreset preset;
        if (!store.Exists(name) && MiragePresetStore.TryNormalizeName(name, out string normalized, out _) && starterPresets.TryGetValue(normalized, out MiragePreset builtIn))
            preset = builtIn;
        else if (!store.TryLoad(name, out preset, out string error)) return SetError(error);
        if (!TryApplyPreset(preset, true)) return false;
        Debug.Log("[MIRAGE PRESET_LOADED] Preset loaded: " + preset.name, this);
        return true;
    }

    public bool LoadPreset(string name, out string error)
    {
        bool result = LoadPreset(name);
        error = LastPresetError;
        return result;
    }

    public string[] GetPresetNames()
    {
        if (store == null) return Array.Empty<string>();
        try { return store.GetNames().Concat(starterPresets.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException)
        {
            SetError("The preset folder could not be listed. Check access to your user profile.");
            return starterPresets.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public List<EffectSetting> GetEffectSettings(EffectType effectType)
    {
        return effectSettings.TryGetValue(effectType, out List<EffectSetting> values) ? values : new List<EffectSetting>();
    }

    private bool Ready()
    {
        return IsReady || SetError("Settings are still initializing. Try again when the status panel is ready.");
    }

    private bool SetError(string error)
    {
        bool changed = LastPresetError != error;
        LastPresetError = error;
        if (changed) Debug.LogWarning("[MIRAGE PRESET] " + error, this);
        return false;
    }

    private void SyncVisibilityState()
    {
        foreach (var control in FindObjectsByType<UIToggleHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            for (int i = 0; i < control.OnToggle.GetPersistentEventCount(); i++)
                if (control.OnToggle.GetPersistentTarget(i) == this) control.value = gameObject.activeSelf;
    }

    private MiragePreset Capture(string name)
    {
        var preset = EmptyPreset(name);
        foreach (var panel in classPanels.Where(panel => panel != null))
        {
            var category = new MiragePresetClass { classIds = (int[])panel.ClassSetting.ClassID.Clone(), effects = new List<MiragePresetEffect>() };
            foreach (var effect in panel.effectPanels.Where(effect => effect != null))
                category.effects.Add(new MiragePresetEffect
                {
                    effect = effect.effectType.ToString(),
                    minimum = effect.MinValue,
                    maximum = effect.MaxValue,
                    rgba = new[] { effect.color.r, effect.color.g, effect.color.b, effect.color.a }
                });
            preset.classes.Add(category);
        }
        foreach (var effect in preset.classes.SelectMany(category => category.effects))
        {
            if (effect.effect == nameof(EffectType.Inpainting) || effect.effect == nameof(EffectType.Opacity))
                preset.inpaintingEnabled = true;
            if (effect.minimum > 0 || effect.maximum < 100 || effect.effect == nameof(EffectType.Info))
                preset.depthEnabled = true;
        }
        return preset;
    }

    private MiragePreset EmptyPreset(string name)
    {
        var preset = new MiragePreset
        {
            schemaVersion = MiragePreset.CurrentVersion, name = name,
            depthUnits = depthRunner != null && depthRunner.UsesMetricDepth ? "metres" : "relative",
            depthModel = CurrentDepthModel(),
            classes = new List<MiragePresetClass>(),
            layers = LayerSettings.Where(layer => layer != null)
                .Select(layer => new MiragePresetLayer { name = layer.gameObject.name, enabled = layer.isOn }).ToList(),
            runners = new List<MiragePresetRunner>()
        };
        AddRunner(preset, "segmentation", segmentationRunner);
        AddRunner(preset, "depth", depthRunner);
        AddRunner(preset, "inpainting", inpaintingRunner);
        return preset;
    }

    private static void AddRunner(MiragePreset preset, string role, ModelRunner runner)
    {
        if (runner != null) preset.runners.Add(new MiragePresetRunner { role = role, updateRate = runner.UpdateRate });
    }

    private bool ValidateForScene(MiragePreset preset, out string error)
    {
        if (!MiragePresetStore.Validate(preset, out error)) return false;
        string units = depthRunner != null && depthRunner.UsesMetricDepth ? "metres" : "relative";
        if (preset.depthEnabled && preset.depthUnits != units)
        {
            error = "This preset's depth ranges use different units from this setup. Ask the maintainer to match the depth model. The current settings were kept.";
            return false;
        }
        if (preset.depthEnabled && units == "relative" && !string.IsNullOrEmpty(preset.depthModel) &&
            !string.IsNullOrEmpty(CurrentDepthModel()) && preset.depthModel != CurrentDepthModel())
        {
            error = "This preset's relative-depth ranges were saved for a different depth model. The current settings were kept.";
            return false;
        }
        var categories = new HashSet<string>(ClassSettings.Where(category => category.ClassID != null)
            .Select(category => MiragePresetStore.ClassKey(category.ClassID)), StringComparer.Ordinal);
        if (preset.classes.Any(category => !categories.Contains(MiragePresetStore.ClassKey(category.classIds))))
        {
            error = "This preset uses an object category that is unavailable in this scene. The current settings were kept.";
            return false;
        }
        var layers = new HashSet<string>(LayerSettings.Where(layer => layer != null).Select(layer => layer.gameObject.name), StringComparer.Ordinal);
        if (preset.layers.Any(layer => !layers.Contains(layer.name)))
        {
            error = "This preset uses display layers that are unavailable in this scene. The current settings were kept.";
            return false;
        }
        foreach (var effect in preset.classes.SelectMany(category => category.effects))
        {
            if (!Enum.TryParse(effect.effect, out EffectType type) || !Enum.IsDefined(typeof(EffectType), type))
            {
                error = "This preset uses an unsupported effect. The current settings were kept.";
                return false;
            }
            if (ClassPanelPrefab == null || ClassContainer == null || EffectPrefab(type) == null)
            {
                error = "The scene is missing an effect panel required by this preset. Ask the maintainer to check the UI prefabs.";
                return false;
            }
        }
        return true;
    }

    private GameObject EffectPrefab(EffectType type)
    {
        if (effectSelector == null) return null;
        switch (type)
        {
            case EffectType.Inpainting: return effectSelector.InpaintingEffectPanelPrefab;
            case EffectType.Opacity: return effectSelector.OpacityEffectPanelPrefab;
            case EffectType.GaussianBlur: return effectSelector.GaussianBlurEffectPanelPrefab;
            case EffectType.Kuwahara: return effectSelector.KuwaharaEffectPanelPrefab;
            default: return effectSelector.EffectPanelPrefab;
        }
    }

    private string CurrentDepthModel()
    {
        if (depthRunner == null) return null;
        string model = depthRunner.ModelAsset != null ? depthRunner.ModelAsset.name : depthRunner.GetType().Name;
        return model != null && model.Length > 128 ? model.Substring(0, 128) : model;
    }

    private bool TryApplyPreset(MiragePreset preset, bool remember)
    {
        if (!ValidateForScene(preset, out string error)) return SetError(error);
        // Construct the complete replacement while the previous cards still exist.
        // This catches missing/malformed prefab components before replacing UI state.
        var replacements = new List<ClassPanel>();
        try
        {
            foreach (var category in preset.classes)
            {
                ClassSetting setting = ClassSettings.First(item => item.ClassID != null &&
                    MiragePresetStore.ClassKey(item.ClassID) == MiragePresetStore.ClassKey(category.classIds));
                var panel = Instantiate(ClassPanelPrefab, ClassContainer).GetComponent<ClassPanel>();
                if (panel == null) throw new InvalidOperationException("Class panel prefab has no ClassPanel component.");
                panel.Initialize(setting);
                replacements.Add(panel);
                foreach (var saved in category.effects)
                {
                    var type = (EffectType)Enum.Parse(typeof(EffectType), saved.effect);
                    var effect = Instantiate(EffectPrefab(type), panel.container).GetComponent<EffectPanel>();
                    if (effect == null) throw new InvalidOperationException("Effect panel prefab has no EffectPanel component.");
                    effect.effectType = type;
                    var button = FindObjectsByType<EffectButton>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                        .FirstOrDefault(candidate => candidate.effectType == type);
                    if (effect.effectName != null) effect.effectName.text = button != null ? button.gameObject.name : type.ToString();
                    if (effect.effectIcon != null && button != null && button.effectIcon != null) effect.effectIcon.sprite = button.effectIcon.sprite;
                    panel.AddEffectPanel(effect);
                    effect.RestorePreset(saved);
                }
            }
        }
        catch (Exception exception)
        {
            foreach (var panel in replacements) { panel.gameObject.SetActive(false); Destroy(panel.gameObject); }
            return SetError("The preset UI could not be restored. The previous settings were kept. " + exception.Message);
        }
        foreach (var panel in classPanels) { panel.gameObject.SetActive(false); Destroy(panel.gameObject); }
        classPanels.Clear();
        classPanels.AddRange(replacements);
        var selected = new HashSet<string>(preset.classes.Select(category => MiragePresetStore.ClassKey(category.classIds)), StringComparer.Ordinal);
        foreach (var checkbox in classToggles)
            checkbox.SetSelectedWithoutNotify(selected.Contains(MiragePresetStore.ClassKey(checkbox.ClassSetting.ClassID)));
        if (!ApplyData(preset)) return false;
        ActivePresetName = preset.name;
        LastPresetError = null;
        if (remember && !store.TrySaveLast(preset, out error))
            Debug.LogWarning("[MIRAGE PRESET_LAST_SESSION] Preset loaded; the last-session copy could not be updated. " + error, this);
        return true;
    }

    private bool ApplyData(MiragePreset preset)
    {
        try
        {
            foreach (var list in effectSettings.Values) list.Clear();
            foreach (var category in preset.classes)
            foreach (var effect in category.effects)
            foreach (int classId in category.classIds)
            {
                var type = (EffectType)Enum.Parse(typeof(EffectType), effect.effect);
                if (type == EffectType.Inpainting)
                    AddInpainting(classId, effect.minimum, effect.maximum);
                else
                {
                    effectSettings[type].Add(new PostProcessorSetting(classId,
                        new Color(effect.rgba[0], effect.rgba[1], effect.rgba[2], effect.rgba[3]), effect.minimum, effect.maximum));
                    if (type == EffectType.Opacity) AddInpainting(classId, effect.minimum, effect.maximum);
                }
            }
            foreach (var handler in effectHandlers)
                if (handler != null && effectSettings.TryGetValue(handler.EffectType, out List<EffectSetting> settings))
                    handler.UpdateClasses(settings);
            foreach (var layer in preset.layers)
            {
                var toggle = LayerSettings.FirstOrDefault(item => item != null && item.gameObject.name == layer.name);
                if (toggle != null) toggle.isOn = layer.enabled;
            }
            foreach (var setting in preset.runners)
            {
                ModelRunner runner = setting.role == "segmentation" ? segmentationRunner :
                    setting.role == "depth" ? depthRunner : inpaintingRunner;
                if (runner == null) continue;
                runner.SetUpdateRate(setting.updateRate);
                foreach (var control in FindObjectsByType<ModelInferenceSpeedSlider>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(control => control.ModelRunner == runner))
                {
                    var slider = control.GetComponent<Slider>();
                    if (slider != null) slider.SetValueWithoutNotify(setting.updateRate);
                    if (control.SliderText != null) control.SliderText.text = setting.updateRate.ToString("0.00");
                }
            }
            if (depthRunner != null) depthRunner.IsEnabled = preset.depthEnabled;
            if (inpaintingRunner != null) inpaintingRunner.IsEnabled = preset.inpaintingEnabled;
            // Model state is set directly; Start-order-dependent toggle callbacks are not needed.
            if (depth != null) depth.SetIsOnWithoutNotify(preset.depthEnabled);
            if (inpainting != null) inpainting.SetIsOnWithoutNotify(preset.inpaintingEnabled);
            var pipeline = FindAnyObjectByType<Pipeline>();
            if (pipeline != null) pipeline.NotifySettingsChanged();
            try
            {
                if (StudyManager.Instance != null) StudyManager.Instance.LogSettings(effectSettings, LayerSettings);
            }
            catch (Exception)
            {
                // Study logging is independent from applying/saving a preset.
                Debug.LogWarning("[MIRAGE STUDY_LOG_WRITE] Effects were applied, but the study settings log could not be written.", this);
            }
            return true;
        }
        catch (Exception exception)
        {
            return SetError("Settings could not be applied. Check the startup report and model setup. " + exception.Message);
        }
    }

    private void AddInpainting(int classId, float minimum, float maximum)
    {
        if (!effectSettings[EffectType.Inpainting].Any(setting => setting.ClassID == classId &&
            setting.MinRange == minimum && setting.MaxRange == maximum))
            effectSettings[EffectType.Inpainting].Add(new InpaintingSetting(classId, minimum, maximum));
    }

    private void SeedStarterPresets()
    {
        ClassSetting person = ClassSettings.FirstOrDefault(category => category.ClassID != null && category.ClassID.Contains(0));
        if (person.ClassID == null) return;
        foreach (var example in new[] {
            new { Name = "Outline people", Type = EffectType.Outline, Color = new[] { 1f, 0.85f, 0f, 1f } },
            new { Name = "Blur people", Type = EffectType.GaussianBlur, Color = new[] { 12f / 255f, 4f / 255f, 1f, 1f } },
            new { Name = "Remove people", Type = EffectType.Inpainting, Color = new[] { 1f, 1f, 1f, 1f } }
        })
        {
            var preset = EmptyPreset(example.Name);
            foreach (var layer in preset.layers) layer.enabled = true;
            foreach (var runner in preset.runners) runner.updateRate = 1f;
            preset.inpaintingEnabled = example.Type == EffectType.Inpainting;
            preset.classes.Add(new MiragePresetClass
            {
                classIds = (int[])person.ClassID.Clone(),
                effects = new List<MiragePresetEffect> { new MiragePresetEffect
                { effect = example.Type.ToString(), minimum = 0, maximum = 100, rgba = example.Color } }
            });
            starterPresets[example.Name] = preset;
            if (store.Exists(example.Name)) continue;
            if (!store.TrySave(example.Name, preset, out string error))
            {
                SetError("Starter presets could not be saved. " + error);
            }
        }
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }
}

[Serializable]
public struct ClassSetting
{
    public string ClassName;
    public int[] ClassID;
}
