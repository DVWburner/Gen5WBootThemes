#!/system/bin/sh
export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_themes"
LOG="$BASE/uninstall.log"
echo "=== gen5w.boot.themes preuninstall $(date 2>/dev/null) ===" > "$LOG"

# Skip full wipe if NEMESIS is staging an in-place upgrade of gen5w.boot.themes
for st in /data/nemesis/stage/gen5w.boot.themes* /data/nemesis/tmp/*gen5w.boot.themes*; do
    if [ -e "$st" ]; then
        echo "Upgrade detected ($st), preserving installed themes and active state." >> "$LOG"
        exit 0
    fi
done

# 1. Stop WelcomeService & unmount any RAM bind-mounts
am force-stop com.mobis.welcome >/dev/null 2>&1
if grep -q "/system/app/WelcomeGoodbye.odex" /proc/mounts 2>/dev/null; then
    umount /system/app/WelcomeGoodbye.odex 2>>"$LOG"
fi
if grep -q "/system/app/WelcomeGoodbye.apk" /proc/mounts 2>/dev/null; then
    umount /system/app/WelcomeGoodbye.apk 2>>"$LOG"
fi
am startservice -n com.mobis.welcome/.WelcomeService >/dev/null 2>&1

# 2. Remove any leftover bootanimation.zip
rm -f /data/local/bootanimation.zip 2>/dev/null

# 3. Restore factory stock splash partition
STOCK_IMG="$BASE/splash_stock.img"
if [ -s "$STOCK_IMG" ] && [ -b "/dev/block/mmcblk0p7" ] && [ -b "/dev/block/mmcblk0p20" ]; then
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

# Keep splash_stock.img safe in /oem_data just in case, clean up active dir
cp "$STOCK_IMG" /oem_data/boot_themes_splash_stock.img 2>/dev/null
rm -rf "$BASE" 2>/dev/null
sync
exit 0
