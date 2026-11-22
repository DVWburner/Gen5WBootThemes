using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BootThemeStudio.Models;

namespace BootThemeStudio.Services;

public sealed class LoadedBootThemeProject
{
    public required BootThemeManifest Manifest { get; init; }
    public byte[]? Splash1920Bytes { get; init; }
    public byte[]? Splash1280Bytes { get; init; }
    public byte[]? PreviewOnBootBytes { get; init; }
    public string? ExtractedGoodbyeMp4Path { get; init; }
    public string? ExtractedWelcomeMp4Path { get; init; }
    public byte[]? PreviewPowerOffBytes { get; init; }
    public string PreviewPowerOffExt { get; init; } = ".gif";
    public byte[]? Poster960Bytes { get; init; }
}

/// <summary>
/// Builds, inspects, and validates Boot Theme `.zip` archives (`<id>_v<version>_r<revision>.zip`)
/// against every rule of the v1.1 `market.ioniqthemes.com` Boot Themes specification,
/// and stages USB drives with the embedded `Boot Themes` NEMESIS installer + `/BootThemes/` folder.
/// </summary>
public static partial class BootThemePackager
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdSlugRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._+-]{0,31}$")]
    private static partial Regex VersionRegex();

    public static string SanitizeSlug(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var sb = new StringBuilder(input.Length);
        bool lastDash = false;
        foreach (char raw in input.Trim().ToLowerInvariant())
        {
            if ((raw >= 'a' && raw <= 'z') || (raw >= '0' && raw <= '9'))
            {
                sb.Append(raw);
                lastDash = false;
            }
            else if (raw is '-' or '_' or ' ' or '.')
            {
                if (sb.Length > 0 && !lastDash)
                {
                    sb.Append(raw == '_' ? '_' : '-');
                    lastDash = true;
                }
            }
            if (sb.Length >= 64) break;
        }
        return sb.ToString().TrimEnd('-', '_');
    }

    public static List<string> ValidateMetadata(BootThemeManifest m)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(m.Id) || !IdSlugRegex().IsMatch(m.Id))
            errs.Add("ID must be 1-64 lowercase characters matching [a-z0-9][a-z0-9_-]* (e.g. 'ioniq-neon-wave').");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Trim().Length > 120)
            errs.Add("Theme Name is required (1-120 characters).");
        if (string.IsNullOrWhiteSpace(m.Author) || m.Author.Trim().Length > 100)
            errs.Add("Creator Display Name (Author) is required (1-100 characters).");
        if (string.IsNullOrWhiteSpace(m.Version) || m.Version.Contains("..") || !VersionRegex().IsMatch(m.Version))
            errs.Add("Version must match e.g. '1.0.0' (letters, digits, '.', '_', '+', '-').");
        if (m.Revision < 1 || m.Revision > 1_000_000)
            errs.Add("Revision must be between 1 and 1,000,000.");
        if (!m.HasOnBoot && !m.HasPowerOff)
            errs.Add("At least one part (Boot Image or Goodbye Video) must be enabled.");
        if (!m.HasPowerOff && m.HasWelcomeVideo)
            errs.Add("Separate Welcome Video requires Goodbye Video (Power Off) to be enabled.");
        if (m.Description != null && m.Description.Length > 2000)
            errs.Add("Description must be 2,000 characters or fewer.");
        return errs;
    }

    public static string GetCanonicalZipFileName(BootThemeManifest m) =>
        $"{m.Id}_v{m.Version}_r{m.Revision}.zip";

    public static void BuildBootThemeZip(
        string outputZipPath,
        BootThemeManifest manifest,
        ProcessedBootImageAssets? bootAssets,
        ProcessedVideoAssets? videoAssets)
    {
        var metaErrs = ValidateMetadata(manifest);
        if (metaErrs.Count > 0)
            throw new InvalidOperationException(string.Join("\n", metaErrs));

        if (manifest.HasOnBoot && bootAssets == null)
            throw new InvalidOperationException("Boot Image is enabled, but no Boot Image has been loaded.");
        if (manifest.HasPowerOff && (videoAssets == null || !File.Exists(videoAssets.GoodbyeMp4Path)))
            throw new InvalidOperationException("Goodbye Video is enabled, but no video has been converted yet.");

        manifest.HasWelcomeVideo = manifest.HasPowerOff
                                   && videoAssets?.WelcomeMp4Path != null
                                   && File.Exists(videoAssets.WelcomeMp4Path);
        manifest.PowerOffDurationMs = manifest.HasPowerOff ? videoAssets?.DurationMs : null;
        manifest.FormatVersion = 1;
        manifest.Resolution = "1920x720";

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });

        string? dir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        string tmpZip = outputZipPath + ".tmp";
        if (File.Exists(tmpZip)) File.Delete(tmpZip);

        using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteBytesEntry(za, "boottheme.json", manifestBytes, CompressionLevel.Optimal);

            if (manifest.HasOnBoot && bootAssets != null)
            {
                WriteBytesEntry(za, "preview_onboot.jpg", bootAssets.Preview960Jpg, CompressionLevel.NoCompression);
                WriteBytesEntry(za, "onboot/splash_1920x720.jpg", bootAssets.Splash1920Jpg, CompressionLevel.NoCompression);
                WriteBytesEntry(za, "onboot/splash_1280x720.jpg", bootAssets.Splash1280Jpg, CompressionLevel.NoCompression);
            }

            if (manifest.HasPowerOff && videoAssets != null)
            {
                string prevName = videoAssets.PreviewExtension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? "preview_poweroff.mp4"
                    : "preview_poweroff.gif";
                WriteBytesEntry(za, prevName, videoAssets.PreviewGifOrMp4Bytes, CompressionLevel.NoCompression);
                WriteBytesEntry(za, "preview_poweroff_poster.jpg", videoAssets.Poster960Jpg, CompressionLevel.NoCompression);
                WriteFileEntry(za, "poweroff/goodbye_1920x720.mp4", videoAssets.GoodbyeMp4Path, CompressionLevel.NoCompression);
                if (manifest.HasWelcomeVideo && !string.IsNullOrWhiteSpace(videoAssets.WelcomeMp4Path))
                {
                    WriteFileEntry(za, "poweroff/welcome_1920x720.mp4", videoAssets.WelcomeMp4Path!, CompressionLevel.NoCompression);
                }
            }
        }

        // Run strict v1.1 audit on the built zip before committing!
        var audit = ValidateZipArchive(tmpZip);
        if (!audit.IsValid)
        {
            try { File.Delete(tmpZip); } catch { }
            throw new InvalidOperationException("Built archive failed v1.1 Store audit:\n• " + string.Join("\n• ", audit.Errors));
        }

        File.Move(tmpZip, outputZipPath, overwrite: true);
    }

    public static PackageValidationResult ValidateZipArchive(string zipPath)
    {
        var res = new PackageValidationResult();
        var fi = new FileInfo(zipPath);
        if (!fi.Exists || fi.Length < 32)
        {
            res.Errors.Add("Archive file is missing or empty.");
            return res;
        }
        if (fi.Length > StudioConstants.MaxTotalZipBytes)
            res.Errors.Add($"Archive size ({fi.Length / (1024.0 * 1024.0):0.##} MB) exceeds 60 MB server limit.");

        byte[] magic = new byte[4];
        using (var mfs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (mfs.Read(magic, 0, 4) < 4 || magic[0] != 0x50 || magic[1] != 0x4B || magic[2] != 0x03 || magic[3] != 0x04)
            {
                res.Errors.Add("File does not start with ZIP PK\\x03\\x04 header.");
                return res;
            }
        }

        using var za = ZipFile.OpenRead(zipPath);
        var entriesByName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalUncomp = 0;

        foreach (var e in za.Entries)
        {
            string norm = e.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(norm) || norm.EndsWith('/')) continue;
            if (IsHarmlessOsMetadata(norm)) continue;

            if (norm.StartsWith('/') || norm.Contains("..") || norm.Contains(':'))
            {
                res.Errors.Add($"Unsafe path in archive: {norm}");
                continue;
            }
            if (!entriesByName.TryAdd(norm, e))
            {
                res.Errors.Add($"Duplicate path in archive: {norm}");
                continue;
            }

            totalUncomp += e.Length;
            if (e.Length > StudioConstants.MaxSingleEntryBytes)
                res.Errors.Add($"{norm} exceeds 40 MB single-file limit.");
            if (norm.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                res.Errors.Add($".webp files are disallowed for Android 4.4.2 compatibility ({norm}).");
            if (norm.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || norm.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                || norm.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("desc.txt", StringComparison.OrdinalIgnoreCase)
                || norm.EndsWith("/desc.txt", StringComparison.OrdinalIgnoreCase))
            {
                res.Errors.Add($"Forbidden file in Boot Theme package: {norm}");
            }
        }

        res.FileCount = entriesByName.Count;
        res.TotalUncompressedBytes = totalUncomp;

        if (res.FileCount > StudioConstants.MaxNonOsFiles)
            res.Errors.Add($"Too many files ({res.FileCount} > {StudioConstants.MaxNonOsFiles}).");
        if (totalUncomp > StudioConstants.MaxTotalZipBytes)
            res.Errors.Add($"Total uncompressed size ({totalUncomp / (1024.0 * 1024.0):0.##} MB) exceeds 60 MB limit.");

        if (!entriesByName.TryGetValue("boottheme.json", out var manifestEntry))
        {
            res.Errors.Add("Missing root boottheme.json manifest.");
            return res;
        }

        BootThemeManifest? manifest;
        using (var s = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<BootThemeManifest>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        if (manifest == null)
        {
            res.Errors.Add("Invalid JSON in boottheme.json.");
            return res;
        }

        res.Errors.AddRange(ValidateMetadata(manifest));

        if (manifest.HasOnBoot)
        {
            CheckImageEntry(entriesByName, "preview_onboot", 960, 360, StudioConstants.MaxStaticPreviewBytes, required: true, res);
            CheckImageEntry(entriesByName, "onboot/splash_1920x720", 1920, 720, StudioConstants.MaxSplashBytes, required: true, res, checkJpuWarn: true);
            CheckImageEntry(entriesByName, "onboot/splash_1280x720", 1280, 720, StudioConstants.MaxSplashBytes, required: false, res, checkJpuWarn: true);
        }
        else
        {
            if (entriesByName.Keys.Any(k => k.StartsWith("onboot/", StringComparison.OrdinalIgnoreCase) || k.StartsWith("preview_onboot.", StringComparison.OrdinalIgnoreCase)))
                res.Errors.Add("hasOnBoot is false, so onboot/ and preview_onboot.* must not be present.");
        }

        if (manifest.HasPowerOff)
        {
            CheckImageEntry(entriesByName, "preview_poweroff_poster", 960, 360, StudioConstants.MaxStaticPreviewBytes, required: true, res);
            bool hasPrevGif = entriesByName.TryGetValue("preview_poweroff.gif", out var prevGif);
            bool hasPrevMp4 = entriesByName.TryGetValue("preview_poweroff.mp4", out var prevMp4);
            if (!hasPrevGif && !hasPrevMp4)
                res.Errors.Add("Missing preview_poweroff.gif or preview_poweroff.mp4.");
            else if (hasPrevGif && hasPrevMp4)
                res.Errors.Add("Include either preview_poweroff.gif OR preview_poweroff.mp4, not both.");
            else if (hasPrevGif && prevGif!.Length > StudioConstants.MaxPowerOffPreviewBytes)
                res.Errors.Add("preview_poweroff.gif exceeds 10 MB limit.");
            else if (hasPrevMp4)
            {
                if (prevMp4!.Length > StudioConstants.MaxPowerOffPreviewBytes)
                    res.Errors.Add("preview_poweroff.mp4 exceeds 10 MB limit.");
                CheckMp4Entry(entriesByName, "preview_poweroff.mp4", required: true, res, expectedW: 960, expectedH: 360, maxBytes: StudioConstants.MaxPowerOffPreviewBytes);
            }

            CheckMp4Entry(entriesByName, "poweroff/goodbye_1920x720.mp4", required: true, res, expectedW: 1920, expectedH: 720, maxBytes: StudioConstants.MaxPayloadMp4Bytes);
            CheckMp4Entry(entriesByName, "poweroff/welcome_1920x720.mp4", required: false, res, expectedW: 1920, expectedH: 720, maxBytes: StudioConstants.MaxPayloadMp4Bytes);
        }
        else
        {
            if (entriesByName.Keys.Any(k => k.StartsWith("poweroff/", StringComparison.OrdinalIgnoreCase) || k.StartsWith("preview_poweroff", StringComparison.OrdinalIgnoreCase)))
                res.Errors.Add("hasPowerOff is false, so poweroff/ and preview_poweroff* must not be present.");
        }

        return res;
    }

    public static LoadedBootThemeProject LoadProjectFromZip(string zipPath, string extractTempDir)
    {
        Directory.CreateDirectory(extractTempDir);
        using var za = ZipFile.OpenRead(zipPath);

        ZipArchiveEntry? manifestEntry = za.Entries.FirstOrDefault(e =>
            e.FullName.Equals("boottheme.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry == null)
            throw new InvalidDataException("Archive does not contain boottheme.json.");

        BootThemeManifest manifest;
        using (var s = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<BootThemeManifest>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new InvalidDataException("Could not parse boottheme.json.");
        }

        byte[]? ReadMatch(string prefix)
        {
            var hit = za.Entries.FirstOrDefault(e =>
            {
                string n = e.FullName.Replace('\\', '/');
                return n.Equals(prefix + ".jpg", StringComparison.OrdinalIgnoreCase)
                       || n.Equals(prefix + ".jpeg", StringComparison.OrdinalIgnoreCase)
                       || n.Equals(prefix + ".png", StringComparison.OrdinalIgnoreCase);
            });
            if (hit == null) return null;
            using var s = hit.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        string? ExtractEntryToDisk(string exactName, string outFileName)
        {
            var hit = za.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(exactName, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            string dst = Path.Combine(extractTempDir, outFileName);
            hit.ExtractToFile(dst, overwrite: true);
            return dst;
        }

        byte[]? prevOffBytes = null;
        string prevOffExt = ".gif";
        var gifEntry = za.Entries.FirstOrDefault(e => e.FullName.Equals("preview_poweroff.gif", StringComparison.OrdinalIgnoreCase));
        var mp4PrevEntry = za.Entries.FirstOrDefault(e => e.FullName.Equals("preview_poweroff.mp4", StringComparison.OrdinalIgnoreCase));
        if (gifEntry != null)
        {
            using var s = gifEntry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            prevOffBytes = ms.ToArray();
            prevOffExt = ".gif";
        }
        else if (mp4PrevEntry != null)
        {
            using var s = mp4PrevEntry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            prevOffBytes = ms.ToArray();
            prevOffExt = ".mp4";
        }

        return new LoadedBootThemeProject
        {
            Manifest = manifest,
            Splash1920Bytes = ReadMatch("onboot/splash_1920x720"),
            Splash1280Bytes = ReadMatch("onboot/splash_1280x720"),
            PreviewOnBootBytes = ReadMatch("preview_onboot"),
            ExtractedGoodbyeMp4Path = ExtractEntryToDisk("poweroff/goodbye_1920x720.mp4", "loaded_goodbye_1920x720.mp4"),
            ExtractedWelcomeMp4Path = ExtractEntryToDisk("poweroff/welcome_1920x720.mp4", "loaded_welcome_1920x720.mp4"),
            PreviewPowerOffBytes = prevOffBytes,
            PreviewPowerOffExt = prevOffExt,
            Poster960Bytes = ReadMatch("preview_poweroff_poster")
        };
    }

    /// <summary>
    /// Stages the embedded `gen5w.boot.themes-1.0.0` NEMESIS installer onto the root of a USB drive
    /// AND optionally copies a built Boot Theme `.zip` into `<drive>\BootThemes\`.
    /// Leaves all unrelated folders on the USB stick untouched!
    /// </summary>
    public static List<string> StageUsbDrive(string driveRoot, string? optionalThemeZipPath, bool includeNemesisInstaller)
    {
        var written = new List<string>();
        if (!Directory.Exists(driveRoot))
            throw new DirectoryNotFoundException($"Drive '{driveRoot}' is not accessible.");

        if (includeNemesisInstaller)
        {
            using Stream? resStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("BootThemeStudio.Assets.gen5w-bootthemes-usb.zip");
            if (resStream == null)
                throw new InvalidOperationException("Embedded Boot Themes NEMESIS USB bundle resource was not found.");

            using var za = new ZipArchive(resStream, ZipArchiveMode.Read);
            foreach (var entry in za.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string dst = Path.Combine(driveRoot, entry.Name);
                entry.ExtractToFile(dst, overwrite: true);
                written.Add(dst);
            }
        }

        string themesFolder = Path.Combine(driveRoot, "BootThemes");
        Directory.CreateDirectory(themesFolder);

        if (!string.IsNullOrWhiteSpace(optionalThemeZipPath) && File.Exists(optionalThemeZipPath))
        {
            string dstTheme = Path.Combine(themesFolder, Path.GetFileName(optionalThemeZipPath));
            File.Copy(optionalThemeZipPath, dstTheme, overwrite: true);
            written.Add(dstTheme);
        }

        return written;
    }

    private static void CheckImageEntry(
        Dictionary<string, ZipArchiveEntry> map,
        string basePathNoExt,
        int expectedW,
        int expectedH,
        long maxBytes,
        bool required,
        PackageValidationResult res,
        bool checkJpuWarn = false)
    {
        var matches = new[] { ".jpg", ".jpeg", ".png" }
            .Select(ext => basePathNoExt + ext)
            .Where(map.ContainsKey)
            .ToList();

        if (matches.Count == 0)
        {
            if (required) res.Errors.Add($"Missing required image: {basePathNoExt}.jpg");
            return;
        }
        if (matches.Count > 1)
        {
            res.Errors.Add($"Multiple formats present for {basePathNoExt} ({string.Join(", ", matches)}).");
            return;
        }

        var entry = map[matches[0]];
        if (entry.Length > maxBytes)
            res.Errors.Add($"{matches[0]} exceeds {maxBytes / (1024.0 * 1024.0):0.#} MB limit.");

        byte[] raw;
        using (var s = entry.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            raw = ms.ToArray();
        }

        try
        {
            var bmp = MediaProcessorService.DecodeBitmapFromBytes(raw);
            if (bmp.PixelWidth != expectedW || bmp.PixelHeight != expectedH)
                res.Errors.Add($"{matches[0]} dimensions are {bmp.PixelWidth}x{bmp.PixelHeight} (expected {expectedW}x{expectedH}).");
            if (checkJpuWarn && !BaselineJpeg444Encoder.IsValidJpu444Jpeg(raw, expectedW, expectedH))
                res.Warnings.Add($"{matches[0]} is not pre-encoded as Baseline 4:4:4 <=450KB (the car app will re-encode on apply).");
        }
        catch (Exception ex)
        {
            res.Errors.Add($"Cannot decode image {matches[0]}: {ex.Message}");
        }
    }

    private static void CheckMp4Entry(
        Dictionary<string, ZipArchiveEntry> map,
        string path,
        bool required,
        PackageValidationResult res,
        int expectedW = 1920,
        int expectedH = 720,
        long maxBytes = StudioConstants.MaxPayloadMp4Bytes)
    {
        if (!map.TryGetValue(path, out var entry))
        {
            if (required) res.Errors.Add($"Missing required video: {path}");
            return;
        }
        if (entry.Length > maxBytes)
            res.Errors.Add($"{path} exceeds {maxBytes / (1024.0 * 1024.0):0.#} MB limit.");

        string tmp = Path.Combine(Path.GetTempPath(), $"bt_audit_{Guid.NewGuid():N}.mp4");
        try
        {
            entry.ExtractToFile(tmp, overwrite: true);
            MediaProcessorService.InspectMp4File(tmp, out int w, out int h, out _, out bool hasAvc1, out bool hasAud, out _);
            if (!hasAvc1) res.Errors.Add($"{path}: Video codec is not H.264 (avc1).");
            if (hasAud) res.Errors.Add($"{path}: Contains an audio ('soun') track (must be silent).");
            if (w != expectedW || h != expectedH) res.Errors.Add($"{path}: Dimensions are {w}x{h} (expected {expectedW}x{expectedH}).");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static bool IsHarmlessOsMetadata(string norm)
    {
        string file = Path.GetFileName(norm);
        return norm.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
               || file.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
               || file.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
               || file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteBytesEntry(ZipArchive za, string entryName, byte[] data, CompressionLevel level)
    {
        var e = za.CreateEntry(entryName, level);
        e.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    private static void WriteFileEntry(ZipArchive za, string entryName, string srcFilePath, CompressionLevel level)
    {
        var e = za.CreateEntry(entryName, level);
        e.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var dst = e.Open();
        using var src = new FileStream(srcFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        src.CopyTo(dst);
    }
}
