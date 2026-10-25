using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Mirage.Diagnostics;
using Mirage.Usability;
using UnityEngine;

public static class MirageUsabilityChecks
{
    private static void Check(bool condition,string message) {if(!condition) throw new Exception(message);}

    public static string Run(string root)
    {
        Application.persistentDataPath=root;
        Application.dataPath=Path.Combine(root,"Assets");
        MirageDiagnostics.Initialize();
        try
        {
            SourceControls(root);
            SetupMessages();
            HealthSampling();
            StudySettings(root);
            ModalHelp();
            DialogLayout();
            return "PASS: actual source input controls, selection failure preserves the active source, actionable setup messages, diagnostics callbacks/health, and Windows dialog layout.";
        }
        finally {MirageDiagnostics.Shutdown();}
    }

    private static void SourceControls(string root)
    {
        TestScheduler.Reset();
        var source=new CameraInput{startOnAwake=false,VideoPlayer=new UnityEngine.Video.VideoPlayer()};
        MirageRuntimeChecks.Invoke(source,typeof(CameraInput),"Awake");
        Directory.CreateDirectory(root);
        string recording=Path.Combine(root,"test recording.mp4");
        File.WriteAllBytes(recording,new byte[]{0});
        string error;
        Check(source.TryStartVideoFile(recording,out error),"Existing recording could not be selected");
        Check(source.UseDebugVideoInput && source.VideoPlayer.source==UnityEngine.Video.VideoSource.Url && source.VideoPlayer.url.Contains("test%20recording.mp4"),"Recording selection did not create a correctly escaped file URI");
        Check(source.VideoPlayer.aspectRatio==UnityEngine.Video.VideoAspectRatio.FitInside,"Recording selection did not preserve the recording's proportions");
        source.VideoPlayer.EmitFrame(1);
        string selected=source.VideoPlayer.url;
        Check(!source.TryStartVideoFile(Path.Combine(root,"missing.mp4"),out error) && source.VideoPlayer.url==selected && source.HasFrame && source.LastInputError==null,"Invalid selection interrupted or falsely marked the current stream as failed");
        Check(!source.TryStartCamera("missing camera",out error) && source.HasFrame && source.LastInputError==null,"Missing camera selection interrupted the current stream");
        source.PauseOrResumeVideo(); Check(!source.VideoPlayer.isPlaying,"Pause did not pause");
        source.PauseOrResumeVideo(); Check(source.VideoPlayer.isPlaying,"Resume did not resume");
        source.VideoPlayer.EmitError("decoder failed at C:\\private\\recording.mp4");
        Check(!source.HasFrame && source.LastInputError.Contains("H.264"),"Decode errors did not invalidate the source with an actionable message");
        source.VideoPlayer.EmitPrepared();Check(source.LastInputError==null,"A newly prepared stream retained its old error");
        WebCamTexture.devices=new[]{new WebCamDevice{name="test camera"}};
        Check(source.TryStartCamera("test camera",out error) && !source.UseDebugVideoInput && source.SelectedCameraDeviceName=="test camera","Runtime camera selection failed");
        source.StopInput();Check(!source.HasFrame && source.LastInputError==null,"Stop did not leave a clean stopped state");
        MirageRuntimeChecks.Invoke(source,typeof(CameraInput),"OnDestroy");
        File.Delete(recording);
    }

    private static void SetupMessages()
    {
        TestScheduler.Reset();
        Check(MirageSetupReport.Check().Any(i=>i.Code=="PIPELINE_MISSING" && i.Blocking),"Missing pipeline has no actionable setup issue");
        var pipeline=new Pipeline();
        var yolo=new YOLOSegmentationRunner{InputWidth=640,InputHeight=640,ImageWidth=1280,ImageHeight=720,OutputWidth=640,OutputHeight=360};
        var depth=new DepthAnythingRunner{InputWidth=504,InputHeight=504,ImageWidth=1280,ImageHeight=720,OutputWidth=640,OutputHeight=360,IsEnabled=false};
        new CameraInput();
        var issues=MirageSetupReport.Check(false);
        Check(issues.Any(i=>i.Code=="MODEL_ASSET_MISSING" && i.Remedy.Contains("ONNX")),"Missing model remedy is not actionable");
        yolo.ModelAsset=new Unity.InferenceEngine.ModelAsset();
        depth.OutputHeight=480;
        Check(MirageSetupReport.Check(false).Any(i=>i.Code=="OUTPUT_GRID_MISMATCH" && i.Blocking),"Mismatched output grids were accepted");
        SystemInfo.supportsComputeShaders=false;
        Check(MirageSetupReport.Check(false).Any(i=>i.Code=="GPU_COMPUTE_UNAVAILABLE"),"Unsupported GPU missing from setup report");
        SystemInfo.supportsComputeShaders=true;
        var moebius=new MoebiusRunner();
        MirageRuntimeChecks.Set(pipeline,typeof(Pipeline),"inpaintingModel",moebius);
        MirageRuntimeChecks.Invoke(moebius,typeof(MoebiusRunner),"Fail","Local service is unavailable");
        Check(MirageSetupReport.Check(false).Any(i=>i.Code=="MOEBIUS_UNAVAILABLE" && i.Blocking),"Backend connection failure has no actionable setup issue");
        moebius.IsEnabled=false;
        Check(!MirageSetupReport.Check(false).Any(i=>i.Code=="MOEBIUS_UNAVAILABLE"),"Disabled removal left a stale connection error blocking the session");
    }

