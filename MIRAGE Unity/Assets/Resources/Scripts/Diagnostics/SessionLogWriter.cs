using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mirage.Diagnostics
{
    /// <summary>Plain .NET bounded session store. No Unity calls are made on its worker thread.</summary>
    public sealed class SessionLogWriter : IDisposable
    {
        public const int SchemaVersion = 1;
        private readonly object queueGate = new object();
        private readonly object ioGate = new object();
        private readonly Queue<string> queue = new Queue<string>();
        private readonly Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Func<DateTime> utcNow;
        private readonly string[] redactionRoots;
        private readonly int queueCapacity, maximumFiles;
        private readonly long maximumFileBytes;
        private readonly Thread worker;
        private static readonly int NewlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
        private StreamWriter output;
        private FileStream sessionLock;
        private int fileIndex;
        private long fileBytes, sequence, dropped, suppressed, rotations;
        private long reportedDropped, reportedSuppressed;
        private string pendingManifest, pendingHealth;
        private volatile bool stopping, disposed, faulted;
        private int disposeStarted;
        private volatile string error;

        public string SessionId { get; }
        public string RootDirectory { get; }
        public string SessionDirectory { get; }
        public bool Available => !faulted && !disposed;
        public string LastError => error;
        public long DroppedEventCount { get { lock (queueGate) return dropped; } }
        public long SuppressedEventCount { get { lock (queueGate) return suppressed; } }
        public long OverwrittenLogFileCount { get { lock (ioGate) return rotations; } }

        public SessionLogWriter(string rootDirectory, IEnumerable<string> sensitiveRoots = null,
            int capacity = 512, long maxFileBytes = 5 * 1024 * 1024, int maxFiles = 3,
            int retainedSessions = 10, bool startWorker = true, Func<DateTime> clock = null)
        {
            utcNow = clock ?? (() => DateTime.UtcNow);
            queueCapacity = Math.Max(8, capacity);
            maximumFileBytes = Math.Max(1024, maxFileBytes);
            maximumFiles = Math.Max(1, Math.Min(10, maxFiles));
            string fullRoot = Path.GetFullPath(rootDirectory);
            RootDirectory = fullRoot.Length > Path.GetPathRoot(fullRoot).Length
                ? fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullRoot;
            SessionId = utcNow().ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
            SessionDirectory = Path.Combine(RootDirectory, "session-" + SessionId);
            redactionRoots = (sensitiveRoots ?? Array.Empty<string>())
                .Concat(new[] { SessionDirectory, RootDirectory })
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().OrderByDescending(p => p.Length).ToArray();
            try
            {
                Directory.CreateDirectory(SessionDirectory);
                File.WriteAllText(Path.Combine(SessionDirectory, ".mirage-session"), SessionId, new UTF8Encoding(false));
                sessionLock = new FileStream(Path.Combine(SessionDirectory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { PruneOwnedSessions(Math.Max(1, retainedSessions)); }
                catch (Exception exception) when (IsStorageError(exception))
                {
                    TryRecord("LOG_RETENTION_FAILED", "Some old diagnostic sessions could not be removed. Current logging will continue.", "warning");
                }
                TryRecord("SESSION_STARTED", "MIRAGE diagnostic session started.", "info", new { schemaVersion = SchemaVersion }, null, true);
                if (startWorker)
                {
                    worker = new Thread(Pump) { IsBackground = true, Name = "MIRAGE diagnostic writer" };
                    worker.Start();
                }
            }
            catch (Exception exception) when (IsStorageError(exception)) { Fail(exception); }
        }

        public bool TryRecord(string code, string message, string severity = "info", object details = null,
            string stack = null, bool bypassRateLimit = false)
        {
            code = Limit(code ?? "MESSAGE", 100);
            message = Limit(message ?? string.Empty, 4096);
            severity = Limit(severity ?? "info", 20);
            lock (queueGate)
            {
                if (disposed || stopping || faulted) return false;
                if (queue.Count >= queueCapacity) { dropped++; return false; }
                DateTime now = utcNow().ToUniversalTime();
                string key = code + "\n" + severity + "\n" + message;
                if (!bypassRateLimit)
                {
                    if (recent.TryGetValue(key, out DateTime previous) && (now - previous).TotalSeconds < 2)
                    {
                        suppressed++;
                        return false;
                    }
                    if (recent.Count >= 256 && !recent.ContainsKey(key))
                        recent.Remove(recent.OrderBy(p => p.Value).First().Key);
                    recent[key] = now;
                }
                string line = MakeEntry(code, message, severity, details, Limit(stack, 8192), now);
                if (Encoding.UTF8.GetByteCount(line) + NewlineBytes > maximumFileBytes)
                {
                    dropped++;
                    line = MakeEntry("LOG_ENTRY_TRUNCATED", "A diagnostic event exceeded the file budget.", "warning",
                        new { originalCode = code }, null, now);
                }
                queue.Enqueue(line);
            }
            Signal();
            return true;
        }

        public void UpdateManifest(object value) { QueueDocument(value, true); }
        public void UpdateHealth(object value) { QueueDocument(value, false); }

        private void QueueDocument(object value, bool manifest)
        {
            if (!Available || stopping) return;
            string json;
            try { json = JsonConvert.SerializeObject(value, Formatting.Indented); }
            catch (Exception) { TryRecord("LOG_METADATA_INVALID", "Diagnostic metadata could not be serialized.", "warning"); return; }
            lock (queueGate)
            {
                if (manifest) pendingManifest = json;
                else pendingHealth = json;
            }
            Signal();
        }

        private string MakeEntry(string code, string message, string severity, object details, string stack, DateTime now)
        {
            JToken detailToken = null;
            if (details != null)
            {
                try
                {
                    detailToken = JToken.FromObject(details);
                    if (detailToken.ToString(Formatting.None).Length > 8192) detailToken = "[details truncated]";
                }
                catch (Exception) { detailToken = "[details unavailable]"; }
            }
            return new JObject
            {
                ["schemaVersion"] = SchemaVersion, ["utc"] = now.ToString("O"), ["sessionId"] = SessionId,
                ["eventId"] = ++sequence, ["severity"] = severity, ["code"] = code,
                ["message"] = message, ["details"] = detailToken, ["stack"] = stack
            }.ToString(Formatting.None);
        }

        private void Pump()
        {
            try
            {
                while (!stopping && !faulted)
                {
                    wake.WaitOne(250);
                    Flush();
                }
                Flush();
            }
            finally { lock (ioGate) { CloseOutput(); CloseSessionLock(); } }
        }

        /// <summary>Drain queued entries. Called on the background writer, or explicitly for an export/test.</summary>
        public void Flush()
        {
            if (faulted) return;
            lock (ioGate)
            {
                try
                {
                    string manifest, health;
                    List<string> batch;
                    long droppedNow, suppressedNow;
                    lock (queueGate)
                    {
                        manifest = pendingManifest; pendingManifest = null;
                        health = pendingHealth; pendingHealth = null;
                        batch = new List<string>(queue);
                        queue.Clear();
                        droppedNow = dropped; suppressedNow = suppressed;
                    }
                    if (manifest != null) WriteDocument("manifest.json", manifest);
                    if (health != null) WriteDocument("health.json", health);
                    foreach (string line in batch) WriteLine(line);
                    if (droppedNow != reportedDropped || suppressedNow != reportedSuppressed)
                    {
                        string summary;
                        lock (queueGate)
                            summary = MakeEntry("LOG_EVENT_COUNTS", "Diagnostic rate and queue counters updated.", "info",
                                new { dropped = droppedNow, suppressed = suppressedNow }, null, utcNow().ToUniversalTime());
                        WriteLine(summary);
                        reportedDropped = droppedNow; reportedSuppressed = suppressedNow;
                    }
                    output?.Flush();
                }
                catch (Exception exception) when (IsStorageError(exception)) { Fail(exception); }
            }
        }

        private void WriteLine(string line)
        {
            long bytes = Encoding.UTF8.GetByteCount(line) + NewlineBytes;
            if (output == null || fileBytes + bytes > maximumFileBytes)
            {
                CloseOutput();
                fileIndex = fileIndex % maximumFiles + 1;
                string path = Path.Combine(SessionDirectory, $"events-{fileIndex:00}.jsonl");
                if (File.Exists(path)) rotations++;
                output = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                fileBytes = 0;
            }
            output.WriteLine(line);
            fileBytes += bytes;
        }

        private void WriteDocument(string name, string content)
        {
            string target = Path.Combine(SessionDirectory, name);
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }

        public bool TryExportSupportBundle(out string archivePath, out string failure)
        {
            archivePath = null;
            failure = null;
            if (!Available) { failure = LastError ?? "Diagnostic logging is unavailable."; return false; }
            Flush();
            if (!Available) { failure = LastError; return false; }
            lock (ioGate)
            {
                try
                {
                    string supportRoot = Path.Combine(Path.GetDirectoryName(RootDirectory), "Support");
                    Directory.CreateDirectory(supportRoot);
                    string candidate = Path.Combine(supportRoot, "mirage-support-" + utcNow().ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ")
                        + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                    using (var file = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                    {
                        foreach (string path in Directory.GetFiles(SessionDirectory).Where(IsExportFile).OrderBy(p => p))
                        {
                            string text = ReadTextShared(path);
                            bool lines = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                            string redacted = RedactDocument(text, lines);
                            var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest);
                            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(redacted);
                        }
                        var note = archive.CreateEntry("READ-ME.txt");
                        using (var writer = new StreamWriter(note.Open(), new UTF8Encoding(false)))
                            writer.Write("MIRAGE support bundle: session metadata, health and diagnostic logs only. No camera images, video or audio are captured. Absolute workstation paths are redacted. Review exception messages before sharing. Nothing is uploaded automatically.\n");
                    }
                    archivePath = candidate;
                    PruneSupportBundles(supportRoot, candidate);
                    return true;
                }
                catch (Exception exception) when (IsStorageError(exception) || exception is JsonException)
                {
                    failure = "Could not export support information: " + exception.GetType().Name;
                    return false;
                }
            }
        }

        private bool IsExportFile(string path)
        {
            string name = Path.GetFileName(path);
            return name == "manifest.json" || name == "health.json" || Regex.IsMatch(name, @"^events-\d{2}\.jsonl$");
        }

        private static string ReadTextShared(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file, Encoding.UTF8)) return reader.ReadToEnd();
        }

        private string RedactDocument(string text, bool jsonLines)
        {
            if (!jsonLines) return RedactToken(JToken.Parse(text)).ToString(Formatting.Indented);
            var builder = new StringBuilder();
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                builder.AppendLine(RedactToken(JToken.Parse(line)).ToString(Formatting.None));
            return builder.ToString();
        }

        private JToken RedactToken(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                value = Regex.Replace(value, @"(?i)\b[a-z]:[\\/][^\r\n]*", "[PATH]");
                value = Regex.Replace(value, @"/(?:Users|home|private|tmp)/[^\r\n]*", "[PATH]");
                foreach (string root in redactionRoots)
                {
                    value = Regex.Replace(value, Regex.Escape(root), "[LOCAL]", RegexOptions.IgnoreCase);
                    value = Regex.Replace(value, Regex.Escape(root.Replace('\\', '/')), "[LOCAL]", RegexOptions.IgnoreCase);
                }
                return new JValue(value);
            }
            if (token is JObject objectValue)
                foreach (var property in objectValue.Properties().ToList()) property.Value = RedactToken(property.Value);
            else if (token is JArray arrayValue)
                for (int i = 0; i < arrayValue.Count; i++) arrayValue[i] = RedactToken(arrayValue[i]);
            return token;
        }

        private void PruneOwnedSessions(int maximum)
        {
            // Only direct, marked folders containing exclusively our known files
            // are eligible. Unknown user content and filesystem links are retained.
            var candidates = Directory.GetDirectories(RootDirectory)
                .Where(p => string.Equals(p, SessionDirectory, StringComparison.OrdinalIgnoreCase) || IsOwnedSession(p))
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
            int remaining = Math.Max(0, candidates.Count - maximum);
            foreach (string target in candidates)
            {
                if (remaining == 0) break;
                if (string.Equals(target, SessionDirectory, StringComparison.OrdinalIgnoreCase) || !IsOwnedSession(target)) continue;
                Directory.Delete(target, true);
                remaining--;
            }
        }

        private bool IsOwnedSession(string candidate)
        {
            string absolute = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetDirectoryName(absolute), RootDirectory, StringComparison.OrdinalIgnoreCase)) return false;
            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) return false;
            string name = Path.GetFileName(absolute);
            if (!Regex.IsMatch(name, @"^session-\d{8}T\d{9}Z-[a-f0-9]{32}$")) return false;
            string marker = Path.Combine(absolute, ".mirage-session");
            if (!File.Exists(marker) || File.ReadAllText(marker) != name.Substring("session-".Length)) return false;
            if (Directory.GetDirectories(absolute).Length != 0) return false;
            foreach (string file in Directory.GetFiles(absolute))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                string fileName = Path.GetFileName(file);
                if (fileName != ".mirage-session" && fileName != "session.lock" && fileName != "manifest.json" && fileName != "health.json"
                    && fileName != "manifest.json.tmp" && fileName != "health.json.tmp"
                    && !Regex.IsMatch(fileName, @"^events-\d{2}\.jsonl$")) return false;
            }
            string lockPath = Path.Combine(absolute, "session.lock");
            if (!File.Exists(lockPath)) return false;
            try
            {
                using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            catch (IOException) { return false; } // Another live MIRAGE process still owns this session.
            return true;
        }

        private static void PruneSupportBundles(string root, string preserve)
        {
            var files = Directory.GetFiles(root).Where(p => Regex.IsMatch(Path.GetFileName(p), @"^mirage-support-\d{8}T\d{9}Z-[a-f0-9]{8}\.zip$"))
                .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
            foreach (string path in files.Where(p => !string.Equals(p, preserve, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(0, files.Count - 5))) File.Delete(path);
        }

        private void Fail(Exception exception)
        {
            faulted = true;
            // Never call Debug.Log here: Unity's log callback is captured by this store.
            error = "Diagnostic storage unavailable (" + exception.GetType().Name + "). Check free space and write permissions.";
        }

        private void Signal()
        {
            try { wake.Set(); }
            catch (ObjectDisposedException) { /* A producer raced orderly shutdown. */ }
        }

        private void CloseOutput()
        {
            try { output?.Dispose(); }
            catch (Exception exception) when (IsStorageError(exception)) { Fail(exception); }
            finally { output = null; }
        }

        private void CloseSessionLock()
        {
            try { sessionLock?.Dispose(); }
            catch (Exception exception) when (IsStorageError(exception)) { Fail(exception); }
            finally { sessionLock = null; }
        }

        private static bool IsStorageError(Exception exception) => exception is IOException || exception is UnauthorizedAccessException
            || exception is System.Security.SecurityException || exception is NotSupportedException || exception is ArgumentException;
        private static string Limit(string value, int maximum) => value == null || value.Length <= maximum ? value : value.Substring(0, maximum) + " [truncated]";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
            TryRecord("SESSION_ENDED", "MIRAGE diagnostic session ended.", "info", new { dropped = DroppedEventCount, suppressed = SuppressedEventCount }, null, true);
            stopping = true;
            Signal();
            if (worker != null)
            {
                if (worker.Join(2000)) wake.Dispose();
            }
            else
            {
                Flush();
                lock (ioGate) { CloseOutput(); CloseSessionLock(); }
                wake.Dispose();
            }
            disposed = true;
        }
    }
}
