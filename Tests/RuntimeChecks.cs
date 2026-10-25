using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ByteTrackCSharp;

public static class MirageRuntimeChecks
{
    internal static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    internal static void Set(object target,Type type,string field,object value) => type.GetField(field,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).SetValue(target,value);
    internal static object Get(object target,Type type,string field) => type.GetField(field,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(target);
    internal static object Invoke(object target,Type type,string method,params object[] args) => type.GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(target,args);
    internal static void Prepare(ModelRunner runner)
    {
        runner.InputWidth=16;runner.InputHeight=9;runner.OutputWidth=16;runner.OutputHeight=9;runner.ImageWidth=32;runner.ImageHeight=18;
        Invoke(runner,typeof(ModelRunner),"Awake");
        Check(runner.IsPrepared,"Model preparation flag was not set");
    }
    private static void Tick(int count=1) { for(int i=0;i<count;i++) TestScheduler.Tick(); }
    private static void VideoFrame(CameraInput camera,long index)
    {
        camera.VideoPlayer.EmitFrame(index);
        camera.CurrentFrame.Stamp=camera.FrameId;
    }

    public static string Run()
    {
        DisabledRunnerInitialization();
        Scheduling();
        DepthContractAndBorrowedOutputs();
        CpuEffectsAndLabels();
        CameraLifecycle();
        BackendConfiguration();
        CoherentPipeline(false);
        CoherentPipeline(true);
        SourceSwitchDiscardsInflightFrame();
        SameIdCameraReplacementIsProcessed();
        return "PASS: actual-source disabled-backend/lazy initialization, scheduler budgets/finally, depth validity+units+borrowed ownership, CPU class/detection selection, camera restart/discontinuity/timestamps, sequential+parallel frame coherence/double buffering, and source-switch invalidation.";
    }

    private static void DisabledRunnerInitialization()
    {
        TestScheduler.Reset();
        // Awake is called by Unity even when a component is disabled. It must
        // neither validate missing assets/dimensions nor allocate a worker.
        var inactiveMigan=new MIGANRunner { enabled=false };
        Invoke(inactiveMigan,typeof(ModelRunner),"Awake");
        Check(!inactiveMigan.IsPrepared && Get(inactiveMigan,typeof(ModelRunner),"worker")==null,"Disabled MI-GAN prepared or allocated a worker");
        var runner=new SchedulingProbe { enabled=false };
        Invoke(runner,typeof(ModelRunner),"Awake");
        Check(!runner.IsPrepared && runner.Preparations==0,"Disabled runner executed preparation");
        runner.InputWidth=16;runner.InputHeight=9;runner.OutputWidth=16;runner.OutputHeight=9;
        runner.ModelAsset=new Unity.InferenceEngine.ModelAsset();
        runner.enabled=true;
        Invoke(runner,typeof(ModelRunner),"OnEnable");
        Check(runner.IsPrepared && runner.Preparations==1,"Enabling a previously disabled runner did not initialize it");
        object worker=Get(runner,typeof(ModelRunner),"worker");
        Invoke(runner,typeof(ModelRunner),"OnEnable");
        Check(runner.Preparations==1 && ReferenceEquals(worker,Get(runner,typeof(ModelRunner),"worker")),"Repeated enable allocated another model/worker");
        var inactiveMoebius=new MoebiusRunner { enabled=false };
        Invoke(inactiveMoebius,typeof(MoebiusRunner),"Awake");
        Check(inactiveMoebius.InputWidth==512 && inactiveMoebius.InputHeight==512 && inactiveMoebius.OutputWidth>0 && !inactiveMoebius.IsPrepared,"Disabled Moebius did not retain dimension defaults without preparing");
    }

    private static void Scheduling()
    {
        TestScheduler.Reset();
        var runner=new SchedulingProbe { ModelAsset=new Unity.InferenceEngine.ModelAsset(),UpdateRate=0 };
        Prepare(runner);
        runner.SetInputFrame(42,1.25);
        runner.StartCoroutine(runner.RunModel());
        Check(runner.Scheduled==1,"Layer budget advanced more than one layer at zero update rate");
        Tick(8);
        Check(runner.Scheduled==3 && runner.Reads==1 && runner.OutputFrameId==42 && !runner.IsRunning,"Layer scheduling did not complete/publish exactly once");
        runner.StartCoroutine(runner.RunModel());
        Check(runner.IsRunning,"Second scheduling run did not start");
        runner.StopAllCoroutines();
        Check(!runner.IsRunning,"Cancelled inference retained running state");
    }

    private static void DepthContractAndBorrowedOutputs()
    {
        TestScheduler.Reset();
        var depth=new DepthProbe { Values=new[]{float.NaN,float.PositiveInfinity,0f,-1f,3.25f} };
        float value;
        for(int i=-1;i<4;i++) Check(!depth.TryGetDepth(i,out value) && float.IsNaN(value),"Invalid depth accepted at "+i);
        Check(!depth.TryGetDepth(5,out value),"Out-of-range depth accepted");
        Check(depth.TryGetDepth(4,out value) && value==3.25f,"Valid depth rejected");
        Check(!depth.UsesMetricDepth && depth.DepthUnitLabel=="relative","Relative unit contract incorrect");
        depth.Metric=true;
        Check(depth.UsesMetricDepth && depth.DepthUnitLabel=="m","Metric unit contract incorrect");
        depth.Values=null;Check(!depth.TryGetDepth(0,out value),"Null data accepted");
        var actual=new DepthAnythingRunner();
        var output=new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1,1,2,2));
        var display=new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1,1,2,2));
        Set(actual,typeof(DepthAnythingRunner),"outputTensor",output);
        Set(actual,typeof(DepthAnythingRunner),"textureTensor",display);
        actual.DisposeOutput();
        Check(!output.Disposed && !display.Disposed,"Borrowed worker tensors were disposed by the depth runner");
        Check(Get(actual,typeof(DepthAnythingRunner),"outputTensor")==null,"Depth borrowed reference not cleared");
        actual.ResetOutput();
        Check(!actual.IsPrepared && actual.ObjectDepthBuffer!=null && actual.DepthData.All(float.IsNaN),"Unprepared depth did not supply an invalid compact binding for class-only GPU effects");
        actual.ModelAsset=new Unity.InferenceEngine.ModelAsset();
        Prepare(actual);
        Set(actual,typeof(DepthAnythingRunner),"outputTensor",output);
        UnityEngine.Rendering.AsyncGPUReadback.TestDone=false;
        var pending=actual.CalculateObjectDepthsAsync(new ComputeBuffer(actual.OutputWidth*actual.OutputHeight,8),1);
        Check(pending.MoveNext(),"Depth reduction did not yield while compact readback was pending");
        (pending as IDisposable).Dispose();
        Check(!(bool)Get(actual,typeof(DepthAnythingRunner),"depthReadbackPending"),"Cancelled depth readback retained its pending state");
        UnityEngine.Rendering.AsyncGPUReadback.TestDone=true;
        var yolo=new YOLOSegmentationRunner();
        Set(yolo,typeof(YOLOSegmentationRunner),"bboxTensor",output);
        yolo.DisposeOutput();
        Check(!output.Disposed,"Borrowed worker tensor was disposed by YOLO");
    }

    private static void CpuEffectsAndLabels()
    {
        TestScheduler.Reset();
        var yolo=new SegmentationProbe();Prepare(yolo);yolo.SetDetection(7);
        var depth=new DepthProbe();Prepare(depth);depth.Values=new[]{3f};
        var pipeline=new Pipeline();
        var effect=new CpuProbe();
        effect.Initialize(yolo,depth,new RectTransform(),pipeline);
        effect.UpdateClasses(new System.Collections.Generic.List<EffectSetting> { new PostProcessorSetting(7,new Color(1,0,0),1,5) });
        var track=new LabelledSTrack(new ByteTrackCSharp.Rect(0,0,1,1),1,7,0);
        Check(effect.Accepts(track),"Class 7/detection 0 was incorrectly treated as detection 7");
        depth.Values[0]=float.NaN;Check(!effect.Accepts(track),"Unavailable depth passed a range filter");
        depth.IsEnabled=false;Check(effect.Accepts(track),"Disabled depth did not allow class-only selection");
        Check(!effect.Accepts(new LabelledSTrack(new ByteTrackCSharp.Rect(0,0,1,1),1,8,0)),"Track/detection class mismatch accepted");
        depth.IsEnabled=true;depth.Values[0]=3;
        var info=new ObjectInfoPostProcessor();
        Set(info,typeof(CPUPostProcessor),"depthEstimationRunner",depth);
        Set(info,typeof(ObjectInfoPostProcessor),"classNames",Enumerable.Range(0,80).Select(i=>i==7?"truck":"class"+i).ToArray());
        string label=(string)Invoke(info,typeof(ObjectInfoPostProcessor),"GetLabelText",track);
        Check(label.Contains("relative") && !label.Contains(" m"),"Relative DA3 depth falsely labelled metres");
        depth.Metric=true;
        label=(string)Invoke(info,typeof(ObjectInfoPostProcessor),"GetLabelText",track);
        Check(label.Contains(" m") && !label.Contains("relative"),"Metric label incorrect");
        depth.Values[0]=float.NaN;
        label=(string)Invoke(info,typeof(ObjectInfoPostProcessor),"GetLabelText",track);
        Check(label.Contains("unavailable"),"Unavailable depth displayed numerically");
    }

    private static void CameraLifecycle()
    {
        TestScheduler.Reset();
        var camera=new CameraInput { startOnAwake=false };
        Invoke(camera,typeof(CameraInput),"Awake");camera.StartCamera();
        Check(camera.CurrentFrame==null,"Webcam allocated the initial 16x16 negotiation frame");
        var first=(WebCamTexture)Get(camera,typeof(CameraInput),"webcamTexture");
        first.width=1280;first.height=720;first.didUpdateThisFrame=true;first.Stamp=9;
        Tick();Invoke(camera,typeof(CameraInput),"Update");
        Check(camera.HasFrame && camera.CurrentFrame.width==1280 && camera.CurrentFrame.height==720 && camera.CurrentFrame.Stamp==9,"First real webcam frame/dimensions incorrect");
        Check(camera.FrameTimestamp==Time.realtimeSinceStartupAsDouble && camera.FrameId==1,"Webcam arrival timestamp/id incorrect");
        long version=camera.SourceVersion;
        camera.StopCamera();camera.StartCamera();
        Check(!camera.HasFrame && camera.SourceVersion>version,"Camera restart did not invalidate history");
        Check(!ReferenceEquals(first,Get(camera,typeof(CameraInput),"webcamTexture")),"Camera restart retained stopped webcam");
        camera.VideoPlayer=new UnityEngine.Video.VideoPlayer();camera.StartVideo();camera.StartVideo();
        long frame=camera.FrameId;VideoFrame(camera,10);
        Check(camera.FrameId==frame+1,"Restarted video registered duplicate callbacks");
        frame=camera.FrameId;version=camera.SourceVersion;VideoFrame(camera,10);
        Check(camera.FrameId==frame && camera.SourceVersion==version,"Duplicate frame-ready callback created another frame/discontinuity");
        version=camera.SourceVersion;VideoFrame(camera,2);
        Check(camera.SourceVersion>version && camera.HasFrame,"Backward video jump did not reset history");
        version=camera.SourceVersion;frame=camera.FrameId;
        camera.VideoPlayer.frame=50;
        Tick();camera.VideoPlayer.EmitSeek();
        Check(camera.SourceVersion>version && camera.HasFrame && camera.FrameId==frame+1,"Paused seek did not publish its available target frame");
        Check(camera.FrameTimestamp==Time.realtimeSinceStartupAsDouble,"Paused seek target timestamp was not refreshed");
        frame=camera.FrameId;version=camera.SourceVersion;VideoFrame(camera,50);
        Check(camera.FrameId==frame && camera.SourceVersion==version,"Frame-ready following seek duplicated the seek target");
    }

    private static void BackendConfiguration()
    {
        TestScheduler.Reset();
        var migan=new MIGANRunner
        {
            ImageWidth=1280,ImageHeight=720,InputWidth=512,InputHeight=512,OutputWidth=1280,OutputHeight=720,
            SelectedClasses=new[]{new InpaintingSetting(7,1,50)}
        };
        var moebius=new MoebiusRunner();
        var pipeline=new Pipeline();
        Set(pipeline,typeof(Pipeline),"inpaintingModel",migan);
        var configure=typeof(MoebiusSetupMenu).GetMethod("Configure",BindingFlags.Static|BindingFlags.NonPublic);
        configure.Invoke(null,new object[]{pipeline,migan,moebius});
        Check(pipeline.ActiveInpaintingModel==moebius && moebius.enabled && moebius.IsEnabled && !migan.enabled && !migan.IsEnabled,"Backend menu did not select exactly one runner");
        Check(moebius.OutputWidth==1280 && moebius.OutputHeight==720 && moebius.SelectedClasses[0].ClassID==7,"Backend menu dropped dimensions/selection");
        Check(!ReferenceEquals(migan.SelectedClasses,moebius.SelectedClasses) && !ReferenceEquals(migan.SelectedClasses[0],moebius.SelectedClasses[0]),"Backend menu aliased mutable selection settings");
        configure.Invoke(null,new object[]{pipeline,moebius,migan});
        Check(pipeline.ActiveInpaintingModel==migan && migan.enabled && !moebius.enabled,"Switching back to MI-GAN failed");
        foreach(string url in new[]{"http://127.0.0.1:8765","http://localhost:8765","http://[::1]:8765"})
        {
            moebius.ServerUrl=url;
            Check((bool)Invoke(moebius,typeof(MoebiusRunner),"ValidEndpoint"),"Loopback endpoint rejected");
        }
        foreach(string url in new[]{"https://127.0.0.1:8765","http://example.com:8765","file:///tmp/model",""})
        {
            moebius.ServerUrl=url;
            Check(!(bool)Invoke(moebius,typeof(MoebiusRunner),"ValidEndpoint"),"Non-loopback/HTTP endpoint accepted");
        }
    }

    private sealed class Fixture
    {
        public Pipeline Pipeline;public CameraInput Camera;public SegmentationProbe Segmentation;public DepthProbe Depth;public InpaintingProbe Inpainting;
    }
    private static Fixture Setup(bool sequential)
    {
        TestScheduler.Reset();
        var fixture=new Fixture();
        fixture.Segmentation=new SegmentationProbe();Prepare(fixture.Segmentation);
        fixture.Depth=new DepthProbe { Segmentation=fixture.Segmentation };Prepare(fixture.Depth);
        fixture.Inpainting=new InpaintingProbe { Depth=fixture.Depth };Prepare(fixture.Inpainting);
        fixture.Camera=new CameraInput { startOnAwake=false,VideoPlayer=new UnityEngine.Video.VideoPlayer(),RequestedWidth=32,RequestedHeight=18 };
        Invoke(fixture.Camera,typeof(CameraInput),"Awake");fixture.Camera.StartVideo();VideoFrame(fixture.Camera,0);
        fixture.Pipeline=new Pipeline { RunAsSequence=sequential,OutputImage=new RawImage(),CPUPostProcessingContainer=new RectTransform() };
        Invoke(fixture.Pipeline,typeof(Pipeline),"Awake");Invoke(fixture.Pipeline,typeof(Pipeline),"Start");
        return fixture;
    }
    private static void Until(Func<bool> done,int limit=100)
    {
        for(int i=0;i<limit&&!done();i++) Tick();
        Check(done(),"Coroutine publication timed out");
    }
    private static void CoherentPipeline(bool sequential)
    {
        var f=Setup(sequential);
        // Camera advances while every stage must retain frame 1's immutable snapshot.
        for(int i=1;f.Pipeline.PublishedFrameCount==0&&i<40;i++) { VideoFrame(f.Camera,i);Tick(); }
        Check(f.Pipeline.PublishedFrameId==1 && f.Pipeline.OutputImage.texture.Stamp==1,"First pipeline frame mixed camera/inference snapshots");
        Texture oldDisplayed=f.Pipeline.OutputImage.texture;
        long firstPublished=f.Pipeline.PublishedFrameCount;
        VideoFrame(f.Camera,100);Tick(2);
        Check(ReferenceEquals(oldDisplayed,f.Pipeline.OutputImage.texture) && oldDisplayed.Stamp==1,"In-flight processing overwrote the displayed frame");
        Until(()=>f.Pipeline.PublishedFrameCount>firstPublished);
        Check(!ReferenceEquals(oldDisplayed,f.Pipeline.OutputImage.texture),"Pipeline did not swap processing/presented buffers");
        Check(f.Pipeline.OutputImage.texture.Stamp==f.Pipeline.PublishedFrameId,"Published image/frame ID disagree");
        Until(()=>f.Pipeline.PublishedFrameId==f.Camera.FrameId);
        long published=f.Pipeline.PublishedFrameCount;Tick(20);
        Check(f.Pipeline.PublishedFrameCount==published,"Same input frame was repeatedly reprocessed");
        Check(f.Inpainting.CoherentReads>0 && f.Depth.CoherentReads>0,"Dependent stages did not observe aligned model outputs");
    }
    private static void SourceSwitchDiscardsInflightFrame()
    {
        var f=Setup(false);
        Tick();f.Camera.NotifySourceDiscontinuity();
        Tick(15);
        Check(f.Pipeline.PublishedFrameCount==0,"Source switch published a stale in-flight frame");
        VideoFrame(f.Camera,100);
        Until(()=>f.Pipeline.PublishedFrameCount==1);
        Check(f.Pipeline.PublishedFrameId==f.Camera.FrameId,"New source frame was not published");
        Check(f.Inpainting.HistoryResets>=2,"Source switch did not reset temporal history");
    }

    private static void SameIdCameraReplacementIsProcessed()
    {
        var f=Setup(false);
        Until(()=>f.Pipeline.PublishedFrameCount==1);
        long previousFrame=f.Camera.FrameId,previousVersion=f.Camera.SourceVersion;
        int previousCalls=f.Segmentation.Calls,previousResets=f.Inpainting.HistoryResets;
        var replacement=new CameraInput {startOnAwake=false,VideoPlayer=new UnityEngine.Video.VideoPlayer(),RequestedWidth=32,RequestedHeight=18};
        Invoke(replacement,typeof(CameraInput),"Awake");replacement.StartVideo();VideoFrame(replacement,0);
        Check(replacement.FrameId==previousFrame && replacement.SourceVersion==previousVersion,"Fixture failed to create colliding source IDs");
        Until(()=>f.Pipeline.PublishedFrameCount==2);
        Check(f.Segmentation.Calls==previousCalls+1,"A replacement camera with colliding IDs was skipped");
        Check(f.Inpainting.HistoryResets==previousResets+1,"Replacement camera did not invalidate temporal history");
        Tick(20);
        Check(f.Pipeline.PublishedFrameCount==2,"Replacement camera frame was processed repeatedly");
    }
}

