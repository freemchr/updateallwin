using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using UpdateAllWin.App.ViewModels;
using UpdateAllWin.Toolbar;
using Windows.ApplicationModel.Activation;

namespace UpdateAllWin.App;

public partial class App : Application, IShellActions
{
    private MainWindow? _window;
    private BackgroundAgent? _agent;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        Instance = this;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Write($"Unhandled UI exception: {e.Exception}");
            e.Handled = true;
        };
    }

    public static App? Instance { get; private set; }
    public static MainViewModel ViewModel { get; private set; } = null!;
    public static string AssetsFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Assets");

    public bool IsMainWindowVisible => _window?.AppWindow.IsVisible == true;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _agent = new BackgroundAgent(_dispatcher, this, AssetsFolder);
        ViewModel = new MainViewModel(_agent, _dispatcher);
        StartupRegistration.Apply(UserPreferences.Current.StartWithWindows);

        HandleActivation(Program.LaunchArguments, firstLaunch: true);
    }

    /// <summary>A second launch (or a notification click) forwarded to this instance.</summary>
    public void OnRedirectedActivation(AppActivationArguments args) =>
        _dispatcher?.TryEnqueue(() => HandleActivation(args, firstLaunch: false));

    private void HandleActivation(AppActivationArguments? activation, bool firstLaunch)
    {
        var arguments = "";
        if (activation?.Kind == ExtendedActivationKind.Launch && activation.Data is ILaunchActivatedEventArgs launch)
            arguments = launch.Arguments ?? "";
        else if (activation?.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs toast)
        {
            // Launched by clicking a notification while the app wasn't running.
            toast.Arguments.TryGetValue("action", out var action);
            if (action == "restart") { _ = _agent!.RestartNowAsync(); return; }
            if (action == "later") { _ = _agent!.RestartLaterAsync(); if (firstLaunch) return; }
            OpenMainWindow(action is "review" ? "updates" : action is "history" ? "history" : null);
            return;
        }

        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var background = tokens.Contains("--background");
        string? page = null;
        var pageIndex = Array.IndexOf(tokens, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < tokens.Length) page = tokens[pageIndex + 1];

        if (background && firstLaunch && !UserPreferences.Current.StartWithWindows)
        {
            // Machine-wide startup entry, but this user turned "Start with Windows" off.
            ExitApplication();
            return;
        }

        if (!background || !firstLaunch) OpenMainWindow(page);
        if (tokens.Contains("--update-all")) _agent!.UpdateAll();
    }

    public void OpenMainWindow(string? page = null)
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }
        _window.ShowAndActivate();
        if (page is not null) _window.Navigate(page);
        _agent!.Monitor.FastPolling = true;
    }

    internal void OnMainWindowHidden()
    {
        if (_agent is not null) _agent.Monitor.FastPolling = false;
    }

    public void ExitApplication()
    {
        _agent?.Dispose();
        _agent = null;
        _window?.CloseForExit();
        Exit();
    }

    internal BackgroundAgent Agent => _agent!;

    internal MainWindow? MainWindowRef => _window;
}

internal static class AppLog
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateAllWin", "ui.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
