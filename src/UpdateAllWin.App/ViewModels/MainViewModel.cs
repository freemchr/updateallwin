using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.Contracts;
using UpdateAllWin.Toolbar;
using Windows.UI;

namespace UpdateAllWin.App.ViewModels;

/// <summary>Shared state for all pages. Everything comes from UpdateAllService over the named pipe.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private EngineStatus? _status;
    private DeviceInfo? _device;
    private bool _serviceAvailable;
    private string _filter = "all";

    public MainViewModel(BackgroundAgent agent, DispatcherQueue dispatcher)
    {
        Agent = agent;
        _dispatcher = dispatcher;
        Policy = new PolicyEditor(new MachinePolicy());
        agent.Monitor.Changed += (_, change) => Apply(change);
        _ = LoadDeviceAsync();
        _ = LoadPolicyAsync();
        UserPreferences.Changed += (_, _) => OnPropertyChanged(nameof(SerialText));
    }

    public BackgroundAgent Agent { get; }
    public ServiceClient Client => Agent.Monitor.Client;
    public PolicyEditor Policy { get; private set; }

    public ObservableCollection<UpdateItemViewModel> Updates { get; } = [];
    public ObservableCollection<UpdateItemViewModel> FilteredUpdates { get; } = [];
    public ObservableCollection<UpdateItemViewModel> ExcludedUpdates { get; } = [];
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    // ------------------------------------------------------------------ status / hero

    public bool ServiceAvailable => _serviceAvailable;
    public bool ServiceMissing => !_serviceAvailable;
    public bool IsBusy => _status?.Stage is EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing;
    public bool IsIdle => !IsBusy && _serviceAvailable;
    public OverallStatus Overall => StatusVisuals.From(_status, _serviceAvailable);
    public bool SimulationMode => _status?.SimulationMode == true;

    public string HeroTitle => !_serviceAvailable ? "Service not running"
        : IsBusy ? _status!.Stage switch
        {
            EngineStage.Scanning => "Checking for updates…",
            EngineStage.Downloading => "Downloading updates…",
            _ => "Installing updates…",
        }
        : Overall switch
        {
            OverallStatus.UpToDate => "Your PC is up to date",
            OverallStatus.UpdatesAvailable when AwaitingApprovalCount > 0 && AvailableCount == AwaitingApprovalCount => "BIOS update needs approval",
            OverallStatus.UpdatesAvailable => $"{AvailableCount} update{(AvailableCount == 1 ? "" : "s")} available",
            OverallStatus.RestartRequired => "Restart required",
            OverallStatus.Failed => "Some updates need attention",
            _ => "Let's check your PC",
        };

    public string HeroSubtitle => !_serviceAvailable ? "The UpdateAll Win background service isn't running. Start it to check for updates."
        : IsBusy ? _status!.Message
        : Overall switch
        {
            OverallStatus.UpToDate => "We'll keep your drivers, BIOS and firmware up to date.",
            OverallStatus.UpdatesAvailable when AwaitingApprovalCount > 0 => "Review the BIOS update, then install. Drivers can be updated straight away.",
            OverallStatus.UpdatesAvailable => "Click Update All to install them, or choose updates below.",
            OverallStatus.RestartRequired => "Updates were installed. Restart your PC to finish.",
            OverallStatus.Failed => _status?.LastError ?? "Open History for details.",
            _ => "Check for driver, BIOS and firmware updates from your PC's manufacturer.",
        };

    public string HeroGlyph => Overall switch
    {
        OverallStatus.UpToDate => "",
        OverallStatus.UpdatesAvailable => "",
        OverallStatus.RestartRequired => "",
        OverallStatus.Failed => "",
        _ => "",
    };

    public Brush HeroBrush => new SolidColorBrush(StatusVisuals.ColorFor(Overall));
    public Brush HeroBackgroundBrush
    {
        get
        {
            var c = StatusVisuals.ColorFor(Overall);
            return new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B));
        }
    }

    public string ProgressText => _status is null ? "" :
        _status.CurrentItem is { Length: > 0 } item ? $"{item}{(_status.Percent is { } p ? $" — {p}%" : "")}" : _status.Message;
    public double ProgressValue => _status?.Percent ?? 0;
    public bool ProgressIndeterminate => _status?.Percent is null;

    // ------------------------------------------------------------------ counts

    private IEnumerable<UpdateItemViewModel> Pending => Updates.Where(u => u.IsInstallable);
    public int AvailableCount => Pending.Count();
    public int AwaitingApprovalCount => Updates.Count(u => u.Dto.State == UpdateState.AwaitingApproval);
    public int DriverCount => Pending.Count(u => u.Category is UpdateCategory.Driver or UpdateCategory.Software or UpdateCategory.Other);
    public int BiosCount => Pending.Count(u => u.Category == UpdateCategory.Bios);
    public int FirmwareCount => Pending.Count(u => u.Category == UpdateCategory.Firmware);
    public string DriverCaption => Caption(DriverCount);
    public string BiosCaption => Caption(BiosCount);
    public string FirmwareCaption => Caption(FirmwareCount);
    public string AvailableHeader => $"Available Updates ({AvailableCount})";
    public string AvailableSummary => AvailableCount == 0 ? "No updates available" : $"{AvailableCount} update{(AvailableCount == 1 ? "" : "s")} available";
    public string AvailableSummaryDetail => AvailableCount == 0 ? "Your drivers, BIOS and firmware are current." : "Drivers, BIOS and firmware found for your device.";
    public int SelectedCount => Updates.Count(u => u.IsSelected);
    public string InstallSelectedText => $"Install Selected ({SelectedCount})";
    public bool CanInstallSelected => IsIdle && SelectedCount > 0;
    public string LastScanText => $"Last scan: {Format.Relative(_status?.LastSuccessfulScanUtc)}";
    public string NextScanText => _status?.NextScheduledScanUtc is { } n ? $"Next scan: {n.ToLocalTime():ddd d MMM, h:mm tt}" : "Automatic scans are off";
    public string SourcesText => _status?.Adapters is { Count: > 0 } a ? string.Join(" · ", a.Select(x => x.DisplayName)) : "";

    private static string Caption(int n) => n == 0 ? "up to date" : n == 1 ? "update available" : "updates available";

    // ------------------------------------------------------------------ restart

    public bool RestartPending => _status?.RestartPending == true;
    public string RestartPendingText => RestartPending ? "Yes" : "No";
    public bool CanDeferRestart => Agent.CanDeferRestart;
    public string RestartBannerText => _status?.RestartDeferralEndsUtc is { } ends && CanDeferRestart
        ? $"Your PC will need to restart to complete the updates. Please restart by {ends.ToLocalTime():ddd d MMM, h:mm tt}."
        : "Your PC will need to restart after installation to complete the updates.";

    // ------------------------------------------------------------------ device

    public string DeviceModel => _device?.Model is { Length: > 0 } m ? m : "Your PC";
    public string DeviceManufacturer => _device?.Manufacturer ?? "";
    public string OsText => _device is null ? "" : $"{_device.OsName} {_device.OsDisplayVersion}".Trim();
    public string BuildText => _device is null ? "" : $"Build {_device.OsBuild}";
    public string CpuText => _device?.Cpu ?? "";
    public string RamText => _device is null ? "" : $"{Format.Capacity(_device.RamBytes)} RAM";
    public string StorageText => _device is null || _device.SystemDiskBytes <= 0 ? "" : $"{Format.Capacity(_device.SystemDiskBytes)} system drive";
    public string BiosText => _device is null ? "" : $"BIOS {_device.BiosVersion}{(_device.BiosDate is { } d ? $" ({d:d MMM yyyy})" : "")}";
    public string SerialText => _device is null ? "" :
        "Serial " + (UserPreferences.Current.ShowSerialNumber ? _device.SerialNumber : Mask(_device.SerialNumber));
    public bool IsDesktop => _device?.Chassis is ChassisKind.Desktop or ChassisKind.AllInOne or ChassisKind.Server;

    private static string Mask(string s) => s.Length <= 4 ? new string('•', s.Length) : new string('•', s.Length - 4) + s[^4..];

    // ------------------------------------------------------------------ actions

    public async Task<string?> CheckForUpdatesAsync() => await SendAsync(IpcCommand.StartScan, OperationTrigger.User);

    public async Task<string?> UpdateAllAsync() => await SendAsync(IpcCommand.StartUpdateAll, OperationTrigger.User);

    public async Task<string?> CancelAsync() => await SendAsync(IpcCommand.Cancel, null);

    public async Task<string?> InstallAsync(IReadOnlyList<string> keys, IReadOnlyList<string> approvedBios) =>
        await SendAsync(IpcCommand.InstallSelected, new InstallRequest(keys, approvedBios));

    public Task<BiosSafetyReport?> GetBiosSafetyReportAsync(IReadOnlyList<string> keys) =>
        Client.GetAsync<BiosSafetyReport>(IpcCommand.GetBiosSafetyReport, keys);

    public async Task RestartNowAsync() => await Agent.RestartNowAsync();

    public async Task RestartLaterAsync()
    {
        await Agent.RestartLaterAsync();
        OnPropertyChanged(nameof(RestartBannerText));
    }

    public async Task<string?> SetExcludedAsync(string key, bool excluded)
    {
        var policy = Policy.ToPolicy();
        var list = policy.ExcludedUpdates.ToList();
        if (excluded) { if (!list.Contains(key)) list.Add(key); }
        else list.Remove(key);
        return await SavePolicyAsync(policy with { ExcludedUpdates = list });
    }

    public async Task<string?> SavePolicyAsync(MachinePolicy? policy = null)
    {
        var error = await ElevatedHelper.SavePolicyAsync(Client, policy ?? Policy.ToPolicy());
        await LoadPolicyAsync();
        Agent.Monitor.InvalidatePolicy();
        return error;
    }

    public async Task LoadPolicyAsync()
    {
        try
        {
            var policy = await Client.GetAsync<MachinePolicy>(IpcCommand.GetPolicy);
            if (policy is null) return;
            Policy = new PolicyEditor(policy);
            OnPropertyChanged(nameof(Policy));
        }
        catch { }
    }

    public async Task LoadHistoryAsync(HistoryKind? kind = null)
    {
        try
        {
            var entries = await Client.GetAsync<List<HistoryEntry>>(IpcCommand.GetHistory, new HistoryQuery(1000, kind)) ?? [];
            History.Clear();
            foreach (var e in entries) History.Add(new HistoryItemViewModel(e));
        }
        catch (ServiceUnavailableException) { }
    }

    public async Task<string> ExportDiagnosticsAsync() => await Client.GetAsync<string>(IpcCommand.ExportDiagnostics) ?? "";

    public void SelectAll(bool selected)
    {
        foreach (var u in FilteredUpdates.Where(u => u.IsInstallable)) u.IsSelected = selected;
    }

    public string CurrentFilter => _filter;

    public void SetFilter(string filter)
    {
        _filter = filter;
        RebuildFiltered();
    }

    private async Task<string?> SendAsync(IpcCommand command, object? payload)
    {
        try
        {
            var response = await Client.SendAsync(command, payload);
            Agent.Monitor.Refresh();
            return response.Ok ? null : response.Error;
        }
        catch (ServiceUnavailableException ex) { return ex.Message; }
        catch (ServiceAccessDeniedException ex) { return ex.Message; }
    }

    private async Task LoadDeviceAsync()
    {
        for (var attempt = 0; attempt < 30 && _device is null; attempt++)
        {
            try
            {
                _device = await Client.GetAsync<DeviceInfo>(IpcCommand.GetDevice);
                _dispatcher.TryEnqueue(() => OnPropertyChanged(string.Empty));
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    // ------------------------------------------------------------------ status sync

    private void Apply(StatusChange change)
    {
        var wasAvailable = _serviceAvailable;
        _status = change.Current;
        _serviceAvailable = change.ServiceAvailable;
        if (_serviceAvailable && !wasAvailable)
        {
            _ = LoadPolicyAsync();
            if (_device is null) _ = LoadDeviceAsync();
        }

        var dtos = _status?.Updates ?? [];
        var existing = Updates.ToDictionary(u => u.Key);
        var keep = new HashSet<string>(dtos.Select(d => d.Key));
        foreach (var gone in Updates.Where(u => !keep.Contains(u.Key)).ToList()) Updates.Remove(gone);
        for (var i = 0; i < dtos.Count; i++)
        {
            if (existing.TryGetValue(dtos[i].Key, out var vm)) vm.Update(dtos[i]);
            else
            {
                vm = new UpdateItemViewModel(dtos[i], selected: true);
                vm.SelectionChanged += (_, _) => OnSelectionChanged();
                Updates.Insert(Math.Min(i, Updates.Count), vm);
            }
        }
        RebuildFiltered();
        OnPropertyChanged(string.Empty);
    }

    private void RebuildFiltered()
    {
        var visible = Updates.Where(u => !u.IsExcluded && (_filter switch
        {
            "drivers" => u.Category is UpdateCategory.Driver or UpdateCategory.Software or UpdateCategory.Other,
            "bios" => u.Category == UpdateCategory.Bios,
            "firmware" => u.Category == UpdateCategory.Firmware,
            _ => true,
        })).ToList();
        Sync(FilteredUpdates, visible);
        Sync(ExcludedUpdates, Updates.Where(u => u.IsExcluded).ToList());
    }

    private static void Sync(ObservableCollection<UpdateItemViewModel> target, List<UpdateItemViewModel> source)
    {
        if (target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var s in source) target.Add(s);
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstallSelectedText));
        OnPropertyChanged(nameof(CanInstallSelected));
    }
}
