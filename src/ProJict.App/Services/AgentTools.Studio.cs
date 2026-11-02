using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// A project's studio (a game or film studio), for agents: the user hires them to its roles (in the Agents view). An agent is told its role
/// and what the role expects when it's hired, swapped or let go, and current_project and list_projects repeat it, with who
/// has the other roles; agents not hired yet wait in the lobby and work as usual.
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

    /// <summary>The project with the agent's place in its studio, if it has one.</summary>
    private JsonObject WithStudio(JsonObject json, Project project, McpSession session)
    {
        if (project.Studio is not { } studio || project.Remote != null) return json;
        var channel = StudioChannelName(studio);
        var role = StudioFlow.RoleOf(studio, session.AgentName);
        var title = StudioFlow.TitleOf(studio, session.AgentName);
        var senior = role != null ? StudioFlow.HolderOf(studio, role) : null;
        var junior = StudioFlow.HireOf(studio, session.AgentName)?.Junior == true;
        json["studio"] = role != null
            ? new JsonObject
            {
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
                ["yourRole"] = null,
                ["team"] = Team(studio),
                ["channel"] = channel,
                ["note"] = $"{project.Name} has a {StudioFlow.KindName(studio.Kind)}, and you're in its lobby: the user hasn't hired you to a role. Work as usual meanwhile.",
            };
        if (LeadReview.IsLead(studio, session.AgentName) && json["studio"] is JsonObject lead)
        {
            // The lead hires: who waits in the lobby, and how.
            lead["lobby"] = new JsonArray([.. Lobby(project, studio).Select(s => (JsonNode)new JsonObject
            {
                ["agent"] = s.AgentName,
                ["client"] = s.Client,
                ["doing"] = s.Status ?? s.Label ?? s.Recent.LastOrDefault()?.Text,
            })]);
            lead["hiring"] = "As the studio's lead you hire: talk with the agents in the lobby first (post_message to @name: what they're good at, " +
                             "their model and tools), then hire_to_role with the role, as its senior (the one there goes back to the lobby) or as a junior " +
                             "under it. The user is told, and can change it.";
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
                ("as", "enum:senior|junior", "Senior (the default) takes the role over; junior joins under its senior.", false)),
            HireToRole);
    }

    private string HireToRole(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        if (project.Studio is not { } studio || project.Remote != null) throw new McpToolException($"{project.Name} has no studio.");
        var me = session.AgentName;
        if (!LeadReview.IsLead(studio, me))
            throw new McpToolException($"Only the studio's lead ({LeadReview.LeadOf(studio) ?? "nobody yet"}) or the user hires. Ask them.");
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
        _state.StatusText = $"{me} hired {agent} as {title} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}" +
                            (replaced != null ? $", instead of {replaced}, who's back in the lobby." : ".");
        return Json(new JsonObject
        {
            ["hired"] = agent,
            ["as"] = title,
            ["replaced"] = replaced,
            ["note"] = $"{agent} is told what the role expects and invited to the studio's channel" + (replaced != null ? $"; {replaced} is back in the lobby" : "") + ".",
            ["team"] = Team(studio),
        });
    }

    /// <summary>For waitingForYou and the watcher: the agent's role in a project's studio, with what it expects, or that it lost it.</summary>
    private IEnumerable<(string Key, string Line)> StudioLines(McpSession session, Project project)
    {
        if (project.Studio is not { } studio) yield break;
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
                $"{ended.By} took you off the {name} role in {project.Name}'s {StudioFlow.KindName(studio.Kind)}: you're back in its lobby, without a role, until you're hired again.");
        }
    }
}
