using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Videos' publish plans, for agents (a film studio's Publisher): posting one with thumbnails to choose from, reading the
/// user's decision (the thumbnail they picked, the text as they left it) and reporting the result. Nothing is published
/// before the user approves; see PublishFlow.
/// </summary>
public sealed partial class AgentTools
{
    private void AddPublishTools()
    {
        Add("plan_publish", "Plan publishing a video",
            "Posts a publish plan for a video: the title, description, tags, playlists, when it goes out and 2–4 thumbnails to choose from (image " +
            "files). The user picks a thumbnail, may edit the text, and approves or declines it in ProJict; publish nothing before it's approved. Give " +
            "plan to replace one of yours that still waits for their decision.",
            Schema(ProjectArg, ("title", "string", "The video's title.", true), ("description", "string", "Its description.", true),
                ("tags", "string[]", "Tags.", false), ("playlists", "string[]", "Playlists to add it to.", false),
                ("when", "string", "When it goes out, e.g. now, 2026-11-06 18:00.", false),
                ("thumbnails", "string[]", "Thumbnail images to choose from, 2–4 (absolute or relative to your working directory).", false),
                ("item", "string", "The video's item id, if it has one.", false), ("platform", "string", "Where it's published (default YouTube).", false),
                ("note", "string", "Anything the user should know: what's still missing, what you'll do once it's approved.", false),
                ("plan", "string", "The id of your plan to replace.", false)),
            PlanPublish, filesArgument: "thumbnails");
        Add("publish_plan", "Publish plan",
            "A video's publish plan: its state, the text as it is now (the user may have edited it), the thumbnails and the one the user picked, and " +
            "what to do next. Without plan: the project's open plans and the latest finished ones.",
            Schema(ProjectArg, ("plan", "string", "The plan's id.", false)), PublishPlanInfo, readOnly: true);
        Add("finish_publish", "Finish publishing",
            "Ends an approved publish plan: published (with its link), or failed with the reason.",
            Schema(ProjectArg, ("plan", "string", "The plan's id.", true), ("outcome", "enum:published|failed", "Whether it's out.", true),
                ("url", "string", "Where it's published.", false), ("summary", "string", "A sentence or two for the user.", true)),
            FinishPublish);
    }

    private string PlanPublish(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        Guid? itemId = null;
        if (args.String("item") is { } itemArg)
        {
            var (itemProject, loc) = FindItem(session, itemArg);
            if (itemProject != project) throw new McpToolException($"That item is in {itemProject.Name}, not {project.Name}.");
            itemId = loc.Item.Id;
        }
        var replacing = args.String("plan") is { } id ? FindPublish(project, id) : null;
        var text = new PublishFlow.Text(args.Required("title"), args.Required("description"), args.Strings("tags"), args.Strings("playlists"), args.String("when"));
        PublishPlan? plan = null;
        ChangePublish(project, () => plan = PublishFlow.Plan(project, text, files, itemId, args.String("platform"), args.String("note"), session.AgentName, replacing));
        _state.StatusText = $"{session.AgentName} planned publishing “{Short(plan!.Title)}”: it waits for your decision.";
        return Json(new JsonObject
        {
            ["plan"] = ShortId(plan.Id),
            ["state"] = Name(plan.State),
            ["thumbnails"] = Paths(plan.Thumbnails, project),
            ["next"] = "Wait for the user's decision: they pick a thumbnail, may edit the text, and approve or decline (it comes in waitingForYou, and " +
                       "publish_plan shows it). Publish nothing before that." +
                       (plan.Thumbnails.Count is > 0 and < 2 ? " Offering 2–4 thumbnails gives them a choice." : ""),
        });
    }

    private string PublishPlanInfo(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        if (args.String("plan") is { } id) return Json(PublishJson(project, FindPublish(project, id), full: true));
        var plans = project.Publishes ?? [];
        return Json(new JsonObject
        {
            ["project"] = project.Name,
            ["open"] = new JsonArray(plans.Where(p => p.IsOpen).Select(p => (JsonNode)PublishJson(project, p, full: false)).ToArray()),
            ["finished"] = new JsonArray(plans.Where(p => !p.IsOpen).TakeLast(5).Select(p => (JsonNode)PublishJson(project, p, full: false)).ToArray()),
            ["next"] = "publish_plan with plan=<id> shows one in full. Post a new one with plan_publish.",
        });
    }

