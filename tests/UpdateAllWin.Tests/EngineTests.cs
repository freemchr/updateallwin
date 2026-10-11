using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Engine;
using UpdateAllWin.Core.Safety;
using UpdateAllWin.ManufacturerAdapters.Simulation;

namespace UpdateAllWin.Tests;

public class EngineTests
{
    private static MachinePolicy Sim(string scenario, Func<MachinePolicy, MachinePolicy>? tweak = null)
    {
        var p = new MachinePolicy { SimulationMode = true, SimulationScenario = scenario };
        return tweak is null ? p : tweak(p);
    }

    private static EngineFixture Fixture(MachinePolicy policy) => new(policy, new SimulatedAdapter(TimeSpan.Zero));

    private static Task<OperationOutcome?> Run(EngineFixture f, OperationKind kind, bool automatic = false, IReadOnlyList<string>? keys = null, IReadOnlyList<string>? bios = null) =>
        f.Engine.RunAsync(new EngineOperation(kind, OperationTrigger.User, keys, bios, Automatic: automatic));

    [Fact]
    public async Task No_updates_reports_up_to_date_quickly()
    {
        using var f = Fixture(Sim("UpToDate"));
        var outcome = await Run(f, OperationKind.UpdateAll);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.Equal("Your PC is up to date", outcome.Message);
        var status = f.Engine.GetStatus();
        Assert.Equal(OverallStatus.UpToDate, status.Overall);
        Assert.Empty(status.Updates);
        Assert.NotNull(status.LastSuccessfulScanUtc);
    }

    [Fact]
    public async Task Scan_finds_updates_and_reports_orange_status()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        await Run(f, OperationKind.Scan);

