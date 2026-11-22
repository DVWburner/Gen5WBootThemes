#!/usr/bin/env python3
"""
Gen5W Boot Splash Image Builder & Verifier
------------------------------------------
Converts any input image to a hardware-JPU-safe 1920x720 (and 1280x720) Baseline 4:4:4 JPEG
and packs a bit-accurate 16 MB Telechips `splash` partition image from the factory backup.

Hardware constraints verified from `mmcblk0boot0.img` (`splash_image_load` & `TCC_JPU_C6_V0.1.0.08`):
  - Partition size: 16,777,216 bytes (16 MB)
  - Header at 0x0000: b"splash\x00\x00" + uint32 count (13) + uint32 reserved (0)
  - 13 entries x 52 bytes at 0x0010..0x02B3:
      char name[20]
      uint32 offset   (512-byte sector aligned, first image at 0x800)
      uint32 size     (exact JPEG byte length; MUST be <= 524,288 bytes; JPU bitstream buf is 1 MB)
      uint32 width    (1920 or 1280)
      uint32 height   (720)
      uint32 padding  ((512 - (size % 512)) % 512)
      char format[12] ("JPEG\x00...")
  - JPEG format: Baseline DCT (SOF0, non-progressive), 3-component YCbCr 4:4:4 (subsampling=0 -> 1x1/1x1/1x1),
    no EXIF/ICC bloat, standard Huffman tables.
"""

import argparse
import hashlib
import io
import os
import struct
import sys
from PIL import Image

PARTITION_SIZE = 16 * 1024 * 1024  # 16,777,216 bytes
MAX_JPEG_BYTES = 450 * 1024        # Safe margin well below 1 MB JPU bitstream buffer (stock max is ~175 KB)

HYUNDAI_SLOTS_1920 = {"hyundai_1920x720", "newhyundai_1920x720", "hyundaieco_1920x720"}
HYUNDAI_SLOTS_1280 = {"hyundai_1280x720", "newhyundai_1280x720"}


def parse_splash_partition(raw: bytes) -> list[dict]:
    if len(raw) != PARTITION_SIZE:
        raise ValueError(f"Expected {PARTITION_SIZE} byte splash image, got {len(raw)}")
    magic = raw[0:8]
    if magic != b"splash\x00\x00":
        raise ValueError(f"Invalid splash magic: {magic!r}")
    count, reserved = struct.unpack_from("<II", raw, 8)
    if count <= 0 or count > 32 or reserved != 0:
        raise ValueError(f"Unexpected header fields: count={count}, reserved={reserved}")

    entries = []
    for idx in range(count):
        eoff = 16 + idx * 52
        name_raw = raw[eoff : eoff + 20]
        name = name_raw.split(b"\x00", 1)[0].decode("ascii")
        img_off, img_size, w, h, pad = struct.unpack_from("<IIIII", raw, eoff + 20)
        fmt_raw = raw[eoff + 40 : eoff + 52]
        fmt = fmt_raw.split(b"\x00", 1)[0].decode("ascii")
        data = raw[img_off : img_off + img_size]
        entries.append(
            {
                "name": name,
                "width": w,
                "height": h,
                "format": fmt,
                "data": data,
                "orig_offset": img_off,
                "orig_size": img_size,
                "orig_pad": pad,
            }
        )
    return entries


def pack_splash_partition(entries: list[dict]) -> bytes:
    out = bytearray(PARTITION_SIZE)
    out[0:8] = b"splash\x00\x00"
    struct.pack_into("<II", out, 8, len(entries), 0)

    cur_offset = 0x800
    for idx, ent in enumerate(entries):
        data = ent["data"]
        size = len(data)
        pad = (512 - (size % 512)) % 512
        if cur_offset + size + pad > PARTITION_SIZE:
            raise ValueError("Packed splash images exceed 16 MB partition size!")

        eoff = 16 + idx * 52
        name_bytes = ent["name"].encode("ascii")
        fmt_bytes = ent["format"].encode("ascii")
        if len(name_bytes) > 19 or len(fmt_bytes) > 11:
            raise ValueError(f"Entry name/format too long: {ent['name']} / {ent['format']}")

        out[eoff : eoff + len(name_bytes)] = name_bytes
        struct.pack_into("<IIIII", out, eoff + 20, cur_offset, size, ent["width"], ent["height"], pad)
        out[eoff + 40 : eoff + 40 + len(fmt_bytes)] = fmt_bytes

        out[cur_offset : cur_offset + size] = data
        cur_offset += size + pad

    return bytes(out)


