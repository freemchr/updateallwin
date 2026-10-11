using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Infrastructure;

namespace UpdateAllWin.Core.Adapters;

public enum AdapterKind
{
    /// <summary>A manufacturer's own tool (HP, Dell, Lenovo...).</summary>
    Manufacturer,
    /// <summary>Works on any PC (Windows Update).</summary>
    Generic,
    /// <summary>Development/testing only.</summary>
    Simulation,
}

/// <summary>
/// A manufacturer integration. Adapters must report their real capabilities via
/// <see cref="Capabilities"/> rather than claiming functionality the vendor tool lacks;
/// the engine uses those flags to decide grouping, approval and safety behaviour.
/// </summary>
public interface IManufacturerAdapter
{
    /// <summary>Stable id, e.g. "hp-hpia".</summary>
    string Id { get; }
    string DisplayName { get; }
    AdapterKind Kind { get; }
    AdapterCapabilities Capabilities { get; }

    Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct);

    /// <summary>Makes sure the vendor tool is present and usable (may download/install it).</summary>
    Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext context, CancellationToken ct);

    Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext context, CancellationToken ct);

    /// <summary>Downloads without installing. Adapters without <see cref="AdapterCapabilities.Download"/> return NotSupported.</summary>
    Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext context, CancellationToken ct);

    /// <summary>Installs the given updates and sets each update's State/StatusMessage.</summary>
    Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext context, CancellationToken ct);

    /// <summary>Whether the vendor tool is currently busy (e.g. running from another source).</summary>
    Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct);

    Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct);
}

public sealed record PrerequisiteResult(bool Ready, string Message)
{
    public static PrerequisiteResult Ok(string message = "Ready") => new(true, message);
    public static PrerequisiteResult NotReady(string message) => new(false, message);
}

public sealed record AdapterOperationResult(bool Success, bool RestartRequired, string Message, int? ExitCode = null, bool NotSupported = false)
{
    public static AdapterOperationResult Unsupported(string message) => new(false, false, message, NotSupported: true);
}

public sealed record InstallationStatus(bool Busy, string Message);

public sealed record AdapterProgress(EngineStage Stage, string Message, int? Percent = null, string? CurrentItem = null);

/// <summary>Everything an adapter needs from the host, so adapters stay testable.</summary>
public sealed class AdapterContext
{
    public required MachinePolicy Policy { get; init; }
    public required DeviceInfo Device { get; init; }
    public required ILogger Logger { get; init; }
    public required IProcessRunner Processes { get; init; }
    public required IDownloader Downloader { get; init; }
    public required DataPaths Paths { get; init; }
    public IProgress<AdapterProgress>? Progress { get; init; }

    public void Report(EngineStage stage, string message, int? percent = null, string? item = null) =>
        Progress?.Report(new AdapterProgress(stage, message, percent, item));
}

/// <summary>An update found by an adapter. Mutable state is owned by the engine during an operation.</summary>
public sealed class DetectedUpdate
{
    public required string AdapterId { get; init; }
    /// <summary>The vendor's identifier (SoftPaq id, Dell release id, Windows Update id...).</summary>
    public required string VendorId { get; init; }
    public required string Name { get; init; }
    public string Source { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public UpdateCategory Category { get; init; } = UpdateCategory.Driver;
    public string CurrentVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
    public UpdatePriority Priority { get; init; } = UpdatePriority.Unknown;
    public long? SizeBytes { get; init; }
    public RestartBehavior Restart { get; init; } = RestartBehavior.Unknown;
    public string? ReleaseNotesUrl { get; init; }
    /// <summary>Adapter-private payload (COM object, descriptor...). Never serialised.</summary>
    public object? Tag { get; init; }

    public UpdateState State { get; set; } = UpdateState.Available;
    public string StatusMessage { get; set; } = "";
    public int? ExitCode { get; set; }

    /// <summary>Stable across scans for the same package version; used for exclusions and approvals.</summary>
    public string Key => $"{AdapterId}:{VendorId}:{AvailableVersion}".ToLowerInvariant();
}
