using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// A project's game studio, for agents: the user hires them to its roles (in the Agents view). An agent is told its role
/// and what the role expects when it's hired, swapped or let go, and current_project and list_projects repeat it, with who
/// has the other roles; agents not hired yet wait in the lobby and work as usual.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>How long an agent still hears that it's no longer in a role (if it connects later).</summary>
    private static readonly TimeSpan StudioEndKeptFor = TimeSpan.FromDays(2);

    /// <summary>The project with the agent's place in its studio, if it has one.</summary>
    private static JsonObject WithStudio(JsonObject json, Project project, McpSession session)
    {
        if (project.Studio is not { } studio || project.Remote != null) return json;
        var role = StudioFlow.RoleOf(studio, session.AgentName);
        var team = studio.Roles.Select(r => (JsonNode)$"{r.Name}: {StudioFlow.HolderOf(studio, r) ?? "nobody yet"}").ToArray();
        json["studio"] = role != null
            ? new JsonObject
            {
                ["yourRole"] = role.Name,
                ["whatItExpects"] = role.Definition,
                ["team"] = new JsonArray(team),
                ["note"] = $"You're the {role.Name} in {project.Name}'s game studio: work as the role expects, and leave what belongs to the other roles to them.",
            }
            : new JsonObject
            {
                ["yourRole"] = null,
                ["team"] = new JsonArray(team),
                ["note"] = $"{project.Name} has a game studio, and you're in its lobby: the user hasn't hired you to a role. Work as usual meanwhile.",
            };
        return json;
    }

    /// <summary>For waitingForYou and the watcher: the agent's role in a project's studio, with what it expects, or that it lost it.</summary>
    private static IEnumerable<(string Key, string Line)> StudioLines(McpSession session, Project project)
    {
        if (project.Studio is not { } studio) yield break;
        if (StudioFlow.HireOf(studio, session.AgentName) is { } hire && studio.Roles.FirstOrDefault(r => r.Id == hire.RoleId) is { } role)
        {
            var who = hire.At >= session.ConnectedAt
                ? $"{hire.By} hired you as {role.Name} in {project.Name}'s game studio."
                : $"You're the {role.Name} in {project.Name}'s game studio (hired by {hire.By}, {hire.At:yyyy-MM-dd}).";
            yield return ($"studio:{project.Id}:{hire.RoleId}:{hire.At.Ticks}",
                $"{who} What the role expects: {role.Definition} Work as it expects; current_project shows it again and who has the other roles.");
        }
        else if (studio.Ended?.LastOrDefault(e => string.Equals(e.Agent, session.AgentName, StringComparison.OrdinalIgnoreCase)) is { } ended &&
                 DateTime.Now - ended.At < StudioEndKeptFor)
        {
            var name = studio.Roles.FirstOrDefault(r => r.Id == ended.RoleId)?.Name ?? "your";
            yield return ($"studio-ended:{project.Id}:{ended.RoleId}:{ended.At.Ticks}",
                $"{ended.By} took you off the {name} role in {project.Name}'s game studio: you're back in its lobby, without a role, until you're hired again.");
        }
    }
}
