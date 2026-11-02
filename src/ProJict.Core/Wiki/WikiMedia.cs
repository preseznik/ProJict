using System.Globalization;

namespace ProJict.Core.Wiki;

/// <summary>Where a picture or player sits across the page.</summary>
public enum MediaPlace { Left, Center, Right }

/// <summary>
/// How a picture, video or sound shows on a wiki page, written in braces right after it: ![](shot.png){medium center}.
/// A size (small, medium, large, full, or a width such as 320px or 50% of the page), a place (left, center, right) and
/// wrap, for text beside it on the other side. Nothing written: its own size (as wide as fits), on the left.
/// </summary>
public readonly record struct MediaLayout(double? Width = null, bool Percent = false, MediaPlace Place = MediaPlace.Left, bool Wrap = false)
{
    /// <summary>The sizes by name, in pixels; "full" is the page's width.</summary>
    public static readonly IReadOnlyList<(string Name, double Width)> Sizes = [("small", 240), ("medium", 420), ("large", 680)];

    public static MediaLayout Full => new(100, Percent: true);

    public bool IsDefault => this == default;
    /// <summary>Text goes beside it: it's wrapped, on the left or the right.</summary>
    public bool TextBeside => Wrap && Place != MediaPlace.Center;

    /// <summary>The words between the braces ("medium center", "width=50% right wrap"); null when one isn't a layout word.</summary>
    public static MediaLayout? Parse(string words)
    {
        var layout = new MediaLayout();
        var any = false;
        foreach (var raw in words.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.TrimStart('.').ToLowerInvariant();
            if (word.StartsWith("width=", StringComparison.Ordinal)) word = word["width=".Length..].Trim('"', '\'');
            switch (word)
            {
                case "left": layout = layout with { Place = MediaPlace.Left }; break;
                case "center" or "centre": layout = layout with { Place = MediaPlace.Center }; break;
                case "right": layout = layout with { Place = MediaPlace.Right }; break;
                case "wrap": layout = layout with { Wrap = true }; break;
                case "full": layout = layout with { Width = 100, Percent = true }; break;
                default:
                    if (Sizes.FirstOrDefault(s => s.Name == word) is { Name: not null } named) layout = layout with { Width = named.Width, Percent = false };
                    else if (Number(word.EndsWith('%') ? word[..^1] : word.EndsWith("px", StringComparison.Ordinal) ? word[..^2] : word) is { } n)
                        layout = layout with { Width = word.EndsWith('%') ? Math.Min(n, 100) : n, Percent = word.EndsWith('%') };
                    else return null;
                    break;
            }
            any = true;
        }
        return any ? layout : null;
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= 4000 ? n : null;

    /// <summary>Its size by name ("small", "full"), or null for a width of its own or none.</summary>
    public string? SizeName => Width is not { } w ? null
        : Percent ? (w == 100 ? "full" : null)
        : Sizes.FirstOrDefault(s => s.Width == w).Name;

    /// <summary>As it's written after a picture: "{medium center}", or "" when it's the usual.</summary>
    public override string ToString()
    {
        if (IsDefault) return "";
        var words = new List<string>();
        if (Width is { } w) words.Add(SizeName ?? w.ToString(CultureInfo.InvariantCulture) + (Percent ? "%" : "px"));
        if (Place != MediaPlace.Left) words.Add(Place == MediaPlace.Center ? "center" : "right");
        if (Wrap) words.Add("wrap");
        return words.Count == 0 ? "" : "{" + string.Join(" ", words) + "}";
    }
}

/// <summary>A picture, video or sound named in a page's text with ![…](…): where it's written, and how it shows.</summary>
public sealed record WikiMediaRef(int Start, int End, string Label, string Target, string Url, string Caption, MediaLayout Layout)
{
    /// <summary>As it's written: ![label](target "caption"){layout}.</summary>
    public string Write() =>
        $"![{Label}]({Target}{(Caption.Length > 0 ? " \"" + Caption.Replace('"', '\'') + "\"" : "")}){Layout}";
}

/// <summary>The pictures, videos and sounds a page names, and changing how one shows.</summary>
public static class WikiMedia
{
    /// <summary>Every ![…](…) in the text, in order, as the page shows them (not those in code blocks or `code`).</summary>
    public static List<WikiMediaRef> Find(string text)
    {
        var found = new List<WikiMediaRef>();
        string? fence = null;
        var i = 0;
        var lineStart = true;
        while (i < text.Length)
        {
            if (lineStart)
            {
                lineStart = false;
                var lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = text.Length;
                var trimmed = text[i..lineEnd].TrimStart();
                var fenceLine = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
                // A code block's lines, its fences included, are skipped whole.
                if (fence != null || fenceLine)
                {
                    if (fence == null) fence = trimmed[..3];
                    else if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                    i = lineEnd + 1;
                    lineStart = true;
                    continue;
                }
            }
            var c = text[i];
            if (c == '\n')
            {
                i++;
                lineStart = true;
                continue;
            }
            if (c == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
            {
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var ticks = 0;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var close = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                i = close > 0 ? close + ticks : i + ticks;
                continue;
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' && Markdown.ImageAt(text, i) is { } image)
            {
                found.Add(image);
                i = image.End;
                continue;
            }
            i++;
        }
        return found;
    }

    /// <summary>
    /// What to write instead of the <paramref name="occurrence"/>-th (from 0) picture or player naming
    /// <paramref name="url"/>, to show it with this layout and caption: where it is in the text and its new text.
    /// Null when the text has no such one.
    /// </summary>
    public static (int Start, int Length, string Text)? Reformat(string text, string url, int occurrence, MediaLayout layout, string caption)
    {
        var same = Find(text).Where(m => string.Equals(m.Url, url, StringComparison.OrdinalIgnoreCase)).ToList();
        if (occurrence < 0 || occurrence >= same.Count) return null;
        var found = same[occurrence];
        return (found.Start, found.End - found.Start, (found with { Layout = layout, Caption = caption.Trim() }).Write());
    }

    /// <summary>Whether the page shows this file in its text (so its list of files needn't play it again).</summary>
    public static bool Shows(string text, string fileName) =>
        Find(text).Any(m => string.Equals(m.Url, fileName, StringComparison.OrdinalIgnoreCase));
}
