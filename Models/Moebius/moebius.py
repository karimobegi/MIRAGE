"""Pinned native Moebius inference, evaluation and loopback Unity bridge.

White masks remove pixels. Returned overlays have straight RGB and mask alpha;
composited images preserve every pixel outside the blend mask exactly.
"""
from __future__ import annotations

import argparse
import base64
from contextlib import contextmanager
from dataclasses import dataclass
import importlib
import io
import json
from pathlib import Path
import subprocess
import sys
import time
import types

SOURCE_URL = "https://github.com/hustvl/Moebius.git"
SOURCE_REVISION = "b88d462bacb9af6e7128a3b4cc4a07418bedfd61"
MODEL_REVISION = "cd01f47fb648219d3fa605806c5ce00b713faa5e"
VAE_REVISION = "012fd343158936a265b8a0ee38a791a7a2841f45"
HERE = Path(__file__).resolve().parent
DEFAULT_CACHE = HERE / ".cache"
MAX_BODY_BYTES = 24 * 1024 * 1024
MAX_PIXELS = 4096 * 4096


def prepare(cache: Path) -> dict:
    """Acquire only the pinned student, Places2 weights and VAE (~1.1 GB)."""
    from huggingface_hub import hf_hub_download

    cache = cache.resolve()
    source = cache / "source"
    cache.mkdir(parents=True, exist_ok=True)
    if not (source / ".git").exists():
        subprocess.run(["git", "clone", "--filter=blob:none", "--no-checkout", SOURCE_URL, str(source)], check=True)
    subprocess.run(["git", "-C", str(source), "fetch", "--depth=1", "origin", SOURCE_REVISION], check=True)
    subprocess.run(["git", "-C", str(source), "checkout", "--detach", SOURCE_REVISION], check=True)
    for repo, rev, names in [
        ("hustvl/Moebius", MODEL_REVISION, ["ft_places2/diffusion_pytorch_model.bin"]),
        ("hustvl/PixelHacker", VAE_REVISION, ["vae/config.json", "vae/diffusion_pytorch_model.bin"]),
    ]:
        for name in names:
            hf_hub_download(repo_id=repo, filename=name, revision=rev, local_dir=cache / "weights")
    manifest = {"source_revision": SOURCE_REVISION, "model_revision": MODEL_REVISION,
                "vae_revision": VAE_REVISION, "variant": "ft_places2"}
    (cache / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest


def doctor() -> dict:
    import shutil
    report = {"python": sys.version, "free_disk_gb": round(shutil.disk_usage(HERE).free / 1e9, 2)}
    try:
        import torch
        report.update(torch=torch.__version__, cuda=torch.cuda.is_available())
        if torch.cuda.is_available():
            report.update(gpu=torch.cuda.get_device_name(), vram_gb=round(torch.cuda.get_device_properties(0).total_memory / 1e9, 2))
    except ImportError as error:
        report["error"] = str(error)
    return report


def _package(name: str, path: Path):
    """Import student modules without upstream __init__ importing the FLA teacher.

    No upstream source is edited; this is a normal namespace package with an
    explicit public student class for removal_model's wildcard import.
    """
    module = types.ModuleType(name)
    module.__path__ = [str(path)]
    sys.modules[name] = module
    return module


@dataclass
class Settings:
    steps: int = 20
    guidance: float = 2.0
    seed: int = 0
    resolution: int = 512
    dilation: int = 8
    feather: float = 3.0
    crop_context: float = 0.5

    def validate(self):
        if not 2 <= self.steps <= 100:
            raise ValueError("steps must be between 2 and 100")
        if self.resolution != 512:
            raise ValueError("The pinned checkpoint is validated at resolution 512 only")
        if not 0 <= self.guidance <= 20 or not 0 <= self.dilation <= 64 or not 0 <= self.feather <= 32:
            raise ValueError("invalid guidance, dilation or feather")
        if not 0 <= self.crop_context <= 4:
            raise ValueError("crop_context must be between 0 and 4")


def prepare_images(image, mask, settings: Settings):
    """Square context crop without stretching; coordinates may include padding."""
    from PIL import Image, ImageFilter
    settings.validate()
    if image.size != mask.size:
        raise ValueError("Image and mask dimensions must match")
    if image.width * image.height > MAX_PIXELS:
        raise ValueError("Image exceeds the 4096 x 4096 pixel budget")
    image, mask = image.convert("RGB"), mask.convert("L")
    binary = mask.point(lambda x: 255 if x >= 128 else 0)
    if settings.dilation:
        binary = binary.filter(ImageFilter.MaxFilter(settings.dilation * 2 + 1))
    bounds = binary.getbbox()
    if bounds is None:
        return image, binary, None, None, None
    x0, y0, x1, y1 = bounds
    side = max(x1 - x0, y1 - y0)
    side = max(64, min(max(image.size), int(round(side * (1 + 2 * settings.crop_context)))))
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    left, top = int(cx - side / 2), int(cy - side / 2)
    # Shift toward real image context before padding. A square wider than the
    # camera still includes the complete observed width instead of losing useful
    # context on the opposite edge.
    left = min(max(left, min(0, image.width - side)), max(0, image.width - side))
    top = min(max(top, min(0, image.height - side)), max(0, image.height - side))
    box = (left, top, left + side, top + side)
    # Repeat observed border RGB instead of inventing black context outside the
    # camera. The matching mask crop remains zero outside the source image.
    import numpy as np
    valid = (max(0, box[0]), max(0, box[1]), min(image.width, box[2]), min(image.height, box[3]))
    observed = np.asarray(image.crop(valid))
    padded = np.pad(observed, ((valid[1] - box[1], box[3] - valid[3]),
                               (valid[0] - box[0], box[2] - valid[2]), (0, 0)), mode="edge")
    crop = Image.fromarray(padded).resize((settings.resolution, settings.resolution), Image.Resampling.LANCZOS)
    crop_mask = binary.crop(box).resize(crop.size, Image.Resampling.NEAREST)
    return image, binary, box, crop, crop_mask


def composite(image, binary, box, generated, feather: float):
    from PIL import Image, ImageFilter
    if box is None:
        return image.copy(), Image.new("RGBA", image.size, (0, 0, 0, 0))
    side = box[2] - box[0]
    patch = generated.convert("RGB").resize((side, side), Image.Resampling.LANCZOS)
    reconstructed = image.copy()
    reconstructed.paste(patch, (box[0], box[1]))
    # Blur inward only: feathering cannot modify pixels outside the removal mask.
    alpha = binary
    if feather:
        import numpy as np
        blurred = binary.filter(ImageFilter.GaussianBlur(feather))
        alpha = Image.fromarray(np.minimum(np.asarray(binary), np.asarray(blurred)).astype("uint8"))
    result = Image.composite(reconstructed, image, alpha)
    overlay = reconstructed.convert("RGBA")
    overlay.putalpha(alpha)
    return result, overlay


class Engine:
    def __init__(self, cache: Path, device="cuda", dtype="float16"):
        initialization_start = time.perf_counter()
        import torch
        import yaml
        from diffusers import AutoencoderKL, DDIMScheduler
        self.torch = torch
        # Huge CPU thread pools make model construction/contention needlessly
        # expensive on Windows; inference itself runs on the selected GPU.
        torch.set_num_threads(min(4, torch.get_num_threads()))
        self.cache = cache.resolve()
        self.device = device
        self.dtype = dtype
        if device.startswith("cuda") and not torch.cuda.is_available():
            raise RuntimeError("CUDA unavailable; install a CUDA PyTorch build or explicitly use --device cpu --dtype float32")
        if device == "cpu" and dtype != "float32":
            raise ValueError("CPU inference requires --dtype float32")
        source = self.cache / "source"
        weight = self.cache / "weights/ft_places2/diffusion_pytorch_model.bin"
        vae_dir = self.cache / "weights/vae"
        if not weight.exists() or not (vae_dir / "config.json").exists():
            raise FileNotFoundError("Run 'python moebius.py prepare' before inference")
        revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
        if revision != SOURCE_REVISION:
            raise RuntimeError(f"Upstream source revision {revision} differs from pinned {SOURCE_REVISION}")
        sys.path.insert(0, str(source))
        model_lib = _package("model_lib", source / "model_lib")
        student = importlib.import_module("model_lib.nets.unet_lambda_prune_lite")
        name = "UNet2DLambdaDWConvMixFFNConditionModel_prune_down_mid_up_block_8x8"
        setattr(model_lib, name, getattr(student, name))
        model_lib.__all__ = [name]
        _package("removal", source / "removal")
        _package("removal.v1_2", source / "removal/v1_2")
        removal = importlib.import_module("removal.v1_2.removal_model")
        pipeline = importlib.import_module("removal.v1_2.pipeline")
        config = yaml.safe_load((source / "config/model_cfg/moebius.yaml").read_text(encoding="utf-8"))
        t0 = time.perf_counter()
        model = removal.build_removal_model(config, 20)
        model.load_state_dict(torch.load(weight, map_location="cpu", weights_only=True), strict=True)
        # DDIM in recent diffusers can promote the latent to float32 after the
        # first step. The upstream UNet derives its timestep-embedding dtype
        # from that input, so enforce the model dtype at this explicit boundary.
        native_forward = model.forward
        def forward_native_dtype(noisy_latents, *args, **kwargs):
            return native_forward(noisy_latents.to(dtype=next(model.parameters()).dtype), *args, **kwargs)
        model.forward = forward_native_dtype
        vae = AutoencoderKL.from_config(json.loads((vae_dir / "config.json").read_text(encoding="utf-8")))
        vae.load_state_dict(torch.load(vae_dir / "diffusion_pytorch_model.bin", map_location="cpu", weights_only=True), strict=True)
        vae.enable_slicing()
        vae.enable_tiling()
        scheduler = DDIMScheduler(beta_start=0.00085, beta_end=0.012, beta_schedule="scaled_linear", num_train_timesteps=1000, clip_sample=False)
        self.pipe = pipeline.RemovalSDXLPipeline_BatchMode(model, vae, scheduler, device=device, dtype=getattr(torch, dtype))
        self.sync()
        self.model_build_ms = (time.perf_counter() - t0) * 1000
        self.load_ms = (time.perf_counter() - initialization_start) * 1000

    def sync(self):
        if self.device.startswith("cuda"):
            self.torch.cuda.synchronize()

    @contextmanager
    def stage_timings(self, timings):
        originals = []
        for obj, name, label in [(self.pipe.vae, "encode", "vae_encode_ms"),
                                 (self.pipe.vae, "decode", "vae_decode_ms"),
                                 (self.pipe.removal_model, "forward", "denoiser_ms")]:
            original = getattr(obj, name)
            def timed(*args, _original=original, _label=label, **kwargs):
                self.sync()
                start = time.perf_counter()
                value = _original(*args, **kwargs)
                if _label == "vae_decode_ms" and not self.torch.isfinite(value.sample).all():
                    raise RuntimeError("Non-finite VAE output; retry with --dtype float32")
                self.sync()
                timings[_label] = timings.get(_label, 0.0) + (time.perf_counter() - start) * 1000
                count_label = _label.removesuffix("_ms") + "_calls"
                timings[count_label] = timings.get(count_label, 0) + 1
                return value
            originals.append((obj, name, original))
            setattr(obj, name, timed)
        try:
            yield
        finally:
            for obj, name, original in originals:
                setattr(obj, name, original)

    def infer(self, image, mask, settings: Settings):
        start = time.perf_counter()
        if self.device.startswith("cuda"):
            self.torch.cuda.reset_peak_memory_stats()
        image, binary, box, crop, crop_mask = prepare_images(image, mask, settings)
        timings = {"preprocess_ms": (time.perf_counter() - start) * 1000}
        if box is None:
            result, overlay = composite(image, binary, None, None, settings.feather)
            timings.update(skipped_empty_mask=True, total_ms=(time.perf_counter() - start) * 1000)
            return result, overlay, timings
        self.sync()
        infer_start = time.perf_counter()
        with self.torch.inference_mode(), self.stage_timings(timings):
            generated = self.pipe([crop], [crop_mask], image_size=settings.resolution,
                                  num_steps=settings.steps, guidance_scale=settings.guidance,
                                  retry=settings.seed, paste=False, compensate=False, noise_offset=0.0357)[0]
        self.sync()
        timings["native_pipeline_ms"] = (time.perf_counter() - infer_start) * 1000
        post_start = time.perf_counter()
        result, overlay = composite(image, binary, box, generated, settings.feather)
        timings.update(postprocess_ms=(time.perf_counter() - post_start) * 1000,
                       total_ms=(time.perf_counter() - start) * 1000, steps=settings.steps,
                       resolution=settings.resolution, crop=list(box), device=self.device,
                       dtype=self.dtype, source_revision=SOURCE_REVISION, model_revision=MODEL_REVISION)
        if self.device.startswith("cuda"):
            timings["peak_vram_mb"] = self.torch.cuda.max_memory_allocated() / (1024 * 1024)
        return result, overlay, timings


def png_bytes(image):
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def make_server(engine, port: int, settings: Settings):
    from http.server import BaseHTTPRequestHandler, HTTPServer
    from PIL import Image

    class Handler(BaseHTTPRequestHandler):
        def reply(self, status, payload):
            body = json.dumps(payload, allow_nan=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path != "/health":
                return self.reply(404, {"error": "Unknown endpoint"})
            self.reply(200, {"ready": True, "backend": "moebius", "source_revision": SOURCE_REVISION,
                             "load_ms": engine.load_ms, "steps": settings.steps})

        def do_POST(self):
            if self.path != "/inpaint":
                return self.reply(404, {"error": "Unknown endpoint"})
            if self.headers.get("Content-Type", "").split(";")[0].strip() != "application/json":
                return self.reply(415, {"error": "Use application/json"})
            if self.headers.get("Origin"):
                return self.reply(403, {"error": "Browser-origin requests are not accepted"})
            try:
                size = int(self.headers.get("Content-Length", "0"))
                if not 0 < size <= MAX_BODY_BYTES:
                    return self.reply(413, {"error": "Request exceeds 24 MB budget"})
                start = time.perf_counter()
                payload = json.loads(self.rfile.read(size))
                image = Image.open(io.BytesIO(base64.b64decode(payload["image"], validate=True)))
                mask = Image.open(io.BytesIO(base64.b64decode(payload["mask"], validate=True)))
                # Single-threaded server bounds inference to one request; no GPU queue.
                result, overlay, timings = engine.infer(image, mask, settings)
                encoded = base64.b64encode(png_bytes(overlay)).decode("ascii")
                timings["server_total_ms"] = (time.perf_counter() - start) * 1000
                self.reply(200, {"frame_id": int(payload.get("frame_id", -1)), "overlay": encoded, "timings": timings})
            except (ValueError, KeyError, OSError, TypeError) as error:
                self.reply(400, {"error": str(error)})
            except Exception as error:
                self.reply(500, {"error": f"{type(error).__name__}: {error}"})

    return HTTPServer(("127.0.0.1", port), Handler)


def serve(engine, port: int, settings: Settings):
    print(f"Moebius ready at http://127.0.0.1:{port}; steps={settings.steps}; loopback only", flush=True)
    make_server(engine, port, settings).serve_forever()


def verify_bridge(engine, image_path: Path, mask_path: Path, output: Path, settings: Settings):
    """Two real HTTP/model round trips, recording cold and warm whole-image time."""
    import threading
    import urllib.request
    from PIL import Image
    import numpy as np
    with Image.open(image_path) as source:
        image = source.convert("RGB")
    with Image.open(mask_path) as source:
        mask = source.convert("L")
    server = make_server(engine, 0, settings)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    reports = []
    try:
        for frame_id, label in [(1729, "cold"), (1730, "warm")]:
            data = json.dumps({"frame_id": frame_id, "image": base64.b64encode(png_bytes(image)).decode("ascii"),
                               "mask": base64.b64encode(png_bytes(mask)).decode("ascii")}).encode("utf-8")
            request = urllib.request.Request(f"http://127.0.0.1:{server.server_port}/inpaint", data=data,
                                             headers={"Content-Type": "application/json"})
            start = time.perf_counter()
            with urllib.request.urlopen(request, timeout=600) as response:
                payload = json.load(response)
            elapsed = (time.perf_counter() - start) * 1000
            if payload.get("frame_id") != frame_id:
                raise AssertionError("Bridge returned the wrong frame ID")
            overlay = Image.open(io.BytesIO(base64.b64decode(payload["overlay"]))).convert("RGBA")
            if overlay.size != image.size:
                raise AssertionError("Bridge changed image dimensions")
            result = Image.alpha_composite(image.convert("RGBA"), overlay).convert("RGB")
            outside = np.asarray(overlay)[..., 3] == 0
            if not np.array_equal(np.asarray(image)[outside], np.asarray(result)[outside]):
                raise AssertionError("Compositing modified unmasked pixels")
            destination = output.with_name(output.stem + "." + label + ".png")
            destination.parent.mkdir(parents=True, exist_ok=True)
            result.save(destination)
            overlay.save(destination.with_name(destination.stem + ".overlay.png"))
            report = dict(payload["timings"], http_round_trip_ms=elapsed, load_ms=engine.load_ms,
                          cold_first_inference=label == "cold", frame_id=frame_id, output=str(destination),
                          image=str(image_path.resolve()), mask=str(mask_path.resolve()),
                          validation="actual native model over loopback HTTP; matching frame, dimensions, unmasked pixels")
            reports.append(report)
            print(json.dumps(report), flush=True)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(5)
    output.with_suffix(".verification.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["doctor", "prepare", "infer", "batch", "serve", "verify"])
    parser.add_argument("--cache", type=Path, default=DEFAULT_CACHE)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--dtype", choices=["float16", "float32", "bfloat16"], default="float16")
    parser.add_argument("--steps", type=int, default=20)
    parser.add_argument("--guidance", type=float, default=2.0)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--resolution", type=int, choices=[512], default=512)
    parser.add_argument("--dilation", type=int, default=8)
    parser.add_argument("--feather", type=float, default=3.0)
    parser.add_argument("--crop-context", type=float, default=0.5)
    parser.add_argument("--image", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--output", type=Path, default=HERE / "outputs/result.png")
    parser.add_argument("--manifest", type=Path, help="JSON list of image/mask pairs; paths relative to manifest")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if args.command == "doctor":
        print(json.dumps(doctor(), indent=2)); return
    if args.command == "prepare":
        print(json.dumps(prepare(args.cache), indent=2)); return
    settings = Settings(args.steps, args.guidance, args.seed, args.resolution, args.dilation, args.feather, args.crop_context)
    settings.validate()
    engine = Engine(args.cache, args.device, args.dtype)
    if args.command == "serve":
        serve(engine, args.port, settings); return
    if args.command == "verify":
        if not args.image or not args.mask:
            parser.error("verify requires --image and --mask")
        verify_bridge(engine, args.image, args.mask, args.output, settings); return
    from PIL import Image
    if args.command == "infer":
        if not args.image or not args.mask:
            parser.error("infer requires --image and --mask")
        pairs = [{"image": str(args.image.resolve()), "mask": str(args.mask.resolve()), "output": str(args.output.resolve())}]
        root = Path.cwd()
    else:
        if not args.manifest:
            parser.error("batch requires --manifest")
        pairs = json.loads(args.manifest.read_text(encoding="utf-8"))
        root = args.manifest.resolve().parent
    reports = []
    for index, pair in enumerate(pairs):
        image_path, mask_path = root / pair["image"], root / pair["mask"]
        output = root / pair.get("output", f"outputs/{image_path.stem}_{index}.png")
        start = time.perf_counter()
        with Image.open(image_path) as image, Image.open(mask_path) as mask:
            result, overlay, timings = engine.infer(image, mask, settings)
        output.parent.mkdir(parents=True, exist_ok=True)
        result.save(output)
        overlay.save(output.with_name(output.stem + ".overlay.png"))
        timings.update(image=str(image_path), mask=str(mask_path), output=str(output), load_ms=engine.load_ms,
                       io_inclusive_ms=(time.perf_counter() - start) * 1000, cold_first_inference=index == 0)
        output.with_suffix(".json").write_text(json.dumps(timings, indent=2), encoding="utf-8")
        reports.append(timings)
        print(json.dumps(timings), flush=True)
    if args.command == "batch":
        args.manifest.with_suffix(".moebius-results.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
