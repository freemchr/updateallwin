namespace UpdateAllWin.Contracts;

public enum UpdateCategory { Driver, Bios, Firmware, Software, Other }

public enum UpdatePriority { Critical, Recommended, Optional, Unknown }

public enum RestartBehavior { Unknown, NotRequired, MayBeRequired, Required }

public enum UpdateState
{
    Available,
    AwaitingApproval,
    Queued,
    Downloading,
    Installing,
    Installed,
    InstalledRestartRequired,
    Failed,
    Skipped,
    Excluded,
}

/// <summary>What the engine is doing right now.</summary>
public enum EngineStage { Idle, Scanning, Downloading, Installing, Complete, Failed, Cancelled }

/// <summary>Single status used by the toolbar/tray colour.</summary>
public enum OverallStatus
{
    /// <summary>Green.</summary>
    UpToDate,
    /// <summary>Blue.</summary>
    Busy,
    /// <summary>Orange.</summary>
    UpdatesAvailable,
    /// <summary>Red.</summary>
    Failed,
    /// <summary>Yellow.</summary>
    RestartRequired,
    /// <summary>Grey: never scanned / service unreachable.</summary>
    Unknown,
}

public enum ScanFrequency { Manual, Daily, Weekly }

public enum OperationTrigger { User, Toolbar, Schedule, CommandLine, Recovery }

public enum HistoryKind { Scan, Install, Failure, Restart, Approval, Service, Policy }

public enum ChassisKind { Unknown, Laptop, Desktop, Tablet, AllInOne, Server }

[Flags]
public enum AdapterCapabilities
{
    None = 0,
    Scan = 1,
    /// <summary>Downloads can happen separately from install.</summary>
    Download = 2,
    /// <summary>Individual updates can be chosen for install.</summary>
    InstallIndividual = 4,
    /// <summary>Install granularity is per category (e.g. "all recommended drivers").</summary>
    InstallByCategory = 8,
    /// <summary>The tool can only install everything it recommends.</summary>
    InstallAllOnly = 16,
    Drivers = 32,
    Bios = 64,
    Firmware = 128,
    ReportsDownloadSize = 256,
    ReportsCurrentVersion = 512,
    ReportsRestartPerUpdate = 1024,
    /// <summary>The tool suspends BitLocker itself for BIOS updates.</summary>
    HandlesBitLockerForBios = 2048,
}

public sealed record DeviceInfo
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>Machine type / product code (e.g. HP platform ID, Lenovo MTM).</summary>
    public string ProductCode { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string OsName { get; init; } = "";
    public string OsDisplayVersion { get; init; } = "";
    public string OsBuild { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public DateTime? BiosDate { get; init; }
    public string Cpu { get; init; } = "";
    public long RamBytes { get; init; }
    public long SystemDiskBytes { get; init; }
    public ChassisKind Chassis { get; init; }
    public bool HasBattery { get; init; }
    public int? BatteryPercent { get; init; }
    public bool OnAcPower { get; init; } = true;
}

public sealed record UpdateDto
{
    /// <summary>Stable key: adapter id + vendor id + version.</summary>
    public string Key { get; init; } = "";
    public string AdapterId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Name { get; init; } = "";
    public UpdateCategory Category { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
    public UpdatePriority Priority { get; init; } = UpdatePriority.Unknown;
    public long? SizeBytes { get; init; }
    public RestartBehavior Restart { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public UpdateState State { get; init; }
    public string StatusMessage { get; init; } = "";
    public bool RequiresApproval { get; init; }
    /// <summary>Install granularity note shown to the user, e.g. "Installed with all BIOS updates".</summary>
    public string GroupingNote { get; init; } = "";
}

public sealed record AdapterInfo(string Id, string DisplayName, AdapterCapabilities Capabilities, bool Available, string StatusMessage);

public sealed record EngineStatus
{
    public EngineStage Stage { get; init; }
    public string Message { get; init; } = "Ready";
    public int? Percent { get; init; }
    public string? CurrentItem { get; init; }
    public OperationTrigger? Trigger { get; init; }
    public OverallStatus Overall { get; init; } = OverallStatus.Unknown;
    public IReadOnlyList<UpdateDto> Updates { get; init; } = [];
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = [];
    public DateTime? LastScanUtc { get; init; }
    public DateTime? LastSuccessfulScanUtc { get; init; }
    public DateTime? NextScheduledScanUtc { get; init; }
    public bool RestartPending { get; init; }
    public DateTime? RestartPendingSinceUtc { get; init; }
    /// <summary>After this time the "Restart later" option is no longer offered.</summary>
    public DateTime? RestartDeferralEndsUtc { get; init; }
    public string? LastError { get; init; }
    public bool BiosInstallInProgress { get; init; }
    public bool SimulationMode { get; init; }
    public string ServiceVersion { get; init; } = "";
    /// <summary>Changes whenever anything in the status changes; lets clients skip redraws.</summary>
    public long Revision { get; init; }
}

public sealed record HistoryEntry
{
    public long Id { get; init; }
    public DateTime TimeUtc { get; init; }
    public HistoryKind Kind { get; init; }
    public OperationTrigger? Trigger { get; init; }
    public string Title { get; init; } = "";
    public string Result { get; init; } = "";
    public bool Success { get; init; }
    public string? AdapterId { get; init; }
    public string? UpdateKey { get; init; }
    public UpdateCategory? Category { get; init; }
    public string? FromVersion { get; init; }
    public string? ToVersion { get; init; }
    public int? ExitCode { get; init; }
    public string? Details { get; init; }
}

/// <summary>Results of the pre-install checks for a BIOS update, shown in the approval dialog.</summary>
public sealed record BiosSafetyReport
{
    public bool CanProceed { get; init; }
    public IReadOnlyList<SafetyCheck> Checks { get; init; } = [];
}

public sealed record SafetyCheck(string Name, SafetyCheckResult Result, string Detail);

public enum SafetyCheckResult { Pass, Warning, Fail }
