using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace UpdateAllWin.Toolbar;

/// <summary>
/// Windows 11 notifications via the Windows App SDK. Works for unpackaged apps; note that
/// notifications cannot be shown from an elevated process, which is why this lives in the
/// per-user agent and not the service.
/// </summary>
public sealed class NotificationService
{
    private readonly string _logoPath;
    private bool _registered;

    public NotificationService(string logoPath) => _logoPath = logoPath;

    /// <summary>Raised when a notification or one of its buttons is clicked. Key "action" identifies it.</summary>
    public event Action<IDictionary<string, string>>? Invoked;

    public void Register()
    {
        if (_registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) => Invoked?.Invoke(args.Arguments);
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            // Notifications unavailable (e.g. elevated or policy-disabled); the tray and toolbar still show status.
        }
    }

    public void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }

    public void RestartRequired(bool allowLater)
    {
        var builder = Base("restart")
            .AddText("UpdateAll Win — Restart Required")
            .AddText("Your system updates have been installed successfully. Restart your computer to complete installation.")
            .AddButton(new AppNotificationButton("Restart Now").AddArgument("action", "restart"));
        if (allowLater) builder.AddButton(new AppNotificationButton("Restart Later").AddArgument("action", "later"));
        builder.AddButton(new AppNotificationButton("Open UpdateAll Win").AddArgument("action", "open"));
        builder.SetScenario(AppNotificationScenario.Reminder);
        Show(builder, "restart");
    }

    public void BiosApprovalNeeded() => Show(Base("review")
        .AddText("BIOS update needs your approval")
        .AddText("Drivers were handled automatically. Review the BIOS update before it's installed.")
        .AddButton(new AppNotificationButton("Review").AddArgument("action", "review")), "approval");

    public void UpdatesAvailable(int count) => Show(Base("open")
        .AddText($"{count} update{(count == 1 ? "" : "s")} available")
        .AddText("Driver, BIOS or firmware updates are ready for your PC.")
        .AddButton(new AppNotificationButton("Update All").AddArgument("action", "updateall"))
        .AddButton(new AppNotificationButton("View").AddArgument("action", "open")), "available");

    public void Failed(string message) => Show(Base("history")
        .AddText("Some updates didn't install")
        .AddText(message)
        .AddButton(new AppNotificationButton("View details").AddArgument("action", "history")), "failed");

    public void Info(string title, string body) => Show(Base("open").AddText(title).AddText(body), "info");

    private AppNotificationBuilder Base(string defaultAction)
    {
        var builder = new AppNotificationBuilder().AddArgument("action", defaultAction);
        if (File.Exists(_logoPath)) builder.SetAppLogoOverride(new Uri(_logoPath), AppNotificationImageCrop.Default);
        return builder;
    }

    private void Show(AppNotificationBuilder builder, string tag)
    {
        if (!_registered) return;
        try
        {
            var notification = builder.BuildNotification();
            notification.Tag = tag;
            notification.Group = "UpdateAllWin";
            AppNotificationManager.Default.Show(notification);
        }
        catch { }
    }
}
