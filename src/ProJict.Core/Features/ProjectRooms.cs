using ProJict.Core.Agents;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// A project's rooms in the Agents view, without any screen: which one shows (the one you last showed, else its first
/// open studio, else its office), its tabs and the rooms it could open, who works in which, and a studio's milestone
/// board. Only your own projects have rooms; the inbox has none to set up.
/// </summary>
public sealed class ProjectRooms
{
    /// <summary>The room you last showed of each project (null: its office), while ProJict runs.</summary>
    private readonly Dictionary<Guid, StudioKind?> _roomOf = [];

    /// <summary>The studio shown as the project's room, or null for its office.</summary>
    public Studio? Shown(Project project)
    {
        if (project.Remote != null) return null;
        var open = StudioFlow.OpenStudiosOf(project).ToList();
        if (_roomOf.TryGetValue(project.Id, out var kind))
        {
            if (kind == null && StudioFlow.HasOffice(project)) return null;
            if (open.FirstOrDefault(s => s.Kind == kind) is { } shown) return shown;
        }
        return open.FirstOrDefault();
    }

    /// <summary>Shows the project's office (null) or its studio of that kind, from now on.</summary>
    public void Show(Project project, StudioKind? kind) => _roomOf[project.Id] = kind;

    /// <summary>The room shown was closed: the project shows its first open room again.</summary>
    public void Forget(Project project) => _roomOf.Remove(project.Id);

    private static bool IsOwn(Project project) => project is { Remote: null, IsInbox: false };

    public static string RoomName(StudioKind kind) => kind == StudioKind.Film ? "Film studio" : "Game studio";

    /// <summary>One of your projects with no studio open: you can set up (or open again) a game or film studio.</summary>
    public static bool CanSetUpStudio(Project project) => IsOwn(project) && !StudioFlow.OpenStudiosOf(project).Any();

    /// <summary>One of the project's open rooms as a tab: its studio's kind (null for the office), name, whether it's shown, and what it is.</summary>
    public sealed record Tab(StudioKind? Kind, string Name, bool Selected, string ToolTip);

    /// <summary>The project's open rooms, as tabs above the room: its office, then its studios.</summary>
    public static IReadOnlyList<Tab> Tabs(Project project, Studio? shown)
    {
        if (project.Remote != null) return [];
        var tabs = new List<Tab>();
        if (StudioFlow.HasOffice(project)) tabs.Add(new Tab(null, "Office", shown == null, "Agents not hired in the project's studios work here"));
        foreach (var studio in StudioFlow.OpenStudiosOf(project).OrderBy(s => s.Kind))
            tabs.Add(new Tab(studio.Kind, RoomName(studio.Kind), studio == shown, $"The {StudioFlow.KindName(studio.Kind)}: a station per role"));
        return tabs;
    }

    /// <summary>Closing the room shown (while the project has another): what happens.</summary>
    public static string CloseToolTip(Studio? shown) => shown != null
        ? $"Close the {StudioFlow.KindName(shown.Kind)}: its roles and hires are kept for when you open it again"
        : "Close the office: agents not hired in the studios wait in a studio's lobby instead";

    /// <summary>A room the project could open: "Office", "Game" or "Film", with its name and what it is.</summary>
    public sealed record Choice(string Room, string Name, string ToolTip);

    /// <summary>The rooms the project could open: its office (while closed), and a studio of each kind it hasn't open.</summary>
    public static IReadOnlyList<Choice> ToOpen(Project project)
    {
        if (!IsOwn(project)) return [];
        var rooms = new List<Choice>();
        if (!StudioFlow.HasOffice(project))
            rooms.Add(new Choice("Office", "Office", "Open the office again: agents not hired in the studios work there"));
        foreach (var kind in new[] { StudioKind.Game, StudioKind.Film })
        {
            var studio = StudioFlow.StudioOf(project, kind);
            if (studio is { Closed: false }) continue;
            rooms.Add(new Choice(kind.ToString(), CanSetUpStudio(project) ? kind.ToString() : RoomName(kind),
                studio != null
                    ? $"Open the {StudioFlow.KindName(kind)} again, with its roles and hires"
                    : kind == StudioKind.Film
                        ? "A film studio for this project: a station per role (director, writer, capture, editor, sound, thumbnails, publisher, producer); you hire agents to them"
                        : "A game studio for this project: a station per role (designers, programmer, artist, sound, QA…); you hire agents to them"));
        }
        return rooms;
    }

    /// <summary>"Set up a studio:" while the project has never had one, else "Open:".</summary>
    public static string OpenLabel(Project project) => CanSetUpStudio(project) && !StudioFlow.StudiosOf(project).Any() ? "Set up a studio:" : "Open:";

    /// <summary>
    /// Who of the agents shown works in the room shown, and where the others are. With all projects (no project), the
    /// office leaves out those hired in an open studio of a project they work in. In a project with an office and
    /// studios, its office holds the agents not hired in them, and a studio those hired to it.
    /// </summary>
    public static (IReadOnlyList<McpSession> Here, string? Elsewhere) Split(IReadOnlyList<McpSession> agents, Project? project, Studio? shown,
                                                                          Func<McpSession, Project?> studioOf)
    {
        if (project == null)
        {
            var inStudios = agents.Where(s => studioOf(s) != null).ToList();
            return ([.. agents.Except(inStudios)], inStudios.Count == 0 ? null
                : "In studios: " + string.Join("; ", inStudios.GroupBy(s => studioOf(s)!.Name).Select(g => $"{string.Join(", ", g.Select(s => s.AgentName))} ({g.Key})")));
        }
        if (project.Remote != null || !StudioFlow.HasOffice(project) || !StudioFlow.OpenStudiosOf(project).Any()) return (agents, null);
        if (shown != null) return ([.. agents.Where(s => StudioFlow.HireOf(shown, s.AgentName) != null)], null);
        var hired = agents.Where(s => StudioFlow.HiredIn(project, s.AgentName) != null).ToList();
        return ([.. agents.Except(hired)], hired.Count == 0 ? null
            : "In the studios: " + string.Join("; ", hired.GroupBy(s => StudioFlow.HiredIn(project, s.AgentName)!.Kind)
                .Select(g => $"{string.Join(", ", g.Select(s => s.AgentName))} ({StudioFlow.KindName(g.Key)})")));
    }

    /// <summary>A category with items, as a milestone: how many of its items are done.</summary>
    public sealed record Milestone(Category Category, int Done, int Total);

    /// <summary>
    /// A studio's milestone board: its categories with items as milestones, the unfinished first (in the board's order),
    /// five at most; and how many of all the project's items are done.
    /// </summary>
    public static (IReadOnlyList<Milestone> Rows, int Done, int Total) Milestones(Project project)
    {
        var categories = project.Categories.Select(c => new Milestone(c, c.Walk().Count(l => l.Item.IsDone), c.Walk().Count())).Where(m => m.Total > 0).ToList();
        var rows = categories.Where(m => m.Done < m.Total).Concat(categories.Where(m => m.Done == m.Total)).Take(5).ToList();
        return (rows, categories.Sum(m => m.Done), categories.Sum(m => m.Total));
    }
}
