using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.ManufacturerAdapters.Common;

namespace UpdateAllWin.ManufacturerAdapters.Dell;

/// <summary>
/// Dell: Dell Command | Update 5.x CLI (dcu-cli.exe), per Dell's DCU 5.x reference guide.
/// DCU filters installs by type/severity/device category, not by individual update, so
/// the adapter declares <see cref="AdapterCapabilities.InstallByCategory"/>.
/// </summary>
public sealed class DellCommandUpdateAdapter : IManufacturerAdapter
{
    public const string AdapterId = "dell-dcu";

    internal static readonly IReadOnlyDictionary<int, string> ExitCodes = new Dictionary<int, string>
    {
        [0] = "Success",
        [1] = "Success - a restart is required to finish",
        [2] = "Unknown application error",
        [3] = "Not a Dell system",
        [4] = "Not running with administrator rights",
        [5] = "A restart is pending from a previous operation - restart first",
        [6] = "Another instance of Dell Command | Update is running",
        [7] = "This model is not supported by Dell's catalog",
        [8] = "No update filters applied",
        [500] = "No updates found",
        [501] = "Error determining available updates",
        [502] = "Scan cancelled",
        [503] = "Download error",
        [1000] = "Error retrieving the result of the update",
        [1001] = "Update cancelled",
        [1002] = "Download error",
        [3000] = "Dell Client Management Service is not running",
        [3001] = "Dell Client Management Service is not installed",
        [3002] = "Dell Client Management Service is disabled",
        [3003] = "Dell Client Management Service is busy",
        [3004] = "Dell Client Management Service is updating itself",
        [3005] = "Dell Client Management Service is installing pending updates",
    };

    private string? _exe;

    public string Id => AdapterId;
    public string DisplayName => "Dell Command | Update";
    public AdapterKind Kind => AdapterKind.Manufacturer;

    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.Scan | AdapterCapabilities.InstallByCategory |
        AdapterCapabilities.Drivers | AdapterCapabilities.Bios | AdapterCapabilities.Firmware |
        AdapterCapabilities.HandlesBitLockerForBios; // -autoSuspendBitLocker=enable (when policy allows)

