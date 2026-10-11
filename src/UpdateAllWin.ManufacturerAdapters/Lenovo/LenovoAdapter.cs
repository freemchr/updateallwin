using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.ManufacturerAdapters.Common;

namespace UpdateAllWin.ManufacturerAdapters.Lenovo;

/// <summary>
/// Lenovo, per Lenovo CDRT documentation:
/// <list type="bullet">
/// <item><b>Commercial Vantage + SU Helper</b> (preferred when installed): <c>suhelper.exe -autoupdate -scanonly</c>
/// writes applicable updates to WMI <c>root\Lenovo:Lenovo_Updates</c>; installs are triggered asynchronously and
/// results are read back from the same WMI class.</item>
/// <item><b>Lenovo System Update</b> (tvsu.exe /CM): has no silent list mode, so the scan reports one combined entry and
/// install applies everything matching the filters. Only reboot types 0 and 3 are included with -noreboot so
/// System Update never restarts or shuts down the PC on its own; BIOS updates (forced-reboot types) are not
/// installed through this path.</item>
/// </list>
/// </summary>
public sealed class LenovoAdapter : IManufacturerAdapter
{
    public const string AdapterId = "lenovo";
    private const string SummaryId = "system-update-all";
    private static readonly string[] TvsuProcesses = ["Tvsu", "TvsuCommandLauncher", "Tvsukernel"];

    private enum Mode { SystemUpdate, SuHelper }

    private Mode _mode = Mode.SystemUpdate;
    private string? _exe;

    public string Id => AdapterId;
    public string DisplayName => _mode == Mode.SuHelper ? "Lenovo Commercial Vantage" : "Lenovo System Update";
    public AdapterKind Kind => AdapterKind.Manufacturer;

    public AdapterCapabilities Capabilities => _mode == Mode.SuHelper
        ? AdapterCapabilities.Scan | AdapterCapabilities.InstallByCategory | AdapterCapabilities.Drivers |
          AdapterCapabilities.Bios | AdapterCapabilities.Firmware
        : AdapterCapabilities.Scan | AdapterCapabilities.InstallAllOnly | AdapterCapabilities.Drivers | AdapterCapabilities.Firmware;

