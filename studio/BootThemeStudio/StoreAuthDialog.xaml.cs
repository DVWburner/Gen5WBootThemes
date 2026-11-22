using System;
using System.Windows;
using BootThemeStudio.Models;
using BootThemeStudio.Services;

namespace BootThemeStudio;

public partial class StoreAuthDialog : Window
{
    private readonly string _baseUrl;
    private bool _registerMode;

    public StoredStoreSession? AuthenticatedSession { get; private set; }

    public StoreAuthDialog(string baseUrl, bool startInRegisterMode = false)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        SetMode(startInRegisterMode);
    }

    private void SetMode(bool registerMode)
    {
        _registerMode = registerMode;
        PanelConfirmPassword.Visibility = registerMode ? Visibility.Visible : Visibility.Collapsed;
        BtnSubmit.Content = registerMode ? "Create Account" : "Sign In";
        BtnTabSignIn.Style = (Style)FindResource(registerMode ? "DarkButton" : "PrimaryCyanButton");
        BtnTabRegister.Style = (Style)FindResource(registerMode ? "PrimaryCyanButton" : "DarkButton");
        TxtError.Text = "";
    }

    private void BtnTabSignIn_Click(object sender, RoutedEventArgs e) => SetMode(false);
    private void BtnTabRegister_Click(object sender, RoutedEventArgs e) => SetMode(true);
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void BtnSubmit_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Text = "";
        string user = TxtUsername.Text.Trim();
        string pass = TxtPassword.Password;

        if (_registerMode && pass != TxtConfirmPassword.Password)
        {
            TxtError.Text = "Passwords do not match.";
            return;
        }

        BtnSubmit.IsEnabled = false;
        try
        {
            StoreAuthResponse resp = _registerMode
                ? await BootThemeStoreClient.RegisterAsync(_baseUrl, user, pass)
                : await BootThemeStoreClient.LoginAsync(_baseUrl, user, pass);

            if (string.IsNullOrWhiteSpace(resp.Token))
            {
                TxtError.Text = resp.Error ?? "Authentication did not return a session token.";
                return;
            }

            var session = new StoredStoreSession
            {
                Username = resp.Account?.Username ?? user,
                CreatedAt = resp.Account?.CreatedAt,
                Token = resp.Token,
                ExpiresAt = resp.ExpiresAt
            };
            StoreAuthStorage.SaveSession(_baseUrl, session);
            AuthenticatedSession = session;
            DialogResult = true;
        }
        catch (BootThemeStoreException ex)
        {
            TxtError.Text = ex.FormatFriendlyMessage();
        }
        catch (Exception ex)
        {
            TxtError.Text = ex.Message;
        }
        finally
        {
            BtnSubmit.IsEnabled = true;
        }
    }
}
