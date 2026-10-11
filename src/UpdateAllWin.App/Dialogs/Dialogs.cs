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

    /// <summary>"What was installed": every update the service tried to install, newest first, grouped by day.</summary>
    public static async Task ShowInstalledAsync(XamlRoot root, IReadOnlyList<HistoryEntry> entries)
    {
        // Right padding keeps the scroll bar clear of the times.
        var panel = new StackPanel { Spacing = 10, MinWidth = 440, Padding = new Thickness(0, 0, 20, 0) };
        if (entries.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Nothing has been installed yet.", TextWrapping = TextWrapping.Wrap });

        foreach (var day in entries.GroupBy(e => e.TimeUtc.ToLocalTime().Date))
        {
            panel.Children.Add(new TextBlock
            {
                Text = day.Key == DateTime.Today ? "Today" : day.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : day.Key.ToString("dddd d MMMM yyyy"),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var e in day)
            {
                var row = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                };
                row.Children.Add(new FontIcon
                {
                    Glyph = e.Success ? "\uE73E" : "\uE711",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(StatusVisuals.ColorFor(e.Success ? OverallStatus.UpToDate : OverallStatus.Failed)),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 3, 0, 0),
                });

                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = e.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                var versions = e.FromVersion is { Length: > 0 } || e.ToVersion is { Length: > 0 } ? $"{e.FromVersion ?? "?"} → {e.ToVersion ?? "?"}  ·  " : "";
                text.Children.Add(new TextBlock
                {
                    Text = versions + e.Result,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                Grid.SetColumn(text, 1);
                row.Children.Add(text);

                var time = new TextBlock
                {
                    Text = e.TimeUtc.ToLocalTime().ToString("t"),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                };
                Grid.SetColumn(time, 2);
                row.Children.Add(time);
                panel.Children.Add(row);
            }
        }

        await new ContentDialog
        {
            Title = "What was installed",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "Close",
        }.Themed(root).ShowAsync();
    }
}

/// <summary>BIOS approval first, then install.</summary>
public static class InstallFlow
{
    public static async Task InstallSelectedAsync(XamlRoot root, MainViewModel vm)
    {
        var selected = vm.Updates.Where(u => u.IsSelected && u.IsInstallable).ToList();
        if (selected.Count == 0) return;

        var bios = selected.Where(u => u.IsBios).ToList();
        var approved = new List<string>();
        if (bios.Count > 0 && vm.BiosApprovalRequired)
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
