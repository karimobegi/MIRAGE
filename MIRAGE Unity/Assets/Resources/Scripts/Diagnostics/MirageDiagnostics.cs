using System;
using System.IO;
using UnityEngine;

namespace Mirage.Diagnostics
{
    [Serializable]
    public sealed class MirageHealthSnapshot
    {
        public string SessionId;
        public string SourceType = "none";
        public string SourceState = "Waiting for input";
        public bool HasInput;
        public long SourceFrameId = -1;
        public long PublishedFrameId = -1;
        public long PublishedFrameCount;
        public double PublishedFramesPerSecond;
        public double FrameAgeSeconds = -1;
        public double LatestPublicationLatencyMilliseconds = -1;
        public bool InputStalled;
        public bool ProcessingStalled;
        public string LatestErrorCode;
        public string LatestErrorMessage;
        public bool LoggingAvailable;
        public long DroppedEventCount;

        internal MirageHealthSnapshot Copy() => (MirageHealthSnapshot)MemberwiseClone();
    }

    /// <summary>Local-only session diagnostics. Details must contain plain metadata, never image or audio data.</summary>
    public static class MirageDiagnostics
    {
        private static readonly object Gate = new object();
        private static SessionLogWriter writer;
        private static MirageHealthSnapshot health = new MirageHealthSnapshot();
        private static string latestErrorCode, latestErrorMessage;
        private static string initializationError;
        private static string exportPath;

        public static string SessionDirectory { get { lock (Gate) return writer?.SessionDirectory; } }
        public static string SessionId { get { lock (Gate) return writer?.SessionId; } }
        public static bool LoggingAvailable { get { lock (Gate) return writer != null && writer.Available; } }
        public static long DroppedEventCount { get { lock (Gate) return writer?.DroppedEventCount ?? 0; } }
        public static string LastExportPath { get { lock (Gate) return exportPath; } }
        public static string LastError
        {
            get { lock (Gate) return writer?.LastError ?? initializationError ?? latestErrorMessage; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Shutdown();
            lock (Gate)
            {
                health = new MirageHealthSnapshot();
                latestErrorCode = latestErrorMessage = initializationError = exportPath = null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            Initialize();
            var host = new GameObject("MIRAGE Diagnostics");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<MirageDiagnosticsRuntime>();
        }

        internal static void Initialize()
        {
            lock (Gate)
            {
                if (writer != null) return;
                try
                {
                    string persistent = Application.persistentDataPath;
                    string project = Path.GetDirectoryName(Application.dataPath);
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    writer = new SessionLogWriter(Path.Combine(persistent, "MIRAGE", "Sessions"),
                        new[] { persistent, project, home });
                    Application.logMessageReceivedThreaded -= CaptureUnityLog;
                    Application.logMessageReceivedThreaded += CaptureUnityLog;
                }
                catch (Exception exception)
                {
                    initializationError = "Diagnostic logging could not start (" + exception.GetType().Name + "). Check free space and write permissions.";
                }
            }
        }

        public static void Record(string code, string message, object details = null, string severity = "info")
        {
            code = Brief(code, 100);
            message = Brief(message, 4096);
            SessionLogWriter current;
            lock (Gate)
            {
                current = writer;
                if (string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase))
                {
                    latestErrorCode = code;
                    latestErrorMessage = message;
                }
            }
            current?.TryRecord(code, message, severity, details);
        }

        private static void CaptureUnityLog(string message, string stack, LogType type)
        {
            message = Brief(message, 4096);
            // Unity invokes this callback from worker threads too. No Unity APIs,
            // scene objects or rendering state are accessed here.
            bool error = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            string severity = error ? "error" : type == LogType.Warning ? "warning" : "info";
            string code = type == LogType.Exception ? "UNITY_EXCEPTION" : error ? "UNITY_ERROR"
                : type == LogType.Warning ? "UNITY_WARNING" : "UNITY_MESSAGE";
            SessionLogWriter current;
            lock (Gate)
            {
                current = writer;
                if (error) { latestErrorCode = code; latestErrorMessage = message; }
            }
            current?.TryRecord(code, message, severity, null, error || type == LogType.Warning ? stack : null);
        }

        public static MirageHealthSnapshot GetHealthSnapshot()
        {
            lock (Gate)
            {
                var snapshot = health.Copy();
                snapshot.SessionId = writer?.SessionId;
                snapshot.LoggingAvailable = writer != null && writer.Available;
                snapshot.DroppedEventCount = writer?.DroppedEventCount ?? 0;
                snapshot.LatestErrorCode = latestErrorCode;
                snapshot.LatestErrorMessage = writer?.LastError ?? initializationError ?? latestErrorMessage;
                return snapshot;
            }
        }

        internal static void SetHealth(MirageHealthSnapshot snapshot)
        {
            SessionLogWriter current;
            lock (Gate) { health = snapshot.Copy(); current = writer; }
            current?.UpdateHealth(GetHealthSnapshot());
        }

        internal static void SetManifest(object manifest)
        {
            SessionLogWriter current;
            lock (Gate) current = writer;
            current?.UpdateManifest(manifest);
        }

        public static bool TryExportSupportBundle(out string archivePath, out string error)
        {
            SessionLogWriter current;
            lock (Gate) current = writer;
            archivePath = null;
            if (current == null) { error = initializationError ?? "Start a MIRAGE session before exporting support information."; return false; }
            bool success = current.TryExportSupportBundle(out archivePath, out error);
            if (success)
            {
                lock (Gate) exportPath = archivePath;
                Record("SUPPORT_EXPORTED", "A local support bundle was created. Nothing was uploaded.");
            }
            return success;
        }

        public static bool OpenLogFolder()
        {
            string path = SessionDirectory;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            Application.OpenURL(new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri);
            return true;
        }

        internal static void Shutdown()
        {
            Application.logMessageReceivedThreaded -= CaptureUnityLog;
            SessionLogWriter current;
            lock (Gate) { current = writer; writer = null; }
            current?.Dispose();
        }

        private static string Brief(string value, int maximum)
        {
            return value == null || value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }
}
