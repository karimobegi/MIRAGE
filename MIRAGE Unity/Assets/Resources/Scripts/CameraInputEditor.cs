using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if UNITY_EDITOR
[CustomEditor(typeof(CameraInput))]
public class CameraInputEditor : Editor
{
    private string[] cameraOptions;
    private int selectedIndex = -1;

    void OnEnable()
    {
        UpdateCameraList();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Camera Settings", EditorStyles.boldLabel);
        
        if (GUILayout.Button("Refresh Camera List"))
        {
            UpdateCameraList();
        }

        if (cameraOptions != null && cameraOptions.Length > 0)
        {
            EditorGUI.BeginChangeCheck();
            selectedIndex = EditorGUILayout.Popup("Select Camera", selectedIndex, cameraOptions);
            if (EditorGUI.EndChangeCheck() && selectedIndex >= 0)
            {
                var prop = serializedObject.FindProperty("selectedCameraDeviceName");
                prop.stringValue = WebCamTexture.devices[selectedIndex].name;
                serializedObject.ApplyModifiedProperties();
            }
        }
        else
        {
            EditorGUILayout.HelpBox("No cameras found", MessageType.Warning);
        }

        EditorGUILayout.Space();
        DrawPropertiesExcluding(serializedObject, "selectedCameraDeviceName");
        
        serializedObject.ApplyModifiedProperties();
    }

    private void UpdateCameraList()
    {
        var devices = WebCamTexture.devices;
        cameraOptions = new string[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            cameraOptions[i] = devices[i].name;
        }

        // Find current camera in list
        var prop = serializedObject.FindProperty("selectedCameraDeviceName");
        selectedIndex = -1;
        for (int i = 0; i < devices.Length; i++)
        {
            if (devices[i].name == prop.stringValue)
            {
                selectedIndex = i;
                break;
            }
        }
        if (selectedIndex == -1 && devices.Length > 0)
        {
            selectedIndex = 0;
            prop.stringValue = devices[0].name;
        }
    }
}
#endif