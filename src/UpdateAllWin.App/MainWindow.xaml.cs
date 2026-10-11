using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.App.Pages;
using UpdateAllWin.App.ViewModels;
using UpdateAllWin.Toolbar;
using Windows.Graphics;

namespace UpdateAllWin.App;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(App.AssetsFolder, "UpdateAllWin.ico"));
        SystemBackdrop = new MicaBackdrop();

        var scale = WindowScale.For(this);
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(820 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(940 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
        }
        CenterOnScreen();

        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        ApplyTheme(UserPreferences.Current.Theme);
        UserPreferences.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyTheme(UserPreferences.Current.Theme));
        RootGrid.ActualThemeChanged += (_, _) => ApplyTheme(UserPreferences.Current.Theme);

        AppWindow.Closing += OnClosing;
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    public MainViewModel ViewModel => App.ViewModel;

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    /// <summary>"dashboard", "updates", "updates:bios", "history", "automation", "settings".</summary>
    public void Navigate(string page)
    {
        var parts = page.Split(':', 2);
        var tag = parts[0].ToLowerInvariant();
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is null) return;
        if (parts.Length > 1) ViewModel.SetFilter(parts[1]);
        else if (tag == "updates") ViewModel.SetFilter("all");
        Nav.SelectedItem = item;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var type = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "updates" => typeof(UpdatesPage),
            "history" => typeof(HistoryPage),
            "automation" => typeof(AutomationPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };
        if (ContentFrame.CurrentSourcePageType != type) ContentFrame.Navigate(type);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (UserPreferences.Current.MinimizeToTray)
        {
            // Keep running in the notification area; the toolbar and notifications stay available.
            args.Cancel = true;
            AppWindow.Hide();
            App.Instance?.OnMainWindowHidden();
        }
        else
        {
            args.Cancel = true;
            App.Instance?.ExitApplication();
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        App.Instance?.Agent.ApplyTheme(theme);

        // Caption buttons don't follow RequestedTheme on their own; colour them to match.
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x14, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        var code = await ElevatedHelper.ServiceControlAsync("start");
        if (code is not 0)
            code = await ElevatedHelper.ServiceControlAsync("install");
        ViewModel.Agent.Monitor.Refresh();
    }

    private async void RestartNow_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Restart now?",
            Content = "Save your work first. Your PC will restart in 30 seconds.",
            PrimaryButtonText = "Restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = RootGrid.ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.RestartNowAsync();
    }

    private async void RestartLater_Click(object sender, RoutedEventArgs e) => await ViewModel.RestartLaterAsync();
}
