#!/usr/bin/env python3
"""
Procedural Neon Wave 1920x720 Startup Animation & Frame-0 Splash Generator
--------------------------------------------------------------------------
Generates a crisp, anti-aliased (2x supersampled), seamlessly looping 12.0-second
(288 frames @ 24 fps) 1920x720 procedural Neon Wave startup animation modeled
closely after the user's Neon Wave artwork:
  - Sweeping upper-left electric blue -> magenta nexus -> lower-right crimson/amber diagonal arc
  - Dual-lobed cyan/blue (left) & coral/amber/gold (right) silk/plasma loops pinching at the center
  - Supersampled 3840x1440 core curves downsampled with LANCZOS for zero stair-stepping
  - Frame 000 matches the static `splash` image (`splash_custom_neonwave_anim.img`)
    pixel-for-pixel so bootloader splash (0.0s - ~3.8s) transitions seamlessly into
    `/data/local/bootanimation.zip` (~3.8s - 10.5s+).
"""

import io
import math
import os
import subprocess
import time
import zipfile
from concurrent.futures import ProcessPoolExecutor
from PIL import Image, ImageChops, ImageDraw, ImageFilter

W = 1920
H = 720
FPS = 24
DURATION_SEC = 12
TOTAL_FRAMES = FPS * DURATION_SEC  # 288 frames = 12.0 seconds seamless loop

GW = 960
GH = 360
SW = 3840  # 2x supersampled core canvas for silky anti-aliased curves
SH = 1440


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def sample_palette(u: float) -> tuple[int, int, int]:
    stops = [
        (0.00, (0, 222, 255)),
        (0.18, (12, 130, 255)),
        (0.36, (95, 65, 255)),
        (0.49, (255, 92, 215)),
        (0.64, (255, 65, 48)),
        (0.82, (255, 165, 12)),
        (1.00, (255, 232, 42)),
    ]
    u = max(0.0, min(1.0, u))
    for i in range(len(stops) - 1):
        u0, c0 = stops[i]
        u1, c1 = stops[i + 1]
        if u <= u1:
            t = (u - u0) / (u1 - u0) if u1 > u0 else 0.0
            return (
                int(round(lerp(c0[0], c1[0], t))),
                int(round(lerp(c0[1], c1[1], t))),
                int(round(lerp(c0[2], c1[2], t))),
            )
    return stops[-1][1]


def eval_ribbon_y(ribbon_id: int, u: float, phase: float) -> float:
    """
    Normalized y in [0..1] for x=u in [0..1], periodic in `phase` in [0..2*pi).
    Matches the iconic geometry of the user's Neon Wave image:
      - Central magenta nexus around (u=0.49, y=0.545)
      - Sweeping upper-left (u~0.08, y~0.03) -> nexus -> lower-right (u~0.85, y~0.94) diagonal arc
      - Flowing cyan/blue left loops and coral/gold right loops that breathe, undulate, and twist!
    """
    nexus_u = 0.49 + 0.022 * math.sin(phase)
    nexus_y = 0.545 + 0.018 * math.cos(phase)
    du = u - nexus_u

    # Horizontal spine with gentle traveling wave
    spine_y = (
        nexus_y
        - 0.11 * du
        + 0.026 * math.sin(2.0 * math.pi * u - phase)
        + 0.012 * math.sin(4.0 * math.pi * u - 2.0 * phase + 0.6)
    )

    if ribbon_id == 0:
        # Upper-left to lower-right sweeping S-arc (signature diagonal curve from the photo!)
        # At u=0.08 (du=-0.41) -> y ~ 0.04; at u=0.49 (du=0) -> y = nexus_y; at u=0.84 (du=+0.35) -> y ~ 0.92
        cubic = 1.05 * du + 3.4 * (du ** 3)
        ripple = 0.045 * du * math.sin(3.5 * du - phase)
        return nexus_y + cubic + ripple

    # Smooth pinch envelope so ribbons 1..4 all focus cleanly through the glowing nexus
    pinch = 0.018 + 1.45 * (du * du) / (0.08 + 1.2 * (du * du))  # 0.018 at nexus, ~0.35 in lobes

    if ribbon_id == 1:
        # Primary bright cyan (bottom-left) -> nexus -> bright gold (upper-right) wave
        lobe = -0.34 * math.sin(4.4 * du - phase * 1.0) - 0.11 * math.sin(8.0 * du - 2.0 * phase)
        tilt = -0.24 * du
        return spine_y + tilt + pinch * lobe
    elif ribbon_id == 2:
        # Counter-arching twin ribbon (forms the wide upper-cyan & lower-amber silk loops)
        lobe = 0.38 * math.sin(4.1 * du - phase * 1.0 + 0.25) + 0.10 * math.cos(7.2 * du - 2.0 * phase)
        tilt = -0.10 * du
        return spine_y + tilt + pinch * lobe
    elif ribbon_id == 3:
        # Secondary inner harmonic loop (crosses gracefully at left cyan & right gold focal nodes)
        lobe = 0.26 * math.cos(5.6 * du - phase + 0.8) - 0.12 * math.sin(3.2 * du + phase)
        return spine_y - 0.16 * du + pinch * lobe
    else:
        # Delicate plasma filament dancing around the main stream
        lobe = -0.22 * math.sin(6.8 * du - 2.0 * phase + 1.4) + 0.14 * math.cos(4.0 * du - phase)
        return spine_y - 0.14 * du + pinch * lobe


