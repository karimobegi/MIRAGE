using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using Mirage.Diagnostics;
/// <summary>
/// Simple Study Manager, holds partiicpant Id is responsible for logging
/// </summary>
public class StudyManager : MonoBehaviour
{
    public static StudyManager Instance;

    public int ParticipantID;
    public bool EnableLogging = true;
    public string LastLogPath { get; private set; }
    public string LastLogError { get; private set; }

    private List<ModelRunner> modelRunners = new List<ModelRunner>();

    void Start()
    {
        Instance = this;

        modelRunners = FindObjectsByType<ModelRunner>(FindObjectsSortMode.None).ToList();
    }


    public void LogSettings(Dictionary<EffectType, List<EffectSetting>> effectSettings, List<Toggle> LayerSettings) {
        if(!EnableLogging) return;
        try {
        
        // Create combined JSON object
        var combinedSettings = new JObject();
        combinedSettings["schemaVersion"] = 1;
        combinedSettings["timestampUtc"] = DateTime.UtcNow.ToString("O");
            
        // Serialize effect settings
        var effectSettingsJson = JObject.Parse(JsonConvert.SerializeObject(effectSettings, Formatting.Indented));
        combinedSettings["effects"] = effectSettingsJson;

        // Create layers object
        var layersObject = new JObject();
        foreach (var layer in LayerSettings ?? new List<Toggle>()) {
            if (layer == null) continue;
            layersObject[layer.gameObject.name] = layer.isOn;
        }
        combinedSettings["layers"] = layersObject;

        // Create model runners object
        var runnersObject = new JObject();
        foreach (var runner in modelRunners) {
            if (runner == null || !runner.enabled) continue;
            var runnerSettings = new JObject();
            runnerSettings["runnerType"] = runner.GetType().Name;
            runnerSettings["isEnabled"] = runner.IsEnabled;
            runnerSettings["updateRate"] = runner.UpdateRate;
            runnerSettings["modelName"] = runner.ModelAsset != null ? runner.ModelAsset.name : "external";
            runnerSettings["inputWidth"] = runner.InputWidth;
            runnerSettings["inputHeight"] = runner.InputHeight;
            runnerSettings["outputWidth"] = runner.OutputWidth;
            runnerSettings["outputHeight"] = runner.OutputHeight;
            runnersObject[runner.gameObject.name] = runnerSettings;
        }
        combinedSettings["modelRunners"] = runnersObject;

        // Save to file
        string jsonData = combinedSettings.ToString(Formatting.Indented);

        // Create directory if it doesn't exist
        string directoryPath = Path.Combine(Application.persistentDataPath, "MIRAGE", "StudyLogs");
        Directory.CreateDirectory(directoryPath);

        // Generate filename with participant ID and timestamp
        string fileName = $"{ParticipantID}_{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}.json";
        string filePath = Path.Combine(directoryPath, fileName);

        File.WriteAllText(filePath, jsonData);
        LastLogPath = filePath;
        LastLogError = null;
        MirageDiagnostics.Record("study.settings.saved", "Study settings were recorded locally.");
        } catch (Exception exception) {
            LastLogError = "Study settings could not be saved. Check the storage location and available disk space.";
            MirageDiagnostics.Record("study.settings.write_failed", LastLogError, new { reason = exception.Message }, "error");
        }
    }

}
