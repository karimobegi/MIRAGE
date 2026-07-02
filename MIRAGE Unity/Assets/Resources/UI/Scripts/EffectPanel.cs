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

    public float MinValue {get => MinMaxSlider.Values.minValue;}
    public float MaxValue {get => MinMaxSlider.Values.maxValue;}
    public Image ColorButton;
    private ClassPanel classPanel;


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
                EffectColorHandler.Instance.SwitchActiveEffectPanel(this, false);
            break;
        }

    }

    public void ClosePanel() {
        classPanel.RemoveEffectPanel(this);
        
        Destroy(gameObject);
    }

    public void OnColorButtonClicked()
    {
        EffectColorHandler.Instance.SwitchActiveEffectPanel(this);
    }
}


