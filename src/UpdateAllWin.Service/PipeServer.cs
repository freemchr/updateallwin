using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Diagnostics;
using UpdateAllWin.Core.Engine;

namespace UpdateAllWin.Service;

/// <summary>
/// Named-pipe endpoint for the desktop app and toolbar. Accepts only the fixed
/// <see cref="IpcCommand"/> set; policy changes require an elevated administrator.
/// </summary>
internal sealed class PipeServer(
    UpdateEngine engine,
    IPolicyStore policy,
    IHistoryStore history,
    IDeviceInfoProvider device,
    DiagnosticsExporter diagnostics,
    Core.Infrastructure.DataPaths paths,
    ILogger<PipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ListenAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Pipe server stopped");
            throw;
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Pipe server listening on {Pipe}", IpcProtocol.PipeName);
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(first);
                first = false;
            }
            catch (UnauthorizedAccessException ex) when (first)
            {
                // Another process already owns the pipe name: refuse to run rather than talk to an impostor's clients.
                logger.LogCritical(ex, "The UpdateAll Win pipe is already in use by another process");
                throw;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = HandleConnectionAsync(pipe, stoppingToken);
        }
    }

    private static NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The account running the server (LocalSystem normally; the developer when run from a console).
        using (var self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Signed-in users may talk to the service but not create pipe instances (prevents squatting).
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(IpcProtocol.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using var _ = pipe;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var request = await IpcProtocol.ReadAsync<IpcRequest>(pipe, timeout.Token);
            if (request is null) return;
            var (isAdmin, user) = IdentifyClient(pipe);
            var response = Dispatch(request, isAdmin, user);
            await IpcProtocol.WriteAsync(pipe, response, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogDebug("Pipe request aborted: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipe request failed");
        }
    }

    /// <summary>
    /// Identifies the caller from its process token (GetNamedPipeClientProcessId) rather than by
    /// impersonating it, so no client security context can ever leak into service threads.
    /// </summary>
    private (bool IsAdmin, string User) IdentifyClient(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return (false, "unknown");
            using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (process.IsInvalid || !OpenProcessToken(process, 0x0008 | 0x0002 /* TOKEN_QUERY | TOKEN_DUPLICATE */, out var token))
                return (false, $"pid {pid}");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                // With UAC this is only true for elevated processes (the filtered token has a deny-only Administrators SID).
                var isAdmin = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                return (isAdmin, identity.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not identify pipe client: {Message}", ex.Message);
            return (false, "unknown");
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

    internal IpcResponse Dispatch(IpcRequest request, bool isAdmin, string user)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommand.Ping:
                    return Ok();
                case IpcCommand.GetStatus:
                    return Ok(engine.GetStatus());
                case IpcCommand.GetDevice:
                    return Ok(device.GetDeviceInfo());
                case IpcCommand.GetPolicy:
                    return Ok(policy.Current);
                case IpcCommand.GetHistory:
                {
                    var q = IpcProtocol.FromElement<HistoryQuery>(request.Payload) ?? new HistoryQuery();
                    return Ok(history.Query(q.Limit, q.Kind));
                }
                case IpcCommand.GetBiosSafetyReport:
                    return Ok(engine.GetBiosSafetyReport(IpcProtocol.FromElement<List<string>>(request.Payload) ?? []));

                case IpcCommand.StartScan:
                    return Start(new EngineOperation(OperationKind.Scan, ClientTrigger(request)));
                case IpcCommand.StartUpdateAll:
                    if (!CanInstall(isAdmin)) return Denied("Your administrator only allows administrators to install updates.");
                    logger.LogInformation("Update All requested by {User}", user);
                    return Start(new EngineOperation(OperationKind.UpdateAll, ClientTrigger(request)));
                case IpcCommand.InstallSelected:
                {
                    if (!CanInstall(isAdmin)) return Denied("Your administrator only allows administrators to install updates.");
                    var install = IpcProtocol.FromElement<InstallRequest>(request.Payload) ?? new InstallRequest([], []);
                    logger.LogInformation("{User} requested {Count} update(s), {Bios} BIOS approval(s)", user, install.Keys.Count, install.ApprovedBiosKeys.Count);
                    return Start(new EngineOperation(OperationKind.Install, OperationTrigger.User, install.Keys, install.ApprovedBiosKeys));
                }
                case IpcCommand.Cancel:
                    engine.Cancel();
                    return Ok();

                case IpcCommand.SetPolicy:
                {
                    if (!isAdmin) return Denied("Changing UpdateAll Win policy requires administrator rights.");
                    var updated = IpcProtocol.FromElement<MachinePolicy>(request.Payload) ?? throw new InvalidDataException("Missing policy.");
                    var error = ValidatePaths(updated);
                    if (error is not null) return new IpcResponse(false, error);
                    if (updated.DownloadFolder is { Length: > 0 } downloads) paths.Secure(downloads);
                    policy.Save(updated);
                    history.Add(new HistoryEntry { TimeUtc = DateTime.UtcNow, Kind = HistoryKind.Policy, Title = "Settings changed", Result = $"By {user}", Success = true });
                    return Ok(policy.Current);
                }
                case IpcCommand.ExcludeUpdate:
                case IpcCommand.IncludeUpdate:
                {
                    if (!isAdmin) return Denied("Excluding updates requires administrator rights.");
                    var key = (IpcProtocol.FromElement<string>(request.Payload) ?? "").Trim().ToLowerInvariant();
                    if (key.Length == 0) return new IpcResponse(false, "Missing update key.");
                    var list = policy.Current.ExcludedUpdates.ToList();
                    if (request.Command == IpcCommand.ExcludeUpdate) { if (!list.Contains(key)) list.Add(key); }
                    else list.Remove(key);
                    policy.Save(policy.Current with { ExcludedUpdates = list });
                    return Ok(policy.Current);
                }

                case IpcCommand.ExportDiagnostics:
                    return Ok(diagnostics.Export());
                case IpcCommand.RestartComputer:
                {
                    var delay = Math.Clamp(IpcProtocol.FromElement<int?>(request.Payload) ?? 30, 0, 600);
                    logger.LogInformation("Restart requested by {User}", user);
                    return engine.RequestRestart(delay) ? Ok() : new IpcResponse(false, "No restart is pending.");
                }
                case IpcCommand.DeferRestart:
                    engine.RecordRestartDeferral();
                    return Ok();
                default:
                    return new IpcResponse(false, $"Unsupported command {request.Command}.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Command} failed", request.Command);
            return new IpcResponse(false, ex.Message);
        }
    }

    private bool CanInstall(bool isAdmin) => isAdmin || policy.Current.AllowStandardUsersToInstall;

    /// <summary>Clients may only label requests as coming from the app or the toolbar.</summary>
    private static OperationTrigger ClientTrigger(IpcRequest request) =>
        IpcProtocol.FromElement<OperationTrigger?>(request.Payload) == OperationTrigger.Toolbar ? OperationTrigger.Toolbar : OperationTrigger.User;

    private IpcResponse Start(EngineOperation op) =>
        engine.TryStart(op) ? Ok() : new IpcResponse(false, "Another update operation is already running.");

    /// <summary>Paths the SYSTEM service will execute or read must not be user-writable.</summary>
    private static string? ValidatePaths(MachinePolicy p)
    {
        if (p.HpiaPath is { Length: > 0 } hpia)
        {
            if (!Path.IsPathFullyQualified(hpia) || !File.Exists(hpia)) return "The HP Image Assistant path does not exist.";
            if (!ManufacturerAdapters.Common.ToolResolver.IsTrustedLocation(hpia, new Core.Infrastructure.DataPaths().Root))
                return "HP Image Assistant must be located under Program Files.";
        }
        if (p.DownloadFolder is { Length: > 0 } folder && !Path.IsPathFullyQualified(folder))
            return "The download folder must be a full path.";
        if (p.HpBiosPasswordFile is { Length: > 0 } pwd && !File.Exists(pwd))
            return "The HP BIOS password file does not exist.";
        return null;
    }

    private static IpcResponse Ok(object? payload = null) => new(true, Payload: payload is null ? null : IpcProtocol.ToElement(payload));

    private static IpcResponse Denied(string message) => new(false, message, AccessDenied: true);
}
