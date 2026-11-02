using System.Windows.Media;

namespace ProJict.App.ViewModels;

/// <summary>A round badge with initials for someone working in a project ("CC" for Claude Code), shown stacked next to it.</summary>
public sealed record WorkerBadge(string Name, string Initials, Brush Background)
{
    private const int MaxShown = 3;

    private static readonly Brush[] Palette =
        new[] { "#D97757", "#3B82F6", "#10A37F", "#A855F7", "#E11D48", "#D9A21B" }
            .Select(hex => (Brush)new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))).Select(b => { b.Freeze(); return b; }).ToArray();

    /// <summary>At most three badges; a fourth one says how many more there are.</summary>
    public static IReadOnlyList<WorkerBadge> For(IReadOnlyList<string> names)
    {
        var badges = names.Take(names.Count > MaxShown ? MaxShown - 1 : MaxShown).Select(n => new WorkerBadge(n, InitialsOf(n), ColorOf(n))).ToList();
        if (names.Count > MaxShown) badges.Add(new WorkerBadge(string.Join(", ", names.Skip(MaxShown - 1)), $"+{names.Count - (MaxShown - 1)}", Brushes.Gray));
        return badges;
    }

    /// <summary>"Claude Code · login fix" → "CC", "Codex" → "C", "Hermes (via alice)" → "H" (where it's from isn't part of its name).</summary>
    public static string InitialsOf(string name)
    {
        var own = name.Split('·')[0];
        if (own.IndexOf(" (via ", StringComparison.Ordinal) is > 0 and var via) own = own[..via];
        var words = own.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    // Common agents get colours of their own, so two of them side by side never look alike; other names are hashed.
    private static readonly Dictionary<string, int> KnownColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = 0, ["opencode"] = 1, ["codex"] = 2, ["cursor"] = 3, ["gemini"] = 4, ["copilot"] = 5,
    };

    /// <summary>The same name always gets the same colour; labels don't change it.</summary>
    private static Brush ColorOf(string name)
    {
        var key = name.Split('·')[0].Trim().ToLowerInvariant();
        if (KnownColors.TryGetValue(key.Split(' ')[0], out var known)) return Palette[known];
        var hash = key.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return Palette[(hash & int.MaxValue) % Palette.Length];
    }

    public static string ToolTipFor(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => $"{names[0]} is working here",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]} are working here",
    };
}
