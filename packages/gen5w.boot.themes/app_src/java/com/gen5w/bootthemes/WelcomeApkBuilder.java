package com.gen5w.bootthemes;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.FilterOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.Map;
import java.util.zip.Adler32;
import java.util.zip.CRC32;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipOutputStream;

/**
 * Builds a RAM-bindable `WelcomeGoodbye_themed.apk` (and optional `WelcomeGoodbye_themed.odex`)
 * from the pristine factory `/data/nemesis/boot_themes/WelcomeGoodbye_stock.apk` without
 * ever modifying `/system`.
 *
 * Key architecture:
 *  - Stock `WelcomeGoodbye.apk` uses `ZIP_STORED` (4-byte aligned) raw MP4 resources so Android's
 *    `AssetManager::openNonAssetFd` + `MediaPlayer::setDataSource(fd, offset, length)` plays them
 *    directly via hardware H.264 decoder!
 *  - If a theme provides ONLY `goodbye_1920x720.mp4`, we replace the Hyundai/Kia/Genesis raw MP4
 *    slots in `WelcomeGoodbye_themed.apk` with `goodbye_1920x720.mp4` (`ZIP_STORED`, 4-byte aligned),
 *    so BOTH Hello and Goodbye play `goodbye_1920x720.mp4` with ZERO `.odex` changes needed!
 *  - If a theme ALSO provides a separate `welcome_1920x720.mp4` (`hasWelcomeVideo == true`), we put
 *    `welcome_1920x720.mp4` into `res/raw/welcome_disney_100_1920.mp4` (`2130903040`) and
 *    `goodbye_1920x720.mp4` into `res/raw/welcome_hyundai_jong_e_1920.mp4` (`2130903044`) + patch
 *    `WelcomeService.G` (`2130903040`) vs `WelcomeService.H` (`2130903044`) in `WelcomeGoodbye_themed.odex`!
 */
public final class WelcomeApkBuilder {
    private WelcomeApkBuilder() {}

    public static void buildThemedWelcomeFiles(
            File stockApk,
            File stockOdex,
            File goodbyeMp4,
            File optionalWelcomeMp4,
            File outThemedApk,
            File outThemedOdex) throws IOException {
        if (!stockApk.isFile()) {
            throw new IOException("Factory WelcomeGoodbye.apk backup not found at " + stockApk.getAbsolutePath());
        }
        if (goodbyeMp4 == null || !goodbyeMp4.isFile()) {
            throw new IOException("Goodbye MP4 file missing.");
        }

        boolean hasSeparateWelcome = (optionalWelcomeMp4 != null && optionalWelcomeMp4.isFile());

        // Map of APK entry names -> source MP4 File to store uncompressed (4-byte aligned)
        Map<String, File> replacements = new HashMap<String, File>();
        if (hasSeparateWelcome) {
            // Slot 2130903040 = welcome_disney_100_1920.mp4 -> Welcome (G)
            // Slot 2130903044 = welcome_hyundai_jong_e_1920.mp4 -> Goodbye (H)
            replacements.put("res/raw/welcome_disney_100_1920.mp4", optionalWelcomeMp4);
            replacements.put("res/raw/welcome_hyundai_jong_e_1920.mp4", goodbyeMp4);
            replacements.put("res/raw/welcome_hyundai_new_jong_e_1920.mp4", goodbyeMp4);
        } else {
            // Replace all active 1920/brand MP4 slots with goodbyeMp4 so it works across all Gen5W variants
            // without even needing an .odex patch!
            String[] slots = new String[] {
                "res/raw/welcome_disney_100_1920.mp4",
                "res/raw/welcome_hyundai_jong_e_1920.mp4",
                "res/raw/welcome_hyundai_new_jong_e_1920.mp4",
                "res/raw/welcome_hyundai_1920.mp4",
                "res/raw/welcome_hyundai_new_1920.mp4",
                "res/raw/welcome_kia_jong_e_1920.mp4",
                "res/raw/welcome_kia_new_1920.mp4",
                "res/raw/welcome_genesis.mp4",
                "res/raw/welcome_n_mode_1920.mp4"
            };
            for (String s : slots) {
                replacements.put(s, goodbyeMp4);
            }
        }

        // Precompute CRC32 + exact length of custom MP4s without loading 35 MB into heap!
        Map<File, long[]> fileCrcAndSize = new HashMap<File, long[]>();
        fileCrcAndSize.put(goodbyeMp4, computeCrcAndLength(goodbyeMp4));
        if (hasSeparateWelcome) {
            fileCrcAndSize.put(optionalWelcomeMp4, computeCrcAndLength(optionalWelcomeMp4));
        }

        File parent = outThemedApk.getParentFile();
        if (parent != null && !parent.exists()) parent.mkdirs();
        File tmpApk = new File(outThemedApk.getAbsolutePath() + ".tmp");

        ZipFile origZip = new ZipFile(stockApk);
        FileOutputStream fos = new FileOutputStream(tmpApk);
        BufferedOutputStream bos = new BufferedOutputStream(fos, 65536);
        CountingOutputStream cos = new CountingOutputStream(bos);
        ZipOutputStream zos = new ZipOutputStream(cos);
        byte[] buf = new byte[65536];

        try {
            Enumeration<? extends ZipEntry> entries = origZip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry origEntry = entries.nextElement();
                String name = origEntry.getName();

                // Strip unused extra 14 MB stock videos when replacing so our themed APK stays tiny & fast!
                if (!replacements.containsKey(name)
                        && name.startsWith("res/raw/welcome_")
                        && name.endsWith(".mp4")
                        && origEntry.getSize() > 1024) {
                    byte[] dummy = new byte[] { 0, 0, 0, 0 };
                    CRC32 c = new CRC32();
                    c.update(dummy);
                    writeStoredBytesAligned(zos, cos, name, dummy, c.getValue());
                    continue;
                }

                if (replacements.containsKey(name)) {
                    File repFile = replacements.get(name);
                    long[] cs = fileCrcAndSize.get(repFile);
                    writeStoredFileAligned(zos, cos, name, repFile, cs[0], cs[1], buf);
                } else if (origEntry.getMethod() == ZipEntry.STORED || "resources.arsc".equals(name)) {
                    long size = origEntry.getSize();
                    long crc = origEntry.getCrc();
                    if (size < 0 || crc < 0) {
                        byte[] raw = JpuSplashBuilder.readFileBytes(stockApk); // fallback never hit on standard APK
                        throw new IOException("Unexpected unknown size on STORED entry: " + name);
                    }
                    ZipEntry outEntry = new ZipEntry(name);
                    outEntry.setMethod(ZipEntry.STORED);
                    outEntry.setSize(size);
                    outEntry.setCompressedSize(size);
                    outEntry.setCrc(crc);
                    alignStoredHeader(outEntry, cos.getCount(), name);
                    zos.putNextEntry(outEntry);
                    InputStream in = origZip.getInputStream(origEntry);
                    try {
                        int r;
                        while ((r = in.read(buf)) != -1) {
                            zos.write(buf, 0, r);
                        }
                    } finally {
                        in.close();
                    }
                    zos.closeEntry();
                } else {
                    ZipEntry outEntry = new ZipEntry(name);
                    outEntry.setMethod(ZipEntry.DEFLATED);
                    outEntry.setExtra(null);
                    zos.putNextEntry(outEntry);
                    InputStream in = origZip.getInputStream(origEntry);
                    try {
                        int r;
                        while ((r = in.read(buf)) != -1) {
                            zos.write(buf, 0, r);
                        }
                    } finally {
                        in.close();
                    }
                    zos.closeEntry();
                }
            }
        } finally {
            zos.close();
            origZip.close();
        }

