package com.gen5w.bootthemes;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;

/**
 * Pure Java Baseline DCT (SOF0) YCbCr 4:4:4 (1x1/1x1/1x1) JPEG encoder & Telechips
 * 16 MB `splash` partition packer, matching the exact hardware JPU constraints of
 * `TCC_JPU_C6_V0.1.0.08` in `mmcblk0boot0`.
 *
 * Note: Android's built-in `Bitmap.compress(JPEG)` outputs YCbCr 4:2:0 (2x2/1x1/1x1),
 * whereas factory `splash` JPEGs use Baseline YCbCr 4:4:4 (1x1/1x1/1x1). Implementing
 * our own clean Baseline 4:4:4 Huffman + FDCT encoder guarantees 100% JPU compatibility
 * whether a creator packaged a `.png`, a 4:2:0 `.jpg`, or an already-4:4:4 `.jpg`!
 */
public final class JpuSplashBuilder {
    public static final int PARTITION_SIZE = 16 * 1024 * 1024; // 16,777,216 bytes
    public static final int MAX_JPEG_BYTES = 450 * 1024;

    private JpuSplashBuilder() {}

    public static final class SplashEntry {
        public String name;
        public int width;
        public int height;
        public String format;
        public byte[] data;
    }

    public static boolean isValidJpu444Jpeg(byte[] jpeg, int expectedW, int expectedH) {
        if (jpeg == null || jpeg.length < 128 || jpeg.length > MAX_JPEG_BYTES) return false;
        if ((jpeg[0] & 0xFF) != 0xFF || (jpeg[1] & 0xFF) != 0xD8) return false;
        if ((jpeg[jpeg.length - 2] & 0xFF) != 0xFF || (jpeg[jpeg.length - 1] & 0xFF) != 0xD9) return false;

        int pos = 2;
        boolean foundSof0 = false;
        while (pos < jpeg.length - 1) {
            if ((jpeg[pos] & 0xFF) != 0xFF) {
                pos++;
                continue;
            }
            while (pos < jpeg.length && (jpeg[pos] & 0xFF) == 0xFF) pos++;
            if (pos >= jpeg.length) break;
            int m = jpeg[pos++] & 0xFF;
            if (m == 0xD9 || m == 0xDA) break;
            if ((m >= 0xD0 && m <= 0xD8) || m == 0x01) continue;
            if (pos + 2 > jpeg.length) return false;
            int segLen = ((jpeg[pos] & 0xFF) << 8) | (jpeg[pos + 1] & 0xFF);
            if (segLen < 2 || pos + segLen > jpeg.length) return false;
            if (m == 0xC2) return false; // Progressive SOF2 forbidden
            if (m == 0xC0) { // Baseline SOF0
                if (segLen < 17) return false;
                int prec = jpeg[pos + 2] & 0xFF;
                int h = ((jpeg[pos + 3] & 0xFF) << 8) | (jpeg[pos + 4] & 0xFF);
                int w = ((jpeg[pos + 5] & 0xFF) << 8) | (jpeg[pos + 6] & 0xFF);
                int ncomp = jpeg[pos + 7] & 0xFF;
                if (prec != 8 || ncomp != 3 || w != expectedW || h != expectedH) return false;
                for (int c = 0; c < 3; c++) {
                    int samp = jpeg[pos + 9 + c * 3] & 0xFF;
                    if (samp != 0x11) return false; // Must be 1x1 (4:4:4)
                }
                foundSof0 = true;
            }
            pos += segLen;
        }
        return foundSof0;
    }

    public static byte[] prepareJpuJpegFromFile(File imgFile, int targetW, int targetH) throws IOException {
        byte[] raw = readFileBytes(imgFile);
        if (isValidJpu444Jpeg(raw, targetW, targetH)) {
            return raw;
        }
        Bitmap src = BitmapFactory.decodeByteArray(raw, 0, raw.length);
        if (src == null) {
            throw new IOException("Cannot decode Boot Image: " + imgFile.getName());
        }
        Bitmap cropped = centerCropAndScale(src, targetW, targetH);
        if (cropped != src) {
            src.recycle();
        }
        try {
            int[] qualities = new int[] { 90, 85, 80, 74, 68 };
            for (int q : qualities) {
                byte[] encoded = BaselineJpeg444Encoder.encode(cropped, q);
                if (isValidJpu444Jpeg(encoded, targetW, targetH)) {
                    return encoded;
                }
            }
            throw new IOException("Encoded Boot Image exceeded safe 450 KB hardware JPU limit.");
        } finally {
            cropped.recycle();
        }
    }

