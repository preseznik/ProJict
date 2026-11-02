using ProJict.Core.Storage;

namespace ProJict.Core.Features;

/// <summary>
/// Which of a project header's buttons show big, on the left of the others: Ship and Clean up until you choose others
/// with the cog there. The same for every project on this computer.
/// </summary>
public static class HeaderLayout
{
    public static IReadOnlyList<string> DefaultBig { get; } = ["Ship", "Clean up"];

    public static IReadOnlyList<string> Big(AppSettings settings) => settings.BigHeaderButtons ?? DefaultBig;

    public static bool IsBig(AppSettings settings, string name) => Big(settings).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes one button big or not; the others stay as they are.</summary>
    public static void SetBig(AppSettings settings, string name, bool big)
    {
        var names = Big(settings).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (big) names.Add(name);
        settings.BigHeaderButtons = names;
    }

    /// <summary>Back to Ship and Clean up.</summary>
    public static void Reset(AppSettings settings) => settings.BigHeaderButtons = null;
}
