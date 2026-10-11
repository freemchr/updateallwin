using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UpdateAllWin.App.ViewModels;
using UpdateAllWin.Contracts;
using UpdateAllWin.Toolbar;

namespace UpdateAllWin.App.Dialogs;

public static class AppDialogs
{
    /// <summary>Dialogs don't inherit the window's chosen theme on their own.</summary>
    private static ContentDialog Themed(this ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        if (root.Content is FrameworkElement element) dialog.RequestedTheme = element.ActualTheme;
        return dialog;
    }

    /// <summary>
    /// Explicit BIOS approval with the pre-flight safety results. Returns true only if the user
    /// confirms and every required check passed.
    /// </summary>
    public static async Task<bool> ApproveBiosAsync(XamlRoot root, IReadOnlyList<UpdateItemViewModel> bios, BiosSafetyReport report)
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = "BIOS updates change your PC's firmware. They're installed by your manufacturer's tool and applied during a restart.",
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var u in bios)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{u.Name}\n{u.CurrentVersion}  →  {u.AvailableVersion}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var checks = new StackPanel { Spacing = 8 };
        foreach (var check in report.Checks)
        {
            var (glyph, result) = check.Result switch
            {
                SafetyCheckResult.Pass => ("", OverallStatus.UpToDate),
                SafetyCheckResult.Warning => ("", OverallStatus.RestartRequired),
                _ => ("", OverallStatus.Failed),
            };
            var row = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(StatusVisuals.ColorFor(result)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = check.Name + ": ", FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = check.Detail });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            checks.Children.Add(row);
        }
        var checksBorder = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Padding=\"14\" CornerRadius=\"8\" " +
            "Background=\"{ThemeResource CardBackgroundFillColorSecondaryBrush}\" />");
        checksBorder.Child = checks;
        panel.Children.Add(checksBorder);

        var confirm = new CheckBox
        {
            Content = "I've saved my work. I understand my PC will restart and must stay plugged in and switched on during the update.",
            IsEnabled = report.CanProceed,
        };
        if (report.CanProceed) panel.Children.Add(confirm);
        else panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Title = "The BIOS update can't be installed yet",
            Message = "Fix the items marked with ✕, then try again.",
        });

        var dialog = new ContentDialog
        {
            Title = "Approve BIOS update",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Approve and install",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        }.Themed(root);
        confirm.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        confirm.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await dialog.ShowAsync() == ContentDialogResult.Primary && report.CanProceed && confirm.IsChecked == true;
    }

    public static async Task ShowErrorAsync(XamlRoot root, string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        }.Themed(root).ShowAsync();
    }

    public static async Task ShowHistoryDetailsAsync(XamlRoot root, HistoryItemViewModel item)
    {
        var e = item.Entry;
        var lines = new List<string>
        {
            $"Result: {e.Result}",
            $"When: {item.Time}",
        };
        if (item.TriggerText.Length > 0) lines.Add($"Started by: {item.TriggerText}");
        if (e.Category is { } c) lines.Add($"Category: {Format.CategoryText(c)}");
        if (item.Versions.Length > 0) lines.Add($"Version: {item.Versions}");
        if (e.AdapterId is { Length: > 0 }) lines.Add($"Source: {e.AdapterId}");
        if (e.ExitCode is { } code) lines.Add($"Exit code: {code}");
        if (e.Details is { Length: > 0 }) lines.Add($"\n{e.Details}");

        await new ContentDialog
        {
            Title = e.Title,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "Close",
        }.Themed(root).ShowAsync();
    }
}

/// <summary>Shared install flow (dashboard + updates page): BIOS approval first, then install.</summary>
public static class InstallFlow
{
    public static async Task InstallSelectedAsync(XamlRoot root, MainViewModel vm)
    {
        var selected = vm.Updates.Where(u => u.IsSelected && u.IsInstallable).ToList();
        if (selected.Count == 0) return;

        var bios = selected.Where(u => u.IsBios).ToList();
        var approved = new List<string>();
        if (bios.Count > 0 && vm.Policy.BiosApprovalRequired)
        {
            var report = await vm.GetBiosSafetyReportAsync(bios.Select(b => b.Key).ToList());
            if (report is not null && await AppDialogs.ApproveBiosAsync(root, bios, report))
                approved.AddRange(bios.Select(b => b.Key));
            else
                selected = selected.Except(bios).ToList();
        }
        else
        {
            approved.AddRange(bios.Select(b => b.Key));
        }

        if (selected.Count == 0) return;
        var error = await vm.InstallAsync(selected.Select(u => u.Key).ToList(), approved);
        if (error is not null) await AppDialogs.ShowErrorAsync(root, "Couldn't start the installation", error);
    }

    /// <summary>Opens the approval dialog for BIOS updates waiting after "Update All".</summary>
    public static async Task ReviewAwaitingAsync(XamlRoot root, MainViewModel vm)
    {
        foreach (var u in vm.Updates) u.IsSelected = u.Dto.State == UpdateState.AwaitingApproval;
        await InstallSelectedAsync(root, vm);
    }
}
