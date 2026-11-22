#!/usr/bin/env python3
"""
Build the starter `neonwave-disney` Boot Theme `.zip` conforming 100% to the
v1.1 `market.ioniqthemes.com` Boot Themes specification:
  - boottheme.json
  - preview_onboot.jpg          (960x360, <= 2 MB)
  - preview_poweroff.gif        (960x360, <= 10 MB)
  - preview_poweroff_poster.jpg (960x360, <= 2 MB)
  - onboot/splash_1920x720.jpg  (1920x720, Baseline DCT SOF0 YCbCr 4:4:4, <= 450 KB)
  - onboot/splash_1280x720.jpg  (1280x720, Baseline DCT SOF0 YCbCr 4:4:4, <= 450 KB)
  - poweroff/goodbye_1920x720.mp4 (1920x720 H.264 avc1 silent .mp4)
"""

import io
import json
import os
import shutil
import subprocess
import zipfile
from pathlib import Path
from PIL import Image
import imageio_ffmpeg

ROOT = Path(__file__).resolve().parent.parent
BUILD = ROOT / "build"
OUT_ZIP = BUILD / "neonwave-disney_v1.0.0_r1.zip"

SRC_BOOT_IMG = BUILD / "neonwave_proc_frame0_1920x720.jpg"
SRC_DISNEY_MP4 = ROOT / "extracted-samples" / "res" / "raw" / "welcome_disney_100_1920.mp4"


def encode_jpu_444(img: Image.Image, w: int, h: int, max_bytes: int = 440 * 1024) -> bytes:
    rgb = img.convert("RGB")
    sw, sh = rgb.size
    target_asp = w / h
    src_asp = sw / sh
    if src_asp > target_asp:
        nw = int(round(sh * target_asp))
        left = (sw - nw) // 2
        rgb = rgb.crop((left, 0, left + nw, sh))
    elif src_asp < target_asp:
        nh = int(round(sw / target_asp))
        top = (sh - nh) // 2
        rgb = rgb.crop((0, top, sw, top + nh))
    resized = rgb.resize((w, h), Image.Resampling.LANCZOS)
    for q in (92, 88, 84, 80, 75):
        buf = io.BytesIO()
        resized.save(buf, format="JPEG", quality=q, subsampling=0, progressive=False, optimize=False)
        data = buf.getvalue()
        if len(data) <= max_bytes:
            return data
    raise RuntimeError("Could not fit JPEG within JPU budget")


