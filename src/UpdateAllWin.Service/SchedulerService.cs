using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Engine;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.Core.Scheduling;

namespace UpdateAllWin.Service;

/// <summary>
/// Runs scheduled scans (and, per policy, downloads/installs), recovers interrupted operations
/// after a service restart, and does housekeeping.
/// </summary>
internal sealed class SchedulerService(
    UpdateEngine engine,
    IPolicyStore policy,
    IHistoryStore history,
    DataPaths paths,
    TimeProvider time,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private DateTime? _recoveryScanAt;
    private DateTime _nextHousekeeping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("UpdateAllService {Version} started", engine.GetStatus().ServiceVersion);
        history.Add(new HistoryEntry { TimeUtc = DateTime.UtcNow, Kind = HistoryKind.Service, Title = "Service started", Result = "Running", Success = true });

        if (engine.RecoverAfterStartup())
            _recoveryScanAt = time.GetLocalNow().DateTime.AddMinutes(2);

        // Give the PC a few minutes after boot before any scheduled work.
        var earliest = time.GetLocalNow().DateTime.AddMinutes(3);

        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                Housekeeping();
                var now = time.GetLocalNow().DateTime;
                if (now >= earliest) RunDueWork(now);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void RunDueWork(DateTime now)
    {
        if (engine.IsBusy) return;
        var p = policy.Current;
        if (Schedule.IsInQuietHours(p, now)) return;

        if (_recoveryScanAt is { } at && now >= at)
        {
            _recoveryScanAt = null;
            logger.LogInformation("Running recovery scan after an interrupted operation");
            engine.TryStart(new EngineOperation(OperationKind.Scan, OperationTrigger.Recovery));
            return;
        }

        var lastScan = engine.GetStatus().LastScanUtc?.ToLocalTime();
        if (Schedule.NextScan(p, lastScan, now) is not { } due || now < due) return;

        var kind = p.AutoInstallDrivers || p.AutoInstallFirmware ? OperationKind.UpdateAll
            : p.AutoDownload ? OperationKind.Download
            : OperationKind.Scan;
        logger.LogInformation("Scheduled {Kind} is due (due {Due:g})", kind, due);
        engine.TryStart(new EngineOperation(kind, OperationTrigger.Schedule, Automatic: true));
    }

    private void Housekeeping()
    {
        if (DateTime.UtcNow < _nextHousekeeping) return;
        _nextHousekeeping = DateTime.UtcNow.AddHours(12);
        var retention = TimeSpan.FromDays(policy.Current.LogRetentionDays);
        history.Prune(TimeSpan.FromDays(Math.Max(90, policy.Current.LogRetentionDays)));
        paths.CleanupOlderThan(retention);
    }
}
