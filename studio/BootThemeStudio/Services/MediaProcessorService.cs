using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BootThemeStudio.Models;

namespace BootThemeStudio.Services;

public enum ImageCropMode
{
    CoverFill = 0,
    FitLetterbox = 1
}

public sealed class ProcessedBootImageAssets
{
    public required byte[] Splash1920Jpg { get; init; }
    public required byte[] Splash1280Jpg { get; init; }
    public required byte[] Preview960Jpg { get; init; }
    public required BitmapSource PreviewBitmap { get; init; }
}

public sealed class ProcessedVideoAssets
{
    public required string GoodbyeMp4Path { get; init; }
    public string? WelcomeMp4Path { get; init; }
    public required byte[] PreviewGifOrMp4Bytes { get; init; }
    public required string PreviewExtension { get; init; } // ".gif" or ".mp4"
    public required byte[] Poster960Jpg { get; init; }
    public required int DurationMs { get; init; }
    public required BitmapSource PosterBitmap { get; init; }
    public required string EngineUsed { get; init; }
}

/// <summary>
/// Handles:
///  1. Boot Image cropping/scaling (`1920x720`, `1280x720`, `960x360`) + JPU Baseline 4:4:4 JPEG encoding.
///  2. Video conversion (`1920x720` silent H.264 `avc1` `.mp4` `<= 35 MB`) + `960x360` animated `.gif` preview
///     + `960x360` non-black peak `preview_poweroff_poster.jpg` via either auto-discovered `ffmpeg.exe`
///     OR zero-dependency Windows Media Foundation (`mfplat.dll` / `mfreadwrite.dll`) fallback!
/// </summary>
public static class MediaProcessorService
{
    private static string? _customFfmpegPath;

