using System.Text.Json;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// What waits for an agent, added to its tool results as "waitingForYou" (each thing once per session): chat invites,
/// @mentions and new messages, the user's comments on items it worked on, work the user sent back, and ship requests
/// in its project. So an agent notices them while it works, without having to look.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Comments on items an agent worked on count this long.</summary>
    private static readonly TimeSpan CommentsKeptFor = TimeSpan.FromDays(14);

    private string WithWaiting(McpSession session, string tool, string result)
    {
        var lines = new List<string>();
        if (!ChatTools.Contains(tool)) lines.AddRange(ChatLines(session));
        lines.AddRange(WorkLines(session));
        if (lines.Count == 0) return result;
        try
        {
            if (JsonNode.Parse(result) is JsonObject json)
            {
                json["waitingForYou"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray());
                return Json(json);
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>Comments, work sent back and ship requests waiting for the agent.</summary>
    private List<string> WorkLines(McpSession session)
    {
        bool Mine(string by) => AgentNaming.IsOwnWork(session, by);
        var now = DateTime.Now;
        var lines = new List<string>();
        foreach (var project in LocalProjects)
            foreach (var loc in project.Walk())
            {
                var item = loc.Item;
                // When the agent last did something on the item: its history or its comments.
                DateTime? mine = null;
                foreach (var entry in item.Activity ?? [])
                    if (entry.Agent && Mine(entry.By) && (mine == null || entry.At > mine)) mine = entry.At;
                foreach (var (comment, _) in item.Comments.Walk())
                    if (comment.Agent && Mine(comment.By) && (mine == null || comment.At > mine)) mine = comment.At;
                if (mine == null) continue;
                var what = $"“{Short(item.DisplayText())}” (#{ShortId(item.Id)}) in {project.Name}";

                var comments = item.Comments.Walk().Select(e => e.Comment)
                    .Where(c => !c.Agent && !c.Deleted && c.At > mine && now - c.At < CommentsKeptFor).ToList();
                if (comments.Count > 0 && session.Noticed.Add($"comments:{item.Id}:{comments.Max(c => c.At).Ticks}"))
                    lines.Add($"{string.Join(", ", comments.Select(c => c.By).Distinct(StringComparer.OrdinalIgnoreCase))} commented on {what} after you " +
                              $"worked on it: read it with list_comments item={ShortId(item.Id)} and answer with add_comment.");

                var finished = (item.Activity ?? []).Any(a => a.Agent && Mine(a.By) && a.Text.StartsWith("Done:", StringComparison.Ordinal));
                if (finished && !item.IsDone && (item.Activity ?? []).LastOrDefault(a => !a.Agent && a.Text.StartsWith(ActivityEntry.SentBack, StringComparison.Ordinal)) is { } back &&
                    back.At > mine && session.Noticed.Add($"sentback:{item.Id}:{back.At.Ticks}"))
                    lines.Add($"{back.By} sent {what} back: your work wasn't accepted yet. get_item shows why (its discussion and history); " +
                              "claim_item to work on it again, or add_comment to ask.");
            }

        foreach (var id in _main.Agents.ProjectsOf(session))
        {
            if (LocalProjects.FirstOrDefault(p => p.Id == id) is not { OpenShip: { } ship } project) continue;
            if (ship.State == ShipState.Requested && session.Noticed.Add($"ship:{ship.Id}:requested"))
                lines.Add($"{ship.RequestedBy} wants {project.Name} shipped ({ShipFlow.Describe(ship.Steps)}): read ship_request project={project.Name}, " +
                          "then post your plan with plan_ship and wait for approval.");
            else if (ship.State == ShipState.Approved && ship.PlannedBy is { } planner && Mine(planner) && session.Noticed.Add($"ship:{ship.Id}:approved"))
                lines.Add($"{ship.DecidedBy ?? "The user"} approved your ship plan for {project.Name}: run it, report_ship each step, then finish_ship.");
        }
        return lines;
    }
}
