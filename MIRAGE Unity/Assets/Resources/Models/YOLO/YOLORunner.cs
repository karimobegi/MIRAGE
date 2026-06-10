using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.InferenceEngine;
using Unity.VisualScripting;
using UnityEditor.EditorTools;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UI;


/// <summary>
/// YOLO Segmentation: Model downloaded from https://docs.ultralytics.com/tasks/segment/#models
/// 
/// See the Export section for instructions on how to export the model to onnx
/// When using the default seg model, these are the tensor dimensions. Using a custom model, these may vary, however the general principles
/// are still applicable
/// 
/// YOLO configurations:
/// Input shape: (1, 3, 640, 640) BCHW 
/// Output shape(s): (1, 32, 160, 160)), 1 is batch size, 32 is number of prototype masks with each mask being 160x160
///                 ((1, 116, 8400), where 1 is the batch size, 116 = 4bbox coordinates + 80 classes + 32 mask coefficients
///                 NOTE: When using a custom trained YOLO models, it is 4 bbox + N CLASSES (as defined in training) + 32 mask coefficients
///
/// We've added some additional pre- and postprocessing steps to improve quality and make it easier to use within our pipeline
/// 1. Preprocessing: Based on the input image's aspect ratio, we downscale the input image to fit within
/// the input tensor dimensions while preserving the aspect ratio. See <see cref="ModelRunner.CalculateScaleAndPadding()"/>
/// 2. The input is fed through the model
/// 3. Output processing: We apply tensor postprocessing to get the actual masks, bounding boxes, labels and scores as described in the YOLO paper
/// 4. Since the mask is 1/4th of the size of the input, we upscale the mask to the Output dimensions.
/// 5. Depending on whether the aspect ratio matches the input image aspect ratio or the model input aspect ratio, we either clip the padding or not
/// 6. This allows us to either continue calculating with the padded square output or with the image in its original aspect ratio
/// Author: J-Britten
/// 
/// </summary>
public class YOLOSegmentationRunner : SegmentationRunner
{
#region Variables
    
    /// <summary>
    /// By which factor YOLO downscales the mask output. Default: 4
    /// </summary>
    [HideInInspector]
    public int MaskScalingFactor = 4;
    
    [Tooltip("Enabled if the Output shape should be clipped to match the original Image input ratio. " +
             "If disabled, the output will be a square image with padding. The original aspect ratio will be preserved.")]
    public bool ClipPadding = true;

    public int YOLOClasses = 80; //replace this with count from text asset later labels
    
    /// <summary>
    /// Intersection Over Union (IoU) threshold for Non-Maximum Suppression (NMS). 
    /// Lower values result in fewer detections by eliminating overlapping boxes, useful for reducing duplicates.
    /// </summary>
    public float iouThreshold = 0.7f;
    
    /// <summary>
    /// Sets the minimum confidence threshold for detections. 
    /// Objects detected with confidence below this threshold will be disregarded. 
    /// Adjusting this value can help reduce false positives.
    /// </summary>
    public float scoreThreshold = 0.25f;

    /// <summary>
    /// Threshold for the mask values. Default: 0.25
    /// We use this to remove noise from the mask
    /// </summary>
    public float maskThreshold = 0.25f;
    public Texture2D InputTexture;
    public RawImage InputImage; //Image to display the input texture

    //Input Tensor
    private Tensor<float> inputTensor;

    //Output Tensors
    private Tensor<int> labelIDTensor; // N, 1 (label IDs)
    private Tensor<float> scoresTensor; // N, 1 (scores)
    private Tensor<float> maskTensor; // N, 160, 160, 1 (masks)
    private Tensor<float> bboxTensor; // N, 4 (coords are centerX, centerY, width, height)

    public Tensor<float> BBoxTensor {
        get {
            return bboxTensor;
        }
    }

    public override int NumObjDetected => numDetections;
    //Output Data (CPU)
    public float[] BBoxes {
        get {
            return bboxData;
        }
    }
    public int[] LabelIDs {
        get {
            return labelIDData;
        }
    }

    public float[] Scores {
        get {
            return scoresData;
        }
    }

    private int[] labelIDData;
    private float[] bboxData;
    private float[] scoresData;

    

    //Pre Processing Variables
    private TextureTransform inputTextureTransform;

    /// <summary>
    /// This shader takes the masks tensor in form of a buffer as input
    /// It takes the NxWxHx1 and turns it into a 1xWxH where each pixel has the ID of the mask that most likely belongs to
    /// </summary>
    private ComputeShader instanceSegmentationShader;
    private int instanceSegmentationKernel;

