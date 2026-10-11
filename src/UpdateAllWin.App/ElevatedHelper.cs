using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.App;

/// <summary>
/// Admin-only actions. The UI runs unelevated; for these it relaunches itself with a UAC prompt
/// in a small no-UI helper mode, which talks to the service as an elevated administrator.
/// </summary>
public static class ElevatedHelper
{
    public const string ServiceName = "UpdateAllService";

    // ---------------------------------------------------------------- called from the UI (unelevated)

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Saves machine policy, prompting for elevation if needed. Returns null on success, else an error.</summary>
    public static async Task<string?> SavePolicyAsync(ServiceClient client, MachinePolicy policy)
    {
        try
        {
            var response = await client.SendAsync(IpcCommand.SetPolicy, policy);
            return response.Ok ? null : response.Error;
        }
        catch (ServiceAccessDeniedException)
        {
            var file = Path.Combine(Path.GetTempPath(), $"UpdateAllWin-policy-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(policy, IpcProtocol.Json));
            try
            {
                var code = await RunElevatedAsync($"--set-policy \"{file}\"");
                return code switch
                {
                    0 => null,
                    null => "Administrator approval was cancelled.",
                    _ => "The settings could not be saved. See the service log for details.",
                };
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    public static Task<int?> ServiceControlAsync(string action) => RunElevatedAsync($"--service-control {action}");

    /// <summary>Returns the exit code, or null if the user declined the UAC prompt.</summary>
    private static async Task<int?> RunElevatedAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helper modes (elevated, before any UI)

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length >= 2 && args[0] == "--set-policy")
        {
            exitCode = SetPolicy(args[1]);
            return true;
        }
        if (args.Length >= 2 && args[0] == "--service-control")
        {
            exitCode = ServiceControl(args[1]);
            return true;
        }
        return false;
    }

    private static int SetPolicy(string file)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<MachinePolicy>(File.ReadAllText(file), IpcProtocol.Json);
            var response = new ServiceClient().SendAsync(IpcCommand.SetPolicy, policy).GetAwaiter().GetResult();
            return response.Ok ? 0 : 1;
        }
        catch
        {
            return 2;
        }
    }

    private static int ServiceControl(string action)
    {
        var serviceExe = Path.Combine(AppContext.BaseDirectory, "UpdateAllService.exe");
        return action switch
        {
            "start" => Run("sc.exe", $"start {ServiceName}"),
            "enable" => EnableService(),
            "disable" => DisableService(),
            "install" when File.Exists(serviceExe) => Run(serviceExe, "install-service"),
            _ => 1,
        };
    }

    private static int EnableService()
    {
        var code = Run("sc.exe", $"config {ServiceName} start= delayed-auto");
        return code == 0 ? Run("sc.exe", $"start {ServiceName}") : code;
    }

    private static int DisableService()
    {
        Run("sc.exe", $"stop {ServiceName}"); // may already be stopped
        return Run("sc.exe", $"config {ServiceName} start= disabled");
    }

    private static int Run(string file, string args)
    {
        var path = Path.IsPathRooted(file) ? file : Path.Combine(Environment.SystemDirectory, file);
        using var p = Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