    private string FinishPublish(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        var plan = FindPublish(project, args.Required("plan"));
        var published = args.Required("outcome").Trim().ToLowerInvariant() switch
        {
            "published" => true,
            "failed" => false,
            _ => throw new McpToolException("outcome must be published or failed."),
        };
        ChangePublish(project, () => PublishFlow.Finish(plan, published, args.String("url"), args.Required("summary"), session.AgentName));
        _state.StatusText = published ? $"{session.AgentName} published “{Short(plan.Title)}”." : $"{session.AgentName} couldn't publish “{Short(plan.Title)}”.";
        return Json(new JsonObject { ["plan"] = ShortId(plan.Id), ["state"] = Name(plan.State) });
    }

    private JsonObject PublishJson(Project project, PublishPlan plan, bool full)
    {
        var json = new JsonObject
        {
            ["plan"] = ShortId(plan.Id),
            ["title"] = plan.Title,
            ["state"] = Name(plan.State),
            ["platform"] = plan.Platform,
            ["plannedBy"] = plan.PlannedBy,
            ["plannedAt"] = plan.PlannedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
        if (plan.ItemId is { } itemId && project.Find(itemId) is { } loc) json["item"] = new JsonObject { ["id"] = ShortId(itemId), ["text"] = Short(loc.Item.Text) };
        if (plan.DecidedBy != null) json["decidedBy"] = plan.DecidedBy;
        if (plan.Url != null) json["url"] = plan.Url;
        if (!full) return json;
        json["description"] = plan.Description;
        json["tags"] = new JsonArray(plan.Tags.Select(t => (JsonNode)t).ToArray());
        json["playlists"] = new JsonArray(plan.Playlists.Select(p => (JsonNode)p).ToArray());
        if (plan.When != null) json["when"] = plan.When;
        if (plan.Note != null) json["note"] = plan.Note;
        json["thumbnails"] = Paths(plan.Thumbnails, project);
        if (plan.ChosenThumbnail is { } chosen)
            json["chosenThumbnail"] = new JsonObject { ["number"] = plan.Chosen + 1, ["path"] = _state.AttachmentPath(project, chosen) };
        if (plan.EditedByYou) json["editedByUser"] = true;
        json["log"] = new JsonArray(plan.Log.Select(e => (JsonNode)$"{e.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {e.By}: {e.Text}").ToArray());
        json["next"] = plan.State switch
        {
            PublishState.Planned => "It waits for the user's decision in ProJict: publish nothing yet. You can still replace it with plan_publish plan=<id>.",
            PublishState.Approved => "Approved: publish it exactly as it is now (the user's text" + (plan.ChosenThumbnail != null ? " and the chosen thumbnail" : "") +
                                     "), with the user's own sign-in or as a package they upload, then finish_publish with the link.",
            _ => "It's finished: nothing to do.",
        };
        return json;
    }

    private static string Name(PublishState state) => state.ToString().ToLowerInvariant();

    private static PublishPlan FindPublish(Project project, string id)
    {
        var key = id.Trim().TrimStart('#').ToLowerInvariant();
        return (project.Publishes ?? []).LastOrDefault(p => p.Id.ToString("N").StartsWith(key, StringComparison.Ordinal) || p.Id.ToString().StartsWith(key, StringComparison.Ordinal))
            ?? throw new McpToolException($"{project.Name} has no publish plan {id}: publish_plan lists them.", ToolProblem.NotFound);
    }

    private void ChangePublish(Project project, Action change)
    {
        try { _main.GetProjectViewModel(project).ChangePublish(change); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message, ToolProblem.Conflict); }
    }

    /// <summary>For waitingForYou and the watcher: the user's decision on the agent's publish plans.</summary>
    private IEnumerable<(string Key, string Line)> PublishLines(McpSession session, Project project)
    {
        foreach (var plan in project.Publishes ?? [])
        {
            if (!AgentNaming.IsOwnWork(session, plan.PlannedBy)) continue;
            var who = plan.DecidedBy ?? "The user";
            var what = $"your publish plan “{Short(plan.Title)}” in {project.Name}";
            switch (plan.State)
            {
                case PublishState.Approved:
                    yield return ($"publish:{plan.Id}:approved",
                        $"{who} approved {what}" + (plan.ChosenThumbnail != null ? $" with thumbnail {plan.Chosen + 1}" : "") +
                        (plan.EditedByYou ? " and changed its text" : "") +
                        $": read it with publish_plan plan={ShortId(plan.Id)} and publish it as it is now, then finish_publish.");
                    break;
                case PublishState.Declined:
                    yield return ($"publish:{plan.Id}:declined", $"{who} declined {what}: publish none of it. publish_plan plan={ShortId(plan.Id)} shows the log.");
                    break;
                case PublishState.Withdrawn:
                    yield return ($"publish:{plan.Id}:withdrawn", $"{plan.Log.LastOrDefault()?.By ?? "The user"} withdrew {what}: publish none of it.");
                    break;
            }
        }
    }
}
