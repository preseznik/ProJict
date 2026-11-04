using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ProJict.Core.Model;

namespace ProJict.App.Views;

/// <summary>
/// One of your own colors (Settings › Board › Colors › Custom, #ed53e5d4): a swatch that opens a palette, the color as
/// #RRGGBB to type, and a button back to the theme's own. Empty means the theme's.
/// </summary>
public sealed class ColorField : DockPanel
{
    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Button _reset;
    private readonly Popup _palette;
    private string? _value;

    /// <summary>The color picked or typed, or the theme's (null) again.</summary>
    public event EventHandler? Changed;

    public ColorField()
    {
        _swatch = new Border { Width = 34, Height = 20, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var pick = new Button { Padding = new Thickness(4), Content = _swatch, ToolTip = "Pick a color" };
        _hex = new TextBox { Width = 90, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationPropertiesName(_hex, "Color as #RRGGBB");
        _reset = new Button { Content = "Theme's", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Use the theme's own color" };
        _palette = new Popup { PlacementTarget = pick, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = Palette() };
        Children.Add(pick);
        Children.Add(_hex);
        Children.Add(_reset);
        Children.Add(_palette);
        pick.Click += (_, _) => _palette.IsOpen = !_palette.IsOpen;
        _reset.Click += (_, _) => Set(null);
        _hex.LostKeyboardFocus += (_, _) => TakeTyped();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            TakeTyped();
            e.Handled = true;
        };
        Show();
    }

    /// <summary>#RRGGBB, or null for the theme's color.</summary>
    public string? Value
    {
        get => _value;
        set
        {
            _value = Rgb.Parse(value)?.ToString();
            Show();
        }
    }

    /// <summary>What it names, read out for screen readers and in the tooltip.</summary>
    public string Label
    {
        get => (string)(ToolTip ?? "");
        set
        {
            ToolTip = value;
            AutomationPropertiesName(_hex, value + " as #RRGGBB");
        }
    }

    private static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>The colors to pick from: twelve hues, light to dark, and greys.</summary>
    public static IReadOnlyList<Rgb> PaletteColors { get; } = MakePalette();

    private static List<Rgb> MakePalette()
    {
        var colors = new List<Rgb>();
        foreach (var lightness in new[] { 0.72, 0.58, 0.45, 0.32 })
            for (var hue = 0; hue < 12; hue++)
                colors.Add(Rgb.FromHsl(hue / 12.0, 0.7, lightness));
        foreach (var grey in new byte[] { 0xF0, 0xD0, 0xB0, 0x90, 0x70, 0x58, 0x44, 0x34, 0x28, 0x1C, 0x12, 0x00 })
            colors.Add(new Rgb(grey, grey, grey));
        return colors;
    }

    private Border Palette()
    {
        var grid = new UniformGrid { Columns = 12, Margin = new Thickness(6) };
        foreach (var color in PaletteColors)
        {
            var swatch = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), ToolTip = color.ToString(), Focusable = true,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, color.ToString());
            swatch.Click += (_, _) =>
            {
                _palette.IsOpen = false;
                Set(color.ToString());
            };
            grid.Children.Add(swatch);
        }
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        return frame;
    }

    private void TakeTyped()
    {
        var text = _hex.Text.Trim();
        if (text.Length == 0) Set(null);
        else if (Rgb.Parse(text) is { } typed) Set(typed.ToString());
        else Show(); // not a color: back to what it was
    }

    private void Set(string? value)
    {
        var next = Rgb.Parse(value)?.ToString();
        if (next == _value)
        {
            Show();
            return;
        }
        _value = next;
        Show();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Show()
    {
        if (Rgb.Parse(_value) is { } c)
        {
            _swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            _hex.Text = c.ToString();
            _reset.IsEnabled = true;
        }
        else
        {
            // The theme's own: an empty, crossed swatch.
            _swatch.Background = new LinearGradientBrush(Colors.Transparent, Colors.Transparent, 45)
            {
                GradientStops = { new GradientStop(Colors.Transparent, 0.46), new GradientStop(Color.FromRgb(0xC0, 0x40, 0x40), 0.47),
                                  new GradientStop(Color.FromRgb(0xC0, 0x40, 0x40), 0.53), new GradientStop(Colors.Transparent, 0.54) },
            };
            _hex.Text = "";
            _reset.IsEnabled = false;
        }
    }
}
