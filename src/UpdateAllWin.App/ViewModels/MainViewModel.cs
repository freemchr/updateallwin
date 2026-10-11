using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.Contracts;
using UpdateAllWin.Toolbar;

namespace UpdateAllWin.App.ViewModels;

/// <summary>State for the main window. Everything comes from UpdateAllService over the named pipe.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private EngineStatus? _status;
    private DeviceInfo? _device;
    private bool _serviceAvailable;

    public MainViewModel(BackgroundAgent agent, DispatcherQueue dispatcher)
    {
        Agent = agent;
        _dispatcher = dispatcher;
        agent.Monitor.Changed += (_, change) => Apply(change);
        _ = LoadDeviceAsync();
        UserPreferences.Changed += (_, _) => OnPropertyChanged(nameof(SerialText));
    }

    public BackgroundAgent Agent { get; }
    public ServiceClient Client => Agent.Monitor.Client;

    public ObservableCollection<UpdateItemViewModel> Updates { get; } = [];

    // ------------------------------------------------------------------ status

    public bool ServiceMissing => !_serviceAvailable;
    public bool IsBusy => _status?.Stage is EngineStage.Scanning or EngineStage.Downloading or EngineStage.Installing;
    public bool IsIdle => !IsBusy && _serviceAvailable;
    public OverallStatus Overall => StatusVisuals.From(_status, _serviceAvailable);
    public bool SimulationMode => _status?.SimulationMode == true;
    public bool RestartPending => _status?.RestartPending == true;
    public int AwaitingApprovalCount => Updates.Count(u => u.Dto.State == UpdateState.AwaitingApproval);
    private int AvailableCount => Updates.Count(u => u.IsInstallable);

    public string Title => !_serviceAvailable ? "UpdateAll Win"
        : IsBusy ? "Updating your PC"
        : RestartPending ? "Your PC has been updated"
        : Overall switch
        {
            OverallStatus.UpToDate => "Your PC is up to date",
            OverallStatus.UpdatesAvailable when AwaitingApprovalCount > 0 => "A BIOS update needs your OK",
            OverallStatus.UpdatesAvailable => $"{AvailableCount} update{(AvailableCount == 1 ? "" : "s")} available",
            OverallStatus.Failed => "Some updates didn't install",
            _ => "Update All",
        };

    public string Subtitle => _device is null ? "Driver, BIOS and firmware updates"
        : _device.Model.StartsWith(ManufacturerName(_device.Manufacturer) ?? _device.Manufacturer, StringComparison.OrdinalIgnoreCase)
        ? _device.Model : $"{_device.Manufacturer} {_device.Model}".Trim();

    private string Brand => _device?.Manufacturer is { Length: > 0 } m && ManufacturerName(m) is { } name ? name + " " : "";

    public string Message => !_serviceAvailable
        ? "The UpdateAll Win background service isn't running.\n\nStart it to check for and install updates (administrator approval required)."
        : IsBusy
        ? $"Finding and installing all {Brand}updates. Please wait…\n\nYou can close this window — updates carry on in the background."
        : RestartPending
        ? "Updates have been installed on your PC.\n\nA restart is required to finish the updates. Please save your work first.\n\n" + RestartDeadlineText
        : Overall switch
        {
            OverallStatus.UpToDate => $"Your drivers, BIOS and firmware are current.\n\nLast checked: {Format.Relative(_status?.LastSuccessfulScanUtc)}.",
            OverallStatus.UpdatesAvailable when AwaitingApprovalCount > 0 =>
                "BIOS updates change your PC's firmware, so they only install after you approve them.\n\nClick Review BIOS update to see the safety checks.",
            OverallStatus.Failed => (_status?.LastError is { Length: > 0 } e ? e : "One or more updates failed.") + "\n\nClick Update All to try again.",
            _ => $"Click Update All to find and install every driver, BIOS and firmware update {(Brand.Length > 0 ? "from " + Brand.Trim() : "for your PC")}.",
        };

    private string RestartDeadlineText => _status?.RestartDeferralEndsUtc is { } ends && CanDeferRestart
        ? $"Please restart by {ends.ToLocalTime():ddd d MMM, h:mm tt}."
        : "Choose Restart Now when you're ready.";

    public Brush AccentBrush => new SolidColorBrush(StatusVisuals.ColorFor(IsBusy ? OverallStatus.Busy : Overall));

    public string ProgressText => _status is null ? "" :
        _status.CurrentItem is { Length: > 0 } item ? $"{item}{(_status.Percent is { } p ? $" — {p}%" : "")}" : _status.Message;
    public double ProgressValue => _status?.Percent ?? 0;
    public bool ProgressIndeterminate => _status?.Percent is null;

    // Which buttons show. Only one set at a time.
    public bool ShowStartService => !_serviceAvailable;
    public bool ShowCancel => IsBusy;
    public bool ShowRestart => _serviceAvailable && !IsBusy && RestartPending;
    public bool ShowReview => IsIdle && !RestartPending && AwaitingApprovalCount > 0;
    public bool ShowUpdateAll => IsIdle && !RestartPending;
    public bool CanDeferRestart => Agent.CanDeferRestart;
    public bool ShowRestartLater => ShowRestart && CanDeferRestart;
    public int RestartNowSpan => ShowRestartLater ? 1 : 2;
    public int UpdateAllColumn => ShowReview ? 1 : 0;
    public int UpdateAllSpan => ShowReview ? 1 : 2;

    // ------------------------------------------------------------------ device

    public bool HasDevice => _device is not null;
    public string DeviceManufacturer => _device?.Manufacturer ?? "";
    public string OsText => _device is null ? "" : $"{_device.OsName} {_device.OsDisplayVersion} (build {_device.OsBuild})".Trim();
    public string CpuText => _device?.Cpu ?? "";
    public string MemoryText => _device is null ? "" : string.Join(" · ", new[]
    {
        $"{Format.Capacity(_device.RamBytes)} RAM",
        _device.SystemDiskBytes > 0 ? $"{Format.Capacity(_device.SystemDiskBytes)} drive" : "",
    }.Where(s => s.Length > 0));
    public string BiosText => _device is null ? "" : $"{_device.BiosVersion}{(_device.BiosDate is { } d ? $" ({d:d MMM yyyy})" : "")}";
    public string SerialText => _device is null ? "" :
        UserPreferences.Current.ShowSerialNumber ? _device.SerialNumber : Mask(_device.SerialNumber);

    private static string Mask(string s) => s.Length <= 4 ? new string('•', s.Length) : new string('•', s.Length - 4) + s[^4..];

    private static string? ManufacturerName(string m) => Controls.ManufacturerLogo.BrandKey(m) switch
    {
        "hp" => "HP",
        "dell" => "Dell",
        "lenovo" => "Lenovo",
        "asus" => "ASUS",
        "acer" => "Acer",
        "msi" => "MSI",
        "microsoft" => "Microsoft",
        _ => null,
    };

    // ------------------------------------------------------------------ actions

    public async Task<string?> UpdateAllAsync() => await SendAsync(IpcCommand.StartUpdateAll, OperationTrigger.User);

    public async Task<string?> CancelAsync() => await SendAsync(IpcCommand.Cancel, null);

    public async Task<string?> InstallAsync(IReadOnlyList<string> keys, IReadOnlyList<string> approvedBios) =>
        await SendAsync(IpcCommand.InstallSelected, new InstallRequest(keys, approvedBios));

    public Task<BiosSafetyReport?> GetBiosSafetyReportAsync(IReadOnlyList<string> keys) =>
        Client.GetAsync<BiosSafetyReport>(IpcCommand.GetBiosSafetyReport, keys);

    public bool BiosApprovalRequired => Agent.Monitor.Policy.BiosApprovalRequired;

    public async Task RestartNowAsync() => await Agent.RestartNowAsync();

    public async Task RestartLaterAsync()
    {
        await Agent.RestartLaterAsync();
        OnPropertyChanged(nameof(Message));
    }

    /// <summary>Per-update install results (successes and failures), newest first.</summary>
    public async Task<List<HistoryEntry>> GetInstalledAsync()
    {
        try
        {
            var entries = await Client.GetAsync<List<HistoryEntry>>(IpcCommand.GetHistory, new HistoryQuery(1000)) ?? [];
            return entries.Where(e => e.UpdateKey is { Length: > 0 } && e.Kind is HistoryKind.Install or HistoryKind.Failure).ToList();
        }
        catch (ServiceUnavailableException) { return []; }
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
        if (_serviceAvailable && !wasAvailable && _device is null) _ = LoadDeviceAsync();

        var dtos = _status?.Updates ?? [];
        var existing = Updates.ToDictionary(u => u.Key);
        var keep = new HashSet<string>(dtos.Select(d => d.Key));
        foreach (var gone in Updates.Where(u => !keep.Contains(u.Key)).ToList()) Updates.Remove(gone);
        for (var i = 0; i < dtos.Count; i++)
        {
            if (existing.TryGetValue(dtos[i].Key, out var vm)) vm.Update(dtos[i]);
            else Updates.Insert(Math.Min(i, Updates.Count), new UpdateItemViewModel(dtos[i], selected: true));
        }
        OnPropertyChanged(string.Empty);
    }
}
