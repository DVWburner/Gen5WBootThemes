#!/usr/bin/env python3
"""
Build & Stage Gen5W Boot + Welcome Customizer Package (`gen5w.boot.customizer`)
-------------------------------------------------------------------------------
Packages:
  1. Procedural Neon Wave Frame 0 -> JPU-verified `splash` (`mmcblk0p7` + `mmcblk0p20`)
  2. Procedural Neon Wave 12.0s (288 frames @ 24 fps, 1920x720) seamless loop -> `/data/local/bootanimation.zip`
  3. Static ARM32 `fb0_unlock` helper (`ioctl("/dev/graphics/fb0", 0x402, &0)`) -> unlocks `TCC_FB_UPDATE_LOCK`
     as soon as `init.svc.bootanim=running` so `bootanimation.zip` is visible on the LCD instead of hidden
     behind the bootloader splash lock!
  4. `WelcomeGoodbye_disney.odex` -> RAM bind-mounted over `/system/app/WelcomeGoodbye.odex` on every boot
     (and immediately on install) so stock Disney 100th (`welcome_disney_100_1920.mp4`) plays for BOTH
     Hello (Welcome) and Goodbye, with zero writes to `/system`!
  5. Full stock backup (`splash_stock_backup.img` on both `/data/nemesis/boot_customizer/` and USB) +
     automatic restore via NEMESIS Manager Uninstall (`preun.sh`) OR USB `RESTORE_STOCK.txt` kill-switch!
"""

from __future__ import annotations

import hashlib
import os
import re
import shutil
import struct
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BUILD_DIR = ROOT / "build"
DIST_DIR = ROOT / "dist"
PKG_SRC_DIR = ROOT / "packages" / "gen5w.boot.customizer"

PKG_ID = "gen5w.boot.customizer"
PKG_VER = "1.0.0"
ZIP_FIXED_TIME = (2026, 1, 1, 0, 0, 0)

FORBIDDEN_SH_COMMANDS = (
    "sed",
    "tr",
    "awk",
    "head",
    "tail",
    "wc",
    "cut",
    "find",
    "xargs",
    "basename",
    "dirname",
    "printf",
    "[[",
    "declare",
    "killall",
    "pkill",
)


def build_fb0_unlock_elf() -> bytes:
    """
    Minimal static ARMv7-A Linux EABI 32-bit executable (184 bytes):
      int fd = open("/dev/graphics/fb0", O_RDWR, 0);
      if (fd >= 0) {
          uint32_t zero = 0;
          ioctl(fd, 0x402, &zero); // TCC_FB_UPDATE_LOCK = 0
          close(fd);
      }
      exit_group(0);
    """
    load_vaddr = 0x00010000
    ehdr_size = 52
    phdr_size = 32
    entry = load_vaddr + ehdr_size + phdr_size  # 0x00010054

    insns = [
        0xE28F0048,  # add  r0, pc, #0x48 -> "/dev/graphics/fb0"
        0xE3A01002,  # mov  r1, #2 (O_RDWR)
        0xE3A02000,  # mov  r2, #0
        0xE3A07005,  # mov  r7, #5 (__NR_open)
        0xEF000000,  # svc  #0
        0xE3500000,  # cmp  r0, #0
        0xBA000009,  # blt  exit_lbl
        0xE1A04000,  # mov  r4, r0
        0xE3A02000,  # mov  r2, #0
        0xE52D2004,  # str  r2, [sp, #-4]!
        0xE3001402,  # movw r1, #0x402
        0xE1A0200D,  # mov  r2, sp
        0xE3A07036,  # mov  r7, #54 (__NR_ioctl)
        0xEF000000,  # svc  #0
        0xE1A00004,  # mov  r0, r4
        0xE3A07006,  # mov  r7, #6 (__NR_close)
        0xEF000000,  # svc  #0
        0xE3A00000,  # exit_lbl: mov r0, #0
        0xE3A070F8,  # mov  r7, #248 (__NR_exit_group)
        0xEF000000,  # svc  #0
    ]
    code = b"".join(struct.pack("<I", w) for w in insns)
    path_bytes = b"/dev/graphics/fb0\x00\x00\x00"
    total_size = ehdr_size + phdr_size + len(code) + len(path_bytes)

    e_ident = b"\x7fELF" + bytes([1, 1, 1, 0]) + b"\x00" * 8
    ehdr = struct.pack(
        "<16sHHIIIIIHHHHHH",
        e_ident,
        2, 40, 1, entry, ehdr_size, 0, 0x05000000,
        ehdr_size, phdr_size, 1, 0, 0, 0,
    )
    phdr = struct.pack(
        "<IIIIIIII",
        1, 0, load_vaddr, load_vaddr, total_size, total_size, 5, 0x1000,
    )
    elf = ehdr + phdr + code + path_bytes
    assert len(elf) == 184
    return elf


