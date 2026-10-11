using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.App.Dialogs;
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
        AppWindow.SetIcon(Path.Combine(App.AssetsFolder, "UpdateAllWin.ico"));
        SystemBackdrop = new MicaBackdrop();

        var scale = WindowScale.For(this);
        AppWindow.Resize(new SizeInt32((int)(600 * scale), (int)(640 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = (int)(480 * scale);
            presenter.PreferredMinimumHeight = (int)(460 * scale);
        }
        CenterOnScreen();

        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        ApplyTheme(UserPreferences.Current.Theme);
        UserPreferences.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyTheme(UserPreferences.Current.Theme));
        RootGrid.ActualThemeChanged += (_, _) => ApplyTheme(UserPreferences.Current.Theme);

        AppWindow.Closing += OnClosing;
    }

    public MainViewModel ViewModel => App.ViewModel;

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    /// <summary>Opens a dialog for a deep link: "history" (what was installed) or "updates" (BIOS review).</summary>
    public async void Navigate(string page)
    {
        if (!RootGrid.IsLoaded)
        {
            // Just created: wait until the window has a XamlRoot to host dialogs.
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) => { RootGrid.Loaded -= onLoaded; Navigate(page); };
            RootGrid.Loaded += onLoaded;
            return;
        }
        if (page.StartsWith("history", StringComparison.OrdinalIgnoreCase))
            await AppDialogs.ShowInstalledAsync(Content.XamlRoot, await ViewModel.GetInstalledAsync());
        else if (page.StartsWith("updates", StringComparison.OrdinalIgnoreCase) && ViewModel.ShowReview)
            await InstallFlow.ReviewAwaitingAsync(Content.XamlRoot, ViewModel);
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (UserPreferences.Current.MinimizeToTray)
        {
            // Keep running in the notification area; the tray icon and notifications stay available.
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

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.UpdateAllAsync() is { } error) await AppDialogs.ShowErrorAsync(Content.XamlRoot, "UpdateAll Win", error);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await ViewModel.CancelAsync();

    private async void Review_Click(object sender, RoutedEventArgs e) => await InstallFlow.ReviewAwaitingAsync(Content.XamlRoot, ViewModel);

    private async void ShowInstalled_Click(object sender, RoutedEventArgs e) =>
        await AppDialogs.ShowInstalledAsync(Content.XamlRoot, await ViewModel.GetInstalledAsync());

    private void ToggleSerial_Click(object sender, RoutedEventArgs e)
    {
        UserPreferences.Current.ShowSerialNumber = !UserPreferences.Current.ShowSerialNumber;
        UserPreferences.Current.Save();
    }
}
