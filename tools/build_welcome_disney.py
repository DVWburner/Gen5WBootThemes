#!/usr/bin/env python3
"""
Build WelcomeGoodbye_disney.odex for Gen5W (`com.mobis.welcome`)
----------------------------------------------------------------
Patches the Dalvik bytecode in `WelcomeService.m()` inside `WelcomeGoodbye.odex`
so the `ecohmc` (Ioniq 5) branch unconditionally selects the embedded stock
Disney 100th video (`res/raw/welcome_disney_100_1920.mp4`, resource ID `0x7f030000`
= `"android.resource://com.mobis.welcome/2130903040"`) for BOTH Welcome (`G`) and
Goodbye (`H`) without modifying `WelcomeGoodbye.apk` on `/system`!

Specifically:
  In `WelcomeService.m()`:
    71 10 xx xx 03 00   invoke-static {v3}, VariantCoding->getVariantCode(I)I
    0A 00               move-result v0
    13 01 36 00         const/16 v1, 0x36
    33 10 15 00         if-ne v0, v1, :cond_98
  We replace `move-result v0` (`0A 00`) with `const/16 v0, 0x36`? Wait: `0A 00` is 2 bytes (1 code unit),
  whereas `const/16` is 4 bytes! Instead, we replace `if-ne v0, v1, :cond_98` (`33 10 15 00`, 4 bytes)
  with two `nop` instructions (`00 00 00 00`, 4 bytes)!
  Wait: doing that neither alters the string table sort order nor changes any offsets, AND logs
  `"setBrand Disney-100th"` cleanly in logcat while setting both `WelcomeService.G` and `WelcomeService.H`
  to `"android.resource://com.mobis.welcome/2130903040"`!
  Let's verify the exact bytecode sequence in `WelcomeGoodbye.odex`, recompute both DEX SHA-1 and
  DEX Adler32 checksums, and verify with `baksmali`!
"""

import hashlib
import os
import struct
import subprocess
import zlib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
STOCK_ODEX = os.path.join(ROOT, "oem-boot-dump", "02_system_media_and_apps", "WelcomeGoodbye.odex")
OUT_ODEX = os.path.join(ROOT, "build", "WelcomeGoodbye_disney.odex")


def main() -> int:
    with open(STOCK_ODEX, "rb") as f:
        odex = bytearray(f.read())

    if odex[:4] != b"dey\n":
        raise ValueError(f"Unexpected ODEX magic: {odex[:8]!r}")

    dex_off, dex_len = struct.unpack_from("<II", odex, 8)
    print(f"Stock WelcomeGoodbye.odex: {len(odex):,} bytes | dex_off={dex_off}, dex_len={dex_len}")

    if odex[dex_off : dex_off + 4] != b"dex\n":
        raise ValueError("Embedded DEX magic mismatch")

    # Verify original Adler32 & SHA-1 so we know the exact layout
    orig_adler = struct.unpack_from("<I", odex, dex_off + 8)[0]
    calc_adler = zlib.adler32(odex[dex_off + 12 : dex_off + dex_len]) & 0xFFFFFFFF
    orig_sha1 = bytes(odex[dex_off + 12 : dex_off + 32])
    calc_sha1 = hashlib.sha1(odex[dex_off + 32 : dex_off + dex_len]).digest()
    print(f"Original Adler32: 0x{orig_adler:08x} (calc: 0x{calc_adler:08x})")
    print(f"Original SHA-1 match: {orig_sha1 == calc_sha1}")
    assert orig_adler == calc_adler, "Stock DEX Adler32 mismatch!"

    # Search for:
    #   0a 00        move-result v0
    #   13 01 36 00  const/16 v1, 0x36
    #   33 10 xx 00  if-ne v0, v1, +xx
    pattern_prefix = bytes([0x0A, 0x00, 0x13, 0x01, 0x36, 0x00, 0x33, 0x10])
    matches = []
    pos = dex_off
    end = dex_off + dex_len
    while True:
        idx = odex.find(pattern_prefix, pos, end)
        if idx < 0:
            break
        branch_off = struct.unpack_from("<h", odex, idx + 8)[0]
        matches.append((idx, branch_off))
        pos = idx + 2

    print(f"Found {len(matches)} VariantCoding 0x36 branch match(es): {matches}")
    if len(matches) != 1:
        raise RuntimeError(f"Expected exactly 1 match for VariantCoding 0x36 check, got {len(matches)}")

    match_idx, branch_off = matches[0]
    if_ne_idx = match_idx + 6
    print(f"Patching `if-ne v0, v1, +{branch_off}` at offset 0x{if_ne_idx:x} ({odex[if_ne_idx:if_ne_idx+4].hex()}) -> `nop; nop` (00000000)")

    # Replace 4-byte `if-ne v0, v1, :cond_98` with two 2-byte Dalvik `nop`s (00 00 00 00)
    # Execution falls straight through into `"setBrand Disney-100th"` and sets both G & H to 2130903040!
    # IMPORTANT: Keep original unoptimized DEX SHA-1 at +12..+32 untouched (just like dexopt & Gen5WThemer),
    # and recompute only the DEX Adler32 checksum over dex_off+12 .. dex_off+dex_len!
    odex[if_ne_idx : if_ne_idx + 4] = b"\x00\x00\x00\x00"

    new_adler = zlib.adler32(odex[dex_off + 12 : dex_off + dex_len]) & 0xFFFFFFFF
    struct.pack_into("<I", odex, dex_off + 8, new_adler)
    print(f"Preserved original SHA-1={orig_sha1.hex()} | Updated Adler32=0x{new_adler:08x}")

    os.makedirs(os.path.dirname(OUT_ODEX), exist_ok=True)
    with open(OUT_ODEX, "wb") as f:
        f.write(odex)

    print(f"[OK] Wrote {OUT_ODEX} ({len(odex):,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
