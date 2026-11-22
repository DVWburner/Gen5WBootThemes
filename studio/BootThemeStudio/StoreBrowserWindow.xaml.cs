using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BootThemeStudio.Models;
using BootThemeStudio.Services;

namespace BootThemeStudio;

public partial class StoreBrowserWindow : Window
{
    private readonly string _baseUrl;
    private readonly StoredStoreSession? _session;
    private bool _ready;

    public string? DownloadedZipToOpen { get; private set; }

    public StoreBrowserWindow(string baseUrl, StoredStoreSession? session)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        _session = session;

        CmbCategory.Items.Add("All Categories");
        foreach (string c in BootThemeStoreClient.CachedOrDefaultCategories)
            CmbCategory.Items.Add(c);
        CmbCategory.SelectedIndex = 0;

        Loaded += async (_, _) =>
        {
            _ready = true;
            await RefreshAsync();
        };
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) await RefreshAsync();
    }

    private async void TxtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RefreshAsync();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync()
    {
        TxtStatus.Text = "Loading Boot Themes catalog from market.ioniqthemes.com...";
        string sort = (CmbSort.SelectedItem as ComboBoxItem)?.Tag as string ?? "newest";
        string? cat = CmbCategory.SelectedItem as string;
        string part = (CmbPart.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        string? q = TxtSearch.Text.Trim();

        try
        {
            var res = await BootThemeStoreClient.FetchCatalogAsync(
                _baseUrl, sort, cat, part, q, _session?.Token);
            LstItems.ItemsSource = res.Items;
            TxtStatus.Text = $"Showing {res.Items.Count} Boot Theme(s).";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Store status: " + ex.Message;
        }
    }

    private async void LstItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ImgOnBootPreview.Source = null;
        ImgPowerOffPoster.Source = null;

        if (LstItems.SelectedItem is not StoreBootThemeListing item)
        {
            TxtDetailTitle.Text = "Select a Boot Theme";
            TxtDetailMeta.Text = "";
            TxtDetailDesc.Text = "";
            BtnDownloadLoad.IsEnabled = false;
            BtnDeleteOwned.Visibility = Visibility.Collapsed;
            return;
        }

        TxtDetailTitle.Text = item.Name;
        TxtDetailMeta.Text = $"ID: {item.Id}  •  {item.FriendlyMetaLine}";
        TxtDetailDesc.Text = item.Description ?? "";
        BtnDownloadLoad.IsEnabled = !string.IsNullOrWhiteSpace(item.DownloadUrl);
        BtnDeleteOwned.Visibility = item.IsOwnedByCurrentUser ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            if (!string.IsNullOrWhiteSpace(item.OnBootPreviewUrl))
            {
                byte[] b = await BootThemeStoreClient.DownloadPreviewAsync(item.OnBootPreviewUrl!);
                ImgOnBootPreview.Source = MediaProcessorService.DecodeBitmapFromBytes(b);
            }
            string? posterUrl = !string.IsNullOrWhiteSpace(item.PowerOffPosterUrl)
                ? item.PowerOffPosterUrl
                : (item.PowerOffPreviewUrl?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true ? item.PowerOffPreviewUrl : null);
            if (!string.IsNullOrWhiteSpace(posterUrl))
            {
                byte[] b = await BootThemeStoreClient.DownloadPreviewAsync(posterUrl!);
                ImgPowerOffPoster.Source = MediaProcessorService.DecodeBitmapFromBytes(b);
            }
        }
        catch { }
    }

    private async void BtnDownloadLoad_Click(object sender, RoutedEventArgs e)
    {
        if (LstItems.SelectedItem is not StoreBootThemeListing item || string.IsNullOrWhiteSpace(item.DownloadUrl))
            return;

        BtnDownloadLoad.IsEnabled = false;
        TxtStatus.Text = $"Downloading {item.Name}...";
        try
        {
            byte[] zipBytes = await BootThemeStoreClient.DownloadPackageVerifiedAsync(item.DownloadUrl!, item.Sha256);
            string cacheDir = Path.Combine(Path.GetTempPath(), "BootThemeStudio_Downloads");
            Directory.CreateDirectory(cacheDir);
            string dst = Path.Combine(cacheDir, $"{item.Id}_v{item.Version}_r{item.Revision}.zip");
            await File.WriteAllBytesAsync(dst, zipBytes);
            DownloadedZipToOpen = dst;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Download Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Download failed.";
        }
        finally
        {
            BtnDownloadLoad.IsEnabled = true;
        }
    }

    private async void BtnDeleteOwned_Click(object sender, RoutedEventArgs e)
    {
        if (LstItems.SelectedItem is not StoreBootThemeListing item || _session == null)
            return;

        var confirm = MessageBox.Show(
            this,
            $"Permanently delete '{item.Name}' ({item.Id}) from market.ioniqthemes.com?\n\nNote: Deleted IDs cannot be reused later.",
            "Confirm Store Deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        BtnDeleteOwned.IsEnabled = false;
        try
        {
            var resp = await BootThemeStoreClient.DeleteBootThemeAsync(_baseUrl, item.Id, _session.Token);
            TxtStatus.Text = resp.Message ?? "Deleted successfully.";
            await RefreshAsync();
        }
        catch (BootThemeStoreException ex)
        {
            MessageBox.Show(this, ex.FormatFriendlyMessage(), "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnDeleteOwned.IsEnabled = true;
        }
    }
}
