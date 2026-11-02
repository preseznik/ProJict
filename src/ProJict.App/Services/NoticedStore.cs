using System.IO;
using ProJict.Core.Storage;

namespace ProJict.App.Services;

/// <summary>
/// What each named agent was told (answers to its messages, comments on its work, work sent back, ship, studio and other
/// news), kept across ProJict restarts: an agent reconnecting after an update isn't told it all again. Kept a month, per
/// data folder, in noticed.json next to ProJict's other settings on this computer; without a path (tests) in memory.
/// Reminders about how an agent is set up (the old bridge, the chat watcher tip, a rename) are said again to each new
/// session. Safe to use from any thread.
/// </summary>
public sealed class NoticedStore
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);
    /// <summary>The newest this many per agent stay.</summary>
    public const int KeptPerAgent = 3000;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly string? _path;
    private readonly string _scope;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, DateTime>> _byAgent;
    private readonly Timer _saveTimer;

    public NoticedStore(string? path, string dataRoot)
    {
        _path = path;
        _scope = Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToLowerInvariant();
        _byAgent = Load(path);
        _saveTimer = new Timer(_ => Save());
    }

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "noticed.json");

    /// <summary>Whether it's news that stays told (not a reminder about how the agent is set up).</summary>
    public static bool Lasts(string key) =>
        key != "oldbridge" && !key.StartsWith("tip:", StringComparison.Ordinal) && !key.StartsWith("renamed:", StringComparison.Ordinal);

    private string KeyOf(string agent) => _scope + "|" + agent.Trim().ToLowerInvariant();

    /// <summary>What the agent was told before.</summary>
    public IReadOnlyCollection<string> For(string agent)
    {
        lock (_lock)
            return _byAgent.TryGetValue(KeyOf(agent), out var keys) ? [.. keys.Keys] : [];
    }

    /// <summary>Notes what the agent has been told now (what it was told before stays as it was).</summary>
    public void Remember(string agent, IEnumerable<string> told, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var added = false;
        lock (_lock)
        {
            var key = KeyOf(agent);
            if (!_byAgent.TryGetValue(key, out var keys)) _byAgent[key] = keys = [];
            foreach (var thing in told)
                if (Lasts(thing) && keys.TryAdd(thing, at)) added = true;
            if (keys.Count > KeptPerAgent)
                foreach (var old in keys.OrderBy(k => k.Value).Take(keys.Count - KeptPerAgent).Select(k => k.Key).ToList()) keys.Remove(old);
        }
        if (added && _path != null) _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private static Dictionary<string, Dictionary<string, DateTime>> Load(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return [];
            var loaded = Json.Deserialize<Dictionary<string, Dictionary<string, DateTime>>>(File.ReadAllText(path)) ?? [];
            var cutoff = DateTime.Now - KeptFor;
            foreach (var keys in loaded.Values)
                foreach (var old in keys.Where(k => k.Value < cutoff).Select(k => k.Key).ToList()) keys.Remove(old);
            return loaded;
        }
        catch (Exception) { return []; }
    }

    /// <summary>Writes it now (it's also written a moment after each change).</summary>
    public void Save()
    {
        if (_path == null) return;
        string json;
        lock (_lock) json = Json.Serialize(_byAgent);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception) { /* kept in memory; written again on the next change */ }
    }
}
