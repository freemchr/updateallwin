using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UpdateAllWin.App.Dialogs;
using UpdateAllWin.App.ViewModels;

namespace UpdateAllWin.App.Pages;

public sealed partial class AutomationPage : Page, INotifyPropertyChanged
{
    public AutomationPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadPolicyAsync();
            Bindings.Update();
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or nameof(MainViewModel.ServiceAvailable)) OnPropertyChanged(nameof(ServiceDescription));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel ViewModel => App.ViewModel;

    public List<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => DateTime.Today.AddHours(h).ToString("t")).ToList();

    public List<string> Days { get; } = Enum.GetValues<DayOfWeek>().Select(d => d.ToString()).ToList();

    public string ServiceDescription => ViewModel.ServiceAvailable
        ? "Running. Checks and installs updates even when this window is closed."
        : "Not running. Start or enable it so updates can run in the background.";

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(XamlRoot);

    internal static async Task SaveAsync(XamlRoot root)
    {
        var error = await App.ViewModel.SavePolicyAsync();
        if (error is not null) await AppDialogs.ShowErrorAsync(root, "Settings not saved", error);
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadPolicyAsync();
        Bindings.Update();
    }

    private async void Include_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key)
        {
            var error = await ViewModel.SetExcludedAsync(key, false);
            if (error is not null) await AppDialogs.ShowErrorAsync(XamlRoot, "UpdateAll Win", error);
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e) => await ServiceControl("start");
    private async void EnableService_Click(object sender, RoutedEventArgs e) => await ServiceControl("enable");
    private async void DisableService_Click(object sender, RoutedEventArgs e) => await ServiceControl("disable");

    private async Task ServiceControl(string action)
    {
        var code = await ElevatedHelper.ServiceControlAsync(action);
        if (code is not (0 or null) && action == "start")
            await AppDialogs.ShowErrorAsync(XamlRoot, "Service", $"The service could not be started (code {code}). Reinstall UpdateAll Win if it is missing.");
        ViewModel.Agent.Monitor.Refresh();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
