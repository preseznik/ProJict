using System.Text.RegularExpressions;

namespace ProJict.Core.Agents;

/// <summary>
/// The names agents sign with. Each agent picks a short name of its own (set_name), e.g. "Ada"; until it does, it goes by
/// its client's name, numbered when several sessions of the same client run at once ("Claude Code 2"). Older history
/// may hold names with a label ("Claude Code · login fix"), from before agents had names.
/// </summary>
public static partial class AgentNaming
{
    public const int MaxLength = 20;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "you", "me", "everyone", "all", "here", "projict", "agent", "user", "owner" };

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}-]*$")]
    private static partial Regex Word();

    /// <summary>Why the name can't be used, or null when it can.</summary>
    public static string? Problem(string name)
    {
        if (name.Length < 2) return "Pick a name of at least 2 characters.";
        if (name.Length > MaxLength) return $"Keep the name short ({MaxLength} characters at most).";
        if (!Word().IsMatch(name)) return "A name is one word: letters, digits and dashes, e.g. Ada or Quill.";
        if (Reserved.Contains(name)) return $"{name} is reserved; pick another name.";
        return null;
    }

    /// <summary>A label that starts with the client's name loses it: "Claude Code · GRID2VR session" → "GRID2VR session".</summary>
    public static string? TrimLabel(string? label, string client)
    {
        label = label?.Trim();
        while (!string.IsNullOrEmpty(label) && label.StartsWith(client, StringComparison.OrdinalIgnoreCase))
            label = label[client.Length..].TrimStart(' ', '·', '-', ':', '|', ',').Trim();
        return string.IsNullOrEmpty(label) ? null : label;
    }

    /// <summary>"Ada (via Alice)": an agent working in a project shared with Alice's ProJict, through her connection.</summary>
    public static string Via(string agent, string guest) => $"{agent} (via {guest})";

    /// <summary>
    /// The agent in "Ada (via Alice)" ("Ada"), when the name is signed that way (through <paramref name="guest"/>, if
    /// given); else null.
    /// </summary>
    public static string? WithoutVia(string name, string? guest = null)
    {
        if (!name.EndsWith(')') || name.LastIndexOf(" (via ", StringComparison.Ordinal) is not (> 0 and var at)) return null;
        var through = name[(at + 6)..^1];
        return guest == null || string.Equals(through, guest, StringComparison.OrdinalIgnoreCase) ? name[..at] : null;
    }

    /// <summary>Who signed it, without a label from before agents had names: "Claude Code · login fix" → "Claude Code".</summary>
    public static string Identity(string name) => name.IndexOf(" · ", StringComparison.Ordinal) is > 0 and var i ? name[..i] : name;

    /// <summary>A numbered client name without its number: "Claude Code 2" → "Claude Code" (when the client is "Claude Code").</summary>
    public static string Unnumbered(string name, string client)
    {
        var identity = Identity(name);
        if (identity.Length > client.Length + 1 && identity.StartsWith(client + " ", StringComparison.OrdinalIgnoreCase) &&
            identity[(client.Length + 1)..].All(char.IsAsciiDigit))
            return identity[..client.Length];
        return identity;
    }

    /// <summary>
    /// Whether something signed <paramref name="by"/> is this session's own work: by its name, or its API token's name
    /// (what it signed before it picked one); a session that hasn't picked a name shares its client's work (any number,
    /// older labels included).
    /// </summary>
    public static bool IsOwnWork(McpSession session, string by) =>
        session.Name != null || !string.IsNullOrWhiteSpace(session.SignAs)
            ? string.Equals(Identity(by), session.AgentName, StringComparison.OrdinalIgnoreCase) ||
              (!string.IsNullOrWhiteSpace(session.SignAs) && string.Equals(Identity(by), session.SignAs.Trim(), StringComparison.OrdinalIgnoreCase))
            : string.Equals(Unnumbered(by, session.Client), session.Client, StringComparison.OrdinalIgnoreCase);
}