def validate_sh_text(name: str, text: str) -> bytes:
    text_lf = text.replace("\r\n", "\n").replace("\r", "\n")
    raw = text_lf.encode("utf-8")
    if raw.startswith(b"\xef\xbb\xbf"):
        raise ValueError(f"{name}: UTF-8 BOM is forbidden")
    if b"\r" in raw:
        raise ValueError(f"{name}: CR bytes are forbidden")
    if not text_lf.startswith("#!/system/bin/sh\n"):
        raise ValueError(f"{name}: must start with #!/system/bin/sh")

    for lineno, line in enumerate(text_lf.splitlines(), 1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        for cmd in FORBIDDEN_SH_COMMANDS:
            if cmd == "[[":
                if "[[" in stripped:
                    raise ValueError(f"{name}:{lineno}: forbidden `[[` syntax: {stripped}")
            else:
                if re.search(rf"(?<![\w.-]){re.escape(cmd)}(?![\w.-])", stripped):
                    # Ignore occurrences inside quoted echo strings if safe, check token-level
                    no_quotes = re.sub(r'"[^"]*"|\'[^\']*\'', "", stripped)
                    if re.search(rf"(?<![\w.-]){re.escape(cmd)}(?![\w.-])", no_quotes):
                        raise ValueError(f"{name}:{lineno}: forbidden command `{cmd}`: {stripped}")
        if "umount -l" in stripped:
            raise ValueError(f"{name}:{lineno}: `umount -l` is unsupported by Gen5W toolbox")
    return raw


def validate_nemesis_meta(name: str, text: str) -> bytes:
    text_lf = text.replace("\r\n", "\n").replace("\r", "\n")
    raw = text_lf.encode("utf-8")
    if raw.startswith(b"\xef\xbb\xbf") or b"\r" in raw:
        raise ValueError(f"{name}: invalid BOM or CR")
    for line in text_lf.splitlines():
        if line.startswith("short-desc="):
            val = line.split("=", 1)[1].strip().strip('"')
            if len(val) > 64:
                raise ValueError(f"{name}: short-desc is {len(val)} chars (max 64)")
        elif line.startswith("detailed-desc="):
            val = line.split("=", 1)[1].strip().strip('"')
            if len(val) > 256:
                raise ValueError(f"{name}: detailed-desc is {len(val)} chars (max 256)")
    return raw


def write_zip_entry(
    zf: zipfile.ZipFile,
    arcname: str,
    data: bytes,
    *,
    executable: bool = False,
    compress_type: int = zipfile.ZIP_DEFLATED,
) -> None:
    info = zipfile.ZipInfo(arcname, date_time=ZIP_FIXED_TIME)
    info.compress_type = compress_type
    info.create_system = 3  # Unix
    info.external_attr = (0o100755 if executable else 0o100644) << 16
    zf.writestr(info, data)


NEMESIS_PKG_DATA = """[nemesis]
version=1.1
id=gen5w.boot.customizer
name=NeonWave Boot & Disney Welcome
type=service
package-version=1.0.0
short-desc="Procedural NeonWave boot anim + Disney 100th Hello/Goodbye"
detailed-desc="Flashes Frame-0 NeonWave splash, installs 12s procedural NeonWave bootanimation.zip with fb0 unlock, and RAM-mounts Disney 100th Welcome/Goodbye video with full stock backup & restore."
os=any
models=any
preinstall=scripts/pre.sh
postinstall=scripts/post.sh
preuninstall=scripts/preun.sh

[service.0]
name=boot-customizer
kind=sh
path=svc/boot-helper.sh
uid=root
mode=restart
"""

PRE_SH = """#!/system/bin/sh
export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_customizer"
mkdir -p "$BASE" 2>/dev/null
chmod 755 "$BASE" 2>/dev/null
exit 0
"""

POST_SH = """#!/system/bin/sh
export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_customizer"
mkdir -p "$BASE" 2>/dev/null
chmod 755 "$BASE" 2>/dev/null

LOG="$BASE/install.log"
echo "=== gen5w.boot.customizer postinstall $(date 2>/dev/null) ===" > "$LOG"

# Locate USB drive containing gen5w-boot-splash payload
USB_DIR=""
for d in /mnt/media_rw/usb0 /mnt/media_rw/usb1 /mnt/media_rw/usb2 /storage/usb0 /storage/usb1 /storage/usb2 /mnt/usb_storage /storage/usbdisk; do
    if [ -d "$d/gen5w-boot-splash" ]; then
        USB_DIR="$d"
        break
    fi
done

echo "USB_DIR=$USB_DIR" >> "$LOG"

# Find installed package root where NEMESIS unpacked svc/ and files/
PKG_ROOT=""
for p in /data/nemesis/packages/gen5w.boot.customizer /data/nemesis/pkgs/gen5w.boot.customizer /oem_data/nemesis/packages/gen5w.boot.customizer; do
    if [ -d "$p" ]; then
        PKG_ROOT="$p"
        break
    fi
done
if [ -z "$PKG_ROOT" ] && [ -d "./files" ]; then
    PKG_ROOT="."
fi
echo "PKG_ROOT=$PKG_ROOT" >> "$LOG"

# 1. Stage patched WelcomeGoodbye_disney.odex & fb0_unlock into persistent $BASE
SRC_ODEX=""
if [ -n "$PKG_ROOT" ] && [ -f "$PKG_ROOT/files/WelcomeGoodbye_disney.odex" ]; then
    SRC_ODEX="$PKG_ROOT/files/WelcomeGoodbye_disney.odex"
elif [ -n "$USB_DIR" ] && [ -f "$USB_DIR/gen5w-boot-splash/WelcomeGoodbye_disney.odex" ]; then
    SRC_ODEX="$USB_DIR/gen5w-boot-splash/WelcomeGoodbye_disney.odex"
fi

if [ -n "$SRC_ODEX" ]; then
    cp "$SRC_ODEX" "$BASE/WelcomeGoodbye_disney.odex" 2>>"$LOG"
    chown 0.0 "$BASE/WelcomeGoodbye_disney.odex" 2>/dev/null || chown root:root "$BASE/WelcomeGoodbye_disney.odex" 2>/dev/null
    chmod 644 "$BASE/WelcomeGoodbye_disney.odex" 2>/dev/null
    echo "[OK] Staged WelcomeGoodbye_disney.odex" >> "$LOG"
fi

SRC_UNLOCK=""
if [ -n "$PKG_ROOT" ] && [ -f "$PKG_ROOT/files/fb0_unlock" ]; then
    SRC_UNLOCK="$PKG_ROOT/files/fb0_unlock"
elif [ -n "$USB_DIR" ] && [ -f "$USB_DIR/gen5w-boot-splash/fb0_unlock" ]; then
    SRC_UNLOCK="$USB_DIR/gen5w-boot-splash/fb0_unlock"
fi

if [ -n "$SRC_UNLOCK" ]; then
    cp "$SRC_UNLOCK" "$BASE/fb0_unlock" 2>>"$LOG"
    chown 0.0 "$BASE/fb0_unlock" 2>/dev/null || chown root:root "$BASE/fb0_unlock" 2>/dev/null
    chmod 755 "$BASE/fb0_unlock" 2>/dev/null
    echo "[OK] Staged fb0_unlock" >> "$LOG"
fi

# 2. Save factory stock splash backup onto internal storage (/oem_data & $BASE) so uninstall works offline
STOCK_BACKUP_DEST="$BASE/splash_stock_backup.img"
if [ ! -s "$STOCK_BACKUP_DEST" ]; then
    if [ -n "$USB_DIR" ] && [ -s "$USB_DIR/gen5w-boot-splash/splash_stock_backup.img" ]; then
        cp "$USB_DIR/gen5w-boot-splash/splash_stock_backup.img" "$STOCK_BACKUP_DEST" 2>>"$LOG"
        echo "[OK] Copied factory splash_stock_backup.img from USB -> $STOCK_BACKUP_DEST" >> "$LOG"
    elif [ -b "/dev/block/mmcblk0p7" ]; then
        dd if=/dev/block/mmcblk0p7 of="$STOCK_BACKUP_DEST" bs=65536 >> "$LOG" 2>&1
        echo "[OK] Backed up live mmcblk0p7 -> $STOCK_BACKUP_DEST" >> "$LOG"
    fi
fi

# 3. Install 12s procedural Neon Wave bootanimation.zip -> /data/local/bootanimation.zip
SRC_BOOTANIM=""
if [ -n "$USB_DIR" ] && [ -f "$USB_DIR/gen5w-boot-splash/bootanimation.zip" ]; then
    SRC_BOOTANIM="$USB_DIR/gen5w-boot-splash/bootanimation.zip"
elif [ -n "$PKG_ROOT" ] && [ -f "$PKG_ROOT/files/bootanimation.zip" ]; then
    SRC_BOOTANIM="$PKG_ROOT/files/bootanimation.zip"
fi

if [ -n "$SRC_BOOTANIM" ]; then
    mkdir -p /data/local 2>/dev/null
    chmod 751 /data/local 2>/dev/null
    cp "$SRC_BOOTANIM" /data/local/bootanimation.zip.tmp 2>>"$LOG"
    if [ -s /data/local/bootanimation.zip.tmp ]; then
        mv /data/local/bootanimation.zip.tmp /data/local/bootanimation.zip
        chown 0.0 /data/local/bootanimation.zip 2>/dev/null || chown root:root /data/local/bootanimation.zip 2>/dev/null
        chmod 644 /data/local/bootanimation.zip
        echo "[OK] Installed /data/local/bootanimation.zip" >> "$LOG"
    fi
fi

# 4. Flash Frame-0 procedural Neon Wave splash_custom.img -> mmcblk0p7 & mmcblk0p20
SRC_SPLASH=""
if [ -n "$USB_DIR" ] && [ -f "$USB_DIR/gen5w-boot-splash/splash_custom.img" ]; then
    SRC_SPLASH="$USB_DIR/gen5w-boot-splash/splash_custom.img"
elif [ -n "$PKG_ROOT" ] && [ -f "$PKG_ROOT/files/splash_custom.img" ]; then
    SRC_SPLASH="$PKG_ROOT/files/splash_custom.img"
fi

if [ -n "$SRC_SPLASH" ] && [ -b "/dev/block/mmcblk0p7" ] && [ -b "/dev/block/mmcblk0p20" ]; then
    IMG_SIZE=""
    ls -l "$SRC_SPLASH" | while read perms owner group size rest; do
        echo "$size" > "$BASE/.sz_tmp"
    done
    if [ -f "$BASE/.sz_tmp" ]; then
        read -r IMG_SIZE < "$BASE/.sz_tmp"
        rm -f "$BASE/.sz_tmp"
    fi
    echo "Splash candidate size: $IMG_SIZE" >> "$LOG"
    if [ "$IMG_SIZE" = "16777216" ]; then
        dd if="$SRC_SPLASH" of=/dev/block/mmcblk0p7 bs=65536 >> "$LOG" 2>&1
        dd if="$SRC_SPLASH" of=/dev/block/mmcblk0p20 bs=65536 >> "$LOG" 2>&1
        sync
        cmp "$SRC_SPLASH" /dev/block/mmcblk0p7 >> "$LOG" 2>&1
        C7=$?
        cmp "$SRC_SPLASH" /dev/block/mmcblk0p20 >> "$LOG" 2>&1
        C20=$?
        echo "Splash flash cmp: p7=$C7 p20=$C20" >> "$LOG"
    else
        echo "WARN: Unexpected splash size '$IMG_SIZE', skipped dd" >> "$LOG"
    fi
fi

# 5. Immediately activate Disney 100th WelcomeGoodbye.odex in RAM so Goodbye works on this very drive!
TARGET_ODEX="/system/app/WelcomeGoodbye.odex"
if [ -s "$BASE/WelcomeGoodbye_disney.odex" ] && [ -f "$TARGET_ODEX" ]; then
    if ! grep -q "$TARGET_ODEX" /proc/mounts 2>/dev/null; then
        mount -o bind "$BASE/WelcomeGoodbye_disney.odex" "$TARGET_ODEX" >> "$LOG" 2>&1
        chmod 644 "$TARGET_ODEX" 2>/dev/null
    fi
    if grep -q "$TARGET_ODEX" /proc/mounts 2>/dev/null; then
        echo "[OK] Bound Disney WelcomeGoodbye.odex over $TARGET_ODEX" >> "$LOG"
        am force-stop com.mobis.welcome >/dev/null 2>&1
        am startservice -n com.mobis.welcome/.WelcomeService >/dev/null 2>&1
        echo "[OK] Restarted com.mobis.welcome/.WelcomeService with Disney 100th" >> "$LOG"
    fi
fi

rm -f "$BASE/restored_flag" 2>/dev/null
sync

if [ -n "$USB_DIR" ] && [ -w "$USB_DIR/gen5w-boot-splash" ]; then
    cp "$LOG" "$USB_DIR/gen5w-boot-splash/install_log.txt" 2>/dev/null
    sync
fi

exit 0
"""

PREUN_SH = """#!/system/bin/sh
export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_customizer"
TARGET_ODEX="/system/app/WelcomeGoodbye.odex"
LOG="$BASE/uninstall.log"
echo "=== gen5w.boot.customizer preuninstall $(date 2>/dev/null) ===" > "$LOG"

# 1. Stop WelcomeService & unmount RAM bind-mount over WelcomeGoodbye.odex
am force-stop com.mobis.welcome >/dev/null 2>&1
if grep -q "$TARGET_ODEX" /proc/mounts 2>/dev/null; then
    umount "$TARGET_ODEX" >> "$LOG" 2>&1
fi
am startservice -n com.mobis.welcome/.WelcomeService >/dev/null 2>&1

# 2. Remove custom /data/local/bootanimation.zip
rm -f /data/local/bootanimation.zip 2>/dev/null

# 3. Restore factory stock splash partition from backup
STOCK_IMG=""
if [ -s "$BASE/splash_stock_backup.img" ]; then
    STOCK_IMG="$BASE/splash_stock_backup.img"
else
    for d in /mnt/media_rw/usb0 /mnt/media_rw/usb1 /storage/usb0 /storage/usb1; do
        if [ -s "$d/gen5w-boot-splash/splash_stock_backup.img" ]; then
            STOCK_IMG="$d/gen5w-boot-splash/splash_stock_backup.img"
            break
        fi
    done
fi

if [ -n "$STOCK_IMG" ] && [ -b "/dev/block/mmcblk0p7" ] && [ -b "/dev/block/mmcblk0p20" ]; then
    IMG_SIZE=""
    ls -l "$STOCK_IMG" | while read perms owner group size rest; do
        echo "$size" > "$BASE/.un_sz"
    done
    if [ -f "$BASE/.un_sz" ]; then
        read -r IMG_SIZE < "$BASE/.un_sz"
        rm -f "$BASE/.un_sz"
    fi
    if [ "$IMG_SIZE" = "16777216" ]; then
        dd if="$STOCK_IMG" of=/dev/block/mmcblk0p7 bs=65536 >> "$LOG" 2>&1
        dd if="$STOCK_IMG" of=/dev/block/mmcblk0p20 bs=65536 >> "$LOG" 2>&1
        sync
    fi
fi

rm -rf "$BASE" 2>/dev/null
sync
exit 0
"""

BOOT_HELPER_SH = """#!/system/bin/sh
# Gen5W Boot & Welcome Customizer Root Service (NEMESIS service.0, uid=root, mode=restart)
# Strict Android 4.4.2 mksh + toolbox compatibility.

export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_customizer"
mkdir -p "$BASE" 2>/dev/null
chmod 755 "$BASE" 2>/dev/null

LOG="$BASE/boot_helper.log"
TARGET_ODEX="/system/app/WelcomeGoodbye.odex"
DISNEY_ODEX="$BASE/WelcomeGoodbye_disney.odex"
UNLOCK_BIN="$BASE/fb0_unlock"

log_msg() {
    echo "[$(date 2>/dev/null)] $1" >> "$LOG" 2>/dev/null
}

# Keep log small across boots: reset at start of boot if uptime < 60s
read -r up_sec rest < /proc/uptime 2>/dev/null
case "$up_sec" in
    [0-9].*|[1-5][0-9].*)
        echo "=== Boot Helper Start (uptime=${up_sec}s) ===" > "$LOG" 2>/dev/null
        ;;
esac

# Check if already restored via USB kill-switch
if [ -f "$BASE/restored_flag" ]; then
    while true; do
        sleep 30
    done
fi

# 1. Early in boot: unlock Telechips fb0 (`TCC_FB_UPDATE_LOCK = 0`) as soon as `bootanim` is running
#    so `/data/local/bootanimation.zip` is immediately visible on the 1920x720 panel!
if [ -f "/data/local/bootanimation.zip" ] && [ -x "$UNLOCK_BIN" ]; then
    ba_state="$(getprop init.svc.bootanim 2>/dev/null)"
    ba_exit="$(getprop service.bootanim.exit 2>/dev/null)"
    if [ "$ba_exit" != "1" ]; then
        tries=0
        while [ "$tries" -lt 8 ]; do
            ba_state="$(getprop init.svc.bootanim 2>/dev/null)"
            if [ "$ba_state" = "running" ]; then
                "$UNLOCK_BIN" >/dev/null 2>&1
                log_msg "Unlocked fb0 for /data/local/bootanimation.zip (try=$tries)"
                break
            fi
            if [ "$ba_state" = "stopped" ]; then
                break
            fi
            sleep 1
            tries=$((tries + 1))
        done
        # Call once more 1s later in case SurfaceFlinger posted first frame right after start
        ba_exit="$(getprop service.bootanim.exit 2>/dev/null)"
        if [ "$ba_exit" != "1" ]; then
            sleep 1
            ba_exit="$(getprop service.bootanim.exit 2>/dev/null)"
            if [ "$ba_exit" != "1" ]; then
                "$UNLOCK_BIN" >/dev/null 2>&1
            fi
        fi
    fi
fi

# 2. Bind-mount Disney 100th WelcomeGoodbye.odex in RAM (no /system disk modifications)
kill_welcome_if_running() {
    ps | while read user pid ppid vsize rss wchan pc state name extra; do
        case "$name" in
            *com.mobis.welcome*)
                kill -9 "$pid" 2>/dev/null
                ;;
        esac
    done
}

was_welcome_up=0
ps | while read user pid ppid vsize rss wchan pc state name extra; do
    case "$name" in
        *com.mobis.welcome*)
            echo "1" > "$BASE/.w_up"
            ;;
    esac
done
if [ -f "$BASE/.w_up" ]; then
    was_welcome_up=1
    rm -f "$BASE/.w_up"
fi

if [ -s "$DISNEY_ODEX" ] && [ -f "$TARGET_ODEX" ]; then
    if ! grep -q "$TARGET_ODEX" /proc/mounts 2>/dev/null; then
        mount -o bind "$DISNEY_ODEX" "$TARGET_ODEX" 2>>"$LOG"
        chmod 644 "$TARGET_ODEX" 2>/dev/null
        log_msg "Bound $DISNEY_ODEX -> $TARGET_ODEX (was_welcome_up=$was_welcome_up)"
        if [ "$was_welcome_up" = "1" ]; then
            kill_welcome_if_running
            # Restart cleanly once ActivityManager is ready
            boot_ok="$(getprop sys.boot_completed 2>/dev/null)"
            if [ "$boot_ok" = "1" ]; then
                am startservice -n com.mobis.welcome/.WelcomeService >/dev/null 2>&1
            fi
        fi
    fi
fi

# 3. Idle loop: watch for USB emergency kill-switch (`RESTORE_STOCK` or `RESTORE_STOCK.txt`)
check_usb_restore() {
    for d in /storage/usb0 /storage/usb1 /mnt/media_rw/usb0 /mnt/media_rw/usb1; do
        for rf in "$d/RESTORE_STOCK" "$d/RESTORE_STOCK.txt" "$d/gen5w-boot-splash/RESTORE_STOCK" "$d/gen5w-boot-splash/RESTORE_STOCK.txt"; do
            if [ -f "$rf" ]; then
                echo "$d"
                return 0
            fi
        done
    done
    return 1
}

while true; do
    usb_hit="$(check_usb_restore)"
    if [ -n "$usb_hit" ]; then
        log_msg "USB RESTORE_STOCK detected on $usb_hit! Reverting to factory stock..."
        am force-stop com.mobis.welcome >/dev/null 2>&1
        if grep -q "$TARGET_ODEX" /proc/mounts 2>/dev/null; then
            umount "$TARGET_ODEX" 2>>"$LOG"
        fi
        am startservice -n com.mobis.welcome/.WelcomeService >/dev/null 2>&1

        rm -f /data/local/bootanimation.zip 2>/dev/null

        STOCK_IMG=""
        if [ -s "$BASE/splash_stock_backup.img" ]; then
            STOCK_IMG="$BASE/splash_stock_backup.img"
        elif [ -s "$usb_hit/gen5w-boot-splash/splash_stock_backup.img" ]; then
            STOCK_IMG="$usb_hit/gen5w-boot-splash/splash_stock_backup.img"
        fi

        if [ -n "$STOCK_IMG" ] && [ -b "/dev/block/mmcblk0p7" ] && [ -b "/dev/block/mmcblk0p20" ]; then
            dd if="$STOCK_IMG" of=/dev/block/mmcblk0p7 bs=65536 >> "$LOG" 2>&1
            dd if="$STOCK_IMG" of=/dev/block/mmcblk0p20 bs=65536 >> "$LOG" 2>&1
            sync
            log_msg "Restored stock splash from $STOCK_IMG"
        fi

        echo "1" > "$BASE/restored_flag"
        if [ -w "$usb_hit" ]; then
            echo "Restored factory splash, removed bootanimation.zip, and unmounted Disney WelcomeGoodbye at $(date 2>/dev/null)" > "$usb_hit/RESTORE_COMPLETED.txt" 2>/dev/null
            sync
        fi
    fi
    sleep 10
done
"""


def main() -> int:
    # Check required build artifacts exist
    splash_custom = BUILD_DIR / "splash_custom_neonwave_anim.img"
    splash_stock = ROOT / "oem-splash-dump" / "partitions" / "splash_p7.img"
    bootanim_zip = BUILD_DIR / "bootanimation.zip"
    disney_odex = BUILD_DIR / "WelcomeGoodbye_disney.odex"
    preview_jpg = BUILD_DIR / "preview_splash_frame0_1920x720.jpg"
    preview_mp4 = BUILD_DIR / "preview_neonwave_anim.mp4"

    for req in (splash_custom, splash_stock, bootanim_zip, disney_odex, preview_jpg):
        if not req.is_file():
            raise FileNotFoundError(f"Missing required build artifact: {req}")

    if splash_custom.stat().st_size != 16_777_216 or splash_stock.stat().st_size != 16_777_216:
        raise ValueError("Splash images must be exactly 16,777,216 bytes!")

    fb0_unlock_bytes = build_fb0_unlock_elf()
    fb0_unlock_path = BUILD_DIR / "fb0_unlock"
    fb0_unlock_path.write_bytes(fb0_unlock_bytes)

    # Save clean copies of package source files in workspace
    (PKG_SRC_DIR / "scripts").mkdir(parents=True, exist_ok=True)
    (PKG_SRC_DIR / "svc").mkdir(parents=True, exist_ok=True)
    (PKG_SRC_DIR / "files").mkdir(parents=True, exist_ok=True)

    nem_meta_bytes = validate_nemesis_meta("NEMESIS.data", NEMESIS_PKG_DATA)
    pre_bytes = validate_sh_text("pre.sh", PRE_SH)
    post_bytes = validate_sh_text("post.sh", POST_SH)
    preun_bytes = validate_sh_text("preun.sh", PREUN_SH)
    helper_bytes = validate_sh_text("boot-helper.sh", BOOT_HELPER_SH)

    (PKG_SRC_DIR / "NEMESIS.data").write_bytes(nem_meta_bytes)
    (PKG_SRC_DIR / "scripts" / "pre.sh").write_bytes(pre_bytes)
    (PKG_SRC_DIR / "scripts" / "post.sh").write_bytes(post_bytes)
    (PKG_SRC_DIR / "scripts" / "preun.sh").write_bytes(preun_bytes)
    (PKG_SRC_DIR / "svc" / "boot-helper.sh").write_bytes(helper_bytes)
    (PKG_SRC_DIR / "files" / "WelcomeGoodbye_disney.odex").write_bytes(disney_odex.read_bytes())
    (PKG_SRC_DIR / "files" / "fb0_unlock").write_bytes(fb0_unlock_bytes)

    # Assemble lightweight NEMESIS package zip (~52 KB! Large splash & bootanimation stream directly from USB!)
    DIST_DIR.mkdir(parents=True, exist_ok=True)
    pkg_zip_name = f"{PKG_ID}-{PKG_VER}.zip"
    pkg_zip_path = DIST_DIR / pkg_zip_name

    with zipfile.ZipFile(pkg_zip_path, "w") as zf:
        write_zip_entry(zf, "NEMESIS.data", nem_meta_bytes, executable=False)
        write_zip_entry(zf, "scripts/pre.sh", pre_bytes, executable=True)
        write_zip_entry(zf, "scripts/post.sh", post_bytes, executable=True)
        write_zip_entry(zf, "scripts/preun.sh", preun_bytes, executable=True)
        write_zip_entry(zf, "svc/boot-helper.sh", helper_bytes, executable=True)
        write_zip_entry(zf, "files/WelcomeGoodbye_disney.odex", disney_odex.read_bytes(), executable=False)
        write_zip_entry(zf, "files/fb0_unlock", fb0_unlock_bytes, executable=True)

    # Verify package zip integrity, Unix modes, and zero CR bytes
    with zipfile.ZipFile(pkg_zip_path, "r") as zf:
        assert zf.testzip() is None
        for sc in ("scripts/pre.sh", "scripts/post.sh", "scripts/preun.sh", "svc/boot-helper.sh", "files/fb0_unlock"):
            mode = (zf.getinfo(sc).external_attr >> 16) & 0o777
            assert mode == 0o755, f"{sc} mode={oct(mode)}"
        for txt in ("NEMESIS.data", "scripts/pre.sh", "scripts/post.sh", "scripts/preun.sh", "svc/boot-helper.sh"):
            assert b"\r" not in zf.read(txt), f"CR found in {txt}"

    usb_nemesis_data = f"[nemesis]\nversion=1.0\ntype=install\npath={pkg_zip_name}\n".encode("utf-8")

    # Save complete local mirror in dist/usb-root/
    usb_mirror = DIST_DIR / "usb-root"
    if usb_mirror.exists():
        shutil.rmtree(usb_mirror)
    (usb_mirror / "gen5w-boot-splash").mkdir(parents=True, exist_ok=True)
    (usb_mirror / "NEMESIS.data").write_bytes(usb_nemesis_data)
    shutil.copy2(pkg_zip_path, usb_mirror / pkg_zip_name)
    shutil.copy2(splash_custom, usb_mirror / "gen5w-boot-splash" / "splash_custom.img")
    shutil.copy2(splash_stock, usb_mirror / "gen5w-boot-splash" / "splash_stock_backup.img")
    shutil.copy2(bootanim_zip, usb_mirror / "gen5w-boot-splash" / "bootanimation.zip")
    shutil.copy2(disney_odex, usb_mirror / "gen5w-boot-splash" / "WelcomeGoodbye_disney.odex")
    shutil.copy2(fb0_unlock_path, usb_mirror / "gen5w-boot-splash" / "fb0_unlock")
    shutil.copy2(preview_jpg, usb_mirror / "gen5w-boot-splash" / "preview_1920x720.jpg")
    if preview_mp4.is_file():
        shutil.copy2(preview_mp4, usb_mirror / "gen5w-boot-splash" / "preview_neonwave_anim.mp4")

    print(f"[OK] Built NEMESIS service package: {pkg_zip_path} ({pkg_zip_path.stat().st_size:,} bytes)")
    print(f"[OK] Staged local USB mirror in   : {usb_mirror}")

    # Stage onto H:\ USB drive cleanly without touching unrelated folders!
    h_drive = Path("H:/")
    if h_drive.exists():
        # Remove old root NEMESIS.sh from previous static-splash execute test so only type=install runs
        old_nem_sh = h_drive / "NEMESIS.sh"
        if old_nem_sh.is_file():
            old_nem_sh.unlink()
            print("[OK] Removed previous H:/NEMESIS.sh (switched H:/NEMESIS.data to type=install)")

        # Remove any stale RESTORE_STOCK trigger if present
        for rf in ("RESTORE_STOCK", "RESTORE_STOCK.txt", "RESTORE_COMPLETED.txt"):
            p = h_drive / rf
            if p.is_file():
                p.unlink()

        (h_drive / "NEMESIS.data").write_bytes(usb_nemesis_data)
        shutil.copy2(pkg_zip_path, h_drive / pkg_zip_name)

        h_payload = h_drive / "gen5w-boot-splash"
        h_payload.mkdir(parents=True, exist_ok=True)
        shutil.copy2(splash_custom, h_payload / "splash_custom.img")
        if not (h_payload / "splash_stock_backup.img").is_file():
            shutil.copy2(splash_stock, h_payload / "splash_stock_backup.img")
        shutil.copy2(bootanim_zip, h_payload / "bootanimation.zip")
        shutil.copy2(disney_odex, h_payload / "WelcomeGoodbye_disney.odex")
        shutil.copy2(fb0_unlock_path, h_payload / "fb0_unlock")
        shutil.copy2(preview_jpg, h_payload / "preview_1920x720.jpg")
        if preview_mp4.is_file():
            shutil.copy2(preview_mp4, h_payload / "preview_neonwave_anim.mp4")

        print("[OK] Staged complete installer onto H:\\ USB drive!")
    else:
        print("WARN: H:\\ drive not detected!")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