def main() -> int:
    BUILD.mkdir(parents=True, exist_ok=True)
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()

    img = Image.open(SRC_BOOT_IMG).convert("RGB")
    splash_1920 = encode_jpu_444(img, 1920, 720)
    splash_1280 = encode_jpu_444(img, 1280, 720)

    prev_onboot_buf = io.BytesIO()
    img.resize((960, 360), Image.Resampling.LANCZOS).save(
        prev_onboot_buf, format="JPEG", quality=88, subsampling=0, progressive=False
    )
    preview_onboot = prev_onboot_buf.getvalue()

    # Extract peak frame (at 2.8s) from Disney 100th MP4 for preview_poweroff_poster.jpg (960x360)
    poster_path = BUILD / "_tmp_disney_poster.jpg"
    gif_path = BUILD / "_tmp_disney_preview.gif"
    subprocess.run(
        [
            ffmpeg, "-y", "-ss", "2.8", "-i", str(SRC_DISNEY_MP4),
            "-frames:v", "1", "-vf", "scale=960:360:flags=lanczos",
            "-q:v", "3", str(poster_path),
        ],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    # Re-encode via Pillow to guarantee clean JFIF <= 2 MB without extra metadata
    poster_img = Image.open(poster_path).convert("RGB").resize((960, 360), Image.Resampling.LANCZOS)
    poster_buf = io.BytesIO()
    poster_img.save(poster_buf, format="JPEG", quality=88, progressive=False)
    preview_poster = poster_buf.getvalue()

    # Generate crisp 960x360 animated GIF preview <= 10 MB using palettegen+paletteuse at 12 fps
    subprocess.run(
        [
            ffmpeg, "-y", "-i", str(SRC_DISNEY_MP4),
            "-vf", "fps=12,scale=960:360:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5",
            "-loop", "0", str(gif_path),
        ],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    preview_gif = gif_path.read_bytes()
    poster_path.unlink(missing_ok=True)
    gif_path.unlink(missing_ok=True)

    manifest = {
        "formatVersion": 1,
        "id": "neonwave-disney",
        "name": "Neon Wave + Disney 100th",
        "author": "Tapple",
        "version": "1.0.0",
        "revision": 1,
        "category": "OEM+",
        "description": "Procedural Neon Wave 1920x720 Boot Image paired with the Disney 100th Platinum Welcome & Goodbye video.",
        "hasOnBoot": True,
        "hasPowerOff": True,
        "hasWelcomeVideo": False,
        "powerOffDurationMs": 5833,
        "resolution": "1920x720",
        "minAppVersion": "1.0.0",
        "compatibleVersions": ["1.0.0+"],
        "tags": ["neon", "wave", "disney", "ioniq"],
    }
    manifest_bytes = json.dumps(manifest, indent=2).encode("utf-8")

    fixed_dt = (2026, 1, 1, 0, 0, 0)
    def add_entry(zf: zipfile.ZipFile, name: str, data: bytes, compress=zipfile.ZIP_DEFLATED):
        zi = zipfile.ZipInfo(name, date_time=fixed_dt)
        zi.compress_type = compress
        zi.create_system = 3
        zi.external_attr = 0o100644 << 16
        zf.writestr(zi, data)

    with zipfile.ZipFile(OUT_ZIP, "w") as zf:
        add_entry(zf, "boottheme.json", manifest_bytes)
        add_entry(zf, "preview_onboot.jpg", preview_onboot, zipfile.ZIP_STORED)
        add_entry(zf, "preview_poweroff.gif", preview_gif, zipfile.ZIP_STORED)
        add_entry(zf, "preview_poweroff_poster.jpg", preview_poster, zipfile.ZIP_STORED)
        add_entry(zf, "onboot/splash_1920x720.jpg", splash_1920, zipfile.ZIP_STORED)
        add_entry(zf, "onboot/splash_1280x720.jpg", splash_1280, zipfile.ZIP_STORED)
        add_entry(zf, "poweroff/goodbye_1920x720.mp4", SRC_DISNEY_MP4.read_bytes(), zipfile.ZIP_STORED)

    print(f"[OK] Built starter theme {OUT_ZIP} ({OUT_ZIP.stat().st_size:,} bytes)")
    print(f"     preview_onboot.jpg={len(preview_onboot):,} B | preview_poweroff.gif={len(preview_gif):,} B | poster={len(preview_poster):,} B")

    # Also build standalone Boot-Only and Goodbye-Only starter themes for mix-and-match testing!
    boot_only_zip = BUILD / "neonwave-boot-only_v1.0.0_r1.zip"
    boot_only_manifest = {
        "formatVersion": 1,
        "id": "neonwave-boot-only",
        "name": "Neon Wave (Boot Image Only)",
        "author": "Tapple",
        "version": "1.0.0",
        "revision": 1,
        "category": "Fantasy / Art",
        "description": "Procedural Neon Wave 1920x720 static Boot Image.",
        "hasOnBoot": True,
        "hasPowerOff": False,
        "hasWelcomeVideo": False,
        "resolution": "1920x720",
        "minAppVersion": "1.0.0",
        "compatibleVersions": ["1.0.0+"],
        "tags": ["neon", "wave", "boot"],
    }
    with zipfile.ZipFile(boot_only_zip, "w") as zf:
        add_entry(zf, "boottheme.json", json.dumps(boot_only_manifest, indent=2).encode("utf-8"))
        add_entry(zf, "preview_onboot.jpg", preview_onboot, zipfile.ZIP_STORED)
        add_entry(zf, "onboot/splash_1920x720.jpg", splash_1920, zipfile.ZIP_STORED)
        add_entry(zf, "onboot/splash_1280x720.jpg", splash_1280, zipfile.ZIP_STORED)

    goodbye_only_zip = BUILD / "disney100-goodbye-only_v1.0.0_r1.zip"
    goodbye_only_manifest = {
        "formatVersion": 1,
        "id": "disney100-goodbye-only",
        "name": "Disney 100th (Goodbye Video Only)",
        "author": "Tapple",
        "version": "1.0.0",
        "revision": 1,
        "category": "TV / Movie / Music",
        "description": "Disney 100th Platinum edition Welcome & Goodbye video.",
        "hasOnBoot": False,
        "hasPowerOff": True,
        "hasWelcomeVideo": False,
        "powerOffDurationMs": 5833,
        "resolution": "1920x720",
        "minAppVersion": "1.0.0",
        "compatibleVersions": ["1.0.0+"],
        "tags": ["disney", "goodbye", "oem"],
    }
    with zipfile.ZipFile(goodbye_only_zip, "w") as zf:
        add_entry(zf, "boottheme.json", json.dumps(goodbye_only_manifest, indent=2).encode("utf-8"))
        add_entry(zf, "preview_poweroff.gif", preview_gif, zipfile.ZIP_STORED)
        add_entry(zf, "preview_poweroff_poster.jpg", preview_poster, zipfile.ZIP_STORED)
        add_entry(zf, "poweroff/goodbye_1920x720.mp4", SRC_DISNEY_MP4.read_bytes(), zipfile.ZIP_STORED)

    dist_themes = ROOT / "dist" / "StarterThemes"
    dist_themes.mkdir(parents=True, exist_ok=True)
    for zp in (OUT_ZIP, boot_only_zip, goodbye_only_zip):
        shutil.copy2(zp, dist_themes / zp.name)
        print(f"[OK] Staged {dist_themes / zp.name} ({zp.stat().st_size:,} bytes)")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
