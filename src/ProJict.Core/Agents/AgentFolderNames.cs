using ProJict.Core.Storage;

namespace ProJict.Core.Agents;

/// <summary>
/// The name an agent picked, by the client it runs in and the folder it works in (#67422d19). A session that comes back
/// without one (its bridge started afresh, say after an update) is given it back on its first call, so it doesn't post as
/// "Claude Code" meanwhile. Kept in the workspace settings; the <see cref="Kept"/> most recently used stay.
/// </summary>
public static class AgentFolderNames
{
    public const int Kept = 200;

    /// <summary>"claude code|f:\coding\game": the same client in the same folder, however the path was written.</summary>
    public static string Key(string client, string folder)
    {
        var path = folder.Trim();
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return client.Trim().ToLowerInvariant() + "|" + path.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>The name the client last went by in the folder, or null.</summary>
    public static string? NameFor(WorkspaceSettings settings, string client, string? folder) =>
        string.IsNullOrWhiteSpace(folder) || settings.AgentFolders is not { } known || !known.TryGetValue(Key(client, folder), out var entry) ? null : entry.Name;

    /// <summary>Notes the name the client goes by in the folder now; the oldest go beyond <see cref="Kept"/>.</summary>
    public static void Remember(WorkspaceSettings settings, string client, string? folder, string name, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name)) return;
        var known = settings.AgentFolders ??= new Dictionary<string, AgentFolder>(StringComparer.OrdinalIgnoreCase);
        known[Key(client, folder)] = new AgentFolder { Name = name.Trim(), At = now };
        foreach (var old in known.OrderByDescending(e => e.Value.At).Skip(Kept).Select(e => e.Key).ToList()) known.Remove(old);
    }
}
