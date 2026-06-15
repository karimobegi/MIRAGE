using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ByteTrackCSharp;

/// <summary>
/// Parent class for PostProcessing effects that run on the CPU
/// Assumes that YOLO26 is used for object detection as it requires individual objects
/// 
/// </summary>
public abstract class CPUPostProcessor : MonoBehaviour, IEffectHandler
{

    [SerializeField] 
    protected PostProcessorSetting[] classSettings = new PostProcessorSetting[] {};
    protected Dictionary<int, List<PostProcessorSetting>> classSettingsMap = new Dictionary<int, List<PostProcessorSetting>>();

    [SerializeField]
    protected EffectType m_EffectType;
    public EffectType EffectType { get => m_EffectType; }

    [Tooltip("Location Relative to center: (0,0) is the center, (-1,-1) is the bottom left corner, (1,1) is the top right corner")]
    public Vector2 Location = new Vector2(0,0);

    [Tooltip("Offset in pixels (scaled)")]
    public Vector2 Offset = new Vector2(0, 0);
    
    public bool IsRunning = false;
    protected YOLOSegmentationRunner yolo26; //per-object detection is required for CPU Post processing effects
    protected DepthEstimationRunner depthEstimationRunner;
    protected RectTransform outputContainer;

    protected Vector2 referenceImageOrigin;
    protected int imgWidth;
    protected int imgHeight;
    protected float scalingFactorX;
    protected float scalingFactorY;
    protected Pipeline pipeline;

    #region Setup
        public virtual void Initialize(YOLOSegmentationRunner r, DepthEstimationRunner d, RectTransform outputContainer, Pipeline pipeline) {
            yolo26 = r;
            depthEstimationRunner = d;
            this.outputContainer = outputContainer;
            imgWidth = yolo26.OutputWidth;
            imgHeight = yolo26.OutputHeight;
            this.pipeline = pipeline;
            
        //  scalingFactorX = OutputContainer.sizeDelta.x / imgWidth;
        // scalingFactorY = OutputContainer.sizeDelta.y / imgHeight;
            
            scalingFactorX = outputContainer.rect.size.x / imgWidth;
            scalingFactorY = outputContainer.rect.size.y / imgHeight;
            referenceImageOrigin = outputContainer.pivot;
            
            UpdateClasses();
        }

        public void UpdateClasses(List<EffectSetting> classes)
        {
            classSettings = classes.Cast<PostProcessorSetting>().ToArray();
            UpdateClasses();
        }

        public virtual void UpdateClasses()
        {
            // Update dictionary for faster access
            classSettingsMap.Clear();
            
            if (classSettings.Length == 0) {
                var defaultSetting = new PostProcessorSetting(-2, Color.black);
                classSettingsMap.Add(defaultSetting.ClassID, new List<PostProcessorSetting> { defaultSetting });
            } else {
                foreach (var setting in classSettings) {
                    if (!classSettingsMap.ContainsKey(setting.ClassID)) {
                        classSettingsMap[setting.ClassID] = new List<PostProcessorSetting>();
                    }
                    classSettingsMap[setting.ClassID].Add(setting);
                }
            }
            
            IsRunning = classSettingsMap.Count > 0;
            OnClassesUpdated();
        }

        protected abstract void OnClassesUpdated();
    #endregion
    #region Effect Execution
        public void Execute() {
            if (!IsRunning) return;
            

            int activeCount = 0;
            try {
                foreach(LabelledSTrack track in pipeline.labelledTracks) {
                    if(IsValidObject(track.Label)) {
                        Vector2 position = CalculatePositionWithOffset(track);
                        Vector2 size = CalculateSize(track);
                        position = ApplyLocationOffset(position, size);

                        if(HasExistingObject(activeCount)) {
                            UpdateObject(activeCount, track, position, size);
                        }
                        else {
                            CreateObject(track, position, size);
                        }
                        activeCount++;
                    }
                }
            } catch (ObjectDisposedException) {
                return;
            }
        
    DeactivateRemainingObjects(activeCount);
        }
        // Abstract methods to be implemented by derived classes
        protected abstract bool HasExistingObject(int index);
        protected abstract void UpdateObject(int index, LabelledSTrack track, Vector2 position, Vector2 size);
        protected abstract void CreateObject(LabelledSTrack track, Vector2 position, Vector2 size);
        protected abstract void DeactivateRemainingObjects(int startIndex);
    #endregion
    #region Utility
        /// <summary>
        /// Calculate the position of the object in the reference image using its TrackID
        /// </summary>
        /// <param name="objectIndex"></param>
        /// <returns></returns>
        protected Vector2 CalculatePositionWithOffset(LabelledSTrack track) {
            var rect = track.getRect();
            var x = rect.x() * scalingFactorX + Offset.x * scalingFactorX;
            var y = rect.y() * scalingFactorY + Offset.y * scalingFactorY;
            return new Vector2(x, -y);
        }

    protected Vector2 CalculatePosition(LabelledSTrack track) {
            var rect = track.getRect();
            var x = rect.x() * scalingFactorX;
            var y = rect.y() * scalingFactorY;
            return new Vector2(x, -y);
        }


        /// <summary>
        /// Calculate the size of the object in the reference image using its trackID
        /// </summary>
        /// <param name="objectIndex"></param>
        /// <returns></returns>
        protected Vector2 CalculateSize(LabelledSTrack track) {
            var rect = track.getRect();
            var width = rect.width() * scalingFactorX;
            var height = rect.height() * scalingFactorY;
            return new Vector2(width, height);
        }
        protected Vector2 ApplyLocationOffset(Vector2 position, Vector2 size) {
            return position + new Vector2(size.x/2 * Location.x, size.y/2 * Location.y);
        }

        protected bool IsValidObject(int objectIndex) {
            int classId = yolo26.LabelIDs[objectIndex];
            float depth = depthEstimationRunner.DepthData[objectIndex];

            if (classSettingsMap.TryGetValue(classId, out List<PostProcessorSetting> settings)) {
                foreach (var setting in settings) {
                    if (depth >= setting.MinRange && depth <= setting.MaxRange) {
                        return true;
                    }
                }
            }
            return false;
        }

        protected Color GetColorForObject(LabelledSTrack track) {
            int classId = track.Label;
            float depth = depthEstimationRunner.DepthData[track.Detection];

            if (classSettingsMap.TryGetValue(classId, out List<PostProcessorSetting> settings)) {
                foreach (var setting in settings) {
                    if (depth >= setting.MinRange && depth <= setting.MaxRange) {
                        return setting.color;
                    }
                }
            }
            return Color.white; // Default color if no matching setting found
        }
    }
    #endregion
