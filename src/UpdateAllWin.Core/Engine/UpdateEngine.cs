using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.Core.Safety;
using UpdateAllWin.Core.Scheduling;

namespace UpdateAllWin.Core.Engine;

public enum OperationKind { Scan, UpdateAll, Install, Download }

public sealed record EngineOperation(
    OperationKind Kind,
    OperationTrigger Trigger,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyList<string>? ApprovedBiosKeys = null,
    /// <summary>Scheduled/background: applies the automatic-install policy instead of "install everything eligible".</summary>
    bool Automatic = false,
    /// <summary>Command-line "--approve-bios": an administrator explicitly approved BIOS updates.</summary>
    bool ApproveAllBios = false);

public sealed record OperationOutcome(
    OperationKind Kind,
    bool Success,
    int Found,
    int Installed,
    int Failed,
    int Skipped,
    int AwaitingApproval,
    bool RestartRequired,
    string Message);

public sealed class EngineOptions
{
    /// <summary>Machine-wide lock shared by the service and the command line. Disabled in tests.</summary>
    public bool UseGlobalLock { get; init; } = true;
    public string GlobalLockName { get; init; } = @"Global\UpdateAllWin.Engine";
}

/// <summary>
/// Orchestrates adapters: scanning, policy, BIOS safety gates, installing and restart tracking.
/// Exactly one operation runs at a time (per machine, when the global lock is enabled).
/// </summary>
public sealed class UpdateEngine : IDisposable
{
    private const string KeyLastScan = "scan.lastUtc";
    private const string KeyLastSuccess = "scan.lastSuccessUtc";
    private const string KeySnapshot = "updates.snapshot";
    private const string KeyOperation = "operation.current";
    private const string KeyRestartSince = "restart.pendingSinceUtc";
    private const string KeyRestartBoot = "restart.bootTimeUtc";
    private const string KeyRestartDeferrals = "restart.deferrals";

    private readonly IReadOnlyList<IManufacturerAdapter> _allAdapters;
    private readonly IPolicyStore _policies;
    private readonly IHistoryStore _history;
    private readonly IStateStore _state;
    private readonly IDeviceInfoProvider _device;
    private readonly ISystemSafety _safety;
    private readonly IProcessRunner _processes;
    private readonly IDownloader _downloader;
    private readonly DataPaths _paths;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Semaphore? _globalLock;
    private readonly object _sync = new();

    private List<DetectedUpdate> _updates = [];
    private bool _updatesAreSnapshot;
    private List<AdapterInfo> _adapterInfo = [];
    private IReadOnlyList<IManufacturerAdapter> _activeAdapters = [];
    private EngineStage _stage = EngineStage.Idle;
    private string _message = "Ready";
    private int? _percent;
    private string? _item;
    private OperationTrigger? _trigger;
    private string? _lastError;
    private bool _biosInProgress;
    private long _revision;
    private CancellationTokenSource? _cts;

    public UpdateEngine(
        IEnumerable<IManufacturerAdapter> adapters,
        IPolicyStore policies,
        IHistoryStore history,
        IStateStore state,
        IDeviceInfoProvider device,
        ISystemSafety safety,
        IProcessRunner processes,
        IDownloader downloader,
        DataPaths paths,
        ILoggerFactory loggers,
        TimeProvider? time = null,
        EngineOptions? options = null)
    {
        _allAdapters = adapters.ToList();
        _policies = policies;
        _history = history;
        _state = state;
        _device = device;
        _safety = safety;
        _processes = processes;
        _downloader = downloader;
        _paths = paths;
        _loggers = loggers;
        _log = loggers.CreateLogger<UpdateEngine>();
        _time = time ?? TimeProvider.System;
        options ??= new EngineOptions();
        if (options.UseGlobalLock)
            _globalLock = new Semaphore(1, 1, options.GlobalLockName);

        LoadSnapshot();
    }

    /// <summary>Raised whenever the status changes (from background threads).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised when an operation finishes.</summary>
    public event EventHandler<OperationOutcome>? OperationCompleted;

    public bool IsBusy => _gate.CurrentCount == 0;

    // ------------------------------------------------------------------ status

    public EngineStatus GetStatus()
    {
        var policy = _policies.Current;
        var (restartPending, since) = GetRestartState();
        lock (_sync)
        {
            var updates = _updates.Select(ToDto).ToList();
            return new EngineStatus
            {
                Stage = _stage,
                Message = _message,
                Percent = _percent,
                CurrentItem = _item,
                Trigger = _trigger,
                Updates = updates,
                Adapters = _adapterInfo.ToList(),
                LastScanUtc = ReadTime(KeyLastScan),
                LastSuccessfulScanUtc = ReadTime(KeyLastSuccess),
                NextScheduledScanUtc = Schedule.NextScan(policy, ReadTime(KeyLastScan)?.ToLocalTime(), _time.GetLocalNow().DateTime)?.ToUniversalTime(),
                RestartPending = restartPending,
                RestartPendingSinceUtc = since,
                RestartDeferralEndsUtc = since is { } s && policy.MaxRestartDeferralHours > 0 ? s.AddHours(policy.MaxRestartDeferralHours) : null,
                LastError = _lastError,
                BiosInstallInProgress = _biosInProgress,
                SimulationMode = policy.SimulationMode,
                Overall = ComputeOverall(updates, restartPending),
                ServiceVersion = typeof(UpdateEngine).Assembly.GetName().Version?.ToString(3) ?? "",
                Revision = Interlocked.Read(ref _revision),
            };
        }
    }

