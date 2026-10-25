# MIRAGE validation

Run each PowerShell script in a fresh PowerShell 7 process from the repository root. Python tests use only the standard library.

```powershell
pwsh -NoProfile -File Tests/Validate-Shaders.ps1
pwsh -NoProfile -File Tests/Validate-Runtime.ps1
pwsh -NoProfile -File Tests/Test-InpaintingGeometry.ps1
python Tests/test_depth_reduction.py
pwsh -NoProfile -File Tests/Validate-Logging.ps1
pwsh -NoProfile -File Tests/Validate-Presets.ps1
pwsh -NoProfile -File Tests/Validate-PresetsUi.ps1
pwsh -NoProfile -File Tests/Validate-Usability.ps1
```

`Validate-Shaders.ps1` compiles every actual compute-shader kernel under `Assets/Resources` with Windows D3DCompiler (`cs_5_0`). It expands quoted local HLSL includes and reports compiler diagnostics. It requires Windows and `d3dcompiler_47.dll`; it does not dispatch GPU kernels or validate Unity bindings.

`Validate-Runtime.ps1` compiles the actual model runners, pipeline, camera input, affected postprocessors, inpainting helpers, and backend/GPU-validation editor commands with `UNITY_EDITOR` enabled. Explicit test seams in `RuntimeStubs.cs` replace Unity rendering, inference, networking, tracking, and serialization services. The deterministic coroutine harness exercises layer budgets/cancellation, depth validity/units and borrowed output ownership, class-ID versus detection-index selection, camera negotiation/restart/seeks, backend switching, and sequential/parallel frame coherence. It verifies that a processing frame cannot overwrite the displayed frame, dependent stages use matching frame IDs, source changes discard pending results, and unchanged input frames are not repeatedly processed. These seams do **not** establish compatibility with real Unity APIs, a successful player build, native camera event ordering, or inference/visual performance.

`test_depth_reduction.py` compares the tile/owner-lane depth-reduction design against an independent per-object reference over 240 seeded randomized cases and explicit empty/thin/contaminated-mask cases. It is a numerical oracle, not an execution of HLSL. `Test-InpaintingGeometry.ps1` checks the actual crop utility against deterministic geometry properties.

For real graphics validation, open Unity with a graphics device and run **MIRAGE > Validate > Inpainting GPU smoke tests**. This executes synthetic class/depth selection, mask bounds/orientation, morphology, crop/NCHW/gamma conversion, alpha compositing, and motion/rejection cases without model weights. This menu has been compiled against test seams here; run it in Unity before relying on the GPU results. Recorded-video runs with the actual ONNX models and target GPU are still needed to assess image quality, flicker, latency, memory use, and frame rate.

## Color conversion checked against Unity Inference Engine 2.2.1

The [official 2.2.1 package](https://download.packages.unity.com/com.unity.ai.inference/-/com.unity.ai.inference-2.2.1.tgz), `Runtime/Core/Converters/TextureConverter.cs`, enables `LINEAR_TO_SRGB` when `GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat)` is true. `Runtime/Core/Resources/InferenceEngine/TextureConversion/TextureToTensor.compute` applies the corresponding RGB transfer after sampling. Therefore `TextureConverter.ToTensor` already restores model-space sRGB for sRGB-format textures; an unconditional extra gamma conversion would double-encode them. This decision follows the actual input graphics format, not only the project's color-space setting.

The custom MI-GAN crop shader and Moebius readback bridge bypass `ToTensor`, so their explicit linear-to-sRGB conversion is intentional. DA3 then applies the ImageNet RGB normalization required by the documented exporter. A real Unity check should still confirm the graphics formats and sampled values of camera, intermediate, and output textures in both Gamma and Linear projects.

## Operator and support checks

`Validate-Logging.ps1` compiles the actual plain .NET session writer against Newtonsoft.Json and exercises real files and ZIPs: concurrent producers, bounded queues, rate suppression, rotation, owned-session retention, active-session locks, path redaction, shutdown, and storage failures. It does not exercise Unity's native log callback delivery.

`Validate-Presets.ps1` exercises the actual preset store, validation, atomic overwrite, last-session files, and storage failures. `Validate-PresetsUi.ps1` compiles the actual UI/preset code against explicit managed Unity seams and checks reconstruction, packed filter controls, lifecycle callbacks, automatic restore, and rejection of incompatible depth units/models before changing visible state.

`Validate-Usability.ps1` compiles the actual desktop operator panel, startup checks, source controls, diagnostics adapter, and release-preparation menu against managed Unity seams. It also runs the pipeline regressions, including applying effects to a paused input and discarding a result whose settings changed in flight. Checks cover file-URI escaping, preserving the active source after a failed selection, decoder errors, paused-video health, setup remedies, and the native Windows file-dialog structure layout. It does not open the native dialog, render the GUI, verify the platform's video codecs, or execute a player build.

Run the new scripts in separate PowerShell processes because they compile overlapping production types. Completing these checks does not establish a tested standalone release. Use [the release checklist](../Docs/RELEASE_CHECKLIST.md) on a real prepared Unity project and on a clean target computer.
