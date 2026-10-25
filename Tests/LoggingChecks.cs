using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Mirage.Diagnostics;

public static class MirageLoggingChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject[] Events(string folder) => Directory.GetFiles(folder, "events-*.jsonl")
        .SelectMany(path => SharedText(path).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        .Select(JObject.Parse).ToArray();
    private static string SharedText(string path)
    {
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(file)) return reader.ReadToEnd();
    }

    public static string Run(string root)
    {
        Directory.CreateDirectory(root);
        BoundedQueueAndRateLimits(Path.Combine(root, "queue"));
        RotationAndSupport(Path.Combine(root, "rotation"));
        ConcurrentWriterAndShutdown(Path.Combine(root, "concurrency"));
        OwnedRetention(Path.Combine(root, "retention"));
        StorageFailure(Path.Combine(root, "failure"));
        return "PASS: actual SessionLogWriter bounded queue/rate limits, concurrent producers, JSONL validity, file rotation, redacted support ZIPs, owned-only retention and storage failure/shutdown.";
    }

    private static void BoundedQueueAndRateLimits(string root)
    {
        DateTime now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        using (var log = new SessionLogWriter(root, capacity: 8, startWorker: false, clock: () => now))
        {
            log.Flush();
            Check(log.TryRecord("REPEAT", "A repeated error", "error"), "First error was rejected");
            Check(!log.TryRecord("REPEAT", "A repeated error", "error"), "Duplicate error was not rate-limited");
            Check(log.SuppressedEventCount == 1, "Suppressed count incorrect");
            now = now.AddSeconds(3);
            Check(log.TryRecord("REPEAT", "A repeated error", "error"), "Expired rate-limit did not allow another error");
            log.Flush();
            Parallel.For(0, 1000, i => log.TryRecord("FLOOD_" + i, "Concurrent event " + i));
            Check(log.DroppedEventCount == 992, "Bounded queue did not reject exactly the excess events");
            log.Flush();
            JObject[] events = Events(log.SessionDirectory);
            Check(events.Count(e => e.Value<string>("code") == "REPEAT") == 2, "Repeat error count incorrect");
            Check(events.Any(e => e.Value<string>("code") == "LOG_EVENT_COUNTS" && e["details"].Value<long>("dropped") == 992), "Dropped-event summary missing");
            Check(events.Select(e => e.Value<long>("eventId")).Distinct().Count() == events.Length, "Event IDs were duplicated under concurrency");
        }
    }

    private static void RotationAndSupport(string root)
    {
        string privateRoot = @"C:\Users\PrivateTestUser";
        using (var log = new SessionLogWriter(root, new[] { privateRoot }, maxFileBytes: 1024, maxFiles: 2, startWorker: false))
        {
            for (int i = 0; i < 40; i++) { log.TryRecord("ROTATE_" + i, new string('x', 140)); log.Flush(); }
            string[] files = Directory.GetFiles(log.SessionDirectory, "events-*.jsonl");
            Check(files.Length == 2 && files.All(path => new FileInfo(path).Length <= 1024), "Rotation exceeded the file-count/size budget");
            Check(log.OverwrittenLogFileCount > 0, "Rotation did not overwrite an old owned log segment");
            log.UpdateManifest(new { model = "DA3-Small", nested = new[] { new { source = privateRoot + @"\Desktop\participant-name.mp4" } } });
            log.UpdateHealth(new { published = 42, latency = 12.0 });
            log.TryRecord("LOCAL_PATH", "Cannot read " + privateRoot + @"\Desktop\participant-name.mp4", "error",
                new { path = @"D:\Research\PrivateVideo.mp4", unix = "/home/private-person/camera.mov" },
                "At " + privateRoot + @"\Project\File.cs:23" + "\nAt public method");
            for (int i = 0; i < 7; i++)
            {
                Check(log.TryExportSupportBundle(out string archivePath, out string error), "Support export failed: " + error);
                Check(File.Exists(archivePath), "Retention removed the just-created support bundle");
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    Check(archive.Entries.Any(e => e.Name == "manifest.json") && archive.Entries.Any(e => e.Name == "health.json"), "Support metadata missing");
                    foreach (var entry in archive.Entries)
                    {
                        using (var reader = new StreamReader(entry.Open()))
                        {
                            string content = reader.ReadToEnd();
                            Check(!content.Contains("PrivateTestUser") && !content.Contains("participant-name") && !content.Contains("PrivateVideo")
                                && !content.Contains("private-person"), "Support ZIP leaked absolute private paths");
                            if (entry.Name.EndsWith(".json")) JToken.Parse(content);
                            if (entry.Name.EndsWith(".jsonl"))
                                foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) JToken.Parse(line);
                        }
                    }
                }
            }
            string support = Path.Combine(Path.GetDirectoryName(root), "Support");
            Check(Directory.GetFiles(support, "*.zip").Length == 5, "Support-bundle retention did not enforce five files");
        }
    }

    private static void ConcurrentWriterAndShutdown(string root)
    {
        var log = new SessionLogWriter(root, capacity: 512);
        Parallel.For(0, 100, i => log.TryRecord("THREAD_" + i, "Thread-safe event " + i));
        log.Dispose();
        log.Dispose();
        Check(!log.Available, "Disposed writer still reports available");
        Check(!log.TryRecord("AFTER", "After shutdown"), "Disposed writer accepted an event");
        JObject[] events = Events(log.SessionDirectory);
        Check(events.Count(e => e.Value<string>("code").StartsWith("THREAD_")) == 100, "Shutdown lost queued events");
        Check(events.Any(e => e.Value<string>("code") == "SESSION_ENDED"), "Clean session-end event missing");
    }

    private static void OwnedRetention(string root)
    {
        Directory.CreateDirectory(root);
        string unrelated = Path.Combine(root, "user-notes");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "unrelated user data");
        string protectedSession;
        using (var protectedLog = new SessionLogWriter(root, startWorker: false)) protectedSession = protectedLog.SessionDirectory;
        File.WriteAllText(Path.Combine(protectedSession, "do-not-delete.txt"), "unknown user content inside a diagnostic directory");
        DateTime sameTime = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 8; i++)
            using (var log = new SessionLogWriter(root + Path.DirectorySeparatorChar, retainedSessions: 2, startWorker: false, clock: () => sameTime)) { }
        Check(File.Exists(Path.Combine(unrelated, "keep.txt")) && File.Exists(Path.Combine(protectedSession, "do-not-delete.txt")), "Retention removed unknown user content");
        Check(Directory.GetDirectories(root).Length == 4, "Owned retention did not leave exactly two eligible sessions plus two protected folders");
        using (var active = new SessionLogWriter(root, retainedSessions: 2, startWorker: false))
        {
            active.Flush();
            for (int i = 0; i < 4; i++)
                using (var next = new SessionLogWriter(root, retainedSessions: 1, startWorker: false)) { }
            Check(File.Exists(Path.Combine(active.SessionDirectory, ".mirage-session"))
                && Directory.GetFiles(active.SessionDirectory, "events-*.jsonl").Length == 1, "Retention damaged another live session");
        }
    }

    private static void StorageFailure(string root)
    {
        Directory.CreateDirectory(root);
        string blocked = Path.Combine(root, "a-file");
        File.WriteAllText(blocked, "not a directory");
        using (var log = new SessionLogWriter(Path.Combine(blocked, "sessions"), startWorker: false))
        {
            Check(!log.Available && !string.IsNullOrWhiteSpace(log.LastError), "Storage failure was not surfaced");
            Check(!log.TryRecord("FAILED", "This cannot be written"), "Failed store accepted an event");
            Check(!log.TryExportSupportBundle(out string path, out string error) && !string.IsNullOrEmpty(error), "Failed store claimed a support export");
        }
    }
}
