# Find a session's logs and prepare a support report

MIRAGE starts a diagnostic session automatically when the application starts or the Unity Editor enters Play mode. A maintainer does not need to add a logger component to each scene.

## For an operator

1. When a problem occurs, note what you were doing, the selected input and effect, and the approximate time.
2. Open **Help & setup (F1) > Status & support**.
3. Read any **Needs attention** message and its recovery instruction.
4. Click **Export support ZIP**.
5. Wait for the success message and the support folder to open.
6. Review the ZIP, then share it with your support contact using your normal communication channel.

Nothing is uploaded automatically. The ZIP contains diagnostic metadata and text logs. It does not contain video frames, recordings, audio, or the study-settings log. Error text and filenames can still reveal information about a session, so review the archive before sharing it.

Use **Open logs folder** to inspect the current session directly. Export soon after an important issue: older diagnostic files are rotated and older sessions are eventually removed.

## Where the files live

Files are written to Unity's writable per-user application-data location. The buttons above open the right folder without requiring the user to type a path.

```text
Application.persistentDataPath/
  MIRAGE/
    Sessions/
      session-<UTC timestamp>-<GUID>/
        manifest.json
        health.json
        events-01.jsonl
        events-02.jsonl
        events-03.jsonl
    Support/
      <generated support archives>.zip
    Presets/
      <named effect setups>.json
    StudyLogs/
      <separate study-settings records>.json
```

Not every log segment exists immediately. Diagnostic and preset folders are created as needed. StudyLogs is used only when the scene's StudyManager records settings with study logging enabled.

## What each diagnostic file contains

| File | Purpose |
|---|---|
| `manifest.json` | App and Unity versions; CPU/GPU and graphics API; model names, input/output dimensions, execution backend and readiness; input type and resolution. |
| `health.json` | Latest sampled input state, published-frame count, processed update rate, frame age, publication latency, stalled-input/processing flags, and latest error. |
| `events-*.jsonl` | A sequence of structured events and captured Unity messages. Each line is one JSON object with UTC time, session/event IDs, severity, code, message, and optional details/stack trace. |

The current manifest does not contain model checkpoint hashes. Keep export revisions and hashes in the maintainer's model/release record when reproducible model comparisons are required.

The runtime samples session health at bounded intervals; the displayed values are not an instant-by-instant hardware profiler. The manifest is refreshed periodically as scene/model state becomes available.

## Understand the status values

- **Processed updates per second** counts new processed images published by MIRAGE. It is different from the desktop or headset's rendering refresh rate.
- **Age of displayed frame** is how old the published input snapshot is at the time of the sample. A paused recording can produce an old frame intentionally.
- **Publication latency** measures source arrival to submission of the processed frame on Unity's graphics queue. It does not measure physical camera-to-display latency.
- **Input stalled** means a running source has stopped delivering new images for the monitored interval. A paused/stopped recording is treated separately.
- **Processing stalled** means input is available but no processed image has been published recently. The timeout is longer for Moebius because a quality-mode request can take seconds.
- **Latest error** records a recent error; it does not prove that the same problem is still blocking the session. Read the current setup checks and later recovery events as well.

Useful event codes include `input.video.selected`, `input.video.decode_failed`, `input.camera.started`, `input.camera.failed`, `INPUT_STALLED`, `INPUT_RECOVERED`, `PROCESSING_STALLED`, `PROCESSING_RECOVERED`, `UNITY_WARNING`, `UNITY_ERROR`, and `UNITY_EXCEPTION`. Each event also has a readable message.

## Retention and storage failures

Each session has up to three event files, capped at 5 MiB each and reused cyclically. The event queue holds at most 512 events. Identical messages are rate-limited over a two-second window; suppression and drop counts make this visible without flooding the log.

The newest ten recognized diagnostic sessions and up to five generated support ZIPs are retained. Cleanup is limited to folders marked as MIRAGE-owned and containing only known diagnostic files. Unknown content, filesystem links, and active-session protection prevent cleanup from treating arbitrary user folders as old logs. Copy an important exported ZIP to your study/support archive if it must be retained longer.

If the destination becomes unwritable or runs out of space, the help window reports that logging is unavailable. The application can continue running. The logger's own failure path does not repeatedly write new Unity errors. Check available space and user-folder permissions, then export available files or restart after storage is fixed.

## Support export and study records

The support ZIP includes the current session's manifest, health snapshot, event logs, and an explanatory `READ-ME.txt`. Export redacts absolute Windows paths, Unix user paths, and recognized home/project locations. Raw local logs can contain workstation paths, and arbitrary message text may contain additional information that automatic redaction cannot identify.

StudyManager's records are separate under `MIRAGE/StudyLogs`. Their filenames include the configured participant ID, and their JSON records capture effect/layer/model settings. They are not added to diagnostic support ZIPs. Reusable named presets are separate again under `MIRAGE/Presets` and contain no participant records.

When the scene contains StudyManager, **Status & support > Open study settings folder** opens those separate records. Study logging is controlled by the maintainer's StudyManager configuration.

## For maintainers adding events

Use `MirageDiagnostics.Record` with a stable event code, a short readable message, and a small plain metadata object. Never pass images, textures, camera/audio buffers, participant records, or complete Unity object graphs. In a source checkout, `MIRAGE Unity/Assets/Resources/Scripts/Diagnostics/README.md` contains the API notes.

Storage behavior is tested by `Tests/Validate-Logging.ps1`. Bootstrap, real scene event delivery, graphics measurements, and operator recovery still need a Unity Play-mode and exported-player run.
