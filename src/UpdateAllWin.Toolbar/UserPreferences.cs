using System.Text.Json;
using Microsoft.Win32;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.Toolbar;

public enum AppTheme { System, Light, Dark }

/// <summary>Per-user preferences (%LocalAppData%\UpdateAllWin\preferences.json). Machine policy lives in the service.</summary>
public sealed class UserPreferences
{
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool ShowSerialNumber { get; set; }
    public bool RestartNotifications { get; set; } = true;
    public DateTime? RestartSnoozedUntilUtc { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateAllWin", "preferences.json");

    public static event EventHandler? Changed;

    public static UserPreferences Current { get; private set; } = Load();

    private static UserPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(FilePath), IpcProtocol.Json) ?? new();
        }
        catch { /* corrupt file: defaults */ }
        return new UserPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, IpcProtocol.Json));
        }
        catch { }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Per-user "start with Windows" entry (HKCU Run) that launches the tray agent.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UpdateAllWin";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            // When installed by the MSI, a machine-wide Run entry starts the agent for every user and the
            // agent exits early if this user turned "Start with Windows" off; no per-user entry is needed.
            if (enabled && !MachineEntryExists()) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static bool MachineEntryExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }
}
