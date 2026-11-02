using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// The AI agents connected to ProJict and the projects each is working in: the one matching its working folder and
/// any it reads or changes. Shown as badges next to projects. Also who's online, away or offline (for Chat's members):
/// you, the agents here, and the people connected from other ProJicts with their agents. Used on the UI thread only.
/// </summary>
public sealed class AgentPresence
{
    private readonly Dictionary<McpSession, HashSet<Guid>> _projects = [];
    /// <summary>
    /// People connected from another ProJict (key: their connection), the project they have open, whether they're away,
    /// and their agents there (by the names they sign with here).
    /// </summary>
    private readonly Dictionary<object, Person> _people = [];
    private string? _presenceSignature;

    private sealed record Person(string Name, Guid? Project, bool Away = false, IReadOnlyDictionary<string, PresenceState>? Agents = null);

    /// <summary>Raised when an agent connects, disconnects, renames itself or starts working in another project.</summary>
    public event Action? Changed;
    /// <summary>Someone came online, went away or offline (also you).</summary>
    public event Action? PresenceChanged;

    /// <summary>Your name here, so you're online too.</summary>
    public string Me { get; set; } = "";
    /// <summary>How long nothing was done on this computer, as of the last <see cref="Tick"/>.</summary>
    public TimeSpan MyIdle { get; private set; }
    public PresenceState MyState => Presence.Of(true, MyIdle);

    private void Raise()
    {
        Changed?.Invoke();
        CheckPresence();
    }

    public void Connected(McpSession session, Project? folderProject)
    {
        _projects[session] = folderProject == null ? [] : [folderProject.Id];
        Raise();
    }

    public void Disconnected(McpSession session)
    {
        if (_projects.Remove(session)) Raise();
    }

    public void WorkingIn(McpSession session, Project project)
    {
        if (_projects.TryGetValue(session, out var ids) && ids.Add(project.Id)) Changed?.Invoke();
    }

    /// <summary>An agent's name changed (set_label).</summary>
    public void Renamed() => Raise();

    public void PersonConnected(object key, string name)
    {
        _people[key] = new Person(name, null);
        Raise();
    }

    public void PersonViewing(object key, Guid? projectId)
    {
        if (!_people.TryGetValue(key, out var person) || person.Project == projectId) return;
        _people[key] = person with { Project = projectId };
        Changed?.Invoke();
    }

    /// <summary>What a connected person's ProJict says: whether they're away, and their agents there.</summary>
    public void PersonPresence(object key, bool away, IReadOnlyDictionary<string, PresenceState> agents)
    {
        if (!_people.TryGetValue(key, out var person)) return;
        _people[key] = person with { Away = away, Agents = agents };
        CheckPresence();
    }

    public void PersonDisconnected(object key)
    {
        if (_people.Remove(key)) Raise();
    }

    /// <summary>Looks again at who's idle (every little while): you, by <paramref name="myIdle"/>, and the agents here.</summary>
    public void Tick(TimeSpan myIdle)
    {
        MyIdle = myIdle;
        CheckPresence();
    }

    private void CheckPresence()
    {
        var signature = Presence.Signature(Everyone());
        if (signature == _presenceSignature) return;
        _presenceSignature = signature;
        PresenceChanged?.Invoke();
    }

    /// <summary>An agent here: online while it uses its tools, away when it hasn't for a while.</summary>
    private static PresenceState StateOf(McpSession session) =>
        Presence.Of(true, DateTime.Now - (session.LastActive is { } at && at > session.ConnectedAt ? at : session.ConnectedAt));

    /// <summary>Everyone online or away here, by name: you, the agents here, and people connected from other ProJicts with their agents.</summary>
    public Dictionary<string, PresenceState> Everyone()
    {
        var everyone = new Dictionary<string, PresenceState>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, PresenceState state) =>
            everyone[name] = everyone.TryGetValue(name, out var was) ? Presence.Best(was, state) : state;
        if (Me.Length > 0) Add(Me, MyState);
        foreach (var session in _projects.Keys) Add(session.AgentName, StateOf(session));
        foreach (var person in _people.Values)
        {
            Add(person.Name, person.Away ? PresenceState.Away : PresenceState.Online);
            foreach (var (agent, state) in person.Agents ?? new Dictionary<string, PresenceState>()) Add(agent, state);
        }
        return everyone;
    }

    /// <summary>Whether someone in this ProJict's Chat is online, away or offline.</summary>
    public PresenceState StateOf(string name) => Everyone().TryGetValue(name, out var state) ? state : PresenceState.Offline;

    /// <summary>The agents connected here, online or away, by name (what this ProJict tells the ones it's connected to).</summary>
    public Dictionary<string, PresenceState> AgentsHere()
    {
        var agents = new Dictionary<string, PresenceState>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _projects.Keys.Where(s => !s.Remote))
            agents[session.AgentName] = agents.TryGetValue(session.AgentName, out var was) ? Presence.Best(was, StateOf(session)) : StateOf(session);
        return agents;
    }

    /// <summary>The connected agents (local ones and API callers), in the order they connected.</summary>
    public IReadOnlyList<McpSession> Sessions => [.. _projects.Keys];

    /// <summary>The projects an agent works in: the one for its folder and any it read or changed.</summary>
    public IReadOnlyCollection<Guid> ProjectsOf(McpSession session) => _projects.TryGetValue(session, out var ids) ? ids : [];

    /// <summary>The agents working in a project and the guests looking at it, one per name, in the order they connected.</summary>
    public IReadOnlyList<string> AgentsIn(Guid projectId) =>
        _projects.Where(kv => kv.Value.Contains(projectId)).Select(kv => kv.Key.AgentName)
            .Concat(_people.Values.Where(p => p.Project == projectId).Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
