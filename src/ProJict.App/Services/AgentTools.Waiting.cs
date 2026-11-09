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
        // Under the name it has now (set_name may have just picked it), what it was told before counts.
        Recall(session);
        // The bridge repeating a call after a restart: the agent never sees this answer, so nothing counts as told.
        if (session.Replaying) return result;
        var lines = SleepLines(session);
        // Asleep: nothing else, so nothing gets it going again (it all comes through once it's woken).
        if (!IsAsleep)
        {
            lines.AddRange(UserStatusLines(session));
            if (!ChatTools.Contains(tool)) lines.AddRange(ChatLines(session));
            lines.AddRange(WorkLines(session));
        }
        if (lines.Count == 0 && !IsAsleep && _main.Agents.MyStatus == ProJict.Core.Chat.UserStatus.Online) return result;
        try
        {
            if (JsonNode.Parse(result) is JsonObject json)
            {
                if (lines.Count > 0) json["waitingForYou"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray());
                MarkAsleep(json);
                return Json(json);
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>
    /// Comments, work sent back and ship requests waiting for the agent; ship requests in <paramref name="projects"/> (by
    /// default the ones it works in).
    /// </summary>
    private List<string> WorkLines(McpSession session, IEnumerable<Guid>? projects = null)
    {
        var now = DateTime.Now;
        var lines = new List<string>();
        if (session.OldBridge && BridgeInstaller.BridgePath is { } bridge && session.Noticed.Add("oldbridge"))
            lines.Add($"You reach ProJict through \"ProJict.exe --mcp\", which a ProJict update cuts off (you'd lose ProJict until your client " +
                      $"reconnects). Ask the user to add ProJict to your client again with \"{bridge}\" --mcp instead (ProJict's Settings › AI agents " +
                      "has the exact command): it stays connected through updates and keeps your name.");
        foreach (var (key, line) in PictureLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        foreach (var (key, line) in ComfyRunLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        foreach (var (key, line) in UsageLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        foreach (var (key, line) in ToolKeyLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        foreach (var (key, line) in WikiAcceptedLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        foreach (var (key, line) in ListenLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        foreach (var (key, line) in ResourceLines(session))
            if (session.Noticed.Add(key)) lines.Add(line);
        lines.AddRange(UserStatusLines(session));
        // Said to each session it's given back to (a tip, so not kept across restarts like news).
        if (session.NameGivenBack is { } given && session.Noticed.Add($"tip:name-given-back:{given}"))
            lines.Add($"You're {given} again: the name you picked in this folder before, which ProJict keeps across restarts and updates. " +
                      "If you're someone else, pick your own name with set_name.");
        if (session.RenamedBy is { } renamer && session.Noticed.Add($"renamed:{session.AgentName}"))
            lines.Add($"{renamer} renamed you {session.AgentName}: everything you do is now signed {session.AgentName}. " +
                      $"Go by that name, also in Chat, and pick it again with set_name {session.AgentName} when you come back.");
        // Also in projects shared with this ProJict, where it signs "Ada (via Alice)".
        foreach (var project in ReachableProjects)
            foreach (var loc in project.Walk())
            {
                bool Mine(string by) => OwnIn(session, project, by);
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

        // Your answers to its tests (#09a108b5), in any of this ProJict's projects.
        foreach (var project in LocalProjects)
            foreach (var (key, line) in TestLines(session, project))
                if (session.Noticed.Add(key)) lines.Add(line);

        foreach (var id in projects ?? _main.Agents.ProjectsOf(session))
        {
            if (LocalProjects.FirstOrDefault(p => p.Id == id) is not { } project) continue;
            foreach (var (key, line) in CleanupLines(session, project))
                if (session.Noticed.Add(key)) lines.Add(line);
            foreach (var (key, line) in StudioLines(session, project))
                if (session.Noticed.Add(key)) lines.Add(line);
            foreach (var (key, line) in WikiReviewLines(session, project))
                if (session.Noticed.Add(key)) lines.Add(line);
            foreach (var (key, line) in PublishLines(session, project))
                if (session.Noticed.Add(key)) lines.Add(line);
            if (project.OpenShip is { } ship)
            {
                if (ship.State == ShipState.Requested && session.Noticed.Add($"ship:{ship.Id}:requested"))
                    lines.Add($"{ship.RequestedBy} wants {project.Name} shipped ({ShipFlow.Describe(ship.Steps)}): read ship_request project={project.Name}, " +
                              "then post your plan with plan_ship and wait for approval.");
                else if (ship.State == ShipState.Approved && ship.PlannedBy is { } planner && AgentNaming.IsOwnWork(session, planner) && session.Noticed.Add($"ship:{ship.Id}:approved"))
                    lines.Add($"{ship.DecidedBy ?? "The user"} approved your ship plan for {project.Name}: run it, report_ship each step, then finish_ship.");
            }
            else if (project.Ships?.LastOrDefault() is { State: ShipState.Declined, PlannedBy: { } planner } declined && AgentNaming.IsOwnWork(session, planner) &&
                     session.Noticed.Add($"ship:{declined.Id}:declined"))
                lines.Add($"{declined.DecidedBy ?? "The user"} declined your ship plan for {project.Name}: run none of it. ship_request shows the log.");
        }
        return lines;
    }
}