    private OverallStatus ComputeOverall(IReadOnlyList<UpdateDto> updates, bool restartPending)
    {
        if (_stage is EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing) return OverallStatus.Busy;
        if (restartPending) return OverallStatus.RestartRequired;
        if (_stage == EngineStage.Failed || updates.Any(u => u.State == UpdateState.Failed)) return OverallStatus.Failed;
        if (updates.Any(u => u.State is UpdateState.Available or UpdateState.AwaitingApproval or UpdateState.Skipped)) return OverallStatus.UpdatesAvailable;
        return ReadTime(KeyLastSuccess) is null ? OverallStatus.Unknown : OverallStatus.UpToDate;
    }

    private UpdateDto ToDto(DetectedUpdate u)
    {
        var adapter = _allAdapters.FirstOrDefault(a => a.Id == u.AdapterId);
        var caps = adapter?.Capabilities ?? AdapterCapabilities.None;
        var note = caps.HasFlag(AdapterCapabilities.InstallAllOnly)
            ? $"{adapter!.DisplayName} installs all of its recommended updates together"
            : caps.HasFlag(AdapterCapabilities.InstallByCategory)
                ? $"Installed together with all {u.Category} updates from {adapter!.DisplayName}"
                : "";
        return new UpdateDto
        {
            Key = u.Key,
            AdapterId = u.AdapterId,
            Source = adapter?.DisplayName ?? u.Source,
            Manufacturer = u.Manufacturer,
            Name = u.Name,
            Category = u.Category,
            CurrentVersion = u.CurrentVersion,
            AvailableVersion = u.AvailableVersion,
            Priority = u.Priority,
            SizeBytes = u.SizeBytes,
            Restart = u.Category == UpdateCategory.Bios && u.Restart == RestartBehavior.Unknown ? RestartBehavior.Required : u.Restart,
            ReleaseNotesUrl = u.ReleaseNotesUrl,
            State = u.State,
            StatusMessage = u.StatusMessage,
            RequiresApproval = u.Category == UpdateCategory.Bios && _policies.Current.BiosApprovalRequired,
            GroupingNote = note,
        };
    }

    // ------------------------------------------------------------------ starting operations

    /// <summary>Starts an operation in the background. Returns false if one is already running.</summary>
    public bool TryStart(EngineOperation operation)
    {
        if (!TryAcquire()) return false;
        _ = Task.Run(async () =>
        {
            try { await RunAcquiredAsync(operation, _cts!.Token); }
            catch (Exception ex) { _log.LogError(ex, "Operation {Kind} crashed", operation.Kind); }
            finally { Release(); }
        });
        return true;
    }

