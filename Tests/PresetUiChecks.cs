using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.Extensions;

public static class PresetUiChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Invoke(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
    private static List<ClassPanel> Cards(PipelineUIHandler ui) => (List<ClassPanel>)typeof(PipelineUIHandler).GetField("classPanels", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui);
    private static T Attach<T>(string label, T component) where T : Component => new GameObject(label).Add(component);
    private static GameObject Asset(string name, Func<GameObject> factory) => new GameObject(name) { IsAsset = true, Factory = factory };
    private static T Child<T>(GameObject parent, string label, T component) where T : Component
    {
        var child = new GameObject(label); child.transform.parent = parent.transform; parent.transform.Children.Add(child); return child.Add(component);
    }
    private static GameObject ClassCard()
    {
        var go = new GameObject("Class card");
        go.Add(new ClassPanel { classNameText = Child(go, "Name", new TMPro.TMP_Text()), container = go.transform, effectPanels = new List<EffectPanel>() });
        return go;
    }
    private static GameObject Checkbox()
    {
        var go = new GameObject("Checkbox");
        var script = go.Add(new ToggleClass { text = Child(go, "Name", new TMPro.TMP_Text()) });
        go.Add(new Toggle { Changed = script.OnToggle });
        return go;
    }
    private static Slider Parameter(GameObject go, string name, float minimum, float maximum)
    {
        return Child(go, name, new Slider { minValue = minimum, maxValue = maximum });
    }
    private static GameObject EffectCard(EffectType type)
    {
        var go = new GameObject("Effect card");
        var panel = go.Add(new EffectPanel
        {
            effectType = type, effectName = Child(go, "Name", new TMPro.TMP_Text()),
            effectIcon = Child(go, "Icon", new Image()), ColorButton = Child(go, "Color", new Image()),
            MinMaxSlider = Child(go, "Range", new MinMaxSlider())
        });
        if (type == EffectType.Opacity)
        {
            var opacity = Parameter(go, "Opacity", 0, 1);
            opacity.gameObject.Add(new OpacitySlider { effectPanel = panel, sliderText = Child(go, "Opacity label", new TMPro.TMP_Text()) });
        }
        if (type == EffectType.GaussianBlur)
            go.Add(new GaussianBlurSlider
            {
                effectPanel = panel, RadiusSlider = Parameter(go, "Radius", 0, 30), SigmaSlider = Parameter(go, "Sigma", 0, 16),
                RadiusText = Child(go, "Radius label", new TMPro.TMP_Text()), SigmaText = Child(go, "Sigma label", new TMPro.TMP_Text())
            });
        if (type == EffectType.Kuwahara)
            go.Add(new KuwaharaSlider
            {
                effectPanel = panel, RadiusSlider = Parameter(go, "Radius", 1, 10), SectorSlider = Parameter(go, "Sectors", 2, 16),
                StrengthSlider = Parameter(go, "Strength", 0, 4), RadiusText = Child(go, "Radius label", new TMPro.TMP_Text()),
                SectorText = Child(go, "Sector label", new TMPro.TMP_Text()), StrengthText = Child(go, "Strength label", new TMPro.TMP_Text())
            });
        return go;
    }
    private sealed class Fixture
    {
        public PipelineUIHandler Ui; public DepthEstimationRunner Depth; public InpaintingRunner Inpainting;
        public EffectSelectorPanel Selector; public PresetProbeHandler Removal;
    }
    private static Fixture Setup(string directory)
    {
        UnityEngine.Object.Registry.Clear();
        Application.persistentDataPath = directory;
        PipelineUIHandler.Instance = null; EffectSelectorPanel.Instance = null; EffectColorHandler.Instance = null; StudyManager.Instance = null;
        var fixture = new Fixture();
        Attach("Segmentation", new YOLOSegmentationRunner());
        fixture.Depth = Attach("Depth", new DepthEstimationRunner { ModelAsset = new UnityEngine.Object { name = "DA3-SMALL" } });
        fixture.Inpainting = Attach("MI-GAN", new InpaintingRunner());
        Attach("Pipeline", new Pipeline { ActiveInpaintingModel = fixture.Inpainting });
        fixture.Selector = Attach("Effect selector", new EffectSelectorPanel
        {
            EffectPanelPrefab = Asset("Effect asset", () => EffectCard(EffectType.Outline)),
            InpaintingEffectPanelPrefab = Asset("Removal asset", () => EffectCard(EffectType.Inpainting)),
            OpacityEffectPanelPrefab = Asset("Opacity asset", () => EffectCard(EffectType.Opacity)),
            GaussianBlurEffectPanelPrefab = Asset("Blur asset", () => EffectCard(EffectType.GaussianBlur)),
            KuwaharaEffectPanelPrefab = Asset("Painterly asset", () => EffectCard(EffectType.Kuwahara))
        });
        EffectSelectorPanel.Instance = fixture.Selector;
        foreach (EffectType type in Enum.GetValues(typeof(EffectType)))
        {
            var handler = Attach(type.ToString(), new PresetProbeHandler { Type = type });
            if (type == EffectType.Inpainting) fixture.Removal = handler;
        }
        fixture.Ui = Attach("Settings", new PipelineUIHandler
        {
            ClassSettings = new List<ClassSetting> { new ClassSetting { ClassName = "Person", ClassID = new[] { 0 } }, new ClassSetting { ClassName = "Car", ClassID = new[] { 2, 7 } } },
            LayerSettings = new List<Toggle> { Attach("Overlay", new Toggle { isOn = true }) },
            CheckBoxContainer = new GameObject("Checkbox container").transform, CheckBoxPrefab = Asset("Checkbox asset", Checkbox),
            ClassContainer = new GameObject("Class container").transform, ClassPanelPrefab = Asset("Class asset", ClassCard),
            depth = Attach("Depth toggle", new Toggle()), inpainting = Attach("Removal toggle", new Toggle())
        });
        Invoke(fixture.Ui, "Awake");
        var start = (IEnumerator)Invoke(fixture.Ui, "Start");
        Check(start.MoveNext() && !fixture.Ui.IsReady, "Readiness did not wait for scene Start barrier");
        Check(!start.MoveNext() && fixture.Ui.IsReady, "Readiness was not reached after the lifecycle barrier");
        return fixture;
    }
    private static EffectPanel Add(Fixture fixture, EffectType type, Color color, float minimum = 0, float maximum = 100)
    {
        var category = fixture.Ui.ClassSettings[1];
        fixture.Ui.ToggleClass(true, category);
        var card = Cards(fixture.Ui).Single();
        var effect = EffectCard(type).GetComponent<EffectPanel>();
        effect.effectType = type;
        card.AddEffectPanel(effect);
        effect.RestorePreset(new MiragePresetEffect { effect = type.ToString(), minimum = minimum, maximum = maximum, rgba = new[] { color.r, color.g, color.b, color.a } });
        return effect;
    }
    public static string Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mirage-preset-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Fixture fixture = Setup(directory);
            Check(fixture.Ui.GetPresetNames().SequenceEqual(new[] { "Blur people", "Outline people", "Remove people" }), "Starter presets missing or incorrectly ordered");
            Check(Cards(fixture.Ui).Count == 0, "Startup silently selected a removal preset");
            Add(fixture, EffectType.Opacity, new Color(1, 1, 1, 0.25f), 5, 80);
            Add(fixture, EffectType.Inpainting, Color.white, 5, 80);
            Add(fixture, EffectType.GaussianBlur, new Color(12f / 255f, 4f / 255f, 1, 1));
            Add(fixture, EffectType.Kuwahara, new Color(3f / 255f, 8f / 255f, 2f / 255f, 1));
            Check(fixture.Ui.SavePreset("Car details"), fixture.Ui.LastPresetError);
            Check(fixture.Removal.Values.Count == 2 && fixture.Removal.Values.All(setting => setting is InpaintingSetting), "Opacity dependency was duplicated or lost its concrete type");
            Check(fixture.Depth.IsEnabled && fixture.Inpainting.IsEnabled, "Required models were not enabled directly");
            fixture.Ui.ToggleClass(false, fixture.Ui.ClassSettings[1]);
            Check(fixture.Ui.LoadPreset("Car details"), fixture.Ui.LastPresetError);
            var restored = Cards(fixture.Ui).Single();
            Check(restored.ClassSetting.ClassID.SequenceEqual(new[] { 2, 7 }) && restored.effectPanels.Count == 4, "Cards/category group were not rebuilt");
            var opacity = restored.effectPanels.Single(effect => effect.effectType == EffectType.Opacity);
            var opacitySlider = opacity.GetComponentInChildren<OpacitySlider>(true);
            Check(opacity.MinValue == 5 && opacity.MaxValue == 80 && opacitySlider.GetComponent<Slider>().value == 0.25f, "Range/opacity controls did not restore");
            Invoke(opacitySlider, "Start");
            Check(opacity.color.a == 0.25f, "Opacity Start overwrote the loaded value");
            var blur = restored.effectPanels.Single(effect => effect.effectType == EffectType.GaussianBlur);
            var blurSlider = blur.GetComponentInChildren<GaussianBlurSlider>(true);
            Check(blurSlider.RadiusSlider.value == 12 && blurSlider.SigmaSlider.value == 4, "Blur controls did not restore");
            Invoke(blurSlider, "Start");
            Check(Math.Abs(blur.color.r - 12f / 255f) < 0.00001f && Math.Abs(blur.color.g - 4f / 255f) < 0.00001f, "Blur Start overwrote packed parameters");
            var painterly = restored.effectPanels.Single(effect => effect.effectType == EffectType.Kuwahara);
            var painterlySlider = painterly.GetComponentInChildren<KuwaharaSlider>(true);
            Invoke(painterlySlider, "Start");
            Check(painterlySlider.RadiusSlider.value == 3 && painterlySlider.SectorSlider.value == 8 && painterlySlider.StrengthSlider.value == 2, "Painterly controls did not restore");
            Check(Math.Abs(painterly.color.b - 2f / 255f) < 0.00001f, "Painterly Start overwrote packed strength");
            var before = Cards(fixture.Ui).Single();
            int changes = UnityEngine.Object.FindAnyObjectByType<Pipeline>().SettingsChanges;
            File.WriteAllText(Path.Combine(fixture.Ui.PresetDirectory, "Broken.json"), "{}");
            Check(!fixture.Ui.LoadPreset("Broken") && ReferenceEquals(before, Cards(fixture.Ui).Single()), "Malformed load mutated the active UI");
            Check(UnityEngine.Object.FindAnyObjectByType<Pipeline>().SettingsChanges == changes, "Rejected load notified the pipeline of a settings change");
            fixture.Depth.UsesMetricDepth = true;
            Check(!fixture.Ui.LoadPreset("Car details") && ReferenceEquals(before, Cards(fixture.Ui).Single()), "Depth-unit mismatch mutated the UI");
            fixture.Depth.UsesMetricDepth = false; fixture.Depth.ModelAsset.name = "Another-relative-model";
            Check(!fixture.Ui.LoadPreset("Car details"), "Relative model mismatch was silently accepted");
            fixture.Depth.ModelAsset.name = "DA3-SMALL";
            Check(fixture.Ui.LoadPreset("Outline people"), fixture.Ui.LastPresetError);
            Check(!fixture.Depth.IsEnabled && !fixture.Inpainting.IsEnabled && Cards(fixture.Ui).Single().ClassSetting.ClassID.Single() == 0, "Outline example enabled expensive models or selected wrong category");
            var legacyVisibility = Attach("ESC control", new UIToggleHandler { value = false });
            legacyVisibility.OnToggle.AddPersistentListener(fixture.Ui, fixture.Ui.ToggleUI);
            fixture.Ui.ToggleUI(true);
            Check(legacyVisibility.value, "Opening settings from a button did not synchronize ESC state");
            Mirage.Usability.MirageOperatorPanel.IsVisible = true;
            Input.CancelPressed = true; Invoke(legacyVisibility, "Update");
            Check(fixture.Ui.gameObject.activeSelf && legacyVisibility.value, "ESC changed the effects panel behind modal help");
            Mirage.Usability.MirageOperatorPanel.IsVisible = false;
            Input.CancelPressed = true; Invoke(legacyVisibility, "Update");
            Check(!fixture.Ui.gameObject.activeSelf && !legacyVisibility.value, "First ESC after opening from a button did not apply and close");
            Input.CancelPressed = true; Invoke(legacyVisibility, "Update");
            Check(fixture.Ui.gameObject.activeSelf && legacyVisibility.value, "ESC did not reopen the applied settings");
            var range = Cards(fixture.Ui).Single().effectPanels.Single().MinMaxSlider;
            range.SetValues(90, 80, false);
            Input.CancelPressed = true; Invoke(legacyVisibility, "Update");
            Check(fixture.Ui.gameObject.activeSelf && legacyVisibility.value, "Failed Apply hid settings or desynchronized ESC");
            range.SetValues(0, 100, false);
            Fixture nextSession = Setup(directory);
            Check(Cards(nextSession.Ui).Single().effectPanels.Single().effectType == EffectType.Outline, "Last applied preset did not restore in a fresh UI session");
            string unavailable = Path.Combine(directory, "readonly-profile");
            Directory.CreateDirectory(Path.Combine(unavailable, "MIRAGE"));
            File.WriteAllText(Path.Combine(unavailable, "MIRAGE", "Presets"), "blocked folder");
            Fixture fallback = Setup(unavailable);
            Check(fallback.Ui.GetPresetNames().Length == 3 && fallback.Ui.LoadPreset("Outline people"), "Unwritable storage prevented the built-in first-run example");
            return "PASS: actual preset UI lifecycle, starter/read-only fallback, card/control restoration, typed opacity dependency, Start callbacks, last session, ESC visibility, and invalid-load/unit/model rejection.";
        }
        finally { Directory.Delete(directory, true); }
    }
}
