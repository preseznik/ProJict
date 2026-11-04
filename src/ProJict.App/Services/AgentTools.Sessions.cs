using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.Services;

/// <summary>
/// Sessions of their own for subagents sharing one connection (#5ba6f2b2): a lead agent and its helpers on one connector
/// each keep their own name, check-outs, status and private messages. start_session gives a secret token for a name;
/// every call that carries it as "session" acts as that agent. ProJict makes the token and keeps only its hash, so no
/// agent can act as another by guessing; it works only from the same connection (the same client in the same folder, or
/// the same API token), and keeps the name across restarts of ProJict and of the bridge.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Subagent sessions by their token's hash, while their connection is here.</summary>
    private readonly Dictionary<string, McpSession> _subSessions = [];
    private SessionTokenStore? _sessionTokens;
    private SessionTokenStore SessionTokens => _sessionTokens ??= new SessionTokenStore(_state.PersistsSettings ? SessionTokenStore.DefaultPath : null);

    private void AddSessionTools() =>
        Add("start_session", "Start a session for a subagent",
            "For a lead agent whose subagents share this connection: starts a session of its own for one of them, under its own name, with its own " +
            "check-outs, status and private messages. Returns a secret token: every call made as that agent passes it as \"session\" (any tool takes it); " +
            "calls without it stay this connection's own. Starting it again with the same name gives the same token back, and the name stays with the " +
            "token when ProJict restarts. Only this connection can use it.",
            Schema(("name", "string", $"The subagent's name: one word of 2 to {AgentNaming.MaxLength} letters, digits or dashes, e.g. Ada.", true)),
            StartSession);

    private string StartSession(McpSession session, Args args, List<Attachment> files)
    {
        var connection = session.Parent ?? session;
        var name = args.Required("name").Trim().TrimStart('@');
        if (AgentNaming.Problem(name) is { } problem) throw new McpToolException(problem);
        if (string.Equals(name, _state.UserName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, connection.Client, StringComparison.OrdinalIgnoreCase))
            throw new McpToolException($"{name} can't be a subagent's name; pick one of its own.");
        // The same name again on this connection: the same session, with a new token (the old one stops working).
        var existing = _subSessions.Values.FirstOrDefault(s => (s.Parent == connection || SameConnection(s.Parent!, connection)) &&
                                                          string.Equals(s.AgentName, name, StringComparison.OrdinalIgnoreCase));
        List<McpSession> others;
        lock (_live) others = _live.Where(s => s != existing).ToList();
        if (existing == null && others.Any(s => string.Equals(s.AgentName, name, StringComparison.OrdinalIgnoreCase)))
            throw new McpToolException($"{name} is taken by another agent connected now; pick another name.", ToolProblem.Conflict);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Hash(token);
        var sub = existing;
        if (sub != null)
        {
            _subSessions.Remove(sub.TokenHash!);
            SessionTokens.Forget(sub.TokenHash!);
        }
        else
        {
            sub = NewSubSession(connection);
            SessionStarted(sub);
            TakeName(sub, name);
        }
        sub.Parent = connection;
        Follow(sub, connection);
        sub.TokenHash = hash;
        _subSessions[hash] = sub;
        SessionTokens.Remember(hash, sub.AgentName, connection.Client, connection.WorkingDirectory, connection.TokenId);
        return Json(new JsonObject
        {
            ["session"] = token,
            ["name"] = sub.AgentName,
            ["use"] = $"Pass \"session\": \"{token}\" with every call {sub.AgentName} makes (any tool); without it, calls are this connection's own" +
                      $" ({connection.AgentName}). Keep the token to {sub.AgentName}: it's its identity here.",
        });
    }

    private McpSession NewSubSession(McpSession connection) => new()
    {
        Parent = connection,
        ClientName = connection.ClientName,
        ClientTitle = connection.ClientTitle,
        WorkingDirectory = connection.WorkingDirectory,
        Remote = connection.Remote,
        ReadOnly = connection.ReadOnly,
        Frozen = connection.Frozen,
        Projects = connection.Projects,
        BaseUrl = connection.BaseUrl,
        // Its name is its own (a token's name or the folder's isn't).
        FolderNameChecked = true,
    };

    /// <summary>
    /// (UI thread.) The subagent session a call's "session" token stands for; after a restart of ProJict or the bridge,
    /// made again under its name. Refused when the token isn't known or belongs to another connection.
    /// </summary>
    private McpSession SubSessionFor(McpSession connection, string token)
    {
        connection = connection.Parent ?? connection;
        var hash = Hash(token.Trim());
        if (_subSessions.TryGetValue(hash, out var sub))
        {
            if (sub.Parent != connection)
            {
                if (!SameConnection(sub.Parent!, connection))
                    throw new McpToolException("That session token belongs to another connection: start a session of your own with start_session.", ToolProblem.Forbidden);
                sub.Parent = connection;
            }
            Follow(sub, connection);
            return sub;
        }
        if (SessionTokens.Find(hash) is not { } known)
            throw new McpToolException("That session token isn't known here (it may have been replaced): start the session again with start_session.", ToolProblem.NotFound);
        if (!string.Equals(known.Client, connection.Client, StringComparison.OrdinalIgnoreCase) || known.TokenId != connection.TokenId ||
            (known.Folder != null && AgentFolderNames.Key("", known.Folder) != AgentFolderNames.Key("", connection.WorkingDirectory ?? "")))
            throw new McpToolException("That session token belongs to another connection: start a session of your own with start_session.", ToolProblem.Forbidden);
        lock (_live)
            if (_live.Any(s => string.Equals(s.AgentName, known.Name, StringComparison.OrdinalIgnoreCase)))
                throw new McpToolException($"{known.Name} is connected already in another session; start this one again with start_session and another name.", ToolProblem.Conflict);
        sub = NewSubSession(connection);
        sub.TokenHash = hash;
        SessionStarted(sub);
        TakeName(sub, known.Name);
        _subSessions[hash] = sub;
        SessionTokens.Remember(hash, sub.AgentName, connection.Client, connection.WorkingDirectory, connection.TokenId);
        return sub;
    }

    private static bool SameConnection(McpSession a, McpSession b) =>
        a.TokenId != null || b.TokenId != null ? a.TokenId == b.TokenId
            : string.Equals(a.Client, b.Client, StringComparison.OrdinalIgnoreCase) && a.WorkingDirectory != null && b.WorkingDirectory != null &&
              AgentFolderNames.Key("", a.WorkingDirectory) == AgentFolderNames.Key("", b.WorkingDirectory);

    /// <summary>What the connection may do now (an API token can be made read only or frozen while connected), its subagents may too.</summary>
    private static void Follow(McpSession sub, McpSession connection)
    {
        sub.ReadOnly = connection.ReadOnly;
        sub.Frozen = connection.Frozen;
        sub.BaseUrl = connection.BaseUrl;
    }

    /// <summary>(UI thread.) The connection ended: so do its subagents' sessions (their tokens keep their names for when it's back).</summary>
    private void EndSubSessions(McpSession connection)
    {
        foreach (var (hash, sub) in _subSessions.Where(s => s.Value.Parent == connection).ToList())
        {
            _subSessions.Remove(hash);
            SessionEnded(sub);
        }
    }

    /// <summary>A subagent session renamed (set_name with its token): its token goes by the new name.</summary>
    private void SubSessionRenamed(McpSession sub)
    {
        if (sub.TokenHash is { } hash && sub.Parent is { } connection)
            SessionTokens.Remember(hash, sub.AgentName, connection.Client, connection.WorkingDirectory, connection.TokenId);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// The subagent sessions' names by their token's hash (never the token), with the connection each belongs to; kept a
/// month, in sessions.json next to ProJict's other settings on this computer; without a path (tests) in memory.
/// </summary>
public sealed class SessionTokenStore(string? path)
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "sessions.json");

    public sealed record Entry(string Name, string Client, string? Folder, Guid? TokenId, DateTime At);

    private readonly Dictionary<string, Entry> _byHash = Load(path);

    public Entry? Find(string hash) => _byHash.GetValueOrDefault(hash);

    public void Remember(string hash, string name, string client, string? folder, Guid? tokenId)
    {
        _byHash[hash] = new Entry(name, client, folder, tokenId, DateTime.Now);
        Save();
    }

    public void Forget(string hash)
    {
        if (_byHash.Remove(hash)) Save();
    }

    private static Dictionary<string, Entry> Load(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return [];
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? [];
            foreach (var old in loaded.Where(e => DateTime.Now - e.Value.At > KeptFor).Select(e => e.Key).ToList()) loaded.Remove(old);
            return loaded;
        }
        catch (Exception) { return []; }
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_byHash));
        }
        catch (Exception) { /* kept in memory; written again on the next change */ }
    }
}
