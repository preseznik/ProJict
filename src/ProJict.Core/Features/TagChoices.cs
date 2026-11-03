namespace ProJict.Core.Features;

/// <summary>
/// The types an item can be tagged with (BUG, ADD, CHANGE…): ProJict's own, then the ones you added (Settings keeps
/// them for the whole workspace), then any other an item of the project already has. All in capitals, each once.
/// </summary>
public static class TagChoices
{
    public static IReadOnlyList<string> BuiltIn { get; } = ["BUG", "ADD", "ISSUE", "CHANGE", "FIX", "IDEA"];
    public const int MaxLength = 16;

    /// <summary>The list to pick from.</summary>
    public static IReadOnlyList<string> For(IEnumerable<string>? custom, IEnumerable<string?> used)
    {
        var list = new List<string>(BuiltIn);
        void Add(string? tag)
        {
            if (Clean(tag) is { } t && !list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
        }
        foreach (var tag in custom ?? []) Add(tag);
        foreach (var tag in used.OfType<string>().Select(Clean).OfType<string>().Distinct().Order(StringComparer.Ordinal)) Add(tag);
        return list;
    }

    /// <summary>
    /// A type as you typed it, made into a tag: capitals, without a trailing colon, spaces as dashes, at most 16
    /// characters; null when nothing's left.
    /// </summary>
    public static string? Clean(string? typed)
    {
        var tag = (typed ?? "").Trim().TrimEnd(':').Trim().ToUpperInvariant();
        tag = string.Join("-", tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        tag = new string(tag.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (tag.Length > MaxLength) tag = tag[..MaxLength];
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>Adds a type you made to the workspace's list; false when it's one already there.</summary>
    public static bool Remember(List<string> custom, string tag)
    {
        if (Clean(tag) is not { } clean || BuiltIn.Contains(clean) || custom.Contains(clean, StringComparer.OrdinalIgnoreCase)) return false;
        custom.Add(clean);
        return true;
    }
}
