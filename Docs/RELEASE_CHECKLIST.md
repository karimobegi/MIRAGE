# Prepare a reusable MIRAGE operator release

This is a maintainer checklist for a Windows desktop release. It is not a claim that a release build has already been created or tested. The current repository and local source checks do not establish a successful Unity player build.

## Define the handoff

- Choose the supported Windows version, GPU/driver configuration, display resolution, input devices, and whether the release uses a fixed demonstration video or a camera.
- Choose one initial scene and a documented starting configuration. Use the Desktop UI workflow for a first operator release.
- Record the application version, Git commit, Unity version, package versions, model filenames/checksums, model input dimensions, and enabled backend.
- Include the [operator guide](OPERATOR_GUIDE.md), a short first-run task, and a support contact.

## Prepare the model and media pack

- Acquire and export the actual baseline models; keep export commands, options, revisions, and hashes with the release record.
- Verify each upstream model/code license and the rights to redistribute its weights in the intended release. Include required license/attribution files and record the source URLs. Do the same for the demonstration clip, icons, and other bundled assets.
- If a model or clip cannot be bundled, provide an acquisition step with a clear filename and verification result. Do not call that release a one-download experience.
- Assign models and media in the final startup scene. Confirm all enabled Model Asset fields resolve after a clean checkout/import.
- Check model input dimensions, matching YOLO/depth output dimensions, relative-depth units, and one-pass MI-GAN defaults.
- Include Moebius only when the separate Python environment, pinned weights, launch/stop procedure, and connection failure recovery have been tested on the target machine. The ordinary MI-GAN release should not require Python.

## Build the intended application

- For the supplied desktop path, open the prepared `Desktop UI` scene and choose **MIRAGE > Build > Windows desktop package**. Save the scene when asked and choose an output folder outside `Assets`. The command requires the three assigned ONNX assets and MI-GAN as the selected backend. Install Windows Build Support for this editor version through Unity Hub if requested.
- On success, the package contains `MIRAGE.exe`, its Unity data/libraries, `START-HERE.txt`, and a `Docs` folder copied from these tutorials. Keep the complete folder together. The command builds Desktop UI explicitly and leaves the benchmark scene list unchanged.
- For a custom/manual build, configure the tested Unity editor's Windows target and put the intended operator scene first in that build's scene list. The repository's historical list includes benchmark scenes; do not accidentally launch benchmark automation as the operator application.
- Resolve player compilation errors, including runtime scripts that reference editor/test-only assemblies. Put editor tools in Editor folders or guard them appropriately.
- Confirm desktop builds do not require a connected VR headset or start an unintended XR provider. Treat a VR release as a separately validated configuration.
- Produce the complete application folder, including its data folder and required libraries. A portable zip must include the whole tested folder, not only the executable.
- Launch the exported application outside the Unity Editor and from a directory the operator can use without administrator access.

## Test the operator path on a clean machine

- Give a person unfamiliar with the code the release and operator guide. Have them launch, identify the input, add an outline, remove it, restore the starting settings, and quit.
- Verify video/camera selection and a missing/unavailable camera path. Check keyboard focus, mouse controls, Esc, window size, and visible text.
- Verify startup checks identify missing models or media before the operator starts a session, with one recovery action per message.
- Save a configuration, restart the application, and load it. Check that the object/effect list, colors, ranges, layers, and model options are restored as intended.
- Verify an unknown or incompatible configuration gives an understandable message instead of partially applying settings. Include a depth-unit/model mismatch test when sharing range-based presets.
- Verify every effect offered by the release, output alignment, transparent colors, relative-depth labels, pause/seek behavior, input switching, and recovery after a failed source/backend.
- Run a representative recorded-video session long enough to inspect memory growth, stale/frozen results, visible artifacts, and latency on the target GPU. Record the measured configuration and limits.

## Make support practical

- Follow [the logging guide](LOGGING.md) to verify session files, error interpretation, support export, and the separate study logs.
- Store configuration and session data in a writable user-data location, and show the operator how to open it.
- Confirm logs have a session identifier, UTC timestamp, application/model configuration, input/backend changes, errors, and bounded performance summaries.
- Exercise a known error and verify it appears in the current session log with a useful recovery message. Repeated errors should not fill the disk or overwhelm the operator.
- Test the support-export operation. Inspect the archive before release and document exactly what it contains. Include configuration and diagnostics by default; make any video, image, or participant-data attachment an explicit choice.
- Test unwritable/full storage and log-rotation/retention behavior. The application should explain a logging failure without losing the main picture.
- Confirm a support archive and saved configuration work after the release is moved to a different folder or installed under another Windows user.

## Publish the release

- Include a versioned release note, checksums, the supported hardware/setup, known limitations, and the result of the clean-machine operator test.
- Keep operator instructions separate from source-building/export instructions so users can find the path that applies to them.
- Keep the current source validation commands in [Tests/README.md](../Tests/README.md). HLSL compilation, numerical checks, and stub-based C# tests complement the real Unity/player tests; they do not replace them.
- Archive the exact source revision and configuration used to build the published package.

The next step toward a nontechnical handoff is a tested release folder with an assigned model/media pack and a known starting configuration. A setup wizard, runtime source picker, and bundled model acquisition should be presented as available features only after they are implemented and tested.
