using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;

namespace UpdateAllWin.ManufacturerAdapters.WindowsUpdate;

/// <summary>
/// Any manufacturer: driver and firmware updates published to Windows Update, through the
/// Windows Update Agent COM API (Microsoft.Update.Session). Used as the fallback for OEMs
/// without an adapter (ASUS, Acer, MSI, Surface...) or alongside one when policy says so.
/// </summary>
public sealed class WindowsUpdateAdapter : IManufacturerAdapter
{
    public const string AdapterId = "windows-update";

    private dynamic? _session;

    public string Id => AdapterId;
    public string DisplayName => "Windows Update";
    public AdapterKind Kind => AdapterKind.Generic;

    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.Scan | AdapterCapabilities.Download | AdapterCapabilities.InstallIndividual |
        AdapterCapabilities.Drivers | AdapterCapabilities.Firmware | AdapterCapabilities.ReportsDownloadSize;

    public Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct) => Task.FromResult(OperatingSystem.IsWindows());

    public Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext context, CancellationToken ct) =>
        Task.FromResult(Type.GetTypeFromProgID("Microsoft.Update.Session") is null
            ? PrerequisiteResult.NotReady("The Windows Update Agent is not available")
            : PrerequisiteResult.Ok());

    public Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext context, CancellationToken ct) =>
        RunSta<IReadOnlyList<DetectedUpdate>>(() =>
        {
            _session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
            _session.ClientApplicationID = "UpdateAll Win";
            dynamic searcher = _session.CreateUpdateSearcher();

            context.Report(EngineStage.Scanning, "Searching Windows Update for drivers...");
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            dynamic updates = result.Updates;

            var items = new List<DetectedUpdate>();
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                dynamic u = updates[i];
                string driverClass = Safe(() => (string)u.DriverClass);
                var category = driverClass.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ? UpdateCategory.Firmware : UpdateCategory.Driver;
                long? size = null;
                try { size = Convert.ToInt64((decimal)u.MaxDownloadSize); } catch { }

                items.Add(new DetectedUpdate
                {
                    AdapterId = AdapterId,
                    VendorId = Safe(() => (string)u.Identity.UpdateID),
                    Name = Safe(() => (string)u.Title),
                    Source = DisplayName,
                    Manufacturer = Safe(() => (string)u.DriverProvider) is { Length: > 0 } provider ? provider : Safe(() => (string)u.DriverManufacturer),
                    Category = category,
                    AvailableVersion = Safe(() => ((DateTime)u.DriverVerDate).ToString("yyyy-MM-dd")),
                    Priority = UpdatePriority.Optional,
                    SizeBytes = size,
                    Restart = RestartBehavior.MayBeRequired,
                    Tag = u,
                });
            }
            context.Logger.LogInformation("Windows Update found {Count} driver update(s)", items.Count);
            return items;
        }, ct);

    public Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext context, CancellationToken ct) =>
        RunSta(() =>
        {
            var collection = BuildCollection(updates, out _);
            if (collection is null) return new AdapterOperationResult(true, false, "Nothing to download");
            dynamic downloader = _session!.CreateUpdateDownloader();
            downloader.Updates = collection;
            context.Report(EngineStage.Downloading, $"Downloading {updates.Count} driver update(s) from Windows Update...");
            dynamic result = downloader.Download();
            int code = (int)result.ResultCode;
            return new AdapterOperationResult(code is 2 or 3, false, $"Windows Update download result {Describe(code)}", code);
        }, ct);

    public Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> updates, AdapterContext context, CancellationToken ct) =>
        RunSta(() =>
        {
            var collection = BuildCollection(updates, out var included);
            if (collection is null) return new AdapterOperationResult(true, false, "Nothing to install");

            context.Report(EngineStage.Installing, $"Installing {included.Count} driver update(s)...");
            dynamic installer = _session!.CreateUpdateInstaller();
            installer.Updates = collection;
            dynamic result = installer.Install();
            bool reboot = (bool)result.RebootRequired;

            for (var i = 0; i < included.Count; i++)
            {
                int code = (int)result.GetUpdateResult(i).ResultCode;
                var ok = code is 2 or 3; // orcSucceeded, orcSucceededWithErrors
                included[i].State = ok ? (reboot ? UpdateState.InstalledRestartRequired : UpdateState.Installed) : UpdateState.Failed;
                included[i].StatusMessage = ok ? (reboot ? "Restart required" : "") : $"Windows Update result {Describe(code)}";
            }
            int overall = (int)result.ResultCode;
            return new AdapterOperationResult(overall is 2 or 3, reboot, $"Windows Update result {Describe(overall)}", overall);
        }, ct);

    public Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct)
    {
        try
        {
            dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Installer", throwOnError: true)!)!;
            bool busy = installer.IsBusy;
            return Task.FromResult(new InstallationStatus(busy, busy ? "Windows Update is installing" : "Idle"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InstallationStatus(false, ex.Message));
        }
    }

    public Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct)
    {
        try
        {
            dynamic info = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", throwOnError: true)!)!;
            return Task.FromResult((bool)info.RebootRequired ? RestartBehavior.Required : RestartBehavior.NotRequired);
        }
        catch
        {
            return Task.FromResult(RestartBehavior.Unknown);
        }
    }

    private dynamic? BuildCollection(IReadOnlyList<DetectedUpdate> updates, out List<DetectedUpdate> included)
    {
        included = [];
        if (_session is null) throw new InvalidOperationException("Check for updates before installing.");
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!;
        foreach (var item in updates)
        {
            if (item.Tag is null) continue;
            dynamic u = item.Tag;
            try { if (!(bool)u.EulaAccepted) u.AcceptEula(); } catch { /* no EULA */ }
            collection.Add(u);
            included.Add(item);
        }
        return included.Count == 0 ? null : collection;
    }

    private static string Describe(int code) => code switch
    {
        0 => "not started (0)",
        1 => "in progress (1)",
        2 => "succeeded (2)",
        3 => "succeeded with errors (3)",
        4 => "failed (4)",
        5 => "aborted (5)",
        _ => code.ToString(),
    };

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static readonly ComWorker Worker = new();

    /// <summary>
    /// WUA objects are apartment-threaded, and the update objects from a scan are reused when
    /// installing, so every call runs on one long-lived STA thread.
    /// </summary>
    private static Task<T> RunSta<T>(Func<T> work, CancellationToken ct) => Worker.Run(work).WaitAsync(ct);

    private sealed class ComWorker
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

        public ComWorker()
        {
            var thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true, Name = "WindowsUpdateCom" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public Task<T> Run<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }
}