    /// <summary>
    /// Number of detected objects each iteration, is used for internal calculations
    /// </summary>
    private int numDetections = 0;


    /// <summary>
    /// Shader that is used to visualize everything YOLO detected
    /// </summary>
    private ComputeShader visualizationShader;
    private RenderTexture visualizationTexture;
    private int visualizationKernel;

    /// <summary>
    /// Variables used to work around a bug when copying data from the GPU to CPU memory, see <see cref="YOLOSegmentationRunner.TransferDataToCPU"/>
    /// </summary>
    private ComputeShader toCPUShader;
    private int extractLabelIDsKernel;
    private ComputeBuffer labelIDsOutputBuffer, bboxOutputBuffer, scoresOutputBuffer;

    public ComputeBuffer ClassIdsBuffer { get => labelIDsOutputBuffer; }

    public ComputeBuffer BBoxBuffer { get => bboxOutputBuffer; }

    public ComputeBuffer ScoresBuffer { get => scoresOutputBuffer; }

#endregion


#region Model Execution

    public override IEnumerator RunModel(params Texture[] inputs) {
        InputImage.texture = inputs[0];
        inputTensor = TextureConverter.ToTensor(inputs[0], inputTextureTransform);
        schedule = worker.ScheduleIterable(inputTensor);

        yield return RunInference();
    }

    protected override bool RequestsDone() {
        return maskTensor.IsReadbackRequestDone() 
        && bboxTensor.IsReadbackRequestDone() 
        && labelIDTensor.IsReadbackRequestDone() 
        && scoresTensor.IsReadbackRequestDone();
    }

    protected override void PeekOutput() {

        labelIDTensor = worker.PeekOutput("output_0") as Tensor<int>; // N, 1 (label IDs)
        scoresTensor = worker.PeekOutput("output_1") as Tensor<float>; // N, 1 (scores)
        bboxTensor = worker.PeekOutput("output_2") as Tensor<float>; // N, 4 (coords are centerX, centerY, width, height)
        maskTensor = worker.PeekOutput("output_3") as Tensor<float>;  // N , 160, 160 ,1      (160,160) by default, adjusted to output width and height for this

        numDetections = Math.Min(labelIDTensor.shape[0], MaxObjects);
        labelIDTensor.ReadbackRequest();
        scoresTensor.ReadbackRequest();
        bboxTensor.ReadbackRequest();
        maskTensor.ReadbackRequest();
    }

