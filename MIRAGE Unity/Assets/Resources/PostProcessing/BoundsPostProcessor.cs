using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ByteTrackCSharp;


/// <summary>
/// A variant of the <see cref="CPUPostProcessor"/> that creates a 2D overlay on top of the detected objects.
/// 
/// This class is used for the Bounding Box Post Processing effect.
/// We also use it for the 2D Replace Post Processing effect
/// 
/// Author: J-Britten
/// </summary>
public class BoundsPostProcessor : CPUPostProcessor
{
#region Variables

    /// <summary>
    /// Whether to maintain the aspect ratio of the image when scaling.
    /// If true, the image will be scaled based on the height of the bounding box.
    /// However, this means the image may not fill the entire bounds of the objects.
    /// </summary>
    public bool MaintainImageAspectRatio = false;

    /// <summary>
    /// Whether to scale the image based on the height of the bounding box.
    /// False = Width based scaling.
    /// This is only used if MaintainImageAspectRatio is true.
    /// </summary>
    public bool ScaleBasedOnHeight = true;

    /// <summary>
    /// The prefab used to create the image objects.
    /// This prefab should have a RectTransform and an Image component.
    /// </summary>
    private readonly struct PooledBounds
    {
        public readonly GameObject Root;
        public readonly RectTransform Rect;
        public readonly Image Image;

        public PooledBounds(GameObject root)
        {
            Root = root;
            Rect = root.GetComponent<RectTransform>();
            Image = root.GetComponent<Image>();
        }
    }

    public GameObject ImagePrefab;
    private readonly List<PooledBounds> imageObjects = new List<PooledBounds>();

    /// <summary>
    /// Aspect ratio of the image prefab.
    /// </summary>
    private float imageAspectRatio = 1f; // Default aspect ratio

#endregion
#region Setup
    public override void Initialize(YOLOSegmentationRunner r, DepthEstimationRunner d, RectTransform outputContainer, Pipeline p) {
        base.Initialize(r, d, outputContainer, p);

        // Calculate aspect ratio of the boxPrefab
        if (ImagePrefab != null) {
            var rect = ImagePrefab.GetComponent<RectTransform>();
            if (rect != null && rect.sizeDelta.y != 0) {
                imageAspectRatio = rect.sizeDelta.x / rect.sizeDelta.y;
            }
        }
    }
#endregion
#region Effect Execution

    protected override bool HasExistingObject(int index) {
        return imageObjects.Count > index;
    }

    protected override void UpdateObject(int index, LabelledSTrack track, Vector2 position, Vector2 size) {
        var entry = imageObjects[index];

        // Adjust size based on aspect ratio setting
        if (MaintainImageAspectRatio) {
            if (ScaleBasedOnHeight) {
                size.x = size.y * imageAspectRatio;
            } else {
                size.y = size.x / imageAspectRatio;
            }
        }

        if (entry.Rect != null) {
            entry.Rect.sizeDelta = size;
            entry.Rect.anchoredPosition = position;
        }
        if (entry.Image != null) entry.Image.color = GetColorForObject(track);
        if (entry.Root != null && !entry.Root.activeSelf) entry.Root.SetActive(true);
    }

    protected override void CreateObject(LabelledSTrack track, Vector2 position, Vector2 size) {
        GameObject box = Instantiate(ImagePrefab, outputContainer.transform);
        var entry = new PooledBounds(box);
        imageObjects.Add(entry);

        // Adjust size based on aspect ratio setting
        if (MaintainImageAspectRatio) {
            if (ScaleBasedOnHeight) {
                size.x = size.y * imageAspectRatio;
            } else {
                size.y = size.x / imageAspectRatio;
            }
        }

        if (entry.Rect != null) {
            entry.Rect.sizeDelta = size;
            entry.Rect.anchoredPosition = position;
        }
        if (entry.Image != null) entry.Image.color = GetColorForObject(track);
    }

    protected override void DeactivateRemainingObjects(int startIndex) {
        for(int i = startIndex; i < imageObjects.Count; i++) {
            if (imageObjects[i].Root != null && imageObjects[i].Root.activeSelf)
                imageObjects[i].Root.SetActive(false);
        }
    }

#endregion
    protected override void OnClassesUpdated()
    {
        foreach (var entry in imageObjects)
        {
            if (entry.Root != null && entry.Root.activeSelf)
            {
                entry.Root.SetActive(false);
            }
        }
    }
}
