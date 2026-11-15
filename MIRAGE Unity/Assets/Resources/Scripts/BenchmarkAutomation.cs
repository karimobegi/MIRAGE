using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

/// <summary>
/// Automation script for running benchmarks with different videos on a single scene.
/// Should be placed in an empty scene and will load the benchmark scene once, then cycle through videos.
/// 
/// Author: J-Britten
/// </summary>
public class BenchmarkAutomation : MonoBehaviour
{
    [Header("Video Configuration")]
    [SerializeField] private List<VideoClip> videoClips = new List<VideoClip>();
    [SerializeField] private string benchmarkSceneName = "BenchmarkScene";
    [SerializeField] private int repetitionsPerVideo = 1;
    
    [Header("Shared Settings")]
    [SerializeField] private float benchmarkDuration = 60f;
    [SerializeField] private bool useVideoLength = false; // If true, benchmark duration will be the video length
    [SerializeField] private float videoSetupDelay = 2f;
    [SerializeField] private float delayBetweenVideos = 3f;
    [SerializeField] private bool autoStartOnPlay = true;
    [SerializeField] private bool returnToAutomationScene = true;
    [SerializeField] private string automationSceneName = "BenchmarkAutomation";
    
    [Header("Status")]
    [SerializeField] private bool isRunning = false;
    [SerializeField] private int currentVideoIndex = -1;
    [SerializeField] private int currentRepetition = 0;
    [SerializeField] private string currentVideoName = "";
    
    // Internal state
    private Coroutine automationCoroutine;
    private IBenchmarkManager currentBenchmarkManager;
    private VideoPlayer videoPlayer;
    