def verify_jpu_jpeg(jpeg_bytes: bytes, expected_w: int, expected_h: int) -> dict:
    if len(jpeg_bytes) < 128 or jpeg_bytes[:2] != b"\xff\xd8" or jpeg_bytes[-2:] != b"\xff\xd9":
        raise ValueError("Invalid SOI/EOI markers in JPEG")
    if len(jpeg_bytes) > MAX_JPEG_BYTES:
        raise ValueError(f"JPEG size {len(jpeg_bytes)} exceeds safe JPU limit {MAX_JPEG_BYTES}")

    pos = 2
    found_sof0 = False
    markers = []
    subsampling = []
    while pos < len(jpeg_bytes) - 1:
        if jpeg_bytes[pos] != 0xFF:
            pos += 1
            continue
        while pos < len(jpeg_bytes) and jpeg_bytes[pos] == 0xFF:
            pos += 1
        if pos >= len(jpeg_bytes):
            break
        m = jpeg_bytes[pos]
        pos += 1
        markers.append(f"0xFF{m:02X}")
        if m in (0xD9, 0xDA):
            break
        if 0xD0 <= m <= 0xD8 or m == 0x01:
            continue
        seg_len = struct.unpack_from(">H", jpeg_bytes, pos)[0]
        if m == 0xC2:
            raise ValueError("Progressive JPEG (SOF2) is NOT allowed for Telechips JPU!")
        if m == 0xC0:
            prec, h, w, ncomp = struct.unpack_from(">BHHB", jpeg_bytes, pos + 2)
            if prec != 8 or ncomp != 3 or w != expected_w or h != expected_h:
                raise ValueError(f"Unexpected SOF0 params: prec={prec}, {w}x{h}, ncomp={ncomp}")
            for c in range(ncomp):
                _, samp, _ = struct.unpack_from(">BBB", jpeg_bytes, pos + 8 + c * 3)
                subsampling.append(f"{samp >> 4}x{samp & 0xF}")
            if subsampling != ["1x1", "1x1", "1x1"]:
                raise ValueError(f"Expected 4:4:4 (1x1/1x1/1x1) subsampling, got {subsampling}")
            found_sof0 = True
        pos += seg_len

    if not found_sof0:
        raise ValueError("Baseline SOF0 marker (0xFFC0) not found in JPEG")

    return {"size": len(jpeg_bytes), "markers": markers, "subsampling": "/".join(subsampling)}


def crop_and_encode_jpu_jpeg(
    img: Image.Image,
    target_w: int,
    target_h: int,
    crop_bottom_pct: float = 0.0,
    vertical_bias: float = 0.5,
) -> tuple[Image.Image, bytes]:
    rgb = img.convert("RGB")
    src_w, src_h = rgb.size

    if crop_bottom_pct > 0.0:
        keep_h = int(round(src_h * (1.0 - crop_bottom_pct)))
        rgb = rgb.crop((0, 0, src_w, keep_h))
        src_w, src_h = rgb.size

    target_aspect = target_w / target_h
    src_aspect = src_w / src_h

    if src_aspect > target_aspect:
        # Wider than target: crop left/right centered
        new_w = int(round(src_h * target_aspect))
        left = (src_w - new_w) // 2
        cropped = rgb.crop((left, 0, left + new_w, src_h))
    else:
        # Taller than target: crop top/bottom using vertical_bias (0.5 = center)
        new_h = int(round(src_w / target_aspect))
        max_top = src_h - new_h
        top = int(round(max_top * vertical_bias))
        top = max(0, min(max_top, top))
        cropped = rgb.crop((0, top, src_w, top + new_h))

    resized = cropped.resize((target_w, target_h), Image.Resampling.LANCZOS)

    # Encode as strict Baseline JFIF (progressive=False, subsampling=0 -> 4:4:4, no EXIF/ICC)
    for quality in (92, 88, 84, 80, 75):
        buf = io.BytesIO()
        resized.save(
            buf,
            format="JPEG",
            quality=quality,
            subsampling=0,
            progressive=False,
            optimize=False,
        )
        data = buf.getvalue()
        if len(data) <= MAX_JPEG_BYTES:
            verify_jpu_jpeg(data, target_w, target_h)
            return resized, data

    raise RuntimeError("Could not encode JPEG within MAX_JPEG_BYTES")


