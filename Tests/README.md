# MIRAGE validation

Run each PowerShell script in a fresh PowerShell 7 process from the repository root. Python tests use only the standard library.

```powershell
pwsh -NoProfile -File Tests/Validate-Shaders.ps1
pwsh -NoProfile -File Tests/Validate-Runtime.ps1
pwsh -NoProfile -File Tests/Test-InpaintingGeometry.ps1
python Tests/test_depth_reduction.py
```

`Validate-Shaders.ps1` compiles every actual compute-shader kernel under `Assets/Resources` with Windows D3DCompiler (`cs_5_0`). It expands quoted local HLSL includes and reports compiler diagnostics. It requires Windows and `d3dcompiler_47.dll`; it does not dispatch GPU kernels or validate Unity bindings.

`Validate-Runtime.ps1` compiles the actual model runners, pipeline, camera input, affected postprocessors, inpainting helpers, and backend/GPU-validation editor commands with `UNITY_EDITOR` enabled. Explicit test seams in `RuntimeStubs.cs` replace Unity rendering, inference, networking, tracking, and serialization services. The deterministic coroutine harness exercises layer budgets/cancellation, depth validity/units and borrowed output ownership, class-ID versus detection-index selection, camera negotiation/restart/seeks, backend switching, and sequential/parallel frame coherence. It verifies that a processing frame cannot overwrite the displayed frame, dependent stages use matching frame IDs, source changes discard pending results, and unchanged input frames are not repeatedly processed. These seams do **not** establish compatibility with real Unity APIs, a successful player build, native camera event ordering, or inference/visual performance.

`test_depth_reduction.py` compares the tile/owner-lane depth-reduction design against an independent per-object reference over 240 seeded randomized cases and explicit empty/thin/contaminated-mask cases. It is a numerical oracle, not an execution of HLSL. `Test-InpaintingGeometry.ps1` checks the actual crop utility against deterministic geometry properties.

For real graphics validation, open Unity with a graphics device and run **MIRAGE > Validate > Inpainting GPU smoke tests**. This executes synthetic class/depth selection, mask bounds/orientation, morphology, crop/NCHW/gamma conversion, alpha compositing, and motion/rejection cases without model weights. This menu has been compiled against test seams here; run it in Unity before relying on the GPU results. Recorded-video runs with the actual ONNX models and target GPU are still needed to assess image quality, flicker, latency, memory use, and frame rate.

## Color conversion checked against Unity Inference Engine 2.2.1

The [official 2.2.1 package](https://download.packages.unity.com/com.unity.ai.inference/-/com.unity.ai.inference-2.2.1.tgz), `Runtime/Core/Converters/TextureConverter.cs`, enables `LINEAR_TO_SRGB` when `GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat)` is true. `Runtime/Core/Resources/InferenceEngine/TextureConversion/TextureToTensor.compute` applies the corresponding RGB transfer after sampling. Therefore `TextureConverter.ToTensor` already restores model-space sRGB for sRGB-format textures; an unconditional extra gamma conversion would double-encode them. This decision follows the actual input graphics format, not only the project's color-space setting.

The custom MI-GAN crop shader and Moebius readback bridge bypass `ToTensor`, so their explicit linear-to-sRGB conversion is intentional. DA3 then applies the ImageNet RGB normalization required by the documented exporter. A real Unity check should still confirm the graphics formats and sampled values of camera, intermediate, and output textures in both Gamma and Linear projects.
