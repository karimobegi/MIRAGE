using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Interface for benchmark management to support null object pattern
/// </summary>
public interface IBenchmarkManager
{
    bool isBenchmarking { get; }
    float maxBenchmarkDuration { get; }
    float BenchmarkDuration { get; }
    int IterationCount { get; }
    bool autoStartEnabled { get; }
    float autoStartDelay { get; }
    bool csvExportEnabled { get; }
    
    void StartBenchmark(float duration = 60f);
    void StopBenchmark();
    void StartIteration();
    void EndIteration();
    void StartSegmentation();
    void EndSegmentation();
    void StartDepthEstimation();
    void EndDepthEstimation();
    void StartInpainting();
    void EndInpainting();
    void StartPostProcessing();
    void EndPostProcessing();
    void PrintBenchmarkResults();
    void PrintBenchmarkResultsCSV();
    void PrintDetailedBenchmarkData();
    void PrintDetailedBenchmarkDataCSV();
    void ExportRawDataToCSV(string filePrefix = "benchmark");
    BenchmarkManager.BenchmarkData[] GetBenchmarkResults();
    
    // Parallel mode support
    void SetParallelMode(bool parallel);
    
    // Auto-start functionality
    void TriggerAutoStart();
    void CancelAutoStart();
    void ResetAutoStart();
    void SetAutoStartEnabled(bool enabled);
    void SetAutoStartDelay(float delay);
    void SetAutoStartOnFirstIteration(bool onFirstIteration);
    void SetCSVExportEnabled(bool enabled);
}

/// <summary>
/// Null object implementation that does nothing - used when benchmarking is disabled
/// </summary>
public class NullBenchmarkManager : IBenchmarkManager
{
    public bool isBenchmarking => false;
    public float maxBenchmarkDuration => 0f;
    public float BenchmarkDuration => 0f;
    public int IterationCount => 0;
    public bool autoStartEnabled => false;
    public float autoStartDelay => 0f;
    public bool csvExportEnabled => false;
    
    public void StartBenchmark(float duration = 60f) { }
    public void StopBenchmark() { }
    public void StartIteration() { }
    public void EndIteration() { }
    public void StartSegmentation() { }
    public void EndSegmentation() { }
    public void StartDepthEstimation() { }
    public void EndDepthEstimation() { }
    public void StartInpainting() { }
    public void EndInpainting() { }
    public void StartPostProcessing() { }
    public void EndPostProcessing() { }
    public void PrintBenchmarkResults() { }
    public void PrintBenchmarkResultsCSV() { }
    public void PrintDetailedBenchmarkData() { }
    public void PrintDetailedBenchmarkDataCSV() { }
    public void ExportRawDataToCSV(string filePrefix = "benchmark") { }
    public BenchmarkManager.BenchmarkData[] GetBenchmarkResults() => new BenchmarkManager.BenchmarkData[0];
    
    // Auto-start functionality (null implementations)
    public void TriggerAutoStart() { }
    public void CancelAutoStart() { }
    public void ResetAutoStart() { }
    public void SetAutoStartEnabled(bool enabled) { }
    public void SetAutoStartDelay(float delay) { }
    public void SetAutoStartOnFirstIteration(bool onFirstIteration) { }
    public void SetCSVExportEnabled(bool enabled) { }
    public void SetParallelMode(bool parallel) { }
}

/// <summary>
/// Manages benchmarking for the Pipeline system, logging performance metrics
/// 
/// Author: J-Britten
/// </summary>
public class BenchmarkManager : MonoBehaviour, IBenchmarkManager
{
    [System.Serializable]
    public class BenchmarkData
    {
        public float timestamp;
        public float segmentationTime;
        public float depthEstimationTime;
        public float inpaintingTime;
        public float postProcessingTime;
        public float totalIterationTime;
        public float unityFPS;
        public int frameCount;
        
        // Stage execution flags for debugging
        public bool segmentationExecuted;
        public bool depthEstimationExecuted;
        public bool inpaintingExecuted;
        public bool postProcessingExecuted;
        
        // Model UpdateRate values at time of execution
        public float segmentationUpdateRate;
        public float depthUpdateRate;
        public float inpaintingUpdateRate;
    }

    [Header("Benchmark Settings")]
    [SerializeField] private bool _isBenchmarking = false;
    [SerializeField] private float _maxBenchmarkDuration = 60f; // Default 60 seconds
    
    [Header("Auto-Start Settings")]
    [SerializeField] private bool _autoStartEnabled = false;
    [SerializeField] private float _autoStartDelay = 2f; // Delay before auto-starting benchmark
    [SerializeField] private bool _autoStartOnFirstIteration = true; // Start benchmark when first iteration begins
    
    [Header("Export Settings")]
    [SerializeField] private bool _csvExportEnabled = true; // Enable/disable automatic CSV export
    
    // Interface properties
    public bool isBenchmarking => _isBenchmarking;
    public float maxBenchmarkDuration => _maxBenchmarkDuration;
    public bool autoStartEnabled => _autoStartEnabled;
    public float autoStartDelay => _autoStartDelay;
    public bool csvExportEnabled => _csvExportEnabled;
    
    private float benchmarkStartTime;
    private float lastIterationStartTime;
    private float currentIterationStartTime;
    private List<BenchmarkData> benchmarkResults = new List<BenchmarkData>();
    private int frameCounter = 0;
    
    // Auto-start tracking
    private bool autoStartTriggered = false;
    private Coroutine autoStartCoroutine;
    private bool firstIterationDetected = false;
    
    // Parallel mode tracking - separate from iteration-based tracking
    private bool isParallelMode = false;
    private float lastParallelSampleTime = 0f;
    private float parallelSampleInterval = 1.0f; // Sample every second in parallel mode
    
    // Parallel mode accumulation for multiple executions per sample period
    private List<float> segmentationTimesThisPeriod = new List<float>();
    private List<float> depthTimesThisPeriod = new List<float>();
    private List<float> inpaintingTimesThisPeriod = new List<float>();
    private List<float> postProcessingTimesThisPeriod = new List<float>();
    
    // Raw execution data for CSV export - stores individual execution times with timestamps
    private List<(float timestamp, float executionTime)> rawSegmentationData = new List<(float, float)>();
    private List<(float timestamp, float executionTime)> rawDepthData = new List<(float, float)>();
    private List<(float timestamp, float executionTime)> rawInpaintingData = new List<(float, float)>();
    private List<(float timestamp, float executionTime)> rawPostProcessingData = new List<(float, float)>();
    
