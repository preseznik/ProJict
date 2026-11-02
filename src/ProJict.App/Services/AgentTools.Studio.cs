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
        var team = studio.Roles.Select(r => (JsonNode)$"{r.Name}: {StudioFlow.HolderOf(studio, r) ?? "nobody yet"}").ToArray();
        json["studio"] = role != null
            ? new JsonObject
            {
                ["yourRole"] = role.Name,
                ["whatItExpects"] = role.Definition,
                ["team"] = new JsonArray(team),
                ["channel"] = channel,
                ["note"] = $"You're the {role.Name} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}: work as the role expects, and leave what belongs to the other roles to them. " +
                           Teamwork(channel),
            }
            : new JsonObject
            {
                ["yourRole"] = null,
                ["team"] = new JsonArray(team),
                ["channel"] = channel,
                ["note"] = $"{project.Name} has a {StudioFlow.KindName(studio.Kind)}, and you're in its lobby: the user hasn't hired you to a role. Work as usual meanwhile.",
            };
        return json;
    }

    /// <summary>For waitingForYou and the watcher: the agent's role in a project's studio, with what it expects, or that it lost it.</summary>
    private IEnumerable<(string Key, string Line)> StudioLines(McpSession session, Project project)
    {
        if (project.Studio is not { } studio) yield break;
        if (StudioFlow.HireOf(studio, session.AgentName) is { } hire && studio.Roles.FirstOrDefault(r => r.Id == hire.RoleId) is { } role)
        {
            var who = hire.At >= session.ConnectedAt
                ? $"{hire.By} hired you as {role.Name} in {project.Name}'s {StudioFlow.KindName(studio.Kind)}."
                : $"You're the {role.Name} in {project.Name}'s {StudioFlow.KindName(studio.Kind)} (hired by {hire.By}, {hire.At:yyyy-MM-dd}).";
            var channel = StudioChannelName(studio);
            yield return ($"studio:{project.Id}:{hire.RoleId}:{hire.At.Ticks}",
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