    // Singleton instance to prevent duplicates
    private static BenchmarkAutomation _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        _instance = null;
    }

    private static string FormatObjectId(UnityEngine.Object target)
    {
#if UNITY_6000_4_OR_NEWER
        return target.GetEntityId().ToString();
#else
        return target.GetInstanceID().ToString();
#endif
    }
    
    /// <summary>
    /// Public access to the singleton instance
    /// </summary>
    public static BenchmarkAutomation Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<BenchmarkAutomation>();
            }
            return _instance;
        }
    }
    
    void Awake()
    {
        // Implement singleton pattern to prevent duplicates when scenes load
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject); // Persist across scene loads
            Debug.Log("BenchmarkAutomation instance created and set to persist across scenes");
        }
        else if (_instance != this)
        {
            Debug.LogWarning("Duplicate BenchmarkAutomation detected, destroying duplicate");
            Destroy(gameObject);
            return;
        }
    }
    
    void Start()
    {
        if (autoStartOnPlay)
        {
            StartAutomation();
        }
    }
    
    /// <summary>
    /// Start the benchmark automation process
    /// </summary>
    public void StartAutomation()
    {
        if (isRunning)
        {
            Debug.LogWarning("Benchmark automation is already running!");
            return;
        }
        
        if (videoClips.Count == 0)
        {
            Debug.LogError("No video clips configured!");
            return;
        }
        
        Debug.Log($"Starting benchmark automation with {videoClips.Count} videos, {repetitionsPerVideo} repetition(s) each");
        isRunning = true;
        currentVideoIndex = -1;
        currentRepetition = 0;
        
        automationCoroutine = StartCoroutine(RunAutomationSequence());
    }
    
    /// <summary>
    /// Stop the benchmark automation process
    /// </summary>
    public void StopAutomation()
    {
        if (!isRunning)
        {
            Debug.LogWarning("Benchmark automation is not running!");
            return;
        }
        
        Debug.Log("Stopping benchmark automation");
        isRunning = false;
        
        if (automationCoroutine != null)
        {
            StopCoroutine(automationCoroutine);
            automationCoroutine = null;
        }
        
        // Stop any running benchmark
        if (currentBenchmarkManager != null && currentBenchmarkManager.isBenchmarking)
        {
            currentBenchmarkManager.StopBenchmark();
        }
        
        // Unsubscribe from events
        UnsubscribeFromBenchmarkEvents();
    }
    
    /// <summary>
    /// Main automation coroutine that handles loading the benchmark scene and cycling through videos
    /// </summary>
    private IEnumerator RunAutomationSequence()
    {
        Debug.Log("=== STARTING BENCHMARK AUTOMATION SEQUENCE ===");
        
        // Start cycling through videos with repetitions
        for (int i = 0; i < videoClips.Count; i++)
        {
            if (!isRunning) break; // Check if stopped
            
            currentVideoIndex = i;
            VideoClip videoClip = videoClips[i];
            currentVideoName = videoClip != null ? videoClip.name : "null";
            
            Debug.Log($"=== PROCESSING VIDEO {i + 1}/{videoClips.Count}: {currentVideoName} ===");
            
            // Run repetitions for this video
            for (int rep = 0; rep < repetitionsPerVideo; rep++)
            {
                if (!isRunning) break; // Check if stopped
                
                currentRepetition = rep;
                Debug.Log($"=== REPETITION {rep + 1}/{repetitionsPerVideo} for video: {currentVideoName} ===");
                Debug.Log($"About to DESTROY and RELOAD benchmark scene: {benchmarkSceneName}");
                
                // Reload the benchmark scene for each repetition to ensure clean state
                yield return LoadSceneAsync(benchmarkSceneName);
                
                Debug.Log("Scene reload complete, setting up fresh components...");
                
                // Find and setup benchmark manager and video player
                yield return SetupBenchmarkComponents();
                
                if (videoPlayer == null)
                {
                    Debug.LogError("No VideoPlayer found in benchmark scene!");
                    CompleteAutomation();
                    yield break;
                }
                
                Debug.Log("Setting up video on fresh VideoPlayer...");
                
                // Setup the video
                yield return SetupVideo(videoClip);
                
                if (currentBenchmarkManager != null)
                {
                    float actualBenchmarkDuration = GetBenchmarkDuration(videoClip);
                    Debug.Log($"Starting benchmark for video {currentVideoName}, repetition {rep + 1}/{repetitionsPerVideo} (Duration: {actualBenchmarkDuration:F2}s)");
                    
                    // Subscribe to benchmark completion - the event will continue the sequence
                    SubscribeToBenchmarkEvents();
                    
                    currentBenchmarkManager.StartBenchmark(actualBenchmarkDuration);
                    
                    // Exit coroutine here - OnBenchmarkCompleted will continue the sequence
                    yield break;
                }
                else
                {
                    Debug.LogError("No BenchmarkManager found in scene");
                    
                    // Continue to next repetition after delay
                    if (rep < repetitionsPerVideo - 1 || i < videoClips.Count - 1)
                    {
                        Debug.Log($"Waiting {delayBetweenVideos}s before continuing...");
                        yield return new WaitForSeconds(delayBetweenVideos);
                    }
                }
            }
        }
        
        // Complete automation (only reached if no benchmark manager found)
        CompleteAutomation();
    }
    
    /// <summary>
    /// Load a scene asynchronously, explicitly destroying the current scene
    /// </summary>
    private IEnumerator LoadSceneAsync(string sceneName)
    {
        Debug.Log($"Destroying current scene and loading: {sceneName}");
        
        // Explicitly use Single mode to destroy current scene and load new one
        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        
        // Log progress
        while (!asyncLoad.isDone)
        {
            yield return null;
        }
        
        Debug.Log($"Scene completely reloaded: {sceneName}");
        
        // Wait an additional frame to ensure scene is fully loaded
        yield return null;
    }
    
    /// <summary>
    /// Find and setup the benchmark manager and video player in the current scene
    /// </summary>
    private IEnumerator SetupBenchmarkComponents()
    {
        Debug.Log("Setting up benchmark components in freshly loaded scene...");
        
        // Wait a frame to ensure all objects are initialized
        yield return null;
        
        // Clear previous references to ensure we find fresh components
        currentBenchmarkManager = null;
        videoPlayer = null;
        
        // Try to find BenchmarkManager in the scene
        BenchmarkManager benchmarkManager = FindFirstObjectByType<BenchmarkManager>();
        
        if (benchmarkManager != null)
        {
            currentBenchmarkManager = benchmarkManager;
            
            // Configure the benchmark manager for automation
            benchmarkManager.SetAutoStartEnabled(false); // Disable auto-start since we control it
            benchmarkManager.SetCSVExportEnabled(false); // Disable automatic export - we'll handle it manually with custom names
            
            Debug.Log($"Fresh BenchmarkManager found and configured: {FormatObjectId(benchmarkManager)}");
        }
        else
        {
            // Try to find NullBenchmarkManager or create one
            currentBenchmarkManager = new NullBenchmarkManager();
            Debug.LogWarning("No BenchmarkManager found in scene, using NullBenchmarkManager");
        }
        
        // Find VideoPlayer component
        videoPlayer = FindFirstObjectByType<VideoPlayer>();
        
        if (videoPlayer != null)
        {
            Debug.Log($"Fresh VideoPlayer found: {FormatObjectId(videoPlayer)}");
        }
        else
        {
            Debug.LogError("No VideoPlayer component found in the benchmark scene!");
        }
    }
    
    /// <summary>
    /// Setup a specific video on the VideoPlayer
    /// </summary>
    private IEnumerator SetupVideo(VideoClip videoClip)
    {
        if (videoPlayer == null)
        {
            Debug.LogError("VideoPlayer not found!");
            yield break;
        }
        
        // Stop the current video
        videoPlayer.Stop();
        
        // Wait a frame for stop to complete
        yield return null;
        
        // Set the new video clip
        videoPlayer.clip = videoClip;
        
        // Reset to beginning
        videoPlayer.time = 0;
        
        // Wait for video setup delay
        yield return new WaitForSeconds(videoSetupDelay);
        
        // Start playing the video
        videoPlayer.Play();
        
        Debug.Log($"Video setup complete: {(videoClip != null ? videoClip.name : "null")}");
    }
    
    /// <summary>
    /// Get the benchmark duration for the current video
    /// </summary>
    private float GetBenchmarkDuration(VideoClip videoClip)
    {
        if (useVideoLength && videoClip != null)
        {
            // Use video length as benchmark duration
            float videoDuration = (float)videoClip.length;
            Debug.Log($"Using video length as benchmark duration: {videoDuration:F2}s for video: {videoClip.name}");
            return videoDuration;
        }
        else
        {
            // Use fixed benchmark duration
            Debug.Log($"Using fixed benchmark duration: {benchmarkDuration:F2}s");
            return benchmarkDuration;
        }
    }
    
    /// <summary>
    /// Subscribe to benchmark manager events
    /// </summary>
    private void SubscribeToBenchmarkEvents()
    {
        if (currentBenchmarkManager is BenchmarkManager bm)
        {
            bm.OnBenchmarkStopped += OnBenchmarkCompleted;
        }
    }
    
    /// <summary>
    /// Unsubscribe from benchmark manager events
    /// </summary>
    private void UnsubscribeFromBenchmarkEvents()
    {
        if (currentBenchmarkManager is BenchmarkManager bm)
        {
            bm.OnBenchmarkStopped -= OnBenchmarkCompleted;
        }
    }
    
    /// <summary>
    /// Called when the current benchmark completes - continues the automation sequence
    /// </summary>
    private void OnBenchmarkCompleted()
    {
        Debug.Log($"Benchmark completed for video {currentVideoName}, repetition {currentRepetition + 1}/{repetitionsPerVideo}...");
        
        // Export results with custom video name and repetition number (always export in automation mode)
        if (currentBenchmarkManager != null)
        {
            string exportPrefix = $"benchmark_{currentVideoName}_rep{currentRepetition + 1}_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            currentBenchmarkManager.ExportRawDataToCSV(exportPrefix);
        }
        
        UnsubscribeFromBenchmarkEvents();
        
        // Continue to next repetition or next video
        StartCoroutine(ContinueSequence());
    }
    
    /// <summary>
    /// Continue the automation sequence - handles both repetitions and video progression
    /// </summary>
    private IEnumerator ContinueSequence()
    {
        // Check if we need more repetitions for the current video
        if (currentRepetition + 1 < repetitionsPerVideo)
        {
            // More repetitions needed for current video
            currentRepetition++;
            Debug.Log($"Starting repetition {currentRepetition + 1}/{repetitionsPerVideo} for video {currentVideoName}");
            Debug.Log($"Waiting {delayBetweenVideos}s before next repetition...");
            yield return new WaitForSeconds(delayBetweenVideos);
            
            // Continue with same video, next repetition
            StartCoroutine(RunNextRepetition());
        }
        else
        {
            // All repetitions for current video complete, move to next video
            currentRepetition = 0; // Reset for next video
            
            if (currentVideoIndex < videoClips.Count - 1)
            {
                Debug.Log($"All repetitions complete for {currentVideoName}. Moving to next video...");
                Debug.Log($"Waiting {delayBetweenVideos}s before next video...");
                yield return new WaitForSeconds(delayBetweenVideos);
                
                // Continue with next video
                StartCoroutine(ProcessNextVideo());
            }
            else
            {
                // All videos and repetitions completed
                Debug.Log("All videos and repetitions completed!");
                CompleteAutomation();
            }
        }
    }
    
    /// <summary>
    /// Run the next repetition of the current video
    /// </summary>
    private IEnumerator RunNextRepetition()
    {
        VideoClip videoClip = videoClips[currentVideoIndex];
        
        Debug.Log($"=== REPETITION {currentRepetition + 1}/{repetitionsPerVideo} for video: {currentVideoName} ===");
        Debug.Log($"About to DESTROY and RELOAD benchmark scene: {benchmarkSceneName}");
        
        // Reload the benchmark scene for each repetition to ensure clean state
        yield return LoadSceneAsync(benchmarkSceneName);
        
        Debug.Log("Scene reload complete, setting up fresh components...");
        
        // Find and setup benchmark manager and video player
        yield return SetupBenchmarkComponents();
        
        if (videoPlayer == null)
        {
            Debug.LogError("No VideoPlayer found in benchmark scene!");
            StartCoroutine(ContinueSequence());
            yield break;
        }
        
        Debug.Log("Setting up video on fresh VideoPlayer...");
        
        // Setup the video
        yield return SetupVideo(videoClip);
        
        if (currentBenchmarkManager != null)
        {
            float actualBenchmarkDuration = GetBenchmarkDuration(videoClip);
            Debug.Log($"Starting benchmark for video {currentVideoName}, repetition {currentRepetition + 1}/{repetitionsPerVideo} (Duration: {actualBenchmarkDuration:F2}s)");
            
            // Subscribe to benchmark completion - the event will continue the sequence
            SubscribeToBenchmarkEvents();
            
            currentBenchmarkManager.StartBenchmark(actualBenchmarkDuration);
        }
        else
        {
            Debug.LogError("BenchmarkManager not found after scene load!");
            StartCoroutine(ContinueSequence());
        }
    }
    
    /// <summary>
    /// Process the next video in the sequence
    /// </summary>
    private IEnumerator ProcessNextVideo()
    {
        currentVideoIndex++;
        currentRepetition = 0; // Reset repetition counter for new video
        
        if (currentVideoIndex >= videoClips.Count || !isRunning)
        {
            CompleteAutomation();
            yield break;
        }
        
        VideoClip videoClip = videoClips[currentVideoIndex];
        currentVideoName = videoClip != null ? videoClip.name : "null";
        
        Debug.Log($"=== PROCESSING NEXT VIDEO {currentVideoIndex + 1}/{videoClips.Count}: {currentVideoName} ===");
        Debug.Log($"=== REPETITION 1/{repetitionsPerVideo} ===");
        Debug.Log($"About to DESTROY and RELOAD benchmark scene: {benchmarkSceneName}");
        
        // Reload the benchmark scene for each video to ensure clean state
        yield return LoadSceneAsync(benchmarkSceneName);
        
        Debug.Log("Scene reload complete, setting up fresh components...");
        
        // Find and setup benchmark manager and video player
        yield return SetupBenchmarkComponents();
        
        if (videoPlayer == null)
        {
            Debug.LogError("No VideoPlayer found in benchmark scene!");
            StartCoroutine(ContinueSequence());
            yield break;
        }
        
        Debug.Log("Setting up video on fresh VideoPlayer...");
        
        // Setup the video
        yield return SetupVideo(videoClip);
        
        if (currentBenchmarkManager != null)
        {
            // Start the benchmark
            float actualBenchmarkDuration = GetBenchmarkDuration(videoClip);
            Debug.Log($"Starting benchmark for video {currentVideoName}, repetition 1/{repetitionsPerVideo} (Duration: {actualBenchmarkDuration:F2}s)");
            
            // Subscribe to benchmark completion
            SubscribeToBenchmarkEvents();
            
            currentBenchmarkManager.StartBenchmark(actualBenchmarkDuration);
        }
        else
        {
            Debug.LogError("No BenchmarkManager found");
            
            // Continue sequence
            StartCoroutine(ContinueSequence());
        }
    }
    
    /// <summary>
    /// Complete the automation sequence
    /// </summary>
    private void CompleteAutomation()
    {
        StartCoroutine(CompleteAutomationAsync());
    }
    
    /// <summary>
    /// Async completion of automation
    /// </summary>
    private IEnumerator CompleteAutomationAsync()
    {
        // Return to automation scene if requested, otherwise stop the application
        if (returnToAutomationScene && !string.IsNullOrEmpty(automationSceneName))
        {
            Debug.Log($"Returning to automation scene: {automationSceneName}");
            yield return LoadSceneAsync(automationSceneName);
        }
        else
        {
            Debug.Log("Benchmark automation completed! Stopping application...");
            
            // Wait a brief moment to ensure the log message is written
            yield return new WaitForSeconds(1f);
            
            // Stop the application
            #if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
            #else
                Application.Quit();
            #endif
        }
        
        // Complete automation
        isRunning = false;
        automationCoroutine = null;
        
        Debug.Log("Benchmark automation completed!");
    }
    
    /// <summary>
    
    void OnDestroy()
    {
        StopAutomation();
        if (_instance == this) _instance = null;
    }
}