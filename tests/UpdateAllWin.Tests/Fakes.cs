using Microsoft.Extensions.Logging.Abstractions;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Adapters;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Engine;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.Core.Safety;

namespace UpdateAllWin.Tests;

internal sealed class FakeSafety : ISystemSafety
{
    public PowerSnapshot Power { get; set; } = new(true, true, 90);
    public BitLockerState BitLocker { get; set; } = new(false, false, true, "off");
    public bool SuspendSucceeds { get; set; } = true;
    public int SuspendCalls { get; private set; }
    public long FreeBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public bool OsRestartPending { get; set; }
    public int RestorePoints { get; private set; }
    public int Restarts { get; private set; }
    public DateTime BootTimeUtc { get; set; } = DateTime.UtcNow.AddHours(-1);

    public PowerSnapshot GetPower() => Power;
    public BitLockerState GetBitLockerState() => BitLocker;
    public bool SuspendBitLockerForOneRestart(out string detail) { SuspendCalls++; detail = SuspendSucceeds ? "suspended" : "failed"; return SuspendSucceeds; }
    public long GetSystemDriveFreeBytes() => FreeBytes;
    public bool IsRestartPendingFromOs() => OsRestartPending;
    public bool TryCreateRestorePoint(string description, out string detail) { RestorePoints++; detail = "ok"; return true; }
    public IDisposable PreventSleep(string reason) => new Nothing();
    public DateTime GetBootTimeUtc() => BootTimeUtc;
    public void RestartComputer(int delaySeconds, string message) => Restarts++;

    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

internal sealed class FakeDevice(string manufacturer = "HP") : IDeviceInfoProvider
{
    public DeviceInfo GetDeviceInfo(bool refresh = false) => new()
    {
        Manufacturer = manufacturer,
        Model = "Test Book 840",
        SerialNumber = "5CG1234XYZ",
        OsName = "Windows 11 Pro",
        OsBuild = "26100",
        BiosVersion = "F.18",
    };
}

internal sealed class InMemoryPolicyStore(MachinePolicy? initial = null) : IPolicyStore
{
    public MachinePolicy Current { get; private set; } = JsonPolicyStore.Validate(initial ?? new MachinePolicy());
    public event EventHandler? Changed;
    public void Save(MachinePolicy policy) { Current = JsonPolicyStore.Validate(policy); Changed?.Invoke(this, EventArgs.Empty); }
}

internal sealed class NoProcesses : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct, string? workingDirectory = null, TimeSpan? timeout = null) =>
        throw new InvalidOperationException("Tests must not start processes.");
}

internal sealed class NoDownloads : IDownloader
{
    public Task<string> GetStringAsync(string url, CancellationToken ct) => throw new InvalidOperationException("No network in tests.");
    public Task DownloadFileAsync(string url, string destination, CancellationToken ct, string? expectedSha256 = null, IProgress<int>? percent = null) =>
        throw new InvalidOperationException("No network in tests.");
}

/// <summary>A scripted adapter with configurable capabilities, for engine policy tests.</summary>
internal sealed class ScriptedAdapter(string id, AdapterCapabilities capabilities, params DetectedUpdate[] updates) : IManufacturerAdapter
{
    public List<IReadOnlyList<string>> InstallCalls { get; } = [];
    public string Id => id;
    public string DisplayName => $"Scripted {id}";
    public AdapterKind Kind => AdapterKind.Manufacturer;
    public AdapterCapabilities Capabilities => capabilities;
    public Task<bool> IsSupportedAsync(DeviceInfo device, CancellationToken ct) => Task.FromResult(true);
    public Task<PrerequisiteResult> CheckPrerequisitesAsync(AdapterContext context, CancellationToken ct) => Task.FromResult(PrerequisiteResult.Ok());
    public Task<IReadOnlyList<DetectedUpdate>> ScanForUpdatesAsync(AdapterContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DetectedUpdate>>(updates.Select(Clone).ToList());
    public Task<AdapterOperationResult> DownloadUpdatesAsync(IReadOnlyList<DetectedUpdate> u, AdapterContext context, CancellationToken ct) =>
        Task.FromResult(AdapterOperationResult.Unsupported("n/a"));
    public Task<AdapterOperationResult> InstallUpdatesAsync(IReadOnlyList<DetectedUpdate> u, AdapterContext context, CancellationToken ct)
    {
        InstallCalls.Add(u.Select(x => x.VendorId).ToList());
        foreach (var x in u) x.State = UpdateState.Installed;
        return Task.FromResult(new AdapterOperationResult(true, false, "ok"));
    }
    public Task<InstallationStatus> GetInstallationStatusAsync(CancellationToken ct) => Task.FromResult(new InstallationStatus(false, ""));
    public Task<RestartBehavior> GetRestartRequirementAsync(CancellationToken ct) => Task.FromResult(RestartBehavior.Unknown);

    public static DetectedUpdate Update(string adapterId, string vendorId, UpdateCategory category) => new()
    {
        AdapterId = adapterId, VendorId = vendorId, Name = vendorId, Category = category, AvailableVersion = "2.0",
    };

    private static DetectedUpdate Clone(DetectedUpdate u) => new()
    {
        AdapterId = u.AdapterId, VendorId = u.VendorId, Name = u.Name, Category = u.Category, AvailableVersion = u.AvailableVersion,
    };
}

/// <summary>Builds an engine over temp storage. Dispose deletes the temp folder.</summary>
internal sealed class EngineFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "uaw-tests-" + Guid.NewGuid().ToString("N"));
    public FakeSafety Safety { get; } = new();
    public InMemoryPolicyStore Policy { get; }
    public SqliteStore Store { get; }
    public UpdateEngine Engine { get; }

    public EngineFixture(MachinePolicy policy, params IManufacturerAdapter[] adapters)
    {
        Directory.CreateDirectory(Root);
        Policy = new InMemoryPolicyStore(policy);
        Store = new SqliteStore(Path.Combine(Root, "test.db"));
        Engine = Create(adapters);
    }

    public UpdateEngine Create(IManufacturerAdapter[] adapters) => new(
        adapters, Policy, Store, Store, new FakeDevice(), Safety, new NoProcesses(), new NoDownloads(),
        new DataPaths(Root), NullLoggerFactory.Instance, options: new EngineOptions { UseGlobalLock = false });

    public void Dispose()
    {
        Engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { }
    }
}
