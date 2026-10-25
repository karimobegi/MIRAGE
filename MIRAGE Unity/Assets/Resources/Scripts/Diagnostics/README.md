# Local session diagnostics

Diagnostics starts automatically when a Player session or Editor Play session begins. Do not add a component to each scene. `MirageDiagnosticsRuntime` stays alive across scene changes and collects plain metadata on the Unity main thread; `SessionLogWriter` writes on a background thread.

Each session is stored under `Application.persistentDataPath/MIRAGE/Sessions/session-<UTC timestamp>-<GUID>/`:

- `manifest.json`: app/Unity versions, CPU/GPU, graphics API, input type/resolution and model names, dimensions, backend and readiness. Model checkpoint hashes are not available from the runtime asset contract.
- `health.json`: latest input/publication state, processed update rate, frame age, graphics-queue publication latency and error state.
- `events-01.jsonl` through `events-03.jsonl`: structured events and Unity messages. Each file is capped at 5 MiB and old segments are overwritten cyclically. Events carry UTC time, session/event IDs, severity and a stable code.
- `.mirage-session`: ownership marker used by retention.
- `session.lock`: an exclusive ownership lock while this session is active.

The newest ten recognized diagnostic sessions are retained. Cleanup only removes direct marked session folders containing exclusively known diagnostic files. Active sessions, unknown files/subdirectories and filesystem links are preserved. Up to five generated support ZIPs are retained in the sibling `MIRAGE/Support` folder.

The queue holds at most 512 events. Identical messages are rate-limited over a two-second window; suppression/drop counters are recorded. No image, video or audio content is captured. Local Unity exception messages may contain local paths. Exported support bundles redact absolute paths, contain only diagnostic metadata/logs, and are never uploaded automatically. Review diagnostic messages before sharing a ZIP.

## Application API

```csharp
using Mirage.Diagnostics;

MirageDiagnostics.Record("preset.loaded", "A preset was loaded.", new { name = "Outline people" });
MirageHealthSnapshot status = MirageDiagnostics.GetHealthSnapshot();

if (MirageDiagnostics.TryExportSupportBundle(out string zipPath, out string error))
    ShowMessage("Support information saved locally: " + zipPath);
else
    ShowMessage(error);

MirageDiagnostics.OpenLogFolder();
```

`Record` details must be small plain metadata objects; never pass textures, raw camera data, participant records or Unity object graphs. `Record` and captured Unity log messages are thread-safe. Export/open-folder and health APIs are intended for the main-thread UI. `LastError` and `LatestErrorMessage` describe the most recent error, not proof of a current blocking condition.

If the log destination is unwritable or full, logging reports unavailable through the UI properties and stops writing. It never recursively calls `Debug.Log` from its own failure handler. The application can keep running.

`Tests/Validate-Logging.ps1` executes the actual storage implementation with real concurrent producers, files and ZIPs. Unity bootstrap/event delivery and real scene telemetry still require a Play-mode run.
