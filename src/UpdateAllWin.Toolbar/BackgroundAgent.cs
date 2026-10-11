using System.Diagnostics;
using Microsoft.UI.Dispatching;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.Toolbar;

/// <summary>Actions the agent needs from the host app.</summary>
public interface IShellActions
{
    /// <param name="page">null, "updates" (review a BIOS update) or "history" (what was installed).</param>
    void OpenMainWindow(string? page = null);
    bool IsMainWindowVisible { get; }
    void ExitApplication();
}

/// <summary>
/// The per-user companion to UpdateAllService: tray icon and notifications.
/// Runs non-elevated in the user's session; all privileged work happens in the service.
/// </summary>
public sealed class BackgroundAgent : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _reminderTimer;
    private bool _awaitingResult;
    private OperationTrigger? _requestedBy;
    private string? _lastTrayIcon;

    public BackgroundAgent(DispatcherQueue dispatcher, IShellActions shell, string assetsFolder)
    {
        _dispatcher = dispatcher;
        Shell = shell;
        AssetsFolder = assetsFolder;
        Monitor = new StatusMonitor(dispatcher);
        Notifications = new NotificationService(Path.Combine(assetsFolder, "Logo256.png"));
        Tray = new TrayIcon("UpdateAll Win", Path.Combine(assetsFolder, StatusVisuals.TrayIconFile(OverallStatus.Unknown)));
        Tray.Activated += () => Shell.OpenMainWindow();
        Tray.MenuProvider = BuildTrayMenu;

        Monitor.Changed += OnStatusChanged;
        Notifications.Invoked += args => _dispatcher.TryEnqueue(() => OnNotificationInvoked(args));
        Notifications.Register();

        _reminderTimer = dispatcher.CreateTimer();
        _reminderTimer.Interval = TimeSpan.FromMinutes(1);
        _reminderTimer.Tick += (_, _) => MaybeRemindRestart();
        _reminderTimer.Start();
    }

    public IShellActions Shell { get; }
    public string AssetsFolder { get; }
    public StatusMonitor Monitor { get; }
    public TrayIcon Tray { get; }
    public NotificationService Notifications { get; }

    // ------------------------------------------------------------------ actions

    public void UpdateAll() => _ = SendAsync(IpcCommand.StartUpdateAll, OperationTrigger.Toolbar);

    public void CheckForUpdates() => _ = SendAsync(IpcCommand.StartScan, OperationTrigger.Toolbar);

    private async Task SendAsync(IpcCommand command, OperationTrigger trigger)
    {
        try
        {
            var response = await Monitor.Client.SendAsync(command, trigger);
            if (!response.Ok)
            {
                Notifications.Info("UpdateAll Win", response.Error ?? "The request could not be started.");
                return;
            }
            _awaitingResult = true;
            _requestedBy = trigger;
            Monitor.FastPolling = true;
            Monitor.Refresh();
        }
        catch (ServiceAccessDeniedException ex)
        {
            Notifications.Info("UpdateAll Win", ex.Message);
        }
        catch (ServiceUnavailableException)
        {
            Notifications.Info("UpdateAll Win service is not running", "Open UpdateAll Win to start or install the background service.");
            Shell.OpenMainWindow();
        }
    }

    public async Task RestartNowAsync()
    {
        try
        {
            await Monitor.Client.InvokeAsync(IpcCommand.RestartComputer, 30);
        }
        catch (ServiceUnavailableException)
        {
            // Interactive users can restart their own PC; give them the standard 30 s warning.
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                "/r /t 30 /c \"UpdateAll Win is restarting your PC to finish installing updates.\"") { UseShellExecute = false, CreateNoWindow = true });
        }
    }

    public async Task RestartLaterAsync()
    {
        var prefs = UserPreferences.Current;
        prefs.RestartSnoozedUntilUtc = DateTime.UtcNow.AddMinutes(Math.Max(15, Monitor.Policy.RestartReminderMinutes));
        prefs.Save();
        try { await Monitor.Client.InvokeAsync(IpcCommand.DeferRestart); } catch { }
    }

    public bool CanDeferRestart =>
        Monitor.Status?.RestartDeferralEndsUtc is not { } ends || DateTime.UtcNow < ends;

    // ------------------------------------------------------------------ status handling

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var restartPending = Monitor.Status?.RestartPending == true;
        var busy = Monitor.IsBusy;
        var items = new List<TrayMenuItem>
        {
            new("Update All", UpdateAll, Enabled: !busy, IsDefault: true),
            new("Check for Updates", CheckForUpdates, Enabled: !busy),
            TrayMenuItem.Separator,
            new("Open UpdateAll Win", () => Shell.OpenMainWindow()),
            new("What Was Installed", () => Shell.OpenMainWindow("history")),
        };
        if (restartPending)
        {
            items.Add(TrayMenuItem.Separator);
            items.Add(new TrayMenuItem("Restart Now to Finish Updates", () => _ = RestartNowAsync()));
        }
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem("Exit", () => Shell.ExitApplication()));
        return items;
    }

    private void OnStatusChanged(object? sender, StatusChange change)
    {
        var status = change.Current;
        var overall = StatusVisuals.From(status, change.ServiceAvailable);

        var icon = StatusVisuals.TrayIconFile(overall);
        if (icon != _lastTrayIcon)
        {
            Tray.SetIcon(Path.Combine(AssetsFolder, icon));
            _lastTrayIcon = icon;
        }
        Tray.SetTooltip(TooltipFor(status, change.ServiceAvailable));
        Tray.BlockShutdown(status?.BiosInstallInProgress == true, "A BIOS update is being installed. Don't turn off your PC.");

        if (status is null) return;

        var wasBusy = IsBusyStage(change.Previous?.Stage);
        var isBusy = IsBusyStage(status.Stage);
        if (isBusy) return;
        if (!wasBusy && !_awaitingResult) return;

        // An operation just finished.
        var requestedBy = _requestedBy ?? status.Trigger;
        _awaitingResult = false;
        _requestedBy = null;
        Monitor.FastPolling = Shell.IsMainWindowVisible;
        OnOperationFinished(change.Previous, status, requestedBy);
    }

    private void OnOperationFinished(EngineStatus? previous, EngineStatus status, OperationTrigger? trigger)
    {
        var fromTray = trigger == OperationTrigger.Toolbar;
        var quiet = InQuietHours();

        if (status.RestartPending && previous?.RestartPending != true)
        {
            ShowRestartReminder(force: true);
            return;
        }
        if (status.Updates.Any(u => u.State == UpdateState.AwaitingApproval) && (fromTray || trigger == OperationTrigger.Schedule) && !quiet)
        {
            Notifications.BiosApprovalNeeded();
            return;
        }
        if ((status.Stage == EngineStage.Failed || status.Updates.Any(u => u.State == UpdateState.Failed)) && !Shell.IsMainWindowVisible && !quiet)
        {
            Notifications.Failed(status.LastError ?? status.Message);
            return;
        }
        if (trigger == OperationTrigger.Schedule && status.Overall == OverallStatus.UpdatesAvailable && !quiet)
        {
            Notifications.UpdatesAvailable(status.Updates.Count(u => u.State is UpdateState.Available or UpdateState.AwaitingApproval));
            return;
        }
        if (fromTray && status.Overall == OverallStatus.UpToDate)
            Notifications.Info("You're all good", "Your PC is up to date. No driver, BIOS or firmware updates are needed.");
    }

    private void MaybeRemindRestart()
    {
        if (Monitor.Status?.RestartPending == true) ShowRestartReminder(force: false);
    }

    private void ShowRestartReminder(bool force)
    {
        var prefs = UserPreferences.Current;
        if (!prefs.RestartNotifications) return;
        if (!force && (prefs.RestartSnoozedUntilUtc is { } until && DateTime.UtcNow < until || InQuietHours())) return;

        Notifications.RestartRequired(CanDeferRestart);
        prefs.RestartSnoozedUntilUtc = DateTime.UtcNow.AddMinutes(Math.Max(15, Monitor.Policy.RestartReminderMinutes));
        prefs.Save();
    }

    private void OnNotificationInvoked(IDictionary<string, string> args)
    {
        args.TryGetValue("action", out var action);
        switch (action)
        {
            case "restart": _ = RestartNowAsync(); break;
            case "later": _ = RestartLaterAsync(); break;
            case "updateall": UpdateAll(); break;
            case "review": Shell.OpenMainWindow("updates"); break;
            case "history": Shell.OpenMainWindow("history"); break;
            default: Shell.OpenMainWindow(); break;
        }
    }

    private bool InQuietHours()
    {
        var p = Monitor.Policy;
        if (!p.QuietHoursEnabled || p.QuietHoursStart == p.QuietHoursEnd) return false;
        var h = DateTime.Now.Hour;
        return p.QuietHoursStart < p.QuietHoursEnd
            ? h >= p.QuietHoursStart && h < p.QuietHoursEnd
            : h >= p.QuietHoursStart || h < p.QuietHoursEnd;
    }

    private static bool IsBusyStage(EngineStage? stage) => stage is EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing;

    private static string TooltipFor(EngineStatus? status, bool available)
    {
        if (!available || status is null) return "UpdateAll Win - service not running";
        var text = status.Overall switch
        {
            OverallStatus.UpToDate => "Up to date",
            OverallStatus.Busy => status.Message,
            OverallStatus.UpdatesAvailable => "Updates available",
            OverallStatus.Failed => "Last update failed",
            OverallStatus.RestartRequired => "Restart required",
            _ => "Ready",
        };
        var last = status.LastSuccessfulScanUtc is { } t ? $"\nLast scan {t.ToLocalTime():g}" : "";
        return $"UpdateAll Win - {text}{last}";
    }

    public void Dispose()
    {
        _reminderTimer.Stop();
        Monitor.Dispose();
        Notifications.Unregister();
        Tray.Dispose();
    }
}