        if (outThemedApk.exists()) outThemedApk.delete();
        if (!tmpApk.renameTo(outThemedApk)) {
            throw new IOException("Failed saving themed WelcomeGoodbye.apk");
        }
        outThemedApk.setReadable(true, false);

        // Handle optional separate Welcome vs Goodbye ODEX routing
        if (outThemedOdex.exists()) outThemedOdex.delete();
        if (hasSeparateWelcome && stockOdex != null && stockOdex.isFile()) {
            patchOdexSeparateWelcomeGoodbye(stockOdex, outThemedOdex);
        }
    }

    private static void patchOdexSeparateWelcomeGoodbye(File stockOdex, File outOdex) throws IOException {
        byte[] odex = JpuSplashBuilder.readFileBytes(stockOdex);
        if (odex.length < 80 || odex[0] != 'd' || odex[1] != 'e' || odex[2] != 'y' || odex[3] != '\n') {
            return;
        }
        int dexOff = readInt32LE(odex, 8);
        int dexLen = readInt32LE(odex, 12);
        if (dexOff < 40 || dexLen < 128 || dexOff + dexLen > odex.length) {
            return;
        }

        // Locate VariantCoding == 0x36 branch in WelcomeService.m():
        //   0A 00        move-result v0
        //   13 01 36 00  const/16 v1, 0x36
        //   33 10 12 00  if-ne v0, v1, +18
        int matchIdx = -1;
        for (int i = dexOff + 112; i < dexOff + dexLen - 32; i += 2) {
            if ((odex[i] & 0xFF) == 0x0A && (odex[i + 1] & 0xFF) == 0x00
                    && (odex[i + 2] & 0xFF) == 0x13 && (odex[i + 3] & 0xFF) == 0x01
                    && (odex[i + 4] & 0xFF) == 0x36 && (odex[i + 5] & 0xFF) == 0x00
                    && (odex[i + 6] & 0xFF) == 0x33 && (odex[i + 7] & 0xFF) == 0x10) {
                matchIdx = i;
                break;
            }
        }
        if (matchIdx < 0) return;

        // NOP out `if-ne v0, v1, +18` so it enters the Disney block:
        // Inside the Disney block:
        //   const-string v0, "android.resource://com.mobis.welcome/2130903040" (str idx 0x0133 = 307)
        //   sput-object v0, WelcomeService->G
        //   const-string v0, "android.resource://com.mobis.welcome/2130903040" (str idx 0x0133 = 307)
        //   sput-object v0, WelcomeService->H
        // Change the SECOND `const-string v0, 307` (`1A 00 33 01`) to `311` (`2130903044`, `1A 00 37 01`)!
        odex[matchIdx + 6] = 0;
        odex[matchIdx + 7] = 0;
        odex[matchIdx + 8] = 0;
        odex[matchIdx + 9] = 0;

        int constStrCount = 0;
        for (int j = matchIdx + 10; j < Math.min(dexOff + dexLen - 8, matchIdx + 48); j += 2) {
            if ((odex[j] & 0xFF) == 0x1A && (odex[j + 1] & 0xFF) == 0x00
                    && (odex[j + 2] & 0xFF) == 0x33 && (odex[j + 3] & 0xFF) == 0x01) {
                constStrCount++;
                if (constStrCount == 2) {
                    odex[j + 2] = 0x37; // 307 + 4 = 311 ("...2130903044")
                    break;
                }
            }
        }

        // Update DEX Adler32 checksum over dexOff+12 .. dexOff+dexLen
        Adler32 adler = new Adler32();
        adler.update(odex, dexOff + 12, dexLen - 12);
        writeInt32LE(odex, dexOff + 8, (int) adler.getValue());

        File tmp = new File(outOdex.getAbsolutePath() + ".tmp");
        FileOutputStream fos = new FileOutputStream(tmp);
        try {
            fos.write(odex);
            fos.flush();
        } finally {
            fos.close();
        }
        tmp.renameTo(outOdex);
        outOdex.setReadable(true, false);
    }

    private static long[] computeCrcAndLength(File f) throws IOException {
        CRC32 crc = new CRC32();
        long len = 0L;
        FileInputStream fis = new FileInputStream(f);
        byte[] buf = new byte[65536];
        int r;
        try {
            while ((r = fis.read(buf)) != -1) {
                crc.update(buf, 0, r);
                len += r;
            }
        } finally {
            fis.close();
        }
        return new long[] { crc.getValue(), len };
    }

    private static void alignStoredHeader(ZipEntry entry, long currentOffset, String name) throws IOException {
        int nameLen = name.getBytes("UTF-8").length;
        long headerEnd = currentOffset + 30L + nameLen;
        int pad = (int) ((4L - (headerEnd % 4L)) % 4L);
        if (pad > 0) {
            entry.setExtra(new byte[pad]);
        } else {
            entry.setExtra(null);
        }
    }

    private static void writeStoredBytesAligned(
            ZipOutputStream zos, CountingOutputStream cos, String name, byte[] data, long crc) throws IOException {
        ZipEntry e = new ZipEntry(name);
        e.setMethod(ZipEntry.STORED);
        e.setSize(data.length);
        e.setCompressedSize(data.length);
        e.setCrc(crc);
        alignStoredHeader(e, cos.getCount(), name);
        zos.putNextEntry(e);
        zos.write(data);
        zos.closeEntry();
    }

    private static void writeStoredFileAligned(
            ZipOutputStream zos, CountingOutputStream cos, String name, File f, long crc, long size, byte[] buf)
            throws IOException {
        ZipEntry e = new ZipEntry(name);
        e.setMethod(ZipEntry.STORED);
        e.setSize(size);
        e.setCompressedSize(size);
        e.setCrc(crc);
        alignStoredHeader(e, cos.getCount(), name);
        zos.putNextEntry(e);
        FileInputStream fis = new FileInputStream(f);
        try {
            int r;
            while ((r = fis.read(buf)) != -1) {
                zos.write(buf, 0, r);
            }
        } finally {
            fis.close();
        }
        zos.closeEntry();
    }

    private static int readInt32LE(byte[] b, int off) {
        return (b[off] & 0xFF)
                | ((b[off + 1] & 0xFF) << 8)
                | ((b[off + 2] & 0xFF) << 16)
                | ((b[off + 3] & 0xFF) << 24);
    }

    private static void writeInt32LE(byte[] b, int off, int val) {
        b[off] = (byte) (val & 0xFF);
        b[off + 1] = (byte) ((val >> 8) & 0xFF);
        b[off + 2] = (byte) ((val >> 16) & 0xFF);
        b[off + 3] = (byte) ((val >> 24) & 0xFF);
    }

    private static final class CountingOutputStream extends FilterOutputStream {
        private long count = 0L;

        CountingOutputStream(OutputStream out) {
            super(out);
        }

        long getCount() {
            return count;
        }

        @Override
        public void write(int b) throws IOException {
            out.write(b);
            count++;
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            out.write(b, off, len);
            count += len;
        }
    }
}
