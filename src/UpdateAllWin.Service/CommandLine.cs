using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Diagnostics;
using UpdateAllWin.Core.Engine;

namespace UpdateAllWin.Service;

/// <summary>
/// Scriptable interface for Intune/RMM/Task Scheduler. Talks to the running service when it is
/// available; otherwise runs the engine in-process (administrator required).
///
/// Exit codes: 0 success/up to date, 1 failure(s), 2 busy, 3 access denied/not elevated,
/// 4 error, 5 BIOS update awaiting approval, 3010 success but restart required.
/// </summary>
internal static class CommandLine
{
    private static readonly string[] Commands =
        ["help", "--help", "/?", "status", "scan", "update-all", "history", "export-diagnostics", "install-service", "uninstall-service"];

    public static bool IsCommand(string arg) => Commands.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        var json = Has(args, "--json");
        try
        {
            return command switch
            {
                "status" => await StatusAsync(json),
                "scan" => await RunOperationAsync(OperationKind.Scan, args, json),
                "update-all" => await RunOperationAsync(OperationKind.UpdateAll, args, json),
                "history" => await HistoryAsync(args, json),
                "export-diagnostics" => await ExportAsync(),
                "install-service" => InstallService(),
                "uninstall-service" => UninstallService(),
                _ => Help(),
            };
        }
        catch (ServiceAccessDeniedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 4;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            UpdateAll Win command line

              UpdateAllService.exe status                     Show update status
              UpdateAllService.exe scan                       Check for updates
              UpdateAllService.exe update-all [options]       Check and install eligible updates
                    --approve-bios      Administrator approval to install BIOS updates
                    --restart           Restart automatically (60 s warning) if required
              UpdateAllService.exe history [--limit N]        Show update history
              UpdateAllService.exe export-diagnostics         Create a diagnostics zip
              UpdateAllService.exe install-service            Register the Windows service (admin)
              UpdateAllService.exe uninstall-service          Remove the Windows service (admin)

              --json   Machine-readable output

            Exit codes: 0 OK/up to date, 1 failures, 2 busy, 3 access denied, 4 error,
                        5 BIOS update awaiting approval, 3010 restart required
            """);
        return 0;
    }

    // ------------------------------------------------------------------ operations

    private static async Task<int> RunOperationAsync(OperationKind kind, string[] args, bool json)
    {
        var approveBios = Has(args, "--approve-bios");
        var restart = Has(args, "--restart");
        if (approveBios && !IsElevated())
        {
            Console.Error.WriteLine("--approve-bios requires an elevated administrator or SYSTEM.");
            return 3;
        }

        var client = new ServiceClient();
        EngineStatus? final;
        try
        {
            await client.InvokeAsync(IpcCommand.Ping);
            final = await RunViaServiceAsync(client, kind, approveBios, json);
            if (final is null) return 2;
        }
        catch (ServiceUnavailableException)
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine("The UpdateAll Win service isn't running. Run this command as administrator to update without it.");
                return 3;
            }
            Log(json, "Service not running - running in-process.");
            return await RunInProcessAsync(kind, approveBios, restart, json);
        }

        var code = ExitCodeFor(final);
        Report(final, json);
        if (code == 3010 && restart)
        {
            Log(json, "Restarting in 60 seconds.");
            await client.InvokeAsync(IpcCommand.RestartComputer, 60);
        }
        return code;
    }

    private static async Task<EngineStatus?> RunViaServiceAsync(ServiceClient client, OperationKind kind, bool approveBios, bool json)
    {
        var start = kind == OperationKind.Scan || approveBios ? IpcCommand.StartScan : IpcCommand.StartUpdateAll;
        var response = await client.SendAsync(start, OperationTrigger.User);
        if (!response.Ok)
        {
            Console.Error.WriteLine(response.Error);
            return null;
        }
        var status = await WaitForIdleAsync(client, json);

        if (kind == OperationKind.UpdateAll && approveBios)
        {
            // Admin-approved: install everything eligible including BIOS updates.
            var keys = status.Updates.Where(u => u.State is UpdateState.Available or UpdateState.AwaitingApproval).Select(u => u.Key).ToList();
            var bios = status.Updates.Where(u => u.Category == UpdateCategory.Bios).Select(u => u.Key).ToList();
            if (keys.Count > 0)
            {
                response = await client.SendAsync(IpcCommand.InstallSelected, new InstallRequest(keys, bios));
                if (!response.Ok)
                {
                    Console.Error.WriteLine(response.Error);
                    return null;
                }
                status = await WaitForIdleAsync(client, json);
            }
        }
        return status;
    }

    private static async Task<EngineStatus> WaitForIdleAsync(ServiceClient client, bool json)
    {
        string? lastMessage = null;
        await Task.Delay(500);
        while (true)
        {
            var status = await client.GetStatusAsync() ?? throw new InvalidOperationException("No status from service.");
            if (status.Message != lastMessage)
            {
                Log(json, status.Message);
                lastMessage = status.Message;
            }
            if (status.Stage is not (EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing)) return status;
            await Task.Delay(1000);
        }
    }

    private static async Task<int> RunInProcessAsync(OperationKind kind, bool approveBios, bool restart, bool json)
    {
        await using var provider = BuildProvider();
        var engine = provider.GetRequiredService<UpdateEngine>();
        var lastMessage = "";
        engine.StatusChanged += (_, _) =>
        {
            var s = engine.GetStatus();
            if (s.Message != lastMessage) Log(json, lastMessage = s.Message);
        };

        var outcome = await engine.RunAsync(new EngineOperation(kind, OperationTrigger.CommandLine, ApproveAllBios: approveBios));
        if (outcome is null)
        {
            Console.Error.WriteLine("Another update operation is already running on this PC.");
            return 2;
        }
        var status = engine.GetStatus();
        Report(status, json);
        var code = ExitCodeFor(status);
        if (code == 3010 && restart) engine.RequestRestart(60);
        return code;
    }

    private static async Task<int> StatusAsync(bool json)
    {
        EngineStatus? status;
        try
        {
            status = await new ServiceClient().GetStatusAsync();
        }
        catch (ServiceUnavailableException)
        {
            await using var provider = BuildProvider();
            status = provider.GetRequiredService<UpdateEngine>().GetStatus();
        }
        Report(status!, json);
        return ExitCodeFor(status!);
    }

    private static async Task<int> HistoryAsync(string[] args, bool json)
    {
        var limit = int.TryParse(Value(args, "--limit"), out var l) ? l : 50;
        IReadOnlyList<HistoryEntry> entries;
        try
        {
            entries = await new ServiceClient().GetAsync<List<HistoryEntry>>(IpcCommand.GetHistory, new HistoryQuery(limit)) ?? [];
        }
        catch (ServiceUnavailableException)
        {
            await using var provider = BuildProvider();
            entries = provider.GetRequiredService<Core.Data.IHistoryStore>().Query(limit);
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(entries, new JsonSerializerOptions(IpcProtocol.Json) { WriteIndented = true }));
            return 0;
        }
        foreach (var e in entries)
            Console.WriteLine($"{e.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {(e.Success ? "OK  " : "FAIL")}  {e.Kind,-8} {e.Title} - {e.Result}");
        return 0;
    }

    private static async Task<int> ExportAsync()
    {
        string path;
        try
        {
            path = await new ServiceClient().GetAsync<string>(IpcCommand.ExportDiagnostics) ?? "";
        }
        catch (ServiceUnavailableException)
        {
            await using var provider = BuildProvider();
            path = provider.GetRequiredService<DiagnosticsExporter>().Export();
        }
        Console.WriteLine(path);
        return 0;
    }

    // ------------------------------------------------------------------ service registration (dev/manual installs; the MSI does this normally)

    private static int InstallService()
    {
        if (!IsElevated()) { Console.Error.WriteLine("Run as administrator."); return 3; }
        var exe = Environment.ProcessPath!;
        var code = Sc($"create {ServiceHost.ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= LocalSystem DisplayName= \"UpdateAll Win Service\"");
        Sc($"description {ServiceHost.ServiceName} \"Checks for and installs driver, BIOS and firmware updates using manufacturer tools.\"");
        Sc($"failure {ServiceHost.ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
        Sc($"start {ServiceHost.ServiceName}");
        return code == 0 ? 0 : 4;
    }

    private static int UninstallService()
    {
        if (!IsElevated()) { Console.Error.WriteLine("Run as administrator."); return 3; }
        Sc($"stop {ServiceHost.ServiceName}");
        return Sc($"delete {ServiceHost.ServiceName}") == 0 ? 0 : 4;
    }

    private static int Sc(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments) { UseShellExecute = false })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    // ------------------------------------------------------------------ helpers

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => ServiceHost.ConfigureServices(services, logging, "UpdateAllCli"));
        return services.BuildServiceProvider();
    }

    private static int ExitCodeFor(EngineStatus status)
    {
        if (status.Stage == EngineStage.Failed || status.Updates.Any(u => u.State == UpdateState.Failed)) return 1;
        if (status.RestartPending) return 3010;
        if (status.Updates.Any(u => u.State == UpdateState.AwaitingApproval)) return 5;
        return 0;
    }

    private static void Report(EngineStatus status, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions(IpcProtocol.Json) { WriteIndented = true }));
            return;
        }
        Console.WriteLine();
        Console.WriteLine($"Status: {status.Overall} - {status.Message}");
        if (status.LastSuccessfulScanUtc is { } last) Console.WriteLine($"Last successful scan: {last.ToLocalTime():g}");
        foreach (var u in status.Updates)
            Console.WriteLine($"  [{u.State,-24}] {u.Category,-8} {u.Name} {u.CurrentVersion} -> {u.AvailableVersion} {u.StatusMessage}");
        if (status.RestartPending) Console.WriteLine("A restart is required to finish installing updates.");
        if (!string.IsNullOrEmpty(status.LastError)) Console.WriteLine($"Errors: {status.LastError}");
    }

    private static void Log(bool json, string message)
    {
        if (!json) Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
