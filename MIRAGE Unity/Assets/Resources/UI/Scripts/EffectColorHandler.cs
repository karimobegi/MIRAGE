using System.Collections;
using System.Collections.Generic;
using HSVPicker;
using UnityEngine;

public class EffectColorHandler : MonoBehaviour
{
    public static EffectColorHandler Instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        Instance = null;
    }
    private ColorPicker colorPicker;

    private EffectPanel effectPanel;

    public RectTransform VirtualCursor;

    public RectTransform ColorHandle;
    void Start()
    {   

        Instance = this;
        colorPicker = GetComponent<ColorPicker>();
        colorPicker.onValueChanged.AddListener(color =>
        {
            if(effectPanel == null || effectPanel.ColorButton == null) return;
            effectPanel.color = color;
            effectPanel.ColorButton.color = color;
            
        });
    }
    
    public void SwitchActiveEffectPanel(EffectPanel effectPanel, bool moveCursor = true)
    {
        this.effectPanel = effectPanel;
        colorPicker.CurrentColor = this.effectPanel.color;
        if(moveCursor) {
            VirtualCursor.position = ColorHandle.position;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

}
