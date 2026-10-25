// Explicit UI/inference seams for exercising production preset code without Unity.
// They do not claim native Unity API or layout/rendering compatibility.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace UnityEngine
{
    public class SerializeField : Attribute { }
    public enum FindObjectsInactive { Include, Exclude }
    public enum FindObjectsSortMode { None }
    public class Object
    {
        internal static readonly List<Object> Registry = new List<Object>();
        public string name;
        public Object() { Registry.Add(this); }
        public static T[] FindObjectsByType<T>(FindObjectsSortMode sort) where T : Object => FindObjectsByType<T>(FindObjectsInactive.Exclude, sort);
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : Object
        {
            return Registry.OfType<T>().Where(item => !(item is Component component) ||
                (component.gameObject != null && !component.gameObject.IsAsset && !component.gameObject.Destroyed &&
                 (inactive == FindObjectsInactive.Include || component.gameObject.activeInHierarchy))).ToArray();
        }
        public static T FindAnyObjectByType<T>() where T : Object => FindObjectsByType<T>(FindObjectsSortMode.None).FirstOrDefault();
        public static GameObject Instantiate(GameObject prefab, Transform parent)
        {
            var clone = prefab.Factory();
            clone.transform.parent = parent;
            if (parent != null) parent.Children.Add(clone);
            return clone;
        }
        public static void Destroy(Object value)
        {
            if (value is GameObject go) { go.Destroyed = true; go.activeInHierarchy = false; }
            Registry.Remove(value);
        }
    }
    public class GameObject : Object
    {
        public readonly List<Component> Components = new List<Component>();
        public Transform transform;
        public bool activeInHierarchy = true, IsAsset, Destroyed;
        public bool activeSelf => activeInHierarchy;
        public int scene = 1;
        public Func<GameObject> Factory;
        public GameObject(string label = "Object") { name = label; transform = new Transform { gameObject = this }; }
        public T Add<T>(T component) where T : Component { component.gameObject = this; Components.Add(component); return component; }
        public T GetComponent<T>() where T : class => Components.OfType<T>().FirstOrDefault();
        public T GetComponentInChildren<T>(bool inactive = false) where T : class => GetComponentsInChildren<T>(inactive).FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : class
        {
            return Components.OfType<T>().Concat(transform.Children.Where(child => inactive || child.activeInHierarchy)
                .SelectMany(child => child.GetComponentsInChildren<T>(inactive))).ToArray();
        }
        public void SetActive(bool active) { activeInHierarchy = active; }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public T GetComponentInChildren<T>(bool inactive = false) where T : class => gameObject.GetComponentInChildren<T>(inactive);
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : class => gameObject.GetComponentsInChildren<T>(inactive);
    }
    public class Transform : Component { public Transform parent; public readonly List<GameObject> Children = new List<GameObject>(); }
    public class MonoBehaviour : Component { public bool enabled = true; }
    public class Sprite : Object { }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1, 1);
    }
    public static class Application { public static string persistentDataPath; }
    public static class Input { public static bool CancelPressed; public static bool GetButtonDown(string name) { bool pressed = CancelPressed; CancelPressed = false; return pressed; } }
    public static class Canvas { public static void ForceUpdateCanvases() { } }
    public static class Debug { public static readonly List<string> Messages = new List<string>(); public static void Log(string value, Object context = null) { Messages.Add(value); } public static void LogWarning(string value, Object context = null) { Messages.Add(value); } }
}
namespace TMPro { public class TMP_Text : UnityEngine.Component { public string text; } }
namespace UnityEngine.UI
{
    public class Image : UnityEngine.Component { public UnityEngine.Color color; public UnityEngine.Sprite sprite; }
    public class Slider : UnityEngine.Component
    {
        public float value, minValue, maxValue = 1;
        public void SetValueWithoutNotify(float newValue) { value = Math.Max(minValue, Math.Min(maxValue, newValue)); }
    }
    public class Toggle : UnityEngine.Component
    {
        public class ToggleEvent : UnityEngine.Events.UnityEvent<bool> { }
        private bool state;
        public Action<bool> Changed;
        public bool isOn { get => state; set { state = value; Changed?.Invoke(value); } }
        public void SetIsOnWithoutNotify(bool value) { state = value; }
    }
}
namespace UnityEngine.UI.Extensions
{
    public struct MinMaxValues { public float minValue, maxValue; }
    public class MinMaxSlider : UnityEngine.Component
    {
        public MinMaxValues Values = new MinMaxValues { minValue = 0, maxValue = 100 };
        public void SetValues(float minimum, float maximum, bool notify = true) { Values = new MinMaxValues { minValue = minimum, maxValue = maximum }; }
    }
}
namespace UnityEngine.EventSystems { }
namespace UnityEngine.Events
{
    public class UnityEvent<T>
    {
        private readonly List<UnityEngine.Object> targets = new List<UnityEngine.Object>();
        private readonly List<Action<T>> listeners = new List<Action<T>>();
        public void AddPersistentListener(UnityEngine.Object target, Action<T> listener) { targets.Add(target); listeners.Add(listener); }
        public int GetPersistentEventCount() => targets.Count;
        public UnityEngine.Object GetPersistentTarget(int index) => targets[index];
        public void Invoke(T value) { foreach (var listener in listeners) listener(value); }
    }
}

public class EffectSelectorPanel : MonoBehaviour
{
    public static EffectSelectorPanel Instance;
    public GameObject EffectPanelPrefab, InpaintingEffectPanelPrefab, OpacityEffectPanelPrefab, KuwaharaEffectPanelPrefab, GaussianBlurEffectPanelPrefab;
    public void SwitchClassPanel(ClassPanel panel) { }
}
public class EffectColorHandler : MonoBehaviour
{
    public static EffectColorHandler Instance;
    public void SwitchActiveEffectPanel(EffectPanel effect, bool move = true) { }
}
public class EffectButton : MonoBehaviour { public EffectType effectType; public TMPro.TMP_Text effectName; public Image effectIcon; }
public class ModelInferenceSpeedSlider : MonoBehaviour { public ModelRunner ModelRunner; public TMPro.TMP_Text SliderText; }
public class DepthToggle : MonoBehaviour { }
public class InpaintingToggle : MonoBehaviour { }
public class ModelRunner : MonoBehaviour { public float UpdateRate = 1; public bool IsEnabled = true; public UnityEngine.Object ModelAsset; public void SetUpdateRate(float value) { UpdateRate = value; } }
public class YOLOSegmentationRunner : ModelRunner { }
public class DepthEstimationRunner : ModelRunner { public bool UsesMetricDepth; }
public class InpaintingRunner : ModelRunner { }
public class Pipeline : MonoBehaviour { public InpaintingRunner ActiveInpaintingModel; public int SettingsChanges; public void NotifySettingsChanged() { SettingsChanges++; } }
public class StudyManager : MonoBehaviour
{
    public static StudyManager Instance;
    public void LogSettings(Dictionary<EffectType, List<EffectSetting>> effects, List<Toggle> layers) { }
}
public class PresetProbeHandler : MonoBehaviour, IEffectHandler
{
    public EffectType Type;
    public EffectType EffectType => Type;
    public List<EffectSetting> Values = new List<EffectSetting>();
    public void UpdateClasses(List<EffectSetting> values) { Values = new List<EffectSetting>(values); }
    public void UpdateClasses() { }
}

namespace Mirage.Usability
{
    public static class MirageOperatorPanel { public static bool IsVisible; }
}