    // Timing data for current iteration
    private float segmentationStartTime;
    private float segmentationEndTime;
    private float depthStartTime;
    private float depthEndTime;
    private float inpaintingStartTime;
    private float inpaintingEndTime;
    private float postProcessingStartTime;
    private float postProcessingEndTime;
    
    // Flags to track which stages ran in current iteration
    private bool segmentationRanThisIteration;
    private bool depthEstimationRanThisIteration;
    private bool inpaintingRanThisIteration;
    private bool postProcessingRanThisIteration;
    
    // Events for Pipeline integration
    public event Action OnBenchmarkStarted;
    public event Action OnBenchmarkStopped;
    
    // Model references for UpdateRate tracking
    private SegmentationRunner segmentationModel;
    private DepthEstimationRunner depthModel;
    private InpaintingRunner inpaintingModel;
    
    private static BenchmarkManager _instance;
    public static BenchmarkManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<BenchmarkManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("BenchmarkManager");
                    _instance = go.AddComponent<BenchmarkManager>();
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
        }
        else if (_instance != this)
        {
            return;
        }
    }

    void Start()
    {
        // Find model references for UpdateRate tracking
        InitializeModelReferences();
        
        // Start auto-start monitoring if enabled
        if (_autoStartEnabled && !_autoStartOnFirstIteration)
        {
            TriggerAutoStart();
        }
    }
    
    /// <summary>
    /// Initialize references to the pipeline models for UpdateRate tracking
    /// </summary>
    private void InitializeModelReferences()
    {
        segmentationModel = FindAnyObjectByType<SegmentationRunner>();
        depthModel = FindAnyObjectByType<DepthEstimationRunner>();
        inpaintingModel = FindAnyObjectByType<InpaintingRunner>();
    }

    void Update()
    {
        if (_isBenchmarking)
        {
            // Check if benchmark should auto-stop
            if (Time.realtimeSinceStartup - benchmarkStartTime >= _maxBenchmarkDuration)
            {
                StopBenchmark();
            }
            
            // Handle parallel mode sampling
            if (isParallelMode && Time.realtimeSinceStartup - lastParallelSampleTime >= parallelSampleInterval)
            {
                SampleParallelTimings();
                lastParallelSampleTime = Time.realtimeSinceStartup;
            }
        }
    }


    public void StartBenchmark(float duration = 60f)
    {
        if (_isBenchmarking)
        {
            Debug.LogWarning("Benchmark is already running!");
            return;
        }

        _maxBenchmarkDuration = duration;
        _isBenchmarking = true;
        benchmarkStartTime = Time.realtimeSinceStartup;
        frameCounter = 0;
        benchmarkResults.Clear();
        
        // Clear raw execution data for CSV export
        rawSegmentationData.Clear();
        rawDepthData.Clear();
        rawInpaintingData.Clear();
        rawPostProcessingData.Clear();

        Debug.Log($"Benchmark started for {duration} seconds.");
        OnBenchmarkStarted?.Invoke();
    }

    public void StopBenchmark()
    {
        if (!_isBenchmarking)
        {
            Debug.LogWarning("No benchmark is currently running!");
            return;
        }

        _isBenchmarking = false;
        
        // Report summary
        ReportBenchmarkResults();
        
        float totalDuration = Time.realtimeSinceStartup - benchmarkStartTime;
        Debug.Log($"Benchmark stopped. Total duration: {totalDuration:F2}s");
        OnBenchmarkStopped?.Invoke();
    }

    public void StartIteration()
    {
        if (!_isBenchmarking) 
        {
            // Handle auto-start on first iteration if enabled
            if (_autoStartEnabled && _autoStartOnFirstIteration && !firstIterationDetected)
            {
                firstIterationDetected = true;
                TriggerAutoStart();
            }
            
            if (!_isBenchmarking) return; // Still not benchmarking after auto-start attempt
        }
        
        // Reset iteration tracking flags
        segmentationRanThisIteration = false;
        depthEstimationRanThisIteration = false;
        inpaintingRanThisIteration = false;
        postProcessingRanThisIteration = false;
        
        // Initialize timing values to 0
        segmentationStartTime = segmentationEndTime = 0f;
        depthStartTime = depthEndTime = 0f;
        inpaintingStartTime = inpaintingEndTime = 0f;
        postProcessingStartTime = postProcessingEndTime = 0f;
        
        currentIterationStartTime = Time.realtimeSinceStartup;
        frameCounter++;
    }

    public void EndIteration()
    {
        if (!_isBenchmarking) return;
        
        float iterationTime = Time.realtimeSinceStartup - currentIterationStartTime;
        float unityFPS = 1.0f / Time.deltaTime;
        
        // Calculate timing values, using 0 for stages that didn't run
        float segTime = segmentationRanThisIteration ? (segmentationEndTime - segmentationStartTime) : 0f;
        float depthTime = depthEstimationRanThisIteration ? (depthEndTime - depthStartTime) : 0f;
        float inpaintTime = inpaintingRanThisIteration ? (inpaintingEndTime - inpaintingStartTime) : 0f;
        float postProcTime = postProcessingRanThisIteration ? (postProcessingEndTime - postProcessingStartTime) : 0f;
        
        BenchmarkData data = new BenchmarkData
        {
            timestamp = Time.realtimeSinceStartup - benchmarkStartTime,
            segmentationTime = segTime,
            depthEstimationTime = depthTime,
            inpaintingTime = inpaintTime,
            postProcessingTime = postProcTime,
            totalIterationTime = iterationTime,
            unityFPS = unityFPS,
            frameCount = frameCounter,
            // Track which stages actually executed
            segmentationExecuted = segmentationRanThisIteration,
            depthEstimationExecuted = depthEstimationRanThisIteration,
            inpaintingExecuted = inpaintingRanThisIteration,
            postProcessingExecuted = postProcessingRanThisIteration,
            // Capture current UpdateRate values
            segmentationUpdateRate = GetSegmentationUpdateRate(),
            depthUpdateRate = GetDepthUpdateRate(),
            inpaintingUpdateRate = GetInpaintingUpdateRate()
        };
        
        benchmarkResults.Add(data);
    }

    // Timing methods for each pipeline stage
    public void StartSegmentation()
    {
        // Handle auto-start on first stage execution in parallel mode
        if (isParallelMode && _autoStartEnabled && _autoStartOnFirstIteration && !firstIterationDetected)
        {
            firstIterationDetected = true;
            TriggerAutoStart();
        }
        
        if (!_isBenchmarking) return;
        segmentationStartTime = Time.realtimeSinceStartup;
        segmentationRanThisIteration = true;
    }

    public void EndSegmentation()
    {
        if (!_isBenchmarking) return;
        segmentationEndTime = Time.realtimeSinceStartup;
        float duration = segmentationEndTime - segmentationStartTime;
        float relativeTimestamp = segmentationEndTime - benchmarkStartTime;
        
        // Store raw execution data for CSV export
        rawSegmentationData.Add((relativeTimestamp, duration));
        
        if (isParallelMode) 
        {
            segmentationTimesThisPeriod.Add(duration);
        }
    }

    public void StartDepthEstimation()
    {
        // Handle auto-start on first stage execution in parallel mode
        if (isParallelMode && _autoStartEnabled && _autoStartOnFirstIteration && !firstIterationDetected)
        {
            firstIterationDetected = true;
            TriggerAutoStart();
        }
        
        if (!_isBenchmarking) return;
        depthStartTime = Time.realtimeSinceStartup;
        depthEstimationRanThisIteration = true;
    }

    public void EndDepthEstimation()
    {
        if (!_isBenchmarking) return;
        depthEndTime = Time.realtimeSinceStartup;
        float duration = depthEndTime - depthStartTime;
        float relativeTimestamp = depthEndTime - benchmarkStartTime;
        
        // Store raw execution data for CSV export
        rawDepthData.Add((relativeTimestamp, duration));
        
        if (isParallelMode) 
        {
            depthTimesThisPeriod.Add(duration);
        }
    }

    public void StartInpainting()
    {
        // Handle auto-start on first stage execution in parallel mode
        if (isParallelMode && _autoStartEnabled && _autoStartOnFirstIteration && !firstIterationDetected)
        {
            firstIterationDetected = true;
            TriggerAutoStart();
        }
        
        if (!_isBenchmarking) return;
        inpaintingStartTime = Time.realtimeSinceStartup;
        inpaintingRanThisIteration = true;
    }

    public void EndInpainting()
    {
        if (!_isBenchmarking) return;
        inpaintingEndTime = Time.realtimeSinceStartup;
        float duration = inpaintingEndTime - inpaintingStartTime;
        float relativeTimestamp = inpaintingEndTime - benchmarkStartTime;
        
        // Store raw execution data for CSV export
        rawInpaintingData.Add((relativeTimestamp, duration));
        
        if (isParallelMode) 
        {
            inpaintingTimesThisPeriod.Add(duration);
        }
    }

    public void StartPostProcessing()
    {
        // Handle auto-start on first stage execution in parallel mode
        if (isParallelMode && _autoStartEnabled && _autoStartOnFirstIteration && !firstIterationDetected)
        {
            firstIterationDetected = true;
            TriggerAutoStart();
        }
        
        if (!_isBenchmarking) return;
        postProcessingStartTime = Time.realtimeSinceStartup;
        postProcessingRanThisIteration = true;
    }

    public void EndPostProcessing()
    {
        if (!_isBenchmarking) return;
        postProcessingEndTime = Time.realtimeSinceStartup;
        float duration = postProcessingEndTime - postProcessingStartTime;
        float relativeTimestamp = postProcessingEndTime - benchmarkStartTime;
        
        // Store raw execution data for CSV export
        rawPostProcessingData.Add((relativeTimestamp, duration));
        
        if (isParallelMode) 
        {
            postProcessingTimesThisPeriod.Add(duration);
        }
    }

    private void ReportBenchmarkResults()
    {
        if (benchmarkResults.Count == 0)
        {
            Debug.Log("No benchmark data collected.");
            return;
        }

        float totalDuration = Time.realtimeSinceStartup - benchmarkStartTime;
        int count = benchmarkResults.Count;

        // Calculate statistics for each metric
        var segStats = CalculateStatistics(benchmarkResults, d => d.segmentationTime);
        var depthStats = CalculateStatistics(benchmarkResults, d => d.depthEstimationTime);
        var inpaintStats = CalculateStatistics(benchmarkResults, d => d.inpaintingTime);
        var postProcStats = CalculateStatistics(benchmarkResults, d => d.postProcessingTime);
        var iterationStats = CalculateStatistics(benchmarkResults, d => d.totalIterationTime);
        var fpsStats = CalculateStatistics(benchmarkResults, d => d.unityFPS);

        // Count how many times each stage ran
        int segmentationCount = 0, depthCount = 0, inpaintingCount = 0, postProcessingCount = 0;
        foreach (var data in benchmarkResults)
        {
            if (data.segmentationExecuted) segmentationCount++;
            if (data.depthEstimationExecuted) depthCount++;
            if (data.inpaintingExecuted) inpaintingCount++;
            if (data.postProcessingExecuted) postProcessingCount++;
        }

        // Build comprehensive report
        System.Text.StringBuilder report = new System.Text.StringBuilder();
        report.AppendLine("\n=== BENCHMARK RESULTS ===");
        report.AppendLine($"Total Duration: {totalDuration:F2}s");
        report.AppendLine($"Total Iterations: {count}");
        report.AppendLine($"Average Pipeline FPS: {1.0f / iterationStats.average:F2}");
        report.AppendLine();

        // Helper function to safely calculate FPS
        System.Func<float, string> SafeFPS = (time) => time > 0f ? $"{1.0f / time:F1}" : "N/A";

        report.AppendLine("SEGMENTATION TIMING:");
        if (segStats.average > 0f)
        {
            report.AppendLine($"  Executed: {segmentationCount}/{count} iterations ({100f * segmentationCount / count:F1}%)");
            report.AppendLine($"  Average: {segStats.average:F4}s ({SafeFPS(segStats.average)} FPS)");
            report.AppendLine($"  Minimum: {segStats.min:F4}s ({SafeFPS(segStats.min)} FPS)");
            report.AppendLine($"  Maximum: {segStats.max:F4}s ({SafeFPS(segStats.max)} FPS)");
            report.AppendLine($"  Std Dev: {segStats.stdDev:F4}s");
        }
        else
        {
            report.AppendLine("  No segmentation timing data available");
        }
        report.AppendLine();

        report.AppendLine("DEPTH ESTIMATION TIMING:");
        if (depthStats.average > 0f)
        {
            report.AppendLine($"  Executed: {depthCount}/{count} iterations ({100f * depthCount / count:F1}%)");
            report.AppendLine($"  Average: {depthStats.average:F4}s ({SafeFPS(depthStats.average)} FPS)");
            report.AppendLine($"  Minimum: {depthStats.min:F4}s ({SafeFPS(depthStats.min)} FPS)");
            report.AppendLine($"  Maximum: {depthStats.max:F4}s ({SafeFPS(depthStats.max)} FPS)");
            report.AppendLine($"  Std Dev: {depthStats.stdDev:F4}s");
        }
        else
        {
            report.AppendLine("  Depth estimation was disabled or did not run");
        }
        report.AppendLine();

        report.AppendLine("INPAINTING TIMING:");
        if (inpaintStats.average > 0f)
        {
            report.AppendLine($"  Executed: {inpaintingCount}/{count} iterations ({100f * inpaintingCount / count:F1}%)");
            report.AppendLine($"  Average: {inpaintStats.average:F4}s ({SafeFPS(inpaintStats.average)} FPS)");
            report.AppendLine($"  Minimum: {inpaintStats.min:F4}s ({SafeFPS(inpaintStats.min)} FPS)");
            report.AppendLine($"  Maximum: {inpaintStats.max:F4}s ({SafeFPS(inpaintStats.max)} FPS)");
            report.AppendLine($"  Std Dev: {inpaintStats.stdDev:F4}s");
        }
        else
        {
            report.AppendLine("  Inpainting was disabled or did not run");
        }
        report.AppendLine();

        report.AppendLine("POST-PROCESSING TIMING:");
        if (postProcStats.average > 0f)
        {
            report.AppendLine($"  Executed: {postProcessingCount}/{count} iterations ({100f * postProcessingCount / count:F1}%)");
            report.AppendLine($"  Average: {postProcStats.average:F4}s ({SafeFPS(postProcStats.average)} FPS)");
            report.AppendLine($"  Minimum: {postProcStats.min:F4}s ({SafeFPS(postProcStats.min)} FPS)");
            report.AppendLine($"  Maximum: {postProcStats.max:F4}s ({SafeFPS(postProcStats.max)} FPS)");
            report.AppendLine($"  Std Dev: {postProcStats.stdDev:F4}s");
        }
        else
        {
            report.AppendLine("  Post-processing was disabled or did not run");
        }
        report.AppendLine();

        report.AppendLine("TOTAL ITERATION TIMING:");
        report.AppendLine($"  Average: {iterationStats.average:F4}s ({1.0f / iterationStats.average:F1} FPS)");
        report.AppendLine($"  Minimum: {iterationStats.min:F4}s ({1.0f / iterationStats.min:F1} FPS)");
        report.AppendLine($"  Maximum: {iterationStats.max:F4}s ({1.0f / iterationStats.max:F1} FPS)");
        report.AppendLine($"  Std Dev: {iterationStats.stdDev:F4}s");
        report.AppendLine();

        report.AppendLine("UNITY FPS:");
        report.AppendLine($"  Average: {fpsStats.average:F2}");
        report.AppendLine($"  Minimum: {fpsStats.min:F2}");
        report.AppendLine($"  Maximum: {fpsStats.max:F2}");
        report.AppendLine($"  Std Dev: {fpsStats.stdDev:F2}");
        report.AppendLine("=========================");

        Debug.Log(report.ToString());
        
        // Also print CSV format for easy copy-paste
        PrintBenchmarkResultsCSV();
        
        // Export raw data to CSV files if enabled
        if (_csvExportEnabled)
        {
            ExportRawDataToCSV();
        }
    }

    /// <summary>
    /// Print benchmark results in CSV format with semicolon separators for easy copy-paste
    /// </summary>
    public void PrintBenchmarkResultsCSV()
    {
        if (benchmarkResults.Count == 0)
        {
            Debug.Log("No benchmark data available for CSV export.");
            return;
        }

        float totalDuration = Time.realtimeSinceStartup - benchmarkStartTime;
        int count = benchmarkResults.Count;

        // Calculate statistics for each metric
        var segStats = CalculateStatistics(benchmarkResults, d => d.segmentationTime);
        var depthStats = CalculateStatistics(benchmarkResults, d => d.depthEstimationTime);
        var inpaintStats = CalculateStatistics(benchmarkResults, d => d.inpaintingTime);
        var postProcStats = CalculateStatistics(benchmarkResults, d => d.postProcessingTime);
        var iterationStats = CalculateStatistics(benchmarkResults, d => d.totalIterationTime);
        var fpsStats = CalculateStatistics(benchmarkResults, d => d.unityFPS);

        // Count how many times each stage ran
        int segmentationCount = 0, depthCount = 0, inpaintingCount = 0, postProcessingCount = 0;
        foreach (var data in benchmarkResults)
        {
            if (data.segmentationExecuted) segmentationCount++;
            if (data.depthEstimationExecuted) depthCount++;
            if (data.inpaintingExecuted) inpaintingCount++;
            if (data.postProcessingExecuted) postProcessingCount++;
        }

        System.Text.StringBuilder csvReport = new System.Text.StringBuilder();
        csvReport.AppendLine("\n=== BENCHMARK RESULTS CSV FORMAT ===");
        csvReport.AppendLine("Copy the lines below and paste into a spreadsheet:");
        csvReport.AppendLine();
        
        // Summary data in CSV format
        csvReport.AppendLine("Metric;Value;Unit");
        csvReport.AppendLine($"Total Duration;{totalDuration.ToString("F2", CultureInfo.InvariantCulture)};seconds");
        csvReport.AppendLine($"Total Iterations;{count};count");
        csvReport.AppendLine($"Average Pipeline FPS;{(1.0f / iterationStats.average).ToString("F2", CultureInfo.InvariantCulture)};fps");
        csvReport.AppendLine();
        
        // Stage statistics in CSV format
        csvReport.AppendLine("Stage;Execution Count;Execution Percentage;Avg Time (s);Min Time (s);Max Time (s);Std Dev (s);Avg FPS;Min FPS;Max FPS");
        
        // Helper function to safely calculate FPS
        System.Func<float, string> SafeFPS = (time) => time > 0f ? (1.0f / time).ToString("F1", CultureInfo.InvariantCulture) : "N/A";
        
        if (segStats.average > 0f)
        {
            csvReport.AppendLine($"Segmentation;{segmentationCount};{(100f * segmentationCount / count).ToString("F1", CultureInfo.InvariantCulture)}%;{segStats.average.ToString("F4", CultureInfo.InvariantCulture)};{segStats.min.ToString("F4", CultureInfo.InvariantCulture)};{segStats.max.ToString("F4", CultureInfo.InvariantCulture)};{segStats.stdDev.ToString("F4", CultureInfo.InvariantCulture)};{SafeFPS(segStats.average)};{SafeFPS(segStats.max)};{SafeFPS(segStats.min)}");
        }
        else
        {
            csvReport.AppendLine("Segmentation;0;0%;0;0;0;0;N/A;N/A;N/A");
        }
        
        if (depthStats.average > 0f)
        {
            csvReport.AppendLine($"Depth Estimation;{depthCount};{(100f * depthCount / count).ToString("F1", CultureInfo.InvariantCulture)}%;{depthStats.average.ToString("F4", CultureInfo.InvariantCulture)};{depthStats.min.ToString("F4", CultureInfo.InvariantCulture)};{depthStats.max.ToString("F4", CultureInfo.InvariantCulture)};{depthStats.stdDev.ToString("F4", CultureInfo.InvariantCulture)};{SafeFPS(depthStats.average)};{SafeFPS(depthStats.max)};{SafeFPS(depthStats.min)}");
        }
        else
        {
            csvReport.AppendLine("Depth Estimation;0;0%;0;0;0;0;N/A;N/A;N/A");
        }
        
        if (inpaintStats.average > 0f)
        {
            csvReport.AppendLine($"Inpainting;{inpaintingCount};{(100f * inpaintingCount / count).ToString("F1", CultureInfo.InvariantCulture)}%;{inpaintStats.average.ToString("F4", CultureInfo.InvariantCulture)};{inpaintStats.min.ToString("F4", CultureInfo.InvariantCulture)};{inpaintStats.max.ToString("F4", CultureInfo.InvariantCulture)};{inpaintStats.stdDev.ToString("F4", CultureInfo.InvariantCulture)};{SafeFPS(inpaintStats.average)};{SafeFPS(inpaintStats.max)};{SafeFPS(inpaintStats.min)}");
        }
        else
        {
            csvReport.AppendLine("Inpainting;0;0%;0;0;0;0;N/A;N/A;N/A");
        }
        
        if (postProcStats.average > 0f)
        {
            csvReport.AppendLine($"Post-Processing;{postProcessingCount};{(100f * postProcessingCount / count).ToString("F1", CultureInfo.InvariantCulture)}%;{postProcStats.average.ToString("F4", CultureInfo.InvariantCulture)};{postProcStats.min.ToString("F4", CultureInfo.InvariantCulture)};{postProcStats.max.ToString("F4", CultureInfo.InvariantCulture)};{postProcStats.stdDev.ToString("F4", CultureInfo.InvariantCulture)};{SafeFPS(postProcStats.average)};{SafeFPS(postProcStats.max)};{SafeFPS(postProcStats.min)}");
        }
        else
        {
            csvReport.AppendLine("Post-Processing;0;0%;0;0;0;0;N/A;N/A;N/A");
        }
        
        csvReport.AppendLine($"Total Iteration;{count};100%;{iterationStats.average.ToString("F4", CultureInfo.InvariantCulture)};{iterationStats.min.ToString("F4", CultureInfo.InvariantCulture)};{iterationStats.max.ToString("F4", CultureInfo.InvariantCulture)};{iterationStats.stdDev.ToString("F4", CultureInfo.InvariantCulture)};{(1.0f / iterationStats.average).ToString("F1", CultureInfo.InvariantCulture)};{(1.0f / iterationStats.max).ToString("F1", CultureInfo.InvariantCulture)};{(1.0f / iterationStats.min).ToString("F1", CultureInfo.InvariantCulture)}");
        csvReport.AppendLine();
        
        // Unity FPS statistics
        csvReport.AppendLine("Unity FPS Statistics;Value;Unit");
        csvReport.AppendLine($"Average Unity FPS;{fpsStats.average.ToString("F2", CultureInfo.InvariantCulture)};fps");
        csvReport.AppendLine($"Minimum Unity FPS;{fpsStats.min.ToString("F2", CultureInfo.InvariantCulture)};fps");
        csvReport.AppendLine($"Maximum Unity FPS;{fpsStats.max.ToString("F2", CultureInfo.InvariantCulture)};fps");
        csvReport.AppendLine($"Unity FPS Std Dev;{fpsStats.stdDev.ToString("F2", CultureInfo.InvariantCulture)};fps");
        
        csvReport.AppendLine("=====================================");

        Debug.Log(csvReport.ToString());
    }

    private struct Statistics
    {
        public float min;
        public float max;
        public float average;
        public float stdDev;
    }

    private Statistics CalculateStatistics(List<BenchmarkData> data, System.Func<BenchmarkData, float> selector)
    {
        if (data.Count == 0) return new Statistics();

        // Filter out zero values (stages that didn't run)
        var validValues = new List<float>();
        foreach (var item in data)
        {
            float value = selector(item);
            if (value > 0f) // Only include positive timing values
            {
                validValues.Add(value);
            }
        }

        if (validValues.Count == 0) 
        {
            return new Statistics
            {
                min = 0f,
                max = 0f,
                average = 0f,
                stdDev = 0f
            };
        }

        float min = float.MaxValue;
        float max = float.MinValue;
        float sum = 0f;

        foreach (var value in validValues)
        {
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
        }

        float average = sum / validValues.Count;

        // Calculate standard deviation
        float sumSquaredDiff = 0f;
        foreach (var value in validValues)
        {
            float diff = value - average;
            sumSquaredDiff += diff * diff;
        }
        float stdDev = Mathf.Sqrt(sumSquaredDiff / validValues.Count);

        return new Statistics
        {
            min = min,
            max = max,
            average = average,
            stdDev = stdDev
        };
    }

    // Public properties for UI display
    public float BenchmarkDuration => _isBenchmarking ? Time.realtimeSinceStartup - benchmarkStartTime : 0f;
    public int IterationCount => frameCounter;
    
    // Public method to get current results
    public BenchmarkData[] GetBenchmarkResults()
    {
        return benchmarkResults.ToArray();
    }
    
    // Public method to manually trigger results report
    public void PrintBenchmarkResults()
    {
        ReportBenchmarkResults();
    }
    
    // Public method to manually export raw data to CSV files
    public void ExportBenchmarkDataToCSV(string customPrefix = null)
    {
        string prefix = string.IsNullOrEmpty(customPrefix) ? "benchmark_manual" : customPrefix;
        ExportRawDataToCSV(prefix);
    }
    
    /// <summary>
    /// Enable or disable automatic CSV export
    /// </summary>
    public void SetCSVExportEnabled(bool enabled)
    {
        _csvExportEnabled = enabled;
        Debug.Log($"CSV export {(enabled ? "enabled" : "disabled")}");
    }
    
    /// <summary>
    /// Get statistics about raw execution data for debugging
    /// </summary>
    public void PrintRawDataStatistics()
    {
        Debug.Log($"Raw Data Statistics:");
        Debug.Log($"  Segmentation executions: {rawSegmentationData.Count}");
        Debug.Log($"  Depth estimation executions: {rawDepthData.Count}");
        Debug.Log($"  Inpainting executions: {rawInpaintingData.Count}");
        Debug.Log($"  Post-processing executions: {rawPostProcessingData.Count}");
        Debug.Log($"  Total benchmark iterations: {benchmarkResults.Count}");
    }
    
    /// <summary>
    /// Get the current UpdateRate for the segmentation model
    /// </summary>
    private float GetSegmentationUpdateRate()
    {
        return segmentationModel != null ? segmentationModel.UpdateRate : 0f;
    }
    
    /// <summary>
    /// Get the current UpdateRate for the depth estimation model
    /// </summary>
    private float GetDepthUpdateRate()
    {
        return depthModel != null ? depthModel.UpdateRate : 0f;
    }
    
    /// <summary>
    /// Get the current UpdateRate for the inpainting model
    /// </summary>
    private float GetInpaintingUpdateRate()
    {
        return inpaintingModel != null ? inpaintingModel.UpdateRate : 0f;
    }
    
    /// <summary>
    /// Get system hardware specifications for CSV export
    /// </summary>
    private (string cpu, string gpu, string ram) GetHardwareSpecs()
    {
        string cpu = SystemInfo.processorType;
        string gpu = SystemInfo.graphicsDeviceName;
        string ram = $"{SystemInfo.systemMemorySize}MB";
        
        return (cpu, gpu, ram);
    }

    /// <summary>
    /// Print detailed iteration-by-iteration data for debugging
    /// </summary>
    public void PrintDetailedBenchmarkData()
    {
        if (benchmarkResults.Count == 0)
        {
            Debug.Log("No benchmark data to display.");
            return;
        }

        System.Text.StringBuilder report = new System.Text.StringBuilder();
        report.AppendLine("\n=== DETAILED BENCHMARK DATA ===");
        report.AppendLine("Frame | Timestamp | Seg(ms) | Depth(ms) | Inpaint(ms) | PostProc(ms) | Total(ms) | Flags");
        report.AppendLine("------|-----------|---------|-----------|-------------|-------------|-----------|----------");

        for (int i = 0; i < benchmarkResults.Count; i++)
        {
            var data = benchmarkResults[i];
            string flags = "";
            flags += data.segmentationExecuted ? "S" : "-";
            flags += data.depthEstimationExecuted ? "D" : "-";
            flags += data.inpaintingExecuted ? "I" : "-";
            flags += data.postProcessingExecuted ? "P" : "-";

            report.AppendLine($"{data.frameCount,5} | {data.timestamp,9:F2} | " +
                            $"{data.segmentationTime * 1000,7:F2} | " +
                            $"{data.depthEstimationTime * 1000,9:F2} | " +
                            $"{data.inpaintingTime * 1000,11:F2} | " +
                            $"{data.postProcessingTime * 1000,11:F2} | " +
                            $"{data.totalIterationTime * 1000,9:F2} | " +
                            $"{flags,8}");
        }

        report.AppendLine("=================================");
        Debug.Log(report.ToString());
        
        // Also print CSV format for detailed data
        PrintDetailedBenchmarkDataCSV();
    }

    /// <summary>
    /// Print detailed iteration-by-iteration data in CSV format for easy copy-paste
    /// </summary>
    public void PrintDetailedBenchmarkDataCSV()
    {
        if (benchmarkResults.Count == 0)
        {
            Debug.Log("No detailed benchmark data to display in CSV format.");
            return;
        }

        System.Text.StringBuilder csvReport = new System.Text.StringBuilder();
        csvReport.AppendLine("\n=== DETAILED BENCHMARK DATA CSV FORMAT ===");
        csvReport.AppendLine("Copy the lines below and paste into a spreadsheet:");
        csvReport.AppendLine();
        
        // CSV Header
        csvReport.AppendLine("Frame;Timestamp (s);Segmentation (ms);Depth Estimation (ms);Inpainting (ms);Post-Processing (ms);Total Iteration (ms);Unity FPS;Segmentation Executed;Depth Executed;Inpainting Executed;PostProcessing Executed;Segmentation UpdateRate;Depth UpdateRate;Inpainting UpdateRate");

        // CSV Data rows
        for (int i = 0; i < benchmarkResults.Count; i++)
        {
            var data = benchmarkResults[i];
            csvReport.AppendLine($"{data.frameCount};{data.timestamp.ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{(data.segmentationTime * 1000).ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{(data.depthEstimationTime * 1000).ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{(data.inpaintingTime * 1000).ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{(data.postProcessingTime * 1000).ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{(data.totalIterationTime * 1000).ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{data.unityFPS.ToString("F2", CultureInfo.InvariantCulture)};" +
                            $"{(data.segmentationExecuted ? "TRUE" : "FALSE")};" +
                            $"{(data.depthEstimationExecuted ? "TRUE" : "FALSE")};" +
                            $"{(data.inpaintingExecuted ? "TRUE" : "FALSE")};" +
                            $"{(data.postProcessingExecuted ? "TRUE" : "FALSE")};" +
                            $"{data.segmentationUpdateRate.ToString("F3", CultureInfo.InvariantCulture)};" +
                            $"{data.depthUpdateRate.ToString("F3", CultureInfo.InvariantCulture)};" +
                            $"{data.inpaintingUpdateRate.ToString("F3", CultureInfo.InvariantCulture)}");
        }

        csvReport.AppendLine("===============================================");
        Debug.Log(csvReport.ToString());
    }

    /// <summary>
    /// Export raw timing data to separate CSV files for each stage/model.
    /// In parallel mode, each stage gets its own file since they don't have synchronized timestamps.
    /// Creates the following files:
    /// - {prefix}_complete.csv: All benchmark data with frame-based synchronization
    /// - {prefix}_segmentation.csv: Raw segmentation execution times
    /// - {prefix}_depth.csv: Raw depth estimation execution times  
    /// - {prefix}_inpainting.csv: Raw inpainting execution times
    /// - {prefix}_postprocessing.csv: Raw post-processing execution times
    /// 
    /// The individual stage files contain the actual execution timestamps and are suitable 
    /// for parallel mode analysis where stages may execute independently.
    /// </summary>
    /// <param name="filePrefix">Prefix for the CSV file names</param>
    public void ExportRawDataToCSV(string filePrefix = "benchmark")
    {
        if (benchmarkResults.Count == 0)
        {
            Debug.LogWarning("No benchmark data available for CSV export.");
            return;
        }

        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string baseFileName = $"{filePrefix}_{timestamp}";
        
        // Create a directory for benchmark exports relative to the project root (not inside Assets)
#if UNITY_ANDROID
        string exportPath = Path.Combine(Application.persistentDataPath, "BenchmarkExports");
#else
        string projectPath = Application.dataPath.Replace("/Assets", "");
        string exportPath = Path.Combine(projectPath, "BenchmarkExports");
#endif
        if (!Directory.Exists(exportPath))
        {
            Directory.CreateDirectory(exportPath);
        }

        try
        {
            // Export complete benchmark data (all stages in one file)
            ExportCompleteDataToCSV(exportPath, baseFileName);
            
            // Export individual stage data (separate files for each stage)
            ExportSegmentationDataToCSV(exportPath, baseFileName);
            ExportDepthEstimationDataToCSV(exportPath, baseFileName);
            ExportInpaintingDataToCSV(exportPath, baseFileName);
            ExportPostProcessingDataToCSV(exportPath, baseFileName);
            
            Debug.Log($"Raw benchmark data exported to: {exportPath}");
            Debug.Log($"Files created with prefix: {baseFileName}");
            
            // Log the exact file paths for easy access
            Debug.Log($"Complete data: {Path.Combine(exportPath, baseFileName + "_complete.csv")}");
            Debug.Log($"Segmentation: {Path.Combine(exportPath, baseFileName + "_segmentation.csv")}");
            Debug.Log($"Depth Estimation: {Path.Combine(exportPath, baseFileName + "_depth.csv")}");
            Debug.Log($"Inpainting: {Path.Combine(exportPath, baseFileName + "_inpainting.csv")}");
            Debug.Log($"Post-Processing: {Path.Combine(exportPath, baseFileName + "_postprocessing.csv")}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Failed to export CSV data: {ex.Message}");
        }
    }

    private void ExportCompleteDataToCSV(string exportPath, string baseFileName)
    {
        string filePath = Path.Combine(exportPath, baseFileName + "_complete.csv");
        
        // Get hardware specifications
        var (cpu, gpu, ram) = GetHardwareSpecs();
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            // Write hardware specifications as metadata comments at the top
            writer.WriteLine($"# Hardware Specifications");
            writer.WriteLine($"# CPU: {cpu}");
            writer.WriteLine($"# GPU: {gpu}");
            writer.WriteLine($"# RAM: {ram}");
            writer.WriteLine($"# Unity Version: {Application.unityVersion}");
            writer.WriteLine($"# Export Date: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine("#");
            
            // Write header with hardware specification columns
            writer.WriteLine("Frame,Timestamp_s,Segmentation_ms,DepthEstimation_ms,Inpainting_ms,PostProcessing_ms,TotalIteration_ms,UnityFPS,SegmentationExecuted,DepthExecuted,InpaintingExecuted,PostProcessingExecuted,SegmentationUpdateRate,DepthUpdateRate,InpaintingUpdateRate,CPU,GPU,RAM_MB");
            
            // Write data rows
            foreach (var data in benchmarkResults)
            {
                writer.WriteLine($"{data.frameCount}," +
                               $"{data.timestamp.ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(data.segmentationTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(data.depthEstimationTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(data.inpaintingTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(data.postProcessingTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(data.totalIterationTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{data.unityFPS.ToString("F2", CultureInfo.InvariantCulture)}," +
                               $"{data.segmentationExecuted}," +
                               $"{data.depthEstimationExecuted}," +
                               $"{data.inpaintingExecuted}," +
                               $"{data.postProcessingExecuted}," +
                               $"{data.segmentationUpdateRate.ToString("F3", CultureInfo.InvariantCulture)}," +
                               $"{data.depthUpdateRate.ToString("F3", CultureInfo.InvariantCulture)}," +
                               $"{data.inpaintingUpdateRate.ToString("F3", CultureInfo.InvariantCulture)}," +
                               $"\"{cpu}\"," +
                               $"\"{gpu}\"," +
                               $"{SystemInfo.systemMemorySize}");
            }
        }
    }

    private void ExportSegmentationDataToCSV(string exportPath, string baseFileName)
    {
        string filePath = Path.Combine(exportPath, baseFileName + "_segmentation.csv");
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            // Write header
            writer.WriteLine("ExecutionIndex,Timestamp_s,ExecutionTime_ms,FPS");
            
            int executionIndex = 0;
            // Use raw execution data for more accurate results
            foreach (var execution in rawSegmentationData)
            {
                executionIndex++;
                float fps = execution.executionTime > 0 ? 1.0f / execution.executionTime : 0f;
                writer.WriteLine($"{executionIndex}," +
                               $"{execution.timestamp.ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(execution.executionTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{fps.ToString("F2", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private void ExportDepthEstimationDataToCSV(string exportPath, string baseFileName)
    {
        string filePath = Path.Combine(exportPath, baseFileName + "_depth.csv");
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            // Write header
            writer.WriteLine("ExecutionIndex,Timestamp_s,ExecutionTime_ms,FPS");
            
            int executionIndex = 0;
            // Use raw execution data for more accurate results
            foreach (var execution in rawDepthData)
            {
                executionIndex++;
                float fps = execution.executionTime > 0 ? 1.0f / execution.executionTime : 0f;
                writer.WriteLine($"{executionIndex}," +
                               $"{execution.timestamp.ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(execution.executionTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{fps.ToString("F2", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private void ExportInpaintingDataToCSV(string exportPath, string baseFileName)
    {
        string filePath = Path.Combine(exportPath, baseFileName + "_inpainting.csv");
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            // Write header
            writer.WriteLine("ExecutionIndex,Timestamp_s,ExecutionTime_ms,FPS");
            
            int executionIndex = 0;
            // Use raw execution data for more accurate results
            foreach (var execution in rawInpaintingData)
            {
                executionIndex++;
                float fps = execution.executionTime > 0 ? 1.0f / execution.executionTime : 0f;
                writer.WriteLine($"{executionIndex}," +
                               $"{execution.timestamp.ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(execution.executionTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{fps.ToString("F2", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private void ExportPostProcessingDataToCSV(string exportPath, string baseFileName)
    {
        string filePath = Path.Combine(exportPath, baseFileName + "_postprocessing.csv");
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            // Write header
            writer.WriteLine("ExecutionIndex,Timestamp_s,ExecutionTime_ms,FPS");
            
            int executionIndex = 0;
            // Use raw execution data for more accurate results
            foreach (var execution in rawPostProcessingData)
            {
                executionIndex++;
                float fps = execution.executionTime > 0 ? 1.0f / execution.executionTime : 0f;
                writer.WriteLine($"{executionIndex}," +
                               $"{execution.timestamp.ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{(execution.executionTime * 1000).ToString("F4", CultureInfo.InvariantCulture)}," +
                               $"{fps.ToString("F2", CultureInfo.InvariantCulture)}");
            }
        }
    }

    /// <summary>
    /// Set whether the pipeline is running in parallel mode
    /// </summary>
    public void SetParallelMode(bool parallel)
    {
        isParallelMode = parallel;
        if (parallel)
        {
            lastParallelSampleTime = Time.realtimeSinceStartup;
            // Clear any existing accumulated timing data
            segmentationTimesThisPeriod.Clear();
            depthTimesThisPeriod.Clear();
            inpaintingTimesThisPeriod.Clear();
            postProcessingTimesThisPeriod.Clear();
        }
        
        // Clear raw execution data when switching modes
        rawSegmentationData.Clear();
        rawDepthData.Clear();
        rawInpaintingData.Clear();
        rawPostProcessingData.Clear();
    }
    
    /// <summary>
    /// Sample timing data in parallel mode
    /// </summary>
    private void SampleParallelTimings()
    {
        if (!_isBenchmarking) return;
        
        frameCounter++;
        float sampleTime = Time.realtimeSinceStartup;
        float unityFPS = 1.0f / Time.deltaTime;
        
        // Calculate average timing for each stage during this period
        float segTime = segmentationTimesThisPeriod.Count > 0 ? segmentationTimesThisPeriod[segmentationTimesThisPeriod.Count - 1] : 0f; // Use most recent
        float depthTime = depthTimesThisPeriod.Count > 0 ? depthTimesThisPeriod[depthTimesThisPeriod.Count - 1] : 0f;
        float inpaintTime = inpaintingTimesThisPeriod.Count > 0 ? inpaintingTimesThisPeriod[inpaintingTimesThisPeriod.Count - 1] : 0f;
        float postProcTime = postProcessingTimesThisPeriod.Count > 0 ? postProcessingTimesThisPeriod[postProcessingTimesThisPeriod.Count - 1] : 0f;
        
        // In parallel mode, total iteration time is the sum of all active stages (since they run in parallel, 
        // the real "iteration time" would be the longest running stage, but for benchmarking purposes, 
        // we'll use the sum to show total computational cost)
        float totalIterTime = segTime + depthTime + inpaintTime + postProcTime;
        
        BenchmarkData data = new BenchmarkData
        {
            timestamp = sampleTime - benchmarkStartTime,
            segmentationTime = segTime,
            depthEstimationTime = depthTime,
            inpaintingTime = inpaintTime,
            postProcessingTime = postProcTime,
            totalIterationTime = totalIterTime, // Use calculated total instead of sample interval
            unityFPS = unityFPS,
            frameCount = frameCounter,
            // Track which stages actually executed since last sample
            segmentationExecuted = segmentationTimesThisPeriod.Count > 0,
            depthEstimationExecuted = depthTimesThisPeriod.Count > 0,
            inpaintingExecuted = inpaintingTimesThisPeriod.Count > 0,
            postProcessingExecuted = postProcessingTimesThisPeriod.Count > 0,
            // Capture current UpdateRate values
            segmentationUpdateRate = GetSegmentationUpdateRate(),
            depthUpdateRate = GetDepthUpdateRate(),
            inpaintingUpdateRate = GetInpaintingUpdateRate()
        };
        
        benchmarkResults.Add(data);
        
        // Clear accumulated timing for next sample period
        segmentationTimesThisPeriod.Clear();
        depthTimesThisPeriod.Clear();
        inpaintingTimesThisPeriod.Clear();
        postProcessingTimesThisPeriod.Clear();
    }

    #region Auto-Start Functionality
    
    /// <summary>
    /// Triggers auto-start of benchmark with configured delay
    /// </summary>
    public void TriggerAutoStart()
    {
        if (_isBenchmarking || autoStartTriggered)
        {
            return; // Already benchmarking or auto-start already triggered
        }

        if (autoStartCoroutine != null)
        {
            StopCoroutine(autoStartCoroutine);
        }

        autoStartTriggered = true;
        autoStartCoroutine = StartCoroutine(AutoStartBenchmarkCoroutine());
    }

    /// <summary>
    /// Coroutine that handles the delayed auto-start of benchmarking
    /// </summary>
    private System.Collections.IEnumerator AutoStartBenchmarkCoroutine()
    {
        Debug.Log($"Auto-start triggered. Benchmark will start in {_autoStartDelay} seconds...");
        
        yield return new WaitForSeconds(_autoStartDelay);
        
        if (!_isBenchmarking) // Check if benchmark wasn't manually started during delay
        {
            Debug.Log("Auto-starting benchmark...");
            StartBenchmark(_maxBenchmarkDuration);
        }
        
        autoStartCoroutine = null;
    }

    /// <summary>
    /// Cancels auto-start if it's pending
    /// </summary>
    public void CancelAutoStart()
    {
        if (autoStartCoroutine != null)
        {
            StopCoroutine(autoStartCoroutine);
            autoStartCoroutine = null;
            autoStartTriggered = false;
            Debug.Log("Auto-start cancelled.");
        }
    }

    /// <summary>
    /// Resets auto-start state - useful for restarting auto-start functionality
    /// </summary>
    public void ResetAutoStart()
    {
        CancelAutoStart();
        autoStartTriggered = false;
        firstIterationDetected = false;
        
        if (_autoStartEnabled && !_autoStartOnFirstIteration)
        {
            TriggerAutoStart();
        }
    }

    /// <summary>
    /// Public method to enable/disable auto-start at runtime
    /// </summary>
    public void SetAutoStartEnabled(bool enabled)
    {
        _autoStartEnabled = enabled;
        
        if (!enabled)
        {
            CancelAutoStart();
        }
        else if (!_autoStartOnFirstIteration && !autoStartTriggered)
        {
            TriggerAutoStart();
        }
    }

    /// <summary>
    /// Public method to change auto-start delay at runtime
    /// </summary>
    public void SetAutoStartDelay(float delay)
    {
        _autoStartDelay = Mathf.Max(0f, delay);
    }

    /// <summary>
    /// Public method to change auto-start trigger mode at runtime
    /// </summary>
    public void SetAutoStartOnFirstIteration(bool onFirstIteration)
    {
        _autoStartOnFirstIteration = onFirstIteration;
        
        // If switching to immediate mode and auto-start is enabled, trigger it
        if (!onFirstIteration && _autoStartEnabled && !autoStartTriggered)
        {
            TriggerAutoStart();
        }
    }

    #endregion
}
