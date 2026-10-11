using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace UpdateAllWin.App.Controls;

/// <summary>A Windows 11 Settings-style row: icon, title, description and a control on the right.</summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingRow : UserControl
{
    private const string Template = """
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="6">
          <Grid ColumnSpacing="16" MinHeight="64" Padding="16,10,16,10">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <FontIcon x:Name="Icon" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" FontSize="18" VerticalAlignment="Center" />
            <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="2">
              <TextBlock x:Name="Header" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Style="{StaticResource BodyStrongTextBlockStyle}" TextWrapping="Wrap" />
              <TextBlock x:Name="Description" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Style="{StaticResource CaptionTextBlockStyle}"
                         Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" />
            </StackPanel>
            <ContentPresenter x:Name="Action" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Grid.Column="2"
                              VerticalAlignment="Center" HorizontalAlignment="Right" />
          </Grid>
        </Border>
        """;

    private readonly FontIcon _icon;
    private readonly TextBlock _header;
    private readonly TextBlock _description;
    private readonly ContentPresenter _action;

    public SettingRow()
    {
        // Built from XAML so {ThemeResource} brushes follow the element's theme (light/dark).
        var root = (Border)XamlReader.Load(Template);
        _icon = (FontIcon)root.FindName("Icon");
        _header = (TextBlock)root.FindName("Header");
        _description = (TextBlock)root.FindName("Description");
        _action = (ContentPresenter)root.FindName("Action");
        Content = root;
    }

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow),
        new PropertyMetadata("", (d, e) => ((SettingRow)d)._header.Text = (string)e.NewValue));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow),
        new PropertyMetadata("", (d, e) =>
        {
            var row = (SettingRow)d;
            row._description.Text = (string)e.NewValue;
            row._description.Visibility = string.IsNullOrEmpty((string)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow),
        new PropertyMetadata("", (d, e) => ((SettingRow)d)._icon.Glyph = (string)e.NewValue));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(nameof(Action), typeof(object), typeof(SettingRow),
        new PropertyMetadata(null, (d, e) => ((SettingRow)d)._action.Content = e.NewValue));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
}
