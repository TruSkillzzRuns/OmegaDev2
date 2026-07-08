using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Pages;

namespace OmegaDev2;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "OmegaDev2";
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag as string ?? "";
        System.Type? target = tag switch
        {
            "home"        => typeof(HomePage),
            "teleportpad" => typeof(TeleportPadPage),
            _             => null,
        };
        if (target != null) ContentFrame.Navigate(target);
    }
}
