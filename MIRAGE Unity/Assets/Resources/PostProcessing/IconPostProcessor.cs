using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ByteTrackCSharp;
/// <summary>
/// Postprocessor that creates an icon for classes
/// 
///  Author: J-Britten
/// </summary>
public class IconPostProcessor : CPUPostProcessor
{
    public GameObject iconPrefab;
    private List<GameObject> icons = new List<GameObject>();

    protected override bool HasExistingObject(int index) {
        return icons.Count > index;
    }

    protected override void UpdateObject(int index, LabelledSTrack track, Vector2 position, Vector2 size) {
        icons[index].GetComponent<RectTransform>().anchoredPosition = position;
        icons[index].GetComponent<Image>().color = GetColorForObject(track);
        icons[index].SetActive(true);
    }

    protected override void CreateObject(LabelledSTrack track, Vector2 position, Vector2 size) {
        GameObject icon = Instantiate(iconPrefab, outputContainer.transform);
        icon.GetComponent<RectTransform>().anchoredPosition = position;
        icon.GetComponent<Image>().color = GetColorForObject(track);
        icons.Add(icon);
    }

    protected override void DeactivateRemainingObjects(int startIndex) {
        for(int i = startIndex; i < icons.Count; i++) {
            icons[i].SetActive(false);
        }
    }

    protected override void OnClassesUpdated()
    {
        foreach (var icon in icons)
        {
            if (icon != null)
            {
                icon.SetActive(false);
            }
        }
    }
}
