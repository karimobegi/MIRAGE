using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using UnityEngine.EventSystems;
using System.IO;
using Newtonsoft.Json;

/// <summary>
/// Script that handles the UI for the pipeline settings.
/// </summary>
public class PipelineUIHandler : MonoBehaviour
{
    public static PipelineUIHandler Instance;

    /// <summary>
    /// List that contains all available classes and their corresponding ClassIDs.
    /// For now, these are added manually in Unity, but could be loaded from a file in the future.
    /// 
    /// For our demo, we used YOLO11 with a total of 80 classes. However, only a few are relevant for the driving context.
    /// </summary>
    public List<ClassSetting> ClassSettings;

    public List<Toggle> LayerSettings;
    private List<ClassPanel> classPanels = new List<ClassPanel>();

    // Lists for each effect type
    private Dictionary<EffectType, List<EffectSetting>> effectSettings = new Dictionary<EffectType, List<EffectSetting>>();

    public Transform CheckBoxContainer;
    public GameObject CheckBoxPrefab;

    public Transform ClassContainer;
    public GameObject ClassPanelPrefab;
    public List<IEffectHandler> effectHandlers = new List<IEffectHandler>();

    void Awake() 
    {
        Instance = this;
        InitializeEffectLists();

        foreach(var setting in ClassSettings) {
            GameObject newCheckBox = Instantiate(CheckBoxPrefab, CheckBoxContainer);
            ToggleClass checkBox = newCheckBox.GetComponent<ToggleClass>();
            checkBox.text.text = setting.ClassName;
            checkBox.ClassSetting = setting;
        }


    }

    void Start()
    {
        // Find all MonoBehaviours that implement IEffectHandler in the scene
        var handlers = FindObjectsOfType<MonoBehaviour>().OfType<IEffectHandler>();
        effectHandlers.AddRange(handlers);

       // EventSystem.current.SetSelectedGameObject(null);
       
       // EventSystem.current.SetSelectedGameObject(CheckBoxContainer.GetChild(1).gameObject);
        StartCoroutine(ModelSettingsSetup());
    }

    private void InitializeEffectLists()
    {
        // Initialize a list for each effect type
        foreach (EffectType effectType in System.Enum.GetValues(typeof(EffectType)))
        {
            effectSettings[effectType] = new List<EffectSetting>();
        }
    }

    public void ToggleClass(bool value, ClassSetting setting) {

        if(value) {
            GameObject newPanel = Instantiate(ClassPanelPrefab, ClassContainer);
            ClassPanel classPanel = newPanel.GetComponent<ClassPanel>();
            classPanel.Initialize(setting);
            classPanels.Add(classPanel);
        } else {

            var c = classPanels.Find(x => x.ClassSetting.ClassName == setting.ClassName);;
            classPanels.Remove(c);
            Destroy(c.gameObject);
        }
       
    }

    public void ToggleUI(bool value) {
        if(!value) { 
            OnSave();
            
        } else {
            gameObject.SetActive(value);
        }

    }

    /// <summary>
    /// Passes changes to the various effect handlers
    /// </summary>
    public void OnSave() 
    {
        shouldCalculateDepth = false;
        shouldInpaint = false;

        // Clear all existing settings
        foreach (var list in effectSettings.Values)
        {
            list.Clear();
        }

        // Go through all class panels
        foreach (var classPanel in classPanels)
        {
            foreach (var effectPanel in classPanel.effectPanels)
            {
                // For each ClassID in the array, create a setting
                foreach (int classId in classPanel.ClassSetting.ClassID)
                {
                    if (effectPanel.effectType == EffectType.Inpainting)
                    {
                        var setting = new InpaintingSetting(
                            classId,
                            effectPanel.MinValue,
                            effectPanel.MaxValue
                        );
                        effectSettings[effectPanel.effectType].Add(setting);
                        shouldInpaint = true;
                    } 
                    else
                    {
                        var setting = new PostProcessorSetting(
                            classId,
                            effectPanel.color,
                            effectPanel.MinValue,
                            effectPanel.MaxValue
                        );
                        effectSettings[effectPanel.effectType].Add(setting);

                        if(effectPanel.effectType == EffectType.Opacity) {
                            var ipSetting = new InpaintingSetting(
                                classId,
                                effectPanel.MinValue,
                                effectPanel.MaxValue
                            );
                            effectSettings[EffectType.Inpainting].Add(ipSetting);
                            shouldInpaint = true;
                        }

                    }

                    if(effectPanel.MinValue > 0 || effectPanel.MaxValue < 100 || effectPanel.effectType == EffectType.Info) {
                        shouldCalculateDepth = true;
                    }
                }
                
            }
        }

        // Update all effect handlers with their corresponding settings
        foreach (var handler in effectHandlers)
        {
            if (effectSettings.ContainsKey(handler.EffectType))
            {
                handler.UpdateClasses(effectSettings[handler.EffectType]);
            }
        }

        StartCoroutine(UpdateModelSettings());

    }

    // Method to get settings for a specific effect type
    public List<EffectSetting> GetEffectSettings(EffectType effectType)
    {
        return effectSettings[effectType];
    }

/**
* Below this is a makeshift solution 
*/
    public Toggle depth;
    public Toggle inpainting;


    bool shouldCalculateDepth = false;
    bool shouldInpaint = false;

    IEnumerator ModelSettingsSetup() {
        yield return new WaitForSeconds(1f);
        var depthToggle = FindObjectOfType<DepthToggle>();
        depthToggle.transform.parent.GetComponentInChildren<Slider>().value = 0.1f;
        depth = depthToggle.gameObject.GetComponent<Toggle>();
        depth.isOn = false;

        var inpaintingToggle = FindObjectOfType<InpaintingToggle>();
        inpainting = inpaintingToggle.gameObject.GetComponent<Toggle>();
        inpainting.isOn = false;

        yield return null;
    }

    IEnumerator UpdateModelSettings() {
        yield return new WaitForSeconds(0.1f);
        depth.isOn = shouldCalculateDepth;
        inpainting.isOn = shouldInpaint;


        if(StudyManager.Instance != null) StudyManager.Instance.LogSettings(effectSettings, LayerSettings);
        yield return new WaitForSeconds(0.1f);
        gameObject.SetActive(false);
        yield return null;
    }



}

[System.Serializable]
public struct ClassSetting {
    public string ClassName;
    public int[] ClassID;
}

