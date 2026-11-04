using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// ProJict's colors (#ed53e5d4): the light or dark theme (Fluent's), and over it a color scheme. A scheme's accent
/// replaces every brush Fluent draws in your Windows accent, shade for shade; its tint goes into the windows' background
/// and title bars; its highlights replace the theme's notice, warning, error and success colors and the agents' marks.
/// The scheme's brushes sit in a dictionary merged after Fluent's, made again whenever the theme changes.
/// </summary>
public static class Look
{
    /// <summary>The windows' background when the scheme tints it.</summary>
    public const string WindowBackgroundKey = "ProJictWindowBackgroundBrush";

    private static ResourceDictionary? _overrides;
    private static ResourceDictionary? _madeFor;
    private static bool _hooked;

    /// <summary>The scheme shown now.</summary>
    public static ColorScheme Scheme { get; private set; } = ColorScheme.Premade[0];

    /// <summary>The theme shown now: System, Light or Dark.</summary>
    public static string Theme { get; private set; } = "System";

    /// <summary>Shows <paramref name="theme"/> (System, Light or Dark) in <paramref name="scheme"/>'s colors.</summary>
    public static void Apply(string theme, ColorScheme scheme)
    {
        if (Application.Current is not { } app) return;
        Hook(app);
        Theme = theme;
        Scheme = scheme;
        app.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        Refresh(force: true);
    }

    /// <summary>The theme's dictionary changed (Windows switched between light and dark): the scheme is made for it again.</summary>
    public static void Refresh(bool force = false)
    {
        if (Application.Current is not { } app) return;
        var fluent = FluentDictionary(app.Resources);
        if (!force && ReferenceEquals(fluent, _madeFor) && _overrides != null) return;
        _madeFor = fluent;
        var entries = new Dictionary<string, object?>();
        if (fluent != null) Collect(fluent, entries);
        var dark = IsDark(fluent, entries);
        var overrides = Overrides(Scheme, entries, dark);
        var merged = app.Resources.MergedDictionaries;
        if (_overrides != null) merged.Remove(_overrides);
        _overrides = overrides;
        merged.Add(overrides);
        foreach (Window window in app.Windows) Tint(window);
    }

