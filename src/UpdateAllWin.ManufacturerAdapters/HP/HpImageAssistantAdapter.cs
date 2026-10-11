using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.ManufacturerAdapters.Common;

namespace UpdateAllWin.ManufacturerAdapters.HP;

/// <summary>
/// HP: HP Image Assistant (HPIA) command line, per the HPIA User Guide (10th ed., April 2026).
/// Scan: /Operation:Analyze /Action:List. Install specific SoftPaqs: /SPList with /Action:Download|Install.
/// HPIA is fetched from HP's official page when not present and must be signed by HP Inc.
/// </summary>
public sealed partial class HpImageAssistantAdapter : IManufacturerAdapter
{
    public const string AdapterId = "hp-hpia";
    private const string HpiaPage = "https://ftp.ext.hp.com/pub/caps-softpaq/cmit/HPIA.html";
    private const string FallbackUrl = "https://hpia.hpcloud.hp.com/downloads/hpia/hp-hpia-5.3.7.exe";

    internal static readonly IReadOnlyDictionary<int, string> ExitCodes = new Dictionary<int, string>
    {
        [0] = "Success",
        [256] = "No recommendations",
        [257] = "No recommendations were selected",
        [3010] = "Installed - restart required",
        [3011] = "A SoftPaq needs manual installation and was skipped",
        [3020] = "One or more SoftPaqs failed to install",
        [4096] = "This platform is not supported by HP Image Assistant",
        [4097] = "Invalid parameters",
        [4098] = "No internet connection",
        [4099] = "Invalid SoftPaq number in list",
        [4102] = "TLS 1.2 or higher is required",
        [4103] = "Could not reach HP's servers",
        [4104] = "No supported OS for this platform (generic reference used)",
        [8192] = "Operation failed",
        [8194] = "Output folder could not be created",
        [8195] = "Download folder could not be created",
        [8196] = "Supported platform list download failed",
        [8199] = "SoftPaq download failed",
        [8200] = "SoftPaq extraction failed",
        [16384] = "Reference file could not be opened",
        [16385] = "Reference file is invalid",
        [16387] = "Reference file does not match this system/OS",
        [16388] = "Error processing the reference file",
        [16389] = "Reference file not found",
    };

    private string? _exe;

    public string Id => AdapterId;
    public string DisplayName => "HP Image Assistant";
    public AdapterKind Kind => AdapterKind.Manufacturer;

    // HPIA's guide doesn't document BitLocker handling, so the engine suspends it (HandlesBitLockerForBios not claimed).
    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.Scan | AdapterCapabilities.Download | AdapterCapabilities.InstallIndividual |
        AdapterCapabilities.Drivers | AdapterCapabilities.Bios | AdapterCapabilities.Firmware |
        AdapterCapabilities.ReportsCurrentVersion;