    public static string? CustomFfmpegPath
    {
        get => _customFfmpegPath;
        set => _customFfmpegPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? FindFfmpegExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_customFfmpegPath) && File.Exists(_customFfmpegPath))
            return _customFfmpegPath;

        string baseDir = AppContext.BaseDirectory;
        string[] directCandidates =
        [
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "tools", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootThemeStudio", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gen5WStudio", "ffmpeg.exe")
        ];
        foreach (string c in directCandidates)
        {
            if (File.Exists(c)) return c;
        }

        // Check PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }

        // Check common WinGet / Chocolatey / Scoop / Python imageio_ffmpeg locations
        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pyRoot = Path.Combine(localApp, "Programs", "Python");
            if (Directory.Exists(pyRoot))
            {
                var found = Directory.EnumerateFiles(pyRoot, "ffmpeg*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null) return found;
            }

            string wingetPkgs = Path.Combine(localApp, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPkgs))
            {
                foreach (string pkgDir in Directory.EnumerateDirectories(wingetPkgs, "*FFmpeg*", SearchOption.TopDirectoryOnly))
                {
                    var found = Directory.EnumerateFiles(pkgDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
            }
        }
        catch { }

        return null;
    }

    public static BitmapSource LoadBitmapUnlocked(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        return DecodeBitmapFromBytes(bytes);
    }

    public static BitmapSource DecodeBitmapFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Renders a source bitmap into an exact `targetW x targetH` canvas using Cover/Fill (with panX/panY in -1..+1)
    /// or Fit/Letterbox on pure black, returning a frozen Bgra32 BitmapSource.
    /// </summary>
    public static BitmapSource RenderCroppedOrFitted(
        BitmapSource source,
        int targetW,
        int targetH,
        ImageCropMode mode,
        double panX = 0.0,
        double panY = 0.0,
        double zoom = 1.0)
    {
        panX = Math.Clamp(panX, -1.0, 1.0);
        panY = Math.Clamp(panY, -1.0, 1.0);
        zoom = Math.Clamp(zoom, 1.0, 3.0);

        double sw = source.PixelWidth;
        double sh = source.PixelHeight;

        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, targetW, targetH));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, targetW, targetH)));

            double baseScale = mode == ImageCropMode.CoverFill
                ? Math.Max(targetW / sw, targetH / sh)
                : Math.Min(targetW / sw, targetH / sh);

            double finalScale = baseScale * zoom;
            double drawW = sw * finalScale;
            double drawH = sh * finalScale;

            double slackX = Math.Max(0, (drawW - targetW) / 2.0);
            double slackY = Math.Max(0, (drawH - targetH) / 2.0);

            double x = (targetW - drawW) / 2.0 - panX * slackX;
            double y = (targetH - drawH) / 2.0 - panY * slackY;

            dc.DrawImage(source, new Rect(x, y, drawW, drawH));
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    public static ProcessedBootImageAssets ProcessBootImage(
        BitmapSource source,
        ImageCropMode mode,
        double panX,
        double panY,
        double zoom)
    {
        BitmapSource bmp1920 = RenderCroppedOrFitted(source, 1920, 720, mode, panX, panY, zoom);
        BitmapSource bmp1280 = RenderCroppedOrFitted(source, 1280, 720, mode, panX, panY, zoom);
        BitmapSource bmp960 = RenderCroppedOrFitted(source, 960, 360, mode, panX, panY, zoom);

        byte[] splash1920 = BaselineJpeg444Encoder.EncodeJpuSafe(bmp1920, 440 * 1024);
        byte[] splash1280 = BaselineJpeg444Encoder.EncodeJpuSafe(bmp1280, 440 * 1024);
        byte[] preview960 = EncodePreviewJpeg(bmp960, 88);

        return new ProcessedBootImageAssets
        {
            Splash1920Jpg = splash1920,
            Splash1280Jpg = splash1280,
            Preview960Jpg = preview960,
            PreviewBitmap = bmp1920
        };
    }

    public static byte[] EncodePreviewJpeg(BitmapSource src, int quality = 88)
    {
        // Using BaselineJpeg444Encoder guarantees Baseline SOF0 (non-progressive) <= 2 MB with crisp chroma!
        for (int q = quality; q >= 60; q -= 6)
        {
            byte[] data = BaselineJpeg444Encoder.Encode(src, q);
            if (data.Length <= StudioConstants.MaxStaticPreviewBytes)
                return data;
        }
        throw new InvalidOperationException("Preview JPEG exceeded 2 MB server limit.");
    }

    /// <summary>
    /// Checks if an `.mp4` already meets the exact v1.1 car/server rules (`1920x720`, H.264 `avc1`,
    /// ZERO `soun` audio tracks, `<= 35 MB`), and parses its `mvhd` duration in milliseconds.
    /// </summary>
    public static bool InspectMp4File(
        string filePath,
        out int width,
        out int height,
        out int durationMs,
        out bool hasAvc1,
        out bool hasAudioTrack,
        out string summaryError)
    {
        width = 0;
        height = 0;
        durationMs = 0;
        hasAvc1 = false;
        hasAudioTrack = false;
        summaryError = "";

        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length < 64)
        {
            summaryError = "MP4 file missing or empty.";
            return false;
        }
        if (fi.Length > StudioConstants.MaxPayloadMp4Bytes)
        {
            summaryError = $"MP4 exceeds 35 MB limit ({fi.Length / (1024.0 * 1024.0):0.##} MB).";
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] head = new byte[12];
        if (fs.Read(head, 0, 12) < 12 || Encoding.ASCII.GetString(head, 4, 4) != "ftyp")
        {
            summaryError = "Not a valid ISO BMFF (.mp4) container (missing ftyp box).";
            return false;
        }

        fs.Position = 0;
        ScanIsoBoxes(fs, 0, fi.Length, ref width, ref height, ref durationMs, ref hasAvc1, ref hasAudioTrack);

        if (!hasAvc1) summaryError = "Video codec is not H.264 (avc1).";
        else if (hasAudioTrack) summaryError = "MP4 contains an audio ('soun') track (must be silent).";
        else if (width != 1920 || height != 720) summaryError = $"Dimensions are {width}x{height} (must be 1920x720).";

        return string.IsNullOrEmpty(summaryError);
    }

    private static void ScanIsoBoxes(
        Stream s,
        long start,
        long end,
        ref int width,
        ref int height,
        ref int durationMs,
        ref bool hasAvc1,
        ref bool hasAudio)
    {
        byte[] hdr = new byte[16];
        long pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            if (s.Read(hdr, 0, 8) < 8) break;
            uint sz32 = ReadUInt32BE(hdr, 0);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            long boxSize = sz32;
            long payloadStart = pos + 8;
            if (sz32 == 1)
            {
                if (s.Read(hdr, 8, 8) < 8) break;
                boxSize = (long)ReadUInt64BE(hdr, 8);
                payloadStart = pos + 16;
            }
            else if (sz32 == 0)
            {
                boxSize = end - pos;
            }
            if (boxSize < 8 || pos + boxSize > end) break;
            long boxEnd = pos + boxSize;

            if (type is "moov" or "trak" or "mdia" or "minf" or "stbl")
            {
                ScanIsoBoxes(s, payloadStart, boxEnd, ref width, ref height, ref durationMs, ref hasAvc1, ref hasAudio);
            }
            else if (type == "mvhd" && boxEnd - payloadStart >= 20)
            {
                s.Position = payloadStart;
                byte[] b = new byte[32];
                int r = s.Read(b, 0, (int)Math.Min(32, boxEnd - payloadStart));
                int ver = b[0];
                if (ver == 0 && r >= 20)
                {
                    uint ts = ReadUInt32BE(b, 12);
                    uint dur = ReadUInt32BE(b, 16);
                    if (ts > 0) durationMs = (int)Math.Round(dur * 1000.0 / ts);
                }
                else if (ver == 1 && r >= 28)
                {
                    uint ts = ReadUInt32BE(b, 20);
                    ulong dur = ReadUInt64BE(b, 24);
                    if (ts > 0) durationMs = (int)Math.Round(dur * 1000.0 / ts);
                }
            }
            else if (type == "hdlr" && boxEnd - payloadStart >= 12)
            {
                s.Position = payloadStart + 8;
                byte[] h4 = new byte[4];
                if (s.Read(h4, 0, 4) == 4 && Encoding.ASCII.GetString(h4) == "soun")
                    hasAudio = true;
            }
            else if (type == "tkhd" && boxEnd - payloadStart >= 84)
            {
                s.Position = payloadStart;
                byte[] b = new byte[96];
                int r = s.Read(b, 0, (int)Math.Min(96, boxEnd - payloadStart));
                int ver = b[0];
                int wOff = ver == 1 ? 88 : 76;
                if (r >= wOff + 8)
                {
                    int wFixed = (int)(ReadUInt32BE(b, wOff) >> 16);
                    int hFixed = (int)(ReadUInt32BE(b, wOff + 4) >> 16);
                    if (wFixed > 0 && hFixed > 0 && width == 0)
                    {
                        width = wFixed;
                        height = hFixed;
                    }
                }
            }
            else if (type == "stsd" && boxEnd - payloadStart >= 16)
            {
                s.Position = payloadStart + 8;
                long entryPos = payloadStart + 8;
                while (entryPos + 8 <= boxEnd)
                {
                    s.Position = entryPos;
                    if (s.Read(hdr, 0, 8) < 8) break;
                    uint esz = ReadUInt32BE(hdr, 0);
                    string etype = Encoding.ASCII.GetString(hdr, 4, 4);
                    if (esz < 8 || entryPos + esz > boxEnd) break;
                    if (etype is "avc1" or "avc3")
                    {
                        hasAvc1 = true;
                        if (esz >= 36)
                        {
                            s.Position = entryPos + 32;
                            byte[] wh = new byte[4];
                            if (s.Read(wh, 0, 4) == 4)
                            {
                                width = (wh[0] << 8) | wh[1];
                                height = (wh[2] << 8) | wh[3];
                            }
                        }
                    }
                    entryPos += esz;
                }
            }

            pos = boxEnd;
        }
    }

    private static uint ReadUInt32BE(byte[] b, int off) =>
        ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    private static ulong ReadUInt64BE(byte[] b, int off) =>
        ((ulong)ReadUInt32BE(b, off) << 32) | ReadUInt32BE(b, off + 4);

    /// <summary>
    /// Converts any video into a silent `1920x720` H.264 `avc1` `.mp4` (`<= 35 MB`), optional separate Welcome video,
    /// `960x360` animated `.gif` preview (`<= 10 MB`), and `960x360` static peak poster `.jpg` (`<= 2 MB`).
    /// Uses `ffmpeg` when available on the system, or native Windows Media Foundation H.264 transcoding otherwise!
    /// </summary>
    public static async Task<ProcessedVideoAssets> ConvertVideosAsync(
        string goodbyeInputPath,
        string? welcomeInputPath,
        string workDir,
        double trimStartSec,
        double maxDurationSec,
        double posterAtPercent,
        ImageCropMode cropMode,
        double panX = 0.0,
        double panY = 0.0,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        string outGoodbye = Path.Combine(workDir, "goodbye_1920x720.mp4");
        string? outWelcome = !string.IsNullOrWhiteSpace(welcomeInputPath)
            ? Path.Combine(workDir, "welcome_1920x720.mp4")
            : null;

        string? ffmpeg = FindFfmpegExecutable();
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            return await Task.Run(() => ConvertViaFfmpeg(
                ffmpeg!,
                goodbyeInputPath,
                welcomeInputPath,
                outGoodbye,
                outWelcome,
                workDir,
                trimStartSec,
                maxDurationSec,
                posterAtPercent,
                cropMode,
                panX,
                panY,
                progress,
                ct), ct).ConfigureAwait(false);
        }

        // Native zero-dependency Windows Media Foundation H.264 fallback!
        return await Task.Run(() => ConvertViaMediaFoundation(
            goodbyeInputPath,
            welcomeInputPath,
            outGoodbye,
            outWelcome,
            trimStartSec,
            maxDurationSec,
            posterAtPercent,
            cropMode,
            panX,
            panY,
            progress,
            ct), ct).ConfigureAwait(false);
    }

    private static ProcessedVideoAssets ConvertViaFfmpeg(
        string ffmpeg,
        string goodbyeSrc,
        string? welcomeSrc,
        string outGoodbye,
        string? outWelcome,
        string workDir,
        double trimStartSec,
        double maxDurationSec,
        double posterAtPercent,
        ImageCropMode cropMode,
        double panX,
        double panY,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Converting Goodbye video to 1920x720 silent H.264 (ffmpeg)...");
        TranscodeSingleMp4Ffmpeg(ffmpeg, goodbyeSrc, outGoodbye, trimStartSec, maxDurationSec, cropMode, panX, panY, ct);

        if (!string.IsNullOrWhiteSpace(welcomeSrc) && outWelcome != null)
        {
            progress?.Report("Converting Welcome video to 1920x720 silent H.264 (ffmpeg)...");
            TranscodeSingleMp4Ffmpeg(ffmpeg, welcomeSrc!, outWelcome, trimStartSec, maxDurationSec, cropMode, panX, panY, ct);
        }

        if (!InspectMp4File(outGoodbye, out _, out _, out int durMs, out _, out _, out string mp4Err))
            throw new InvalidOperationException("Converted MP4 validation failed: " + mp4Err);

        if (durMs <= 0) durMs = 5000;

        // Extract non-black peak poster frame at `posterAtPercent` (clamped inside video duration)
        progress?.Report("Extracting 960x360 poster frame...");
        double posterSec = Math.Clamp((durMs / 1000.0) * Math.Clamp(posterAtPercent, 0.05, 0.95), 0.0, Math.Max(0.0, durMs / 1000.0 - 0.1));
        string tmpPosterPng = Path.Combine(workDir, "_tmp_poster.png");
        RunProcessChecked(ffmpeg, [
            "-y",
            "-ss", posterSec.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", outGoodbye,
            "-frames:v", "1",
            "-vf", "scale=960:360:flags=lanczos",
            tmpPosterPng
        ], ct);

        if (!File.Exists(tmpPosterPng))
        {
            RunProcessChecked(ffmpeg, ["-y", "-i", outGoodbye, "-frames:v", "1", "-vf", "scale=960:360:flags=lanczos", tmpPosterPng], ct);
        }

        BitmapSource posterBmp = LoadBitmapUnlocked(tmpPosterPng);
        if (posterBmp.PixelWidth != 960 || posterBmp.PixelHeight != 360)
            posterBmp = RenderCroppedOrFitted(posterBmp, 960, 360, ImageCropMode.CoverFill);
        byte[] posterJpg = EncodePreviewJpeg(posterBmp, 88);
        try { File.Delete(tmpPosterPng); } catch { }

        // Build compact 960x360 silent H.264 preview_poweroff.mp4 (~0.3–1.2 MB — ~6–10x smaller over car cellular/hotspot than a 960x360 GIF!)
        progress?.Report("Generating compact 960x360 H.264 video preview...");
        string tmpPrevMp4 = Path.Combine(workDir, "_tmp_preview_960x360.mp4");
        byte[]? previewBytes = null;
        string previewExt = ".mp4";

        foreach (int crf in new[] { 26, 30 })
        {
            RunProcessChecked(ffmpeg, [
                "-y",
                "-t", "10",
                "-i", outGoodbye,
                "-an", "-sn", "-dn",
                "-vf", "scale=960:360:flags=lanczos,fps=24,format=yuv420p",
                "-c:v", "libx264",
                "-profile:v", "baseline",
                "-level:v", "3.1",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(CultureInfo.InvariantCulture),
                "-movflags", "+faststart",
                tmpPrevMp4
            ], ct);

            if (File.Exists(tmpPrevMp4))
            {
                long len = new FileInfo(tmpPrevMp4).Length;
                if (len > 0 && len <= StudioConstants.MaxPowerOffPreviewBytes)
                {
                    previewBytes = File.ReadAllBytes(tmpPrevMp4);
                    break;
                }
            }
        }
        try { if (File.Exists(tmpPrevMp4)) File.Delete(tmpPrevMp4); } catch { }

        if (previewBytes == null)
        {
            string tmpGif = Path.Combine(workDir, "_tmp_preview.gif");
            RunProcessChecked(ffmpeg, [
                "-y", "-t", "6", "-i", outGoodbye,
                "-vf", "fps=8,scale=960:360:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=96[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5",
                "-loop", "0", tmpGif
            ], ct);
            previewBytes = File.ReadAllBytes(tmpGif);
            previewExt = ".gif";
            try { File.Delete(tmpGif); } catch { }
        }

        return new ProcessedVideoAssets
        {
            GoodbyeMp4Path = outGoodbye,
            WelcomeMp4Path = outWelcome,
            PreviewGifOrMp4Bytes = previewBytes,
            PreviewExtension = previewExt,
            Poster960Jpg = posterJpg,
            DurationMs = durMs,
            PosterBitmap = posterBmp,
            EngineUsed = "FFmpeg"
        };
    }

    /// <summary>
    /// Extracts a single preview frame from a source video and renders it with the exact requested
    /// Cover/Fit + Horizontal/Vertical Pan framing so the user can interactively see their pan before transcoding!
    /// </summary>
    public static BitmapSource? ExtractRawVideoFrame(string videoPath, double timeSec)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return null;

        string? ffmpeg = FindFfmpegExecutable();
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            string tmpPng = Path.Combine(Path.GetTempPath(), $"bt_rawframe_{Guid.NewGuid():N}.png");
            try
            {
                var args = new List<string> { "-y" };
                if (timeSec > 0.05)
                {
                    args.Add("-ss");
                    args.Add(timeSec.ToString("0.###", CultureInfo.InvariantCulture));
                }
                args.AddRange(["-i", videoPath, "-frames:v", "1", tmpPng]);
                RunProcessChecked(ffmpeg!, args, CancellationToken.None);
                if (File.Exists(tmpPng))
                    return LoadBitmapUnlocked(tmpPng);
            }
            catch { }
            finally
            {
                try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch { }
            }
        }

        return WindowsMediaFoundationTranscoder.ExtractSingleRawFrame(videoPath, timeSec);
    }

    private static void TranscodeSingleMp4Ffmpeg(
        string ffmpeg,
        string srcPath,
        string dstPath,
        double trimStartSec,
        double maxDurationSec,
        ImageCropMode cropMode,
        double panX,
        double panY,
        CancellationToken ct)
    {
        panX = Math.Clamp(panX, -1.0, 1.0);
        panY = Math.Clamp(panY, -1.0, 1.0);

        // Fast path: if source is already compliant 1920x720 silent H.264 avc1 <= 35 MB and no trim/pan requested, copy directly!
        if (trimStartSec <= 0.01
            && Math.Abs(panX) < 0.005
            && Math.Abs(panY) < 0.005
            && InspectMp4File(srcPath, out _, out _, out int existingDurMs, out _, out _, out _)
            && (maxDurationSec <= 0 || existingDurMs <= (maxDurationSec + 0.25) * 1000))
        {
            File.Copy(srcPath, dstPath, overwrite: true);
            return;
        }

        // panX in [-1..+1] -> fx in [0..1] (0 = Left, 0.5 = Center, 1 = Right)
        // panY in [-1..+1] -> fy in [0..1] (0 = Top,  0.5 = Middle, 1 = Bottom)
        string fx = ((panX + 1.0) * 0.5).ToString("0.####", CultureInfo.InvariantCulture);
        string fy = ((panY + 1.0) * 0.5).ToString("0.####", CultureInfo.InvariantCulture);

        string vf = cropMode == ImageCropMode.CoverFill
            ? $"scale=1920:720:force_original_aspect_ratio=increase:flags=lanczos,crop=1920:720:(in_w-1920)*{fx}:(in_h-720)*{fy},fps=30,format=yuv420p"
            : "scale=1920:720:force_original_aspect_ratio=decrease:flags=lanczos,pad=1920:720:(ow-iw)/2:(oh-ih)/2:black,fps=30,format=yuv420p";

        foreach (int crf in new[] { 20, 24, 28 })
        {
            var args = new List<string> { "-y" };
            if (trimStartSec > 0.01)
            {
                args.Add("-ss");
                args.Add(trimStartSec.ToString("0.###", CultureInfo.InvariantCulture));
            }
            args.Add("-i");
            args.Add(srcPath);
            if (maxDurationSec > 0.2)
            {
                args.Add("-t");
                args.Add(maxDurationSec.ToString("0.###", CultureInfo.InvariantCulture));
            }
            args.AddRange([
                "-an", "-sn", "-dn",
                "-vf", vf,
                "-c:v", "libx264",
                "-profile:v", "high",
                "-level:v", "4.1",
                "-pix_fmt", "yuv420p",
                "-crf", crf.ToString(CultureInfo.InvariantCulture),
                "-movflags", "+faststart",
                dstPath
            ]);

            RunProcessChecked(ffmpeg, args, ct);
            if (File.Exists(dstPath) && new FileInfo(dstPath).Length <= StudioConstants.MaxPayloadMp4Bytes)
                return;
        }

        throw new InvalidOperationException("Converted video still exceeded the 35 MB limit; please trim duration.");
    }

    private static void RunProcessChecked(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch " + exe);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
        string err = stderrTask.GetAwaiter().GetResult();
        _ = stdoutTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            string tail = err.Length > 400 ? err[^400..] : err;
            throw new InvalidOperationException($"FFmpeg exited with code {proc.ExitCode}: {tail}");
        }
    }

    private static ProcessedVideoAssets ConvertViaMediaFoundation(
        string goodbyeSrc,
        string? welcomeSrc,
        string outGoodbye,
        string? outWelcome,
        double trimStartSec,
        double maxDurationSec,
        double posterAtPercent,
        ImageCropMode cropMode,
        double panX,
        double panY,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Converting Goodbye video via Windows Media Foundation H.264...");
        var (durMs, sampledFrames960) = WindowsMediaFoundationTranscoder.TranscodeToSilent1920x720H264(
            goodbyeSrc, outGoodbye, trimStartSec, maxDurationSec, cropMode, panX, panY, collectPreviewFrames: true, ct);

        if (!string.IsNullOrWhiteSpace(welcomeSrc) && outWelcome != null)
        {
            progress?.Report("Converting Welcome video via Windows Media Foundation H.264...");
            WindowsMediaFoundationTranscoder.TranscodeToSilent1920x720H264(
                welcomeSrc!, outWelcome, trimStartSec, maxDurationSec, cropMode, panX, panY, collectPreviewFrames: false, ct);
        }

        if (sampledFrames960.Count == 0)
            throw new InvalidOperationException("No video frames could be decoded from the input file.");

        int posterIdx = Math.Clamp((int)Math.Round((sampledFrames960.Count - 1) * Math.Clamp(posterAtPercent, 0.05, 0.95)), 0, sampledFrames960.Count - 1);
        BitmapSource posterBmp = sampledFrames960[posterIdx];
        byte[] posterJpg = EncodePreviewJpeg(posterBmp, 88);

        progress?.Report("Encoding compact 960x360 H.264 preview...");
        string tmpPrevMp4 = Path.Combine(Path.GetDirectoryName(outGoodbye) ?? Path.GetTempPath(), "_mf_preview_960x360.mp4");
        byte[] prevBytes;
        string prevExt = ".mp4";
        try
        {
            WindowsMediaFoundationTranscoder.TranscodeToSilentH264CustomSize(
                goodbyeSrc, tmpPrevMp4, outW: 960, outH: 360, fps: 24, bitrateBps: 1_200_000,
                trimStartSec, Math.Min(maxDurationSec > 0 ? maxDurationSec : 10.0, 10.0), cropMode, panX, panY, collectPreviewFrames: false, ct);
            prevBytes = File.ReadAllBytes(tmpPrevMp4);
        }
        catch
        {
            prevBytes = BuildLoopingGifWithNetscapeExt(sampledFrames960, delayCentiseconds: 10);
            prevExt = ".gif";
        }
        finally
        {
            try { if (File.Exists(tmpPrevMp4)) File.Delete(tmpPrevMp4); } catch { }
        }

        return new ProcessedVideoAssets
        {
            GoodbyeMp4Path = outGoodbye,
            WelcomeMp4Path = outWelcome,
            PreviewGifOrMp4Bytes = prevBytes,
            PreviewExtension = prevExt,
            Poster960Jpg = posterJpg,
            DurationMs = Math.Max(500, durMs),
            PosterBitmap = posterBmp,
            EngineUsed = "Windows Media Foundation"
        };
    }

    private static byte[] BuildLoopingGifWithNetscapeExt(IReadOnlyList<BitmapSource> frames, int delayCentiseconds)
    {
        var enc = new GifBitmapEncoder();
        int step = frames.Count > 60 ? 2 : 1;
        for (int i = 0; i < frames.Count; i += step)
        {
            enc.Frames.Add(BitmapFrame.Create(frames[i]));
        }
        using var rawMs = new MemoryStream();
        enc.Save(rawMs);
        byte[] raw = rawMs.ToArray();

        // Inject NETSCAPE2.0 infinite loop application extension + frame Graphic Control Extension delay if needed
        if (raw.Length < 14 || raw[0] != 'G' || raw[1] != 'I' || raw[2] != 'F')
            return raw;

        int gctFlag = (raw[10] & 0x80) != 0 ? 1 : 0;
        int gctSize = gctFlag != 0 ? 3 * (1 << ((raw[10] & 0x07) + 1)) : 0;
        int headerLen = 13 + gctSize;
        if (headerLen >= raw.Length) return raw;

        using var outMs = new MemoryStream(raw.Length + 64);
        outMs.Write(raw, 0, headerLen);
        // NETSCAPE2.0 loop forever block
        outMs.Write([
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
            (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, 0x00
        ]);

        // Patch delay inside each Image Descriptor by inserting Graphic Control Extension before 0x2C where absent
        int pos = headerLen;
        while (pos < raw.Length)
        {
            byte b = raw[pos];
            if (b == 0x2C) // Image Descriptor without preceding GCE -> insert GCE delay
            {
                outMs.Write([0x21, 0xF9, 0x04, 0x00, (byte)(delayCentiseconds & 0xFF), (byte)((delayCentiseconds >> 8) & 0xFF), 0x00, 0x00]);
            }
            else if (b == 0x21 && pos + 6 < raw.Length && raw[pos + 1] == 0xF9 && raw[pos + 2] == 0x04)
            {
                raw[pos + 4] = (byte)(delayCentiseconds & 0xFF);
                raw[pos + 5] = (byte)((delayCentiseconds >> 8) & 0xFF);
            }
            // Copy rest verbatim if we can't safely parse sub-blocks, or parse cleanly:
            break;
        }
        outMs.Write(raw, pos, raw.Length - pos);
        return outMs.ToArray();
    }
}

