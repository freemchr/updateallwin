using CommunityToolkit.Mvvm.ComponentModel;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.App.ViewModels;

/// <summary>Editable copy of the machine policy for the Automation and Settings pages.</summary>
public sealed class PolicyEditor : ObservableObject
{
    private MachinePolicy _policy;

    public PolicyEditor(MachinePolicy policy) => _policy = policy;

    public MachinePolicy ToPolicy() => _policy;

    private void Set(MachinePolicy updated, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _policy = updated;
        IsDirty = true;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(IsDirty));
    }

    public bool IsDirty { get; private set; }

    // Scope
    public bool IncludeDrivers { get => _policy.IncludeDrivers; set => Set(_policy with { IncludeDrivers = value }); }
    public bool IncludeBios { get => _policy.IncludeBios; set => Set(_policy with { IncludeBios = value }); }
    public bool IncludeFirmware { get => _policy.IncludeFirmware; set => Set(_policy with { IncludeFirmware = value }); }
    public bool CriticalOnly { get => _policy.CriticalOnly; set => Set(_policy with { CriticalOnly = value }); }
    public int WindowsUpdateIndex { get => (int)_policy.WindowsUpdate; set => Set(_policy with { WindowsUpdate = (WindowsUpdateMode)Math.Max(0, value) }); }

    // Schedule
    public int ScanFrequencyIndex
    {
        get => (int)_policy.ScanFrequency;
        set
        {
            Set(_policy with { ScanFrequency = (ScanFrequency)Math.Max(0, value) });
            OnPropertyChanged(nameof(IsWeekly));
            OnPropertyChanged(nameof(IsScheduled));
        }
    }
    public bool IsWeekly => _policy.ScanFrequency == ScanFrequency.Weekly;
    public bool IsScheduled => _policy.ScanFrequency != ScanFrequency.Manual;
    public int ScanHour { get => _policy.ScanHour; set => Set(_policy with { ScanHour = Math.Max(0, value) }); }
    public int ScanDayIndex { get => (int)_policy.ScanDay; set => Set(_policy with { ScanDay = (DayOfWeek)Math.Max(0, value) }); }
    public bool AutoDownload { get => _policy.AutoDownload; set => Set(_policy with { AutoDownload = value }); }
    public bool AutoInstallDrivers { get => _policy.AutoInstallDrivers; set => Set(_policy with { AutoInstallDrivers = value }); }
    public bool AutoInstallFirmware { get => _policy.AutoInstallFirmware; set => Set(_policy with { AutoInstallFirmware = value }); }

    // Quiet hours
    public bool QuietHoursEnabled { get => _policy.QuietHoursEnabled; set => Set(_policy with { QuietHoursEnabled = value }); }
    public int QuietHoursStart { get => _policy.QuietHoursStart; set => Set(_policy with { QuietHoursStart = Math.Max(0, value) }); }
    public int QuietHoursEnd { get => _policy.QuietHoursEnd; set => Set(_policy with { QuietHoursEnd = Math.Max(0, value) }); }

    // BIOS safety
    public bool BiosApprovalRequired { get => _policy.BiosApprovalRequired; set => Set(_policy with { BiosApprovalRequired = value }); }
    public double BiosMinimumBatteryPercent { get => _policy.BiosMinimumBatteryPercent; set => Set(_policy with { BiosMinimumBatteryPercent = (int)value }); }
    public bool AllowBitLockerSuspension { get => _policy.AllowBitLockerSuspension; set => Set(_policy with { AllowBitLockerSuspension = value }); }
    public bool RequireBitLockerRecoveryKey { get => _policy.RequireBitLockerRecoveryKey; set => Set(_policy with { RequireBitLockerRecoveryKey = value }); }
    public string HpBiosPasswordFile { get => _policy.HpBiosPasswordFile ?? ""; set => Set(_policy with { HpBiosPasswordFile = value }); }

    // Install safety
    public bool CreateRestorePoint { get => _policy.CreateRestorePoint; set => Set(_policy with { CreateRestorePoint = value }); }
    public double MinimumFreeSpaceGb { get => _policy.MinimumFreeSpaceGb; set => Set(_policy with { MinimumFreeSpaceGb = (int)value }); }
    public bool AllowStandardUsersToInstall { get => _policy.AllowStandardUsersToInstall; set => Set(_policy with { AllowStandardUsersToInstall = value }); }

    // Restart
    private static readonly int[] ReminderChoices = [60, 120, 240, 480, 1440];
    private static readonly int[] DeferralChoices = [24, 48, 72, 168, 0];
    public int RestartReminderIndex
    {
        get => Math.Max(0, Array.IndexOf(ReminderChoices, _policy.RestartReminderMinutes) is var i && i >= 0 ? i : 2);
        set => Set(_policy with { RestartReminderMinutes = ReminderChoices[Math.Clamp(value, 0, ReminderChoices.Length - 1)] });
    }
    public int MaxDeferralIndex
    {
        get => Array.IndexOf(DeferralChoices, _policy.MaxRestartDeferralHours) is var i && i >= 0 ? i : 2;
        set => Set(_policy with { MaxRestartDeferralHours = DeferralChoices[Math.Clamp(value, 0, DeferralChoices.Length - 1)] });
    }

    // Housekeeping / tools
    public double LogRetentionDays { get => _policy.LogRetentionDays; set => Set(_policy with { LogRetentionDays = (int)value }); }
    public string DownloadFolder { get => _policy.DownloadFolder ?? ""; set => Set(_policy with { DownloadFolder = value }); }
    public string HpiaPath { get => _policy.HpiaPath ?? ""; set => Set(_policy with { HpiaPath = value }); }
    public bool RemoveDataOnUninstall { get => _policy.RemoveDataOnUninstall; set => Set(_policy with { RemoveDataOnUninstall = value }); }

    // Development
    public bool SimulationMode { get => _policy.SimulationMode; set => Set(_policy with { SimulationMode = value }); }
    private static readonly string[] Scenarios = ["UpdatesAvailable", "UpToDate", "DownloadFailure", "InstallFailure", "NetworkDown"];
    public int SimulationScenarioIndex
    {
        get => Math.Max(0, Array.IndexOf(Scenarios, _policy.SimulationScenario));
        set => Set(_policy with { SimulationScenario = Scenarios[Math.Clamp(value, 0, Scenarios.Length - 1)] });
    }
}
