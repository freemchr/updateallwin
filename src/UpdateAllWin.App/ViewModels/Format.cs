using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.Contracts;
using UpdateAllWin.Toolbar;

namespace UpdateAllWin.App.ViewModels;

/// <summary>Formatting helpers usable from x:Bind function bindings.</summary>
public static class Format
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;

    public static string Size(long? bytes) => bytes switch
    {
        null or <= 0 => "—",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 100L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:N1} MB".Replace(".0 MB", " MB"),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:N0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:N1} GB",
    };

    /// <summary>Marketing-style capacity: 15.7 GiB of RAM → "16 GB".</summary>
    public static string Capacity(long bytes)
    {
        if (bytes <= 0) return "";
        var gb = bytes / 1024.0 / 1024.0 / 1024.0;
        if (gb > 900) return $"{Math.Round(gb / 1024.0, 0)} TB";
        int[] sizes = [2, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 256, 512];
        var nearest = sizes.FirstOrDefault(s => s >= gb * 0.95);
        return nearest > 0 ? $"{nearest} GB" : $"{gb:N0} GB";
    }

    public static string Relative(DateTime? utc)
    {
        if (utc is not { } t) return "Never";
        var local = t.ToLocalTime();
        var span = DateTime.Now - local;
        if (span < TimeSpan.FromMinutes(1)) return "Just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (local.Date == DateTime.Today) return $"Today {local:t}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday {local:t}";
        return local.ToString("d MMM yyyy, t");
    }

    public static string CategoryText(UpdateCategory c) => c switch
    {
        UpdateCategory.Bios => "BIOS",
        UpdateCategory.Firmware => "Firmware",
        UpdateCategory.Driver => "Driver",
        UpdateCategory.Software => "Software",
        _ => "Other",
    };

    public static string CategoryGlyph(UpdateCategory c, string name)
    {
        var n = name.ToLowerInvariant();
        if (c == UpdateCategory.Bios) return "";        // chip
        if (c == UpdateCategory.Firmware) return "";    // firmware/database-ish
        if (n.Contains("audio") || n.Contains("sound")) return "";
        if (n.Contains("graphic") || n.Contains("display") || n.Contains("video")) return "";
        if (n.Contains("wi-fi") || n.Contains("wireless") || n.Contains("wlan") || n.Contains("network") || n.Contains("ethernet")) return "";
        if (n.Contains("bluetooth")) return "";
        if (n.Contains("camera") || n.Contains("webcam")) return "";
        if (n.Contains("touchpad") || n.Contains("keyboard") || n.Contains("input")) return "";
        if (n.Contains("storage") || n.Contains("nvme") || n.Contains("ssd") || n.Contains("rst")) return "";
        return "";
    }

    public static string StateText(UpdateState s) => s switch
    {
        UpdateState.Available => "Ready",
        UpdateState.AwaitingApproval => "Needs approval",
        UpdateState.Queued => "Queued",
        UpdateState.Downloading => "Downloading",
        UpdateState.Installing => "Installing",
        UpdateState.Installed => "Installed",
        UpdateState.InstalledRestartRequired => "Restart required",
        UpdateState.Failed => "Failed",
        UpdateState.Skipped => "Skipped",
        UpdateState.Excluded => "Excluded",
        _ => s.ToString(),
    };

    public static Brush StateBrush(UpdateState s) => new SolidColorBrush(s switch
    {
        UpdateState.Installed => StatusVisuals.ColorFor(OverallStatus.UpToDate),
        UpdateState.InstalledRestartRequired or UpdateState.AwaitingApproval => StatusVisuals.ColorFor(OverallStatus.RestartRequired),
        UpdateState.Failed => StatusVisuals.ColorFor(OverallStatus.Failed),
        UpdateState.Downloading or UpdateState.Installing or UpdateState.Queued => StatusVisuals.ColorFor(OverallStatus.Busy),
        UpdateState.Skipped => StatusVisuals.ColorFor(OverallStatus.UpdatesAvailable),
        _ => StatusVisuals.ColorFor(OverallStatus.Unknown),
    });

    public static string PriorityText(UpdatePriority p) => p == UpdatePriority.Unknown ? "—" : p.ToString();

    public static string RestartText(RestartBehavior r) => r switch
    {
        RestartBehavior.Required => "Required",
        RestartBehavior.MayBeRequired => "May be required",
        RestartBehavior.NotRequired => "No",
        _ => "—",
    };
}