    public Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct) =>
        Task.FromResult(OemDetector.Classify(device.Manufacturer) == Oem.Dell);

    public async Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext ctx, CancellationToken ct)
    {
        _exe = Find();
        if (_exe is null)
        {
            ctx.Report(EngineStage.Scanning, "Installing Dell Command | Update...");
            await Winget.InstallAsync(ctx.Processes, ctx.Logger, "Dell.CommandUpdate.Universal", ct);
            _exe = Find();
        }
        return _exe is null
            ? PrerequisiteResult.NotReady("Dell Command | Update is not installed and could not be installed automatically")
            : PrerequisiteResult.Ok($"Dell Command | Update {FileVersionInfo.GetVersionInfo(_exe).ProductVersion}");
    }

    public async Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext ctx, CancellationToken ct)
    {
        var report = ctx.Paths.NewWorkFolder("dcu-scan");
        ctx.Report(EngineStage.Scanning, "Dell Command | Update is checking for updates...");
        var result = await ctx.Processes.RunAsync(_exe!,
            $"/scan -updateType={UpdateTypes(ctx.Policy, null)}{Severity(ctx.Policy)} -report={ToolResolver.Quote(report)} " +
            $"-outputLog={ToolResolver.Quote(Path.Combine(report, "scan.log"))} -silent",
            ct, timeout: TimeSpan.FromMinutes(45));
        ctx.Logger.LogInformation("DCU scan: {Result}", Describe(result.ExitCode));

        if (result.ExitCode == 500) return [];
        if (result.ExitCode != 0) throw new InvalidOperationException($"Dell Command | Update: {Describe(result.ExitCode)}");

        var items = new List<DetectedUpdate>();
        foreach (var xml in Directory.EnumerateFiles(report, "*.xml", SearchOption.AllDirectories))
            items.AddRange(ParseReport(XDocument.Load(xml)));
        return items;
    }

    internal IEnumerable<DetectedUpdate> ParseReport(XDocument doc)
    {
        foreach (var u in doc.Descendants().Where(e => e.Name.LocalName.Equals("update", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Child(u, "name");
            if (name.Length == 0) continue;
            var type = Child(u, "type").ToLowerInvariant();
            var urgency = Child(u, "urgency").ToLowerInvariant();
            yield return new DetectedUpdate
            {
                AdapterId = AdapterId,
                VendorId = Child(u, "release") is { Length: > 0 } rel ? rel : Path.GetFileNameWithoutExtension(Child(u, "file")) is { Length: > 0 } f ? f : name,
                Name = name,
                Source = DisplayName,
                Manufacturer = "Dell",
                Category = type.Contains("bios") ? UpdateCategory.Bios
                    : type.Contains("firmware") ? UpdateCategory.Firmware
                    : type.Contains("application") || type.Contains("utility") ? UpdateCategory.Software
                    : UpdateCategory.Driver,
                AvailableVersion = Child(u, "version"),
                Priority = urgency.Contains("urgent") || urgency.Contains("critical") || urgency.Contains("security") ? UpdatePriority.Critical
                    : urgency.Contains("optional") ? UpdatePriority.Optional
                    : UpdatePriority.Recommended,
                Restart = type.Contains("bios") || type.Contains("firmware") ? RestartBehavior.Required : RestartBehavior.MayBeRequired,
            };
        }
    }

    public Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct) =>
        Task.FromResult(AdapterOperationResult.Unsupported("Dell Command | Update downloads and installs in one step"));

    public async Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct)
    {
        var log = Path.Combine(ctx.Paths.NewWorkFolder("dcu-install"), "apply.log");
        var bitlocker = ctx.Policy.AllowBitLockerSuspension ? "enable" : "disable";
        var result = await ctx.Processes.RunAsync(_exe!,
            $"/applyUpdates -updateType={UpdateTypes(ctx.Policy, updates)}{Severity(ctx.Policy)} -reboot=disable " +
            $"-autoSuspendBitLocker={bitlocker} -outputLog={ToolResolver.Quote(log)} -silent",
            ct, timeout: TimeSpan.FromHours(3));
        ctx.Logger.LogInformation("DCU apply: {Result}", Describe(result.ExitCode));

        // 0 = done, 1 = done but restart needed. 500 on apply is undocumented; treat as "nothing applied".
        var success = result.ExitCode is 0 or 1 or 500;
        var restart = result.ExitCode == 1;
        foreach (var u in updates)
        {
            u.State = !success ? UpdateState.Failed : restart ? UpdateState.InstalledRestartRequired : UpdateState.Installed;
            u.StatusMessage = success ? (restart ? "Restart required" : "") : Describe(result.ExitCode);
            if (!success) u.ExitCode = result.ExitCode;
        }
        return new AdapterOperationResult(success, restart, Describe(result.ExitCode), result.ExitCode);
    }

    public Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct)
    {
        var running = Process.GetProcessesByName("dcu-cli").Concat(Process.GetProcessesByName("DellCommandUpdate")).ToList();
        var busy = running.Count > 0;
        running.ForEach(p => p.Dispose());
        return Task.FromResult(new InstallationStatus(busy, busy ? "Dell Command | Update is running" : "Idle"));
    }

    public Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct) => Task.FromResult(RestartBehavior.Unknown);

    internal static string Describe(int code) =>
        ExitCodes.TryGetValue(code, out var text) ? $"{text} ({code})"
        : code is >= 100 and <= 113 ? $"Invalid command line ({code})"
        : $"Dell Command | Update exit code {code}";

    private static string UpdateTypes(MachinePolicy policy, IReadOnlyList<DetectedUpdate>? items)
    {
        var types = new List<string>();
        bool Want(UpdateCategory c, bool enabled) => enabled && (items is null || items.Any(i => i.Category == c));
        if (Want(UpdateCategory.Bios, policy.IncludeBios)) types.Add("bios");
        if (Want(UpdateCategory.Firmware, policy.IncludeFirmware)) types.Add("firmware");
        if (Want(UpdateCategory.Driver, policy.IncludeDrivers)) types.Add("driver");
        return types.Count == 0 ? "driver" : string.Join(",", types);
    }

    private static string Severity(MachinePolicy policy) => policy.CriticalOnly ? " -updateSeverity=security,critical" : "";

    private static string Child(XElement e, string name) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

    private static string? Find() => ToolResolver.FirstExisting(
        ToolResolver.ProgramFiles("Dell", "CommandUpdate", "dcu-cli.exe"),
        ToolResolver.ProgramFilesX86("Dell", "CommandUpdate", "dcu-cli.exe"));
}
