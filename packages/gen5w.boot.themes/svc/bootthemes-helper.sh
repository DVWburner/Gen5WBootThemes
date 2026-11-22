#!/system/bin/sh
# Boot Themes Root Service & Direct Command Helper (NEMESIS service.0, uid=root, mode=restart)
# Strictly Android 4.4.2 KitKat mksh + toolbox compatible (NO sed/awk/tr/head/tail/wc/cut/find/xargs/printf/[[).

export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_themes"
ACTIVE_DIR="$BASE/active"
LOG="$BASE/helper.log"

TARGET_APK="/system/app/WelcomeGoodbye.apk"
TARGET_ODEX="/system/app/WelcomeGoodbye.odex"

ACTIVE_APK="$ACTIVE_DIR/WelcomeGoodbye_themed.apk"
ACTIVE_ODEX="$ACTIVE_DIR/WelcomeGoodbye_themed.odex"
ACTIVE_VIDEO_ENABLED="$ACTIVE_DIR/goodbye_enabled"
CANDIDATE_SPLASH="$ACTIVE_DIR/splash_candidate.img"
STOCK_SPLASH="$BASE/splash_stock.img"

mkdir -p "$BASE" "$BASE/themes" "$ACTIVE_DIR" "$BASE/cache" "$BASE/import_queue" 2>/dev/null
chmod 777 "$BASE" "$BASE/themes" "$ACTIVE_DIR" "$BASE/cache" "$BASE/import_queue" 2>/dev/null

log_msg() {
    echo "[$(date 2>/dev/null)] $1" >> "$LOG" 2>/dev/null
    chmod 666 "$LOG" 2>/dev/null
}

is_mounted() {
    grep -q "$1" /proc/mounts 2>/dev/null
    return $?
}

kill_welcome() {
    am force-stop com.mobis.welcome >/dev/null 2>&1
    ps | while read user pid ppid vsize rss wchan pc state name extra; do
        case "$name" in
            *com.mobis.welcome*)
                kill -9 "$pid" 2>/dev/null
                ;;
        esac
    done
}

restart_welcome_if_booted() {
    boot_ok="$(getprop sys.boot_completed 2>/dev/null)"
    if [ "$boot_ok" = "1" ]; then
        am startservice -n com.mobis.welcome/.WelcomeService >/dev/null 2>&1
    fi
}

unmount_welcome() {
    if is_mounted "$TARGET_ODEX" || is_mounted "$TARGET_APK"; then
        kill_welcome
        if is_mounted "$TARGET_ODEX"; then
            umount "$TARGET_ODEX" 2>>"$LOG"
        fi
        if is_mounted "$TARGET_APK"; then
            umount "$TARGET_APK" 2>>"$LOG"
        fi
        log_msg "Unmounted custom WelcomeGoodbye overlay"
        restart_welcome_if_booted
    fi
}

mount_welcome_active() {
    if [ ! -f "$ACTIVE_VIDEO_ENABLED" ]; then
        unmount_welcome
        return 0
    fi
    val=""
    read -r val < "$ACTIVE_VIDEO_ENABLED" 2>/dev/null
    if [ "$val" != "1" ]; then
        unmount_welcome
        return 0
    fi

    # Check if WelcomeService already started before our bind-mount
    was_up=0
    ps | while read user pid ppid vsize rss wchan pc state name extra; do
        case "$name" in
            *com.mobis.welcome*)
                echo "1" > "$BASE/.w_up"
                ;;
        esac
    done
    if [ -f "$BASE/.w_up" ]; then
        was_up=1
        rm -f "$BASE/.w_up"
    fi

    changed=0
    if [ -s "$ACTIVE_APK" ] && [ -f "$TARGET_APK" ]; then
        chown 0.0 "$ACTIVE_APK" 2>/dev/null || chown root:root "$ACTIVE_APK" 2>/dev/null
        chmod 644 "$ACTIVE_APK" 2>/dev/null
        if ! is_mounted "$TARGET_APK"; then
            if [ "$was_up" = "1" ]; then
                kill_welcome
            fi
            mount -o bind "$ACTIVE_APK" "$TARGET_APK" 2>>"$LOG"
            chmod 644 "$TARGET_APK" 2>/dev/null
            changed=1
            log_msg "Bound $ACTIVE_APK -> $TARGET_APK"
        fi
    fi

    if [ -s "$ACTIVE_ODEX" ] && [ -f "$TARGET_ODEX" ]; then
        chown 0.0 "$ACTIVE_ODEX" 2>/dev/null || chown root:root "$ACTIVE_ODEX" 2>/dev/null
        chmod 644 "$ACTIVE_ODEX" 2>/dev/null
        if ! is_mounted "$TARGET_ODEX"; then
            if [ "$was_up" = "1" ] && [ "$changed" = "0" ]; then
                kill_welcome
            fi
            mount -o bind "$ACTIVE_ODEX" "$TARGET_ODEX" 2>>"$LOG"
            chmod 644 "$TARGET_ODEX" 2>/dev/null
            changed=1
            log_msg "Bound $ACTIVE_ODEX -> $TARGET_ODEX"
        fi
    fi

    if [ "$changed" = "1" ]; then
        restart_welcome_if_booted
    fi
    return 0
}