    /// <summary>Runs an operation and waits for it. Returns null if another operation is running.</summary>
    public async Task<OperationOutcome?> RunAsync(EngineOperation operation, CancellationToken ct = default)
    {
        if (!TryAcquire()) return null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts!.Token);
            return await RunAcquiredAsync(operation, linked.Token);
        }
        finally
        {
            Release();
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private bool TryAcquire()
    {
        if (!_gate.Wait(0)) return false;
        if (_globalLock is not null && !_globalLock.WaitOne(0))
        {
            _gate.Release();
            _log.LogWarning("Another UpdateAll Win process is already updating this PC");
            return false;
        }
        _cts = new CancellationTokenSource();
        return true;
    }

    private void Release()
    {
        _cts?.Dispose();
        _cts = null;
        _globalLock?.Release();
        _gate.Release();
        Changed();
    }

    private async Task<OperationOutcome> RunAcquiredAsync(EngineOperation op, CancellationToken ct)
    {
        _trigger = op.Trigger;
        _lastError = null;
        using (var who = System.Security.Principal.WindowsIdentity.GetCurrent())
            _log.LogDebug("{Kind} running as {User} (impersonation {Level})", op.Kind, who.Name, who.ImpersonationLevel);
        _state.SetJson(KeyOperation, new PersistedOperation(op.Kind, op.Trigger, _time.GetUtcNow().UtcDateTime));
        OperationOutcome outcome;
        try
        {
            outcome = op.Kind switch
            {
                OperationKind.Scan => await ScanCoreAsync(op.Trigger, ct),
                OperationKind.UpdateAll => await UpdateAllCoreAsync(op, ct),
                OperationKind.Install => await InstallCoreAsync(op.Keys ?? [], ApprovedSet(op), op.Trigger, ct),
                OperationKind.Download => await DownloadEligibleAsync(op.Trigger, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            SetStage(outcome.Success ? EngineStage.Complete : EngineStage.Failed, outcome.Message);
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
                foreach (var u in _updates.Where(u => u.State is UpdateState.Queued or UpdateState.Downloading or UpdateState.Installing))
                {
                    u.State = UpdateState.Available;
                    u.StatusMessage = "Cancelled";
                }
            outcome = new OperationOutcome(op.Kind, false, 0, 0, 0, 0, 0, false, "Cancelled");
            SetStage(EngineStage.Cancelled, "Cancelled");
            AddHistory(HistoryKind.Service, op.Trigger, $"{op.Kind} cancelled", "Cancelled", false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Kind} failed", op.Kind);
            _lastError = ex.Message;
            outcome = new OperationOutcome(op.Kind, false, 0, 0, 0, 0, 0, false, ex.Message);
            SetStage(EngineStage.Failed, ex.Message);
            AddHistory(HistoryKind.Failure, op.Trigger, $"{op.Kind} failed", ex.Message, false, details: ex.ToString());
        }
        finally
        {
            _biosInProgress = false;
            _state.Set(KeyOperation, null);
            SaveSnapshot();
        }

        OperationCompleted?.Invoke(this, outcome);
        return outcome;
    }

    private HashSet<string> ApprovedSet(EngineOperation op)
    {
        var set = new HashSet<string>(op.ApprovedBiosKeys ?? [], StringComparer.OrdinalIgnoreCase);
        if (op.ApproveAllBios)
            lock (_sync)
                foreach (var u in _updates.Where(u => u.Category == UpdateCategory.Bios)) set.Add(u.Key);
        return set;
    }

    // ------------------------------------------------------------------ scan

    private async Task<OperationOutcome> ScanCoreAsync(OperationTrigger trigger, CancellationToken ct)
    {
        SetStage(EngineStage.Scanning, "Detecting your PC...", 0);
        var policy = _policies.Current;
        var device = _device.GetDeviceInfo(refresh: true);
        var adapters = await SelectAdaptersAsync(device, policy, ct);
        _activeAdapters = adapters;

        var found = new List<DetectedUpdate>();
        var infos = new List<AdapterInfo>();
        var errors = new List<string>();
        var anySucceeded = false;

        if (adapters.Count == 0)
            errors.Add("No update source supports this PC. Enable Windows Update drivers in Settings.");

        for (var i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];
            var basePercent = i * 100 / adapters.Count;
            SetStage(EngineStage.Scanning, $"Checking {adapter.DisplayName}...", basePercent);
            var ctx = CreateContext(adapter, policy, device);
            try
            {
                var prereq = await adapter.CheckPrerequisitesAsync(ctx, ct);
                if (!prereq.Ready)
                {
                    infos.Add(new AdapterInfo(adapter.Id, adapter.DisplayName, adapter.Capabilities, false, prereq.Message));
                    errors.Add($"{adapter.DisplayName}: {prereq.Message}");
                    continue;
                }

                var results = await adapter.ScanForUpdatesAsync(ctx, ct);
                found.AddRange(results);
                anySucceeded = true;
                infos.Add(new AdapterInfo(adapter.Id, adapter.DisplayName, adapter.Capabilities, true,
                    $"{results.Count} update(s) found"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Adapter} scan failed", adapter.DisplayName);
                infos.Add(new AdapterInfo(adapter.Id, adapter.DisplayName, adapter.Capabilities, false, ex.Message));
                errors.Add($"{adapter.DisplayName}: {ex.Message}");
            }
        }

        var visible = ApplyScanPolicy(found, policy);
        lock (_sync)
        {
            _updates = visible;
            _updatesAreSnapshot = false;
            _adapterInfo = infos;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        _state.Set(KeyLastScan, now.ToString("O"));
        if (anySucceeded) _state.Set(KeyLastSuccess, now.ToString("O"));
        _lastError = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;

        var count = visible.Count(u => u.State != UpdateState.Excluded);
        var message = !anySucceeded ? "Couldn't check for updates"
            : count == 0 ? "Your PC is up to date"
            : $"{count} update{(count == 1 ? "" : "s")} available";
        AddHistory(anySucceeded ? HistoryKind.Scan : HistoryKind.Failure, trigger, "Update scan", message, anySucceeded,
            details: _lastError);
        SetStage(anySucceeded ? EngineStage.Complete : EngineStage.Failed, message, 100);

        return new OperationOutcome(OperationKind.Scan, anySucceeded, count, 0, 0, 0, 0, GetRestartState().Pending, message);
    }

    private async Task<IReadOnlyList<IManufacturerAdapter>> SelectAdaptersAsync(DeviceInfo device, MachinePolicy policy, CancellationToken ct)
    {
        if (policy.SimulationMode)
            return _allAdapters.Where(a => a.Kind == AdapterKind.Simulation).ToList();

        var manufacturer = new List<IManufacturerAdapter>();
        var generic = new List<IManufacturerAdapter>();
        foreach (var adapter in _allAdapters.Where(a => a.Kind != AdapterKind.Simulation))
        {
            if (!await adapter.IsSupportedAsync(device, ct)) continue;
            (adapter.Kind == AdapterKind.Manufacturer ? manufacturer : generic).Add(adapter);
        }

        var useGeneric = policy.WindowsUpdate switch
        {
            WindowsUpdateMode.Always => true,
            WindowsUpdateMode.Never => false,
            _ => manufacturer.Count == 0,
        };
        return useGeneric ? [.. manufacturer, .. generic] : manufacturer;
    }

    private static List<DetectedUpdate> ApplyScanPolicy(IEnumerable<DetectedUpdate> found, MachinePolicy policy)
    {
        var excluded = new HashSet<string>(policy.ExcludedUpdates, StringComparer.OrdinalIgnoreCase);
        var list = new List<DetectedUpdate>();
        foreach (var u in found)
        {
            var included = u.Category switch
            {
                UpdateCategory.Bios => policy.IncludeBios,
                UpdateCategory.Firmware => policy.IncludeFirmware,
                UpdateCategory.Driver => policy.IncludeDrivers,
                _ => true,
            };
            if (!included) continue;
            if (policy.CriticalOnly && u.Priority != UpdatePriority.Critical) continue;
            if (excluded.Contains(u.Key))
            {
                u.State = UpdateState.Excluded;
                u.StatusMessage = "Excluded by policy";
            }
            list.Add(u);
        }
        return list
            .OrderBy(u => u.State == UpdateState.Excluded)
            .ThenBy(u => u.Category switch { UpdateCategory.Bios => 0, UpdateCategory.Firmware => 1, UpdateCategory.Driver => 2, _ => 3 })
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ update all

    private async Task<OperationOutcome> UpdateAllCoreAsync(EngineOperation op, CancellationToken ct)
    {
        var scan = await ScanCoreAsync(op.Trigger, ct);
        if (!scan.Success) return scan with { Kind = OperationKind.UpdateAll };

        var policy = _policies.Current;
        List<string> keys;
        lock (_sync)
        {
            keys = _updates
                .Where(u => u.State == UpdateState.Available && IsEligible(u, policy, op.Automatic))
                .Select(u => u.Key)
                .ToList();
        }

        if (keys.Count == 0)
        {
            var awaiting = CountAwaitingApprovalCandidates(policy);
            var msg = scan.Found == 0 ? "Your PC is up to date"
                : awaiting > 0 ? "A BIOS update is waiting for your approval"
                : "No updates are eligible for automatic installation";
            if (awaiting > 0) MarkBiosAwaitingApproval(policy, ApprovedSet(op));
            return new OperationOutcome(OperationKind.UpdateAll, true, scan.Found, 0, 0, 0, awaiting, GetRestartState().Pending, msg);
        }

        var result = await InstallCoreAsync(keys, ApprovedSet(op), op.Trigger, ct);
        return result with { Kind = OperationKind.UpdateAll, Found = scan.Found };
    }

    private static bool IsEligible(DetectedUpdate u, MachinePolicy policy, bool automatic)
    {
        if (!automatic) return true; // BIOS approval is enforced later in InstallCore
        return u.Category switch
        {
            UpdateCategory.Bios => !policy.BiosApprovalRequired && policy.AutoInstallFirmware,
            UpdateCategory.Firmware => policy.AutoInstallFirmware,
            _ => policy.AutoInstallDrivers,
        };
    }

    private int CountAwaitingApprovalCandidates(MachinePolicy policy)
    {
        lock (_sync)
            return _updates.Count(u => u.Category == UpdateCategory.Bios && policy.BiosApprovalRequired
                                       && u.State is UpdateState.Available or UpdateState.AwaitingApproval);
    }

    private void MarkBiosAwaitingApproval(MachinePolicy policy, HashSet<string> approved)
    {
        if (!policy.BiosApprovalRequired) return;
        lock (_sync)
            foreach (var u in _updates.Where(u => u.Category == UpdateCategory.Bios && u.State == UpdateState.Available && !approved.Contains(u.Key)))
            {
                u.State = UpdateState.AwaitingApproval;
                u.StatusMessage = "Needs your approval";
            }
        Changed();
    }

    // ------------------------------------------------------------------ download only (scheduled pre-staging)

    private async Task<OperationOutcome> DownloadEligibleAsync(OperationTrigger trigger, CancellationToken ct)
    {
        var scan = await ScanCoreAsync(trigger, ct);
        if (!scan.Success || scan.Found == 0) return scan with { Kind = OperationKind.Download };

        var policy = _policies.Current;
        var device = _device.GetDeviceInfo();
        var downloaded = 0;
        foreach (var group in SnapshotUpdates().Where(u => u.State == UpdateState.Available).GroupBy(u => u.AdapterId))
        {
            var adapter = _activeAdapters.FirstOrDefault(a => a.Id == group.Key);
            if (adapter is null || !adapter.Capabilities.HasFlag(AdapterCapabilities.Download)) continue;
            var items = group.Where(u => u.Category != UpdateCategory.Bios || !policy.BiosApprovalRequired).ToList();
            if (items.Count == 0) continue;

            SetStage(EngineStage.Downloading, $"Downloading {items.Count} update(s) from {adapter.DisplayName}...");
            var result = await adapter.DownloadUpdatesAsync(items, CreateContext(adapter, policy, device), ct);
            foreach (var u in items)
            {
                u.State = UpdateState.Available;
                u.StatusMessage = result.Success ? "Downloaded - ready to install" : $"Download failed: {result.Message}";
            }
            if (result.Success) downloaded += items.Count;
            AddHistory(result.Success ? HistoryKind.Install : HistoryKind.Failure, trigger,
                $"Downloaded updates from {adapter.DisplayName}", result.Message, result.Success, adapter.Id, exitCode: result.ExitCode);
        }
        return scan with { Kind = OperationKind.Download, Message = downloaded > 0 ? $"{downloaded} update(s) downloaded and ready to install" : scan.Message };
    }

    // ------------------------------------------------------------------ install

    private async Task<OperationOutcome> InstallCoreAsync(IReadOnlyList<string> keys, HashSet<string> approvedBios, OperationTrigger trigger, CancellationToken ct)
    {
        if (_updatesAreSnapshot)
        {
            // Results restored after a service restart have no live adapter state; rescan first.
            await ScanCoreAsync(trigger, ct);
        }

        var policy = _policies.Current;
        var device = _device.GetDeviceInfo(refresh: true);
        var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var all = SnapshotUpdates();
        var targets = all.Where(u => keySet.Contains(u.Key) && u.State is not (UpdateState.Excluded or UpdateState.Installed or UpdateState.InstalledRestartRequired)).ToList();
        var skipped = new List<DetectedUpdate>();
        var awaiting = new List<DetectedUpdate>();

        if (targets.Count == 0)
            return new OperationOutcome(OperationKind.Install, true, 0, 0, 0, 0, 0, GetRestartState().Pending, "Nothing to install");

        // 1. Disk space.
        var minFree = (long)policy.MinimumFreeSpaceGb * 1024 * 1024 * 1024;
        if (_safety.GetSystemDriveFreeBytes() < minFree)
        {
            foreach (var u in targets) Skip(u, $"Less than {policy.MinimumFreeSpaceGb} GB free on the system drive");
            AddHistory(HistoryKind.Failure, trigger, "Install blocked", $"Less than {policy.MinimumFreeSpaceGb} GB free disk space", false);
            return Summarise(OperationKind.Install, targets, false, "Not enough free disk space");
        }

        // 2. BIOS approval and safety gates.
        foreach (var bios in targets.Where(u => u.Category == UpdateCategory.Bios).ToList())
        {
            if (policy.BiosApprovalRequired && !approvedBios.Contains(bios.Key))
            {
                bios.State = UpdateState.AwaitingApproval;
                bios.StatusMessage = "Needs your approval";
                awaiting.Add(bios);
                targets.Remove(bios);
                continue;
            }

            var report = BuildBiosSafetyReport([bios], policy, device);
            if (!report.CanProceed)
            {
                var reasons = string.Join("; ", report.Checks.Where(c => c.Result == SafetyCheckResult.Fail).Select(c => c.Detail));
                Skip(bios, reasons);
                skipped.Add(bios);
                targets.Remove(bios);
                AddHistory(HistoryKind.Failure, trigger, $"BIOS update blocked: {bios.Name}", reasons, false, bios.AdapterId, bios.Key, bios.Category);
            }
            else if (approvedBios.Contains(bios.Key))
            {
                AddHistory(HistoryKind.Approval, trigger, $"BIOS update approved: {bios.Name}", "Approved", true, bios.AdapterId, bios.Key, bios.Category,
                    bios.CurrentVersion, bios.AvailableVersion);
            }
        }

        // 3. Respect how each vendor tool groups installs, so selection can't bypass approval/exclusions.
        foreach (var group in targets.GroupBy(u => u.AdapterId).ToList())
        {
            var adapter = FindAdapter(group.Key);
            if (adapter is null) continue;
            var siblings = all.Where(u => u.AdapterId == adapter.Id).ToList();

            if (adapter.Capabilities.HasFlag(AdapterCapabilities.InstallAllOnly))
            {
                var blocker = siblings.FirstOrDefault(u => u.State == UpdateState.Excluded
                    || (u.Category == UpdateCategory.Bios && !targets.Contains(u)));
                if (blocker is not null)
                {
                    foreach (var u in group) { Skip(u, $"{adapter.DisplayName} installs all its updates together, including '{blocker.Name}', which is excluded or not approved."); skipped.Add(u); targets.Remove(u); }
                    continue;
                }
                foreach (var u in siblings.Where(u => u.State == UpdateState.Available && !targets.Contains(u))) targets.Add(u);
            }
            else if (adapter.Capabilities.HasFlag(AdapterCapabilities.InstallByCategory))
            {
                foreach (var category in group.Select(u => u.Category).Distinct().ToList())
                {
                    var inCategory = siblings.Where(u => u.Category == category).ToList();
                    var blocker = inCategory.FirstOrDefault(u => u.State == UpdateState.Excluded
                        || (category == UpdateCategory.Bios && !targets.Contains(u)));
                    if (blocker is not null)
                    {
                        foreach (var u in group.Where(u => u.Category == category))
                        {
                            Skip(u, $"{adapter.DisplayName} installs all {category} updates together, and '{blocker.Name}' is excluded or not approved.");
                            skipped.Add(u);
                            targets.Remove(u);
                        }
                        continue;
                    }
                    foreach (var u in inCategory.Where(u => u.State == UpdateState.Available && !targets.Contains(u))) targets.Add(u);
                }
            }
        }

        if (targets.Count == 0)
        {
            Changed();
            var msg = awaiting.Count > 0 ? "A BIOS update is waiting for your approval" : "Nothing could be installed";
            return Summarise(OperationKind.Install, [.. skipped, .. awaiting], awaiting.Count > 0 && skipped.Count == 0, msg);
        }

        foreach (var u in targets) { u.State = UpdateState.Queued; u.StatusMessage = ""; }
        Changed();

        // 4. Restore point (non-fatal).
        if (policy.CreateRestorePoint)
        {
            SetStage(EngineStage.Installing, "Creating a restore point...");
            if (!policy.SimulationMode)
            {
                var ok = _safety.TryCreateRestorePoint("UpdateAll Win - before installing updates", out var detail);
                _log.LogInformation("Restore point: {Detail}", detail);
                if (!ok) AddHistory(HistoryKind.Service, trigger, "Restore point not created", detail, false);
            }
        }

        // 5. Install per adapter.
        var restartRequired = false;
        using (_safety.PreventSleep("UpdateAll Win is installing driver and firmware updates"))
        {
            foreach (var group in targets.GroupBy(u => u.AdapterId))
            {
                ct.ThrowIfCancellationRequested();
                var adapter = FindAdapter(group.Key);
                var items = group.ToList();
                if (adapter is null)
                {
                    foreach (var u in items) Fail(u, "Update source is no longer available; check again");
                    continue;
                }

                var ctx = CreateContext(adapter, policy, device);
                try
                {
                    if (adapter.Capabilities.HasFlag(AdapterCapabilities.Download))
                    {
                        foreach (var u in items) u.State = UpdateState.Downloading;
                        SetStage(EngineStage.Downloading, $"Downloading {items.Count} update(s) from {adapter.DisplayName}...", 0);
                        var download = await adapter.DownloadUpdatesAsync(items, ctx, ct);
                        if (!download.Success && !download.NotSupported)
                        {
                            foreach (var u in items) Fail(u, $"Download failed: {download.Message}", download.ExitCode);
                            AddHistory(HistoryKind.Failure, trigger, $"Download failed ({adapter.DisplayName})", download.Message, false, adapter.Id, exitCode: download.ExitCode);
                            continue;
                        }
                    }

                    var biosItems = items.Where(u => u.Category == UpdateCategory.Bios).ToList();
                    if (biosItems.Count > 0 && !PrepareBitLockerForBios(adapter, policy, biosItems, trigger))
                    {
                        items = items.Except(biosItems).ToList();
                        if (items.Count == 0) continue;
                    }

                    _biosInProgress = items.Any(u => u.Category == UpdateCategory.Bios);
                    foreach (var u in items) u.State = UpdateState.Installing;
                    SetStage(EngineStage.Installing,
                        _biosInProgress ? $"Installing updates including BIOS with {adapter.DisplayName} - do not turn off your PC"
                                        : $"Installing {items.Count} update(s) with {adapter.DisplayName}...", 0);

                    var result = await adapter.InstallUpdatesAsync(items, ctx, ct);
                    restartRequired |= result.RestartRequired || items.Any(u => u.State == UpdateState.InstalledRestartRequired);

                    foreach (var u in items.Where(u => u.State is UpdateState.Installing or UpdateState.Queued or UpdateState.Downloading))
                        if (result.Success) Succeed(u, result.RestartRequired);
                        else Fail(u, result.Message, result.ExitCode);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "{Adapter} install failed", adapter.DisplayName);
                    foreach (var u in items.Where(u => u.State is not (UpdateState.Installed or UpdateState.InstalledRestartRequired)))
                        Fail(u, ex.Message);
                }
                finally
                {
                    _biosInProgress = false;
                }

                foreach (var u in items)
                    AddHistory(u.State is UpdateState.Installed or UpdateState.InstalledRestartRequired ? HistoryKind.Install : HistoryKind.Failure,
                        trigger, u.Name, u.State == UpdateState.InstalledRestartRequired ? "Installed - restart required" : u.State == UpdateState.Installed ? "Installed" : $"Failed: {u.StatusMessage}",
                        u.State is UpdateState.Installed or UpdateState.InstalledRestartRequired, u.AdapterId, u.Key, u.Category, u.CurrentVersion, u.AvailableVersion, u.ExitCode);
                Changed();
            }
        }

        if (restartRequired || _safety.IsRestartPendingFromOs()) MarkRestartRequired();

        var processed = targets.Concat(skipped).Concat(awaiting).Distinct().ToList();
        var installed = processed.Count(u => u.State is UpdateState.Installed or UpdateState.InstalledRestartRequired);
        var failed = processed.Count(u => u.State == UpdateState.Failed);
        var message = failed > 0 ? $"{installed} installed, {failed} failed"
            : installed > 0 ? $"{installed} update{(installed == 1 ? "" : "s")} installed" + (restartRequired ? " - restart required" : "")
            : awaiting.Count > 0 ? "A BIOS update is waiting for your approval"
            : "No updates were installed";
        return Summarise(OperationKind.Install, processed, failed == 0, message);
    }

    /// <summary>Suspends BitLocker for one restart when policy allows and the vendor tool doesn't handle it.</summary>
    private bool PrepareBitLockerForBios(IManufacturerAdapter adapter, MachinePolicy policy, List<DetectedUpdate> biosItems, OperationTrigger trigger)
    {
        if (policy.SimulationMode || adapter.Capabilities.HasFlag(AdapterCapabilities.HandlesBitLockerForBios)) return true;
        var state = _safety.GetBitLockerState();
        if (!state.ProtectionOn || !policy.AllowBitLockerSuspension) return true;

        if (_safety.SuspendBitLockerForOneRestart(out var detail))
        {
            AddHistory(HistoryKind.Service, trigger, "BitLocker suspended for one restart", detail, true);
            return true;
        }

        foreach (var u in biosItems) Skip(u, $"BIOS update skipped: {detail}");
        AddHistory(HistoryKind.Failure, trigger, "BIOS update skipped", detail, false);
        return false;
    }

    // ------------------------------------------------------------------ BIOS safety report

    public BiosSafetyReport GetBiosSafetyReport(IReadOnlyList<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var items = SnapshotUpdates().Where(u => set.Contains(u.Key) && u.Category == UpdateCategory.Bios).ToList();
        return BuildBiosSafetyReport(items, _policies.Current, _device.GetDeviceInfo());
    }

    private BiosSafetyReport BuildBiosSafetyReport(IReadOnlyList<DetectedUpdate> items, MachinePolicy policy, DeviceInfo device)
    {
        var checks = new List<SafetyCheck>();
        var adapters = items.Select(u => FindAdapter(u.AdapterId)).Distinct().ToList();

        if (policy.SimulationMode)
            checks.Add(new("Device compatibility", SafetyCheckResult.Warning, "Simulation mode - no real firmware will be flashed"));
        else if (adapters.Count > 0 && adapters.All(a => a is { Kind: AdapterKind.Manufacturer }))
            checks.Add(new("Device compatibility", SafetyCheckResult.Pass,
                $"Matched to {device.Manufacturer} {device.Model} by {string.Join(", ", adapters.Select(a => a!.DisplayName))}"));
        else
            checks.Add(new("Device compatibility", SafetyCheckResult.Fail, "BIOS updates are only installed through the manufacturer's own tool"));

        checks.Add(new("Package source", SafetyCheckResult.Pass,
            "Downloaded from the manufacturer and signature-checked by the manufacturer's tool"));

        var power = _safety.GetPower();
        checks.Add(power.OnAcPower
            ? new("AC power", SafetyCheckResult.Pass, "Connected to AC power")
            : new("AC power", SafetyCheckResult.Fail, "Connect the power adapter before updating the BIOS"));

        if (!power.HasBattery)
            checks.Add(new("Battery", SafetyCheckResult.Pass, "No battery (desktop PC)"));
        else if (power.BatteryPercent >= policy.BiosMinimumBatteryPercent)
            checks.Add(new("Battery", SafetyCheckResult.Pass, $"Battery at {power.BatteryPercent}%"));
        else
            checks.Add(new("Battery", SafetyCheckResult.Fail, $"Battery at {power.BatteryPercent}% - charge to at least {policy.BiosMinimumBatteryPercent}%"));

        var bl = _safety.GetBitLockerState();
        var toolHandles = adapters.All(a => a?.Capabilities.HasFlag(AdapterCapabilities.HandlesBitLockerForBios) == true);
        if (!bl.Known)
            checks.Add(new("BitLocker", SafetyCheckResult.Warning, bl.Detail));
        else if (!bl.ProtectionOn)
            checks.Add(new("BitLocker", SafetyCheckResult.Pass, "BitLocker protection is off"));
        else if (!bl.HasRecoveryPassword && policy.RequireBitLockerRecoveryKey)
            checks.Add(new("BitLocker", SafetyCheckResult.Fail, "BitLocker is on but no recovery key protector exists - back up a recovery key first"));
        else if (!policy.AllowBitLockerSuspension)
            checks.Add(new("BitLocker", SafetyCheckResult.Warning, "BitLocker suspension is turned off by policy - you may be asked for your recovery key after the update"));
        else if (toolHandles)
            checks.Add(new("BitLocker", SafetyCheckResult.Pass, "The manufacturer's tool suspends BitLocker for the BIOS update"));
        else
            checks.Add(new("BitLocker", SafetyCheckResult.Pass, "BitLocker will be suspended for one restart, then resumes automatically"));

        var free = _safety.GetSystemDriveFreeBytes();
        checks.Add(free >= (long)policy.MinimumFreeSpaceGb * 1024 * 1024 * 1024
            ? new("Disk space", SafetyCheckResult.Pass, $"{free / 1024 / 1024 / 1024:N0} GB free")
            : new("Disk space", SafetyCheckResult.Fail, $"Less than {policy.MinimumFreeSpaceGb} GB free"));

        checks.Add(new("Restart", SafetyCheckResult.Warning,
            "Your PC will restart to apply the BIOS update. Do not turn it off or unplug it until Windows has started again."));

        return new BiosSafetyReport { CanProceed = checks.All(c => c.Result != SafetyCheckResult.Fail), Checks = checks };
    }

    // ------------------------------------------------------------------ restart tracking

    public (bool Pending, DateTime? SinceUtc) GetRestartState()
    {
        var since = ReadTime(KeyRestartSince);
        if (since is not null && RestartHasHappened())
        {
            ClearRestartRequired("Restart completed");
            since = null;
        }
        var osPending = !_policies.Current.SimulationMode && _safety.IsRestartPendingFromOs();
        return (since is not null || osPending, since);
    }

    private bool RestartHasHappened()
    {
        var recordedBoot = ReadTime(KeyRestartBoot);
        return recordedBoot is not null && _safety.GetBootTimeUtc() > recordedBoot.Value.AddMinutes(1);
    }

    private void MarkRestartRequired()
    {
        if (ReadTime(KeyRestartSince) is null)
            _state.Set(KeyRestartSince, _time.GetUtcNow().UtcDateTime.ToString("O"));
        _state.Set(KeyRestartBoot, _safety.GetBootTimeUtc().ToString("O"));
        Changed();
    }

    private void ClearRestartRequired(string reason)
    {
        _state.Set(KeyRestartSince, null);
        _state.Set(KeyRestartBoot, null);
        _state.Set(KeyRestartDeferrals, null);
        AddHistory(HistoryKind.Restart, null, reason, "Restarted", true);
        lock (_sync)
            foreach (var u in _updates.Where(u => u.State == UpdateState.InstalledRestartRequired))
            {
                u.State = UpdateState.Installed;
                u.StatusMessage = "";
            }
        Changed();
    }

    public void RecordRestartDeferral()
    {
        var count = int.TryParse(_state.Get(KeyRestartDeferrals), out var c) ? c + 1 : 1;
        _state.Set(KeyRestartDeferrals, count.ToString());
        AddHistory(HistoryKind.Restart, OperationTrigger.User, "Restart deferred", $"Deferred {count} time(s)", true);
    }

    /// <summary>Restarts the PC if (and only if) a restart is pending. Simulation mode only clears the flag.</summary>
    public bool RequestRestart(int delaySeconds)
    {
        var (pending, _) = GetRestartState();
        if (!pending) return false;
        if (_policies.Current.SimulationMode)
        {
            ClearRestartRequired("Simulated restart (no real restart in simulation mode)");
            return true;
        }
        AddHistory(HistoryKind.Restart, OperationTrigger.User, "Restart started", $"Restarting in {delaySeconds} seconds", true);
        _safety.RestartComputer(delaySeconds, "UpdateAll Win is restarting your PC to finish installing updates.");
        return true;
    }

    // ------------------------------------------------------------------ recovery / snapshot

    /// <summary>Called on service start: records interrupted operations and completed restarts.</summary>
    public bool RecoverAfterStartup()
    {
        var interrupted = _state.GetJson<PersistedOperation>(KeyOperation);
        if (interrupted is not null)
        {
            _state.Set(KeyOperation, null);
            AddHistory(HistoryKind.Service, OperationTrigger.Recovery, $"{interrupted.Kind} was interrupted",
                $"The service stopped while an operation started at {interrupted.StartedUtc.ToLocalTime():g} was running. A new scan will run shortly.", false);
            _log.LogWarning("Recovered from interrupted {Kind} operation", interrupted.Kind);
        }
        GetRestartState(); // clears the restart flag if the PC has restarted since
        return interrupted is not null;
    }

    private void SaveSnapshot()
    {
        List<UpdateDto> dtos;
        lock (_sync) dtos = _updates.Select(ToDto).ToList();
        _state.SetJson(KeySnapshot, dtos);
    }

    private void LoadSnapshot()
    {
        var dtos = _state.GetJson<List<UpdateDto>>(KeySnapshot);
        if (dtos is null) return;
        _updates = dtos.Select(d => new DetectedUpdate
        {
            AdapterId = d.AdapterId,
            VendorId = d.Key.Split(':').ElementAtOrDefault(1) ?? d.Key,
            Name = d.Name,
            Source = d.Source,
            Manufacturer = d.Manufacturer,
            Category = d.Category,
            CurrentVersion = d.CurrentVersion,
            AvailableVersion = d.AvailableVersion,
            Priority = d.Priority,
            SizeBytes = d.SizeBytes,
            Restart = d.Restart,
            ReleaseNotesUrl = d.ReleaseNotesUrl,
            State = d.State is UpdateState.Queued or UpdateState.Downloading or UpdateState.Installing ? UpdateState.Available : d.State,
            StatusMessage = d.StatusMessage,
        }).ToList();
        _updatesAreSnapshot = true;
    }

    // ------------------------------------------------------------------ helpers

    private IManufacturerAdapter? FindAdapter(string id) =>
        _activeAdapters.FirstOrDefault(a => a.Id == id) ?? _allAdapters.FirstOrDefault(a => a.Id == id);

    private List<DetectedUpdate> SnapshotUpdates()
    {
        lock (_sync) return _updates.ToList();
    }

    private AdapterContext CreateContext(IManufacturerAdapter adapter, MachinePolicy policy, DeviceInfo device) => new()
    {
        Policy = policy,
        Device = device,
        Logger = _loggers.CreateLogger($"Adapter.{adapter.Id}"),
        Processes = _processes,
        Downloader = _downloader,
        Paths = _paths.WithDownloadFolder(policy.DownloadFolder),
        Progress = new Progress<AdapterProgress>(p => SetStage(p.Stage, p.Message, p.Percent, p.CurrentItem)),
    };

    private void SetStage(EngineStage stage, string message, int? percent = null, string? item = null)
    {
        _stage = stage;
        _message = message;
        _percent = percent;
        _item = item;
        Changed();
    }

    private void Changed()
    {
        Interlocked.Increment(ref _revision);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Skip(DetectedUpdate u, string reason)
    {
        u.State = UpdateState.Skipped;
        u.StatusMessage = reason;
    }

    private static void Fail(DetectedUpdate u, string reason, int? exitCode = null)
    {
        u.State = UpdateState.Failed;
        u.StatusMessage = reason;
        u.ExitCode ??= exitCode;
    }

    private static void Succeed(DetectedUpdate u, bool restart)
    {
        u.State = restart ? UpdateState.InstalledRestartRequired : UpdateState.Installed;
        u.StatusMessage = restart ? "Restart required" : "";
    }

    private OperationOutcome Summarise(OperationKind kind, IReadOnlyList<DetectedUpdate> items, bool success, string message) => new(
        kind,
        success,
        items.Count,
        items.Count(u => u.State is UpdateState.Installed or UpdateState.InstalledRestartRequired),
        items.Count(u => u.State == UpdateState.Failed),
        items.Count(u => u.State == UpdateState.Skipped),
        items.Count(u => u.State == UpdateState.AwaitingApproval),
        GetRestartState().Pending,
        message);

    private void AddHistory(HistoryKind kind, OperationTrigger? trigger, string title, string result, bool success,
        string? adapterId = null, string? key = null, UpdateCategory? category = null,
        string? from = null, string? to = null, int? exitCode = null, string? details = null)
    {
        try
        {
            _history.Add(new HistoryEntry
            {
                TimeUtc = _time.GetUtcNow().UtcDateTime,
                Kind = kind,
                Trigger = trigger,
                Title = title,
                Result = result,
                Success = success,
                AdapterId = adapterId,
                UpdateKey = key,
                Category = category,
                FromVersion = from,
                ToVersion = to,
                ExitCode = exitCode,
                Details = details,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not write history");
        }
    }

    private DateTime? ReadTime(string key) =>
        DateTime.TryParse(_state.Get(key), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

    public void Dispose()
    {
        Cancel();
        _globalLock?.Dispose();
        _gate.Dispose();
    }

    private sealed record PersistedOperation(OperationKind Kind, OperationTrigger Trigger, DateTime StartedUtc);
}
