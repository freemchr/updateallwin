using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UpdateAllWin.App.Dialogs;
using UpdateAllWin.App.ViewModels;
using UpdateAllWin.Contracts;
using Windows.Storage.Pickers;

namespace UpdateAllWin.App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    public MainViewModel ViewModel => App.ViewModel;

    private HistoryKind? SelectedKind =>
        (KindFilter.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } tag && Enum.TryParse<HistoryKind>(tag, out var kind) ? kind : null;

    private Task LoadAsync() => ViewModel.LoadHistoryAsync(SelectedKind);

    private async void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItemViewModel item) await AppDialogs.ShowHistoryDetailsAsync(XamlRoot, item);
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"UpdateAllWin-history-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("CSV file", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Instance!.MainWindowRef!));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var sb = new StringBuilder("Time,Kind,Started by,Title,Result,Success,Versions,Exit code,Details\r\n");
        foreach (var h in ViewModel.History)
            sb.AppendJoin(',', Csv(h.Time), Csv(h.KindText), Csv(h.TriggerText), Csv(h.Title), Csv(h.Result),
                Csv(h.Entry.Success ? "Yes" : "No"), Csv(h.Versions), Csv(h.Entry.ExitCode?.ToString()), Csv(h.Entry.Details)).Append("\r\n");
        await File.WriteAllTextAsync(file.Path, sb.ToString(), Encoding.UTF8);
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ViewModel.ExportDiagnosticsAsync();
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await AppDialogs.ShowErrorAsync(XamlRoot, "Couldn't export diagnostics", ex.Message);
        }
    }

    /// <summary>Quotes a CSV field and neutralises spreadsheet formula injection.</summary>
    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