flash_splash_file() {
    src="$1"
    label="$2"
    if [ ! -r "$src" ]; then
        log_msg "ERROR: Splash image not found: $src"
        echo "ERROR: Splash image not found: $src"
        return 1
    fi
    if [ ! -b "/dev/block/mmcblk0p7" ] || [ ! -b "/dev/block/mmcblk0p20" ]; then
        log_msg "ERROR: mmcblk0p7 or mmcblk0p20 missing"
        echo "ERROR: Splash partitions not found"
        return 1
    fi

    # Ensure stock backup exists before flashing anything
    if [ ! -s "$STOCK_SPLASH" ]; then
        if [ -s "/oem_data/boot_themes_splash_stock.img" ]; then
            cp "/oem_data/boot_themes_splash_stock.img" "$STOCK_SPLASH" 2>/dev/null
        else
            dd if=/dev/block/mmcblk0p7 of="$STOCK_SPLASH" bs=65536 >> "$LOG" 2>&1
        fi
        chmod 644 "$STOCK_SPLASH" 2>/dev/null
    fi

    sz=""
    ls -l "$src" | while read perms owner group size rest; do
        echo "$size" > "$BASE/.sp_sz"
    done
    if [ -f "$BASE/.sp_sz" ]; then
        read -r sz < "$BASE/.sp_sz"
        rm -f "$BASE/.sp_sz"
    fi
    if [ "$sz" != "16777216" ]; then
        log_msg "ERROR: Invalid splash size '$sz' (expected 16777216)"
        echo "ERROR: Invalid splash size '$sz'"
        return 1
    fi

    dd if="$src" of=/dev/block/mmcblk0p7 bs=65536 >> "$LOG" 2>&1
    rc7=$?
    dd if="$src" of=/dev/block/mmcblk0p20 bs=65536 >> "$LOG" 2>&1
    rc20=$?
    sync

    cmp "$src" /dev/block/mmcblk0p7 >> "$LOG" 2>&1
    c7=$?
    cmp "$src" /dev/block/mmcblk0p20 >> "$LOG" 2>&1
    c20=$?

    if [ "$rc7" = "0" ] && [ "$rc20" = "0" ] && [ "$c7" = "0" ] && [ "$c20" = "0" ]; then
        log_msg "[OK] Flashed & cmp-verified $label to mmcblk0p7 & mmcblk0p20"
        echo "OK"
        return 0
    fi
    log_msg "ERROR: Flash/verify mismatch (dd=$rc7/$rc20 cmp=$c7/$c20)"
    echo "ERROR: Flash verification failed"
    return 1
}

ensure_stock_captures() {
    if [ ! -s "$STOCK_SPLASH" ]; then
        if [ -s "/oem_data/boot_themes_splash_stock.img" ]; then
            cp "/oem_data/boot_themes_splash_stock.img" "$STOCK_SPLASH" 2>/dev/null
        elif [ -b "/dev/block/mmcblk0p7" ]; then
            dd if=/dev/block/mmcblk0p7 of="$STOCK_SPLASH" bs=65536 >> "$LOG" 2>&1
        fi
        chmod 644 "$STOCK_SPLASH" 2>/dev/null
    fi
    if [ ! -s "$BASE/WelcomeGoodbye_stock.apk" ] && ! is_mounted "$TARGET_APK" && [ -f "$TARGET_APK" ]; then
        cp "$TARGET_APK" "$BASE/WelcomeGoodbye_stock.apk" 2>/dev/null
        chmod 644 "$BASE/WelcomeGoodbye_stock.apk" 2>/dev/null
    fi
    if [ ! -s "$BASE/WelcomeGoodbye_stock.odex" ] && ! is_mounted "$TARGET_ODEX" && [ -f "$TARGET_ODEX" ]; then
        cp "$TARGET_ODEX" "$BASE/WelcomeGoodbye_stock.odex" 2>/dev/null
        chmod 644 "$BASE/WelcomeGoodbye_stock.odex" 2>/dev/null
    fi
}