/// <summary>
/// Built-in Windows 10/11 Media Foundation (`IMFSourceReader` -> RGB32 scale/crop -> `IMFSinkWriter` H.264)
/// video converter so `BootThemeStudio` works on any clean Windows PC even if `ffmpeg` is not installed!
/// </summary>
internal static class WindowsMediaFoundationTranscoder
{
    private const int MF_VERSION = 0x00020070;
    private const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC);
    private const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");

    public static (int DurationMs, List<BitmapSource> Sampled960Frames) TranscodeToSilent1920x720H264(
        string inputPath,
        string outputPath,
        double trimStartSec,
        double maxDurationSec,
        ImageCropMode cropMode,
        double panX,
        double panY,
        bool collectPreviewFrames,
        CancellationToken ct) =>
        TranscodeToSilentH264CustomSize(
            inputPath, outputPath, 1920, 720, 30, 8_000_000,
            trimStartSec, maxDurationSec, cropMode, panX, panY, collectPreviewFrames, ct);

    public static (int DurationMs, List<BitmapSource> Sampled960Frames) TranscodeToSilentH264CustomSize(
        string inputPath,
        string outputPath,
        int outW,
        int outH,
        int fps,
        uint bitrateBps,
        double trimStartSec,
        double maxDurationSec,
        ImageCropMode cropMode,
        double panX,
        double panY,
        bool collectPreviewFrames,
        CancellationToken ct)
    {
        CheckHr(MFStartup(MF_VERSION, 0), "MFStartup");
        IntPtr readerAttr = IntPtr.Zero;
        IntPtr writerAttr = IntPtr.Zero;
        IntPtr reader = IntPtr.Zero;
        IntPtr writer = IntPtr.Zero;

        var sampled = new List<BitmapSource>();
        try
        {
            CheckHr(MFCreateAttributes(out readerAttr, 1), "MFCreateAttributes(reader)");
            SetAttrUInt32(readerAttr, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            CheckHr(MFCreateSourceReaderFromURL(inputPath, readerAttr, out reader), "MFCreateSourceReaderFromURL");

            // Request decoded RGB32 frames from first video stream
            CheckHr(MFCreateMediaType(out IntPtr decType), "MFCreateMediaType(dec)");
            try
            {
                SetAttrGuid(decType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetAttrGuid(decType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                CheckHr(SourceReader_SetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, decType), "SetCurrentMediaType(RGB32)");
            }
            finally
            {
                Marshal.Release(decType);
            }

            CheckHr(SourceReader_GetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, out IntPtr actualType), "GetCurrentMediaType");
            int srcW, srcH;
            try
            {
                ulong wh = GetAttrUInt64(actualType, MF_MT_FRAME_SIZE);
                srcW = (int)(wh >> 32);
                srcH = (int)(wh & 0xFFFFFFFF);
            }
            finally
            {
                Marshal.Release(actualType);
            }

            if (srcW <= 0 || srcH <= 0)
                throw new InvalidOperationException("Could not determine input video resolution.");

            if (File.Exists(outputPath)) File.Delete(outputPath);
            CheckHr(MFCreateAttributes(out writerAttr, 1), "MFCreateAttributes(writer)");
            SetAttrUInt32(writerAttr, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            CheckHr(MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, writerAttr, out writer), "MFCreateSinkWriterFromURL");

            CheckHr(MFCreateMediaType(out IntPtr outType), "MFCreateMediaType(out)");
            int streamIndex;
            try
            {
                SetAttrGuid(outType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetAttrGuid(outType, MF_MT_SUBTYPE, MFVideoFormat_H264);
                SetAttrUInt32(outType, MF_MT_AVG_BITRATE, bitrateBps);
                SetAttrUInt32(outType, MF_MT_INTERLACE_MODE, 2); // Progressive
                SetAttrUInt64(outType, MF_MT_FRAME_SIZE, ((ulong)outW << 32) | (uint)outH);
                SetAttrUInt64(outType, MF_MT_FRAME_RATE, ((ulong)fps << 32) | 1u);
                SetAttrUInt64(outType, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1u);
                CheckHr(SinkWriter_AddStream(writer, outType, out streamIndex), "SinkWriter.AddStream");
            }
            finally
            {
                Marshal.Release(outType);
            }

            CheckHr(MFCreateMediaType(out IntPtr inType), "MFCreateMediaType(in)");
            try
            {
                SetAttrGuid(inType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetAttrGuid(inType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                SetAttrUInt32(inType, MF_MT_INTERLACE_MODE, 2);
                SetAttrUInt64(inType, MF_MT_FRAME_SIZE, ((ulong)outW << 32) | (uint)outH);
                SetAttrUInt64(outTypeOrIn: inType, MF_MT_FRAME_RATE, ((ulong)fps << 32) | 1u);
                SetAttrUInt64(inType, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1u);
                CheckHr(SinkWriter_SetInputMediaType(writer, streamIndex, inType), "SinkWriter.SetInputMediaType");
            }
            finally
            {
                Marshal.Release(inType);
            }

            CheckHr(SinkWriter_BeginWriting(writer), "SinkWriter.BeginWriting");

            long trimStartHns = (long)(Math.Max(0, trimStartSec) * 10_000_000.0);
            long maxDurHns = maxDurationSec > 0.1 ? (long)(maxDurationSec * 10_000_000.0) : 150_000_000L;
            long frameDurHns = 10_000_000L / fps;
            long nextOutPts = 0;
            long? basePts = null;
            byte[] srcBytes = new byte[srcW * srcH * 4];
            byte[] dstBytes = new byte[outW * outH * 4];
            int outFrameIdx = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                CheckHr(SourceReader_ReadSample(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out int flags, out long tsHns, out IntPtr sample), "ReadSample");
                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    if (sample != IntPtr.Zero) Marshal.Release(sample);
                    break;
                }
                if (sample == IntPtr.Zero) continue;

                try
                {
                    if (tsHns < trimStartHns) continue;
                    basePts ??= tsHns;
                    long relPts = tsHns - basePts.Value;
                    if (relPts > maxDurHns) break;
                    if (relPts + frameDurHns / 2 < nextOutPts) continue;

                    CheckHr(Sample_ConvertToContiguousBuffer(sample, out IntPtr mediaBuf), "ConvertToContiguousBuffer");
                    try
                    {
                        CheckHr(Buffer_Lock(mediaBuf, out IntPtr pScan, out _, out int curLen), "Buffer.Lock");
                        try
                        {
                            int copyLen = Math.Min(curLen, srcBytes.Length);
                            Marshal.Copy(pScan, srcBytes, 0, copyLen);
                        }
                        finally
                        {
                            Buffer_Unlock(mediaBuf);
                        }
                    }
                    finally
                    {
                        Marshal.Release(mediaBuf);
                    }

                    // Convert via WPF high-quality scaler to 1920x720 (and optional 960x360 every 3rd frame)
                    var srcBmp = BitmapSource.Create(srcW, srcH, 96, 96, PixelFormats.Bgr32, null, srcBytes, srcW * 4);
                    srcBmp.Freeze();
                    BitmapSource scaled1920 = MediaProcessorService.RenderCroppedOrFitted(srcBmp, outW, outH, cropMode, panX, panY);

                    if (collectPreviewFrames && outFrameIdx % 3 == 0 && sampled.Count < 80)
                    {
                        sampled.Add(MediaProcessorService.RenderCroppedOrFitted(srcBmp, 960, 360, cropMode, panX, panY));
                    }

                    // Media Foundation RGB32 uncompressed video uses bottom-up scanlines by default unless negative stride;
                    // check if SourceReader gave top-down BGR32 and write matching scanlines:
                    scaled1920.CopyPixels(dstBytes, outW * 4, 0);
                    FlipVerticalInPlace(dstBytes, outW * 4, outH);

                    WriteRgbSample(writer, streamIndex, dstBytes, nextOutPts, frameDurHns);
                    nextOutPts += frameDurHns;
                    outFrameIdx++;
                }
                finally
                {
                    Marshal.Release(sample);
                }
            }

            CheckHr(SinkWriter_Finalize(writer), "SinkWriter.Finalize");
            int totalMs = (int)(nextOutPts / 10_000L);
            return (totalMs, sampled);
        }
        finally
        {
            if (writer != IntPtr.Zero) Marshal.Release(writer);
            if (reader != IntPtr.Zero) Marshal.Release(reader);
            if (writerAttr != IntPtr.Zero) Marshal.Release(writerAttr);
            if (readerAttr != IntPtr.Zero) Marshal.Release(readerAttr);
            MFShutdown();
        }
    }

    public static BitmapSource? ExtractSingleRawFrame(string inputPath, double timeSec)
    {
        if (MFStartup(MF_VERSION, 0) < 0) return null;
        IntPtr readerAttr = IntPtr.Zero;
        IntPtr reader = IntPtr.Zero;
        try
        {
            if (MFCreateAttributes(out readerAttr, 1) < 0) return null;
            SetAttrUInt32(readerAttr, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            if (MFCreateSourceReaderFromURL(inputPath, readerAttr, out reader) < 0) return null;

            CheckHr(MFCreateMediaType(out IntPtr decType), "MFCreateMediaType(dec)");
            try
            {
                SetAttrGuid(decType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetAttrGuid(decType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                if (SourceReader_SetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, decType) < 0)
                    return null;
            }
            finally
            {
                Marshal.Release(decType);
            }

            if (SourceReader_GetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, out IntPtr actualType) < 0)
                return null;
            int srcW, srcH;
            try
            {
                ulong wh = GetAttrUInt64(actualType, MF_MT_FRAME_SIZE);
                srcW = (int)(wh >> 32);
                srcH = (int)(wh & 0xFFFFFFFF);
            }
            finally
            {
                Marshal.Release(actualType);
            }
            if (srcW <= 0 || srcH <= 0) return null;

            long targetHns = (long)(Math.Max(0, timeSec) * 10_000_000.0);
            byte[] srcBytes = new byte[srcW * srcH * 4];
            BitmapSource? lastFrame = null;

            for (int guard = 0; guard < 180; guard++)
            {
                if (SourceReader_ReadSample(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out int flags, out long tsHns, out IntPtr sample) < 0)
                    break;
                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    if (sample != IntPtr.Zero) Marshal.Release(sample);
                    break;
                }
                if (sample == IntPtr.Zero) continue;
                try
                {
                    if (tsHns >= targetHns || lastFrame == null)
                    {
                        if (Sample_ConvertToContiguousBuffer(sample, out IntPtr mediaBuf) >= 0)
                        {
                            try
                            {
                                if (Buffer_Lock(mediaBuf, out IntPtr pScan, out _, out int curLen) >= 0)
                                {
                                    try
                                    {
                                        Marshal.Copy(pScan, srcBytes, 0, Math.Min(curLen, srcBytes.Length));
                                        var bmp = BitmapSource.Create(srcW, srcH, 96, 96, PixelFormats.Bgr32, null, srcBytes, srcW * 4);
                                        bmp.Freeze();
                                        lastFrame = bmp;
                                    }
                                    finally
                                    {
                                        Buffer_Unlock(mediaBuf);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.Release(mediaBuf);
                            }
                        }
                        if (tsHns >= targetHns) break;
                    }
                }
                finally
                {
                    Marshal.Release(sample);
                }
            }

            return lastFrame;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (reader != IntPtr.Zero) Marshal.Release(reader);
            if (readerAttr != IntPtr.Zero) Marshal.Release(readerAttr);
            MFShutdown();
        }
    }

    private static void FlipVerticalInPlace(byte[] buf, int stride, int height)
    {
        byte[] rowTmp = new byte[stride];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bot = (height - 1 - y) * stride;
            Buffer.BlockCopy(buf, top, rowTmp, 0, stride);
            Buffer.BlockCopy(buf, bot, buf, top, stride);
            Buffer.BlockCopy(rowTmp, 0, buf, bot, stride);
        }
    }

    private static void WriteRgbSample(IntPtr writer, int streamIndex, byte[] data, long ptsHns, long durHns)
    {
        CheckHr(MFCreateMemoryBuffer(data.Length, out IntPtr buf), "MFCreateMemoryBuffer");
        try
        {
            CheckHr(Buffer_Lock(buf, out IntPtr ptr, out _, out _), "OutBuffer.Lock");
            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
            }
            finally
            {
                Buffer_Unlock(buf);
            }
            CheckHr(Buffer_SetCurrentLength(buf, data.Length), "SetCurrentLength");
            CheckHr(MFCreateSample(out IntPtr sample), "MFCreateSample");
            try
            {
                CheckHr(Sample_AddBuffer(sample, buf), "AddBuffer");
                CheckHr(Sample_SetSampleTime(sample, ptsHns), "SetSampleTime");
                CheckHr(Sample_SetSampleDuration(sample, durHns), "SetSampleDuration");
                CheckHr(SinkWriter_WriteSample(writer, streamIndex, sample), "WriteSample");
            }
            finally
            {
                Marshal.Release(sample);
            }
        }
        finally
        {
            Marshal.Release(buf);
        }
    }

    private static void CheckHr(int hr, string op)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // Minimal vtable COM calls for IMFAttributes / IMFSourceReader / IMFSinkWriter / IMFSample / IMFMediaBuffer
    private static unsafe void SetAttrUInt32(IntPtr attr, Guid key, uint val)
    {
        var vt = *(IntPtr**)attr;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)vt[21];
        CheckHr(fn(attr, &key, val), "IMFAttributes.SetUINT32");
    }

    private static unsafe void SetAttrUInt64(IntPtr outTypeOrIn, Guid key, ulong val)
    {
        var vt = *(IntPtr**)outTypeOrIn;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)vt[22];
        CheckHr(fn(outTypeOrIn, &key, val), "IMFAttributes.SetUINT64");
    }

    private static unsafe ulong GetAttrUInt64(IntPtr attr, Guid key)
    {
        ulong val = 0;
        var vt = *(IntPtr**)attr;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)vt[8];
        CheckHr(fn(attr, &key, &val), "IMFAttributes.GetUINT64");
        return val;
    }

    private static unsafe void SetAttrGuid(IntPtr attr, Guid key, Guid val)
    {
        var vt = *(IntPtr**)attr;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)vt[24];
        CheckHr(fn(attr, &key, &val), "IMFAttributes.SetGUID");
    }

    private static unsafe int SourceReader_GetCurrentMediaType(IntPtr reader, int idx, out IntPtr mediaType)
    {
        IntPtr mt = IntPtr.Zero;
        var vt = *(IntPtr**)reader;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)vt[6];
        int hr = fn(reader, idx, &mt);
        mediaType = mt;
        return hr;
    }

    private static unsafe int SourceReader_SetCurrentMediaType(IntPtr reader, int idx, IntPtr mediaType)
    {
        var vt = *(IntPtr**)reader;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int>)vt[7];
        return fn(reader, idx, IntPtr.Zero, mediaType);
    }

    private static unsafe int SourceReader_ReadSample(IntPtr reader, int idx, int ctrl, out int actualIdx, out int flags, out long ts, out IntPtr sample)
    {
        int a = 0, f = 0;
        long t = 0;
        IntPtr s = IntPtr.Zero;
        var vt = *(IntPtr**)reader;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int, int*, int*, long*, IntPtr*, int>)vt[9];
        int hr = fn(reader, idx, ctrl, &a, &f, &t, &s);
        actualIdx = a; flags = f; ts = t; sample = s;
        return hr;
    }

    private static unsafe int SinkWriter_AddStream(IntPtr writer, IntPtr targetType, out int idx)
    {
        int i = 0;
        var vt = *(IntPtr**)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)vt[3];
        int hr = fn(writer, targetType, &i);
        idx = i;
        return hr;
    }

    private static unsafe int SinkWriter_SetInputMediaType(IntPtr writer, int idx, IntPtr inputType)
    {
        var vt = *(IntPtr**)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int>)vt[4];
        return fn(writer, idx, inputType, IntPtr.Zero);
    }

    private static unsafe int SinkWriter_BeginWriting(IntPtr writer)
    {
        var vt = *(IntPtr**)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vt[5];
        return fn(writer);
    }

    private static unsafe int SinkWriter_WriteSample(IntPtr writer, int idx, IntPtr sample)
    {
        var vt = *(IntPtr**)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)vt[6];
        return fn(writer, idx, sample);
    }

    private static unsafe int SinkWriter_Finalize(IntPtr writer)
    {
        var vt = *(IntPtr**)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vt[11];
        return fn(writer);
    }

    private static unsafe int Sample_SetSampleTime(IntPtr sample, long hns)
    {
        var vt = *(IntPtr**)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vt[36];
        return fn(sample, hns);
    }

    private static unsafe int Sample_SetSampleDuration(IntPtr sample, long hns)
    {
        var vt = *(IntPtr**)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vt[38];
        return fn(sample, hns);
    }

    private static unsafe int Sample_AddBuffer(IntPtr sample, IntPtr buf)
    {
        var vt = *(IntPtr**)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[42];
        return fn(sample, buf);
    }

    private static unsafe int Sample_ConvertToContiguousBuffer(IntPtr sample, out IntPtr buf)
    {
        IntPtr b = IntPtr.Zero;
        var vt = *(IntPtr**)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vt[41];
        int hr = fn(sample, &b);
        buf = b;
        return hr;
    }

    private static unsafe int Buffer_Lock(IntPtr buf, out IntPtr pScan, out int maxLen, out int curLen)
    {
        IntPtr p = IntPtr.Zero;
        int m = 0, c = 0;
        var vt = *(IntPtr**)buf;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int*, int*, int>)vt[3];
        int hr = fn(buf, &p, &m, &c);
        pScan = p; maxLen = m; curLen = c;
        return hr;
    }

    private static unsafe int Buffer_Unlock(IntPtr buf)
    {
        var vt = *(IntPtr**)buf;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vt[4];
        return fn(buf);
    }

    private static unsafe int Buffer_SetCurrentLength(IntPtr buf, int len)
    {
        var vt = *(IntPtr**)buf;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vt[6];
        return fn(buf, len);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string pwszURL, IntPtr pAttributes, out IntPtr ppSourceReader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string pwszOutputURL, IntPtr pByteStream, IntPtr pAttributes, out IntPtr ppSinkWriter);
}
