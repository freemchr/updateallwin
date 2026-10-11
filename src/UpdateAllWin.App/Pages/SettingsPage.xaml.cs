using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UpdateAllWin.App.Dialogs;
using UpdateAllWin.App.ViewModels;
using UpdateAllWin.Toolbar;

namespace UpdateAllWin.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadPreferences();
            await ViewModel.LoadPolicyAsync();
            Bindings.Update();
        };
    }

    public MainViewModel ViewModel => App.ViewModel;

    public string AboutText { get; } =
        $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}. Uses each manufacturer's official tools: HP Image Assistant, " +
        "Dell Command | Update, Lenovo System Update / Commercial Vantage, and Windows Update for other PCs.";

    private void LoadPreferences()
    {
        _loading = true;
        var p = UserPreferences.Current;
        StartWithWindows.IsOn = p.StartWithWindows;
        ShowToolbar.IsOn = p.ShowToolbar;
        AlwaysOnTop.IsOn = p.ToolbarAlwaysOnTop;
        MinimizeToTray.IsOn = p.MinimizeToTray;
        RestartNotifications.IsOn = p.RestartNotifications;
        ShowSerial.IsOn = p.ShowSerialNumber;
        Theme.SelectedIndex = (int)p.Theme;
        _loading = false;
    }

    private void Pref_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var p = UserPreferences.Current;
        var agent = ViewModel.Agent;
        switch (((FrameworkElement)sender).Name)
        {
            case nameof(StartWithWindows):
                p.StartWithWindows = StartWithWindows.IsOn;
                StartupRegistration.Apply(p.StartWithWindows);
                break;
            case nameof(ShowToolbar):
                agent.SetToolbarVisible(ShowToolbar.IsOn); // saves the preference
                return;
            case nameof(AlwaysOnTop):
                p.ToolbarAlwaysOnTop = AlwaysOnTop.IsOn;
                agent.SetToolbarAlwaysOnTop(p.ToolbarAlwaysOnTop);
                break;
            case nameof(MinimizeToTray):
                p.MinimizeToTray = MinimizeToTray.IsOn;
                break;
            case nameof(RestartNotifications):
                p.RestartNotifications = RestartNotifications.IsOn;
                break;
            case nameof(ShowSerial):
                p.ShowSerialNumber = ShowSerial.IsOn;
                break;
        }
        p.Save();
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Theme.SelectedIndex < 0) return;
        UserPreferences.Current.Theme = (AppTheme)Theme.SelectedIndex;
        UserPreferences.Current.Save();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await AutomationPage.SaveAsync(XamlRoot);

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadPolicyAsync();
        Bindings.Update();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UpdateAllWin", "Logs");
        if (Directory.Exists(logs)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logs}\"") { UseShellExecute = true });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ViewModel.ExportDiagnosticsAsync();
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await AppDialogs.ShowErrorAsync(XamlRoot, "Couldn't export diagnostics", ex.Message);
        }
    }
}
