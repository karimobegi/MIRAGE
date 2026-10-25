using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class ClassPanel : MonoBehaviour
{

    public ClassSetting ClassSetting;
    public TMP_Text classNameText;

    public Transform container;

    public Transform popupPosition;

    public List<EffectPanel> effectPanels;

    public void Initialize(ClassSetting setting) {
        ClassSetting = setting;
        if (classNameText != null) classNameText.text = setting.ClassName;
        if (effectPanels == null) effectPanels = new List<EffectPanel>();
    }
    public void AddEffectPanel(EffectPanel effectPanel) {
        if (effectPanels == null) effectPanels = new List<EffectPanel>();
        if (effectPanel == null || effectPanels.Contains(effectPanel)) return;
        effectPanels.Add(effectPanel);
        effectPanel.Initialize(this);
    }

    public void RemoveEffectPanel(EffectPanel effectPanel) {
        effectPanels.Remove(effectPanel);
    }


    public void OnAddEffectButtonPress() {
        if (EffectSelectorPanel.Instance != null) EffectSelectorPanel.Instance.SwitchClassPanel(this);
    }
}
