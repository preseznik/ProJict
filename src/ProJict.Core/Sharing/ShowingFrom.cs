using ProJict.Core.Model;

namespace ProJict.Core.Sharing;

/// <summary>
/// What a ProJict connected to others shows (#45694fa0): everything together (null), only its own projects and channels
/// (<see cref="Own"/>), or only one connected ProJict's (its address). A connection that's gone shows everything again.
/// </summary>
public static class ShowingFrom
{
    /// <summary>Only this ProJict's own.</summary>
    public const string Own = "this";

    /// <summary>What's shown, given the connections there are now: a connection that was removed falls back to everything.</summary>
    public static string? Effective(string? from, IEnumerable<string> connections) =>
        from is null or Own ? from : connections.Contains(from, StringComparer.OrdinalIgnoreCase) ? from : null;

    public static bool Shows(string? from, Project project) => from switch
    {
        null => true,
        Own => project.Remote == null,
        _ => project.Remote is { } remote && string.Equals(remote.HostKey, from, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>This ProJict's own channels, wiki and Inbox are shown.</summary>
    public static bool ShowsOwn(string? from) => from is null or Own;

    /// <summary>The channels and projects of the ProJict at this address are shown.</summary>
    public static bool ShowsHost(string? from, string hostKey) => from == null || string.Equals(from, hostKey, StringComparison.OrdinalIgnoreCase);
}
