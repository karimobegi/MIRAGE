// Test seams only. These are not Unity API compatibility references or a renderer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public sealed class SerializeField : Attribute { }
    public sealed class HideInInspector : Attribute { }
    public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string text) { } }
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float a, float b) { } }
    public sealed class MinAttribute : Attribute { public MinAttribute(float a) { } }
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string text) { } }
    public enum FindObjectsSortMode { None }
    public enum FindObjectsInactive { Include }
    public partial class Object
    {
        public string name = "test";
        public static readonly List<Object> Objects = new List<Object>();
        public Object() { Objects.Add(this); }
        public static T FindAnyObjectByType<T>() where T : Object => Objects.OfType<T>().FirstOrDefault();
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode) where T : Object => Objects.OfType<T>().ToArray();
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive,FindObjectsSortMode mode) where T : Object => Objects.OfType<T>().ToArray();
        public static T Instantiate<T>(T value) where T : Object => value;
        public static GameObject Instantiate(GameObject value, Transform parent) => new GameObject();
        public static void Destroy(Object value) { Objects.Remove(value); }
        public static void DestroyImmediate(Object value) { Destroy(value); }
        public static void DontDestroyOnLoad(Object value) { }
    }
    public class MonoBehaviour : Object
    {
        public bool enabled = true;
        public GameObject gameObject=new GameObject();
        public Coroutine StartCoroutine(IEnumerator iterator) => TestScheduler.Start(iterator, this);
        public void StopAllCoroutines() => TestScheduler.Stop(this);
    }
    public sealed class Coroutine
    {
        internal readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        internal Coroutine Waiting;
        internal MonoBehaviour Owner;
        public bool Done;
    }
    public static class TestScheduler
    {
        private static readonly List<Coroutine> Running = new List<Coroutine>();
        public static void Reset() { Running.Clear(); Time.realtimeSinceStartupAsDouble = 0; Object.Objects.Clear(); }
        public static Coroutine Start(IEnumerator iterator, MonoBehaviour owner)
        {
            var result = new Coroutine { Owner = owner };
            result.Stack.Push(iterator); Running.Add(result); Advance(result); return result;
        }
        private static void Advance(Coroutine c)
        {
            if (c.Done || (c.Waiting != null && !c.Waiting.Done)) return;
            c.Waiting = null;
            int guard = 10000;
            while (c.Stack.Count > 0)
            {
                if (--guard == 0) throw new Exception("Coroutine failed to yield");
                IEnumerator top = c.Stack.Peek();
                if (!top.MoveNext()) { (top as IDisposable)?.Dispose(); c.Stack.Pop(); continue; }
                var child = top.Current as IEnumerator;
                if (child != null) { c.Stack.Push(child); continue; }
                var waiting = top.Current as Coroutine;
                if (waiting != null && waiting.Done) continue;
                c.Waiting = waiting; return;
            }
            c.Done = true;
        }
        public static void Tick()
        {
            Time.realtimeSinceStartupAsDouble += 0.016;
            foreach (var coroutine in Running.ToArray()) Advance(coroutine);
            Running.RemoveAll(c => c.Done);
        }
        public static void Stop(MonoBehaviour owner)
        {
            foreach (var c in Running.Where(c => c.Owner == owner).ToArray())
            {
                foreach (var stack in c.Stack) (stack as IDisposable)?.Dispose();
                c.Done = true;
            }
            Running.RemoveAll(c => c.Done);
        }
    }
    public static class Time { public static double realtimeSinceStartupAsDouble; public static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble; }
    public static class Debug
    {
        public static void LogError(object value, Object owner = null) { throw new Exception("Unexpected runtime error: " + value); }
        public static void LogWarning(object value, Object owner = null) { }
        public static void Log(object value, Object owner = null) { }
    }
    public static class Mathf
    {
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Min(float a,float b) => Math.Min(a,b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int CeilToInt(float value) => (int)Math.Ceiling(value);
        public static int FloorToInt(float value) => (int)Math.Floor(value);
        public static int RoundToInt(float value) => (int)Math.Round(value);
        public static float Floor(float value) => (float)Math.Floor(value);
        public static int Clamp(int value, int a, int b) => Math.Min(b, Math.Max(a, value));
        public static float Clamp(float value, float a, float b) => Math.Min(b, Math.Max(a, value));
        public static bool Approximately(float a, float b) => Math.Abs(a - b) < .00001f;
        public static float Pow(float a,float b)=>(float)Math.Pow(a,b);
        public static float Abs(float value)=>Math.Abs(value);
        public static float LinearToGammaSpace(float value)=>value<=.0031308f?12.92f*value:1.055f*Pow(value,1/2.4f)-.055f;
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float a, float b) { x = a; y = b; }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x+b.x, a.y+b.y);
    }
    public struct Vector4 { public float x,y,z,w; public Vector4(float a,float b,float c,float d) { x=a;y=b;z=c;w=d; } }
    public struct Color
    {
        public float r,g,b,a;
        public Color(float red,float green,float blue,float alpha=1) { r=red;g=green;b=blue;a=alpha; }
        public static Color clear => new Color(0,0,0,0);
        public static Color white => new Color(1,1,1,1);
        public static Color black => new Color(0,0,0,1);
    }
    public struct Color32 { public byte r,g,b,a; public Color32(byte r,byte g,byte b,byte a) {this.r=r;this.g=g;this.b=b;this.a=a;} }
    public struct Vector2Int { public int x,y; public Vector2Int(int x,int y) {this.x=x;this.y=y;} }
    public enum FilterMode { Bilinear }
    public enum TextureWrapMode { Clamp }
    public enum TextureFormat { RGBA32,RGBAFloat,RFloat }
    public enum RenderTextureReadWrite { Linear }
    public enum ColorSpace { Gamma,Linear }
    public static class QualitySettings { public static ColorSpace activeColorSpace=ColorSpace.Linear; }
    public static partial class SystemInfo { public static bool supportsAsyncGPUReadback=true,supportsComputeShaders=true; }
    public static partial class Application { public static bool isPlaying=true; }
    public static class JsonUtility { public static string ToJson(object value)=>"{}"; public static T FromJson<T>(string value)=>default; }
    public class Texture : Object { public int width, height; public long Stamp; public FilterMode filterMode;public TextureWrapMode wrapMode; }
    public class Texture2D : Texture
    {
        public Texture2D() { }
        public Texture2D(int w,int h,TextureFormat format,bool mipmap,bool linear) {width=w;height=h;}
        public void LoadRawTextureData<T>(UnityEngine.Rendering.TestReadback<T> data) { }
        public UnityEngine.Rendering.TestReadback<T> GetRawTextureData<T>()=>new UnityEngine.Rendering.TestReadback<T>();
        public void Apply(bool updateMipmaps=true) { }
        public byte[] EncodeToPNG()=>new byte[0];
        public bool LoadImage(byte[] bytes,bool markNonReadable)=>true;
        public void SetPixels(Color[] colors) { }
        public Color[] GetPixels()=>new Color[width*height];
        public void ReadPixels(Rect rect,int x,int y) { }
    }
    public enum RenderTextureFormat { ARGB32, R8,RFloat,ARGBFloat }
    public class RenderTexture : Texture
    {
        public static RenderTexture active;
        public RenderTextureFormat format;
        public bool enableRandomWrite;
        public RenderTexture(int w,int h,int depth,RenderTextureFormat format=RenderTextureFormat.ARGB32) { width=w;height=h;this.format=format; }
        public RenderTexture(int w,int h,int depth,RenderTextureFormat format,RenderTextureReadWrite readWrite) : this(w,h,depth,format) { }
        public RenderTexture(int w,int h,int depth,UnityEngine.Experimental.Rendering.GraphicsFormat format) : this(w,h,depth) { }
        public static RenderTexture GetTemporary(int w,int h,int depth,RenderTextureFormat format,RenderTextureReadWrite readWrite)=>new RenderTexture(w,h,depth,format);
        public static void ReleaseTemporary(RenderTexture texture) { }
        public void Create() { }
        public void Release() { }
    }
    public class WebCamTexture : Texture
    {
        public static WebCamDevice[] devices = Array.Empty<WebCamDevice>();
        public bool isPlaying, didUpdateThisFrame;
        public WebCamTexture(int width,int height,int fps) { this.width=16;this.height=16; }
        public WebCamTexture(string name,int width,int height,int fps) : this(width,height,fps) { }
        public void Play() { isPlaying=true; }
        public void Stop() { isPlaying=false; }
    }
    public struct WebCamDevice { public string name; }
    public static class Graphics { public static void Blit(Texture source,RenderTexture target) { target.Stamp=source.Stamp; } }
    public static class GL { public static void Clear(bool depth,bool color,Color value) { if(RenderTexture.active!=null) RenderTexture.active.Stamp=0; } }
    public class ComputeBuffer : IDisposable
    {
        public int count;
        public ComputeBuffer(int count,int stride) { this.count=count; }
        public void SetData(Array data) { }
        public void GetData(Array data,int destinationStart=0,int sourceStart=0,int count=0) { }
        public void Dispose() { }
        public void Release() { }
    }
    public class ComputeShader : Object
    {
        public int FindKernel(string name) => 0;
        public void SetInt(string name,int value) { }
        public void SetFloat(string name,float value) { }
        public void SetBool(string name,bool value) { }
        public void SetVector(string name,Vector4 value) { }
        public void SetBuffer(int kernel,string name,ComputeBuffer value) { }
        public void SetTexture(int kernel,string name,Texture value) { }
        public void Dispatch(int kernel,int x,int y,int z) { }
    }
    public static class Resources { public static T Load<T>(string name) where T : Object,new() => new T(); }
    public class TextAsset : Object { public string text; }
    public class Transform : Object { public Transform transform => this; }
    public struct Rect
    {
        public float x,y,width,height;
        public Rect(float x,float y,float width,float height) {this.x=x;this.y=y;this.width=width;this.height=height;}
        public float xMin=>x;public float yMin=>y;public float xMax=>x+width;public float yMax=>y+height;
        public Vector2 center=>new Vector2(x+width/2,y+height/2);
        public Vector2 size {get=>new Vector2(width,height);set {width=value.x;height=value.y;} }
        public static Rect MinMaxRect(float x0,float y0,float x1,float y1)=>new Rect(x0,y0,x1-x0,y1-y0);
        public bool Overlaps(Rect other)=>xMin<other.xMax&&xMax>other.xMin&&yMin<other.yMax&&yMax>other.yMin;
    }
    public class RectTransform : Transform { public Rect rect=new Rect { size=new Vector2(640,360) }; public Vector2 pivot, anchoredPosition; }
    public partial class GameObject : Object
    {
        public GameObject(string name = "test") { this.name = name; }
        public Transform transform=new Transform();
        public TestScene scene;
        public T AddComponent<T>() where T:new() => new T();
        public void SetActive(bool active) { }
        public T GetComponent<T>() where T:new() => new T();
        public T GetComponentInChildren<T>() where T:new() => new T();
        public T GetComponentInParent<T>() where T:new() => new T();
    }
}
namespace UnityEngine.UI { public class RawImage : UnityEngine.Object { public UnityEngine.Texture texture; } }
namespace TMPro
{
    public class TMP_Text : UnityEngine.Object
    {
        public string text;
        public UnityEngine.Color color;
        public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
        public T GetComponent<T>() where T:new() => new T();
    }
}
namespace UnityEngine.Video
{
    public enum VideoSource { VideoClip,Url }
    public enum VideoAspectRatio { NoScaling = 0, FitInside = 3 }
    public class VideoPlayer : UnityEngine.Object
    {
        public UnityEngine.RenderTexture targetTexture;
        public long frame=-1;
        public bool sendFrameReadyEvents;
        public bool isPlaying;
        public double time;
        public VideoSource source;
        public VideoAspectRatio aspectRatio;
        public string url;
        public event Action<VideoPlayer,long> frameReady;
        public event Action<VideoPlayer> seekCompleted;
        public event Action<VideoPlayer> prepareCompleted;
        public event Action<VideoPlayer,string> errorReceived;
        public void Play() { isPlaying=true; }
        public void Pause() { isPlaying=false; }
        public void Stop() { isPlaying=false; }
        public void EmitError(string message) { errorReceived?.Invoke(this,message); }
        public void EmitPrepared() { prepareCompleted?.Invoke(this); }
        public void EmitFrame(long frame) { this.frame=frame;frameReady?.Invoke(this,frame); }
        public void EmitSeek() { seekCompleted?.Invoke(this); }
    }
}
namespace UnityEngine.Rendering
{
    public class TestReadback<T> { public int Length=>0;public T this[int index] {get=>default;set { }} public void CopyTo(T[] values) { } }
    public struct AsyncGPUReadbackRequest
    {
        public bool done => AsyncGPUReadback.TestDone;
        public bool hasError => false;
        public void WaitForCompletion() { }
        public TestReadback<T> GetData<T>() => new TestReadback<T>();
    }
    public static class AsyncGPUReadback
    {
        public static bool TestDone=true;
        public static AsyncGPUReadbackRequest Request(UnityEngine.ComputeBuffer b) => new AsyncGPUReadbackRequest();
        public static AsyncGPUReadbackRequest Request(UnityEngine.ComputeBuffer b,int size,int offset) => new AsyncGPUReadbackRequest();
        public static AsyncGPUReadbackRequest Request(UnityEngine.Texture b,int mip,UnityEngine.TextureFormat format) => new AsyncGPUReadbackRequest();
    }
}
namespace Unity.InferenceEngine
{
    public class ModelAsset : UnityEngine.Object { }
    public enum BackendType { GPUCompute }
    public enum DataType { Float }
    public enum TensorLayout { NCHW }
    public enum ChannelSwizzle { RGBA }
    public enum CoordOrigin { TopLeft }
    public class Model { public List<int> layers=new List<int>(); }
    public class TensorShape { public int[] values; public TensorShape(params int[] values) { this.values=values; } public int this[int index] => values[index]; }
    public class DynamicTensorShape { public DynamicTensorShape(params int[] values) { } }
    public class Tensor<T> : IDisposable
    {
        public TensorShape shape;
        public bool Disposed;
        public Tensor(TensorShape shape) { this.shape=shape; }
        public void Dispose() { Disposed=true; }
    }
    public class Worker : IDisposable
    {
        public Worker(Model model,BackendType backend) { }
        public void Dispose() { }
        public object PeekOutput(int index) => null;
        public object PeekOutput(string name) => null;
        public IEnumerator ScheduleIterable(params object[] inputs) { yield break; }
    }
    public struct TextureTransform
    {
        public TextureTransform SetDimensions(int w,int h,int c) => this;
        public TextureTransform SetTensorLayout(TensorLayout layout) => this;
        public TextureTransform SetChannelSwizzle(ChannelSwizzle swizzle) => this;
        public TextureTransform SetCoordOrigin(CoordOrigin origin) => this;
    }
    public static class TextureConverter
    {
        public static void ToTensor(UnityEngine.Texture texture,Tensor<float> tensor,TextureTransform transform) { }
        public static void RenderToTexture(Tensor<float> tensor,UnityEngine.RenderTexture texture,TextureTransform transform=default) { }
    }
    public class ComputeTensorData { public UnityEngine.ComputeBuffer buffer=new UnityEngine.ComputeBuffer(1,4); public static ComputeTensorData Pin<T>(Tensor<T> tensor) => new ComputeTensorData(); }
    public static class ModelLoader { public static Model Load(ModelAsset asset) => new Model(); }
    public class FunctionalTensor
    {
        public FunctionalTensor this[params object[] index] => new FunctionalTensor();
        public static FunctionalTensor operator +(FunctionalTensor a,FunctionalTensor b) => a;
        public static FunctionalTensor operator -(FunctionalTensor a,FunctionalTensor b) => a;
        public static FunctionalTensor operator -(int a,FunctionalTensor b) => b;
        public static FunctionalTensor operator *(FunctionalTensor a,FunctionalTensor b) => a;
        public static FunctionalTensor operator /(FunctionalTensor a,FunctionalTensor b) => a;
        public static FunctionalTensor operator /(FunctionalTensor a,float b) => a;
    }
    public class FunctionalGraph
    {
        public FunctionalTensor AddInput(DataType t,DynamicTensorShape s) => new FunctionalTensor();
        public Model Compile(params FunctionalTensor[] tensors) => new Model();
    }
    public static class Functional
    {
        public static FunctionalTensor Constant(float value) => new FunctionalTensor();
        public static FunctionalTensor Constant(TensorShape s,float[] values) => new FunctionalTensor();
        public static FunctionalTensor Pad(FunctionalTensor a,int[] b,float c) => a;
        public static FunctionalTensor[] Forward(Model m,params FunctionalTensor[] input) => new [] { new FunctionalTensor(),new FunctionalTensor() };
        public static FunctionalTensor Ceil(FunctionalTensor a)=>a;
        public static FunctionalTensor Interpolate(FunctionalTensor a,int[] dims,string mode) => a;
        public static FunctionalTensor Transpose(FunctionalTensor a,int b,int c) => a;
        public static FunctionalTensor ReduceMax(FunctionalTensor a,int b) => a;
        public static FunctionalTensor Sigmoid(FunctionalTensor a) => a;
        public static FunctionalTensor ArgMax(FunctionalTensor a,int b) => a;
        public static FunctionalTensor MatMul(FunctionalTensor a,FunctionalTensor b) => a;
        public static FunctionalTensor NMS(FunctionalTensor a,FunctionalTensor b,float c,float d) => a;
        public static FunctionalTensor Unsqueeze(FunctionalTensor a,int b) => a;
        public static FunctionalTensor BroadcastTo(FunctionalTensor a,int[] b) => a;
        public static FunctionalTensor Gather(FunctionalTensor a,int b,FunctionalTensor c) => a;
        public static FunctionalTensor Reshape(FunctionalTensor a,int[] b) => a;
        public static FunctionalTensor Mul(FunctionalTensor a,FunctionalTensor b) => a;
        public static FunctionalTensor ReduceSum(FunctionalTensor a,int b) => a;
        public static FunctionalTensor Concat(FunctionalTensor[] a,int b) => a[0];
        public static FunctionalTensor Where(FunctionalTensor a,FunctionalTensor b,FunctionalTensor c) => a;
        public static FunctionalTensor Greater(FunctionalTensor a,FunctionalTensor b) => a;
    }
}

