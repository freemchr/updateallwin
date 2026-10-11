using System.IO.Pipes;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Diagnostics;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.Core.Scheduling;
using UpdateAllWin.ManufacturerAdapters.Dell;
using UpdateAllWin.ManufacturerAdapters.HP;
using UpdateAllWin.ManufacturerAdapters.Simulation;
using UpdateAllWin.Service;

namespace UpdateAllWin.Tests;

public class ScheduleTests
{
    [Fact]
    public void Daily_scan_runs_next_day_at_configured_hour()
    {
        var p = new MachinePolicy { ScanFrequency = ScanFrequency.Daily, ScanHour = 12 };
        Assert.Equal(new DateTime(2026, 10, 12, 12, 0, 0), Schedule.NextScan(p, new DateTime(2026, 10, 11, 13, 0, 0), DateTime.Now));
        Assert.Equal(new DateTime(2026, 10, 11, 12, 0, 0), Schedule.NextScan(p, new DateTime(2026, 10, 11, 9, 0, 0), DateTime.Now));
    }

    [Fact]
    public void Weekly_scan_runs_on_configured_day()
    {
        var p = new MachinePolicy { ScanFrequency = ScanFrequency.Weekly, ScanDay = DayOfWeek.Wednesday, ScanHour = 9 };
        // 2026-10-11 is a Sunday
        Assert.Equal(new DateTime(2026, 10, 14, 9, 0, 0), Schedule.NextScan(p, new DateTime(2026, 10, 11, 10, 0, 0), DateTime.Now));
    }

    [Fact]
    public void Manual_never_schedules_and_first_run_is_immediate()
    {
        Assert.Null(Schedule.NextScan(new MachinePolicy { ScanFrequency = ScanFrequency.Manual }, null, DateTime.Now));
        var now = new DateTime(2026, 10, 11, 8, 0, 0);
        Assert.Equal(now, Schedule.NextScan(new MachinePolicy(), null, now));
    }

    [Theory]
    [InlineData(22, 7, 23, true)]
    [InlineData(22, 7, 3, true)]
    [InlineData(22, 7, 12, false)]
    [InlineData(9, 17, 12, true)]
    [InlineData(9, 17, 18, false)]
    public void Quiet_hours_wrap_midnight(int start, int end, int hour, bool expected)
    {
        var p = new MachinePolicy { QuietHoursEnabled = true, QuietHoursStart = start, QuietHoursEnd = end };
        Assert.Equal(expected, Schedule.IsInQuietHours(p, new DateTime(2026, 10, 11, hour, 30, 0)));
    }
}

public class PolicyTests
{
    [Fact]
    public void Validation_clamps_unsafe_values()
    {
        var p = JsonPolicyStore.Validate(new MachinePolicy
        {
            BiosMinimumBatteryPercent = 1,
            ScanHour = 99,
            ExcludedUpdates = [" HP:SP1:1.0 ", "hp:sp1:1.0", ""],
        });
        Assert.Equal(20, p.BiosMinimumBatteryPercent);
        Assert.Equal(23, p.ScanHour);
        Assert.Equal(["hp:sp1:1.0"], p.ExcludedUpdates);
    }

    [Fact]
    public void Defaults_require_bios_approval()
    {
        Assert.True(new MachinePolicy().BiosApprovalRequired);
        Assert.False(new MachinePolicy().AutoInstallDrivers);
    }
}

public class DetectionTests
{
    [Theory]
    [InlineData("HP", Oem.HP)]
    [InlineData("Hewlett-Packard", Oem.HP)]
    [InlineData("Dell Inc.", Oem.Dell)]
    [InlineData("LENOVO", Oem.Lenovo)]
    [InlineData("Microsoft Corporation", Oem.Microsoft)]
    [InlineData("ASUSTeK COMPUTER INC.", Oem.Asus)]
    [InlineData("Micro-Star International Co., Ltd.", Oem.Msi)]
    [InlineData("", Oem.Unknown)]
    [InlineData("System manufacturer", Oem.Other)]
    public void Manufacturer_detection(string manufacturer, Oem expected) =>
        Assert.Equal(expected, OemDetector.Classify(manufacturer));
}