# Handle direct CLI invocations from the Boot Themes app via `/system/xbin/nemesis root -c "sh ... <CMD>"`
CMD="$1"
case "$CMD" in
    ENSURE_BACKUPS)
        ensure_stock_captures
        echo "OK"
        exit 0
        ;;
    FLASH_CANDIDATE_SPLASH)
        flash_splash_file "$CANDIDATE_SPLASH" "Custom Boot Image"
        exit $?
        ;;
    RESTORE_STOCK_SPLASH)
        flash_splash_file "$STOCK_SPLASH" "Factory Stock Boot Image"
        exit $?
        ;;
    APPLY_GOODBYE)
        unmount_welcome
        mount_welcome_active
        echo "OK"
        exit 0
        ;;
    RESTORE_STOCK_GOODBYE)
        echo "0" > "$ACTIVE_VIDEO_ENABLED"
        chmod 666 "$ACTIVE_VIDEO_ENABLED" 2>/dev/null
        unmount_welcome
        rm -f "$ACTIVE_APK" "$ACTIVE_ODEX" 2>/dev/null
        echo "OK"
        exit 0
        ;;
    RESTORE_ALL_STOCK)
        echo "0" > "$ACTIVE_VIDEO_ENABLED"
        chmod 666 "$ACTIVE_VIDEO_ENABLED" 2>/dev/null
        unmount_welcome
        rm -f "$ACTIVE_APK" "$ACTIVE_ODEX" "$ACTIVE_DIR/boot_theme_id" "$ACTIVE_DIR/goodbye_theme_id" 2>/dev/null
        flash_splash_file "$STOCK_SPLASH" "Factory Stock Boot Image"
        exit $?
        ;;
    SCAN_USB)
        # Copy any `.zip` files found in USB `/BootThemes/*.zip` into `$BASE/import_queue/` where the app can unpack them
        count=0
        for d in /mnt/media_rw/usb0 /mnt/media_rw/usb1 /storage/usb0 /storage/usb1 /mnt/usb_storage /storage/usbdisk; do
            if [ -d "$d/BootThemes" ]; then
                for z in "$d/BootThemes"/*.zip; do
                    if [ -f "$z" ]; then
                        cp "$z" "$BASE/import_queue/" 2>/dev/null && count=$((count + 1))
                    fi
                done
                chmod 666 "$BASE/import_queue"/*.zip 2>/dev/null
                break
            fi
        done
        echo "FOUND:$count"
        exit 0
        ;;
esac

# Daemon mode (`mode=restart` at boot):
read -r up_sec rest < /proc/uptime 2>/dev/null
case "$up_sec" in
    [0-9].*|[1-5][0-9].*)
        echo "=== Boot Themes Helper Boot Start (uptime=${up_sec}s) ===" > "$LOG" 2>/dev/null
        chmod 666 "$LOG" 2>/dev/null
        ;;
esac

ensure_stock_captures

# Clean up any legacy bootanimation.zip & keep legacy boot_customizer helper idle
if [ -f "/data/local/bootanimation.zip" ]; then
    rm -f /data/local/bootanimation.zip 2>/dev/null
fi
if [ -d "/data/nemesis/boot_customizer" ]; then
    echo "1" > /data/nemesis/boot_customizer/restored_flag 2>/dev/null
fi

# Mount active Goodbye/Welcome video in RAM early in boot
mount_welcome_active

# Watch for USB emergency kill-switch (`RESTORE_STOCK.txt` or `RESTORE_BOOT_THEMES.txt`)
check_usb_killswitch() {
    for d in /storage/usb0 /storage/usb1 /mnt/media_rw/usb0 /mnt/media_rw/usb1; do
        for rf in "$d/RESTORE_STOCK" "$d/RESTORE_STOCK.txt" "$d/RESTORE_BOOT_THEMES.txt" "$d/BootThemes/RESTORE_STOCK.txt"; do
            if [ -f "$rf" ]; then
                echo "$d"
                return 0
            fi
        done
    done
    return 1
}

while true; do
    # Re-verify mount in case anything unmounted it, and check USB kill-switch
    usb_hit="$(check_usb_killswitch)"
    if [ -n "$usb_hit" ]; then
        log_msg "USB RESTORE_STOCK kill-switch detected on $usb_hit! Restoring stock..."
        echo "0" > "$ACTIVE_VIDEO_ENABLED"
        chmod 666 "$ACTIVE_VIDEO_ENABLED" 2>/dev/null
        unmount_welcome
        rm -f "$ACTIVE_APK" "$ACTIVE_ODEX" "$ACTIVE_DIR/boot_theme_id" "$ACTIVE_DIR/goodbye_theme_id" 2>/dev/null
        if [ -s "$STOCK_SPLASH" ]; then
            flash_splash_file "$STOCK_SPLASH" "Factory Stock Boot Image (USB Kill-Switch)" >/dev/null 2>&1
        fi
        if [ -w "$usb_hit" ]; then
            echo "Restored stock Boot Image & Goodbye Video at $(date 2>/dev/null)" > "$usb_hit/BOOT_THEMES_RESTORED.txt" 2>/dev/null
            sync
        fi
        while [ -n "$(check_usb_killswitch)" ]; do
            sleep 10
        done
    else
        mount_welcome_active
    fi
    sleep 10
done
