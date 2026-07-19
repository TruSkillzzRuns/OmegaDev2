using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed class CurrencyRow
{
    public string ProtoRef { get; }
    public string Name { get; }
    public long Amount { get; }
    public string AmountText => Amount.ToString("N0");
    public string MaxAmountText => MaxAmount > 0 ? $" / {MaxAmount:N0}" : "";
    public int MaxAmount { get; }

    // Plain get/set — x:Bind TwoWay just needs a settable property, no
    // change notification required since the user only ever edits this
    // themselves (no code-driven updates need to flow back to the box).
    public string NewAmountText { get; set; }

    public CurrencyRow(CurrencyEntry entry)
    {
        ProtoRef = entry.ProtoRef;
        Name = entry.Name;
        Amount = entry.Amount;
        MaxAmount = entry.MaxAmount;
        NewAmountText = entry.Amount.ToString();
    }
}

// Currency Editor — per-currency balance view + set, unlike the
// !player givecurrency chat command which only ever grants ALL
// currencies at once.
public sealed partial class CurrencyEditorPage : Page
{
    private readonly ServerApiClient _api = new();
    public ObservableCollection<CurrencyRow> Currencies { get; } = new();

    public CurrencyEditorPage()
    {
        InitializeComponent();
        CurrencyList.ItemsSource = Currencies;
        _ = RefreshAsync();
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        RefreshBtn.IsEnabled = false;
        StatusText.Text = "loading…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetCurrencyListAsync(TargetPlayer);
            Currencies.Clear();
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "server unreachable";
                return;
            }
            foreach (var entry in resp.Currencies.OrderBy(c => c.Name))
                Currencies.Add(new CurrencyRow(entry));
            StatusText.Text = $"{resp.Player}: {resp.Currencies.Count} currencies";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
        }
    }

    private async void SetOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string protoRef) return;
        var row = Currencies.FirstOrDefault(c => c.ProtoRef == protoRef);
        if (row == null) return;

        if (long.TryParse(row.NewAmountText, out long amount) == false || amount < 0)
        {
            StatusText.Text = "enter a valid non-negative amount";
            return;
        }

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostCurrencySetAsync(TargetPlayer, protoRef, amount);
            StatusText.Text = resp == null
                ? "no response"
                : resp.Ok
                    ? $"{row.Name}: {resp.PreviousAmount:N0} -> {resp.NewAmount:N0}"
                    : (resp.Error ?? "set failed");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }
}
