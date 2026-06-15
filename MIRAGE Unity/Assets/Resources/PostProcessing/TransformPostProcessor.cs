using System.Collections;
using System.Collections.Generic;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Experimental.Rendering;
using ByteTrackCSharp;
/// <summary>
/// PostProcessor that applies a Translate, Rotate, and/or Scale effect the output of the ImageCopyPostProcessor and applies it to a RawImage.
/// 
/// This way individual objects can be upscaled or downscaled.
/// 
/// This effect is very experimental and causes various errors
/// 
/// Author: J-Britten
/// </summary>
[RequireComponent(typeof(ImageCopyPostProcessor))]
public class TransformPostProcessor : CPUPostProcessor
{

    public float ScalingFactor = 1.0f;

    public Vector3 Rotation = Vector3.zero;
    public GameObject RawImagePrefab;
    // Dictionary to track textures assigned to each RawImage
    
    private List<GameObject> imageObjects = new List<GameObject>();
    private Dictionary<RawImage, Texture2D> imageTextures = new Dictionary<RawImage, Texture2D>();


    private ImageCopyPostProcessor imageCopyPostProcessor;
    
    
    public override void Initialize(YOLOSegmentationRunner r, DepthEstimationRunner d, RectTransform outputContainer, Pipeline p)
    {
        base.Initialize(r, d, outputContainer, p);

        imageCopyPostProcessor = gameObject.GetComponent<ImageCopyPostProcessor>();

    }


    // Method to cut a region from a RenderTexture and assign it to a RawImage
    public void ApplyRegionToRawImage(RenderTexture source, UnityEngine.Rect region, RawImage targetImage)
    {
        try {
            // Check if target image is still valid
            if (targetImage == null || source == null)
            {
                return;
            }
            
            // Check if we already have a texture for this target
            Texture2D tempTexture;
            if (imageTextures.TryGetValue(targetImage, out tempTexture))
            {
                // If texture was destroyed or size has changed, create a new one
                if (tempTexture == null || tempTexture.width != (int)region.width || tempTexture.height != (int)region.height)
                {
                    if (tempTexture != null)
                    {
                        UnityEngine.Object.Destroy(tempTexture);
                    }
                    tempTexture = new Texture2D((int)region.width, (int)region.height, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
                    imageTextures[targetImage] = tempTexture;
                }
            }
            else
            {
                // Create a new texture if we don't have one yet
                tempTexture = new Texture2D((int)region.width, (int)region.height, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
                imageTextures.Add(targetImage, tempTexture);
            }

            // Set the active RenderTexture and read pixels from the specified region
            RenderTexture.active = source;
            tempTexture.ReadPixels(region, 0, 0);
            tempTexture.Apply();

            RenderTexture.active = null;

            // Assign the texture to the RawImage if it still exists
            if (targetImage != null)
            {
                targetImage.texture = tempTexture;
            }
        } catch (System.Exception e)
        {
            Debug.LogError("Error applying region to RawImage: " + e.Message);
        }
    }

    // Clean up textures when the component is disabled
    protected void OnDisable()
    {
        CleanupTextures();
    }

    // Clean up textures when the component is destroyed
    protected void OnDestroy()
    {
        CleanupTextures();
    }

    // Helper method to clean up textures
    private void CleanupTextures()
    {
        // Create a list to store keys for removal to avoid modifying the dictionary during iteration
        List<RawImage> keysToRemove = new List<RawImage>();
        
        // Destroy all textures we've created
        foreach (var pair in imageTextures)
        {
            if (pair.Value != null)
            {
                UnityEngine.Object.Destroy(pair.Value);
            }
            keysToRemove.Add(pair.Key);
        }
        
        // Remove all entries from the dictionary
        foreach (var key in keysToRemove)
        {
            imageTextures.Remove(key);
        }
    }

    protected override void CreateObject(LabelledSTrack track, Vector2 position, Vector2 size)
    {
        GameObject go = Instantiate(RawImagePrefab, outputContainer.transform);
        imageObjects.Add(go);

        UpdateObject(imageObjects.Count - 1, track, position, size);
    }

    protected override void DeactivateRemainingObjects(int startIndex)
    {
      for(int i = startIndex; i < imageObjects.Count; i++)
        {
            imageObjects[i].SetActive(false);
        }
    }

    protected override bool HasExistingObject(int index)
    {
        return imageObjects.Count > index;
    }

    protected override void OnClassesUpdated()
    {
        foreach(var obj in imageObjects)
        { if(obj != null)
            {
                obj.SetActive(false);
            }
        }
    }

    protected override void UpdateObject(int index, LabelledSTrack track, Vector2 position, Vector2 size)
    {
        if (index >= imageObjects.Count || imageObjects[index] == null)
        {
            return;
        }
        
        var rawImage = imageObjects[index].GetComponent<RawImage>();
        if (rawImage == null)
        {
            return;
        }
        
        var rect = imageObjects[index].GetComponent<RectTransform>();
        if (rect == null)
        {
            return;
        }
        
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        
        // Check if the OutputTexture exists
        if (imageCopyPostProcessor == null || imageCopyPostProcessor.OutputTexture == null)
        {
            return;
        }
        
        // Get the height of the source image/texture
        float imgHeight = imageCopyPostProcessor.OutputTexture.height;
        
        Vector2 rawPos = CalculatePosition(track);
        // Calculate the top-left corner of the region from the center position
        float regionX = rawPos.x - (size.x / 2);
        // Y position is calculated from the top of the image and needs to be inverted
        float regionY = (imgHeight + rawPos.y) - (size.y / 2);
        
        // Apply the region to the RawImage using the corrected coordinates
        UnityEngine.Rect region = new UnityEngine.Rect(regionX, regionY, size.x, size.y);
        ApplyRegionToRawImage(imageCopyPostProcessor.OutputTexture, region, rawImage);

        rect.localScale = new Vector3(ScalingFactor, ScalingFactor, 1f);

        rect.rotation = Quaternion.Euler(Rotation.x, Rotation.y, Rotation.z);

    }
}
