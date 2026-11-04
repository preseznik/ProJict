using System.Globalization;

namespace ProJict.Core.Model;

/// <summary>A color without transparency, written #RRGGBB.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>"#1e90c8", "1E90C8" or "#19c"; null when it isn't a color.</summary>
    public static Rgb? Parse(string? text)
    {
        var hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>How bright it looks, 0 (black) to 1 (white): the relative luminance contrast is measured with.</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    /// <summary>The contrast ratio with another color, 1 (none) to 21 (black on white).</summary>
    public double Contrast(Rgb other)
    {
        var (a, b) = (Luminance, other.Luminance);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Text on this color: black or white, whichever reads better.</summary>
    public Rgb TextOn => Contrast(Black) >= Contrast(White) ? Black : White;

    public static Rgb Black { get; } = new(0, 0, 0);
    public static Rgb White { get; } = new(255, 255, 255);

    /// <summary>This color with <paramref name="amount"/> (0 to 1) of <paramref name="other"/> mixed in.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte M(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new Rgb(M(R, other.R), M(G, other.G), M(B, other.B));
    }

    /// <summary>Lighter by <paramref name="amount"/> of the way to white, keeping its hue and saturation.</summary>
    public Rgb Lighter(double amount)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s, l + (1 - l) * Math.Clamp(amount, 0, 1));
    }

    /// <summary>Darker by <paramref name="amount"/> of the way to black, keeping its hue and saturation.</summary>
    public Rgb Darker(double amount)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s, l * (1 - Math.Clamp(amount, 0, 1)));
    }

    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        if (s <= 0) { var v = (byte)Math.Round(l * 255); return new Rgb(v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 1 / 2.0 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        byte C(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Rgb(C(Hue(p, q, h + 1 / 3.0)), C(Hue(p, q, h)), C(Hue(p, q, h - 1 / 3.0)));
    }
}

/// <summary>An accent and its shades, three lighter and three darker, the way Windows makes them for its own accent.</summary>
public sealed record AccentShades(Rgb Accent, Rgb Light1, Rgb Light2, Rgb Light3, Rgb Dark1, Rgb Dark2, Rgb Dark3)
{
    public static AccentShades Of(Rgb accent) =>
        new(accent, accent.Lighter(0.25), accent.Lighter(0.49), accent.Lighter(0.69), accent.Darker(0.17), accent.Darker(0.37), accent.Darker(0.55));

    /// <summary>The shades in order: the accent, Light1–3, Dark1–3.</summary>
    public IReadOnlyList<Rgb> All => [Accent, Light1, Light2, Light3, Dark1, Dark2, Dark3];
}

/// <summary>
/// ProJict's colors beside the light or dark theme (#ed53e5d4): Windows (your Windows accent), a premade scheme, or your
/// own. A scheme names an accent (buttons, links, selections, check boxes), a tint for the windows' background, and may
/// change the highlights: notice bars, warnings, errors, success, and agents at work. What it leaves out stays as the
/// theme has it.
/// </summary>
public sealed record ColorScheme(string Name, string Description, Rgb? Accent = null, Rgb? Tint = null, Rgb? Notice = null, Rgb? Warning = null,
    Rgb? Error = null, Rgb? Success = null, Rgb? AgentActivity = null)
{
    public const string WindowsName = "Windows";
    public const string CustomName = "Custom";

    /// <summary>The dark theme's background, and the light one's, that a tint is mixed into.</summary>
    public static Rgb DarkBase { get; } = new(0x20, 0x20, 0x20);
    public static Rgb LightBase { get; } = new(0xF3, 0xF3, 0xF3);

    /// <summary>How much of the tint goes into the dark and the light background.</summary>
    public const double DarkTint = 0.13, LightTint = 0.09;

    /// <summary>The schemes ProJict comes with, Windows first (Custom isn't among them).</summary>
    public static IReadOnlyList<ColorScheme> Premade { get; } =
    [
        new(WindowsName, "Your Windows accent color, and the theme's own background"),
        new("Ocean", "Blue, with a cool blue background", Rgb.Parse("#0F8BD0"), Rgb.Parse("#0F7CC0")),
        new("Forest", "Green, with a green-grey background", Rgb.Parse("#2E9E5B"), Rgb.Parse("#2E7D4F")),
        new("Sunset", "Orange, with a warm background", Rgb.Parse("#E8692C"), Rgb.Parse("#C8553D")),
        new("Rose", "Pink, with a rosy background", Rgb.Parse("#D6457A"), Rgb.Parse("#B83B6B")),
        new("Grape", "Purple, with a violet background", Rgb.Parse("#8A5CD6"), Rgb.Parse("#6B4BB8")),
        new("Gold", "Amber, with a warm brown background", Rgb.Parse("#C8961E"), Rgb.Parse("#8A6A1E")),
        new("Slate", "Steel blue, with a cool grey background", Rgb.Parse("#5B7A99"), Rgb.Parse("#4A5A6A")),
    ];

    /// <summary>The names to pick from: the premade schemes, then Custom.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Premade.Select(s => s.Name), CustomName];

    /// <summary>The scheme the settings pick (an unknown name is Windows).</summary>
    public static ColorScheme For(string? name, CustomColors? custom) =>
        string.Equals(name, CustomName, StringComparison.OrdinalIgnoreCase)
            ? FromCustom(custom ?? new CustomColors())
            : Premade.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Premade[0];

    public static ColorScheme FromCustom(CustomColors c) =>
        new(CustomName, "Your own colors", Rgb.Parse(c.Accent), Rgb.Parse(c.Background), Rgb.Parse(c.Notice), Rgb.Parse(c.Warning), Rgb.Parse(c.Error),
            Rgb.Parse(c.Success), Rgb.Parse(c.AgentActivity));

    /// <summary>Changes nothing: the theme as Fluent and Windows have it.</summary>
    public bool IsPlain => Accent == null && Tint == null && Notice == null && Warning == null && Error == null && Success == null && AgentActivity == null;

    /// <summary>The windows' background in the dark or light theme, when the scheme tints it.</summary>
    public Rgb? Background(bool dark) => Tint is { } t ? (dark ? DarkBase.Mix(t, DarkTint) : LightBase.Mix(t, LightTint)) : null;

    /// <summary>A background shade of the theme (<paramref name="themeColor"/>) with the scheme's tint in it, or it as it is.</summary>
    public Rgb Tinted(Rgb themeColor, bool dark) => Tint is { } t ? themeColor.Mix(t, dark ? DarkTint : LightTint) : themeColor;
}

/// <summary>Your own colors (Settings › Board › Colors › Custom), each #RRGGBB, or null for the theme's.</summary>
public sealed class CustomColors
{
    /// <summary>Buttons, links, selections and check boxes.</summary>
    public string? Accent { get; set; }
    /// <summary>Mixed into the windows' background.</summary>
    public string? Background { get; set; }
    /// <summary>Notice bars (an agent asking, a tool key, the browser).</summary>
    public string? Notice { get; set; }
    /// <summary>Warnings and counts that want your attention.</summary>
    public string? Warning { get; set; }
    public string? Error { get; set; }
    public string? Success { get; set; }
    /// <summary>The marks of agents at work (the Agents view and the board).</summary>
    public string? AgentActivity { get; set; }

    public CustomColors Copy() => (CustomColors)MemberwiseClone();
}
