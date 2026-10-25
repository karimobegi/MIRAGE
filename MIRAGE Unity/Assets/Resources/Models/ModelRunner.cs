using System;
using System.Collections;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Base class for running a model.
/// At it's most basic, all models are run the same way for this project. We thus
/// only need to implement the abstract methods here.
/// 
/// A runner works as follows:
/// 1. Prepare the model for execution on Awake
/// 2. <see cref="RunModel"/> is called with the input textures as a coroutine and implemented by a child class
/// 3. The coroutine executes <see cref="RunInference"/> which first processes the model layer by layer with <see cref="ProcessLayers"/>
/// 4. <see cref="ProcessLayers"/> runs until the numbers of layers are processed or the model is done which then calls <see cref="PeekOutput"/>
/// 5. <see cref="PeekOutput"/> is implemented by the child class to peek the output tensors of the model. We dont read them yet to avoid blocking calls in case the data isnt ready
/// 6. <see cref="ReadOutput"/> is implemented by the child class to read the output tensors of the model once they are ready (<see cref="RequestsDone"/>)
/// 7. <see cref="ReadOutput"/> takes care of whatever happens with the output tensors next
/// Author: J-Britten
/// </summary>
public abstract class ModelRunner : MonoBehaviour
{
    public bool IsEnabled = true;
    public Unity.InferenceEngine.ModelAsset ModelAsset;

    /// <summary>
    /// Total number of layers to process each frame.
    /// Is set to <see cref="ModelRunner.runtimeModel.layers.Count"/> by default, can be controlled with the UI
    /// </summary>
    protected int layersPerFrame = 20;
    private int totalLayers = 20;

    [Range(0, 1)]
    [Tooltip("Fraction of model layers scheduled per rendered frame. Use 1 for the lowest inference latency.")]
    public float UpdateRate = 1f;

    public int MaxObjects = 256;

    [Tooltip("The Output Image to display the model output")]
    public RawImage OutputImage;

    /// <summary>
    /// The ACTUAL width of the input image. 
    /// While we can always resize an input image to the shape the model expects, doing uniform downsampling may create better results
    /// See <see cref="CalculateScaleAndPadding"/> for more information
    /// </summary>
    [Tooltip("Width of the INPUT image in pixels")]
    public int ImageWidth;

    [Tooltip("Height of the INPUT image in pixels")]
    public int ImageHeight;

    /// <summary>
    /// Model Input Dimensions
    /// </summary>
    [Tooltip("Width of the INPUT tensor")]
    public int InputWidth;
    [Tooltip("Height of the INPUT tensor")]
    public int InputHeight;

    /// <summary>
    /// Model Output Dimensions
    /// </summary>
    public int OutputWidth;
    public int OutputHeight;

    protected Unity.InferenceEngine.Model runtimeModel;
    protected Unity.InferenceEngine.Worker worker;
    protected IEnumerator schedule;
    protected bool inferencePending = false;
    protected bool modelRunning = false;

    protected RenderTexture outputTexture;

    public bool IsRunning { get { return modelRunning; } }
    public Texture OutputTexture { get { return outputTexture; } }
    [NonSerialized] public bool PublishOutputToUI = true;

    // A result is only published with the immutable camera snapshot it belongs to.
    protected long InputFrameId { get; private set; } = -1;
    protected double InputCapturedAt { get; private set; }
    public long OutputFrameId { get; protected set; } = -1;
    public bool IsPrepared { get; private set; }
    protected virtual bool UsesInferenceEngine => true;

    public void SetInputFrame(long frameId, double capturedAt)
    {
        InputFrameId = frameId;
        InputCapturedAt = capturedAt;
        // The same source frame can be processed again after changing a preset.
        // A failed new request must never reuse its previous success marker.
        OutputFrameId = -1;
    }



    /// <summary>
    /// The scale factor we need to downscale the input image to fit within the model's input dimensions
    /// </summary>
    protected float scale;

    /// <summary>
    /// The downscaled width of the input image to fit within the models input dimensions
    /// </summary>
    protected int scaledWidth;

    /// <summary>
    /// The downscaled height of the input image to fit within the models input dimensions
    /// </summary>
    protected int scaledHeight;

    /// <summary>
    /// The value we want to pad the image with
    /// </summary>
    protected float paddingValue;

    /// <summary>
    /// Padding values for the image
    /// </summary>
    protected int pad_h;
    protected int pad_w;
    protected int pad_h_half;
    protected int pad_w_half;

