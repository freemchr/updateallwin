using System.IO.Pipes;
using System.Security.Principal;

namespace UpdateAllWin.Contracts;

public sealed class ServiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ServiceAccessDeniedException(string message) : Exception(message);

/// <summary>Client for the UpdateAllService named pipe. One connection per request.</summary>
public sealed class ServiceClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public ServiceClient(string? pipeName = null, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName ?? IpcProtocol.PipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<IpcResponse> SendAsync(IpcCommand command, object? payload = null, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification); // lets the service check who we are, but not act as us
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_connectTimeout);
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            throw new ServiceUnavailableException("The UpdateAll Win service is not running.", ex);
        }

        var request = new IpcRequest(command, payload is null ? null : IpcProtocol.ToElement(payload));
        await IpcProtocol.WriteAsync(pipe, request, ct);
        var response = await IpcProtocol.ReadAsync<IpcResponse>(pipe, ct)
                       ?? throw new InvalidDataException("Empty response from service.");
        if (response.AccessDenied) throw new ServiceAccessDeniedException(response.Error ?? "Access denied.");
        return response;
    }

    public async Task<T?> GetAsync<T>(IpcCommand command, object? payload = null, CancellationToken ct = default)
    {
        var response = await SendAsync(command, payload, ct);
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? $"{command} failed.");
        return IpcProtocol.FromElement<T>(response.Payload);
    }

    public async Task InvokeAsync(IpcCommand command, object? payload = null, CancellationToken ct = default)
    {
        var response = await SendAsync(command, payload, ct);
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? $"{command} failed.");
    }

    public Task<EngineStatus?> GetStatusAsync(CancellationToken ct = default) => GetAsync<EngineStatus>(IpcCommand.GetStatus, ct: ct);
}
