using System;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Controls;

public sealed partial class ServerVersionPicker : UserControl
{
    private sealed record Option(string Label, string Url);

    // Same fixed ports as StartServer.bat/StartServer_v48.bat/StartServer_v53.bat
    // and the Account Migration page's version list.
    private static readonly Option[] Options =
    {
        new("1.48", "http://localhost:8081"),
        new("1.52", "http://localhost:8080"),
        new("1.53", "http://localhost:8082"),
    };

    private bool _suppressChange;

    public ServerVersionPicker()
    {
        InitializeComponent();

        foreach (var o in Options)
            VersionCombo.Items.Add(o.Label);

        _suppressChange = true;
        int match = Array.FindIndex(Options, o => o.Url == AppState.ServerUrl);
        VersionCombo.SelectedIndex = match >= 0 ? match : 1; // default 1.52
        _suppressChange = false;

        AppState.ServerUrlChanged += OnServerUrlChangedExternally;
        Unloaded += (_, _) => AppState.ServerUrlChanged -= OnServerUrlChangedExternally;
    }

    private void OnServerUrlChangedExternally()
    {
        // Keep in sync if something else (e.g. Diagnostics page) changes the URL directly.
        int match = Array.FindIndex(Options, o => o.Url == AppState.ServerUrl);
        if (match >= 0 && VersionCombo.SelectedIndex != match)
        {
            _suppressChange = true;
            VersionCombo.SelectedIndex = match;
            _suppressChange = false;
        }
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChange) return;
        int index = VersionCombo.SelectedIndex;
        if (index < 0 || index >= Options.Length) return;
        AppState.ServerUrl = Options[index].Url;
    }
}
