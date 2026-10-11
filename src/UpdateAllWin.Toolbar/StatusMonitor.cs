using Microsoft.UI.Dispatching;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.Toolbar;

public sealed record StatusChange(EngineStatus? Previous, EngineStatus? Current, bool ServiceAvailable, bool AvailabilityChanged);

/// <summary>
/// Polls UpdateAllService and raises <see cref="Changed"/> on the UI thread. Polls every second while
/// something is happening or a window is visible, otherwise every 10 seconds.
/// </summary>
public sealed class StatusMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private long _lastRevision = -1;
    private DateTime _lastPolicyFetch = DateTime.MinValue;

    public StatusMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _ = Task.Run(LoopAsync);
    }

    public ServiceClient Client { get; } = new();
    public EngineStatus? Status { get; private set; }
    public MachinePolicy Policy { get; private set; } = new();
    public bool ServiceAvailable { get; private set; }

    /// <summary>Set while a window that shows live status is visible.</summary>
    public bool FastPolling { get; set; }

    public event EventHandler<StatusChange>? Changed;

    public bool IsBusy => Status?.Stage is EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing;

    public void Refresh() => _wake.Release();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            EngineStatus? status = null;
            var available = false;
            try
            {
                status = await Client.GetStatusAsync(_cts.Token);
                available = true;
                if (DateTime.UtcNow - _lastPolicyFetch > TimeSpan.FromMinutes(10))
                {
                    Policy = await Client.GetAsync<MachinePolicy>(IpcCommand.GetPolicy, ct: _cts.Token) ?? Policy;
                    _lastPolicyFetch = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Service stopped/not installed: report unavailable and keep trying.
            }

            var availabilityChanged = available != ServiceAvailable;
            if (availabilityChanged || (status is not null && status.Revision != _lastRevision) || (status is null && Status is not null))
            {
                var previous = Status;
                _lastRevision = status?.Revision ?? -1;
                _dispatcher.TryEnqueue(() =>
                {
                    Status = status;
                    ServiceAvailable = available;
                    Changed?.Invoke(this, new StatusChange(previous, status, available, availabilityChanged));
                });
            }

            var busy = status?.Stage is EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing;
            var delay = busy || FastPolling ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
            try { await _wake.WaitAsync(delay, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Forces a policy re-read on the next poll (after settings change).</summary>
    public void InvalidatePolicy()
    {
        _lastPolicyFetch = DateTime.MinValue;
        Refresh();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
