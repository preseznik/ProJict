using System.Globalization;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.Core.Agents;

/// <summary>
/// What the Agents view knows about agents, without any screen: who has been here and when, what each has checked out,
/// said lately and is doing now, and how long ago ("12 s ago").
/// </summary>
public static class AgentRoster
{
    /// <summary>An agent counts as busy this long after its last call, or after setting its status.</summary>
    public static readonly TimeSpan BusyFor = TimeSpan.FromSeconds(30);
    /// <summary>An agent's own status counts this long; then what it last did shows instead.</summary>
    public static readonly TimeSpan StatusFor = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The agents that signed work, comments or chat messages in your projects and channels, or picked a name here, and
    /// when each was last seen; DateTime.MinValue for those only known (being invited to a channel isn't being here).
    /// Older numbered and labelled names count as their agent's.
    /// </summary>
    public static Dictionary<string, DateTime> Seen(IEnumerable<Project> projects, IEnumerable<ChatChannel> channels,
                                                    IReadOnlyDictionary<string, string> registry, IEnumerable<McpSession> live)
    {
        var clients = registry.Values.Concat(live.Select(s => s.Client)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        void Saw(string by, DateTime at)
        {
            var name = AgentNaming.Identity(by);
            foreach (var client in clients) name = AgentNaming.Unnumbered(name, client);
            if (name.Length > 0 && (!seen.TryGetValue(name, out var last) || at > last)) seen[name] = at;
        }
        foreach (var project in projects.Where(p => p.Remote == null))
        {
            foreach (var loc in project.Walk())
            {
                foreach (var entry in loc.Item.Activity ?? []) if (entry.Agent) Saw(entry.By, entry.At);
                foreach (var (comment, _) in loc.Item.Comments.Walk()) if (comment.Agent) Saw(comment.By, comment.At);
            }
            foreach (var category in project.Categories)
                foreach (var (comment, _) in category.Comments.Walk()) if (comment.Agent) Saw(comment.By, comment.At);
        }
        foreach (var channel in channels)
        {
            foreach (var message in channel.Messages) if (message.Agent && !message.Notice) Saw(message.By, message.At);
            foreach (var member in channel.Members) if (member.Agent) Saw(member.Name, DateTime.MinValue);
        }
        foreach (var name in registry.Keys) seen.TryAdd(name, DateTime.MinValue);
        return seen;
    }

    /// <summary>
    /// The projects the Agents view offers, by name: your own, without those you've hidden in the sidebar, except the one
    /// shown (so the list never goes blank under it).
    /// </summary>
    public static IReadOnlyList<string> ProjectChoices(IEnumerable<Project> projects, ICollection<Guid> hidden, Project? shown) =>
        [.. projects.Where(p => p.Remote == null && (!hidden.Contains(p.Id) || p == shown)).Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Items the agent has checked out right now, in your projects (or only <paramref name="only"/>).</summary>
    public static IReadOnlyList<(Project Project, TodoItem Item)> CheckedOut(IEnumerable<Project> projects, McpSession session, Project? only) =>
        projects.Where(p => p.Remote == null && (only == null || p == only))
            .SelectMany(p => p.Walk().Select(l => (Project: p, l.Item)))
            .Where(x => x.Item.State == ItemState.InProgress && session.Claimed.Contains(x.Item.Id) &&
                        string.Equals(x.Item.ClaimedBy, session.AgentName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>The agent's latest message in Chat since it connected (for its speech bubble at the cooler).</summary>
    public static string? LastSaid(IEnumerable<ChatChannel> channels, McpSession session)
    {
        ChatMessage? last = null;
        foreach (var channel in channels)
            for (var i = channel.Messages.Count - 1; i >= 0; i--)
            {
                var m = channel.Messages[i];
                if (m.At < session.ConnectedAt || (last != null && m.At <= last.At)) break;
                if (m.Agent && !m.Deleted && !m.Notice && string.Equals(m.By, session.AgentName, StringComparison.OrdinalIgnoreCase))
                {
                    last = m;
                    break;
                }
            }
        return last?.Text;
    }

    /// <summary>The agent's latest actions, in the project shown (with all projects, all of them), oldest first.</summary>
    public static IEnumerable<AgentAction> ActionsIn(McpSession session, Guid? only) =>
        session.Recent.Where(a => only == null || a.ProjectId == null || a.ProjectId == only);

    /// <summary>What an agent is doing now, as its card says, and since when; busy while it acted or said so in the last half minute.</summary>
    public sealed record Doing(string Text, DateTime At, bool Busy);

    /// <summary>Its own words while they're fresh, else what it last did (in the project shown), else that it's only connected.</summary>
    public static Doing DoingNow(McpSession session, Guid? only, DateTime now)
    {
        var last = ActionsIn(session, only).LastOrDefault();
        var statusFresh = session.Status != null && session.StatusAt is { } since && now - since < StatusFor;
        var (text, at) = statusFresh ? (session.Status!, session.StatusAt!.Value)
            : last != null ? (last.Times > 1 ? $"{last.Text} ×{last.Times}" : last.Text, last.At)
            : ("Connected, nothing done yet", session.ConnectedAt);
        var busy = now - (session.LastActive ?? session.ConnectedAt) < BusyFor || (statusFresh && now - session.StatusAt!.Value < BusyFor);
        return new Doing(text, at, busy);
    }

    /// <summary>
    /// "Programmer · Claude Code · login fix · watching Chat · In GRID2VR · since 22:41": its role in the room shown, what
    /// it runs in (when it has a name of its own), its label, and where it works since when.
    /// </summary>
    public static string About(McpSession session, string? role, bool watching, IReadOnlyList<string> projects, DateTime today, CultureInfo culture)
    {
        var since = session.ConnectedAt.ToString(session.ConnectedAt.Date == today ? "HH:mm" : "d MMM HH:mm", culture);
        var about = new[] { role, session.Name != null ? session.Client : null, session.Label, watching ? "watching Chat" : null }.Where(s => s != null);
        return string.Join(" · ", about.Append((projects.Count == 0 ? "Connected" : "In " + string.Join(", ", projects)) + " · since " + since));
    }

    /// <summary>For a project's badges in the sidebar: who is there and what each did last.</summary>
    public static string ProjectToolTip(IReadOnlyList<McpSession> sessions, Guid projectId, DateTime now, CultureInfo culture)
    {
        if (sessions.Count == 0) return "";
        var lines = sessions.Select(s =>
        {
            var last = s.Recent.LastOrDefault(a => a.ProjectId == projectId) ?? s.Recent.LastOrDefault();
            var doing = s.Status is { } status && s.StatusAt is { } since && now - since < StatusFor ? status
                : last != null ? $"{last.Text} ({Age(last.At, now, culture)})" : "connected, nothing done yet";
            return $"{s.FullName}: {doing}";
        });
        return string.Join("\n", lines) + "\nRight-click › Agents shows more.";
    }

    /// <summary>"now", "12 s ago", "3 min ago", "10:42", "2 Nov 10:42".</summary>
    public static string Age(DateTime at, DateTime now, CultureInfo culture)
    {
        var age = now - at;
        if (age < TimeSpan.FromSeconds(5)) return "now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds} s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        return at.Date == now.Date ? at.ToString("HH:mm", culture) : at.ToString("d MMM HH:mm", culture);
    }
}
