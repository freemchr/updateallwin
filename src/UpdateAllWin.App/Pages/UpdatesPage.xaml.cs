using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UpdateAllWin.App.Dialogs;
using UpdateAllWin.App.ViewModels;

namespace UpdateAllWin.App.Pages;

public sealed partial class UpdatesPage : Page
{
    private bool _syncingFilter;

    public UpdatesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => SelectCurrentFilter();
    }

    public MainViewModel ViewModel => App.ViewModel;

    private void SelectCurrentFilter()
    {
        _syncingFilter = true;
        var tag = ViewModel.CurrentFilter;
        foreach (var item in Filter.Items) item.IsSelected = (string)item.Tag == tag;
        _syncingFilter = false;
    }

    private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingFilter || sender.SelectedItem?.Tag is not string tag) return;
        ViewModel.SetFilter(tag);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await Report(await ViewModel.CheckForUpdatesAsync());

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await Report(await ViewModel.UpdateAllAsync());

    private async void InstallSelected_Click(object sender, RoutedEventArgs e) => await InstallFlow.InstallSelectedAsync(XamlRoot, ViewModel);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll(true);

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll(false);

    private async void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UpdateItemViewModel { Dto.ReleaseNotesUrl: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async void Exclude_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UpdateItemViewModel item) return;
        await Report(await ViewModel.SetExcludedAsync(item.Key, true));
    }

    private async void Include_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UpdateItemViewModel item) return;
        await Report(await ViewModel.SetExcludedAsync(item.Key, false));
    }

    private async Task Report(string? error)
    {
        if (error is not null) await AppDialogs.ShowErrorAsync(XamlRoot, "UpdateAll Win", error);
    }
}
