using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using UpdateAllWin.Contracts;
using Windows.Graphics;
using WinRT.Interop;
using static UpdateAllWin.Toolbar.Native;

namespace UpdateAllWin.Toolbar;

/// <summary>
/// The floating "Update All" toolbar: a small, draggable, optionally always-on-top Fluent window.
/// Status colours: green up to date, blue busy, orange updates available, red failed, yellow restart required.
/// </summary>
public sealed partial class ToolbarWindow : Window
{
    private const int WidthDip = 236;
    private const int HeightDip = 58;

    private readonly BackgroundAgent _agent;
    private readonly IntPtr _hwnd;
    private readonly OverlappedPresenter _presenter;
    private readonly Grid _root;
    private readonly Ellipse _dot;
    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly ProgressRing _ring;
    private readonly FontIcon _glyph;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _completeTimer;
    private bool _showingComplete;
    private bool _allowClose;

    public ToolbarWindow(BackgroundAgent agent)
    {
        _agent = agent;
        Title = "UpdateAll Win";
        _hwnd = WindowNative.GetWindowHandle(this);

        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        _presenter.IsAlwaysOnTop = UserPreferences.Current.ToolbarAlwaysOnTop;
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(System.IO.Path.Combine(agent.AssetsFolder, "UpdateAllWin.ico"));
        SystemBackdrop = new DesktopAcrylicBackdrop();
        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // --- content ---
        var logo = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center };
        logo.Children.Add(new LogoMark());
        _dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            StrokeThickness = 2,
            Stroke = new SolidColorBrush(Colors.White),
            Fill = new SolidColorBrush(StatusVisuals.ColorFor(OverallStatus.Unknown)),
        };
        logo.Children.Add(_dot);

        _ring = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Colors.White) };
        _glyph = new FontIcon { Glyph = "", FontSize = 15 };
        _label = new TextBlock { Text = "Update All", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(_ring);
        content.Children.Add(_glyph);
        content.Children.Add(_label);

        _button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 38,
            CornerRadius = new CornerRadius(8),
        };
        _button.Click += (_, _) => _agent.UpdateAll();
        AutomationHelper.SetName(_button, "Update All");

        // Visible way to hide the bar (the whole bar is draggable, so it doesn't need a grip).
        // Hiding is remembered; the tray icon menu and Settings bring it back.
        var hide = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Opacity = 0.7,
        };
        hide.Click += (_, _) => _agent.SetToolbarVisible(false);
        ToolTipService.SetToolTip(hide, "Hide toolbar (show it again from the tray icon or Settings)");
        AutomationHelper.SetName(hide, "Hide toolbar");

        _root = new Grid
        {
            Padding = new Thickness(10, 8, 6, 8),
            ColumnSpacing = 10,
            Background = new SolidColorBrush(Colors.Transparent), // hit-testable so the whole bar can be dragged
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(_button, 1);
        Grid.SetColumn(hide, 2);
        _root.Children.Add(logo);
        _root.Children.Add(_button);
        _root.Children.Add(hide);
        _root.ContextFlyout = BuildMenu();
        _root.PointerPressed += OnPointerPressed;
        _root.RequestedTheme = ToElementTheme(UserPreferences.Current.Theme);
        Content = _root;

        // --- size, position, persistence ---
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));
        AppWindow.Move(InitialPosition());

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) =>
        {
            UserPreferences.Current.ToolbarX = AppWindow.Position.X;
            UserPreferences.Current.ToolbarY = AppWindow.Position.Y;
            UserPreferences.Current.Save();
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange) { _saveTimer.Stop(); _saveTimer.Start(); }
        };

        _completeTimer = DispatcherQueue.CreateTimer();
        _completeTimer.Interval = TimeSpan.FromSeconds(5);
        _completeTimer.IsRepeating = false;
        _completeTimer.Tick += (_, _) => { _showingComplete = false; Update(_agent.Monitor.Status, _agent.Monitor.ServiceAvailable); };

        // Closing (Alt+F4) only hides the toolbar; the background service is unaffected.
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            _agent.SetToolbarVisible(false);
        };

        Update(null, false);
    }

    public void SetAlwaysOnTop(bool value) => _presenter.IsAlwaysOnTop = value;

    public void ApplyTheme(AppTheme theme) => _root.RequestedTheme = ToElementTheme(theme);

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Shows "Complete" briefly after an operation started from the toolbar finishes.</summary>
    public void FlashComplete(string text)
    {
        _showingComplete = true;
        _label.Text = text;
        _glyph.Glyph = "";
        _glyph.Visibility = Visibility.Visible;
        _ring.IsActive = false;
        _ring.Visibility = Visibility.Collapsed;
        _completeTimer.Stop();
        _completeTimer.Start();
    }

    public void Update(EngineStatus? status, bool serviceAvailable)
    {
        var overall = StatusVisuals.From(status, serviceAvailable);
        _dot.Fill = new SolidColorBrush(StatusVisuals.ColorFor(overall));
        var busy = overall == OverallStatus.Busy;

        if (busy)
        {
            _showingComplete = false;
            _completeTimer.Stop();
            _label.Text = status!.Stage switch
            {
                EngineStage.Scanning => "Scanning…",
                EngineStage.Downloading => status.Percent is { } p ? $"Downloading {p}%" : "Downloading…",
                EngineStage.Installing => "Installing…",
                _ => "Working…",
            };
            _ring.IsActive = true;
            _ring.Visibility = Visibility.Visible;
            _glyph.Visibility = Visibility.Collapsed;
        }
        else if (!_showingComplete)
        {
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
            _glyph.Visibility = Visibility.Visible;
            _glyph.Glyph = "";
            _label.Text = "Update All";
        }
        _button.IsEnabled = !busy;

        var last = status?.LastSuccessfulScanUtc is { } t ? $"Last scan: {t.ToLocalTime():g}" : "Not scanned yet";
        var state = !serviceAvailable ? "UpdateAll Win service is not running"
            : overall switch
            {
                OverallStatus.UpToDate => "Everything is up to date",
                OverallStatus.Busy => status!.Message,
                OverallStatus.UpdatesAvailable => $"{status!.Updates.Count(u => u.State is UpdateState.Available or UpdateState.AwaitingApproval)} update(s) available",
                OverallStatus.Failed => "The last update failed - right-click to view history",
                OverallStatus.RestartRequired => "Restart required to finish installing updates",
                _ => "Status unknown",
            };
        ToolTipService.SetToolTip(_root, $"UpdateAll Win\n{state}\n{last}");
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open UpdateAll Win", "", () => _agent.Shell.OpenMainWindow()));
        menu.Items.Add(Item("Check for Updates", "", () => _agent.CheckForUpdates()));
        menu.Items.Add(Item("View Update History", "", () => _agent.Shell.OpenMainWindow("history")));
        menu.Items.Add(Item("Settings", "", () => _agent.Shell.OpenMainWindow("settings")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Hide Toolbar", "", () => _agent.SetToolbarVisible(false)));
        menu.Items.Add(Item("Exit Toolbar", "", () => _agent.ExitToolbarAndTray()));
        return menu;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        if (!point.Properties.IsLeftButtonPressed) return;
        // Let Windows move the window as if the title bar was dragged.
        ReleaseCapture();
        SendMessage(_hwnd, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    private PointInt32 InitialPosition()
    {
        var size = AppWindow.Size;
        var prefs = UserPreferences.Current;
        if (prefs.ToolbarX is { } x && prefs.ToolbarY is { } y)
        {
            // Keep the saved position only if it is still on a connected display.
            var area = DisplayArea.GetFromPoint(new PointInt32(x + size.Width / 2, y + size.Height / 2), DisplayAreaFallback.None);
            if (area is not null) return new PointInt32(x, y);
        }
        // Default: just above the taskbar at the bottom-right of the primary display.
        var work = DisplayArea.Primary.WorkArea;
        var margin = (int)(16 * GetDpiForWindow(_hwnd) / 96.0);
        return new PointInt32(work.X + work.Width - size.Width - margin, work.Y + work.Height - size.Height - margin);
    }

    internal static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}

internal static class AutomationHelper
{
    public static void SetName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