    public Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct) =>
        Task.FromResult(OemDetector.Classify(device.Manufacturer) == Oem.HP);

    public async Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext ctx, CancellationToken ct)
    {
        if (ctx.Policy.HpiaPath is { } configured)
        {
            if (!File.Exists(configured)) return PrerequisiteResult.NotReady($"HPIA was not found at {configured}");
            if (!ToolResolver.IsTrustedLocation(configured, ctx.Paths.Root))
                return PrerequisiteResult.NotReady("The configured HPIA path is in a user-writable location; move it under Program Files");
            _exe = configured;
            return PrerequisiteResult.Ok($"Using {configured}");
        }

        var managedDir = Path.Combine(ctx.Paths.Tools, "HPIA");
        var managed = Find(managedDir);
        var installed = ToolResolver.FirstExisting(ToolResolver.ProgramFiles("HP", "HPIA", "HPImageAssistant.exe"),
                                                   ToolResolver.ProgramFilesX86("HP", "HPIA", "HPImageAssistant.exe"));

        ctx.Report(EngineStage.Scanning, "Checking for the latest HP Image Assistant...");
        var latest = await GetLatestAsync(ctx, ct);

        if (managed is not null && (latest is null || GetVersion(managed) is not { } have || have >= latest.Value.Version))
        {
            _exe = managed;
            return PrerequisiteResult.Ok($"HP Image Assistant {GetVersion(managed)}");
        }
        if (managed is null && installed is not null)
        {
            _exe = installed;
            return PrerequisiteResult.Ok($"Using installed HP Image Assistant {GetVersion(installed)}");
        }

        try
        {
            ctx.Report(EngineStage.Scanning, "Downloading HP Image Assistant...");
            _exe = await DownloadAsync(latest?.Url ?? FallbackUrl, managedDir, ctx, ct);
            return PrerequisiteResult.Ok($"HP Image Assistant {GetVersion(_exe)} downloaded");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogError(ex, "HPIA download failed");
            _exe = managed ?? installed;
            return _exe is not null
                ? PrerequisiteResult.Ok($"Using existing HP Image Assistant (update failed: {ex.Message})")
                : PrerequisiteResult.NotReady($"HP Image Assistant could not be downloaded: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext ctx, CancellationToken ct)
    {
        var report = ctx.Paths.NewWorkFolder("hpia-scan");
        ctx.Report(EngineStage.Scanning, "HP Image Assistant is analysing this PC...");
        var result = await ctx.Processes.RunAsync(_exe!,
            $"/Operation:Analyze /Action:List /Category:{Categories(ctx.Policy)} /Selection:{(ctx.Policy.CriticalOnly ? "Critical" : "All")} /Silent " +
            $"/ReportFolder:{ToolResolver.Quote(report)} /SoftPaqDownloadFolder:{ToolResolver.Quote(DownloadFolder(ctx))} /LogFolder:{ToolResolver.Quote(report)}",
            ct, timeout: TimeSpan.FromMinutes(45));

        LogExit(ctx, result.ExitCode);
        if (result.ExitCode is 256 or 257) return [];
        if (result.ExitCode is not (0 or 4104))
            throw new InvalidOperationException($"HP Image Assistant: {Describe(result.ExitCode)}");

        var recommendations = HpiaReportParser.ParseFolder(report);
        ctx.Logger.LogInformation("HPIA found {Count} recommendation(s)", recommendations.Count);
        return recommendations.Select(r => new DetectedUpdate
        {
            AdapterId = AdapterId,
            VendorId = r.SoftPaqId,
            Name = r.Name,
            Source = DisplayName,
            Manufacturer = "HP",
            Category = r.Category,
            CurrentVersion = r.CurrentVersion,
            AvailableVersion = r.AvailableVersion,
            Priority = ctx.Policy.CriticalOnly || (r.Comments?.Contains("critical", StringComparison.OrdinalIgnoreCase) ?? false)
                ? UpdatePriority.Critical : UpdatePriority.Recommended,
            Restart = r.Category is UpdateCategory.Bios or UpdateCategory.Firmware ? RestartBehavior.Required : RestartBehavior.MayBeRequired,
            ReleaseNotesUrl = r.ReleaseNotesUrl,
        }).ToList();
    }

    public async Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct)
    {
        var work = ctx.Paths.NewWorkFolder("hpia-download");
        var list = WriteSpList(work, updates);
        var folder = DownloadFolder(ctx);
        var sizes = await GetSoftPaqSizesAsync(updates, ctx, ct);

        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchDownloadsAsync(updates, sizes, folder, ctx, watching.Token);
        ProcessResult result;
        try
        {
            result = await ctx.Processes.RunAsync(_exe!,
                $"/Operation:Analyze /Action:Download /SPList:{ToolResolver.Quote(list)} /Silent " +
                $"/ReportFolder:{ToolResolver.Quote(work)} /SoftPaqDownloadFolder:{ToolResolver.Quote(folder)}",
                ct, timeout: TimeSpan.FromHours(2));
        }
        finally
        {
            watching.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
        }
        LogExit(ctx, result.ExitCode);
        return new AdapterOperationResult(result.ExitCode is 0 or 256 or 257, false, Describe(result.ExitCode), result.ExitCode);
    }

    /// <summary>
    /// SoftPaq sizes from HP's server (HEAD request). HPIA's report has no sizes, and the .exe sits
    /// next to the release notes, so the URL comes from those. Null where unknown.
    /// </summary>
    private static async Task<Dictionary<DetectedUpdate, long?>> GetSoftPaqSizesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct)
    {
        var sizes = new Dictionary<DetectedUpdate, long?>();
        foreach (var u in updates)
        {
            long? size = null;
            if (u.ReleaseNotesUrl is { } notes && notes.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    size = await ctx.Downloader.GetContentLengthAsync(notes[..^5] + ".exe", timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    ctx.Logger.LogInformation("Couldn't get the size of {SoftPaq}: {Error}", u.VendorId, ex.Message);
                }
            }
            sizes[u] = size > 0 ? size : null;
        }
        return sizes;
    }

    /// <summary>
    /// Shows per-SoftPaq progress while HPIA downloads, by comparing each spNNNNNN.exe in the download
    /// folder with its size on HP's server. A SoftPaq counts as downloaded once its file is full size.
    /// </summary>
    private static async Task WatchDownloadsAsync(IReadOnlyList<DetectedUpdate> updates, Dictionary<DetectedUpdate, long?> sizes,
        string folder, AdapterContext ctx, CancellationToken ct)
    {
        var total = sizes.Values.Sum(v => v ?? 0);
        var last = "";
        while (!ct.IsCancellationRequested)
        {
            long done = 0;
            DetectedUpdate? current = null;
            foreach (var u in updates)
            {
                var file = new FileInfo(Path.Combine(folder, HpiaReportParser.NormalizeId(u.VendorId) + ".exe"));
                var length = file.Exists ? file.Length : 0;
                if (sizes[u] is not { } size) { current ??= file.Exists ? u : null; continue; }

                done += Math.Min(length, size);
                if (length >= size)
                {
                    u.State = UpdateState.Downloaded;
                    u.Percent = null;
                }
                else
                {
                    u.State = UpdateState.Downloading;
                    u.Percent = (int)(length * 100 / size);
                    if (length > 0) current ??= u;
                }
            }

            int? overall = total > 0 ? (int)(done * 100 / total) : null;
            var state = string.Join(",", updates.Select(u => $"{u.State}{u.Percent}")) + overall;
            if (state != last)
            {
                last = state;
                ctx.Report(EngineStage.Downloading,
                    current is null ? $"Downloading {updates.Count} update(s) from HP..." : $"Downloading {current.Name}...",
                    overall, current?.Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public async Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext ctx, CancellationToken ct)
    {
        var work = ctx.Paths.NewWorkFolder("hpia-install");
        var list = WriteSpList(work, updates);
        var password = ctx.Policy.HpBiosPasswordFile is { } pwd && File.Exists(pwd) && updates.Any(u => u.Category == UpdateCategory.Bios)
            ? $" /BIOSPwdFile:{ToolResolver.Quote(pwd)}" : "";

        var result = await ctx.Processes.RunAsync(_exe!,
            $"/Operation:Analyze /Action:Install /SPList:{ToolResolver.Quote(list)} /Silent /AutoCleanup " +
            $"/ReportFolder:{ToolResolver.Quote(work)} /SoftPaqDownloadFolder:{ToolResolver.Quote(DownloadFolder(ctx))} /LogFolder:{ToolResolver.Quote(work)}{password}",
            ct, timeout: TimeSpan.FromHours(3));
        LogExit(ctx, result.ExitCode);

        var restart = result.ExitCode == 3010;
        var overallOk = result.ExitCode is 0 or 3010;
        var perItem = HpiaReportParser.ParseRemediation(work);

        foreach (var u in updates)
        {
            if (perItem.TryGetValue(HpiaReportParser.NormalizeId(u.VendorId), out var rem))
            {
                // Statuses seen from HPIA 5.3.7: INSTALL_COMPLETED, and INSTALL_UNKNOWN with ReturnCode 0
                // (the SoftPaq returned success but HPIA couldn't confirm it; the next scan rechecks).
                var failed = rem.Status.Contains("fail", StringComparison.OrdinalIgnoreCase);
                var ok = !failed && (rem.ReturnCode is 0 or 3010
                    || rem.Status.Contains("success", StringComparison.OrdinalIgnoreCase)
                    || rem.Status.Contains("completed", StringComparison.OrdinalIgnoreCase));
                var unconfirmed = ok && rem.Status.Contains("unknown", StringComparison.OrdinalIgnoreCase);
                var itemRestart = rem.ReturnCode == 3010 || (ok && restart && u.Restart != RestartBehavior.NotRequired);
                u.State = ok ? (itemRestart ? UpdateState.InstalledRestartRequired : UpdateState.Installed) : UpdateState.Failed;
                u.StatusMessage = !ok ? $"{rem.Status} {rem.Description}".Trim()
                    : unconfirmed ? (itemRestart ? "Restart required; HP will confirm on the next check" : "HP will confirm on the next check")
                    : itemRestart ? "Restart required" : "";
                u.ExitCode = rem.ReturnCode;
            }
            else if (overallOk)
            {
                var itemRestart = restart && u.Restart != RestartBehavior.NotRequired;
                u.State = itemRestart ? UpdateState.InstalledRestartRequired : UpdateState.Installed;
                u.StatusMessage = itemRestart ? "Restart required" : "";
            }
            else if (result.ExitCode == 3011)
            {
                u.State = UpdateState.Skipped;
                u.StatusMessage = "HP marks this SoftPaq as needing manual installation";
            }
            else
            {
                u.State = UpdateState.Failed;
                u.StatusMessage = Describe(result.ExitCode);
                u.ExitCode = result.ExitCode;
            }
        }
        return new AdapterOperationResult(overallOk, restart, Describe(result.ExitCode), result.ExitCode);
    }

    public Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct)
    {
        var running = Process.GetProcessesByName("HPImageAssistant");
        var busy = running.Length > 0;
        foreach (var p in running) p.Dispose();
        return Task.FromResult(new InstallationStatus(busy, busy ? "HP Image Assistant is running" : "Idle"));
    }

    public Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct) => Task.FromResult(RestartBehavior.Unknown);

    // ---------------------------------------------------------------- helpers

    internal static string Describe(int code) =>
        ExitCodes.TryGetValue(code, out var text) ? $"{text} ({code})" : $"HP Image Assistant exit code {code}";

    private static void LogExit(AdapterContext ctx, int code)
    {
        if (code is 0 or 256 or 257 or 3010) ctx.Logger.LogInformation("HPIA: {Result}", Describe(code));
        else ctx.Logger.LogWarning("HPIA: {Result}", Describe(code));
    }

    private static string Categories(MachinePolicy policy)
    {
        var cats = new List<string>();
        if (policy.IncludeBios) cats.Add("BIOS");
        if (policy.IncludeDrivers) cats.Add("Drivers");
        if (policy.IncludeFirmware) cats.Add("Firmware");
        return cats.Count == 0 ? "Drivers" : string.Join(",", cats);
    }

    /// <summary>A stable folder so SoftPaqs pre-downloaded by a scheduled run are reused at install time.</summary>
    private static string DownloadFolder(AdapterContext ctx)
    {
        var folder = Path.Combine(ctx.Paths.Downloads, "HPIA");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string WriteSpList(string folder, IEnumerable<DetectedUpdate> updates)
    {
        var path = Path.Combine(folder, "splist.txt");
        File.WriteAllLines(path, updates.Select(u => HpiaReportParser.SpListNumber(u.VendorId)).Distinct());
        return path;
    }

    private static string? Find(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "HPImageAssistant.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    private static Version? GetVersion(string exe)
    {
        var info = FileVersionInfo.GetVersionInfo(exe);
        var match = VersionRegex().Match(info.ProductVersion ?? info.FileVersion ?? "");
        return match.Success && Version.TryParse(match.Value, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    private static async Task<(Version Version, string Url)?> GetLatestAsync(AdapterContext ctx, CancellationToken ct)
    {
        try
        {
            var html = await ctx.Downloader.GetStringAsync(HpiaPage, ct);
            var best = DownloadLinkRegex().Matches(html)
                .Select(m => (Ok: Version.TryParse(m.Groups["ver"].Value, out var v), Version: v, Url: m.Value))
                .Where(x => x.Ok && x.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (best.Ok) return (new Version(best.Version!.Major, best.Version.Minor, Math.Max(best.Version.Build, 0)), best.Url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning("Could not check HP's HPIA page: {Message}", ex.Message);
        }
        return null;
    }

    private static async Task<string> DownloadAsync(string url, string targetDir, AdapterContext ctx, CancellationToken ct)
    {
        var staging = ctx.Paths.NewDownloadFolder("hpia-setup");
        var installer = Path.Combine(staging, Path.GetFileName(new Uri(url).LocalPath));
        await ctx.Downloader.DownloadFileAsync(url, installer, ct);

        if (!Authenticode.IsSignedBy(installer, "HP Inc", out var signer))
            throw new InvalidDataException($"HP Image Assistant package failed signature verification: {signer}");
        ctx.Logger.LogInformation("HPIA package signature verified: {Signer}", signer);

        var extract = targetDir + ".new";
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        Directory.CreateDirectory(extract);

        // HP's own CMSL module extracts SoftPaqs with: -e -f "<folder>" -s
        var result = await ctx.Processes.RunAsync(installer, $"-e -f {ToolResolver.Quote(extract)} -s", ct, timeout: TimeSpan.FromMinutes(10));
        var exe = Find(extract) ?? throw new InvalidOperationException($"HPImageAssistant.exe not found after extraction (exit code {result.ExitCode})");
        if (!Authenticode.IsSignedBy(exe, "HP Inc", out signer))
            throw new InvalidDataException($"HPImageAssistant.exe failed signature verification: {signer}");

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        Directory.Move(extract, targetDir);
        return Find(targetDir)!;
    }

    [GeneratedRegex(@"https?://[^""'\s<>]+?hp-hpia-(?<ver>\d+(?:\.\d+){1,3})\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadLinkRegex();

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex VersionRegex();
}
