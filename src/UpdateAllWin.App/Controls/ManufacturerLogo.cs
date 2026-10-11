using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace UpdateAllWin.App.Controls;

/// <summary>
/// The PC manufacturer's logo (Assets/Brands/*.svg) on a white tile so it reads in light and dark themes.
/// Falls back to the manufacturer name for brands without a logo file.
/// </summary>
public sealed partial class ManufacturerLogo : UserControl
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _fallback = new()
    {
        FontWeight = FontWeights.Bold,
        FontSize = 18,
        Foreground = new SolidColorBrush(Colors.Black),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public ManufacturerLogo()
    {
        var grid = new Grid();
        grid.Children.Add(_image);
        grid.Children.Add(_fallback);
        Content = new Border
        {
            Child = grid,
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
    }

    public static readonly DependencyProperty ManufacturerProperty = DependencyProperty.Register(
        nameof(Manufacturer), typeof(string), typeof(ManufacturerLogo),
        new PropertyMetadata("", (d, e) => _ = ((ManufacturerLogo)d).LoadAsync((string)e.NewValue)));

    public string Manufacturer
    {
        get => (string)GetValue(ManufacturerProperty);
        set => SetValue(ManufacturerProperty, value);
    }

    /// <summary>Maps a WMI manufacturer string to a logo file name in Assets/Brands.</summary>
    public static string? BrandKey(string? manufacturer)
    {
        var m = (manufacturer ?? "").Trim().ToLowerInvariant();
        if (m is "hp" or "hpe" || m.StartsWith("hewlett")) return "hp";
        if (m.StartsWith("dell") || m.StartsWith("alienware")) return "dell";
        if (m.StartsWith("lenovo")) return "lenovo";
        if (m.StartsWith("asus")) return "asus";
        if (m.StartsWith("acer")) return "acer";
        if (m.StartsWith("micro-star") || m == "msi") return "msi";
        if (m.StartsWith("microsoft")) return "microsoft";
        return null;
    }

    private async Task LoadAsync(string manufacturer)
    {
        _fallback.Text = manufacturer;
        var key = BrandKey(manufacturer);
        var path = key is null ? null : Path.Combine(App.AssetsFolder, "Brands", key + ".svg");
        if (path is null || !File.Exists(path))
        {
            _image.Source = null;
            _fallback.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var svg = new SvgImageSource();
            var status = await svg.SetSourceAsync(stream);
            if (status != SvgImageSourceLoadStatus.Success) throw new InvalidDataException(status.ToString());
            _image.Source = svg;
            _fallback.Visibility = Visibility.Collapsed;
            AutomationHelper(manufacturer);
        }
        catch
        {
            _image.Source = null;
            _fallback.Visibility = Visibility.Visible;
        }
    }

    private void AutomationHelper(string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"{name} logo");
}