    private static void Hook(Application app)
    {
        if (_hooked) return;
        _hooked = true;
        // Every window gets the scheme's background and title bar as it opens.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Tint((Window)sender)));
        // With the System theme, WPF swaps light and dark when Windows does: the scheme follows once it has.
        SystemEvents.UserPreferenceChanged += (_, _) =>
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Refresh());
    }

    /// <summary>The Fluent theme dictionary WPF merged for the theme shown (light or dark).</summary>
    private static ResourceDictionary? FluentDictionary(ResourceDictionary resources)
    {
        foreach (var d in resources.MergedDictionaries)
        {
            if (d.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true) return d;
            if (FluentDictionary(d) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>A dictionary's entries as lookups see them: merged ones in order, its own last (they win).</summary>
    private static void Collect(ResourceDictionary d, Dictionary<string, object?> into)
    {
        foreach (var m in d.MergedDictionaries) Collect(m, into);
        foreach (var key in d.Keys)
            if (key is string name)
                into[name] = d[key];
    }

    private static bool IsDark(ResourceDictionary? fluent, Dictionary<string, object?> entries)
    {
        if (fluent?.Source?.OriginalString is { } source)
        {
            if (source.Contains("Dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (source.Contains("Light", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return entries.TryGetValue("TextFillColorPrimaryBrush", out var text) && text is SolidColorBrush { Color: var c } && c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 140;
    }

    /// <summary>The scheme's brushes over the theme's <paramref name="entries"/>.</summary>
    internal static ResourceDictionary Overrides(ColorScheme scheme, IReadOnlyDictionary<string, object?> entries, bool dark)
    {
        var d = new ResourceDictionary();
        if (scheme.Accent is { } accent) AccentOverrides(d, entries, accent);
        var background = ToRgb(entries.TryGetValue("SolidBackgroundFillColorBaseBrush", out var b) && b is SolidColorBrush { Color: var bc } ? bc : null)
                         ?? (dark ? ColorScheme.DarkBase : ColorScheme.LightBase);
        if (scheme.Background(dark) is { } tinted)
        {
            background = tinted;
            d[WindowBackgroundKey] = Brush(tinted);
            foreach (var key in new[]
                     {
                         "SolidBackgroundFillColorBaseBrush", "SolidBackgroundFillColorBaseAltBrush", "SolidBackgroundFillColorSecondaryBrush",
                         "SolidBackgroundFillColorTertiaryBrush", "SolidBackgroundFillColorQuarternaryBrush", "AcrylicBackgroundFillColorDefaultBrush",
                         "AcrylicInAppFillColorDefaultBrush",
                     })
                if (entries.TryGetValue(key, out var value) && value is SolidColorBrush { Color: var c } brush)
                    d[key] = Brush(scheme.Tinted(new Rgb(c.R, c.G, c.B), dark), c.A, brush.Opacity);
        }
        // A highlight replaces the theme's color, and its background (bars, chips) is the window's with some of it mixed in.
        void Highlight(Rgb? color, string brush, string backgroundBrush, double amount)
        {
            if (color is not { } c) return;
            d[brush] = Brush(c);
            d[backgroundBrush] = Brush(background.Mix(c, amount));
        }
        Highlight(scheme.Notice, "SystemFillColorAttentionBrush", "SystemFillColorAttentionBackgroundBrush", dark ? 0.22 : 0.16);
        Highlight(scheme.Warning, "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush", dark ? 0.25 : 0.2);
        Highlight(scheme.Error, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", dark ? 0.25 : 0.2);
        Highlight(scheme.Success, "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush", dark ? 0.25 : 0.2);
        var caution = scheme.Warning ?? ToRgb(entries.TryGetValue("SystemFillColorCautionBrush", out var w) && w is SolidColorBrush { Color: var wc } ? wc : null);
        if (caution is { } warning) d["TextOnCautionBrush"] = Brush(warning.TextOn);
        if (scheme.AgentActivity is { } agents) d["AgentActivityBrush"] = Brush(agents);
        return d;
    }

    /// <summary>
    /// Every brush the theme draws in a shade of your Windows accent gets the same shade of the scheme's: buttons, links,
    /// selections, check boxes, toggles, sliders, focus lines. Text on accent turns black or white, whichever reads.
    /// </summary>
    private static void AccentOverrides(ResourceDictionary d, IReadOnlyDictionary<string, object?> entries, Rgb accent)
    {
        Color[] system = [SystemColors.AccentColor, SystemColors.AccentColorLight1, SystemColors.AccentColorLight2, SystemColors.AccentColorLight3,
                          SystemColors.AccentColorDark1, SystemColors.AccentColorDark2, SystemColors.AccentColorDark3];
        var mine = AccentShades.Of(accent).All;
        Color? Map(Color c)
        {
            for (var i = 0; i < system.Length; i++)
                if (system[i].R == c.R && system[i].G == c.G && system[i].B == c.B)
                    return Color.FromArgb(c.A, mine[i].R, mine[i].G, mine[i].B);
            return null;
        }
        foreach (var (key, value) in entries)
        {
            // Only brushes that follow the Windows accent (Fluent leaves those unfrozen); the others keep their color.
            switch (value)
            {
                case SolidColorBrush { IsFrozen: false } solid when Map(solid.Color) is { } mapped:
                    d[key] = new SolidColorBrush(mapped) { Opacity = solid.Opacity };
                    break;
                case LinearGradientBrush { IsFrozen: false } gradient when gradient.GradientStops.Any(s => Map(s.Color) != null):
                    var copy = new LinearGradientBrush
                    {
                        StartPoint = gradient.StartPoint, EndPoint = gradient.EndPoint, MappingMode = gradient.MappingMode, Opacity = gradient.Opacity,
                        SpreadMethod = gradient.SpreadMethod, ColorInterpolationMode = gradient.ColorInterpolationMode,
                        RelativeTransform = gradient.RelativeTransform?.CloneCurrentValue(), Transform = gradient.Transform?.CloneCurrentValue(),
                    };
                    foreach (var stop in gradient.GradientStops) copy.GradientStops.Add(new GradientStop(Map(stop.Color) ?? stop.Color, stop.Offset));
                    d[key] = copy;
                    break;
            }
        }
        // Text on the accent fill, and on selected text, in black or white.
        if (d["AccentFillColorDefaultBrush"] is SolidColorBrush { Color: var fill })
        {
            var text = new Rgb(fill.R, fill.G, fill.B).TextOn;
            foreach (var key in new[] { "TextOnAccentFillColorPrimaryBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver" })
                d[key] = Brush(text);
            foreach (var key in new[] { "TextOnAccentFillColorSecondaryBrush", "AccentButtonForegroundPressed" })
                d[key] = Brush(text, 0x80);
        }
        if (d["AccentFillColorSelectedTextBackgroundBrush"] is SolidColorBrush { Color: var selected })
            d["TextOnAccentFillColorSelectedTextBrush"] = Brush(new Rgb(selected.R, selected.G, selected.B).TextOn);
    }

    /// <summary>The window's background and title bar in the scheme's tint (or the theme's own, without one).</summary>
    public static void Tint(Window window)
    {
        // Windows that draw their own background (see-through ones, overlays) keep it.
        if (window.AllowsTransparency) return;
        var tinted = Application.Current?.TryFindResource(WindowBackgroundKey) is SolidColorBrush;
        var ours = GetTinted(window);
        if (!ours && DependencyPropertyHelper.GetValueSource(window, Control.BackgroundProperty).BaseValueSource == BaseValueSource.Local) return;
        if (tinted)
        {
            window.SetResourceReference(Control.BackgroundProperty, WindowBackgroundKey);
            SetTinted(window, true);
        }
        else if (ours)
        {
            window.ClearValue(Control.BackgroundProperty);
            SetTinted(window, false);
        }
        TitleBar(window, tinted && Scheme.Background(IsDarkNow()) is { } bg ? bg : null);
    }

    private static bool IsDarkNow()
    {
        var entries = new Dictionary<string, object?>();
        var fluent = Application.Current is { } app ? FluentDictionary(app.Resources) : null;
        if (fluent != null) Collect(fluent, entries);
        return IsDark(fluent, entries);
    }

    private static readonly DependencyProperty TintedProperty =
        DependencyProperty.RegisterAttached("Tinted", typeof(bool), typeof(Look), new PropertyMetadata(false));
    private static bool GetTinted(Window w) => (bool)w.GetValue(TintedProperty);
    private static void SetTinted(Window w, bool value) => w.SetValue(TintedProperty, value);

    /// <summary>The title bar's color on Windows 11 (null: the system's).</summary>
    private static void TitleBar(Window window, Rgb? color)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = color is { } c ? c.R | (c.G << 8) | (c.B << 16) : unchecked((int)0xFFFFFFFF);
        try { _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref value, sizeof(int)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const int DwmCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static SolidColorBrush Brush(Rgb c, byte alpha = 255, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Rgb? ToRgb(Color? c) => c is { } v ? new Rgb(v.R, v.G, v.B) : null;
}
