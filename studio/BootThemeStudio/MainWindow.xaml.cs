using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BootThemeStudio.Models;
using BootThemeStudio.Services;
using Microsoft.Win32;

namespace BootThemeStudio;

public partial class MainWindow : Window
{
    private readonly string _storeUrl = StudioConstants.DefaultStoreUrl;
    private readonly string _sessionWorkDir = Path.Combine(
        Path.GetTempPath(),
        "BootThemeStudio_Work_" + Guid.NewGuid().ToString("N")[..8]);

    private StoredStoreSession? _session;
    private bool _idManuallyEdited;
    private bool _isPlayingPreview;

    private BitmapSource? _rawBootSourceBitmap;
    private ProcessedBootImageAssets? _processedBootAssets;

    private string? _goodbyeInputPath;
    private string? _welcomeInputPath;
    private BitmapSource? _rawVideoSampleFrame;
    private ProcessedVideoAssets? _processedVideoAssets;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_sessionWorkDir);

        foreach (string c in BootThemeStoreClient.CachedOrDefaultCategories)
            CmbCategory.Items.Add(c);
        CmbCategory.SelectedIndex = 0;

        RefreshEncoderLabel();
        UpdatePartStates();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Load shared creator account session (shared with Gen5WStudio via DPAPI!)
        var saved = StoreAuthStorage.LoadSession(_storeUrl);
        if (saved != null && !saved.IsExpired())
        {
            _session = saved;
            if (string.IsNullOrWhiteSpace(TxtAuthor.Text))
                TxtAuthor.Text = saved.Username;
            UpdateAccountUi();

            try
            {
                var me = await BootThemeStoreClient.ValidateSessionAsync(_storeUrl, saved.Token);
                if (me.Ok && me.Account != null)
                {
                    saved.Username = me.Account.Username;
                    StoreAuthStorage.SaveSession(_storeUrl, saved);
                    _session = saved;
                    UpdateAccountUi();
                }
            }
            catch (BootThemeStoreException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                StoreAuthStorage.ClearSession(_storeUrl);
                _session = null;
                UpdateAccountUi();
            }
            catch
            {
                // Offline or transient network error — keep cached session ready
            }
        }
        else
        {
            UpdateAccountUi();
        }

        // 2. Sync live store categories asynchronously
        try
        {
            var liveCats = await BootThemeStoreClient.RefreshCategoriesAsync(_storeUrl);
            if (liveCats.Count > 0)
            {
                string? prev = CmbCategory.SelectedItem as string;
                CmbCategory.Items.Clear();
                foreach (string c in liveCats) CmbCategory.Items.Add(c);
                int idx = prev != null ? CmbCategory.Items.IndexOf(prev) : 0;
                CmbCategory.SelectedIndex = idx >= 0 ? idx : 0;
            }
        }
        catch { }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        StopMediaPreview();
        try
        {
            if (Directory.Exists(_sessionWorkDir))
                Directory.Delete(_sessionWorkDir, recursive: true);
        }
        catch { }
    }

    private void RefreshEncoderLabel()
    {
        string? ff = MediaProcessorService.FindFfmpegExecutable();
        TxtEncoderInfo.Text = ff != null
            ? $"Video engine: FFmpeg ({Path.GetFileName(ff)}) + JPU 4:4:4 JPEG encoder."
            : "Video engine: Built-in Windows Media Foundation H.264 + JPU 4:4:4 JPEG encoder (zero external tools needed).";
    }

    private void UpdateAccountUi()
    {
        if (_session != null)
        {
            TxtAccountStatus.Text = $"Signed in: @{_session.Username}";
            BtnSignIn.Content = "Switch Account";
            BtnSignOut.Visibility = Visibility.Visible;
        }
        else
        {
            TxtAccountStatus.Text = "Not signed in (shared with Launcher Studio)";
            BtnSignIn.Content = "Sign In / Register";
            BtnSignOut.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdatePartStates()
    {
        if (CardOnBoot == null || CardPowerOff == null) return;
        bool onBoot = ChkHasOnBoot.IsChecked == true;
        bool powerOff = ChkHasPowerOff.IsChecked == true;
        bool sepWelcome = powerOff && ChkSeparateWelcome.IsChecked == true;

        CardOnBoot.Opacity = onBoot ? 1.0 : 0.42;
        CardOnBoot.IsEnabled = onBoot;

        CardPowerOff.Opacity = powerOff ? 1.0 : 0.42;
        CardPowerOff.IsEnabled = powerOff;
        ChkSeparateWelcome.IsEnabled = powerOff;
        PanelWelcomePicker.Visibility = sepWelcome ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PartToggle_Changed(object sender, RoutedEventArgs e) => UpdatePartStates();

    private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtId == null || _idManuallyEdited) return;
        string slug = BootThemePackager.SanitizeSlug(TxtName.Text);
        if (!string.IsNullOrEmpty(slug))
            TxtId.Text = slug;
    }

    private void TxtId_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtId != null && TxtId.IsKeyboardFocused)
            _idManuallyEdited = true;
    }

    private void BtnBumpRev_Click(object sender, RoutedEventArgs e)
    {
        int rev = int.TryParse(TxtRevision.Text.Trim(), out int r) ? Math.Max(1, r + 1) : 2;
        TxtRevision.Text = rev.ToString(CultureInfo.InvariantCulture);
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        StopMediaPreview();
        _idManuallyEdited = false;
        TxtName.Text = "My Gen5W Boot Theme";
        TxtId.Text = "my-gen5w-boot-theme";
        TxtVersion.Text = "1.0.0";
        TxtRevision.Text = "1";
        TxtTags.Text = "ioniq, gen5w";
        TxtDescription.Text = "";
        ChkHasOnBoot.IsChecked = true;
        ChkHasPowerOff.IsChecked = true;
        ChkSeparateWelcome.IsChecked = false;

        _rawBootSourceBitmap = null;
        _processedBootAssets = null;
        ImgBootPreview.Source = null;
        TxtBootPlaceholder.Visibility = Visibility.Visible;
        TxtBootStats.Text = "Outputs hardware-JPU Baseline 4:4:4 1920×720 + 1280×720 JPEGs (≤ 450 KB).";

        _goodbyeInputPath = null;
        _welcomeInputPath = null;
        _rawVideoSampleFrame = null;
        _processedVideoAssets = null;
        SldVideoPanX.Value = 0;
        SldVideoPanY.Value = 0;
        ImgVideoPoster.Source = null;
        TxtGoodbyeSource.Text = "Source: (none)";
        TxtWelcomeSource.Text = "Welcome: (none)";
        TxtVideoPlaceholder.Visibility = Visibility.Visible;
        TxtVideoStats.Text = "Converts any MP4/MOV/MKV/WEBM to silent 1920×720 H.264 + 960×360 GIF & poster.";
        TxtStatusBar.Text = "Started a new Boot Theme project.";
    }

    private void BtnOpenZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Boot Theme (.zip)",
            Filter = "Boot Theme Archive (*.zip)|*.zip"
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadProjectZipIntoEditor(dlg.FileName);
    }

    private void LoadProjectZipIntoEditor(string zipPath)
    {
        try
        {
            StopMediaPreview();
            var loaded = BootThemePackager.LoadProjectFromZip(zipPath, _sessionWorkDir);
            var m = loaded.Manifest;

            _idManuallyEdited = true;
            TxtName.Text = m.Name;
            TxtId.Text = m.Id;
            TxtAuthor.Text = m.Author;
            TxtVersion.Text = m.Version;
            TxtRevision.Text = m.Revision.ToString(CultureInfo.InvariantCulture);
            TxtDescription.Text = m.Description ?? "";
            TxtTags.Text = m.Tags != null ? string.Join(", ", m.Tags) : "";

            if (!string.IsNullOrWhiteSpace(m.Category))
            {
                if (!CmbCategory.Items.Contains(m.Category))
                    CmbCategory.Items.Add(m.Category);
                CmbCategory.SelectedItem = m.Category;
            }

            ChkHasOnBoot.IsChecked = m.HasOnBoot;
            ChkHasPowerOff.IsChecked = m.HasPowerOff;
            ChkSeparateWelcome.IsChecked = m.HasWelcomeVideo;

            if (m.HasOnBoot && loaded.Splash1920Bytes != null)
            {
                _rawBootSourceBitmap = MediaProcessorService.DecodeBitmapFromBytes(loaded.Splash1920Bytes);
                RebuildBootImageAssets();
            }

            if (m.HasPowerOff && loaded.ExtractedGoodbyeMp4Path != null)
            {
                _goodbyeInputPath = loaded.ExtractedGoodbyeMp4Path;
                _welcomeInputPath = loaded.ExtractedWelcomeMp4Path;
                TxtGoodbyeSource.Text = "Source: " + Path.GetFileName(_goodbyeInputPath);
                TxtWelcomeSource.Text = _welcomeInputPath != null ? "Welcome: " + Path.GetFileName(_welcomeInputPath) : "Welcome: (none)";

                byte[] posterBytes = loaded.Poster960Bytes ?? [];
                BitmapSource? posterBmp = posterBytes.Length > 0 ? MediaProcessorService.DecodeBitmapFromBytes(posterBytes) : null;
                if (posterBmp != null && posterBytes.Length > 0 && loaded.PreviewPowerOffBytes != null)
                {
                    MediaProcessorService.InspectMp4File(_goodbyeInputPath, out _, out _, out int durMs, out _, out _, out _);
                    _processedVideoAssets = new ProcessedVideoAssets
                    {
                        GoodbyeMp4Path = _goodbyeInputPath,
                        WelcomeMp4Path = _welcomeInputPath,
                        PreviewGifOrMp4Bytes = loaded.PreviewPowerOffBytes,
                        PreviewExtension = loaded.PreviewPowerOffExt,
                        Poster960Jpg = posterBytes,
                        DurationMs = m.PowerOffDurationMs ?? durMs,
                        PosterBitmap = posterBmp,
                        EngineUsed = "Loaded Archive"
                    };
                    ShowProcessedVideoInUi(_processedVideoAssets);
                }
            }

            TxtStatusBar.Text = $"Loaded Boot Theme '{m.Name}' ({Path.GetFileName(zipPath)}).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to Open Archive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnPickFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "FFmpeg Executable (ffmpeg*.exe)|ffmpeg*.exe|All Executables (*.exe)|*.exe"
        };
        if (dlg.ShowDialog(this) == true)
        {
            MediaProcessorService.CustomFfmpegPath = dlg.FileName;
            RefreshEncoderLabel();
        }
    }

    private void BtnSignIn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new StoreAuthDialog(_storeUrl) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.AuthenticatedSession != null)
        {
            _session = dlg.AuthenticatedSession;
            if (string.IsNullOrWhiteSpace(TxtAuthor.Text))
                TxtAuthor.Text = _session.Username;
            UpdateAccountUi();
            TxtStatusBar.Text = $"Signed in to market.ioniqthemes.com as @{_session.Username}.";
        }
    }

    private void BtnSignOut_Click(object sender, RoutedEventArgs e)
    {
        StoreAuthStorage.ClearSession(_storeUrl);
        _session = null;
        UpdateAccountUi();
        TxtStatusBar.Text = "Signed out of creator account on this PC.";
    }

    private void BtnBrowseStore_Click(object sender, RoutedEventArgs e)
    {
        var win = new StoreBrowserWindow(_storeUrl, _session) { Owner = this };
        if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.DownloadedZipToOpen))
        {
            LoadProjectZipIntoEditor(win.DownloadedZipToOpen!);
        }
    }

    private void BtnPickBootImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose Source Boot Image",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _rawBootSourceBitmap = MediaProcessorService.LoadBitmapUnlocked(dlg.FileName);
            SldPanX.Value = 0;
            SldPanY.Value = 0;
            SldZoom.Value = 1.0;
            RebuildBootImageAssets();
            TxtStatusBar.Text = $"Loaded Boot Image '{Path.GetFileName(dlg.FileName)}' ({_rawBootSourceBitmap.PixelWidth}×{_rawBootSourceBitmap.PixelHeight}).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Image Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BootCropControl_Changed(object sender, SelectionChangedEventArgs e) => RebuildBootImageAssets();

    private void BootCropSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => RebuildBootImageAssets();

    private void BtnResetCrop_Click(object sender, RoutedEventArgs e)
    {
        SldPanX.Value = 0;
        SldPanY.Value = 0;
        SldZoom.Value = 1.0;
        RebuildBootImageAssets();
    }

    private void RebuildBootImageAssets()
    {
        if (_rawBootSourceBitmap == null || ImgBootPreview == null) return;
        try
        {
            var mode = CmbBootCropMode.SelectedIndex == 1 ? ImageCropMode.FitLetterbox : ImageCropMode.CoverFill;
            _processedBootAssets = MediaProcessorService.ProcessBootImage(
                _rawBootSourceBitmap,
                mode,
                SldPanX.Value,
                SldPanY.Value,
                SldZoom.Value);

            ImgBootPreview.Source = _processedBootAssets.PreviewBitmap;
            TxtBootPlaceholder.Visibility = Visibility.Collapsed;
            TxtBootStats.Text =
                $"Ready • 1920×720 JPU 4:4:4: {_processedBootAssets.Splash1920Jpg.Length / 1024.0:0.#} KB  |  " +
                $"1280×720: {_processedBootAssets.Splash1280Jpg.Length / 1024.0:0.#} KB  |  " +
                $"960×360 Preview: {_processedBootAssets.Preview960Jpg.Length / 1024.0:0.#} KB";
        }
        catch (Exception ex)
        {
            TxtBootStats.Text = "Crop error: " + ex.Message;
        }
    }

    private async void BtnPickGoodbyeVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose Goodbye / Startup Video",
            Filter = "Video Files (*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.gif)|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.gif|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _goodbyeInputPath = dlg.FileName;
        _processedVideoAssets = null;
        StopMediaPreview();

        SldVideoPanX.Value = 0;
        SldVideoPanY.Value = 0;
        TxtGoodbyeSource.Text = "Source: " + Path.GetFileName(dlg.FileName);
        TxtStatusBar.Text = "Extracting video preview frame for live pan/crop framing...";

        double trimStart = double.TryParse(TxtTrimStart.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ts) ? Math.Max(0, ts) : 0;
        string picked = dlg.FileName;
        _rawVideoSampleFrame = await Task.Run(() => MediaProcessorService.ExtractRawVideoFrame(picked, trimStart + 0.5));
        UpdateLiveVideoCropPreview();

        TxtStatusBar.Text = _rawVideoSampleFrame != null
            ? $"Loaded '{Path.GetFileName(picked)}' ({_rawVideoSampleFrame.PixelWidth}×{_rawVideoSampleFrame.PixelHeight}). Adjust Vertical/Horizontal Pan, then click 'Convert & Build Previews'."
            : "Selected video. Adjust Vertical/Horizontal Pan if needed, then click 'Convert & Build Previews'.";
    }

    private void VideoCropControl_Changed(object sender, SelectionChangedEventArgs e) => UpdateLiveVideoCropPreview();

    private void VideoPanSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLiveVideoCropPreview();

    private void BtnResetVideoPan_Click(object sender, RoutedEventArgs e)
    {
        SldVideoPanX.Value = 0;
        SldVideoPanY.Value = 0;
        UpdateLiveVideoCropPreview();
    }

    private void UpdateLiveVideoCropPreview()
    {
        if (_rawVideoSampleFrame == null || ImgVideoPoster == null || CmbVideoCropMode == null)
            return;

        // Show live cropped still frame while adjusting pan/mode (stops looping old transcode so pan movement is visible!)
        StopMediaPreview();
        var mode = CmbVideoCropMode.SelectedIndex == 1 ? ImageCropMode.FitLetterbox : ImageCropMode.CoverFill;
        var preview960 = MediaProcessorService.RenderCroppedOrFitted(
            _rawVideoSampleFrame,
            960,
            360,
            mode,
            SldVideoPanX.Value,
            SldVideoPanY.Value);

        ImgVideoPoster.Source = preview960;
        TxtVideoPlaceholder.Visibility = Visibility.Collapsed;

        if (_processedVideoAssets != null)
        {
            TxtVideoStats.Text =
                $"Framing changed (Vertical Pan: {SldVideoPanY.Value:+0.00;-0.00;0.00}, Horizontal: {SldVideoPanX.Value:+0.00;-0.00;0.00}) — click 'Convert & Build Previews' to apply to video.";
        }
    }

    private void BtnPickWelcomeVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose Separate Door-Open Welcome Video",
            Filter = "Video Files (*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv)|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _welcomeInputPath = dlg.FileName;
        TxtWelcomeSource.Text = "Welcome: " + Path.GetFileName(dlg.FileName);
    }

    private async void BtnConvertVideo_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_goodbyeInputPath) || !File.Exists(_goodbyeInputPath))
        {
            MessageBox.Show(this, "Please choose a Goodbye Video file first.", "No Video Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        double trimStart = double.TryParse(TxtTrimStart.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ts) ? Math.Max(0, ts) : 0;
        double maxDur = double.TryParse(TxtMaxDuration.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double md) ? Math.Clamp(md, 0.5, 30.0) : 10.0;
        double posterPct = double.TryParse(TxtPosterPct.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pp) ? Math.Clamp(pp / 100.0, 0.05, 0.95) : 0.5;
        var mode = CmbVideoCropMode.SelectedIndex == 1 ? ImageCropMode.FitLetterbox : ImageCropMode.CoverFill;
        double panX = SldVideoPanX.Value;
        double panY = SldVideoPanY.Value;
        string? welcomeIn = ChkSeparateWelcome.IsChecked == true ? _welcomeInputPath : null;

        StopMediaPreview();
        BtnConvertVideo.IsEnabled = false;
        try
        {
            var prog = new Progress<string>(msg => TxtStatusBar.Text = msg);
            _processedVideoAssets = await MediaProcessorService.ConvertVideosAsync(
                _goodbyeInputPath,
                welcomeIn,
                _sessionWorkDir,
                trimStart,
                maxDur,
                posterPct,
                mode,
                panX,
                panY,
                prog);

            ShowProcessedVideoInUi(_processedVideoAssets);
            TxtStatusBar.Text = "Video conversion & preview generation complete!";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Video Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatusBar.Text = "Video conversion failed.";
        }
        finally
        {
            BtnConvertVideo.IsEnabled = true;
        }
    }

    private void ShowProcessedVideoInUi(ProcessedVideoAssets assets)
    {
        ImgVideoPoster.Source = assets.PosterBitmap;
        TxtVideoPlaceholder.Visibility = Visibility.Collapsed;

        long mp4Bytes = new FileInfo(assets.GoodbyeMp4Path).Length;
        TxtVideoStats.Text =
            $"Ready ({assets.EngineUsed}) • {assets.DurationMs / 1000.0:0.1}s silent H.264: {mp4Bytes / (1024.0 * 1024.0):0.##} MB  |  " +
            $"Preview {assets.PreviewExtension}: {assets.PreviewGifOrMp4Bytes.Length / 1024.0:0.#} KB  |  " +
            $"Poster: {assets.Poster960Jpg.Length / 1024.0:0.#} KB";

        try
        {
            MediaPreview.Source = new Uri(assets.GoodbyeMp4Path, UriKind.Absolute);
            MediaPreview.Play();
            _isPlayingPreview = true;
        }
        catch { }
    }

    private void MediaPreview_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_isPlayingPreview)
        {
            MediaPreview.Position = TimeSpan.Zero;
            MediaPreview.Play();
        }
    }

    private void BtnTogglePlay_Click(object sender, RoutedEventArgs e)
    {
        if (MediaPreview.Source == null) return;
        if (_isPlayingPreview)
        {
            MediaPreview.Pause();
            _isPlayingPreview = false;
        }
        else
        {
            MediaPreview.Play();
            _isPlayingPreview = true;
        }
    }

    private void StopMediaPreview()
    {
        try
        {
            _isPlayingPreview = false;
            MediaPreview.Stop();
            MediaPreview.Source = null;
        }
        catch { }
    }

    private BootThemeManifest CollectManifestFromUi()
    {
        int rev = int.TryParse(TxtRevision.Text.Trim(), out int r) ? Math.Max(1, r) : 1;
        var tags = TxtTags.Text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0 && t.Length <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new BootThemeManifest
        {
            FormatVersion = 1,
            Id = BootThemePackager.SanitizeSlug(TxtId.Text),
            Name = TxtName.Text.Trim(),
            Author = TxtAuthor.Text.Trim(),
            Version = TxtVersion.Text.Trim(),
            Revision = rev,
            Category = (CmbCategory.SelectedItem as string) ?? "OEM+",
            Description = (TxtDescription.Text ?? "").Replace("\r\n", "\n").Trim(),
            HasOnBoot = ChkHasOnBoot.IsChecked == true,
            HasPowerOff = ChkHasPowerOff.IsChecked == true,
            HasWelcomeVideo = ChkHasPowerOff.IsChecked == true
                              && ChkSeparateWelcome.IsChecked == true
                              && !string.IsNullOrWhiteSpace(_processedVideoAssets?.WelcomeMp4Path),
            PowerOffDurationMs = ChkHasPowerOff.IsChecked == true ? _processedVideoAssets?.DurationMs : null,
            Resolution = "1920x720",
            MinAppVersion = "1.0.0",
            CompatibleVersions = ["1.0.0+"],
            Tags = tags
        };
    }

    private void BtnExportZip_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var manifest = CollectManifestFromUi();
            var errs = BootThemePackager.ValidateMetadata(manifest);
            if (errs.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n• ", errs), "Metadata Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "Export Boot Theme (.zip)",
                FileName = BootThemePackager.GetCanonicalZipFileName(manifest),
                Filter = "Boot Theme Archive (*.zip)|*.zip"
            };
            if (sfd.ShowDialog(this) != true) return;

            BootThemePackager.BuildBootThemeZip(sfd.FileName, manifest, _processedBootAssets, _processedVideoAssets);
            var fi = new FileInfo(sfd.FileName);
            TxtStatusBar.Text = $"Exported & verified {fi.Name} ({fi.Length / (1024.0 * 1024.0):0.##} MB).";
            MessageBox.Show(
                this,
                $"Successfully built & verified Boot Theme package:\n{sfd.FileName}\n\nSize: {fi.Length / (1024.0 * 1024.0):0.##} MB",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnUploadStore_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _session.IsExpired())
        {
            var auth = new StoreAuthDialog(_storeUrl) { Owner = this };
            if (auth.ShowDialog() != true || auth.AuthenticatedSession == null)
                return;
            _session = auth.AuthenticatedSession;
            if (string.IsNullOrWhiteSpace(TxtAuthor.Text))
                TxtAuthor.Text = _session.Username;
            UpdateAccountUi();
        }

        string? tempZip = null;
        BtnUploadStore.IsEnabled = false;
        try
        {
            var manifest = CollectManifestFromUi();
            TxtId.Text = manifest.Id;

            tempZip = Path.Combine(_sessionWorkDir, BootThemePackager.GetCanonicalZipFileName(manifest));
            TxtStatusBar.Text = "Building & validating Boot Theme package for upload...";
            BootThemePackager.BuildBootThemeZip(tempZip, manifest, _processedBootAssets, _processedVideoAssets);

            long mb = new FileInfo(tempZip).Length;
            var confirm = MessageBox.Show(
                this,
                $"Upload '{manifest.Name}' ({Path.GetFileName(tempZip)}, {mb / (1024.0 * 1024.0):0.##} MB) to market.ioniqthemes.com as @{_session.Username}?",
                "Confirm Store Upload",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            TxtStatusBar.Text = $"Uploading {Path.GetFileName(tempZip)} to market.ioniqthemes.com...";
            var resp = await BootThemeStoreClient.UploadBootThemeAsync(_storeUrl, tempZip, manifest, _session.Token);
            TxtStatusBar.Text = resp.Message ?? "Uploaded successfully!";
            MessageBox.Show(
                this,
                (resp.Message ?? "Uploaded to Boot Themes Store successfully!") + $"\n\nSlug ID: {manifest.Id}\nVersion: v{manifest.Version} (r{manifest.Revision})",
                "Upload Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (BootThemeStoreException ex)
        {
            TxtStatusBar.Text = "Upload failed.";
            MessageBox.Show(this, ex.FormatFriendlyMessage(), "Store Upload Rejected", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            TxtStatusBar.Text = "Upload failed.";
            MessageBox.Show(this, ex.Message, "Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnUploadStore.IsEnabled = true;
            if (tempZip != null)
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
    }

    private void BtnPrepareUsb_Click(object sender, RoutedEventArgs e)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Removable || d.DriveType == DriveType.Fixed))
            .ToList();

        var folderDlg = new OpenFolderDialog
        {
            Title = "Select USB Flash Drive Root (e.g. H:\\) to Stage 'Boot Themes' Installer & Theme"
        };
        var removable = drives.FirstOrDefault(d => d.DriveType == DriveType.Removable);
        if (removable != null)
            folderDlg.InitialDirectory = removable.RootDirectory.FullName;

        if (folderDlg.ShowDialog(this) != true) return;
        string targetRoot = folderDlg.FolderName;

        try
        {
            string? builtZip = null;
            bool hasCurrentTheme = (ChkHasOnBoot.IsChecked == true && _processedBootAssets != null)
                                   || (ChkHasPowerOff.IsChecked == true && _processedVideoAssets != null);

            if (hasCurrentTheme)
            {
                var manifest = CollectManifestFromUi();
                if (string.IsNullOrWhiteSpace(manifest.Author)) manifest.Author = "Creator";
                if (BootThemePackager.ValidateMetadata(manifest).Count == 0)
                {
                    builtZip = Path.Combine(_sessionWorkDir, BootThemePackager.GetCanonicalZipFileName(manifest));
                    BootThemePackager.BuildBootThemeZip(builtZip, manifest, _processedBootAssets, _processedVideoAssets);
                }
            }

            var written = BootThemePackager.StageUsbDrive(targetRoot, builtZip, includeNemesisInstaller: true);
            TxtStatusBar.Text = $"Staged {written.Count} file(s) onto {targetRoot}.";
            MessageBox.Show(
                this,
                "Staged onto USB drive:\n• " + string.Join("\n• ", written) +
                "\n\nPlug the USB drive into the car to install 'Boot Themes' via NEMESIS, or open 'Boot Themes' -> 'Import USB' to load themes from /BootThemes/.",
                "USB Drive Ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "USB Staging Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
