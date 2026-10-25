using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ToggleClass : MonoBehaviour
{
    public ClassSetting ClassSetting;
    public TMP_Text text;
    public void OnToggle(bool value) {
        if (PipelineUIHandler.Instance != null) PipelineUIHandler.Instance.ToggleClass(value, ClassSetting);
    }

    public void SetSelectedWithoutNotify(bool value)
    {
        var toggle = GetComponent<Toggle>();
        if (toggle == null) toggle = GetComponentInChildren<Toggle>(true);
        if (toggle != null) toggle.SetIsOnWithoutNotify(value);
    }
}
