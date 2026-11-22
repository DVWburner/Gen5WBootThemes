#!/system/bin/sh
export PATH=/data/nemesis/bin:/sbin:/vendor/bin:/system/sbin:/system/bin:/system/xbin
export LD_LIBRARY_PATH=/vendor/lib:/system/lib

BASE="/data/nemesis/boot_themes"
mkdir -p "$BASE" 2>/dev/null
mkdir -p "$BASE/themes" 2>/dev/null
mkdir -p "$BASE/active" 2>/dev/null
mkdir -p "$BASE/cache" 2>/dev/null
chmod 777 "$BASE" "$BASE/themes" "$BASE/active" "$BASE/cache" 2>/dev/null

# Clean up legacy one-off bootanimation.zip or old boot_customizer mount if present
rm -f /data/local/bootanimation.zip 2>/dev/null
mkdir -p /data/nemesis/boot_customizer 2>/dev/null
echo "1" > /data/nemesis/boot_customizer/restored_flag 2>/dev/null
chmod 666 /data/nemesis/boot_customizer/restored_flag 2>/dev/null

if grep -q "/system/app/WelcomeGoodbye.odex" /proc/mounts 2>/dev/null || grep -q "/system/app/WelcomeGoodbye.apk" /proc/mounts 2>/dev/null; then
    am force-stop com.mobis.welcome >/dev/null 2>&1
    umount /system/app/WelcomeGoodbye.odex 2>/dev/null
    umount /system/app/WelcomeGoodbye.apk 2>/dev/null
fi

exit 0