    /// <summary>
    /// For some models, it may be beneficial to downscale the Input Image so it fits within the model's input dimensions
    /// instead of just simple resize. This way, we can keep the proportions of the input image and avoid stretching
    /// and, in theory, improve model accuracy
    /// </summary>
    protected virtual void CalculateScaleAndPadding()
    {
        float scaleW = (float)InputWidth / (float)ImageWidth; //Whatever your IDE claims, these casts are necessary
        float scaleH = (float)InputHeight / (float)ImageHeight;
        //        Debug.Log("Scale W: " + scaleW + " Scale H: " + scaleH);
        scale = Math.Min(scaleW, scaleH);
        //      Debug.Log("Scale: " + scale);

        scaledWidth = (int)(ImageWidth * scale);
        scaledHeight = (int)(ImageHeight * scale);

        //    Debug.Log("Scaled Width: " + scaledWidth + " Scaled Height: " + scaledHeight);
        pad_h = (int)InputHeight - scaledHeight;
        pad_w = (int)InputWidth - scaledWidth;
        pad_h_half = (int)Mathf.Floor(pad_h / 2.0f);
        pad_w_half = (int)Mathf.Floor(pad_w / 2.0f);

        //  Debug.Log("Pad H: " + pad_h + " Pad W: " + pad_w + " Pad H Half: " + pad_h_half + " Pad W Half: " + pad_w_half);

    }

    /// <summary>
    /// Prepare the model for execution, called in <see cref="Awake"/>
    /// </summary>
    protected abstract void PrepareModel();


    /// <summary>
    /// Method that begins the model inference by parsing the input textures to the model needs
    /// </summary>
    /// <param name="inputs">Input Textures</param>
    /// <returns></returns>
    public abstract IEnumerator RunModel(params Texture[] inputs);

    /// <summary>
    /// Peek the Output tensors of the model
    /// </summary>            
    protected abstract void PeekOutput();

    /// <summary>
    /// Check if the tensor readback requests are done - implemented by the child class and depends on how many output tensors the model has
    /// </summary>
    /// <returns></returns>
    protected abstract bool RequestsDone();

    /// <summary>
    /// Read the output tensors of the model
    /// </summary>
    protected abstract void ReadOutput();

    /// <summary>
    /// Dispose the output tensors of the model
    /// </summary>
    public abstract void DisposeOutput();


    protected virtual void Awake()
    {
        // Unity calls Awake even on disabled components. Only the selected
        // backend should load weights or require an assigned ONNX asset.
        if (enabled) InitializeRunner();
    }

    protected virtual void OnEnable()
    {
        if (!IsPrepared) InitializeRunner();
    }

    private void InitializeRunner()
    {
        if (InputWidth <= 0 || InputHeight <= 0 || OutputWidth <= 0 || OutputHeight <= 0)
        {
            Debug.LogError("Model Runner: Input and Output dimensions must be greater than 0");
            enabled = false;
            return;
        }
        if (UsesInferenceEngine && ModelAsset == null)
        {
            Debug.LogError($"{name}: assign an ONNX Model Asset before running this model.", this);
            enabled = false;
            return;
        }
        PrepareModel(); //Prepare the model
        if (!UsesInferenceEngine)
        {
            IsPrepared = true;
            return;
        }
        worker = new Unity.InferenceEngine.Worker(runtimeModel, Unity.InferenceEngine.BackendType.GPUCompute);
        // Set the layers per frame to the total number of layers per default
        totalLayers = runtimeModel.layers.Count;
        layersPerFrame = totalLayers;
        SetUpdateRate(UpdateRate); //Set the update rate of the model
        IsPrepared = true;
    }


    /// <summary>
    /// Run the model inference in a loop as part of a coroutine
    /// </summary>
    /// <returns></returns>
    protected virtual IEnumerator RunInference()
    {
        modelRunning = true;
        try
        {
            while (modelRunning)
            {
                if (!inferencePending)
                    ProcessLayers();
                // GPU-only consumers can enqueue dependent work immediately. Runners
                // needing CPU metadata explicitly wait for compact async readbacks.
                if (inferencePending && RequestsDone())
                {
                    ReadOutput();
                    OutputFrameId = InputFrameId;
                    yield break;
                }
                yield return null;
            }
        }
        finally
        {
            modelRunning = false;
            inferencePending = false;
        }
    }

    /// <summary>
    /// Process the layers of the model based on the number of layers per frame that should be processed
    /// </summary>
    protected void ProcessLayers()
    {
        int i = 0;
        while (layersPerFrame >= totalLayers || i < layersPerFrame)
        {
            if (!schedule.MoveNext())
            {
                PeekOutput();
                inferencePending = true;
                return;
            }
            i++;
        }
    }

    /// <summary>
    /// Update the output image with the output texture
    /// </summary>
    protected void UpdateOutputImage()
    {
        if (PublishOutputToUI && OutputImage != null)
            OutputImage.texture = outputTexture;
    }

    /// <summary>
    /// Set the update rate of the model
    /// Smaller values spread layer scheduling across rendered frames, increasing latency.
    /// </summary>
    /// <param name="rate">Value between 0 and 1</param>
    public void SetUpdateRate(float rate) {
        UpdateRate = Mathf.Clamp(rate, 0, 1);
        // This control spreads scheduling over frames; even zero must make progress.
        layersPerFrame = Mathf.Max(1, Mathf.CeilToInt(totalLayers * UpdateRate));
    }
}