    private static void HealthSampling()
    {
        TestScheduler.Reset();
        var source=new CameraInput{startOnAwake=false,VideoPlayer=new UnityEngine.Video.VideoPlayer()};
        MirageRuntimeChecks.Invoke(source,typeof(CameraInput),"Awake");source.StartVideo();source.VideoPlayer.EmitFrame(1);
        var runtime=new MirageDiagnosticsRuntime();
        MirageRuntimeChecks.Invoke(runtime,typeof(MirageDiagnosticsRuntime),"Awake");
        MirageRuntimeChecks.Invoke(runtime,typeof(MirageDiagnosticsRuntime),"Sample",1.0);
        var first=MirageDiagnostics.GetHealthSnapshot();Check(first.HasInput && first.SourceType=="video","Health did not report video input");
        source.VideoPlayer.Pause();
        MirageRuntimeChecks.Invoke(runtime,typeof(MirageDiagnosticsRuntime),"Sample",100.0);
        var paused=MirageDiagnostics.GetHealthSnapshot();Check(!paused.InputStalled && !paused.ProcessingStalled,"Paused recording falsely reported a stall");
        Application.EmitLog("test failure", "test stack", LogType.Error);
        Check(MirageDiagnostics.GetHealthSnapshot().LatestErrorCode=="UNITY_ERROR","Unity error callback was not captured");
        Application.EmitLog(new string('x',20000),"",LogType.Error);
        Check(MirageDiagnostics.GetHealthSnapshot().LatestErrorMessage.Length<=4096,"A large error bypassed the diagnostic health message budget");
        first.SourceState="mutated";
        Check(MirageDiagnostics.GetHealthSnapshot().SourceState!="mutated","Health snapshots alias mutable internal state");
        MirageRuntimeChecks.Invoke(source,typeof(CameraInput),"OnDestroy");
    }

    private static void DialogLayout()
    {
        var dialog=typeof(VideoFilePicker).GetNestedType("OpenFileName",BindingFlags.NonPublic);
        Check(dialog!=null && Marshal.SizeOf(dialog)==(IntPtr.Size==8?152:88),"Windows file dialog structure has the wrong native layout");
    }

    private static void StudySettings(string root)
    {
        TestScheduler.Reset();
        Application.persistentDataPath=root;
        var study=new StudyManager{ParticipantID=21};
        MirageRuntimeChecks.Invoke(study,typeof(StudyManager),"Start");
        var settings=new System.Collections.Generic.Dictionary<EffectType,System.Collections.Generic.List<EffectSetting>>();
        settings[EffectType.Info]=new System.Collections.Generic.List<EffectSetting>{new PostProcessorSetting(7,Color.white,0,100)};
        study.LogSettings(settings,new System.Collections.Generic.List<UnityEngine.UI.Toggle>());
        string first=study.LastLogPath;
        study.LogSettings(settings,new System.Collections.Generic.List<UnityEngine.UI.Toggle>());
        Check(first!=study.LastLogPath && File.Exists(first) && File.Exists(study.LastLogPath),"Study settings collided or were not saved in the writable user folder");
        Check(study.LastLogPath.StartsWith(Path.Combine(root,"MIRAGE","StudyLogs")) && study.LastLogError==null,"Study settings used the application install folder");
        var document=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(first));
        Check((int)document["schemaVersion"]==1 && document["effects"]!=null,"Study settings lost their schema/effect data");
        string blocked=Path.Combine(root,"blocked-study-profile");File.WriteAllText(blocked,"not a directory");
        Application.persistentDataPath=blocked;
        study.LogSettings(settings,new System.Collections.Generic.List<UnityEngine.UI.Toggle>());
        Check(!string.IsNullOrEmpty(study.LastLogError),"Study write failure was not contained and reported");
        Application.persistentDataPath=root;
    }

    private static void ModalHelp()
    {
        TestScheduler.Reset();
        var module=new UnityEngine.EventSystems.BaseInputModule{enabled=true};
        var panel=new MirageOperatorPanel();
        MirageRuntimeChecks.Invoke(panel,typeof(MirageOperatorPanel),"SetVisible",true);
        Check(MirageOperatorPanel.IsVisible && !module.enabled,"Help did not isolate the underlying effects input");
        var newSceneModule=new UnityEngine.EventSystems.BaseInputModule{enabled=true};
        MirageRuntimeChecks.Invoke(panel,typeof(MirageOperatorPanel),"Refresh");
        Check(!newSceneModule.enabled,"A scene's new input module bypassed modal help");
        MirageRuntimeChecks.Invoke(panel,typeof(MirageOperatorPanel),"OnDisable");
        Check(!MirageOperatorPanel.IsVisible && module.enabled && newSceneModule.enabled,"Closing help did not restore input");
    }
}