def render_frame(frame_idx: int) -> bytes:
    phase = (2.0 * math.pi * frame_idx) / TOTAL_FRAMES
    nexus_u = 0.49 + 0.022 * math.sin(phase)

    # 1. Atmospheric colored corner/corridor glow (matches the soft cyan-left & amber-right aura)
    amb_w, amb_h = 240, 90
    amb = Image.new("RGB", (amb_w, amb_h), (0, 0, 0))
    amb_draw = ImageDraw.Draw(amb)

    pulse1 = (phase / (2.0 * math.pi) * 2.0) % 1.0
    pulse2 = (pulse1 + 0.5) % 1.0

    for i in range(84):
        u = i / 83.0
        col = sample_palette(u)
        ym = eval_ribbon_y(1, u, phase)
        cx = u * amb_w
        cy = ym * amb_h
        dp = min(abs(u - pulse1), 1.0 - abs(u - pulse1))
        boost = 0.95 + 0.40 * math.exp(-((dp / 0.15) ** 2))
        rad = 13.0 + 7.0 * math.exp(-(((u - nexus_u) / 0.28) ** 2))
        amb_draw.ellipse(
            (cx - rad, cy - rad * 0.9, cx + rad, cy + rad * 0.9),
            fill=(
                min(255, int(col[0] * 0.20 * boost)),
                min(255, int(col[1] * 0.20 * boost)),
                min(255, int(col[2] * 0.20 * boost)),
            ),
        )

    amb = amb.filter(ImageFilter.GaussianBlur(radius=15))
    base = amb.resize((W, H), Image.Resampling.BICUBIC)

    # 2. Multi-radius neon bloom layers (960x360) + 2x supersampled crisp core (3840x1440)
    glow_med = Image.new("RGB", (GW, GH), (0, 0, 0))
    gm_draw = ImageDraw.Draw(glow_med)

    glow_tight = Image.new("RGB", (GW, GH), (0, 0, 0))
    gt_draw = ImageDraw.Draw(glow_tight)

    core_ss = Image.new("RGB", (SW, SH), (0, 0, 0))
    cs_draw = ImageDraw.Draw(core_ss)

    ribbon_specs = [
        # (ribbon_id, intensity, med_w, tight_w, ss_core_w, color_shift)
        (0, 0.88, 8, 4, 5, -0.05),   # Diagonal sweeping blue -> magenta -> red/amber arc
        (4, 0.68, 6, 3, 4, 0.02),    # Inner harmonic filament
        (3, 0.82, 8, 4, 5, -0.02),   # Secondary lobe ribbon
        (2, 0.96, 10, 5, 6, 0.01),   # Counter-arching wide silk loop
        (1, 1.12, 12, 6, 8, 0.00),   # Primary hero neon wave
    ]

    num_seg = 240
    for rid, inten, mw, tw, ssw, cshift in ribbon_specs:
        pts_g = []
        pts_s = []
        for s in range(num_seg + 1):
            u = s / num_seg
            yv = eval_ribbon_y(rid, u, phase)
            pts_g.append((u * GW, yv * GH))
            pts_s.append((u * SW, yv * SH))

        for s in range(num_seg):
            u = (s + 0.5) / num_seg
            yv = (pts_g[s][1] + pts_g[s + 1][1]) / (2.0 * GH)

            # Graceful fade at top/bottom screen limits for the sweeping diagonal arc (ribbon 0)
            edge_fade = 1.0
            if rid == 0:
                if yv < 0.02 or yv > 0.96:
                    continue
                if yv < 0.12:
                    edge_fade = (yv - 0.02) / 0.10
                elif yv > 0.82:
                    edge_fade = (0.96 - yv) / 0.14

            d1 = min(abs(u - pulse1), 1.0 - abs(u - pulse1))
            d2 = min(abs(u - pulse2), 1.0 - abs(u - pulse2))
            shimmer = 0.86 + 0.34 * math.exp(-((d1 / 0.11) ** 2)) + 0.22 * math.exp(-((d2 / 0.14) ** 2))

            # Bright hot-spots at the central magenta nexus and left-cyan / right-gold focal pinch points
            dn = abs(u - nexus_u)
            dl = abs(u - 0.12)
            dr = abs(u - 0.88)
            focal = (
                1.0
                + 0.46 * math.exp(-((dn / 0.075) ** 2))
                + 0.28 * math.exp(-((dl / 0.065) ** 2))
                + 0.30 * math.exp(-((dr / 0.065) ** 2))
            )

            k = inten * edge_fade * shimmer * focal
            col = sample_palette(u + cshift)

            gm_draw.line(
                [pts_g[s], pts_g[s + 1]],
                fill=(
                    min(255, int(col[0] * 0.58 * k)),
                    min(255, int(col[1] * 0.58 * k)),
                    min(255, int(col[2] * 0.58 * k)),
                ),
                width=mw,
            )
            gt_draw.line(
                [pts_g[s], pts_g[s + 1]],
                fill=(
                    min(255, int(col[0] * 0.90 * k)),
                    min(255, int(col[1] * 0.90 * k)),
                    min(255, int(col[2] * 0.90 * k)),
                ),
                width=tw,
            )

            whiten = min(0.76, 0.30 * k)
            cs_draw.line(
                [pts_s[s], pts_s[s + 1]],
                fill=(
                    min(255, int(lerp(col[0], 255, whiten) * min(1.20, k))),
                    min(255, int(lerp(col[1], 255, whiten) * min(1.20, k))),
                    min(255, int(lerp(col[2], 255, whiten) * min(1.20, k))),
                ),
                width=ssw,
            )

    # 3. Subtle drifting micro-bokeh sparks along the ribbons
    for p_idx in range(24):
        seed_u = (p_idx * 0.61803398875) % 1.0
        pu = (seed_u + (phase / (2.0 * math.pi)) * (0.12 if p_idx % 2 == 0 else -0.12)) % 1.0
        env = math.sin(math.pi * pu) ** 1.6
        twinkle = 0.5 + 0.5 * math.sin(2.0 * phase + p_idx * 1.9)
        alpha = env * twinkle * 0.65
        if alpha < 0.08:
            continue
        by = eval_ribbon_y(1 + (p_idx % 2), pu, phase) + 0.04 * math.sin(p_idx * 3.1 + phase)
        px, py = pu * GW, by * GH
        pcol = sample_palette(pu)
        rad = 1.4 + (p_idx % 3) * 0.7
        gt_draw.ellipse(
            (px - rad, py - rad, px + rad, py + rad),
            fill=(
                min(255, int(lerp(pcol[0], 255, 0.35) * alpha)),
                min(255, int(lerp(pcol[1], 255, 0.35) * alpha)),
                min(255, int(lerp(pcol[2], 255, 0.35) * alpha)),
            ),
        )

    # 4. Composite bloom + supersampled LANCZOS core
    glow_wide = glow_med.filter(ImageFilter.GaussianBlur(radius=19))
    glow_mid = glow_med.filter(ImageFilter.GaussianBlur(radius=7.5))
    glow_t = glow_tight.filter(ImageFilter.GaussianBlur(radius=2.5))

    glow_all = ImageChops.add(ImageChops.add(glow_wide, glow_mid), glow_t)
    glow_up = glow_all.resize((W, H), Image.Resampling.BICUBIC)
    core_1x = core_ss.resize((W, H), Image.Resampling.LANCZOS)

    final = ImageChops.add(ImageChops.add(base, glow_up), core_1x)

    buf = io.BytesIO()
    final.save(buf, format="JPEG", quality=85, subsampling=0, progressive=False, optimize=False)
    return buf.getvalue()


