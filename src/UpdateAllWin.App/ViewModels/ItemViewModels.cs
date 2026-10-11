using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.App.ViewModels;

public sealed class UpdateItemViewModel : ObservableObject
{
    private UpdateDto _dto;
    private bool _isSelected;

    public UpdateItemViewModel(UpdateDto dto, bool selected)
    {
        _dto = dto;
        _isSelected = selected && dto.State is UpdateState.Available or UpdateState.AwaitingApproval or UpdateState.Failed or UpdateState.Skipped;
    }

    public event EventHandler? SelectionChanged;

    public UpdateDto Dto => _dto;
    public string Key => _dto.Key;
    public string Name => _dto.Name;
    public string Source => _dto.Source;
    public string Manufacturer => string.IsNullOrEmpty(_dto.Manufacturer) ? _dto.Source : _dto.Manufacturer;
    public UpdateCategory Category => _dto.Category;
    public string CategoryText => Format.CategoryText(_dto.Category);
    public string Glyph => Format.CategoryGlyph(_dto.Category, _dto.Name);
    public string CurrentVersion => string.IsNullOrEmpty(_dto.CurrentVersion) ? "—" : _dto.CurrentVersion;
    public string AvailableVersion => string.IsNullOrEmpty(_dto.AvailableVersion) ? "—" : _dto.AvailableVersion;
    public string SizeText => Format.Size(_dto.SizeBytes);
    public string PriorityText => Format.PriorityText(_dto.Priority);
    public string RestartText => Format.RestartText(_dto.Restart);
    public string StateText => Format.StateText(_dto.State);
    public Brush StateBrush => Format.StateBrush(_dto.State);
    public string StatusMessage => string.IsNullOrEmpty(_dto.StatusMessage) ? _dto.GroupingNote : _dto.StatusMessage;
    public bool IsInstallable => _dto.State is UpdateState.Available or UpdateState.AwaitingApproval or UpdateState.Failed or UpdateState.Skipped;
    public bool IsBios => _dto.Category == UpdateCategory.Bios;
    public bool IsExcluded => _dto.State == UpdateState.Excluded;
    public bool HasReleaseNotes => !string.IsNullOrEmpty(_dto.ReleaseNotesUrl);

    // ------------------------------------------------------------------ main window list row

    /// <summary>Downloading and installing show a spinner instead of an icon.</summary>
    public bool IsWorking => _dto.State is UpdateState.Downloading or UpdateState.Installing;
    public bool IsDownloading => _dto.State == UpdateState.Downloading;
    public double DownloadPercent => _dto.Percent ?? 0;
    public bool DownloadPercentUnknown => _dto.Percent is null;

    public string RowStatus => _dto.State switch
    {
        UpdateState.Available => "Ready",
        UpdateState.AwaitingApproval => "Needs your OK",
        UpdateState.Queued => "Waiting",
        UpdateState.Downloading => _dto.Percent is { } p ? $"Downloading {p}%" : "Downloading…",
        UpdateState.Downloaded => "Downloaded",
        UpdateState.Installing => "Installing…",
        UpdateState.Installed => "Installed",
        UpdateState.InstalledRestartRequired => "Installed · restart needed",
        UpdateState.Failed => "Failed",
        UpdateState.Skipped => "Skipped",
        _ => "",
    };

    public string RowGlyph => _dto.State switch
    {
        UpdateState.Available => "\uE896",                                                  // download arrow
        UpdateState.AwaitingApproval or UpdateState.Skipped => "\uE7BA",                    // warning
        UpdateState.Queued => "\uE823",                                                     // clock
        UpdateState.Downloaded or UpdateState.Installed or UpdateState.InstalledRestartRequired => "\uE73E", // tick
        UpdateState.Failed => "\uE711",                                                     // cross
        _ => "",
    };

    /// <summary>Blue tick once downloaded, green once installed.</summary>
    public Brush RowBrush => new SolidColorBrush(Toolbar.StatusVisuals.ColorFor(_dto.State switch
    {
        UpdateState.Downloaded => OverallStatus.Busy,
        UpdateState.Installed or UpdateState.InstalledRestartRequired => OverallStatus.UpToDate,
        UpdateState.Failed => OverallStatus.Failed,
        UpdateState.AwaitingApproval or UpdateState.Skipped => OverallStatus.UpdatesAvailable,
        _ => OverallStatus.Unknown,
    }));

    public string RowToolTip => string.IsNullOrEmpty(_dto.StatusMessage) ? $"{CurrentVersion} → {AvailableVersion}" : _dto.StatusMessage;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value && IsInstallable)) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Update(UpdateDto dto)
    {
        _dto = dto;
        if (!IsInstallable) _isSelected = false;
        OnPropertyChanged(string.Empty); // refresh all bindings
    }
}