// Collaborator seams: the real runtime classes under test are compiled separately.
public interface IEffectHandler { EffectType EffectType {get;} void UpdateClasses(); void UpdateClasses(List<EffectSetting> settings); }
public enum EffectType { Info,Inpainting }
public class EffectSetting { public int ClassID; public float MinRange,MaxRange; }
public class PostProcessorSetting : EffectSetting
{
    public UnityEngine.Color color;
    public PostProcessorSetting(int id,UnityEngine.Color color,float min=0,float max=0) { ClassID=id;this.color=color;MinRange=min;MaxRange=max; }
    public PostProcessorSettingStruct ToStruct()=>new PostProcessorSettingStruct();
}
public struct PostProcessorSettingStruct { }
public class InpaintingSetting : EffectSetting
{
    public InpaintingSetting(int id,float min,float max) {ClassID=id;MinRange=min;MaxRange=max;}
    public InpaintingSettingStruct ToStruct()=>new InpaintingSettingStruct(ClassID,MinRange,MaxRange);
}
public struct InpaintingSettingStruct
{
    public int ClassID;public float MinRange,MaxRange;
    public InpaintingSettingStruct(int id,float min,float max) {ClassID=id;MinRange=min;MaxRange=max;}
}
public interface IBenchmarkManager
{
    void SetParallelMode(bool parallel);
    void StartIteration(); void EndIteration(); void StartSegmentation(); void EndSegmentation();
    void StartDepthEstimation(); void EndDepthEstimation(); void StartInpainting(); void EndInpainting();
    void StartPostProcessing(); void EndPostProcessing();
}
public class NullBenchmarkManager : IBenchmarkManager
{
    public void SetParallelMode(bool parallel) { }
    public void StartIteration() { } public void EndIteration() { }
    public void StartSegmentation() { } public void EndSegmentation() { }
    public void StartDepthEstimation() { } public void EndDepthEstimation() { }
    public void StartInpainting() { } public void EndInpainting() { }
    public void StartPostProcessing() { } public void EndPostProcessing() { }
}
public class BenchmarkManager : UnityEngine.MonoBehaviour, IBenchmarkManager
{
    public void SetParallelMode(bool parallel) { }
    public void StartIteration() { } public void EndIteration() { }
    public void StartSegmentation() { } public void EndSegmentation() { }
    public void StartDepthEstimation() { } public void EndDepthEstimation() { }
    public void StartInpainting() { } public void EndInpainting() { }
    public void StartPostProcessing() { } public void EndPostProcessing() { }
}
namespace ByteTrackCSharp
{
    public class Rect
    {
        private float X,Y,W,H;
        public Rect(float x,float y,float w,float h) { X=x;Y=y;W=w;H=h; }
        public float x()=>X; public float y()=>Y; public float width()=>W; public float height()=>H;
    }
    public class Object
    {
        public Rect rect;public int label,detection;public float prob;
        public Object(Rect r,int l,float p,int d) { rect=r;label=l;prob=p;detection=d; }
    }
    public class STrack { }
    public class LabelledSTrack : STrack
    {
        public int Label,Detection; private Rect rect;
        public LabelledSTrack(Rect r,float p,int l,int d) { rect=r;Label=l;Detection=d; }
        public Rect getRect()=>rect;
    }
    public class BYTETracker
    {
        private Func<Object,STrack> factory;
        public BYTETracker(Func<Object,STrack> factory) { this.factory=factory; }
        public List<STrack> update(List<Object> objects) => objects.Select(factory).ToList();
    }
}
namespace UnityEngine.Experimental.Rendering { public enum GraphicsFormat { R8G8B8A8_UNorm } }
namespace Unity.VisualScripting { }
namespace UnityEngine.Networking
{
    public class UploadHandlerRaw { public UploadHandlerRaw(byte[] bytes) { } }
    public class DownloadHandlerBuffer { public string text; }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success,ConnectionError }
        public UploadHandlerRaw uploadHandler;public DownloadHandlerBuffer downloadHandler;public int timeout;public Result result;public string error;
        public UnityWebRequest(string url,string method) { }
        public void SetRequestHeader(string key,string value) { }
        public object SendWebRequest()=>new object();
        public void Abort() { }
        public void Dispose() { }
    }
}
namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method,AllowMultiple=true)]
    public class MenuItem : Attribute { public MenuItem(string path,bool validate=false) { } }
    public static class Selection {public static UnityEngine.GameObject activeGameObject;}
    public static class EditorApplication {public static bool isPlayingOrWillChangePlaymode,isPlaying;}
    public static class Undo
    {
        public static T AddComponent<T>(UnityEngine.GameObject target) where T:new()=>new T();
        public static void SetCurrentGroupName(string name) { }
        public static void RecordObject(UnityEngine.Object target,string name) { }
    }
    public static partial class EditorUtility {public static void SetDirty(UnityEngine.Object target) { }}
    public static class EditorGUIUtility {public static void PingObject(UnityEngine.Object target) { }}
    public class SerializedProperty
    {
        private UnityEngine.Object target;private string field;
        public SerializedProperty(UnityEngine.Object target,string field) {this.target=target;this.field=field;}
        public UnityEngine.Object objectReferenceValue
        {
            set { target.GetType().GetField(this.field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public).SetValue(target,value); }
        }
    }
    public class SerializedObject
    {
        private UnityEngine.Object target;
        public SerializedObject(UnityEngine.Object target) {this.target=target;}
        public SerializedProperty FindProperty(string name)=>new SerializedProperty(target,name);
        public void ApplyModifiedProperties() { }
    }
}
namespace UnityEditor.SceneManagement {public static partial class EditorSceneManager {public static void MarkSceneDirty(UnityEngine.TestScene scene) { }}}

namespace UnityEngine
{
    public struct TestScene
    {
        public int id; public string path; public bool isDirty;
        public static implicit operator TestScene(int id)=>new TestScene{id=id};
        public static bool operator ==(TestScene a,TestScene b)=>a.id==b.id && a.path==b.path;
        public static bool operator !=(TestScene a,TestScene b)=>!(a==b);
        public override bool Equals(object value)=>value is TestScene s && this==s;
        public override int GetHashCode()=>id;
    }
}

#if !MIRAGE_DIAGNOSTICS_REAL
namespace Mirage.Diagnostics
{
    public static class MirageDiagnostics
    {
        public static void Record(string code,string message,object details=null,string severity="info") { }
    }
}
#endif
