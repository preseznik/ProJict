using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// A project's studios (a game studio, a film studio, or both), for agents: the user hires them to its roles (in the Agents view). An
/// agent is told its role and what the role expects when it's hired, swapped or let go, and current_project and list_projects repeat
/// it, with who has the other roles; agents not hired yet stay in the office (or wait in the lobby while it's closed) and work as usual.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>How long an agent still hears that it's no longer in a role (if it connects later).</summary>
    private static readonly TimeSpan StudioEndKeptFor = TimeSpan.FromDays(2);

    /// <summary>The studio's channel as an agent names it ("#grid2vr"), if it has one.</summary>
    private string? StudioChannelName(Studio studio) =>
        studio.ChannelId is { } id && Chat.Channels.FirstOrDefault(c => c.Id == id) is { } channel ? "#" + channel.Name : null;

    /// <summary>How a team works together, in a hire's news and current_project.</summary>
    private static string Teamwork(string? channel) =>
        $"Talk with the team in {channel ?? "the project's channel"}: say what you're good at (your model and tools: code, images, sound, video, tests…), " +
        "volunteer for the items that fit you, and agree who takes what before starting; hand over what suits someone else better.";

    /// <summary>The project with the agent's place in its open studios, if it has any: "studio", or "studios" when it has two.</summary>
    private JsonObject WithStudio(JsonObject json, Project project, McpSession session)
    {
        if (project.Remote != null) return json;
        var studios = StudioFlow.OpenStudiosOf(project).Select(s => StudioJson(project, s, session)).ToList();
        if (studios.Count == 1) json["studio"] = studios[0];
        else if (studios.Count > 1) json["studios"] = new JsonArray([.. studios]);
        return json;
    }

    /// <summary>The agent's place in one of the project's studios: its role and the team, or that it isn't hired there.</summary>
    private JsonObject StudioJson(Project project, Studio studio, McpSession session)
    {
        var channel = StudioChannelName(studio);
        var role = StudioFlow.RoleOf(studio, session.AgentName);
        var title = StudioFlow.TitleOf(studio, session.AgentName);
        var senior = role != null ? StudioFlow.HolderOf(studio, role) : null;
        var junior = StudioFlow.HireOf(studio, session.AgentName)?.Junior == true;
        var json = role != null
            ? new JsonObject
            {
                ["kind"] = StudioFlow.KindName(studio.Kind),
                ["yourRole"] = title,
                ["whatItExpects"] = role.Definition,
                ["team"] = Team(studio),
                ["channel"] = channel,
                ["note"] = (junior
                               ? $"You're a Junior {role.Name} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}, under {senior}, its senior: agree with {senior} which of the role's work you take. "
                               : $"You're the {title} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}: work as the role expects" +
                                 (StudioFlow.JuniorsOf(studio, role) is { Count: > 0 } juniors ? $", and share its work with your juniors ({string.Join(", ", juniors)})" : "") + ". ") +
                           "Leave what belongs to the other roles to them. " + Teamwork(channel),
            }
            : new JsonObject
            {
                ["kind"] = StudioFlow.KindName(studio.Kind),
                ["yourRole"] = null,
                ["team"] = Team(studio),
                ["channel"] = channel,
                ["note"] = $"{project.Name} has a {StudioFlow.KindName(studio.Kind)}, and you're {(StudioFlow.HasOffice(project) ? "in the office" : "in its lobby")}: " +
                           "you aren't hired to a role there. Work as usual meanwhile.",
            };
        if (LeadReview.IsLead(studio, session.AgentName))
        {
            // The lead hires: who isn't hired here yet, and how.
            json["lobby"] = new JsonArray([.. Lobby(project, studio).Select(s => (JsonNode)new JsonObject
            {
                ["agent"] = s.AgentName,
                ["client"] = s.Client,
                ["doing"] = s.Status ?? s.Label ?? s.Recent.LastOrDefault()?.Text,
            })]);
            json["hiring"] = "As the studio's lead you hire: talk with the agents not hired here yet (lobby) first (post_message to @name: what they're " +
                             "good at, their model and tools), then hire_to_role with the role, as its senior (the one there leaves the role) or as a " +
                             "junior under it. The user is told, and can change it.";
        }
        return json;
    }

    /// <summary>Each role and who has it: its senior, and any juniors.</summary>
    private static JsonArray Team(Studio studio) => new([.. studio.Roles.Select(r =>
    {
        var holder = StudioFlow.HolderOf(studio, r);
        var juniors = StudioFlow.JuniorsOf(studio, r);
        return (JsonNode)$"{r.Name}: {holder ?? "nobody yet"}{(juniors.Count > 0 ? $", with junior{(juniors.Count > 1 ? "s" : "")} {string.Join(", ", juniors)}" : "")}";
    })]);

    /// <summary>The agents connected now that work in the project and have no role in its studio.</summary>
    private IEnumerable<McpSession> Lobby(Project project, Studio studio) =>
        _main.Agents.Sessions.Where(s => s.Name != null && StudioFlow.HireOf(studio, s.AgentName) == null && _main.Agents.ProjectsOf(s).Contains(project.Id))
            .GroupBy(s => s.AgentName, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

    private void AddStudioTools()
    {
        Add("hire_to_role", "Hire to a studio role",
            "For a studio's lead (Lead designer, or Director in a film studio): hires an agent to a role in the studio, after you've talked with " +
            "it (current_project lists who waits in the lobby). As the role's senior (the one there goes back to the lobby) or as a junior under " +
            "it. It's told what the role expects and invited to the studio's channel; the user is told and can change it.",
            Schema(ProjectArg, ("agent", "string", "The agent to hire, by name; it must be connected.", true),
                ("role", "string", "The role, by name (e.g. Programmer).", true),
                ("as", "enum:senior|junior", "Senior (the default) takes the role over; junior joins under its senior.", false),
                ("studio", "enum:game|film", "Which studio, when the project has both and you lead both.", false)),
            HireToRole);
    }

    private string HireToRole(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var open = project.Remote == null ? StudioFlow.OpenStudiosOf(project).ToList() : [];
        if (open.Count == 0) throw new McpToolException($"{project.Name} has no studio.");
        var me = session.AgentName;
        var kind = args.String("studio") switch
        {
            null => (StudioKind?)null,
            "game" => StudioKind.Game,
            "film" => StudioKind.Film,
            _ => throw new McpToolException("studio must be game or film."),
        };
        if (kind != null && open.All(s => s.Kind != kind))
            throw new McpToolException($"{project.Name} has no {StudioFlow.KindName(kind.Value)} open; it has a {string.Join(" and a ", open.Select(s => StudioFlow.KindName(s.Kind)))}.");
        var led = open.Where(s => (kind == null || s.Kind == kind) && LeadReview.IsLead(s, me)).ToList();
        if (led.Count == 0)
            throw new McpToolException("Only a studio's lead or the user hires: " +
                                       string.Join("; ", open.Where(s => kind == null || s.Kind == kind).Select(s => $"the {StudioFlow.KindName(s.Kind)}'s is {LeadReview.LeadOf(s) ?? "nobody yet"}")) + ". Ask them.");
        if (led.Count > 1) throw new McpToolException("You lead both studios: say which with studio=game or studio=film.");
        var studio = led[0];
        var agent = args.Required("agent").Trim().TrimStart('@');
        var roleName = args.Required("role").Trim();
        var role = studio.Roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new McpToolException($"No role {roleName} here; the roles are {string.Join(", ", studio.Roles.Select(r => r.Name))}.");
        var junior = args.String("as") switch
        {
            null or "senior" => false,
            "junior" => true,
            _ => throw new McpToolException("as must be senior or junior."),
        };
        if (string.Equals(agent, me, StringComparison.OrdinalIgnoreCase)) throw new McpToolException("You can't hire yourself: ask the user.");
        if (role == LeadReview.LeadRole(studio) && !junior) throw new McpToolException("That's your role: you can hire a junior to it, not someone in your place.");
        if (!_main.Agents.Sessions.Any(s => string.Equals(s.AgentName, agent, StringComparison.OrdinalIgnoreCase)))
            throw new McpToolException($"{agent} isn't connected: talk with an agent from the lobby first (current_project lists them).");

        string? replaced;
        try { replaced = StudioFlow.Hire(studio, agent, role, me, junior); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        // The team talks in the studio's channel: the new hire is invited.
        try
        {
            var channel = StudioFlow.ChannelFor(project, studio, Chat, _state.ChatMe);
            Chat.Invite(channel, _state.ChatMe, agent, true);
        }
        catch (ProJict.Core.Chat.ChatException) { }
        _state.MarkDirty(project);
        _main.GetProjectViewModel(project).RefreshStudio();
        _main.AgentActivity.RefreshStudioView();
        var title = StudioFlow.TitleOf(studio, agent) ?? role.Name;
        var back = StudioFlow.HasOffice(project) ? "in the office" : "in the lobby";
        _state.StatusText = $"{me} hired {agent} as {title} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}" +
                            (replaced != null ? $", instead of {replaced}, who's back {back}." : ".");
        return Json(new JsonObject
        {
            ["hired"] = agent,
            ["as"] = title,
            ["replaced"] = replaced,
            ["note"] = $"{agent} is told what the role expects and invited to the studio's channel" + (replaced != null ? $"; {replaced} is back {back}" : "") + ".",
            ["team"] = Team(studio),
        });
    }

    /// <summary>For waitingForYou and the watcher: the agent's role in a project's open studios, with what it expects, or that it lost it.</summary>
    private IEnumerable<(string Key, string Line)> StudioLines(McpSession session, Project project) =>
        StudioFlow.OpenStudiosOf(project).SelectMany(studio => StudioLines(session, project, studio));

    private IEnumerable<(string Key, string Line)> StudioLines(McpSession session, Project project, Studio studio)
    {
        if (StudioFlow.HireOf(studio, session.AgentName) is { } hire && studio.Roles.FirstOrDefault(r => r.Id == hire.RoleId) is { } role)
        {
            var title = StudioFlow.TitleOf(studio, session.AgentName) ?? role.Name;
            var under = hire.Junior && StudioFlow.HolderOf(studio, role) is { } senior ? $" under {senior}, its senior" : "";
            var who = hire.At >= session.ConnectedAt
                ? $"{hire.By} hired you as {title} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}{under}."
                : $"You're the {title} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}{under} (hired by {hire.By}, {hire.At:yyyy-MM-dd}).";
            var channel = StudioChannelName(studio);
            yield return ($"studio:{project.Id}:{hire.RoleId}:{hire.Junior}:{hire.At.Ticks}",
                $"{who} What the role expects: {role.Definition} Work as it expects; current_project shows it again and who has the other roles. " +
                (channel != null ? $"You're invited to {channel}, the studio's channel: join it. " : "") + Teamwork(channel));
        }
        else if (studio.Ended?.LastOrDefault(e => string.Equals(e.Agent, session.AgentName, StringComparison.OrdinalIgnoreCase)) is { } ended &&
                 DateTime.Now - ended.At < StudioEndKeptFor)
        {
            var name = studio.Roles.FirstOrDefault(r => r.Id == ended.RoleId)?.Name ?? "your";
            yield return ($"studio-ended:{project.Id}:{ended.RoleId}:{ended.At.Ticks}",
                $"{ended.By} took you off the {name} role in {project.Name}'s {StudioFlow.KindName(studio.Kind)}: you're back {(StudioFlow.HasOffice(project) ? "in the office" : "in its lobby")}, without a role, until you're hired again.");
        }
    }
}
