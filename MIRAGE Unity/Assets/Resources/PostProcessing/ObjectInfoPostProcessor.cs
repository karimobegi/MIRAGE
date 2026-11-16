using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ByteTrackCSharp;

/// <summary>
/// PostProcessor that displays object information as text
/// 
///  Author: J-Britten
/// </summary>
public class ObjectInfoPostProcessor : CPUPostProcessor {

    private readonly struct PooledLabel
    {
        public readonly GameObject Root;
        public readonly RectTransform Rect;
        public readonly TMP_Text Text;

        public PooledLabel(GameObject root, TMP_Text text)
        {
            Root = text != null ? text.gameObject : root;
            Rect = text != null ? text.GetComponent<RectTransform>() : root.GetComponent<RectTransform>();
            Text = text;
        }
    }

    public GameObject prefab;
    private TextAsset classes; //get classes from YOLO for formatting
    private readonly List<PooledLabel> textObjects = new List<PooledLabel>();
    private string[] classNames;

    public override void Initialize(YOLOSegmentationRunner r, DepthEstimationRunner d, RectTransform op, Pipeline p)
    {
        base.Initialize(r, d, op, p);
        classes = r.TextAsset; //get classes from YOLO for formatting
        classNames = classes.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    protected override bool HasExistingObject(int index) {
        return textObjects.Count > index;
    }

    protected override void UpdateObject(int index, LabelledSTrack track, Vector2 position, Vector2 size) {
        var entry = textObjects[index];
        if (entry.Rect != null) entry.Rect.anchoredPosition = position;
        if (entry.Root != null && !entry.Root.activeSelf) entry.Root.SetActive(true);
        if (entry.Text != null) {
            entry.Text.text = GetLabelText(track);
            entry.Text.color = GetColorForObject(track);
        }
    }

    protected override void CreateObject(LabelledSTrack track, Vector2 position, Vector2 size) {
        GameObject obj = Instantiate(prefab, outputContainer.transform);
        obj.GetComponent<RectTransform>().anchoredPosition = position;
        TMP_Text text = obj.GetComponentInChildren<TMP_Text>();
        var entry = new PooledLabel(obj, text);
        if (entry.Rect != null) entry.Rect.anchoredPosition = position;
        if (entry.Text != null) {
            entry.Text.text = GetLabelText(track);
            entry.Text.color = GetColorForObject(track);
        }
        textObjects.Add(entry);
    }

    protected override void DeactivateRemainingObjects(int startIndex) {
        for(int i = startIndex; i < textObjects.Count; i++) {
            if (textObjects[i].Root != null && textObjects[i].Root.activeSelf)
                textObjects[i].Root.SetActive(false);
        }
    }

    /// <summary>
    /// Parse the label text for the object
    /// </summary>
    /// <param name="objectIndex"></param>
    /// <returns></returns>
    private string GetLabelText(LabelledSTrack track) {
        int labelId = track.Label;
        string className = labelId >= 0 && labelId < classNames.Length ? classNames[labelId] : labelId.ToString();
        if (!depthEstimationRunner.TryGetDepth(track.Detection, out float depth))
            return $"{className}\nDepth unavailable";
        string value = depthEstimationRunner.UsesMetricDepth
            ? $"{depth:0.00} m"
            : $"{depth:0.00} (relative)";
        return $"{className}\n{value}";
    }

    protected override void OnClassesUpdated()
    {
        // Clear or update existing text objects as needed
        foreach (var entry in textObjects)
        {
            if (entry.Root != null && entry.Root.activeSelf)
            {
                entry.Root.SetActive(false);
            }
        }
    }
}