def main() -> int:
    out_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "build"))
    os.makedirs(out_dir, exist_ok=True)
    frames_dir = os.path.join(out_dir, "neonwave_frames")
    os.makedirs(frames_dir, exist_ok=True)

    print(f"Generating {TOTAL_FRAMES} supersampled frames ({DURATION_SEC}s @ {FPS} fps, {W}x{H})...")
    t0 = time.time()

    workers = min(8, os.cpu_count() or 4)
    with ProcessPoolExecutor(max_workers=workers) as ex:
        results = list(ex.map(render_frame, range(TOTAL_FRAMES), chunksize=8))

    dt = time.time() - t0
    total_bytes = sum(len(b) for b in results)
    max_frame = max(len(b) for b in results)
    print(
        f"Rendered {TOTAL_FRAMES} frames in {dt:.1f}s | total={total_bytes/1048576:.2f} MB | "
        f"avg={total_bytes//TOTAL_FRAMES:,} B | max={max_frame:,} B"
    )

    frame0_path = os.path.join(out_dir, "neonwave_proc_frame0_1920x720.jpg")
    with open(frame0_path, "wb") as f:
        f.write(results[0])

    frame_mid_path = os.path.join(out_dir, "neonwave_proc_frame144_1920x720.jpg")
    with open(frame_mid_path, "wb") as f:
        f.write(results[TOTAL_FRAMES // 2])

    bootanim_zip_path = os.path.join(out_dir, "bootanimation.zip")
    fixed_dt = (2026, 1, 1, 0, 0, 0)
    desc_txt = f"{W} {H} {FPS}\np 0 0 part0\n".encode("ascii")

    with zipfile.ZipFile(bootanim_zip_path, "w", compression=zipfile.ZIP_STORED) as zf:
        zi_desc = zipfile.ZipInfo("desc.txt", date_time=fixed_dt)
        zi_desc.compress_type = zipfile.ZIP_STORED
        zi_desc.create_system = 3
        zi_desc.external_attr = 0o100644 << 16
        zf.writestr(zi_desc, desc_txt)

        for idx, jpg_bytes in enumerate(results):
            fname = f"{idx:03d}.jpg"
            disk_p = os.path.join(frames_dir, fname)
            with open(disk_p, "wb") as df:
                df.write(jpg_bytes)

            zi = zipfile.ZipInfo(f"part0/{fname}", date_time=fixed_dt)
            zi.compress_type = zipfile.ZIP_STORED
            zi.create_system = 3
            zi.external_attr = 0o100644 << 16
            zf.writestr(zi, jpg_bytes)

    zip_sz = os.path.getsize(bootanim_zip_path)
    print(f"Created {bootanim_zip_path} ({zip_sz:,} bytes / {zip_sz/1048576:.2f} MB, ZIP_STORED)")

    try:
        import imageio_ffmpeg
        ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
        mp4_path = os.path.join(out_dir, "preview_neonwave_anim.mp4")
        subprocess.run(
            [
                ffmpeg_exe, "-y",
                "-framerate", str(FPS),
                "-i", os.path.join(frames_dir, "%03d.jpg"),
                "-c:v", "libx264",
                "-pix_fmt", "yuv420p",
                "-crf", "18",
                mp4_path,
            ],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        print(f"Created MP4 preview: {mp4_path} ({os.path.getsize(mp4_path):,} bytes)")
    except Exception as e:
        print(f"MP4 preview skipped: {e}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
