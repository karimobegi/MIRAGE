# Moebius quality backend

This integration runs the official Moebius Places2 checkpoint locally in Python and connects it to MIRAGE through a loopback HTTP sidecar. MI-GAN remains the low-latency backend. Moebius is a single-image quality option; it does not provide temporal consistency or a demonstrated real-time driving pipeline.

The source is pinned to `b88d462bacb9af6e7128a3b4cc4a07418bedfd61`, Moebius weights to `cd01f47fb648219d3fa605806c5ce00b713faa5e`, and the PixelHacker VAE to `012fd343158936a265b8a0ee38a791a7a2841f45`. The setup downloads approximately 1.1 GB of weights, plus the source and Python dependencies. Generated files, models and the environment are ignored by Git.

## Install and run

From the repository root, with a working CUDA PyTorch installation already available:

```powershell
.\Models\Moebius\setup.ps1 -ReuseInstalledTorch
.\Models\Moebius\.venv\Scripts\python.exe .\Models\Moebius\moebius.py doctor
.\Models\Moebius\.venv\Scripts\python.exe .\Models\Moebius\moebius.py serve --steps 20
```

`-ReuseInstalledTorch` gives the dedicated environment access to the existing installation; new packages install only in this environment. For a fully separate environment, create `Models/Moebius/.venv`, install a CUDA build of PyTorch and torchvision using the [official installation instructions](https://pytorch.org/get-started/locally/), then run `setup.ps1` without that switch. CPU inference is available explicitly with `--device cpu --dtype float32` and will be slow.

The loader imports only the upstream student inference modules. It avoids the optional teacher/training imports that otherwise require FLA/Triton on Windows. It does not edit the downloaded upstream source. Checkpoint loading uses PyTorch `weights_only=True`. A dtype adapter maintains the model precision when the DDIM scheduler promotes intermediate latents after the first step.

The service is ready when it prints `Moebius ready at http://127.0.0.1:8765`. `GET /health` reports readiness. It binds only to loopback, accepts one request at a time and rejects browser-origin requests. Stop the terminal process when finished.

## Select in Unity

1. Open a Pipeline scene and select its `Pipeline` GameObject.
2. Choose **MIRAGE > Inpainting > Use Moebius on selected Pipeline**.
3. The editor copies dimensions, class/range settings and output UI from the existing MI-GAN runner, assigns the new runner on Pipeline and disables the other inpainting runners. The change supports Undo.
4. Start the Python service, then enter Play mode. The existing Remove/Opacity controls target the selected backend.
5. Select the Pipeline and choose **Use MI-GAN on selected Pipeline** to restore MI-GAN.

The menu is available in Edit mode. It expects one existing MI-GAN runner in the scene. The Moebius runner's `ServerUrl` defaults to `http://127.0.0.1:8765`; remote endpoints are rejected. The service keeps the model loaded, and the runner submits at most one frame at a time. MIRAGE waits for a coherent frame result, so this quality mode can update much more slowly than the live MI-GAN pipeline.

The GPU selection mask and camera capture share Unity's bottom-left texture convention. Both are serialized with the same PNG path, and Pillow returns the overlay in the corresponding image coordinates. Linear camera RGB is encoded to sRGB before upload; RFloat mask red is explicitly serialized as grayscale. Returned RGB is tagged sRGB for Unity sampling, while alpha stays linear. GPU masks and input snapshots are captured before the network wait; frame IDs and a history generation prevent old responses from being published after settings/input changes. A full Unity Play-mode orientation/color check is still required on each graphics backend.

## Images, clips and timings

White mask pixels identify the area to remove, black pixels identify known content. Image and mask dimensions must match. The adapter takes an aspect-preserving square crop around the union of selected pixels, adds context, dilates the mask and feathers inward when compositing at original resolution. At image edges it repeats observed RGB border values while keeping the outside mask black. Context size is capped to the longer camera dimension. Pixels outside the blend mask remain exactly equal to the input. The pinned checkpoint is validated at **512 x 512** only.

```powershell
.\Models\Moebius\.venv\Scripts\python.exe .\Models\Moebius\moebius.py infer `
  --image C:\data\frame.png --mask C:\data\mask.png `
  --output C:\data\result.png --steps 20 --dilation 8 --feather 3
```

Each run saves the composited image, a straight-alpha `*.overlay.png`, and a timing/provenance JSON. `--crop-context` defaults to `0.5`; `--guidance` defaults to `2.0`; `--seed` defaults to `0`. Fewer requested steps are experimental quality/speed settings, not equivalent results. Upstream uses strength `0.99`, so report `denoiser_calls` alongside the requested step count.

For an extracted clip, make a JSON manifest that matches each frame to its mask explicitly:

```json
[
  {"image": "frames/0001.png", "mask": "masks/0001.png", "output": "outputs/0001.png"},
  {"image": "frames/0002.png", "mask": "masks/0002.png", "output": "outputs/0002.png"}
]
```

```powershell
.\Models\Moebius\.venv\Scripts\python.exe .\Models\Moebius\moebius.py batch --manifest C:\data\clip.json
```

Paths are relative to the manifest. The model loads once and a `*.moebius-results.json` collects all frame timings. Use the same frames, masks and GPU when comparing against MI-GAN. Evaluate masked-region quality, temporal flicker, complete frame latency and peak VRAM; image quality alone is insufficient for selecting a live backend.

`total_ms` includes crop preparation, both VAE encodes, the complete denoising loop, VAE decoding and original-resolution compositing. `io_inclusive_ms` additionally includes image reads and output writes. `load_ms` is reported separately. Stage timing synchronizes CUDA; it is not an enqueue-only number. The first inference is labeled cold. These native timings do not include Unity capture/upload/display; the Unity runner reports full request round-trip time.

## Verification

```powershell
.\Models\Moebius\.venv\Scripts\python.exe -m unittest discover -s .\Models\Moebius -p "test_*.py" -v
.\Models\Moebius\.venv\Scripts\python.exe .\Models\Moebius\moebius.py verify `
  --image C:\data\frame.png --mask C:\data\mask.png --output C:\data\verification.png
```

The contract tests exercise polarity, portrait/edge crops, aspect ratio, exact unmasked-pixel preservation, HTTP validation, dimensions and asymmetric mask orientation without loading a model. `verify` performs two **real model** HTTP round trips (cold/warm), checks matching frame IDs, sizes and compositing, and saves the outputs plus a `*.verification.json` report. This verifies the Python bridge; it does not substitute for Unity Play-mode validation.

### Local trial, 25 October 2026

A real street-image test used [Ultralytics' bus photograph](https://raw.githubusercontent.com/ultralytics/assets/main/im/bus.jpg), 810 x 1080, with a hand-traced polygon around the left foreground pedestrian. The source URL, SHA-256 hashes and polygon are recorded in `outputs/bus.provenance.json`; generated files are local and ignored by Git.

On the RTX 4050 Laptop GPU (6 GB), Python 3.13.12, PyTorch 2.13.0+cu132 and diffusers 0.38.0, float16 / 512 / 20 requested steps (19 actual denoiser calls) measured:

| Measurement | Cold first request | Warm request |
|---|---:|---:|
| Complete loopback HTTP round trip | 18.08 s | 12.72 s |
| Native pipeline, including VAE | 17.05 s | 11.14 s |
| Whole-image processing and composition | 17.56 s | 12.19 s |
| Peak PyTorch allocated VRAM | 1.23 GB | 1.23 GB |

Startup, including imports and model loading, took 64.60 s. These are two observations on one image with other desktop work running, not a performance distribution or a MI-GAN comparison. `outputs/street-20.verification.json` contains detailed timings and `outputs/street-20.warm.png` contains the result. Frame IDs, dimensions and exact unmasked pixels passed the bridge check.

Visual inspection showed the pedestrian removed but a hallucinated tall pole and distorted bus structure in the filled region. This establishes that the native integration works; it does **not** establish better removal quality. Keep Moebius opt-in and compare additional masks and scenes before selecting it for an experiment. It is not a real-time option on this tested GPU.

## Upstream and deployment

- [Official code, weights and Apache 2.0 license statement](https://github.com/hustvl/Moebius)
- [Paper](https://arxiv.org/abs/2606.19195): the reported 26.01 ms is **per diffusion step**; its 20-step configuration totals 0.52 seconds under the authors' setup.

This is a native Python backend, not an ONNX model. An ONNX/Unity Inference Engine implementation would require separately exporting and validating the VAE, denoiser, scheduler and lambda layers; no compatible graph is claimed or generated here.