    public static Bitmap centerCropAndScale(Bitmap src, int targetW, int targetH) {
        int sw = src.getWidth();
        int sh = src.getHeight();
        if (sw == targetW && sh == targetH) {
            return src;
        }
        float targetAspect = (float) targetW / (float) targetH;
        float srcAspect = (float) sw / (float) sh;
        int cropX = 0, cropY = 0, cropW = sw, cropH = sh;
        if (srcAspect > targetAspect) {
            cropW = Math.max(1, Math.round(sh * targetAspect));
            cropX = (sw - cropW) / 2;
        } else if (srcAspect < targetAspect) {
            cropH = Math.max(1, Math.round(sw / targetAspect));
            cropY = (sh - cropH) / 2;
        }
        Bitmap sub = Bitmap.createBitmap(src, cropX, cropY, cropW, cropH);
        if (cropW == targetW && cropH == targetH) {
            return sub;
        }
        Bitmap scaled = Bitmap.createScaledBitmap(sub, targetW, targetH, true);
        if (scaled != sub) {
            sub.recycle();
        }
        return scaled;
    }

    public static void buildCustomSplashPartition(
            File stockSplashFile,
            File splash1920File,
            File optionalSplash1280File,
            File outSplashFile) throws IOException {
        if (!stockSplashFile.isFile() || stockSplashFile.length() != PARTITION_SIZE) {
            throw new IOException("Factory splash backup missing or invalid size (" + stockSplashFile.getAbsolutePath() + ")");
        }
        byte[] stockRaw = readFileBytes(stockSplashFile);
        List<SplashEntry> entries = parseSplashPartition(stockRaw);

        byte[] jpg1920 = prepareJpuJpegFromFile(splash1920File, 1920, 720);
        byte[] jpg1280 = prepareJpuJpegFromFile(
                (optionalSplash1280File != null && optionalSplash1280File.isFile()) ? optionalSplash1280File : splash1920File,
                1280, 720);

        int replaced = 0;
        for (SplashEntry e : entries) {
            if ("hyundai_1920x720".equals(e.name)
                    || "newhyundai_1920x720".equals(e.name)
                    || "hyundaieco_1920x720".equals(e.name)
                    || "kia_1920x720".equals(e.name)
                    || "newkia_1920x720".equals(e.name)
                    || "genesis_1920x720".equals(e.name)) {
                // Only replace slots matching the car's brand family or Hyundai slots?
                // Wait: replacing all brand 1920/1280 boot slots (leaving error/parking untouched!)
                // means the same Boot Theme works on Hyundai Ioniq 5, Kia EV6, AND Genesis Gen5W!
                if (e.width == 1920 && e.height == 720) {
                    e.data = jpg1920;
                    replaced++;
                }
            } else if ("hyundai_1280x720".equals(e.name)
                    || "newhyundai_1280x720".equals(e.name)
                    || "kia_1280x720".equals(e.name)
                    || "newkia_1280x720".equals(e.name)
                    || "genesis_1280x720".equals(e.name)) {
                if (e.width == 1280 && e.height == 720) {
                    e.data = jpg1280;
                    replaced++;
                }
            }
        }
        if (replaced == 0) {
            throw new IOException("No matching splash slots found in partition table.");
        }

        byte[] packed = packSplashPartition(entries);
        File parent = outSplashFile.getParentFile();
        if (parent != null && !parent.exists()) parent.mkdirs();
        File tmp = new File(outSplashFile.getAbsolutePath() + ".tmp");
        FileOutputStream fos = new FileOutputStream(tmp);
        try {
            fos.write(packed);
            fos.flush();
        } finally {
            fos.close();
        }
        if (outSplashFile.exists()) outSplashFile.delete();
        if (!tmp.renameTo(outSplashFile)) {
            throw new IOException("Failed writing candidate splash image.");
        }
        outSplashFile.setReadable(true, false);
    }

