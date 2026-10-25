using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mirage.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mirage.Usability
{
    /// <summary>Desktop first-run help and recovery, available in prepared players without the Editor.</summary>
    public sealed class MirageOperatorPanel : MonoBehaviour
    {
        public static bool IsVisible { get; private set; }
        private const string SeenKey = "MIRAGE.OperatorHelp.Seen.v1";
        private bool visible;
        private int tab;
        private Rect window;
        private Vector2 scroll;
        private string videoPath = "", presetName = "My preset", feedback = "";
        private string[] cameras = Array.Empty<string>(), presets = Array.Empty<string>();
        private int cameraIndex;
        private float nextRefresh;
        private string lastReport = "";
        private List<MirageSetupIssue> issues = new List<MirageSetupIssue>();
        private MirageHealthSnapshot health;
        private readonly List<BaseInputModule> suspendedInput = new List<BaseInputModule>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<MirageOperatorPanel>() != null) return;
            var host = new GameObject("MIRAGE Help and Setup");
            DontDestroyOnLoad(host);
            host.AddComponent<MirageOperatorPanel>();
        }

        private IEnumerator Start()
        {
            yield return null;
            window = new Rect(20, 52, Mathf.Min(760, Screen.width - 40), Mathf.Min(690, Screen.height - 72));
            RefreshCameras();
            Refresh();
            SetVisible(PlayerPrefs.GetInt(SeenKey, 0) == 0 || issues.Any(issue => issue.Blocking));
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) SetVisible(!visible);
            if (visible && Time.realtimeSinceStartup >= nextRefresh)
            {
                Refresh();
                nextRefresh = Time.realtimeSinceStartup + 0.5f;
            }
        }

        private void Refresh()
        {
            issues = MirageSetupReport.Check();
            health = MirageDiagnostics.GetHealthSnapshot();
            var ui = GetEffectsUi();
            if (visible && tab == 2) presets = ui != null && ui.IsReady ? ui.GetPresetNames() : Array.Empty<string>();
            string report = string.Join("|", issues.Select(issue => issue.Code + ":" + issue.Message));
            if (report != lastReport)
            {
                lastReport = report;
                MirageDiagnostics.Record("setup.status", issues.Any(issue => issue.Blocking) ? "Setup needs attention." : "Setup checks completed.",
                    issues.Select(issue => new { code = issue.Code, message = issue.Message, remedy = issue.Remedy, blocking = issue.Blocking }).ToArray());
            }
            if (visible) SuspendInput();
        }

        private void OnGUI()
        {
            if (GUI.Button(new Rect(Mathf.Max(8, Screen.width - 180), 10, 168, 32), "Help & setup (F1)")) SetVisible(!visible);
            if (!visible) return;
            window.width = Mathf.Min(760, Mathf.Max(280, Screen.width - 24));
            window.height = Mathf.Min(690, Mathf.Max(240, Screen.height - 64));
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 44, Mathf.Max(44, Screen.height - window.height));
            window = GUI.ModalWindow(0x4D4952, window, DrawWindow, "MIRAGE — Help & setup");
        }

        private void DrawWindow(int id)
        {
            int selectedTab = GUILayout.Toolbar(tab, new[] { "Start here", "Input", "Presets", "Status & support" });
            if (selectedTab != tab) { tab = selectedTab; scroll = new Vector2(0, 0); feedback = ""; Refresh(); }
            scroll = GUILayout.BeginScrollView(scroll);
            if (tab == 0) DrawStart();
            else if (tab == 1) DrawInput();
            else if (tab == 2) DrawPresets();
            else DrawSupport();
            if (!string.IsNullOrEmpty(feedback)) { GUILayout.Space(12); Label(feedback); }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Close help and return to MIRAGE", GUILayout.Height(32)))
            {
                PlayerPrefs.SetInt(SeenKey, 1);
                PlayerPrefs.Save();
                SetVisible(false);
            }
            if (GUILayout.Button("Quit MIRAGE", GUILayout.Height(32)))
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, window.width, 20));
        }

        private void DrawStart()
        {
            Label("Start with a recording", true);
            Label("1. Open Input and choose a video recording. You can pause or restart it there.");
            Label("2. Load Outline people below, or open the effects panel and choose Person or Car, then + and Outline.");
            Label("3. Press Esc to apply the effects panel changes. Keep the Base and Augmented Reality layers visible.");
            Label("4. Save a named preset when the result looks right. It can be reused in another session or copied to another prepared computer.");
            var ui = GetEffectsUi();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = ui != null && ui.IsReady;
            if (GUILayout.Button("Load Outline people", GUILayout.Height(30)))
                feedback = ui.LoadPreset("Outline people") ? "Outline people loaded. Choose a recording in Input." : ui.LastPresetError;
            if (GUILayout.Button("Open the effects panel", GUILayout.Height(30)))
            {
                ui.ToggleUI(true);
                SetVisible(false);
            }
            GUI.enabled = wasEnabled;
            GUILayout.Space(10);
            Label("If you cannot see an effect", true);
            Label("Check that the matching object is visible and the relevant layer is on. Start with the full range and an opaque outline color. Depth values are relative unless a metric setup was explicitly calibrated.");
            Label("Remove and Opacity also need the inpainting model. Moebius is an optional quality experiment and can take several seconds for one image.");
            if (GUILayout.Button("Check setup and get help")) tab = 3;
        }

        private void DrawInput()
        {
            var source = CameraInput.Instance;
            if (source == null) { Label("This scene has no input component. Ask the maintainer to prepare Desktop UI."); return; }
            Label(source.UseDebugVideoInput ? "Current input: recording" : "Current input: camera", true);
            if (!string.IsNullOrEmpty(source.LastInputError)) Label(source.LastInputError);
            if (GUILayout.Button("Choose video...", GUILayout.Height(32)))
            {
                if (VideoFilePicker.TryChoose(out string path, out string error)) OpenVideo(source, path);
                else if (!string.IsNullOrEmpty(error)) feedback = error;
            }
            Label("Or paste a local video path:");
            videoPath = GUILayout.TextField(videoPath, 4096);
            if (GUILayout.Button("Open recording")) OpenVideo(source, videoPath);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Pause / resume")) source.PauseOrResumeVideo();
            if (GUILayout.Button("Restart recording")) source.RestartVideo();
            if (GUILayout.Button("Stop input")) source.StopInput();
            GUILayout.EndHorizontal();
            GUILayout.Space(14);
            Label("Use a connected camera", true);
            if (GUILayout.Button("Refresh cameras")) RefreshCameras();
            if (cameras.Length == 0) Label("No camera was found. Connect it and refresh. If it is busy, close the app using it.");
            else
            {
                cameraIndex = GUILayout.SelectionGrid(cameraIndex, cameras, 1);
                if (GUILayout.Button("Use selected camera", GUILayout.Height(30)))
                    feedback = source.TryStartCamera(cameras[Mathf.Clamp(cameraIndex, 0, cameras.Length - 1)], out string error)
                        ? "Camera selected. Waiting for its first frame." : error;
            }
            Label("Video decoding depends on the computer. If a file will not open, try an H.264 MP4 recording.");
        }

        private void OpenVideo(CameraInput source, string path)
        {
            videoPath = path ?? "";
            feedback = source.TryStartVideoFile(videoPath, out string error) ? "Opening the recording..." : error;
        }

        private void RefreshCameras()
        {
            cameras = WebCamTexture.devices.Select(device => device.name).ToArray();
            cameraIndex = Mathf.Clamp(cameraIndex, 0, Mathf.Max(0, cameras.Length - 1));
        }

        private void DrawPresets()
        {
            Label("Save and reuse an effect setup", true);
            var ui = GetEffectsUi();
            if (ui == null || !ui.IsReady) { Label("Waiting for the effects panel to finish loading."); return; }
            Label("Save captures the current effect cards, colors, ranges, layers and model update settings. Load replaces the current setup.");
            Label("Saving an existing name updates that preset.");
            if (!string.IsNullOrEmpty(ui.LastPresetError)) Label("Preset message: " + ui.LastPresetError);
            presetName = GUILayout.TextField(presetName, 80);
            if (GUILayout.Button("Save preset", GUILayout.Height(30)))
            {
                feedback = ui.SavePreset(presetName) ? "Preset saved." : ui.LastPresetError;
                Refresh();
            }
            GUILayout.Space(10);
            Label("Available presets", true);
            foreach (string name in presets)
                if (GUILayout.Button("Load: " + name)) feedback = ui.LoadPreset(name) ? "Loaded " + name + "." : ui.LastPresetError;
            if (presets.Length == 0) Label("No presets have been saved yet.");
            if (GUILayout.Button("Open preset folder")) OpenFolder(ui.PresetDirectory);
            Label("To share a setup, copy its JSON file into the other computer's preset folder, then reopen this tab. The other computer needs its own prepared MIRAGE models and input.");
        }

        private void DrawSupport()
        {
            bool blocked = issues.Any(issue => issue.Blocking);
            Label(blocked ? "Setup needs attention" : "Setup checks", true);
            if (issues.Count == 0) Label("The scene's model and image-size checks passed.");
            foreach (var issue in issues)
            {
                Label((issue.Blocking ? "Needs attention: " : "Note: ") + issue.Message);
                Label(issue.Remedy + " [" + issue.Code + "]");
                GUILayout.Space(6);
            }
            if (health != null)
            {
                Label("Session status", true);
                Label($"Input: {health.SourceState}. Processed updates: {health.PublishedFramesPerSecond:F1} per second.");
                if (health.PublishedFrameId < 0) Label("No processed image has been displayed yet.");
                else Label($"The displayed image is {health.FrameAgeSeconds:F1} s old. Time from receiving that image to this result: {health.LatestPublicationLatencyMilliseconds:F0} ms.");
                if (health.InputStalled) Label("No new input frames are arriving. Check playback, the camera connection or the selected input.");
                if (health.ProcessingStalled) Label("Processing has not produced a new frame recently. Check setup above; Moebius quality mode can take longer.");
                if (!string.IsNullOrEmpty(health.LatestErrorMessage)) Label("Last reported error: " + health.LatestErrorMessage);
            }
            var ui = GetEffectsUi();
            if (ui != null && !string.IsNullOrEmpty(ui.LastPresetError))
                Label("The last preset operation needs attention: " + ui.LastPresetError + " Open Presets to choose another setup.");
            GUILayout.Space(10);
            Label("Support logs", true);
            Label(MirageDiagnostics.LoggingAvailable ? "A local diagnostic session is being recorded." : "Diagnostic logging is unavailable: " + MirageDiagnostics.LastError);
            Label("The support ZIP includes diagnostic metadata and logs. It does not include video frames, recordings, or study settings. Review the ZIP before sharing it.");
            if (GUILayout.Button("Open logs folder")) feedback = MirageDiagnostics.OpenLogFolder() ? "Log folder opened." : MirageDiagnostics.LastError ?? "The log folder is not available yet.";
            if (GUILayout.Button("Export support ZIP", GUILayout.Height(32)))
            {
                if (MirageDiagnostics.TryExportSupportBundle(out string archive, out string error))
                {
                    feedback = "Support ZIP created: " + Path.GetFileName(archive);
                    OpenFolder(Path.GetDirectoryName(archive));
                }
                else feedback = error;
            }
            if (GUILayout.Button("Refresh setup checks")) Refresh();
            if (StudyManager.Instance != null)
            {
                GUILayout.Space(8);
                Label("Study settings are stored separately and are excluded from support ZIPs.");
                if (GUILayout.Button("Open study settings folder"))
                    OpenFolder(Path.Combine(Application.persistentDataPath, "MIRAGE", "StudyLogs"));
            }
        }

        private static PipelineUIHandler GetEffectsUi()
        {
            return PipelineUIHandler.Instance != null ? PipelineUIHandler.Instance :
                FindObjectsByType<PipelineUIHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Application.OpenURL(new Uri(Path.GetFullPath(path) + Path.DirectorySeparatorChar).AbsoluteUri);
            }
            catch (Exception error) { feedback = "The folder could not be opened: " + error.Message; }
        }

        private void SetVisible(bool value)
        {
            visible = value;
            IsVisible = value;
            if (visible)
            {
                Refresh();
                // Prevent clicks in this desktop help window reaching the effect UI underneath it.
                SuspendInput();
            }
            else RestoreInput();
        }

        private void SuspendInput()
        {
            foreach (var module in FindObjectsByType<BaseInputModule>(FindObjectsSortMode.None))
                if (module.enabled) { suspendedInput.Add(module); module.enabled = false; }
        }

        private void RestoreInput()
        {
            foreach (var module in suspendedInput) if (module != null) module.enabled = true;
            suspendedInput.Clear();
        }

        private static void Label(string text, bool bold = false)
        {
            var style = new GUIStyle(GUI.skin.label) { wordWrap = true, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal };
            GUILayout.Label(text, style);
        }

        private void OnEnable() { IsVisible = visible; }
        private void OnDisable() { IsVisible = false; RestoreInput(); }
        private void OnDestroy() { IsVisible = false; RestoreInput(); }
    }
}