def main() -> int:
    ap = argparse.ArgumentParser(description="Build Gen5W splash.img from a custom image")
    ap.add_argument("--stock", required=True, help="Path to factory splash_p7.img")
    ap.add_argument("--input", required=True, help="Path to custom input image (PNG/JPG/WEBP)")
    ap.add_argument("--output", required=True, help="Output path for custom splash.img")
    ap.add_argument("--preview", required=False, help="Optional path to save the 1920x720 JPEG preview")
    ap.add_argument("--crop-bottom-pct", type=float, default=0.0, help="Fraction of bottom edge to trim first (e.g. 0.04 for watermark)")
    ap.add_argument("--vertical-bias", type=float, default=0.5, help="Vertical crop center (0.0=top, 0.5=center, 1.0=bottom)")
    args = ap.parse_args()

    with open(args.stock, "rb") as f:
        stock_bytes = f.read()

    # Round-trip self-test on factory stock image first!
    stock_entries = parse_splash_partition(stock_bytes)
    repacked_stock = pack_splash_partition(stock_entries)
    if repacked_stock != stock_bytes:
        raise AssertionError("CRITICAL: Packer round-trip of factory splash_p7.img is NOT bit-identical!")
    print("[OK] Round-trip self-test passed: packer reproduces factory splash_p7.img bit-for-bit!")

    src_img = Image.open(args.input)
    _, jpg_1920 = crop_and_encode_jpu_jpeg(
        src_img, 1920, 720, crop_bottom_pct=args.crop_bottom_pct, vertical_bias=args.vertical_bias
    )
    _, jpg_1280 = crop_and_encode_jpu_jpeg(
        src_img, 1280, 720, crop_bottom_pct=args.crop_bottom_pct, vertical_bias=args.vertical_bias
    )

    info_1920 = verify_jpu_jpeg(jpg_1920, 1920, 720)
    info_1280 = verify_jpu_jpeg(jpg_1280, 1280, 720)
    print(f"[OK] Encoded 1920x720 JPEG: {info_1920['size']:,} bytes | subsampling={info_1920['subsampling']} | markers={','.join(info_1920['markers'])}")
    print(f"[OK] Encoded 1280x720 JPEG: {info_1280['size']:,} bytes | subsampling={info_1280['subsampling']}")

    if args.preview:
        os.makedirs(os.path.dirname(os.path.abspath(args.preview)), exist_ok=True)
        with open(args.preview, "wb") as pf:
            pf.write(jpg_1920)
        print(f"[OK] Saved 1920x720 preview to: {args.preview}")

    new_entries = []
    for ent in stock_entries:
        updated = dict(ent)
        if ent["name"] in HYUNDAI_SLOTS_1920:
            updated["data"] = jpg_1920
        elif ent["name"] in HYUNDAI_SLOTS_1280:
            updated["data"] = jpg_1280
        new_entries.append(updated)

    custom_splash = pack_splash_partition(new_entries)
    # Re-parse custom_splash and verify every single entry!
    verified_entries = parse_splash_partition(custom_splash)
    assert len(verified_entries) == 13
    for ve in verified_entries:
        if "error_" not in ve["name"]:
            verify_jpu_jpeg(ve["data"], ve["width"], ve["height"])

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "wb") as out_f:
        out_f.write(custom_splash)

    sha = hashlib.sha256(custom_splash).hexdigest()
    print(f"[OK] Built & verified custom splash image: {args.output} ({len(custom_splash):,} bytes, sha256={sha})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