    protected override void ReadOutput()
    {
/*        if(OutputBuffer != null) {
            OutputBuffer.Dispose();
        }*/
        CreateInstanceSegmentationMask();
        TransferDataToCPU();

        // Clean up tensors immediately
        inputTensor.Dispose();
        maskTensor.Dispose();
        scoresTensor.Dispose();     
        DisposeOutput(); //Temporary
    }
#endregion

#region Model Preparation
    protected override void PrepareModel()
    {
        CalculateScaleAndPadding();
        //int[] padding = new int[] {pad_w_half, pad_w - pad_w_half, pad_h - pad_h_half, pad_h_half};
        int[] padding = new int[] {0, pad_w, 0, pad_h};

        inputTextureTransform = new TextureTransform()
        //.SetDimensions(InputWidth, InputHeight,3)
        .SetDimensions(scaledWidth, scaledHeight,3)
        .SetChannelSwizzle(ChannelSwizzle.RGBA)
        .SetTensorLayout(TensorLayout.NCHW);
        var internalMaskWidth = InputWidth / MaskScalingFactor;
        var internalMaskHeight = InputHeight / MaskScalingFactor;
        var pad_h_scaled = Mathf.RoundToInt(pad_h / MaskScalingFactor);
        var pad_w_scaled = Mathf.RoundToInt(pad_w / MaskScalingFactor);

        var ctoC = Functional.Constant(new TensorShape(4,4),new float[] //center to corners matrix
        {
                    1,      0,      1,      0,
                    0,      1,      0,      1,
                    -0.5f,  0,      0.5f,   0,
                    0,      -0.5f,  0,      0.5f
        });

        var model = ModelLoader.Load(ModelAsset); //Load YOLO model
        var graph = new FunctionalGraph();
        //var input = graph.AddInput(model, 0); //get input tensor from original model
        var input = graph.AddInput(DataType.Float, new DynamicTensorShape(1,3,-1,-1));

        input = Functional.Pad(input, padding, paddingValue); //At this point, the input tensor is padded to match the input dimensions of the model

        //no preprocessing needed
        var outputs = Functional.Forward(model, input);

        var boxes_scores = outputs[0]; //shape (1, 116, 8400) by default, the number is changing depending on the number of classes the yolo model has

        var classUpper = 4 + YOLOClasses; //upper bounds for tensor creation below, by default YOLO has 80 classes, so it would be 84
        var boxCoords = boxes_scores[0, 0..4, ..];  //The first 4 elements are the box coordinates, shape (4, 8400)
        boxCoords = Functional.Transpose(boxCoords, 0,1); //Transpose to shape (8400, 4)
        var allScores = boxes_scores[0, 4..classUpper, ..]; //The next 80 elements are the class scores, shape ( 80, 8400)
        var scores = Functional.ReduceMax(allScores, 0); //Reduce to shape (8400)
        var classIDs = Functional.ArgMax(allScores, 0); //Reduce to shape (8400)

        var boxCorners = Functional.MatMul(boxCoords, ctoC); //Transform the box coordinates to corners
        
        var indices = Functional.NMS(boxCorners, scores, iouThreshold, scoreThreshold); //iou threshold, score threshold //shape = N (the amount of objects that meet the threshold criteria)
        var indices_unsqueezed = Functional.Unsqueeze(indices, -1); // N
        var indices_2 = Functional.BroadcastTo(indices_unsqueezed, new int[] {4}); // N, 4
        var coords = Functional.Gather(boxCoords, 0, indices_2); //N, 4

        var labelIDs = Functional.Gather(classIDs, 0, indices); //N, ids for labels
        var usedScores = Functional.Gather(scores, 0, indices); //N, all relevant scores

        var mask_coefs = boxes_scores[0, classUpper.., ..]; //shape (1, 32, 8400)
        mask_coefs = Functional.Transpose(mask_coefs, 0,1); //shape (8400, 32)

        var indices_3 = Functional.BroadcastTo(indices_unsqueezed, new int[] {32}); // N, 32
        mask_coefs = Functional.Gather(mask_coefs, 0, indices_3); //shape (N, 32)
       // mask_coefs = Functional.Gather(mask_coefs, 1, indices); //shape (1, 32, N)

        var maskPrototypes = outputs[1]; //shape (1, 32, 160, 160) //consider turning this into shape N, 32, 160, 160 then apply multiplication accordingly

        var coefs = Functional.Reshape(mask_coefs, new int[] {-1, 32,1,1}); //reshape coefficients to match match prototype for multiplication, -1 = N
        var masks = Functional.Mul(coefs, maskPrototypes); //for each result, multiply the coefficients with the prototype on their respective mask layer
        
        masks = Functional.ReduceSum(masks,1);    //shape (N, 160, 160)

        //The following section takes care of upscaling the masks to the original image size
        FunctionalTensor upscalingTensor = Functional.Constant(0.0f); //Create a tensor of zeros
        upscalingTensor = Functional.BroadcastTo(upscalingTensor, new int[] {1,internalMaskHeight,internalMaskWidth}); //Give it the same shape as a mask
        
        //Concatenate the upscaling tensor with the masks
        //we need to do this because sentis interpolation cant work/handle Length(dimX)==0 tensors. These can occur if yolo doesnt detect any object in a frame
        //By concatenating a tensor of zeros, we can avoid this issue and ensure each dimenson is at least of length 1
        upscalingTensor = Functional.Concat(new FunctionalTensor[] {upscalingTensor, masks}, 0); 

        upscalingTensor = Functional.Reshape(upscalingTensor, new int[] {-1,1,internalMaskHeight,internalMaskWidth}); //Reshape concatenated to NCHW shape. Interpolating only works with this shape

        if(ClipPadding) {
            upscalingTensor = upscalingTensor[..,..,0..(internalMaskHeight-pad_h_scaled), 0..(internalMaskWidth-pad_w_scaled)]; //clip padding, must match the padding array specified above
        }

        upscalingTensor = Functional.Interpolate(upscalingTensor, new int[] {OutputHeight,OutputWidth}, mode: "linear"); //upscale (linear is bilinear)
        
        
        masks = upscalingTensor[1..]; //Remove the added zero tensor
        //https://github.com/ultralytics/ultralytics/issues/17672
        // Add sigmoid activation to normalize values between 0 and 1
        masks = Functional.Sigmoid(masks);
        
        masks = Functional.Where( //remove noise from the mask
            Functional.Greater(masks, Functional.Constant( maskThreshold )),
            masks,
            Functional.Constant(0.0f )
        );


       // var nullPointerCatcherTensor = Functional.Constant(-1.0f);
       // nullPointerCatcherTensor = Functional.BroadcastTo(nullPointerCatcherTensor, new int[] {1,1, MaskHeight, MaskWidth});
       // masks = Functional.Concat(new FunctionalTensor[] {masks, nullPointerCatcherTensor}, 0);
//        masks = Functional.Reshape(masks, new int[] {-1, OutputHeight, OutputWidth, 1});



        var newOutputs = new FunctionalTensor[] {labelIDs, usedScores, coords, masks};

        runtimeModel = graph.Compile(newOutputs);

        //runtimeModel = graph.Compile(boxes_scores);
        PreparePostProcessing();
    }


#endregion

#region Post Processing
    private void PreparePostProcessing()
    {   
        PrepareToCPUShader();
        OutputBuffer = new ComputeBuffer(OutputWidth * OutputHeight, sizeof(int) * 2);
        labelIDsOutputBuffer = new ComputeBuffer(MaxObjects, sizeof(int));
        bboxOutputBuffer = new ComputeBuffer(MaxObjects * 4, sizeof(float));
        scoresOutputBuffer = new ComputeBuffer(MaxObjects, sizeof(float));
        bboxData = new float[MaxObjects * 4];
        labelIDData = new int[MaxObjects];
        scoresData = new float[MaxObjects];

        instanceSegmentationShader = Resources.Load<ComputeShader>("Models/YOLO/InstanceSegmentationShader");
        instanceSegmentationKernel = instanceSegmentationShader.FindKernel("InstanceSegmentation");
        instanceSegmentationShader.SetInt("MaskWidth", OutputWidth);
        instanceSegmentationShader.SetInt("MaskHeight", OutputHeight);

        float scale = Mathf.Max((float)OutputWidth/InputHeight, (float)OutputHeight/InputWidth);
        instanceSegmentationShader.SetFloat("Scale", scale);

        visualizationShader = Resources.Load<ComputeShader>("Models/YOLO/InstanceSegmentationVisualizerShader");
        visualizationKernel = visualizationShader.FindKernel("VisualizeSegmentation");
        visualizationShader.SetInt("TextureWidth", OutputWidth);
        visualizationShader.SetInt("TextureHeight", OutputHeight);
        visualizationShader.SetBool("ShowObjects", false);
        // Create visualization texture
        visualizationTexture = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32);
        visualizationTexture.enableRandomWrite = true;
        visualizationTexture.Create();
    }

    private void CreateInstanceSegmentationMask()
    {
        
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "InputMasks", ComputeTensorData.Pin(maskTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "BoundingBoxes", ComputeTensorData.Pin(bboxTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "LabelIDs", ComputeTensorData.Pin(labelIDTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "Scores", ComputeTensorData.Pin(scoresTensor).buffer);
        instanceSegmentationShader.SetBuffer(instanceSegmentationKernel, "OutputBuffer", OutputBuffer);
        instanceSegmentationShader.SetInt("NumMasks", numDetections);
        
        instanceSegmentationShader.Dispatch(instanceSegmentationKernel, 
            Mathf.CeilToInt(OutputWidth / 8f), 
            Mathf.CeilToInt(OutputHeight / 8f), 
            1);
        // Download the instance segmentation buffer to an array
        // Visualize the segmentation
        visualizationShader.SetBuffer(visualizationKernel, "InstanceBuffer", OutputBuffer);
        visualizationShader.SetTexture(visualizationKernel, "OutputTexture", visualizationTexture);
        visualizationShader.Dispatch(visualizationKernel, 
            Mathf.CeilToInt(OutputWidth / 8f), 
            Mathf.CeilToInt(OutputHeight / 8f), 
            1);

        // Assign the visualization texture to your output image
        OutputImage.texture = visualizationTexture;
    }

    /// <summary>
    /// Sentis struggles transferring data from YOLO to the CPU, presumably because even though
    /// the shapes are Nx1 and Nx4 for the LabelIDs and bounding boxes, the actual sizes are Nx1x160x160xM for an unknown reason
    /// It appears to be a bug though.
    /// 
    /// 
    /// This method executes a compute shader which only copies the first "numObjects" (*4) values to new ComputeBuffers which 
    /// then are copied to the CPU to work around this issue
    /// 
    /// </summary>
    private void TransferDataToCPU()
    {
        if (numDetections == 0) return;

        // Create output buffers
       // labelIDsOutputBuffer = new ComputeBuffer(numDetections, sizeof(int));
       // bboxOutputBuffer = new ComputeBuffer(numDetections * 4, sizeof(float));

        // Set shader parameters
        toCPUShader.SetBuffer(extractLabelIDsKernel, "LabelIDsInput", ComputeTensorData.Pin(labelIDTensor).buffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "BBoxInput", ComputeTensorData.Pin(bboxTensor).buffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "ScoresInput", ComputeTensorData.Pin(scoresTensor).buffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "LabelIDsOutput", labelIDsOutputBuffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "BBoxOutput", bboxOutputBuffer);
        toCPUShader.SetBuffer(extractLabelIDsKernel, "ScoresOutput", scoresOutputBuffer);
        toCPUShader.SetInt("NumDetections", numDetections);

        // Dispatch shader
        toCPUShader.Dispatch(extractLabelIDsKernel, numDetections, 1, 1);

        
        //int[] labelResults = new int[numDetections];
        //float[] bboxResults = new float[numDetections * 4];
        //float[] scoresResults = new float[numDetections];
        labelIDsOutputBuffer.GetData(labelIDData);
        bboxOutputBuffer.GetData(bboxData);
        scoresOutputBuffer.GetData(scoresData);
        // Create new native arrays
        //bboxData = new NativeArray<float>(bboxResults, Allocator.Persistent);
        //labelIDData = new NativeArray<int>(labelResults, Allocator.Persistent);
        //scoresData = new NativeArray<float>(scoresResults, Allocater.Persistent);

    }

    private void PrepareToCPUShader()
    {
        toCPUShader = Resources.Load<ComputeShader>("Models/YOLO/ToCPU");
        extractLabelIDsKernel = toCPUShader.FindKernel("ToCPU");
    }

#endregion
    void OnDestroy()
    {
        if (visualizationTexture != null)
        {
            visualizationTexture.Release();
        }
        if (worker != null) worker.Dispose();
        if (labelIDTensor != null) labelIDTensor.Dispose();
        if (scoresTensor != null) scoresTensor.Dispose();
        if (inputTensor != null) inputTensor.Dispose();
        if (bboxTensor != null) bboxTensor.Dispose();
        if (maskTensor != null) maskTensor.Dispose();
        if (OutputBuffer != null)
        {
            OutputBuffer.Dispose();
        }
        if (visualizationTexture != null)
        {
            visualizationTexture.Release();
        }
        if (labelIDsOutputBuffer != null)
        {
            labelIDsOutputBuffer.Dispose();
        }
        if (scoresOutputBuffer != null)
        {
            scoresOutputBuffer.Dispose();
        }
        //if (bboxData.IsCreated) bboxData.Dispose();
        //if (labelIDData.IsCreated) labelIDData.Dispose();
        //if (scoresData.IsCreated) scoresData.Dispose();
    }

    public override void DisposeOutput()
    {
        if (labelIDTensor != null) labelIDTensor.Dispose();
        if (bboxTensor != null) bboxTensor.Dispose();
        if (scoresTensor != null) scoresTensor.Dispose();
       // if (bboxData.IsCreated) bboxData.Dispose();
       // if (labelIDData.IsCreated) labelIDData.Dispose();
       // if (scoresData.IsCreated) scoresData.Dispose();

         
 //       if (labelIDsOutputBuffer != null) labelIDsOutputBuffer.Dispose();
 //       if(bboxOutputBuffer != null)bboxOutputBuffer.Dispose();
 //       if (scoresOutputBuffer != null) scoresOutputBuffer.Dispose();

    }

    private void OnValidate()
    {
        // Calculate aspect ratios
        float inputAspect = (float)InputWidth / InputHeight;
        float outputAspect = (float)OutputWidth / OutputHeight;

        // If aspect ratios don't match (with small float comparison tolerance)
        ClipPadding = !Mathf.Approximately(inputAspect, outputAspect);
    
        float imageAspect = (float) ImageWidth / ImageHeight;
        bool aspectMatch = Mathf.Approximately(imageAspect, outputAspect);
        if(ClipPadding && !aspectMatch) {
            Debug.LogWarning("YOLO: Output Aspect Ratio does not match the Image Aspect Ratio but will be clipped. This will result in a distorted output image" );
        } else if(aspectMatch) {
            Debug.LogWarning("YOLO: Output Aspect Ratio matches the Image Aspect Ratio! Image will be clipped correctly" );
        }
    }

}
