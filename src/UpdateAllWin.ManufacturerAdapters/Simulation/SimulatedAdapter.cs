using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;

namespace UpdateAllWin.ManufacturerAdapters.Simulation;

/// <summary>
/// Development/test adapter: produces realistic updates and timings without touching the PC.
/// Scenario comes from <see cref="MachinePolicy.SimulationScenario"/>:
/// UpToDate, UpdatesAvailable, DownloadFailure, InstallFailure, NetworkDown.
/// </summary>
public sealed class SimulatedAdapter : IManufacturerAdapter
{
    public const string AdapterId = "simulated";

    private readonly HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _step;
    private string _lastScenario = "";

    public SimulatedAdapter() : this(TimeSpan.FromMilliseconds(700)) { }

    /// <param name="step">Base delay; tests pass TimeSpan.Zero.</param>
    public SimulatedAdapter(TimeSpan step) => _step = step;

    public string Id => AdapterId;
    public string DisplayName => "Simulated updates";
    public AdapterKind Kind => AdapterKind.Simulation;

    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.Scan | AdapterCapabilities.Download | AdapterCapabilities.InstallIndividual |
        AdapterCapabilities.Drivers | AdapterCapabilities.Bios | AdapterCapabilities.Firmware |
        AdapterCapabilities.ReportsDownloadSize | AdapterCapabilities.ReportsCurrentVersion | AdapterCapabilities.ReportsRestartPerUpdate;

    public Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct) => Task.FromResult(true);

    public Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext context, CancellationToken ct)
    {
        var scenario = context.Policy.SimulationScenario;
        if (!scenario.Equals(_lastScenario, StringComparison.OrdinalIgnoreCase))
        {
            _installed.Clear(); // switching scenario starts fresh
            _lastScenario = scenario;
        }
        return Task.FromResult(Is(context, "NetworkDown")
            ? PrerequisiteResult.NotReady("No internet connection (simulated)")
            : PrerequisiteResult.Ok());
    }

    public async Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext context, CancellationToken ct)
    {
        for (var p = 0; p <= 100; p += 20)
        {
            context.Report(EngineStage.Scanning, "Analysing drivers, BIOS and firmware...", p);
            await Task.Delay(_step, ct);
        }
        if (Is(context, "UpToDate")) return [];

        var vendor = string.IsNullOrWhiteSpace(context.Device.Manufacturer) ? "OEM" : context.Device.Manufacturer;
        DetectedUpdate[] catalog =
        [
            Make("intel-gfx", "Intel Graphics Driver", UpdateCategory.Driver, "31.0.101.4953", "31.0.101.5534", 857_000_000, UpdatePriority.Recommended, RestartBehavior.Required, "Intel"),
            Make("realtek-audio", "Realtek Audio Driver", UpdateCategory.Driver, "6.0.9555.1", "6.0.9724.1", 290_000_000, UpdatePriority.Recommended, RestartBehavior.NotRequired, "Realtek"),
            Make("system-bios", $"{vendor} System BIOS Update", UpdateCategory.Bios, "F.18 Rev.A", "F.20 Rev.A", 18_400_000, UpdatePriority.Critical, RestartBehavior.Required, vendor),
        ];
        return catalog.Where(u => !_installed.Contains(u.Key)).ToList();
    }

    public async Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext context, CancellationToken ct)
    {
        foreach (var u in updates)
        {
            for (var p = 0; p <= 100; p += 25)
            {
                u.Percent = p;
                context.Report(EngineStage.Downloading, $"Downloading {u.Name}...", p, u.Name);
                await Task.Delay(_step / 2, ct);
            }
            u.State = UpdateState.Downloaded;
        }
        return Is(context, "DownloadFailure")
            ? new AdapterOperationResult(false, false, "Download failed: connection reset (simulated)", 4098)
            : new AdapterOperationResult(true, false, "Downloaded");
    }

    public async Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext context, CancellationToken ct)
    {
        var restart = false;
        var allOk = true;
        for (var i = 0; i < updates.Count; i++)
        {
            var u = updates[i];
            context.Report(EngineStage.Installing, $"Installing {u.Name}...", i * 100 / updates.Count, u.Name);
            await Task.Delay(_step * 2, ct);

            if (Is(context, "InstallFailure") && i == 0)
            {
                u.State = UpdateState.Failed;
                u.StatusMessage = "Installer returned 1603 (simulated)";
                u.ExitCode = 1603;
                allOk = false;
                continue;
            }

            var needsRestart = u.Restart == RestartBehavior.Required;
            restart |= needsRestart;
            u.State = needsRestart ? UpdateState.InstalledRestartRequired : UpdateState.Installed;
            u.StatusMessage = needsRestart ? "Restart required" : "";
            _installed.Add(u.Key);
        }
        context.Report(EngineStage.Installing, "Finishing...", 100);
        return new AdapterOperationResult(allOk, restart, allOk ? "Installed (simulated)" : "One or more installs failed (simulated)", allOk ? 0 : 1603);
    }

    public Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct) => Task.FromResult(new InstallationStatus(false, "Idle"));

    public Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct) => Task.FromResult(RestartBehavior.Unknown);

    private static bool Is(AdapterContext context, string scenario) =>
        context.Policy.SimulationScenario.Equals(scenario, StringComparison.OrdinalIgnoreCase);

    private static DetectedUpdate Make(string id, string name, UpdateCategory category, string current, string available,
        long size, UpdatePriority priority, RestartBehavior restart, string manufacturer) => new()
    {
        AdapterId = AdapterId,
        VendorId = id,
        Name = name,
        Source = "Simulated",
        Manufacturer = manufacturer,
        Category = category,
        CurrentVersion = current,
        AvailableVersion = available,
        SizeBytes = size,
        Priority = priority,
        Restart = restart,
    };
}
