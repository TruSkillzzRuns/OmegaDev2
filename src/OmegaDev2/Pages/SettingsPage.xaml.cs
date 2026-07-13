using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;
using Windows.System;

namespace OmegaDev2.Pages;

public sealed partial class SettingsPage : Page
{
    private UpdateCheckService.UpdateInfo? _lastInfo;

    public SettingsPage()
    {
        InitializeComponent();
        CurrentVersionText.Text = "v" + UpdateCheckService.CurrentAssemblyVersion();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckBtn.IsEnabled = false;
        InstallBtn.IsEnabled = false;
        ReleaseNotesBtn.IsEnabled = false;
        ReleaseNotesPanel.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking GitHub for the latest release…";

        var info = await UpdateCheckService.CheckAsync();
        _lastInfo = info;

        CurrentVersionText.Text = "v" + info.CurrentVersion;
        LatestVersionText.Text = string.IsNullOrEmpty(info.LatestVersion) ? "—" : "v" + info.LatestVersion;
        UpdateStatusText.Text = info.Message;

        if (info.UpdateAvailable && !string.IsNullOrEmpty(info.DownloadUrl))
        {
            InstallBtn.IsEnabled = true;
            ReleaseNotesBtn.IsEnabled = !string.IsNullOrEmpty(info.ReleaseNotes);
        }
        else if (info.UpdateAvailable)
        {
            UpdateStatusText.Text = info.Message + " (Release has no ZIP asset attached — use View Release Notes to download manually.)";
            ReleaseNotesBtn.IsEnabled = !string.IsNullOrEmpty(info.ReleaseUrl);
        }

        CheckBtn.IsEnabled = true;
    }

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        if (_lastInfo is null) return;
        if (!string.IsNullOrEmpty(_lastInfo.ReleaseNotes))
        {
            ReleaseNotesText.Text = _lastInfo.ReleaseNotes;
            ReleaseNotesPanel.Visibility = Visibility.Visible;
        }
        else if (Uri.TryCreate(_lastInfo.ReleaseUrl, UriKind.Absolute, out var uri))
        {
            _ = Launcher.LaunchUriAsync(uri);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_lastInfo is null || string.IsNullOrEmpty(_lastInfo.DownloadUrl)) return;

        var dlg = new ContentDialog
        {
            Title = $"Install v{_lastInfo.LatestVersion}?",
            Content = "OmegaDev2 will download the update, close, install the new files, and relaunch. Continue?",
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        CheckBtn.IsEnabled = false;
        InstallBtn.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;

        var progress = new Progress<UpdateCheckService.DownloadProgress>(p =>
        {
            DownloadProgress.Value = p.PercentOrZero;
            UpdateStatusText.Text = p.TotalBytes is { } t
                ? $"Downloading… {p.BytesReceived / (1024 * 1024)} / {t / (1024 * 1024)} MB ({p.PercentOrZero}%)"
                : $"Downloading… {p.BytesReceived / (1024 * 1024)} MB";
        });

        try
        {
            UpdateStatusText.Text = "Downloading update…";
            bool launched = await UpdateCheckService.DownloadAndInstallAsync(
                _lastInfo.DownloadUrl, _lastInfo.LatestVersion, progress, CancellationToken.None);

            if (launched)
            {
                UpdateStatusText.Text = "Update downloaded. Closing OmegaDev2 to install…";
                // Give the wrapper a beat to attach to our PID before we exit.
                await System.Threading.Tasks.Task.Delay(750);
                Application.Current.Exit();
            }
            else
            {
                UpdateStatusText.Text = "Downloaded, but failed to launch the installer. Try again or install manually from the release page.";
                CheckBtn.IsEnabled = true;
                InstallBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Install failed: {ex.GetType().Name}: {ex.Message}";
            CheckBtn.IsEnabled = true;
            InstallBtn.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }
}