    public static List<SplashEntry> parseSplashPartition(byte[] raw) throws IOException {
        if (raw.length != PARTITION_SIZE) {
            throw new IOException("Expected 16 MB splash image, got " + raw.length);
        }
        String magic = new String(raw, 0, 6, "US-ASCII");
        if (!"splash".equals(magic) || raw[6] != 0 || raw[7] != 0) {
            throw new IOException("Invalid splash partition header magic.");
        }
        int count = readInt32LE(raw, 8);
        if (count <= 0 || count > 32) {
            throw new IOException("Unexpected splash entry count: " + count);
        }
        List<SplashEntry> list = new ArrayList<SplashEntry>(count);
        for (int i = 0; i < count; i++) {
            int eoff = 16 + i * 52;
            SplashEntry ent = new SplashEntry();
            ent.name = readFixedAscii(raw, eoff, 20);
            int imgOff = readInt32LE(raw, eoff + 20);
            int imgSize = readInt32LE(raw, eoff + 24);
            ent.width = readInt32LE(raw, eoff + 28);
            ent.height = readInt32LE(raw, eoff + 32);
            ent.format = readFixedAscii(raw, eoff + 40, 12);
            if (imgOff < 0x200 || imgSize <= 0 || imgOff + imgSize > raw.length) {
                throw new IOException("Invalid splash entry bounds for " + ent.name);
            }
            ent.data = new byte[imgSize];
            System.arraycopy(raw, imgOff, ent.data, 0, imgSize);
            list.add(ent);
        }
        return list;
    }

    public static byte[] packSplashPartition(List<SplashEntry> entries) throws IOException {
        byte[] out = new byte[PARTITION_SIZE];
        byte[] magic = "splash".getBytes("US-ASCII");
        System.arraycopy(magic, 0, out, 0, 6);
        writeInt32LE(out, 8, entries.size());
        writeInt32LE(out, 12, 0);

        int curOffset = 0x800;
        for (int i = 0; i < entries.size(); i++) {
            SplashEntry ent = entries.get(i);
            int size = ent.data.length;
            int pad = (512 - (size % 512)) % 512;
            if (curOffset + size + pad > PARTITION_SIZE) {
                throw new IOException("Packed splash entries exceed 16 MB partition.");
            }
            int eoff = 16 + i * 52;
            byte[] nb = ent.name.getBytes("US-ASCII");
            System.arraycopy(nb, 0, out, eoff, Math.min(nb.length, 19));
            writeInt32LE(out, eoff + 20, curOffset);
            writeInt32LE(out, eoff + 24, size);
            writeInt32LE(out, eoff + 28, ent.width);
            writeInt32LE(out, eoff + 32, ent.height);
            writeInt32LE(out, eoff + 36, pad);
            byte[] fb = ent.format.getBytes("US-ASCII");
            System.arraycopy(fb, 0, out, eoff + 40, Math.min(fb.length, 11));

            System.arraycopy(ent.data, 0, out, curOffset, size);
            curOffset += size + pad;
        }
        return out;
    }

