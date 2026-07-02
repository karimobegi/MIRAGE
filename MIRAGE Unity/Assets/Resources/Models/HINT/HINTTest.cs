using UnityEngine;

 
public class HINTTest : MonoBehaviour
{
    public Unity.InferenceEngine.ModelAsset modelAsset;
 
    void Start()
    {
        // Load model
        var model = Unity.InferenceEngine.ModelLoader.Load(modelAsset);
        Debug.Log("Model loaded");
 
        // Create worker on GPU
        var worker = new Unity.InferenceEngine.Worker(model, Unity.InferenceEngine.BackendType.GPUCompute);
        Debug.Log("Worker created");
 
        // Create dummy inputs
        // image: [1, 3, 256, 256] - random values in [0, 1]
        var image = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 3, 512, 512));
        // mask: [1, 1, 256, 256] - center square mask
        var mask = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 1, 512, 512));
 
        // Fill mask with a center square (1 = missing region)
       var maskData = new float[1 * 1 * 512 * 512];
        for (int y = 128; y < 384; y++)
            for (int x = 128; x < 384; x++)
                maskData[y * 512 + x] = 1f;
        mask = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 1, 512, 512), maskData);
 
        Debug.Log("Running inference...");
 
        // Set inputs and run
        worker.SetInput("image", image);
        worker.SetInput("mask", mask);
        worker.Schedule();
 
        // Read output
        var output = worker.PeekOutput("output") as Unity.InferenceEngine.Tensor<float>;
        output.ReadbackAndClone().Dispose();
        Debug.Log($"Output shape: {output.shape}");
        Debug.Log("HINT ONNX validation PASSED");
 
        // Cleanup
        image.Dispose();
        mask.Dispose();
        output.Dispose();
        worker.Dispose();
    }
}
 