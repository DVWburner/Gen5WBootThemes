#!/usr/bin/env python3
"""
Compile, v1-sign, validate, and package `gen5w.boot.themes` (Boot Themes APK + NEMESIS service).
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PKG_ID = "gen5w.boot.themes"
PKG_VER = "1.0.0"
PKG_DIR = ROOT / "packages" / PKG_ID
APP_SRC = PKG_DIR / "app_src"
BUILD_APK_DIR = ROOT / "build" / "bootthemes_apk"
OUT_APK = PKG_DIR / "app" / "BootThemes.apk"
DIST_DIR = ROOT / "dist"

SDK = Path(os.environ.get("ANDROID_HOME") or os.path.join(os.environ["LOCALAPPDATA"], "Android", "Sdk"))
BT = SDK / "build-tools" / "36.0.0"
ANDROID_JAR = SDK / "platforms" / "android-36" / "android.jar"

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


def run(cmd: list[str]) -> None:
    print("+", " ".join(cmd))
    subprocess.run(cmd, check=True)


def validate_sh_file(path: Path) -> bytes:
    text = path.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\r", "\n")
    raw = text.encode("utf-8")
    if raw.startswith(b"\xef\xbb\xbf") or b"\r" in raw:
        raise ValueError(f"{path}: BOM/CR forbidden")
    if not text.startswith("#!/system/bin/sh\n"):
        raise ValueError(f"{path}: missing #!/system/bin/sh")
    for lineno, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if not s or s.startswith("#"):
            continue
        no_quotes = re.sub(r'"[^"]*"|\'[^\']*\'', "", s)
        for cmd in FORBIDDEN_SH_COMMANDS:
            if cmd == "[[":
                if "[[" in no_quotes:
                    raise ValueError(f"{path}:{lineno}: forbidden `[[`: {s}")
            elif re.search(rf"(?<![\w.-]){re.escape(cmd)}(?![\w.-])", no_quotes):
                raise ValueError(f"{path}:{lineno}: forbidden command `{cmd}`: {s}")
        if "umount -l" in no_quotes:
            raise ValueError(f"{path}:{lineno}: `umount -l` unsupported")
    path.write_bytes(raw)
    return raw


def validate_nemesis_data(path: Path) -> bytes:
    text = path.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\r", "\n")
    raw = text.encode("utf-8")
    for line in text.splitlines():
        if line.startswith("short-desc="):
            v = line.split("=", 1)[1].strip().strip('"')
            if len(v) > 64:
                raise ValueError(f"short-desc too long ({len(v)} > 64): {v}")
        elif line.startswith("detailed-desc="):
            v = line.split("=", 1)[1].strip().strip('"')
            if len(v) > 256:
                raise ValueError(f"detailed-desc too long ({len(v)} > 256): {v}")
    path.write_bytes(raw)
    return raw


def build_apk() -> Path:
    if not ANDROID_JAR.is_file():
        raise SystemExit(f"Missing android.jar at {ANDROID_JAR}")

    if BUILD_APK_DIR.exists():
        shutil.rmtree(BUILD_APK_DIR)
    (BUILD_APK_DIR / "gen").mkdir(parents=True)
    (BUILD_APK_DIR / "classes").mkdir(parents=True)
    (BUILD_APK_DIR / "dex").mkdir(parents=True)
    OUT_APK.parent.mkdir(parents=True, exist_ok=True)

    res_zip = BUILD_APK_DIR / "res.zip"
    base_apk = BUILD_APK_DIR / "base.apk"
    unsigned_apk = BUILD_APK_DIR / "unsigned.apk"
    aligned_apk = BUILD_APK_DIR / "aligned.apk"

    run([str(BT / "aapt2.exe"), "compile", "--dir", str(APP_SRC / "res"), "-o", str(res_zip)])
    run([
        str(BT / "aapt2.exe"), "link",
        "-I", str(ANDROID_JAR),
        "--manifest", str(APP_SRC / "AndroidManifest.xml"),
        "--min-sdk-version", "19",
        "--target-sdk-version", "19",
        "--java", str(BUILD_APK_DIR / "gen"),
        "-o", str(base_apk),
        str(res_zip),
    ])

    java_files = [str(p) for p in (APP_SRC / "java").rglob("*.java")]
    java_files += [str(p) for p in (BUILD_APK_DIR / "gen").rglob("*.java")]
    run([
        "javac", "--release", "8", "-Xlint:-options", "-nowarn",
        "-cp", str(ANDROID_JAR),
        "-d", str(BUILD_APK_DIR / "classes"),
        *java_files,
    ])

    classes_jar = BUILD_APK_DIR / "classes_in.jar"
    run(["jar", "cf", str(classes_jar), "-C", str(BUILD_APK_DIR / "classes"), "."])
    run([
        str(BT / "d8.bat"), "--release", "--min-api", "19",
        "--lib", str(ANDROID_JAR),
        "--output", str(BUILD_APK_DIR / "dex"),
        str(classes_jar),
    ])

    shutil.copy2(base_apk, unsigned_apk)
    with zipfile.ZipFile(unsigned_apk, "a", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.write(BUILD_APK_DIR / "dex" / "classes.dex", "classes.dex")

    run([str(BT / "zipalign.exe"), "-f", "4", str(unsigned_apk), str(aligned_apk)])

    keystore = ROOT / "build" / "bootthemes_release.p12"
    if not keystore.is_file():
        run([
            "keytool", "-genkeypair",
            "-keystore", str(keystore),
            "-storetype", "pkcs12",
            "-storepass", "gen5wboot",
            "-keypass", "gen5wboot",
            "-alias", "bootthemes",
            "-keyalg", "RSA",
            "-keysize", "2048",
            "-validity", "10000",
            "-dname", "CN=Gen5W Boot Themes",
        ])

    run([
        str(BT / "apksigner.bat"), "sign",
        "--ks", str(keystore),
        "--ks-pass", "pass:gen5wboot",
        "--key-pass", "pass:gen5wboot",
        "--v1-signing-enabled", "true",
        "--v2-signing-enabled", "true",
        "--min-sdk-version", "19",
        "--out", str(OUT_APK),
        str(aligned_apk),
    ])
    print(f"[OK] Built signed KitKat APK: {OUT_APK} ({OUT_APK.stat().st_size:,} bytes)")
    return OUT_APK


def write_zip_entry(zf: zipfile.ZipFile, arcname: str, data: bytes, *, executable: bool = False) -> None:
    info = zipfile.ZipInfo(arcname, date_time=ZIP_FIXED_TIME)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3
    info.external_attr = (0o100755 if executable else 0o100644) << 16
    zf.writestr(info, data)


def main() -> int:
    nem_bytes = validate_nemesis_data(PKG_DIR / "NEMESIS.data")
    pre_bytes = validate_sh_file(PKG_DIR / "scripts" / "pre.sh")
    post_bytes = validate_sh_file(PKG_DIR / "scripts" / "post.sh")
    preun_bytes = validate_sh_file(PKG_DIR / "scripts" / "preun.sh")
    svc_bytes = validate_sh_file(PKG_DIR / "svc" / "bootthemes-helper.sh")

    apk_path = build_apk()
    apk_bytes = apk_path.read_bytes()

    DIST_DIR.mkdir(parents=True, exist_ok=True)
    pkg_zip_name = f"{PKG_ID}-{PKG_VER}.zip"
    pkg_zip_path = DIST_DIR / pkg_zip_name

    with zipfile.ZipFile(pkg_zip_path, "w") as zf:
        write_zip_entry(zf, "NEMESIS.data", nem_bytes, executable=False)
        write_zip_entry(zf, "app/BootThemes.apk", apk_bytes, executable=False)
        write_zip_entry(zf, "scripts/pre.sh", pre_bytes, executable=True)
        write_zip_entry(zf, "scripts/post.sh", post_bytes, executable=True)
        write_zip_entry(zf, "scripts/preun.sh", preun_bytes, executable=True)
        write_zip_entry(zf, "svc/bootthemes-helper.sh", svc_bytes, executable=True)

    with zipfile.ZipFile(pkg_zip_path, "r") as zf:
        assert zf.testzip() is None
        for sc in ("scripts/pre.sh", "scripts/post.sh", "scripts/preun.sh", "svc/bootthemes-helper.sh"):
            assert ((zf.getinfo(sc).external_attr >> 16) & 0o777) == 0o755

    # Also build standalone USB installer zip for embedding inside the .NET 10 BootThemeStudio app!
    usb_nem = f"[nemesis]\nversion=1.0\ntype=install\npath={pkg_zip_name}\n".encode("utf-8")
    usb_zip_path = DIST_DIR / f"{PKG_ID}-{PKG_VER}-USB.zip"
    with zipfile.ZipFile(usb_zip_path, "w") as uzf:
        write_zip_entry(uzf, "NEMESIS.data", usb_nem, executable=False)
        write_zip_entry(uzf, pkg_zip_name, pkg_zip_path.read_bytes(), executable=False)

    print(f"[OK] Built NEMESIS package : {pkg_zip_path} ({pkg_zip_path.stat().st_size:,} bytes)")
    print(f"[OK] Built USB bundle zip  : {usb_zip_path} ({usb_zip_path.stat().st_size:,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
