namespace UpdateAllWin.Contracts;

/// <summary>Which Windows Update driver source to use.</summary>
public enum WindowsUpdateMode
{
    /// <summary>Only when no manufacturer adapter supports this PC.</summary>
    Fallback,
    /// <summary>Alongside the manufacturer tool.</summary>
    Always,
    Never,
}

/// <summary>
/// Machine-wide policy enforced by UpdateAllService. Stored in %ProgramData% and only
/// changeable by administrators.
/// </summary>
public sealed record MachinePolicy
{
    // --- Scope ---
    public bool IncludeDrivers { get; init; } = true;
    public bool IncludeBios { get; init; } = true;
    public bool IncludeFirmware { get; init; } = true;
    public bool CriticalOnly { get; init; }
    public WindowsUpdateMode WindowsUpdate { get; init; } = WindowsUpdateMode.Fallback;
    public IReadOnlyList<string> ExcludedUpdates { get; init; } = [];

    // --- Schedule ---
    public ScanFrequency ScanFrequency { get; init; } = ScanFrequency.Daily;
    /// <summary>Local hour (0-23) for scheduled scans.</summary>
    public int ScanHour { get; init; } = 12;
    public DayOfWeek ScanDay { get; init; } = DayOfWeek.Wednesday;
    public bool AutoDownload { get; init; } = true;
    /// <summary>Install drivers (and firmware, if enabled) automatically after scheduled scans.</summary>
    public bool AutoInstallDrivers { get; init; }
    public bool AutoInstallFirmware { get; init; }

    // --- Quiet hours: no automatic scans/installs/notifications ---
    public bool QuietHoursEnabled { get; init; }
    public int QuietHoursStart { get; init; } = 22;
    public int QuietHoursEnd { get; init; } = 7;

    // --- BIOS safety ---
    public bool BiosApprovalRequired { get; init; } = true;
    public int BiosMinimumBatteryPercent { get; init; } = 50;
    /// <summary>Allow UpdateAll Win to suspend BitLocker for one restart when a BIOS update needs it.</summary>
    public bool AllowBitLockerSuspension { get; init; } = true;
    /// <summary>Block BIOS updates when BitLocker is on but no recovery password protector exists.</summary>
    public bool RequireBitLockerRecoveryKey { get; init; } = true;
    /// <summary>Path to an HP BIOS password file (created with HP's HPQPswd tool), if the BIOS is password-protected.</summary>
    public string? HpBiosPasswordFile { get; init; }

    // --- Install safety ---
    public bool CreateRestorePoint { get; init; } = true;
    public int MinimumFreeSpaceGb { get; init; } = 5;

    // --- Restart ---
    public int RestartReminderMinutes { get; init; } = 240;
    /// <summary>After this many hours "Restart later" is no longer offered. 0 = unlimited.</summary>
    public int MaxRestartDeferralHours { get; init; } = 72;

    // --- Housekeeping ---
    public int LogRetentionDays { get; init; } = 30;
    /// <summary>Null = %ProgramData%\UpdateAllWin\Downloads.</summary>
    public string? DownloadFolder { get; init; }
    public bool AllowStandardUsersToInstall { get; init; } = true;
    /// <summary>Delete settings and history when the product is uninstalled.</summary>
    public bool RemoveDataOnUninstall { get; init; }

    // --- Tools ---
    /// <summary>Optional path to an existing HPImageAssistant.exe (otherwise managed automatically).</summary>
    public string? HpiaPath { get; init; }

    // --- Development ---
    /// <summary>Use the simulated adapter instead of real manufacturer tools.</summary>
    public bool SimulationMode { get; init; }
    public string SimulationScenario { get; init; } = "UpdatesAvailable";
}
