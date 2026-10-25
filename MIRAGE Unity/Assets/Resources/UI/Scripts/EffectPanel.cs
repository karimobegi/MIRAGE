using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.Extensions;


public class EffectPanel : MonoBehaviour
{

    public EffectType effectType;

    public TMP_Text effectName;

    public Image effectIcon;


    public Color color = Color.white;
    public MinMaxSlider MinMaxSlider;

    public float MinValue {get => MinMaxSlider != null ? MinMaxSlider.Values.minValue : 0f;}
    public float MaxValue {get => MinMaxSlider != null ? MinMaxSlider.Values.maxValue : 100f;}
    public Image ColorButton;
    private ClassPanel classPanel;
    private bool refreshRestoredRange;
    private float restoredMinimum, restoredMaximum;


    public void Initialize(ClassPanel panel) {
        this.classPanel = panel;


        if(effectType == EffectType.ColorArea) {
            color.a = 0.5f;
        }

        switch (effectType) {
            case EffectType.ColorArea:
            case EffectType.Outline:
            case EffectType.Icon:
            case EffectType.BBox:
            case EffectType.Info:
                if (EffectColorHandler.Instance != null) EffectColorHandler.Instance.SwitchActiveEffectPanel(this, false);
            break;
        }

    }

    public void ClosePanel() {
        classPanel.RemoveEffectPanel(this);
        
        Destroy(gameObject);
    }

    public void OnColorButtonClicked()
    {
        if (EffectColorHandler.Instance != null) EffectColorHandler.Instance.SwitchActiveEffectPanel(this);
    }

    /// <summary>Restore both data and visible controls before their Start callbacks run.</summary>
    public void RestorePreset(MiragePresetEffect settings)
    {
        color = new Color(settings.rgba[0], settings.rgba[1], settings.rgba[2], settings.rgba[3]);
        if (ColorButton != null) ColorButton.color = color;
        restoredMinimum = settings.minimum;
        restoredMaximum = settings.maximum;
        if (MinMaxSlider != null) MinMaxSlider.SetValues(restoredMinimum, restoredMaximum, false);
        refreshRestoredRange = true;

        // Packed RGB values also hold filter parameters in the existing shader UI.
        foreach (var opacity in GetComponentsInChildren<OpacitySlider>(true))
        {
            var slider = opacity.GetComponent<Slider>();
            if (slider != null) slider.SetValueWithoutNotify(color.a);
            if (opacity.sliderText != null) opacity.sliderText.text = color.a.ToString("0.00");
        }
        foreach (var blur in GetComponentsInChildren<GaussianBlurSlider>(true))
        {
            if (blur.RadiusSlider != null) blur.RadiusSlider.SetValueWithoutNotify(color.r * 255f);
            if (blur.SigmaSlider != null) blur.SigmaSlider.SetValueWithoutNotify(color.g * 255f);
            if (blur.RadiusText != null) blur.RadiusText.text = (color.r * 255f).ToString("0.##");
            if (blur.SigmaText != null) blur.SigmaText.text = (color.g * 255f).ToString("0.##");
        }
        foreach (var painterly in GetComponentsInChildren<KuwaharaSlider>(true))
        {
            if (painterly.RadiusSlider != null) painterly.RadiusSlider.SetValueWithoutNotify(color.r * 255f);
            if (painterly.SectorSlider != null) painterly.SectorSlider.SetValueWithoutNotify(color.g * 255f);
            if (painterly.StrengthSlider != null) painterly.StrengthSlider.SetValueWithoutNotify(color.b * 255f);
            if (painterly.RadiusText != null) painterly.RadiusText.text = (color.r * 255f).ToString("0.##");
            if (painterly.SectorText != null) painterly.SectorText.text = (color.g * 255f).ToString("0.##");
            if (painterly.StrengthText != null) painterly.StrengthText.text = (color.b * 255f).ToString("0.00");
        }
    }

    private void LateUpdate()
    {
        // A just-instantiated layout may not have its final dimensions until
        // the first visible UI frame. Refresh handles once after that layout.
        if (!refreshRestoredRange || MinMaxSlider == null) return;
        Canvas.ForceUpdateCanvases();
        MinMaxSlider.SetValues(restoredMinimum, restoredMaximum, false);
        refreshRestoredRange = false;
    }
}


