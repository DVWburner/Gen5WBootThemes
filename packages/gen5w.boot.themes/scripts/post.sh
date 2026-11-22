#!/system/bin/sh
export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_themes"
mkdir -p "$BASE" "$BASE/themes" "$BASE/active" "$BASE/cache" 2>/dev/null
chmod 777 "$BASE" "$BASE/themes" "$BASE/active" "$BASE/cache" 2>/dev/null

LOG="$BASE/install.log"
echo "=== gen5w.boot.themes postinstall $(date 2>/dev/null) ===" > "$LOG"
chmod 666 "$LOG" 2>/dev/null

# 1. Preserve pristine factory stock splash backup!
#    Check in order: existing $BASE/splash_stock.img -> legacy /data/nemesis/boot_customizer -> USB backup -> live mmcblk0p7
STOCK_DEST="$BASE/splash_stock.img"
if [ ! -s "$STOCK_DEST" ]; then
    if [ -s "/data/nemesis/boot_customizer/splash_stock_backup.img" ]; then
        cp "/data/nemesis/boot_customizer/splash_stock_backup.img" "$STOCK_DEST" 2>>"$LOG"
        echo "[OK] Migrated stock splash backup from boot_customizer" >> "$LOG"
    fi
fi

if [ ! -s "$STOCK_DEST" ]; then
    for d in /mnt/media_rw/usb0 /mnt/media_rw/usb1 /storage/usb0 /storage/usb1 /mnt/usb_storage /storage/usbdisk; do
        if [ -s "$d/gen5w-boot-splash/splash_stock_backup.img" ]; then
            cp "$d/gen5w-boot-splash/splash_stock_backup.img" "$STOCK_DEST" 2>>"$LOG"
            echo "[OK] Copied stock splash backup from $d/gen5w-boot-splash" >> "$LOG"
            break
        elif [ -s "$d/BootThemes/splash_stock_backup.img" ]; then
            cp "$d/BootThemes/splash_stock_backup.img" "$STOCK_DEST" 2>>"$LOG"
            echo "[OK] Copied stock splash backup from $d/BootThemes" >> "$LOG"
            break
        fi
    done
fi

if [ ! -s "$STOCK_DEST" ] && [ -b "/dev/block/mmcblk0p7" ]; then
    dd if=/dev/block/mmcblk0p7 of="$STOCK_DEST" bs=65536 >> "$LOG" 2>&1
    echo "[OK] Captured live mmcblk0p7 -> $STOCK_DEST" >> "$LOG"
fi
chmod 644 "$STOCK_DEST" 2>/dev/null

# 2. Save pristine factory WelcomeGoodbye.apk & .odex backups (unmounting any bind first)
if ! grep -q "/system/app/WelcomeGoodbye.apk" /proc/mounts 2>/dev/null; then
    if [ ! -s "$BASE/WelcomeGoodbye_stock.apk" ] && [ -f "/system/app/WelcomeGoodbye.apk" ]; then
        cp /system/app/WelcomeGoodbye.apk "$BASE/WelcomeGoodbye_stock.apk" 2>>"$LOG"
        chmod 644 "$BASE/WelcomeGoodbye_stock.apk" 2>/dev/null
    fi
fi
if ! grep -q "/system/app/WelcomeGoodbye.odex" /proc/mounts 2>/dev/null; then
    if [ ! -s "$BASE/WelcomeGoodbye_stock.odex" ] && [ -f "/system/app/WelcomeGoodbye.odex" ]; then
        cp /system/app/WelcomeGoodbye.odex "$BASE/WelcomeGoodbye_stock.odex" 2>>"$LOG"
        chmod 644 "$BASE/WelcomeGoodbye_stock.odex" 2>/dev/null
    fi
fi

# 3. Auto-import any starter `.zip` themes from USB `BootThemes/` folder into `$BASE/import_queue/`
mkdir -p "$BASE/import_queue" 2>/dev/null
chmod 777 "$BASE/import_queue" 2>/dev/null
for d in /mnt/media_rw/usb0 /mnt/media_rw/usb1 /storage/usb0 /storage/usb1; do
    if [ -d "$d/BootThemes" ]; then
        for z in "$d/BootThemes"/*.zip; do
            if [ -f "$z" ]; then
                cp "$z" "$BASE/import_queue/" 2>>"$LOG"
            fi
        done
        chmod 666 "$BASE/import_queue"/*.zip 2>/dev/null
        break
    fi
done

sync
exit 0
