using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// PostProcessor that displays object information as text
/// 
///  Author: J-Britten
/// </summary>
public class ObjectInfoPostProcessor : CPUPostProcessor {

    public GameObject prefab;
    private TextAsset classes; //get classes from YOLO for formatting
    private List<TMP_Text> textObjects = new List<TMP_Text>();
    private string[] classNames;

    public override void Initialize(YOLOSegmentationRunner r, DepthEstimationRunner d, RectTransform op)
    {
        base.Initialize(r, d, op);
        classes = r.TextAsset; //get classes from YOLO for formatting
        classNames = classes.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    protected override bool HasExistingObject(int index) {
        return textObjects.Count > index;
    }

    protected override void UpdateObject(int index, int objectIndex, Vector2 position, Vector2 size) {
        textObjects[index].GetComponent<RectTransform>().anchoredPosition = position;
        textObjects[index].gameObject.SetActive(true);
        textObjects[index].text = GetLabelText(objectIndex);
        textObjects[index].color = GetColorForObject(objectIndex);
    }

    protected override void CreateObject(int objectIndex, Vector2 position, Vector2 size) {
        GameObject obj = Instantiate(prefab, outputContainer.transform);
        obj.GetComponent<RectTransform>().anchoredPosition = position;
        TMP_Text text = obj.GetComponentInChildren<TMP_Text>();
        text.text = GetLabelText(objectIndex);
        text.color = GetColorForObject(objectIndex);
        textObjects.Add(text);
    }

    protected override void DeactivateRemainingObjects(int startIndex) {
        for(int i = startIndex; i < textObjects.Count; i++) {
            textObjects[i].gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Parse the label text for the object
    /// </summary>
    /// <param name="objectIndex"></param>
    /// <returns></returns>
    private string GetLabelText(int objectIndex) {
        int labelId = yolo26.LabelIDs[objectIndex];
        string className = labelId < classNames.Length ? classNames[labelId] : labelId.ToString();
        return $"{className}\n{depthEstimationRunner.DepthData[objectIndex]:0.00}m";
    }

    protected override void OnClassesUpdated()
    {
        // Clear or update existing text objects as needed
        foreach (var textObject in textObjects)
        {
            if (textObject != null)
            {
                textObject.gameObject.SetActive(false);
            }
        }
    }
}