    public Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct) =>
        Task.FromResult(OemDetector.Classify(device.Manufacturer) == Oem.Lenovo);

    public async Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext ctx, CancellationToken ct)
    {
        var suHelper = ToolResolver.FirstExisting(ToolResolver.ProgramFiles("Lenovo", "SUHelper", "suhelper.exe"));
        if (suHelper is not null)
        {
            _mode = Mode.SuHelper;
            _exe = suHelper;
            return PrerequisiteResult.Ok("Using Lenovo Commercial Vantage (SU Helper)");
        }

        _mode = Mode.SystemUpdate;
        _exe = FindTvsu();
        if (_exe is null)
        {
            ctx.Report(EngineStage.Scanning, "Installing Lenovo System Update...");
            await Winget.InstallAsync(ctx.Processes, ctx.Logger, "Lenovo.SystemUpdate", ct);
            _exe = FindTvsu();
        }
        return _exe is null
            ? PrerequisiteResult.NotReady("Lenovo System Update is not installed and could not be installed automatically")
            : PrerequisiteResult.Ok("Using Lenovo System Update");
    }

    public async Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext ctx, CancellationToken ct)
    {
        if (_mode == Mode.SystemUpdate)
        {
            return
            [
                new DetectedUpdate
                {
                    AdapterId = AdapterId,
                    VendorId = SummaryId,
                    Name = "Lenovo recommended driver and firmware updates",
                    Source = DisplayName,
                    Manufacturer = "Lenovo",
                    Category = UpdateCategory.Driver,
                    AvailableVersion = "Determined by Lenovo System Update",
                    Priority = UpdatePriority.Recommended,
                    Restart = RestartBehavior.MayBeRequired,
                    StatusMessage = "Lenovo System Update has no silent list mode; it decides what applies when installing",
                },
            ];
        }

        ctx.Report(EngineStage.Scanning, "Lenovo Commercial Vantage is checking for updates...");
        var before = ReadWmiUpdates(ctx.Logger);
        var result = await ctx.Processes.RunAsync(_exe!, $"-autoupdate -scanonly -packagetype {PackageTypes(ctx.Policy, includeBios: true)}", ct, timeout: TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0) throw new InvalidOperationException($"SU Helper could not start a scan ({SuHelperCode(result.ExitCode)})");

        var updates = await WaitForWmiToSettleAsync(ctx, before, TimeSpan.FromMinutes(20), ct);
        return updates
            .Where(u => u.Status.Equals("Applicable", StringComparison.OrdinalIgnoreCase))
            .Where(u => !ctx.Policy.CriticalOnly || u.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            .Select(u => new DetectedUpdate
            {
                AdapterId = AdapterId,
                VendorId = u.PackageId,
                Name = u.Title,
                Source = DisplayName,
                Manufacturer = "Lenovo",
                Category = CategoryFromTitle(u.Title),
                AvailableVersion = u.Version,
                Priority = u.Severity.ToLowerInvariant() switch
                {
                    "critical" => UpdatePriority.Critical,
                    "recommended" => UpdatePriority.Recommended,
                    "optional" => UpdatePriority.Optional,
                    _ => UpdatePriority.Unknown,
                },
                Restart = CategoryFromTitle(u.Title) == UpdateCategory.Bios ? RestartBehavior.Required : RestartBehavior.MayBeRequired,
            }).ToList();
    }

    public Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct) =>
        Task.FromResult(AdapterOperationResult.Unsupported("Lenovo tools download and install in one step"));

    public Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct) =>
        _mode == Mode.SuHelper ? InstallWithSuHelperAsync(updates, ctx, ct) : InstallWithSystemUpdateAsync(updates, ctx, ct);

    private async Task<AdapterOperationResult> InstallWithSystemUpdateAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct)
    {
        var search = ctx.Policy.CriticalOnly ? "C" : "R";
        ctx.Report(EngineStage.Installing, "Lenovo System Update is installing updates...");
        var result = await ctx.Processes.RunAsync(_exe!,
            $"/CM -search {search} -action INSTALL -packagetypes {PackageTypes(ctx.Policy, includeBios: false)} " +
            "-includerebootpackages 3 -noreboot -noicon -nolicense -exporttowmi",
            ct, timeout: TimeSpan.FromHours(3));
        await WaitForProcessesAsync(TvsuProcesses, TimeSpan.FromHours(3), ct);

        var installed = ReadWmiUpdates(ctx.Logger).Where(u => u.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var u in installed) ctx.Logger.LogInformation("Lenovo System Update installed {Title} {Version}", u.Title, u.Version);

        // tvsu.exe has no documented exit codes; report what WMI shows.
        foreach (var u in updates)
        {
            u.State = UpdateState.InstalledRestartRequired;
            u.StatusMessage = installed.Count > 0 ? $"{installed.Count} Lenovo package(s) recorded as installed" : "Completed - see Lenovo System Update history";
        }
        return new AdapterOperationResult(true, true, $"Lenovo System Update finished (exit code {result.ExitCode})", result.ExitCode);
    }

    private async Task<AdapterOperationResult> InstallWithSuHelperAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct)
    {
        var bios = updates.Where(u => u.Category == UpdateCategory.Bios).ToList();
        var others = updates.Except(bios).ToList();

        if (others.Count > 0)
        {
            // Filter-based install restricted to reboot types 0 and 3 (no update-forced restarts or shutdowns).
            ctx.Report(EngineStage.Installing, "Lenovo Commercial Vantage is installing driver and firmware updates...");
            var r = await ctx.Processes.RunAsync(_exe!,
                $"-autoupdate -packagetype {PackageTypes(ctx.Policy, includeBios: false)} -reboottype 0,3", ct, timeout: TimeSpan.FromMinutes(5));
            if (r.ExitCode != 0)
                foreach (var u in others) { u.State = UpdateState.Failed; u.StatusMessage = SuHelperCode(r.ExitCode); }
        }

        if (bios.Count > 0)
        {
            // BIOS reaches this point only after explicit approval and the engine's safety checks.
            ctx.Report(EngineStage.Installing, "Lenovo Commercial Vantage is installing the BIOS update...");
            var r = await ctx.Processes.RunAsync(_exe!, $"-autoupdate -include {string.Join(",", bios.Select(b => b.VendorId))}", ct, timeout: TimeSpan.FromMinutes(5));
            if (r.ExitCode != 0)
                foreach (var u in bios) { u.State = UpdateState.Failed; u.StatusMessage = SuHelperCode(r.ExitCode); }
        }

        // SU Helper only triggers the session; poll WMI for the outcome.
        var pending = updates.Where(u => u.State != UpdateState.Failed).ToList();
        var deadline = DateTime.UtcNow.AddHours(2);
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            var wmi = ReadWmiUpdates(ctx.Logger).ToDictionary(u => u.PackageId, StringComparer.OrdinalIgnoreCase);
            foreach (var u in pending.ToList())
            {
                if (!wmi.TryGetValue(u.VendorId, out var w)) continue;
                if (w.Status.Equals("Installed", StringComparison.OrdinalIgnoreCase))
                {
                    u.State = UpdateState.InstalledRestartRequired;
                    u.StatusMessage = "Restart required";
                    pending.Remove(u);
                }
                else if (w.Status.Contains("fail", StringComparison.OrdinalIgnoreCase))
                {
                    u.State = UpdateState.Failed;
                    u.StatusMessage = $"Lenovo reported: {w.Status} {w.AdditionalInfo}".Trim();
                    pending.Remove(u);
                }
            }
            ctx.Report(EngineStage.Installing, $"Waiting for Lenovo Commercial Vantage ({updates.Count - pending.Count}/{updates.Count} done)...");
        }
        foreach (var u in pending)
        {
            u.State = UpdateState.Failed;
            u.StatusMessage = "Lenovo did not report a result within 2 hours";
        }

        var ok = updates.All(u => u.State != UpdateState.Failed);
        return new AdapterOperationResult(ok, updates.Any(u => u.State == UpdateState.InstalledRestartRequired),
            ok ? "Lenovo updates installed" : "Some Lenovo updates did not install");
    }

    public Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct)
    {
        var running = TvsuProcesses.SelectMany(Process.GetProcessesByName).ToList();
        var busy = running.Count > 0;
        running.ForEach(p => p.Dispose());
        return Task.FromResult(new InstallationStatus(busy, busy ? "Lenovo System Update is running" : "Idle"));
    }

    public Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct) => Task.FromResult(RestartBehavior.Unknown);

    // ---------------------------------------------------------------- helpers

    /// <summary>Lenovo package types: 2 = driver, 3 = BIOS, 4 = firmware.</summary>
    private static string PackageTypes(MachinePolicy policy, bool includeBios)
    {
        var types = new List<string>();
        if (policy.IncludeDrivers) types.Add("2");
        if (includeBios && policy.IncludeBios) types.Add("3");
        if (policy.IncludeFirmware) types.Add("4");
        return types.Count == 0 ? "2" : string.Join(",", types);
    }

    private static UpdateCategory CategoryFromTitle(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("bios")) return UpdateCategory.Bios;
        if (t.Contains("firmware")) return UpdateCategory.Firmware;
        if (t.Contains("driver")) return UpdateCategory.Driver;
        return UpdateCategory.Other;
    }

    private static string SuHelperCode(int code) => code switch
    {
        1 => "SU Helper parameter error (1)",
        2 => "Lenovo System Update add-in is busy (2)",
        3 => "SU Helper unexpected error (3)",
        _ => $"SU Helper exit code {code}",
    };

    internal sealed record LenovoWmiUpdate(string PackageId, string Title, string Version, string Severity, string Status, string AdditionalInfo);

    private static List<LenovoWmiUpdate> ReadWmiUpdates(ILogger logger)
    {
        var list = new List<LenovoWmiUpdate>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Lenovo", "SELECT * FROM Lenovo_Updates");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    string S(string p) { try { return obj[p]?.ToString()?.Trim() ?? ""; } catch { return ""; } }
                    list.Add(new LenovoWmiUpdate(S("PackageID"), S("Title"), S("Version"), S("Severity"), S("Status"), S("AdditionalInfo")));
                }
            }
        }
        catch (ManagementException ex)
        {
            logger.LogDebug("Lenovo_Updates WMI class not available yet: {Message}", ex.Message);
        }
        return list;
    }

    /// <summary>SU Helper is asynchronous: wait until the WMI results change and then stay stable.</summary>
    private static async Task<List<LenovoWmiUpdate>> WaitForWmiToSettleAsync(AdapterContext ctx, List<LenovoWmiUpdate> before, TimeSpan max, CancellationToken ct)
    {
        string Fingerprint(IEnumerable<LenovoWmiUpdate> l) => string.Join("|", l.Select(u => $"{u.PackageId}:{u.Status}").Order());
        var original = Fingerprint(before);
        var last = original;
        var stableRounds = 0;
        var changed = false;
        var deadline = DateTime.UtcNow + max;
        var current = before;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            current = ReadWmiUpdates(ctx.Logger);
            var fp = Fingerprint(current);
            changed |= fp != original;
            stableRounds = fp == last ? stableRounds + 1 : 0;
            last = fp;
            // Settled: results changed and then held for a minute, or never changed for 5 minutes (nothing new).
            if ((changed && stableRounds >= 4) || (!changed && DateTime.UtcNow > deadline - max + TimeSpan.FromMinutes(5))) break;
        }
        return current;
    }

    private static async Task WaitForProcessesAsync(string[] names, TimeSpan max, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + max;
        while (DateTime.UtcNow < deadline)
        {
            var running = names.SelectMany(Process.GetProcessesByName).ToList();
            var any = running.Count > 0;
            running.ForEach(p => p.Dispose());
            if (!any) return;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private static string? FindTvsu() => ToolResolver.FirstExisting(
        ToolResolver.ProgramFilesX86("Lenovo", "System Update", "tvsu.exe"),
        ToolResolver.ProgramFiles("Lenovo", "System Update", "tvsu.exe"));
}