public class ParserTests
{
    [Fact]
    public void Hpia_xml_report_is_parsed()
    {
        var xml = XDocument.Parse("""
            <HPIA>
              <Recommendations>
                <BIOS>
                  <Recommendation>
                    <TargetComponent>BIOS</TargetComponent>
                    <TargetVersion>01.10.00</TargetVersion>
                    <ReferenceVersion>01.12.00</ReferenceVersion>
                    <Comments>Critical update</Comments>
                    <Solution><Softpaq><Id>sp151063</Id><Name>HP System BIOS</Name><ReleaseNotesUrl>ftp.hp.com/sp151063.html</ReleaseNotesUrl></Softpaq></Solution>
                  </Recommendation>
                </BIOS>
                <Drivers>
                  <Recommendation>
                    <TargetComponent>Graphics</TargetComponent>
                    <TargetVersion>31.0.101.4953</TargetVersion>
                    <ReferenceVersion>31.0.101.5534</ReferenceVersion>
                    <Solution><Softpaq><Id>SP150000</Id><Name>Intel Graphics Driver</Name></Softpaq></Solution>
                  </Recommendation>
                </Drivers>
              </Recommendations>
            </HPIA>
            """);
        var recs = HpiaReportParser.ParseXml(xml);
        Assert.Equal(2, recs.Count);
        Assert.Equal(UpdateCategory.Bios, recs[0].Category);
        Assert.Equal("sp151063", recs[0].SoftPaqId);
        Assert.Equal("01.12.00", recs[0].AvailableVersion);
        Assert.Equal("https://ftp.hp.com/sp151063.html", recs[0].ReleaseNotesUrl);
        Assert.Equal(UpdateCategory.Driver, recs[1].Category);
        Assert.Equal("sp150000", recs[1].SoftPaqId);
    }