    private static String readFixedAscii(byte[] b, int off, int maxLen) throws IOException {
        int len = 0;
        while (len < maxLen && b[off + len] != 0) len++;
        return new String(b, off, len, "US-ASCII");
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

    public static byte[] readFileBytes(File f) throws IOException {
        FileInputStream fis = new FileInputStream(f);
        ByteArrayOutputStream bos = new ByteArrayOutputStream((int) Math.min(f.length(), 1048576L));
        byte[] buf = new byte[32768];
        int r;
        try {
            while ((r = fis.read(buf)) != -1) {
                bos.write(buf, 0, r);
            }
        } finally {
            fis.close();
        }
        return bos.toByteArray();
    }

    /**
     * Compact, dependency-free Baseline JFIF (SOF0, YCbCr 4:4:4 1x1/1x1/1x1) JPEG encoder.
     */
    static final class BaselineJpeg444Encoder {
        private static final int[] ZIGZAG = {
             0,  1,  5,  6, 14, 15, 27, 28,
             2,  4,  7, 13, 16, 26, 29, 42,
             3,  8, 12, 17, 25, 30, 41, 43,
             9, 11, 18, 24, 31, 40, 44, 53,
            10, 19, 23, 32, 39, 45, 52, 54,
            20, 22, 33, 38, 46, 51, 55, 60,
            21, 34, 37, 47, 50, 56, 59, 61,
            35, 36, 48, 49, 57, 58, 62, 63
        };

        private static final int[] STD_LUM_QT = {
            16, 11, 10, 16, 24, 40, 51, 61,
            12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56,
            14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68,109,103, 77,
            24, 35, 55, 64, 81,104,113, 92,
            49, 64, 78, 87,103,121,120,101,
            72, 92, 95, 98,112,100,103, 99
        };

        private static final int[] STD_CHROM_QT = {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99
        };

        private static final int[] BITS_DC_LUM = { 0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static final int[] VAL_DC_LUM  = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private static final int[] BITS_DC_CHROM = { 0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static final int[] VAL_DC_CHROM  = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private static final int[] BITS_AC_LUM = { 0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
        private static final int[] VAL_AC_LUM = {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
            0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
            0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16,
            0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
            0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
            0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
            0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
            0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
            0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
            0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4,
            0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
            0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
            0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa
        };

        private static final int[] BITS_AC_CHROM = { 0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        private static final int[] VAL_AC_CHROM = {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
            0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
            0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
            0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34,
            0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
            0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38,
            0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
            0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96,
            0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
            0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
            0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
            0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2,
            0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
            0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
            0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa
        };

        private static final float[] AAN_SCALE = {
            1.0f, 1.387039845f, 1.306562965f, 1.175875602f,
            1.0f, 0.785694958f, 0.541196100f, 0.275899379f
        };

        public static byte[] encode(Bitmap bmp, int quality) {
            int w = bmp.getWidth();
            int h = bmp.getHeight();
            int q = Math.max(1, Math.min(100, quality));
            int scale = (q < 50) ? (5000 / q) : (200 - q * 2);

            int[] lumQtZig = new int[64];
            int[] chrQtZig = new int[64];
            float[] fdtblY = new float[64];
            float[] fdtblC = new float[64];

            for (int i = 0; i < 64; i++) {
                int yv = (STD_LUM_QT[i] * scale + 50) / 100;
                yv = Math.max(1, Math.min(255, yv));
                int cv = (STD_CHROM_QT[i] * scale + 50) / 100;
                cv = Math.max(1, Math.min(255, cv));
                lumQtZig[ZIGZAG[i]] = yv;
                chrQtZig[ZIGZAG[i]] = cv;
            }
            int k = 0;
            for (int row = 0; row < 8; row++) {
                for (int col = 0; col < 8; col++) {
                    float denom = AAN_SCALE[row] * AAN_SCALE[col] * 8.0f;
                    fdtblY[k] = 1.0f / (lumQtZig[ZIGZAG[k]] * denom);
                    fdtblC[k] = 1.0f / (chrQtZig[ZIGZAG[k]] * denom);
                    k++;
                }
            }

            int[][] dcY = buildHuffTable(BITS_DC_LUM, VAL_DC_LUM);
            int[][] acY = buildHuffTable(BITS_AC_LUM, VAL_AC_LUM);
            int[][] dcC = buildHuffTable(BITS_DC_CHROM, VAL_DC_CHROM);
            int[][] acC = buildHuffTable(BITS_AC_CHROM, VAL_AC_CHROM);

            ByteArrayOutputStream out = new ByteArrayOutputStream(262144);
            // SOI
            out.write(0xFF); out.write(0xD8);
            // APP0 JFIF
            byte[] app0 = {
                (byte) 0xFF, (byte) 0xE0, 0x00, 0x10,
                'J', 'F', 'I', 'F', 0x00, 0x01, 0x01, 0x00,
                0x00, 0x01, 0x00, 0x01, 0x00, 0x00
            };
            out.write(app0, 0, app0.length);
            // DQT 0 & 1
            writeDqt(out, 0, lumQtZig);
            writeDqt(out, 1, chrQtZig);
            // SOF0 (Baseline DCT, 8-bit, 3 components, all 1x1 -> YCbCr 4:4:4!)
            byte[] sof0 = {
                (byte) 0xFF, (byte) 0xC0, 0x00, 0x11, 0x08,
                (byte) ((h >> 8) & 0xFF), (byte) (h & 0xFF),
                (byte) ((w >> 8) & 0xFF), (byte) (w & 0xFF),
                0x03,
                0x01, 0x11, 0x00,
                0x02, 0x11, 0x01,
                0x03, 0x11, 0x01
            };
            out.write(sof0, 0, sof0.length);
            // DHT (4 standard tables)
            writeDht(out, 0x00, BITS_DC_LUM, VAL_DC_LUM);
            writeDht(out, 0x10, BITS_AC_LUM, VAL_AC_LUM);
            writeDht(out, 0x01, BITS_DC_CHROM, VAL_DC_CHROM);
            writeDht(out, 0x11, BITS_AC_CHROM, VAL_AC_CHROM);
            // SOS
            byte[] sos = {
                (byte) 0xFF, (byte) 0xDA, 0x00, 0x0C, 0x03,
                0x01, 0x00,
                0x02, 0x11,
                0x03, 0x11,
                0x00, 0x3F, 0x00
            };
            out.write(sos, 0, sos.length);

            BitWriter bw = new BitWriter(out);
            int[] rowPixels = new int[w * 8];
            float[] blkY = new float[64];
            float[] blkCb = new float[64];
            float[] blkCr = new float[64];
            int[] qBlk = new int[64];

            int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;

            for (int by = 0; by < h; by += 8) {
                int copyH = Math.min(8, h - by);
                bmp.getPixels(rowPixels, 0, w, 0, by, w, copyH);
                for (int r = copyH; r < 8; r++) {
                    System.arraycopy(rowPixels, (copyH - 1) * w, rowPixels, r * w, w);
                }

                for (int bx = 0; bx < w; bx += 8) {
                    int p = 0;
                    for (int rr = 0; rr < 8; rr++) {
                        int rowBase = rr * w;
                        for (int cc = 0; cc < 8; cc++) {
                            int px = Math.min(bx + cc, w - 1);
                            int argb = rowPixels[rowBase + px];
                            int red = (argb >> 16) & 0xFF;
                            int grn = (argb >> 8) & 0xFF;
                            int blu = argb & 0xFF;

                            blkY[p]  =  0.29900f * red + 0.58700f * grn + 0.11400f * blu - 128.0f;
                            blkCb[p] = -0.16874f * red - 0.33126f * grn + 0.50000f * blu;
                            blkCr[p] =  0.50000f * red - 0.41869f * grn - 0.08131f * blu;
                            p++;
                        }
                    }

                    fdctAndQuant(blkY, fdtblY, qBlk);
                    prevDcY = encodeBlock(bw, qBlk, prevDcY, dcY, acY);

                    fdctAndQuant(blkCb, fdtblC, qBlk);
                    prevDcCb = encodeBlock(bw, qBlk, prevDcCb, dcC, acC);

                    fdctAndQuant(blkCr, fdtblC, qBlk);
                    prevDcCr = encodeBlock(bw, qBlk, prevDcCr, dcC, acC);
                }
            }

            bw.flushWithOnes();
            // EOI
            out.write(0xFF); out.write(0xD9);
            return out.toByteArray();
        }

        private static void fdctAndQuant(float[] data, float[] fdtbl, int[] outZig) {
            for (int i = 0; i < 64; i += 8) {
                float t0 = data[i] + data[i + 7];
                float t7 = data[i] - data[i + 7];
                float t1 = data[i + 1] + data[i + 6];
                float t6 = data[i + 1] - data[i + 6];
                float t2 = data[i + 2] + data[i + 5];
                float t5 = data[i + 2] - data[i + 5];
                float t3 = data[i + 3] + data[i + 4];
                float t4 = data[i + 3] - data[i + 4];

                float t10 = t0 + t3;
                float t13 = t0 - t3;
                float t11 = t1 + t2;
                float t12 = t1 - t2;

                data[i] = t10 + t11;
                data[i + 4] = t10 - t11;

                float z1 = (t12 + t13) * 0.707106781f;
                data[i + 2] = t13 + z1;
                data[i + 6] = t13 - z1;

                t10 = t4 + t5;
                t11 = t5 + t6;
                t12 = t6 + t7;

                float z5 = (t10 - t12) * 0.382683433f;
                float z2 = 0.541196100f * t10 + z5;
                float z4 = 1.306562965f * t12 + z5;
                float z3 = t11 * 0.707106781f;

                float z11 = t7 + z3;
                float z13 = t7 - z3;

                data[i + 5] = z13 + z2;
                data[i + 3] = z13 - z2;
                data[i + 1] = z11 + z4;
                data[i + 7] = z11 - z4;
            }

            for (int i = 0; i < 8; i++) {
                float t0 = data[i] + data[i + 56];
                float t7 = data[i] - data[i + 56];
                float t1 = data[i + 8] + data[i + 48];
                float t6 = data[i + 8] - data[i + 48];
                float t2 = data[i + 16] + data[i + 40];
                float t5 = data[i + 16] - data[i + 40];
                float t3 = data[i + 24] + data[i + 32];
                float t4 = data[i + 24] - data[i + 32];

                float t10 = t0 + t3;
                float t13 = t0 - t3;
                float t11 = t1 + t2;
                float t12 = t1 - t2;

                data[i] = t10 + t11;
                data[i + 32] = t10 - t11;

                float z1 = (t12 + t13) * 0.707106781f;
                data[i + 16] = t13 + z1;
                data[i + 48] = t13 - z1;

                t10 = t4 + t5;
                t11 = t5 + t6;
                t12 = t6 + t7;

                float z5 = (t10 - t12) * 0.382683433f;
                float z2 = 0.541196100f * t10 + z5;
                float z4 = 1.306562965f * t12 + z5;
                float z3 = t11 * 0.707106781f;

                float z11 = t7 + z3;
                float z13 = t7 - z3;

                data[i + 40] = z13 + z2;
                data[i + 24] = z13 - z2;
                data[i + 8] = z11 + z4;
                data[i + 56] = z11 - z4;
            }

            for (int i = 0; i < 64; i++) {
                float v = data[i] * fdtbl[i];
                outZig[ZIGZAG[i]] = Math.round(v);
            }
        }

        private static int encodeBlock(BitWriter bw, int[] zz, int prevDc, int[][] dcHuff, int[][] acHuff) {
            int dc = zz[0];
            int diff = dc - prevDc;
            if (diff == 0) {
                bw.writeBits(dcHuff[0][0], dcHuff[0][1]);
            } else {
                int abs = diff < 0 ? -diff : diff;
                int nbits = 32 - Integer.numberOfLeadingZeros(abs);
                int mag = diff < 0 ? (diff + (1 << nbits) - 1) : diff;
                bw.writeBits(dcHuff[nbits][0], dcHuff[nbits][1]);
                bw.writeBits(mag, nbits);
            }

            int endNonZero = 63;
            while (endNonZero > 0 && zz[endNonZero] == 0) endNonZero--;
            if (endNonZero == 0) {
                bw.writeBits(acHuff[0x00][0], acHuff[0x00][1]);
                return dc;
            }

            int i = 1;
            while (i <= endNonZero) {
                int startZero = i;
                while (zz[i] == 0 && i <= endNonZero) i++;
                int run = i - startZero;
                while (run >= 16) {
                    bw.writeBits(acHuff[0xF0][0], acHuff[0xF0][1]);
                    run -= 16;
                }
                int val = zz[i];
                int abs = val < 0 ? -val : val;
                int nbits = 32 - Integer.numberOfLeadingZeros(abs);
                int mag = val < 0 ? (val + (1 << nbits) - 1) : val;
                int sym = (run << 4) | nbits;
                bw.writeBits(acHuff[sym][0], acHuff[sym][1]);
                bw.writeBits(mag, nbits);
                i++;
            }
            if (endNonZero < 63) {
                bw.writeBits(acHuff[0x00][0], acHuff[0x00][1]);
            }
            return dc;
        }

        private static int[][] buildHuffTable(int[] bits, int[] vals) {
            int[][] tbl = new int[256][2];
            int code = 0;
            int p = 0;
            for (int l = 1; l <= 16; l++) {
                int cnt = bits[l];
                for (int j = 0; j < cnt; j++) {
                    int sym = vals[p++];
                    tbl[sym][0] = code;
                    tbl[sym][1] = l;
                    code++;
                }
                code <<= 1;
            }
            return tbl;
        }

        private static void writeDqt(ByteArrayOutputStream out, int id, int[] zigQt) {
            out.write(0xFF); out.write(0xDB);
            out.write(0x00); out.write(67);
            out.write(id & 0x0F);
            for (int i = 0; i < 64; i++) {
                out.write(zigQt[i] & 0xFF);
            }
        }

        private static void writeDht(ByteArrayOutputStream out, int tcTh, int[] bits, int[] vals) {
            int len = 19 + vals.length;
            out.write(0xFF); out.write(0xC4);
            out.write((len >> 8) & 0xFF); out.write(len & 0xFF);
            out.write(tcTh);
            for (int i = 1; i <= 16; i++) out.write(bits[i] & 0xFF);
            for (int v : vals) out.write(v & 0xFF);
        }

        private static final class BitWriter {
            private final ByteArrayOutputStream out;
            private int bitBuf = 0;
            private int bitCnt = 0;

            BitWriter(ByteArrayOutputStream out) {
                this.out = out;
            }

            void writeBits(int val, int nbits) {
                bitBuf = (bitBuf << nbits) | (val & ((1 << nbits) - 1));
                bitCnt += nbits;
                while (bitCnt >= 8) {
                    bitCnt -= 8;
                    int b = (bitBuf >> bitCnt) & 0xFF;
                    out.write(b);
                    if (b == 0xFF) {
                        out.write(0x00);
                    }
                }
            }

            void flushWithOnes() {
                if (bitCnt > 0) {
                    int pad = 8 - bitCnt;
                    writeBits((1 << pad) - 1, pad);
                }
            }
        }
    }
}
