using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class EffectSelectorPanel : MonoBehaviour
{
    public static EffectSelectorPanel Instance;

    public GameObject EffectPanelPrefab;
    public GameObject InpaintingEffectPanelPrefab;

    public GameObject OpacityEffectPanelPrefab;

    public GameObject KuwaharaEffectPanelPrefab;

    public GameObject GaussianBlurEffectPanelPrefab;
    public ClassPanel activeClassPanel;
    // Start is called before the first frame update

    Vector2 initialPos;

    void Awake()
    {
        Instance = this;   
        initialPos = GetComponent<RectTransform>().anchoredPosition;
        gameObject.SetActive(false);
    }


    public void SwitchClassPanel(ClassPanel panel) {
        

        activeClassPanel = panel;
        gameObject.SetActive(true);
        transform.position = panel.popupPosition.position;
    }




    public void RegisterEffectButton(EffectButton button) {
        button.GetComponent<Button>().onClick.AddListener(() => {

            transform.GetComponent<RectTransform>().anchoredPosition = initialPos;
            gameObject.SetActive(false);
            GameObject go;
            switch(button.effectType) {
                case EffectType.Inpainting:
                 go = Instantiate(InpaintingEffectPanelPrefab, activeClassPanel.container);
                    break;

                case EffectType.Opacity:
                    go = Instantiate(OpacityEffectPanelPrefab, activeClassPanel.container);
                    break;

                case EffectType.GaussianBlur:
                    go = Instantiate(GaussianBlurEffectPanelPrefab, activeClassPanel.container);
                    break;
                case EffectType.Kuwahara:
                    go = Instantiate(KuwaharaEffectPanelPrefab, activeClassPanel.container);
                    break;
                default:
                    go = Instantiate(EffectPanelPrefab, activeClassPanel.container);
                    break;
            }
            

            EffectPanel effectPanel = go.GetComponent<EffectPanel>();
            effectPanel.effectName.text = button.effectName.text;
            effectPanel.effectIcon.sprite = button.effectIcon.sprite;

            effectPanel.effectType = button.effectType;
            activeClassPanel.AddEffectPanel(effectPanel);
        });
    }
}
