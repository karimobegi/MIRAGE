using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Toggle;

public class UIToggleHandler : MonoBehaviour
{

    public bool value = true;
    [SerializeField]
    public ToggleEvent OnToggle = new ToggleEvent();
    // Start is called before the first frame update
    void Update() {
        if (Mirage.Usability.MirageOperatorPanel.IsVisible) return;
        
        if (Input.GetButtonDown("Cancel")) {
            // Buttons and preset helpers may have opened the settings panel
            // independently. Use its real visibility rather than stale state.
            PipelineUIHandler settings = null;
            for (int i = 0; i < OnToggle.GetPersistentEventCount(); i++)
            {
                settings = OnToggle.GetPersistentTarget(i) as PipelineUIHandler;
                if (settings != null) break;
            }
            value = settings != null ? !settings.gameObject.activeSelf : !value;
            OnToggle.Invoke(value);
            // A failed Apply keeps settings open and must keep ESC in sync.
            if (settings != null) value = settings.gameObject.activeSelf;

        }
    }


}
