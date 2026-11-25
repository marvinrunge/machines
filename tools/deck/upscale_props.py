"""
Upscales the textures of the story props that are still Meshy's (by their sidecar's source) to twice their size.

    uv run --with pillow --with numpy python -I tools/deck/upscale_props.py extract <folder> [prop ...]
    realesrgan-ncnn-vulkan.exe -i <folder>/in -o <folder>/out -n realesrgan-x4plus -f png
    uv run --with pillow --with numpy python -I tools/deck/upscale_props.py pack <folder> [prop ...]

extract writes each prop's base colour as <folder>/in/<prop>_<image>.png; Real-ESRGAN (the remake's tools/texture-upscale/bin;
set VK_LOADER_LAYERS_DISABLE=~implicit~ or it misses the GPU) makes them four times larger; pack brings them down to twice the
old size (Lanczos, which keeps the sharpness and drops most of what the network invented) and puts them into the GLB. The
normal and metal-roughness maps are only resized (Lanczos; the network smooths the normals' fine relief away), the normals
set back to unit length. A prop whose textures are already larger than at ORIGINALS is left as it is, so running it twice
does not upscale twice. tone_props.py rewrites the props from the originals at their old size: run this again after it.
"""
import io
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tone_props import FOLDER, ORIGINALS, ROOT, chunks, meshy, packed  # noqa: E402

FACTOR = 2


def roles(doc: dict) -> dict[int, str]:
    found = {}
    for m in doc.get("materials", []):
        pbr = m.get("pbrMetallicRoughness", {})
        for key, role in (("baseColorTexture", "base"), ("metallicRoughnessTexture", "mr")):
            if key in pbr:
                found[doc["textures"][pbr[key]["index"]]["source"]] = role
        for key, role in (("normalTexture", "normal"), ("emissiveTexture", "base"), ("occlusionTexture", "mr")):
            if key in m:
                found.setdefault(doc["textures"][m[key]["index"]]["source"], role)
    return found


def views_of(doc: dict, binary: bytes) -> list[bytes]:
    assert all(v.get("buffer", 0) == 0 for v in doc["bufferViews"]) and len(doc["buffers"]) == 1
    return [binary[v.get("byteOffset", 0): v.get("byteOffset", 0) + v["byteLength"]] for v in doc["bufferViews"]]


def picture(doc: dict, views: list[bytes], index: int) -> Image.Image:
    return Image.open(io.BytesIO(views[doc["images"][index]["bufferView"]]))


def props(only: set[str]) -> list[Path]:
    found = []
    for glb in sorted((ROOT / FOLDER).glob("*.glb")):
        rel = f"{FOLDER}/{glb.name}"
        if (only and glb.stem not in only) or not meshy(rel):
            continue
        original = subprocess.run(["git", "show", f"{ORIGINALS}:{rel}"], cwd=ROOT, capture_output=True).stdout
        if original:
            doc, binary = chunks(original)
            old = picture(doc, views_of(doc, binary), 0).size[0]
            doc, binary = chunks(glb.read_bytes())
            if picture(doc, views_of(doc, binary), 0).size[0] > old:
                print(f"{glb.stem}: already upscaled")
                continue
        found.append(glb)
    return found


def extract(folder: Path, only: set[str]) -> None:
    (folder / "in").mkdir(parents=True, exist_ok=True)
    for glb in props(only):
        doc, binary = chunks(glb.read_bytes())
        views = views_of(doc, binary)
        for index, role in roles(doc).items():
            if role == "base":
                picture(doc, views, index).convert("RGB").save(folder / "in" / f"{glb.stem}_{index}.png")
        print(f"{glb.stem}: extracted")


def unit_normals(rgb: np.ndarray) -> np.ndarray:
    n = rgb.astype(np.float32) / 127.5 - 1.0
    n[..., 2] = np.maximum(n[..., 2], 0.05)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return np.clip((n + 1.0) * 127.5 + 0.5, 0, 255).astype(np.uint8)


def pack(folder: Path, only: set[str]) -> None:
    for glb in props(only):
        doc, binary = chunks(glb.read_bytes())
        views = views_of(doc, binary)
        for index, role in roles(doc).items():
            image = doc["images"][index]
            old = picture(doc, views, index)
            size = (old.size[0] * FACTOR, old.size[1] * FACTOR)
            if role != "base":
                out = old.convert("RGB").resize(size, Image.LANCZOS)
                if role == "normal":
                    out = Image.fromarray(unit_normals(np.asarray(out)))
            else:
                out = Image.open(folder / "out" / f"{glb.stem}_{index}.png").convert("RGB").resize(size, Image.LANCZOS)
            buffer = io.BytesIO()
            if image["mimeType"] == "image/png":
                out.save(buffer, "PNG", optimize=True)
            else:
                out.save(buffer, "JPEG", quality=92, subsampling=0)
            views[image["bufferView"]] = buffer.getvalue()
        glb.write_bytes(packed(doc, views))
        print(f"{glb.stem}: {glb.stat().st_size / 1e6:.1f} MB")


if __name__ == "__main__":
    {"extract": extract, "pack": pack}[sys.argv[1]](Path(sys.argv[2]), set(sys.argv[3:]))
