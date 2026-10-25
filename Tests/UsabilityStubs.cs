// Explicit seams for source-level UI/diagnostics checks. No real Unity API claim.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityEngine
{
    public enum RuntimeInitializeLoadType { SubsystemRegistration,BeforeSceneLoad,AfterSceneLoad }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType phase) { } }
    public sealed class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int value) { } }
    public enum LogType { Error,Exception,Assert,Warning,Log }
    public enum RuntimePlatform { WindowsPlayer }
    public static partial class Application
    {
        public static string persistentDataPath=Path.Combine(Path.GetTempPath(),"mirage-usability-test");
        public static string dataPath=Path.Combine(persistentDataPath,"Assets");
        public static string productName="MIRAGE",version="test",unityVersion="stub";
        public static RuntimePlatform platform=RuntimePlatform.WindowsPlayer;
        public static string LastOpenedUrl;
        public static event Action<string,string,LogType> logMessageReceivedThreaded;
        public static void OpenURL(string url) { LastOpenedUrl=url; }
        public static void Quit() { }
        public static void EmitLog(string message,string stack,LogType type)=>logMessageReceivedThreaded?.Invoke(message,stack,type);
    }
    public static partial class SystemInfo
    {
        public static string graphicsDeviceName="test-gpu",graphicsDeviceType="test-api",processorType="test-cpu";
        public static int graphicsMemorySize=6144,processorCount=4,systemMemorySize=16384;
    }
    public static class Screen {public static int width=1280,height=720;}
    public enum KeyCode { F1 }
    public static class Input {public static bool GetKeyDown(KeyCode key)=>false;}
    public static class PlayerPrefs
    {
        private static readonly Dictionary<string,int> Values=new Dictionary<string,int>();
        public static int GetInt(string key,int fallback=0)=>Values.TryGetValue(key,out int value)?value:fallback;
        public static void SetInt(string key,int value) {Values[key]=value;}
        public static void Save() { }
    }
    public enum FontStyle { Normal,Bold }
    public class GUIStyle {public bool wordWrap;public FontStyle fontStyle;public GUIStyle(GUIStyle other=null) { }}
    public class GUISkin {public GUIStyle label=new GUIStyle();}
    public static class GUI
    {
        public static bool enabled=true;
        public static GUISkin skin=new GUISkin();
        public static bool Button(Rect rect,string label)=>false;
        public static Rect ModalWindow(int id,Rect rect,Action<int> action,string title) {action(id);return rect;}
        public static void DragWindow(Rect rect) { }
    }
    public class GUILayoutOption { }
    public static class GUILayout
    {
        public static int Toolbar(int selected,string[] labels)=>selected;
        public static Vector2 BeginScrollView(Vector2 value)=>value;
        public static void EndScrollView() { }
        public static bool Button(string label,params GUILayoutOption[] options)=>false;
        public static string TextField(string value,int maximum)=>value;
        public static int SelectionGrid(int selected,string[] labels,int columns)=>selected;
        public static void Label(string text,GUIStyle style) { }
        public static void Space(float amount) { }
        public static void BeginHorizontal() { }
        public static void EndHorizontal() { }
        public static GUILayoutOption Height(float value)=>new GUILayoutOption();
    }
}
namespace UnityEngine.EventSystems {public class BaseInputModule : MonoBehaviour { }}
namespace UnityEngine.UI {public class Toggle : UnityEngine.Object {public bool isOn;public GameObject gameObject=new GameObject();}}

// The real preset UI has a separate actual-source test harness. This seam only
// lets the operator panel compile/call its public contract.
public class PipelineUIHandler : MonoBehaviour
{
    public static PipelineUIHandler Instance;
    public bool IsReady=true;
    public string LastPresetError, PresetDirectory=Path.Combine(Application.persistentDataPath,"MIRAGE","Presets");
    public string[] GetPresetNames()=>new[]{"Outline people"};
    public bool SavePreset(string name)=>true;
    public bool LoadPreset(string name)=>true;
    public void ToggleUI(bool visible) { }
}
namespace UnityEditor
{
    public static partial class EditorUtility
    {
        public static string LastDialog;
        public static bool DisplayDialog(string title,string message,string okay) {LastDialog=message;return true;}
        public static string SaveFolderPanel(string title,string folder,string name)=>"";
        public static void RevealInFinder(string folder) { }
    }
    public static class BuildPipeline
    {
        public static bool IsBuildTargetSupported(BuildTargetGroup group,BuildTarget target)=>true;
        public static Build.Reporting.BuildReport BuildPlayer(BuildPlayerOptions options)=>new Build.Reporting.BuildReport();
    }
    public enum BuildTargetGroup { Standalone }
    public enum BuildTarget { StandaloneWindows64 }
    public enum BuildOptions { None }
    public struct BuildPlayerOptions {public string[] scenes;public string locationPathName;public BuildTarget target;public BuildOptions options;}
}
namespace UnityEditor.Build.Reporting
{
    public enum BuildResult { Succeeded,Failed }
    public class BuildReport {public BuildSummary summary=new BuildSummary();}
    public class BuildSummary {public BuildResult result=BuildResult.Succeeded;}
}
namespace UnityEditor.SceneManagement
{
    public static partial class EditorSceneManager
    {
        public static bool SaveCurrentModifiedScenesIfUserWantsTo()=>true;
        public static void OpenScene(string path) { }
    }
}