        var status = f.Engine.GetStatus();
        Assert.Equal(3, status.Updates.Count);
        Assert.Equal(OverallStatus.UpdatesAvailable, status.Overall);
        Assert.Contains(status.Updates, u => u.Category == UpdateCategory.Bios && u.RequiresApproval);
    }

    [Fact]
    public async Task Update_all_installs_drivers_but_never_bypasses_bios_approval()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        var outcome = await Run(f, OperationKind.UpdateAll);

        var status = f.Engine.GetStatus();
        Assert.All(status.Updates.Where(u => u.Category == UpdateCategory.Driver),
            u => Assert.True(u.State is UpdateState.Installed or UpdateState.InstalledRestartRequired));
        var bios = Assert.Single(status.Updates, u => u.Category == UpdateCategory.Bios);
        Assert.Equal(UpdateState.AwaitingApproval, bios.State);
        Assert.Equal(1, outcome!.AwaitingApproval);
        Assert.True(status.RestartPending); // the graphics driver needs a restart
        Assert.Equal(OverallStatus.RestartRequired, status.Overall);
    }

    [Fact]
    public async Task Approved_bios_installs_when_safe()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        await Run(f, OperationKind.Scan);
        var bios = f.Engine.GetStatus().Updates.Single(u => u.Category == UpdateCategory.Bios);

        await Run(f, OperationKind.Install, keys: [bios.Key], bios: [bios.Key]);

        Assert.Equal(UpdateState.InstalledRestartRequired, f.Engine.GetStatus().Updates.Single(u => u.Key == bios.Key).State);
        Assert.Contains(f.Store.Query(), h => h.Kind == HistoryKind.Approval);
    }

    [Fact]
    public async Task Bios_is_blocked_on_battery_power_even_when_approved()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        f.Safety.Power = new PowerSnapshot(OnAcPower: false, HasBattery: true, BatteryPercent: 95);
        await Run(f, OperationKind.Scan);
        var bios = f.Engine.GetStatus().Updates.Single(u => u.Category == UpdateCategory.Bios);

        await Run(f, OperationKind.Install, keys: [bios.Key], bios: [bios.Key]);

        var after = f.Engine.GetStatus().Updates.Single(u => u.Key == bios.Key);
        Assert.Equal(UpdateState.Skipped, after.State);
        Assert.Contains("power adapter", after.StatusMessage);
    }

    [Fact]
    public async Task Bios_is_blocked_when_bitlocker_has_no_recovery_key()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        f.Safety.BitLocker = new BitLockerState(ProtectionOn: true, HasRecoveryPassword: false, Known: true, "on");
        await Run(f, OperationKind.Scan);
        var bios = f.Engine.GetStatus().Updates.Single(u => u.Category == UpdateCategory.Bios);

        var report = f.Engine.GetBiosSafetyReport([bios.Key]);
        Assert.False(report.CanProceed);
        Assert.Contains(report.Checks, c => c.Name == "BitLocker" && c.Result == SafetyCheckResult.Fail);
    }

    [Fact]
    public async Task Low_battery_fails_the_bios_safety_report()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        f.Safety.Power = new PowerSnapshot(true, true, 30);
        await Run(f, OperationKind.Scan);
        var bios = f.Engine.GetStatus().Updates.Single(u => u.Category == UpdateCategory.Bios);

        var report = f.Engine.GetBiosSafetyReport([bios.Key]);
        Assert.Contains(report.Checks, c => c.Name == "Battery" && c.Result == SafetyCheckResult.Fail);
    }

    [Fact]
    public async Task Download_failure_marks_updates_failed_and_status_red()
    {
        using var f = Fixture(Sim("DownloadFailure"));
        await Run(f, OperationKind.UpdateAll);

        var status = f.Engine.GetStatus();
        Assert.Contains(status.Updates, u => u.State == UpdateState.Failed && u.StatusMessage.Contains("Download failed"));
        Assert.Equal(OverallStatus.Failed, status.Overall);
        Assert.Contains(f.Store.Query(), h => h.Kind == HistoryKind.Failure);
    }

    [Fact]
    public async Task Install_failure_is_recorded_per_update()
    {
        using var f = Fixture(Sim("InstallFailure"));
        var outcome = await Run(f, OperationKind.UpdateAll);

        Assert.Equal(1, outcome!.Failed);
        Assert.Contains(f.Store.Query(), h => h.Kind == HistoryKind.Failure && h.Result.Contains("1603"));
    }

    [Fact]
    public async Task Network_down_fails_the_scan_cleanly()
    {
        using var f = Fixture(Sim("NetworkDown"));
        var outcome = await Run(f, OperationKind.Scan);

        Assert.False(outcome!.Success);
        var status = f.Engine.GetStatus();
        Assert.Equal(EngineStage.Failed, status.Stage);
        Assert.Contains("No internet", status.LastError);
    }

    [Fact]
    public async Task Only_one_operation_runs_at_a_time()
    {
        using var f = new EngineFixture(Sim("UpdatesAvailable"), new SimulatedAdapter(TimeSpan.FromMilliseconds(100)));
        Assert.True(f.Engine.TryStart(new EngineOperation(OperationKind.Scan, OperationTrigger.User)));
        Assert.False(f.Engine.TryStart(new EngineOperation(OperationKind.UpdateAll, OperationTrigger.Toolbar)));
        Assert.Null(await f.Engine.RunAsync(new EngineOperation(OperationKind.Scan, OperationTrigger.User)));

        f.Engine.Cancel();
        await WaitIdle(f);
    }

    [Fact]
    public async Task Scheduled_runs_respect_auto_install_policy()
    {
        using var f = Fixture(Sim("UpdatesAvailable", p => p with { AutoInstallDrivers = false }));
        var outcome = await Run(f, OperationKind.UpdateAll, automatic: true);

        Assert.Equal(0, outcome!.Installed);
        Assert.All(f.Engine.GetStatus().Updates, u => Assert.NotEqual(UpdateState.Installed, u.State));
    }

    [Fact]
    public async Task Excluded_updates_are_never_installed()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        await Run(f, OperationKind.Scan);
        var audio = f.Engine.GetStatus().Updates.Single(u => u.Name.Contains("Audio"));
        f.Policy.Save(f.Policy.Current with { ExcludedUpdates = [audio.Key] });

        await Run(f, OperationKind.UpdateAll);

        Assert.Equal(UpdateState.Excluded, f.Engine.GetStatus().Updates.Single(u => u.Key == audio.Key).State);
    }

    [Fact]
    public async Task Critical_only_hides_non_critical_updates()
    {
        using var f = Fixture(Sim("UpdatesAvailable", p => p with { CriticalOnly = true }));
        await Run(f, OperationKind.Scan);
        Assert.All(f.Engine.GetStatus().Updates, u => Assert.Equal(UpdatePriority.Critical, u.Priority));
    }

    [Fact]
    public async Task Low_disk_space_blocks_installs()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        f.Safety.FreeBytes = 1024;
        var outcome = await Run(f, OperationKind.UpdateAll);
        Assert.Equal(0, outcome!.Installed);
        Assert.Contains(f.Engine.GetStatus().Updates, u => u.StatusMessage.Contains("free"));
    }

    [Fact]
    public async Task Simulated_restart_clears_flag_without_restarting()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        await Run(f, OperationKind.UpdateAll);
        Assert.True(f.Engine.GetStatus().RestartPending);

        Assert.True(f.Engine.RequestRestart(30));
        Assert.Equal(0, f.Safety.Restarts);
        Assert.False(f.Engine.GetStatus().RestartPending);
    }

    [Fact]
    public async Task Restart_flag_clears_after_the_pc_restarts()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        await Run(f, OperationKind.UpdateAll);
        Assert.True(f.Engine.GetStatus().RestartPending);

        f.Safety.BootTimeUtc = DateTime.UtcNow.AddMinutes(10); // a later boot
        Assert.False(f.Engine.GetStatus().RestartPending);
        Assert.Contains(f.Store.Query(), h => h.Kind == HistoryKind.Restart && h.Title == "Restart completed");
    }

    [Fact]
    public async Task Interrupted_operation_is_recovered_after_service_restart()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        f.Store.Set("operation.current", """{"kind":"UpdateAll","trigger":"User","startedUtc":"2026-10-11T00:00:00Z"}""");
        var engine = f.Create([new SimulatedAdapter(TimeSpan.Zero)]);

        Assert.True(engine.RecoverAfterStartup());
        Assert.Contains(f.Store.Query(), h => h.Title.Contains("interrupted"));
        Assert.Null(f.Store.Get("operation.current"));
        engine.Dispose();
    }

    [Fact]
    public async Task Results_survive_a_service_restart()
    {
        using var f = Fixture(Sim("UpdatesAvailable"));
        await Run(f, OperationKind.Scan);
        var engine = f.Create([new SimulatedAdapter(TimeSpan.Zero)]);
        Assert.Equal(3, engine.GetStatus().Updates.Count);
        engine.Dispose();
    }

    [Fact]
    public async Task All_or_nothing_tool_is_blocked_while_its_bios_update_is_unapproved()
    {
        var adapter = new ScriptedAdapter("allonly", AdapterCapabilities.InstallAllOnly,
            ScriptedAdapter.Update("allonly", "drv", UpdateCategory.Driver),
            ScriptedAdapter.Update("allonly", "bios", UpdateCategory.Bios));
        using var f = new EngineFixture(new MachinePolicy(), adapter);

        await Run(f, OperationKind.UpdateAll);

        Assert.Empty(adapter.InstallCalls);
        Assert.Contains(f.Engine.GetStatus().Updates, u => u.Category == UpdateCategory.Driver && u.State == UpdateState.Skipped);
    }

    [Fact]
    public async Task Update_still_listed_after_installing_is_not_installed_again()
    {
        // HPIA keeps recommending some SoftPaqs after they install (it checks a different component).
        var adapter = new ScriptedAdapter("hp", AdapterCapabilities.InstallIndividual,
            ScriptedAdapter.Update("hp", "sp165987", UpdateCategory.Driver));
        using var f = new EngineFixture(new MachinePolicy(), adapter);

        await Run(f, OperationKind.UpdateAll);
        var second = await Run(f, OperationKind.UpdateAll);

        Assert.Single(adapter.InstallCalls);
        var update = Assert.Single(f.Engine.GetStatus().Updates);
        Assert.Equal(UpdateState.Installed, update.State);
        Assert.Contains("won't be installed again", update.StatusMessage);
        Assert.Equal(OverallStatus.UpToDate, f.Engine.GetStatus().Overall);
        Assert.Equal("Your PC is up to date", second!.Message);
    }

    [Fact]
    public async Task By_category_tool_installs_the_whole_category()
    {
        var adapter = new ScriptedAdapter("bycat", AdapterCapabilities.InstallByCategory,
            ScriptedAdapter.Update("bycat", "a", UpdateCategory.Driver),
            ScriptedAdapter.Update("bycat", "b", UpdateCategory.Driver),
            ScriptedAdapter.Update("bycat", "fw", UpdateCategory.Firmware));
        using var f = new EngineFixture(new MachinePolicy(), adapter);
        await Run(f, OperationKind.Scan);
        var a = f.Engine.GetStatus().Updates.Single(u => u.Name == "a");

        await Run(f, OperationKind.Install, keys: [a.Key]);

        var call = Assert.Single(adapter.InstallCalls);
        Assert.Equal(["a", "b"], call.Order());
    }

    [Fact]
    public async Task Bitlocker_is_suspended_for_approved_bios_when_tool_does_not_handle_it()
    {
        var adapter = new ScriptedAdapter("hp", AdapterCapabilities.InstallIndividual,
            ScriptedAdapter.Update("hp", "bios", UpdateCategory.Bios));
        using var f = new EngineFixture(new MachinePolicy(), adapter);
        f.Safety.BitLocker = new BitLockerState(true, true, true, "on");
        await Run(f, OperationKind.Scan);
        var bios = f.Engine.GetStatus().Updates.Single();

        await Run(f, OperationKind.Install, keys: [bios.Key], bios: [bios.Key]);

        Assert.Equal(1, f.Safety.SuspendCalls);
        Assert.Single(adapter.InstallCalls);
    }

    private static async Task WaitIdle(EngineFixture f)
    {
        for (var i = 0; i < 100 && f.Engine.IsBusy; i++) await Task.Delay(50);
    }
}
