package com.gen5w.bootthemes;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.List;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Safely unpacks, validates, lists, and deletes locally installed Boot Themes under
 * `/data/nemesis/boot_themes/themes/<id>/` conforming to the v1.1 `boottheme.json` contract.
 */
public final class LocalThemeRepository {
    public static final String BASE_PATH = "/data/nemesis/boot_themes";
    public static final long MAX_UNCOMPRESSED_TOTAL = 65L * 1024L * 1024L;
    public static final long MAX_SINGLE_ENTRY = 42L * 1024L * 1024L;
    public static final int MAX_FILES = 30;

    private final File themesRoot;
    private final File activeRoot;

    public LocalThemeRepository() {
        this.themesRoot = new File(BASE_PATH, "themes");
        this.activeRoot = new File(BASE_PATH, "active");
        if (!themesRoot.exists()) themesRoot.mkdirs();
        if (!activeRoot.exists()) activeRoot.mkdirs();
    }

    public File getBaseDir() {
        return new File(BASE_PATH);
    }

    public File getActiveDir() {
        return activeRoot;
    }

    public String getActiveBootThemeId() {
        return readSingleLine(new File(activeRoot, "boot_theme_id"));
    }

    public void setActiveBootThemeId(String id) {
        writeSingleLine(new File(activeRoot, "boot_theme_id"), id);
    }

    public String getActiveGoodbyeThemeId() {
        String enabled = readSingleLine(new File(activeRoot, "goodbye_enabled"));
        if (!"1".equals(enabled)) return "";
        return readSingleLine(new File(activeRoot, "goodbye_theme_id"));
    }

    public void setActiveGoodbyeTheme(String id, boolean enabled) {
        writeSingleLine(new File(activeRoot, "goodbye_theme_id"), enabled ? id : "");
        writeSingleLine(new File(activeRoot, "goodbye_enabled"), enabled ? "1" : "0");
    }

    public List<BootThemeItem> listInstalledThemes() {
        List<BootThemeItem> out = new ArrayList<BootThemeItem>();
        File[] dirs = themesRoot.listFiles();
        if (dirs == null) return out;
        for (File d : dirs) {
            if (!d.isDirectory()) continue;
            File manifest = new File(d, "boottheme.json");
            if (!manifest.isFile()) {
                manifest = new File(d, "bootanim.json"); // legacy fallback
            }
            if (!manifest.isFile()) continue;
            try {
                BootThemeItem item = loadFromDirectory(d, manifest);
                if (item != null) {
                    out.add(item);
                }
            } catch (Exception ignored) {
            }
        }
        Collections.sort(out, new Comparator<BootThemeItem>() {
            @Override
            public int compare(BootThemeItem a, BootThemeItem b) {
                return a.name.compareToIgnoreCase(b.name);
            }
        });
        return out;
    }

    private BootThemeItem loadFromDirectory(File dir, File manifestFile) throws Exception {
        String jsonText = new String(JpuSplashBuilder.readFileBytes(manifestFile), "UTF-8");
        JSONObject o = new JSONObject(jsonText);
        BootThemeItem it = new BootThemeItem();
        it.id = sanitizeSlug(o.optString("id", dir.getName()));
        it.name = o.optString("name", it.id).trim();
        it.author = o.optString("author", "Local").trim();
        it.version = o.optString("version", "1.0.0").trim();
        it.revision = o.optInt("revision", 1);
        it.category = o.optString("category", "Other").trim();
        it.description = o.optString("description", "").trim();
        it.powerOffDurationMs = o.optInt("powerOffDurationMs", 0);
        it.resolution = o.optString("resolution", "1920x720").trim();
        it.localDir = dir;

        it.localSplash1920 = findFirstExisting(new File(dir, "onboot"), new String[] {
            "splash_1920x720.jpg", "splash_1920x720.jpeg", "splash_1920x720.png"
        });
        it.localSplash1280 = findFirstExisting(new File(dir, "onboot"), new String[] {
            "splash_1280x720.jpg", "splash_1280x720.jpeg", "splash_1280x720.png"
        });
        it.localGoodbyeMp4 = findFirstExisting(new File(dir, "poweroff"), new String[] {
            "goodbye_1920x720.mp4"
        });
        it.localWelcomeMp4 = findFirstExisting(new File(dir, "poweroff"), new String[] {
            "welcome_1920x720.mp4"
        });

        it.localOnBootPreview = findFirstExisting(dir, new String[] {
            "preview_onboot.jpg", "preview_onboot.jpeg", "preview_onboot.png"
        });
        if (it.localOnBootPreview == null) {
            it.localOnBootPreview = it.localSplash1920;
        }
        it.localPowerOffPreview = findFirstExisting(dir, new String[] {
            "preview_poweroff.gif", "preview_poweroff.mp4"
        });
        it.localPowerOffPoster = findFirstExisting(dir, new String[] {
            "preview_poweroff_poster.jpg", "preview_poweroff_poster.jpeg", "preview_poweroff_poster.png"
        });

        it.hasOnBoot = (it.localSplash1920 != null && it.localSplash1920.isFile());
        it.hasPowerOff = (it.localGoodbyeMp4 != null && it.localGoodbyeMp4.isFile());
        it.hasWelcomeVideo = (it.localWelcomeMp4 != null && it.localWelcomeMp4.isFile());

        long totalBytes = 0L;
        totalBytes += sumDirBytes(dir);
        it.fileSizeBytes = totalBytes;

        if (!it.hasOnBoot && !it.hasPowerOff) {
            return null;
        }
        return it;
    }

