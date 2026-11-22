using System;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BootThemeStudio.Services;

/// <summary>
/// Pure C# Baseline DCT (SOF0) YCbCr 4:4:4 (1x1/1x1/1x1) JPEG encoder for Gen5W's
/// Telechips `TCC_JPU_C6` bootloader splash hardware decoder.
///
/// Standard WPF `JpegBitmapEncoder` outputs YCbCr 4:2:0 (2x2/1x1/1x1), whereas factory
/// `splash` partition JPEGs use Baseline DCT SOF0 YCbCr 4:4:4 (`<= 450 KB`).
/// </summary>
public static class BaselineJpeg444Encoder
{
    private static readonly int[] ZigZag =
    [
         0,  1,  5,  6, 14, 15, 27, 28,
         2,  4,  7, 13, 16, 26, 29, 42,
         3,  8, 12, 17, 25, 30, 41, 43,
         9, 11, 18, 24, 31, 40, 44, 53,
        10, 19, 23, 32, 39, 45, 52, 54,
        20, 22, 33, 38, 46, 51, 55, 60,
        21, 34, 37, 47, 50, 56, 59, 61,
        35, 36, 48, 49, 57, 58, 62, 63
    ];

    private static readonly int[] StdLumQt =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68,109,103, 77,
        24, 35, 55, 64, 81,104,113, 92,
        49, 64, 78, 87,103,121,120,101,
        72, 92, 95, 98,112,100,103, 99
    ];

    private static readonly int[] StdChromQt =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    ];

    private static readonly int[] BitsDcLum = [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly int[] ValDcLum  = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly int[] BitsDcChrom = [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly int[] ValDcChrom  = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly int[] BitsAcLum = [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly int[] ValAcLum =
    [
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
    ];

    private static readonly int[] BitsAcChrom = [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly int[] ValAcChrom =
    [
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
    ];

    private static readonly float[] AanScale =
    [
        1.0f, 1.387039845f, 1.306562965f, 1.175875602f,
        1.0f, 0.785694958f, 0.541196100f, 0.275899379f
    ];

    public static byte[] EncodeJpuSafe(BitmapSource src, int maxBytes = 440 * 1024)
    {
        int[] qualities = [92, 88, 84, 80, 75, 70, 64];
        foreach (int q in qualities)
        {
            byte[] encoded = Encode(src, q);
            if (encoded.Length <= maxBytes && IsValidJpu444Jpeg(encoded, src.PixelWidth, src.PixelHeight))
                return encoded;
        }
        throw new InvalidOperationException("Encoded Boot Image exceeded safe 450 KB hardware JPU limit.");
    }

    public static byte[] Encode(BitmapSource bitmap, int quality = 90)
    {
        BitmapSource bgra = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Bgr32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        return EncodeFromBgra32(pixels, w, h, stride, quality);
    }

    public static byte[] EncodeFromBgra32(byte[] bgraPixels, int w, int h, int stride, int quality = 90)
    {
        int q = Math.Clamp(quality, 1, 100);
        int scale = q < 50 ? (5000 / q) : (200 - q * 2);

        int[] lumQtZig = new int[64];
        int[] chrQtZig = new int[64];
        float[] fdtblY = new float[64];
        float[] fdtblC = new float[64];

        for (int i = 0; i < 64; i++)
        {
            int yv = Math.Clamp((StdLumQt[i] * scale + 50) / 100, 1, 255);
            int cv = Math.Clamp((StdChromQt[i] * scale + 50) / 100, 1, 255);
            lumQtZig[ZigZag[i]] = yv;
            chrQtZig[ZigZag[i]] = cv;
        }

        int k = 0;
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                float denom = AanScale[row] * AanScale[col] * 8.0f;
                fdtblY[k] = 1.0f / (lumQtZig[ZigZag[k]] * denom);
                fdtblC[k] = 1.0f / (chrQtZig[ZigZag[k]] * denom);
                k++;
            }
        }

        var dcY = BuildHuffTable(BitsDcLum, ValDcLum);
        var acY = BuildHuffTable(BitsAcLum, ValAcLum);
        var dcC = BuildHuffTable(BitsDcChrom, ValDcChrom);
        var acC = BuildHuffTable(BitsAcChrom, ValAcChrom);

        using var ms = new MemoryStream(262144);
        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);
        // APP0 JFIF
        ms.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        // DQT
        WriteDqt(ms, 0, lumQtZig);
        WriteDqt(ms, 1, chrQtZig);
        // SOF0 (Baseline DCT, 8-bit, 3 components, 1x1/1x1/1x1 -> YCbCr 4:4:4)
        ms.Write([
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)((h >> 8) & 0xFF), (byte)(h & 0xFF),
            (byte)((w >> 8) & 0xFF), (byte)(w & 0xFF),
            0x03,
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01
        ]);
        // DHT
        WriteDht(ms, 0x00, BitsDcLum, ValDcLum);
        WriteDht(ms, 0x10, BitsAcLum, ValAcLum);
        WriteDht(ms, 0x01, BitsDcChrom, ValDcChrom);
        WriteDht(ms, 0x11, BitsAcChrom, ValAcChrom);
        // SOS
        ms.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);

        var bw = new BitWriter(ms);
        float[] blkY = new float[64];
        float[] blkCb = new float[64];
        float[] blkCr = new float[64];
        int[] qBlk = new int[64];

        int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;

        for (int by = 0; by < h; by += 8)
        {
            for (int bx = 0; bx < w; bx += 8)
            {
                int p = 0;
                for (int rr = 0; rr < 8; rr++)
                {
                    int py = Math.Min(by + rr, h - 1);
                    int rowOff = py * stride;
                    for (int cc = 0; cc < 8; cc++)
                    {
                        int px = Math.Min(bx + cc, w - 1);
                        int off = rowOff + px * 4;
                        int blu = bgraPixels[off];
                        int grn = bgraPixels[off + 1];
                        int red = bgraPixels[off + 2];

                        blkY[p]  =  0.29900f * red + 0.58700f * grn + 0.11400f * blu - 128.0f;
                        blkCb[p] = -0.16874f * red - 0.33126f * grn + 0.50000f * blu;
                        blkCr[p] =  0.50000f * red - 0.41869f * grn - 0.08131f * blu;
                        p++;
                    }
                }

                FdctAndQuant(blkY, fdtblY, qBlk);
                prevDcY = EncodeBlock(bw, qBlk, prevDcY, dcY, acY);

                FdctAndQuant(blkCb, fdtblC, qBlk);
                prevDcCb = EncodeBlock(bw, qBlk, prevDcCb, dcC, acC);

                FdctAndQuant(blkCr, fdtblC, qBlk);
                prevDcCr = EncodeBlock(bw, qBlk, prevDcCr, dcC, acC);
            }
        }

        bw.FlushWithOnes();
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);
        return ms.ToArray();
    }

    public static bool IsValidJpu444Jpeg(byte[] jpeg, int expectedW, int expectedH)
    {
        if (jpeg == null || jpeg.Length < 128) return false;
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        if (jpeg[^2] != 0xFF || jpeg[^1] != 0xD9) return false;

        int pos = 2;
        bool foundSof0 = false;
        while (pos < jpeg.Length - 1)
        {
            if (jpeg[pos] != 0xFF) { pos++; continue; }
            while (pos < jpeg.Length && jpeg[pos] == 0xFF) pos++;
            if (pos >= jpeg.Length) break;
            int m = jpeg[pos++];
            if (m == 0xD9 || m == 0xDA) break;
            if ((m >= 0xD0 && m <= 0xD8) || m == 0x01) continue;
            if (pos + 2 > jpeg.Length) return false;
            int segLen = (jpeg[pos] << 8) | jpeg[pos + 1];
            if (segLen < 2 || pos + segLen > jpeg.Length) return false;
            if (m == 0xC2) return false; // Progressive forbidden
            if (m == 0xC0)
            {
                if (segLen < 17) return false;
                int prec = jpeg[pos + 2];
                int h = (jpeg[pos + 3] << 8) | jpeg[pos + 4];
                int w = (jpeg[pos + 5] << 8) | jpeg[pos + 6];
                int ncomp = jpeg[pos + 7];
                if (prec != 8 || ncomp != 3 || w != expectedW || h != expectedH) return false;
                for (int c = 0; c < 3; c++)
                {
                    if (jpeg[pos + 9 + c * 3] != 0x11) return false;
                }
                foundSof0 = true;
            }
            pos += segLen;
        }
        return foundSof0;
    }

    private static void FdctAndQuant(float[] data, float[] fdtbl, int[] outZig)
    {
        for (int i = 0; i < 64; i += 8)
        {
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

        for (int i = 0; i < 8; i++)
        {
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

        for (int i = 0; i < 64; i++)
        {
            outZig[ZigZag[i]] = (int)MathF.Round(data[i] * fdtbl[i]);
        }
    }

    private static int EncodeBlock(BitWriter bw, int[] zz, int prevDc, (int Code, int Len)[] dcHuff, (int Code, int Len)[] acHuff)
    {
        int dc = zz[0];
        int diff = dc - prevDc;
        if (diff == 0)
        {
            bw.WriteBits(dcHuff[0].Code, dcHuff[0].Len);
        }
        else
        {
            uint abs = (uint)(diff < 0 ? -diff : diff);
            int nbits = 32 - BitOperations.LeadingZeroCount(abs);
            int mag = diff < 0 ? (diff + (1 << nbits) - 1) : diff;
            bw.WriteBits(dcHuff[nbits].Code, dcHuff[nbits].Len);
            bw.WriteBits(mag, nbits);
        }

        int endNonZero = 63;
        while (endNonZero > 0 && zz[endNonZero] == 0) endNonZero--;
        if (endNonZero == 0)
        {
            bw.WriteBits(acHuff[0x00].Code, acHuff[0x00].Len);
            return dc;
        }

        int i = 1;
        while (i <= endNonZero)
        {
            int startZero = i;
            while (zz[i] == 0 && i <= endNonZero) i++;
            int run = i - startZero;
            while (run >= 16)
            {
                bw.WriteBits(acHuff[0xF0].Code, acHuff[0xF0].Len);
                run -= 16;
            }
            int val = zz[i];
            uint abs = (uint)(val < 0 ? -val : val);
            int nbits = 32 - BitOperations.LeadingZeroCount(abs);
            int mag = val < 0 ? (val + (1 << nbits) - 1) : val;
            int sym = (run << 4) | nbits;
            bw.WriteBits(acHuff[sym].Code, acHuff[sym].Len);
            bw.WriteBits(mag, nbits);
            i++;
        }
        if (endNonZero < 63)
        {
            bw.WriteBits(acHuff[0x00].Code, acHuff[0x00].Len);
        }
        return dc;
    }

    private static (int Code, int Len)[] BuildHuffTable(int[] bits, int[] vals)
    {
        var tbl = new (int Code, int Len)[256];
        int code = 0;
        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int cnt = bits[l];
            for (int j = 0; j < cnt; j++)
            {
                int sym = vals[p++];
                tbl[sym] = (code, l);
                code++;
            }
            code <<= 1;
        }
        return tbl;
    }

    private static void WriteDqt(Stream s, int id, int[] zigQt)
    {
        s.WriteByte(0xFF); s.WriteByte(0xDB);
        s.WriteByte(0x00); s.WriteByte(67);
        s.WriteByte((byte)(id & 0x0F));
        for (int i = 0; i < 64; i++) s.WriteByte((byte)(zigQt[i] & 0xFF));
    }

    private static void WriteDht(Stream s, int tcTh, int[] bits, int[] vals)
    {
        int len = 19 + vals.Length;
        s.WriteByte(0xFF); s.WriteByte(0xC4);
        s.WriteByte((byte)((len >> 8) & 0xFF)); s.WriteByte((byte)(len & 0xFF));
        s.WriteByte((byte)tcTh);
        for (int i = 1; i <= 16; i++) s.WriteByte((byte)(bits[i] & 0xFF));
        foreach (int v in vals) s.WriteByte((byte)(v & 0xFF));
    }

    private sealed class BitWriter(Stream output)
    {
        private int _bitBuf;
        private int _bitCnt;

        public void WriteBits(int val, int nbits)
        {
            _bitBuf = (_bitBuf << nbits) | (val & ((1 << nbits) - 1));
            _bitCnt += nbits;
            while (_bitCnt >= 8)
            {
                _bitCnt -= 8;
                int b = (_bitBuf >> _bitCnt) & 0xFF;
                output.WriteByte((byte)b);
                if (b == 0xFF) output.WriteByte(0x00);
            }
        }

        public void FlushWithOnes()
        {
            if (_bitCnt > 0)
            {
                int pad = 8 - _bitCnt;
                WriteBits((1 << pad) - 1, pad);
            }
        }
    }
}