    [Fact]
    public void Hpia_json_remediation_is_parsed()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "report.json"), """
            {"HPIA":{"Recommendations":[{"Name":"BIOS","SoftPaqID":"sp151063","Remediation":{"Status":"Success","ReturnCode":3010}}]}}
            """);
        var rem = HpiaReportParser.ParseRemediation(dir);
        Assert.Equal(3010, rem["sp151063"].ReturnCode);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("sp151063", "151063")]
    [InlineData("SP151063.exe", "151063")]
    [InlineData("151063", "151063")]
    public void SpList_uses_bare_numbers(string id, string expected) => Assert.Equal(expected, HpiaReportParser.SpListNumber(id));

    [Fact]
    public void Dell_report_is_parsed()
    {
        var doc = XDocument.Parse("""
            <updates>
              <update><release>ABC12</release><name>Dell System BIOS</name><version>1.20.0</version><urgency>Urgent</urgency><type>BIOS</type><category>BIOS</category></update>
              <update><release>XYZ34</release><name>Intel Wi-Fi Driver</name><version>23.1</version><urgency>Recommended</urgency><type>Driver</type></update>
            </updates>
            """);
        var updates = new DellCommandUpdateAdapter().ParseReport(doc).ToList();
        Assert.Equal(2, updates.Count);
        Assert.Equal(UpdateCategory.Bios, updates[0].Category);
        Assert.Equal(UpdatePriority.Critical, updates[0].Priority);
        Assert.Equal(UpdatePriority.Recommended, updates[1].Priority);
    }

    [Theory]
    [InlineData(5, "pending")]
    [InlineData(1, "restart is required")]
    [InlineData(105, "Invalid command line")]
    public void Dell_exit_codes_are_described(int code, string text) =>
        Assert.Contains(text, DellCommandUpdateAdapter.Describe(code), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void History_csv_neutralises_formulas()
    {
        var csv = HistoryCsv.Build([new HistoryEntry { Title = "=HYPERLINK(\"x\")", Result = "ok", Kind = HistoryKind.Install }]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
    }

    [Fact]
    public void Serial_is_masked() => Assert.Equal("••••••1234", DiagnosticsExporter.Mask("ABCDEF1234"));

    [Fact]
    public void Vendor_report_serial_is_masked() =>
        Assert.Equal("<SerialNumber>••••••1234</SerialNumber>",
            DiagnosticsExporter.Redact("<SerialNumber>abcdef1234</SerialNumber>", "ABCDEF1234"));
}

public class IpcTests
{
    [Fact]
    public async Task Messages_round_trip_over_a_pipe()
    {
        var name = "uaw-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var req = await IpcProtocol.ReadAsync<IpcRequest>(server, CancellationToken.None);
            var install = IpcProtocol.FromElement<InstallRequest>(req!.Payload);
            await IpcProtocol.WriteAsync(server, new IpcResponse(true, Payload: IpcProtocol.ToElement(install!.Keys.Count)), CancellationToken.None);
        });

        var client = new ServiceClient(name);
        var count = await client.GetAsync<int>(IpcCommand.InstallSelected, new InstallRequest(["a", "b"], []));
        await serverTask;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Missing_service_is_reported_as_unavailable()
    {
        var client = new ServiceClient("uaw-nope-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.GetStatusAsync());
    }

    [Fact]
    public void Standard_users_cannot_change_policy()
    {
        using var f = new EngineFixture(new MachinePolicy(), new SimulatedAdapter(TimeSpan.Zero));
        var server = Server(f);
        var response = server.Dispatch(new IpcRequest(IpcCommand.SetPolicy, IpcProtocol.ToElement(new MachinePolicy { BiosApprovalRequired = false })), isAdmin: false, "user");
        Assert.True(response.AccessDenied);
        Assert.True(f.Policy.Current.BiosApprovalRequired);
    }

    [Fact]
    public void Admins_cannot_point_hpia_at_a_user_writable_path()
    {
        using var f = new EngineFixture(new MachinePolicy(), new SimulatedAdapter(TimeSpan.Zero));
        var exe = Path.Combine(f.Root, "HPImageAssistant.exe");
        File.WriteAllText(exe, "");
        var response = Server(f).Dispatch(new IpcRequest(IpcCommand.SetPolicy, IpcProtocol.ToElement(new MachinePolicy { HpiaPath = exe })), isAdmin: true, "admin");
        Assert.False(response.Ok);
        Assert.Null(f.Policy.Current.HpiaPath);
    }

    [Fact]
    public void Standard_users_can_update_unless_policy_forbids_it()
    {
        using var f = new EngineFixture(new MachinePolicy { AllowStandardUsersToInstall = false, SimulationMode = true }, new SimulatedAdapter(TimeSpan.Zero));
        var response = Server(f).Dispatch(new IpcRequest(IpcCommand.StartUpdateAll), isAdmin: false, "user");
        Assert.True(response.AccessDenied);
    }

    [Fact]
    public void Restart_is_refused_when_none_is_pending()
    {
        using var f = new EngineFixture(new MachinePolicy(), new SimulatedAdapter(TimeSpan.Zero));
        var response = Server(f).Dispatch(new IpcRequest(IpcCommand.RestartComputer, IpcProtocol.ToElement(0)), isAdmin: false, "user");
        Assert.False(response.Ok);
        Assert.Equal(0, f.Safety.Restarts);
    }

    private static PipeServer Server(EngineFixture f) => new(
        f.Engine, f.Policy, f.Store, new FakeDevice(),
        new DiagnosticsExporter(new DataPaths(f.Root), f.Store, f.Policy, new FakeDevice()),
        new DataPaths(f.Root), NullLogger<PipeServer>.Instance);
}