    public BootThemeItem installFromZip(File zipFile) throws Exception {
        if (zipFile == null || !zipFile.isFile()) {
            throw new IOException("Package zip not found.");
        }
        // Check PK\x03\x04 magic
        FileInputStream magicIn = new FileInputStream(zipFile);
        byte[] magic = new byte[4];
        try {
            if (magicIn.read(magic) != 4
                    || magic[0] != 0x50 || magic[1] != 0x4B || magic[2] != 0x03 || magic[3] != 0x04) {
                throw new SecurityException("Invalid ZIP header signature.");
            }
        } finally {
            magicIn.close();
        }

        File stageDir = new File(themesRoot, ".stage_" + System.currentTimeMillis());
        deleteRecursive(stageDir);
        if (!stageDir.mkdirs()) {
            throw new IOException("Cannot create staging directory.");
        }

        ZipFile zf = new ZipFile(zipFile);
        long totalExtracted = 0L;
        int fileCount = 0;
        try {
            Enumeration<? extends ZipEntry> en = zf.entries();
            byte[] buf = new byte[32768];
            while (en.hasMoreElements()) {
                ZipEntry ze = en.nextElement();
                String rawName = ze.getName().replace('\\', '/');
                if (rawName.startsWith("__MACOSX/")
                        || rawName.endsWith(".DS_Store")
                        || rawName.endsWith("Thumbs.db")
                        || rawName.endsWith("desktop.ini")) {
                    continue;
                }
                if (rawName.startsWith("/") || rawName.contains("..") || rawName.contains(":")) {
                    throw new SecurityException("Blocked unsafe zip path: " + rawName);
                }
                if (ze.isDirectory()) {
                    continue;
                }
                if (!isAllowedEntryPath(rawName)) {
                    throw new SecurityException("Disallowed file in Boot Theme package: " + rawName);
                }
                fileCount++;
                if (fileCount > MAX_FILES) {
                    throw new SecurityException("Archive exceeds max file count (" + MAX_FILES + ").");
                }

                File outFile = new File(stageDir, rawName);
                String canonStage = stageDir.getCanonicalPath() + File.separator;
                String canonOut = outFile.getCanonicalPath();
                if (!canonOut.startsWith(canonStage)) {
                    throw new SecurityException("Zip slip traversal blocked: " + rawName);
                }
                File p = outFile.getParentFile();
                if (p != null && !p.exists()) p.mkdirs();

                InputStream in = zf.getInputStream(ze);
                FileOutputStream fos = new FileOutputStream(outFile);
                long entryBytes = 0L;
                try {
                    int r;
                    while ((r = in.read(buf)) != -1) {
                        entryBytes += r;
                        totalExtracted += r;
                        if (entryBytes > MAX_SINGLE_ENTRY || totalExtracted > MAX_UNCOMPRESSED_TOTAL) {
                            throw new SecurityException("Boot Theme exceeds maximum allowed size.");
                        }
                        fos.write(buf, 0, r);
                    }
                    fos.flush();
                } finally {
                    fos.close();
                    in.close();
                }
                outFile.setReadable(true, false);
            }

            File manifest = new File(stageDir, "boottheme.json");
            if (!manifest.isFile()) {
                manifest = new File(stageDir, "bootanim.json");
            }
            if (!manifest.isFile()) {
                throw new IllegalArgumentException("Missing boottheme.json manifest in package.");
            }

            String jsonText = new String(JpuSplashBuilder.readFileBytes(manifest), "UTF-8");
            JSONObject o = new JSONObject(jsonText);
            String slug = sanitizeSlug(o.optString("id", ""));
            if (slug.isEmpty()) {
                throw new IllegalArgumentException("Invalid or missing theme ID in boottheme.json.");
            }

            BootThemeItem verify = loadFromDirectory(stageDir, manifest);
            if (verify == null) {
                throw new IllegalArgumentException("Package contains neither a valid Boot Image nor a Goodbye Video.");
            }

            File finalDir = new File(themesRoot, slug);
            deleteRecursive(finalDir);
            if (!stageDir.renameTo(finalDir)) {
                throw new IOException("Failed moving staged theme into themes directory.");
            }
            setWorldReadableRecursive(finalDir);

            File finalManifest = new File(finalDir, "boottheme.json");
            if (!finalManifest.isFile()) finalManifest = new File(finalDir, "bootanim.json");
            return loadFromDirectory(finalDir, finalManifest);
        } catch (Exception ex) {
            deleteRecursive(stageDir);
            throw ex;
        } finally {
            zf.close();
        }
    }

