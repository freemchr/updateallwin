using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UpdateAllWin.App.Dialogs;
using UpdateAllWin.App.ViewModels;
using UpdateAllWin.Toolbar;

namespace UpdateAllWin.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.SetFilter("all");
    }

    public MainViewModel ViewModel => App.ViewModel;

    private async void Check_Click(object sender, RoutedEventArgs e) => await Report(await ViewModel.CheckForUpdatesAsync());

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await Report(await ViewModel.UpdateAllAsync());

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await ViewModel.CancelAsync();

    private async void Review_Click(object sender, RoutedEventArgs e) => await InstallFlow.ReviewAwaitingAsync(XamlRoot, ViewModel);

    private async void InstallSelected_Click(object sender, RoutedEventArgs e) => await InstallFlow.InstallSelectedAsync(XamlRoot, ViewModel);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll(true);

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll(false);

    private void Category_Click(object sender, RoutedEventArgs e) =>
        App.Instance?.OpenMainWindow($"updates:{((FrameworkElement)sender).Tag}");

    private void ToggleSerial_Click(object sender, RoutedEventArgs e)
    {
        UserPreferences.Current.ShowSerialNumber = !UserPreferences.Current.ShowSerialNumber;
        UserPreferences.Current.Save();
    }

    private async Task Report(string? error)
    {
        if (error is not null) await AppDialogs.ShowErrorAsync(XamlRoot, "UpdateAll Win", error);
    }
}
