using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        ServerUrlBox.Text = AppState.ServerUrl;
        RefreshStatusDot();
        AppState.ServerStatusChanged += RefreshStatusDot;
        Unloaded += (_, _) => AppState.ServerStatusChanged -= RefreshStatusDot;
    }

    private void ServerUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Live-update as they type — cheap.
        AppState.ServerUrl = ServerUrlBox.Text?.Trim() ?? "http://localhost:8080";
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.ServerUrl = ServerUrlBox.Text?.Trim() ?? "http://localhost:8080";
        StatusText.Text = "URL saved.";
    }

    private void ToolCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            // Find MainWindow.Nav.SelectedItem by tag and set it — Frame.Navigate
            // routed via nav keeps the selected item in sync.
            var window = App.MainWindow;
            if (window?.NavView is NavigationView nav)
            {
                foreach (var item in nav.MenuItems)
                {
                    if (item is NavigationViewItem nvi && (nvi.Tag as string) == tag)
                    {
                        nav.SelectedItem = nvi;
                        return;
                    }
                }
            }
        }
    }

    private void RefreshStatusDot()
    {
        var brushKey = AppState.ServerReachable ? "OmegaDev2.SuccessBrush" : "OmegaDev2.DangerBrush";
        if (Application.Current.Resources.TryGetValue(brushKey, out var b) && b is Brush br)
            StatusDot.Fill = br;
        StatusText.Text = AppState.ServerReachable ? "Server is online." : "Server not reachable.";
    }
}