    public boolean deleteTheme(String id) {
        String slug = sanitizeSlug(id);
        if (slug.isEmpty()) return false;
        File dir = new File(themesRoot, slug);
        if (!dir.exists()) return false;
        deleteRecursive(dir);
        return !dir.exists();
    }

    private static boolean isAllowedEntryPath(String path) {
        String lower = path.toLowerCase(Locale.US);
        if ("boottheme.json".equals(lower) || "bootanim.json".equals(lower)) return true;
        if (lower.equals("preview_onboot.jpg") || lower.equals("preview_onboot.jpeg") || lower.equals("preview_onboot.png")) return true;
        if (lower.equals("preview_poweroff.gif") || lower.equals("preview_poweroff.mp4")) return true;
        if (lower.equals("preview_poweroff_poster.jpg") || lower.equals("preview_poweroff_poster.jpeg") || lower.equals("preview_poweroff_poster.png")) return true;
        if (lower.equals("onboot/splash_1920x720.jpg") || lower.equals("onboot/splash_1920x720.jpeg") || lower.equals("onboot/splash_1920x720.png")) return true;
        if (lower.equals("onboot/splash_1280x720.jpg") || lower.equals("onboot/splash_1280x720.jpeg") || lower.equals("onboot/splash_1280x720.png")) return true;
        if (lower.equals("poweroff/goodbye_1920x720.mp4") || lower.equals("poweroff/welcome_1920x720.mp4")) return true;
        return false;
    }

    public static String sanitizeSlug(String raw) {
        if (raw == null) return "";
        String s = raw.trim().toLowerCase(Locale.US).replaceAll("[^a-z0-9_-]", "-");
        while (s.startsWith("-")) s = s.substring(1);
        while (s.endsWith("-")) s = s.substring(0, s.length() - 1);
        if (s.length() > 64) s = s.substring(0, 64);
        return s;
    }

    private static File findFirstExisting(File dir, String[] names) {
        if (dir == null || !dir.isDirectory()) return null;
        for (String n : names) {
            File f = new File(dir, n);
            if (f.isFile() && f.length() > 0) return f;
        }
        return null;
    }

    private static long sumDirBytes(File f) {
        if (f == null || !f.exists()) return 0L;
        if (f.isFile()) return f.length();
        long sum = 0L;
        File[] children = f.listFiles();
        if (children != null) {
            for (File c : children) sum += sumDirBytes(c);
        }
        return sum;
    }

    public static void deleteRecursive(File f) {
        if (f == null || !f.exists()) return;
        if (f.isDirectory()) {
            File[] kids = f.listFiles();
            if (kids != null) {
                for (File k : kids) deleteRecursive(k);
            }
        }
        f.delete();
    }

    private static void setWorldReadableRecursive(File f) {
        if (f == null || !f.exists()) return;
        f.setReadable(true, false);
        if (f.isDirectory()) {
            f.setExecutable(true, false);
            File[] kids = f.listFiles();
            if (kids != null) {
                for (File k : kids) setWorldReadableRecursive(k);
            }
        }
    }

    private static String readSingleLine(File f) {
        if (f == null || !f.isFile()) return "";
        try {
            String raw = new String(JpuSplashBuilder.readFileBytes(f), "UTF-8").trim();
            int nl = raw.indexOf('\n');
            return (nl >= 0 ? raw.substring(0, nl) : raw).trim();
        } catch (Exception e) {
            return "";
        }
    }

    private static void writeSingleLine(File f, String val) {
        try {
            File p = f.getParentFile();
            if (p != null && !p.exists()) p.mkdirs();
            FileOutputStream fos = new FileOutputStream(f);
            try {
                fos.write(((val == null ? "" : val.trim()) + "\n").getBytes("UTF-8"));
                fos.flush();
            } finally {
                fos.close();
            }
            f.setReadable(true, false);
            f.setWritable(true, false);
        } catch (Exception ignored) {
        }
    }
}
