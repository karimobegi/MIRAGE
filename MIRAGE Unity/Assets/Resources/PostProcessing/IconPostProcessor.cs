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
    private readonly struct PooledIcon
    {
        public readonly GameObject Root;
        public readonly RectTransform Rect;
        public readonly Image Image;

        public PooledIcon(GameObject root)
        {
            Root = root;
            Rect = root.GetComponent<RectTransform>();
            Image = root.GetComponent<Image>();
        }
    }

    public GameObject iconPrefab;
    private readonly List<PooledIcon> icons = new List<PooledIcon>();

    protected override bool HasExistingObject(int index) {
        return icons.Count > index;
    }

    protected override void UpdateObject(int index, LabelledSTrack track, Vector2 position, Vector2 size) {
        var entry = icons[index];
        if (entry.Rect != null) entry.Rect.anchoredPosition = position;
        if (entry.Image != null) entry.Image.color = GetColorForObject(track);
        if (entry.Root != null && !entry.Root.activeSelf) entry.Root.SetActive(true);
    }

    protected override void CreateObject(LabelledSTrack track, Vector2 position, Vector2 size) {
        GameObject icon = Instantiate(iconPrefab, outputContainer.transform);
        var entry = new PooledIcon(icon);
        if (entry.Rect != null) entry.Rect.anchoredPosition = position;
        if (entry.Image != null) entry.Image.color = GetColorForObject(track);
        icons.Add(entry);
    }

    protected override void DeactivateRemainingObjects(int startIndex) {
        for(int i = startIndex; i < icons.Count; i++) {
            if (icons[i].Root != null && icons[i].Root.activeSelf)
                icons[i].Root.SetActive(false);
        }
    }

    protected override void OnClassesUpdated()
    {
        foreach (var entry in icons)
        {
            if (entry.Root != null && entry.Root.activeSelf)
            {
                entry.Root.SetActive(false);
            }
        }
    }
}
