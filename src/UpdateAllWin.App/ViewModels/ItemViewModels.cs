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

public sealed class HistoryItemViewModel(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string Result => Entry.Result;
    public string Time => Entry.TimeUtc.ToLocalTime().ToString("d MMM yyyy  HH:mm");
    public string KindText => Entry.Kind switch
    {
        HistoryKind.Install => "Install",
        HistoryKind.Failure => "Error",
        HistoryKind.Scan => "Scan",
        HistoryKind.Restart => "Restart",
        HistoryKind.Approval => "Approval",
        HistoryKind.Policy => "Settings",
        _ => "Service",
    };
    public string TriggerText => Entry.Trigger switch
    {
        OperationTrigger.Toolbar => "Toolbar",
        OperationTrigger.Schedule => "Scheduled",
        OperationTrigger.CommandLine => "Command line",
        OperationTrigger.Recovery => "Recovery",
        OperationTrigger.User => "You",
        _ => "",
    };
    public string Glyph => Entry.Success ? Entry.Kind switch
    {
        HistoryKind.Scan => "",
        HistoryKind.Restart => "",
        HistoryKind.Approval => "",
        HistoryKind.Policy => "",
        HistoryKind.Service => "",
        _ => "",
    } : "";
    public Brush GlyphBrush => new SolidColorBrush(Entry.Success
        ? Toolbar.StatusVisuals.ColorFor(Entry.Kind == HistoryKind.Install ? OverallStatus.UpToDate : OverallStatus.Busy)
        : Toolbar.StatusVisuals.ColorFor(OverallStatus.Failed));
    public string Versions => Entry.FromVersion is { Length: > 0 } || Entry.ToVersion is { Length: > 0 }
        ? $"{Entry.FromVersion ?? "?"} → {Entry.ToVersion ?? "?"}" : "";
}