public sealed class SchedulingProbe : ModelRunner
{
    public int Scheduled,Reads,Preparations;
    protected override void PrepareModel() { Preparations++;runtimeModel=new Unity.InferenceEngine.Model();runtimeModel.layers.AddRange(new[]{1,2,3}); }
    private IEnumerator Layers() { for(int i=0;i<3;i++) { Scheduled++;yield return null; } }
    public override IEnumerator RunModel(params Texture[] inputs) { schedule=Layers();yield return RunInference(); }
    protected override void PeekOutput() { }
    protected override bool RequestsDone()=>true;
    protected override void ReadOutput() { Reads++; }
    public override void DisposeOutput() { }
}
public sealed class SegmentationProbe : YOLOSegmentationRunner
{
    public int Calls;
    protected override bool UsesInferenceEngine=>false;
    protected override void PrepareModel()
    {
        OutputBuffer=new ComputeBuffer(OutputWidth*OutputHeight,8);
        MirageRuntimeChecks.Set(this,typeof(YOLOSegmentationRunner),"bboxData",new float[4]);
        MirageRuntimeChecks.Set(this,typeof(YOLOSegmentationRunner),"labelIDData",new int[1]);
        MirageRuntimeChecks.Set(this,typeof(YOLOSegmentationRunner),"scoresData",new float[1]);
        SetDetection(7);
    }
    public void SetDetection(int label)
    {
        MirageRuntimeChecks.Set(this,typeof(YOLOSegmentationRunner),"numDetections",1);
        LabelIDs[0]=label;Scores[0]=1;BBoxes[0]=5;BBoxes[1]=4;BBoxes[2]=2;BBoxes[3]=2;
    }
    public override IEnumerator RunModel(params Texture[] inputs)
    {
        Calls++;long stamp=inputs[0].Stamp;long id=InputFrameId;
        yield return null;yield return null;
        MirageRuntimeChecks.Check(inputs[0].Stamp==stamp && stamp==id,"Segmentation input mutated in flight");
        OutputFrameId=id;
    }
}
public sealed class DepthProbe : DepthEstimationRunner
{
    public float[] Values=new[]{3f};public bool Metric;public SegmentationProbe Segmentation;public int CoherentReads;public long ReadbackFrame=-1;
    protected override bool UsesInferenceEngine=>false;
    public override bool UsesMetricDepth=>Metric;
    public override float[] DepthData=>Values;
    private ComputeBuffer buffer=new ComputeBuffer(1,4);
    public override ComputeBuffer ObjectDepthBuffer=>buffer;
    protected override void PrepareModel() { }
    public override IEnumerator RunModel(params Texture[] inputs)
    {
        long stamp=inputs[0].Stamp;long id=InputFrameId;yield return null;
        MirageRuntimeChecks.Check(inputs[0].Stamp==stamp && stamp==id,"Depth input mutated in flight");OutputFrameId=id;
    }
    public override IEnumerator CalculateObjectDepthsAsync(ComputeBuffer segmentation,int count)
    {
        MirageRuntimeChecks.Check(Segmentation.OutputFrameId==OutputFrameId,"Depth reduction used mismatched segmentation frame");
        long frame=OutputFrameId;yield return null;
        MirageRuntimeChecks.Check(Segmentation.OutputFrameId==frame,"Segmentation buffer changed during depth reduction");
        ReadbackFrame=frame;Values[0]=3;CoherentReads++;
    }
    public override void CalculateObjectDepths(ComputeBuffer segmentation,int count) { throw new Exception("Pipeline used blocking depth compatibility API"); }
    public override void ResetOutput() { if(Values!=null) for(int i=0;i<Values.Length;i++) Values[i]=float.NaN; }
    protected override void PeekOutput() { }
    protected override bool RequestsDone()=>true;
    protected override void ReadOutput() { }
    public override void DisposeOutput() { }
}
public sealed class InpaintingProbe : InpaintingRunner
{
    public DepthProbe Depth;public int CoherentReads,HistoryResets;
    protected override bool UsesInferenceEngine=>false;
    protected override void PrepareModel() { outputTexture=new RenderTexture(OutputWidth,OutputHeight,0); }
    public override IEnumerator RunModel(Texture input,YOLOSegmentationRunner yolo,ComputeBuffer depth)
    {
        long frame=InputFrameId;long stamp=input.Stamp;
        MirageRuntimeChecks.Check(yolo.OutputFrameId==frame && (!UseDepthFilter||Depth.ReadbackFrame==frame),"Inpainting started before aligned metadata completion");
        yield return null;yield return null;yield return null;
        MirageRuntimeChecks.Check(input.Stamp==stamp && stamp==frame && yolo.OutputFrameId==frame,"Inpainting dependencies mutated in flight");
        outputTexture.Stamp=frame;OutputFrameId=frame;CoherentReads++;
    }
    public override void ResetHistory() { HistoryResets++; }
    public override IEnumerator RunModel(Texture input,ComputeBuffer mask,ComputeBuffer depth) { yield break; }
    public override IEnumerator RunModel(params Texture[] inputs) { yield break; }
    protected override void PeekOutput() { }
    protected override bool RequestsDone()=>true;
    protected override void ReadOutput() { }
    public override void DisposeOutput() { }
}
public sealed class CpuProbe : CPUPostProcessor
{
    public bool Accepts(LabelledSTrack track)=>IsValidObject(track);
    protected override void OnClassesUpdated() { }
    protected override bool HasExistingObject(int index)=>false;
    protected override void UpdateObject(int index,LabelledSTrack track,Vector2 position,Vector2 size) { }
    protected override void CreateObject(LabelledSTrack track,Vector2 position,Vector2 size) { }
    protected override void DeactivateRemainingObjects(int index) { }
}
