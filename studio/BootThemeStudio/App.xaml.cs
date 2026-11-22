using System;
using System.IO;
using System.Windows;
using BootThemeStudio.Models;
using BootThemeStudio.Services;

namespace BootThemeStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless CLI self-test support (`BootThemeStudio.exe --self-test <sampleImg> <sampleMp4> <outZip>`)
        if (e.Args.Length >= 4 && string.Equals(e.Args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                RunSelfTest(e.Args[1], e.Args[2], e.Args[3]);
                Console.WriteLine("[SELF-TEST OK] Built and audited v1.1 Boot Theme package successfully.");
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SELF-TEST FAILED] " + ex);
                Shutdown(2);
                return;
            }
        }

        base.OnStartup(e);
    }

    private static void RunSelfTest(string sampleImgPath, string sampleMp4Path, string outZipPath)
    {
        var bmp = MediaProcessorService.LoadBitmapUnlocked(sampleImgPath);
        var bootAssets = MediaProcessorService.ProcessBootImage(bmp, ImageCropMode.CoverFill, 0, 0, 1.0);
        if (!BaselineJpeg444Encoder.IsValidJpu444Jpeg(bootAssets.Splash1920Jpg, 1920, 720))
            throw new InvalidOperationException("1920x720 JPEG failed 4:4:4 JPU check.");
        if (!BaselineJpeg444Encoder.IsValidJpu444Jpeg(bootAssets.Splash1280Jpg, 1280, 720))
            throw new InvalidOperationException("1280x720 JPEG failed 4:4:4 JPU check.");

        string tmpWork = Path.Combine(Path.GetTempPath(), "bt_studio_selftest_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var vidAssets = MediaProcessorService.ConvertVideosAsync(
                sampleMp4Path, null, tmpWork, 0, 2.0, 0.5, ImageCropMode.CoverFill, panX: 0.0, panY: -0.6).GetAwaiter().GetResult();

            var manifest = new BootThemeManifest
            {
                Id = "studio-selftest-pack",
                Name = "Studio Self-Test Pack",
                Author = "BootThemeStudio",
                Version = "1.0.0",
                Revision = 1,
                Category = "OEM+",
                Description = "Automated .NET 10 Studio verification build.",
                HasOnBoot = true,
                HasPowerOff = true,
                Tags = ["test", "gen5w"]
            };

            // Also verify zero-dependency Windows Media Foundation H.264 fallback!
            MediaProcessorService.CustomFfmpegPath = "__force_mf__";
            // Temporarily test MF transcode on 1.5s clip:
            string mfOut = Path.Combine(tmpWork, "mf_test_1920x720.mp4");
            var (mfDur, mfFrames) = WindowsMediaFoundationTranscoder.TranscodeToSilent1920x720H264(
                sampleMp4Path, mfOut, 0, 1.5, ImageCropMode.CoverFill, 0.0, -0.6, collectPreviewFrames: true, default);
            if (!MediaProcessorService.InspectMp4File(mfOut, out int mfw, out int mfh, out _, out bool mfAvc, out bool mfAud, out string mfErr))
                throw new InvalidOperationException($"Media Foundation MP4 check failed ({mfw}x{mfh} avc={mfAvc} aud={mfAud}): {mfErr}");
            MediaProcessorService.CustomFfmpegPath = null;

            BootThemePackager.BuildBootThemeZip(outZipPath, manifest, bootAssets, vidAssets);
            var audit = BootThemePackager.ValidateZipArchive(outZipPath);
            if (!audit.IsValid)
                throw new InvalidOperationException("Audit failed: " + string.Join("; ", audit.Errors));
        }
        finally
        {
            try { if (Directory.Exists(tmpWork)) Directory.Delete(tmpWork, recursive: true); } catch { }
        }
    }
}
