using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// The publish plan's rules. An AI agent posts a plan for a video (and may replace it until you decide); you pick a
/// thumbnail, may edit the text, and approve or decline; only an approved plan is published, by the agent reporting
/// the result. Agents can't decide. Every change is signed in the plan's log. Breaking a rule throws
/// InvalidOperationException with the reason.
/// </summary>
public static class PublishFlow
{
    /// <summary>Plans kept per project (the oldest finished ones go).</summary>
    public const int Kept = 30;
    /// <summary>Thumbnails a plan offers at most (2–4 is what you choose from).</summary>
    public const int MaxThumbnails = 4;

    public sealed record Text(string Title, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Playlists, string? When);

    /// <summary>
    /// An agent's plan for a video: a new one, or <paramref name="replacing"/> (its own or another agent's, while it still
    /// waits for your decision). Replacing it clears the thumbnail you picked and your edits.
    /// </summary>
    public static PublishPlan Plan(Project project, Text text, IReadOnlyList<Attachment> thumbnails, Guid? itemId, string? platform, string? note,
        string agent, PublishPlan? replacing = null)
    {
        if (string.IsNullOrWhiteSpace(text.Title)) throw new InvalidOperationException("Give the video a title.");
        if (thumbnails.Count > MaxThumbnails) throw new InvalidOperationException($"Offer at most {MaxThumbnails} thumbnails (2–4 to choose from).");
        if (replacing is { State: not PublishState.Planned })
            throw new InvalidOperationException($"That plan is {StateName(replacing.State)}: post a new one instead.");
        var plan = replacing ?? new PublishPlan();
        Apply(plan, text);
        plan.Thumbnails = [.. thumbnails];
        plan.Chosen = thumbnails.Count == 1 ? 0 : null;
        plan.ItemId = itemId ?? plan.ItemId;
        plan.Platform = string.IsNullOrWhiteSpace(platform) ? plan.Platform : platform.Trim();
        plan.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        plan.PlannedBy = agent;
        plan.PlannedAt = DateTime.Now;
        plan.EditedByYou = false;
        plan.State = PublishState.Planned;
        Log(plan, agent, true, replacing == null ? $"Planned it: {Count(thumbnails.Count)}" : $"Replaced the plan: {Count(thumbnails.Count)}");
        if (replacing == null)
        {
            var plans = project.Publishes ??= [];
            plans.Add(plan);
            // Old finished plans go; open ones always stay.
            while (plans.Count > Kept && plans.FindIndex(p => !p.IsOpen) is >= 0 and var oldest) plans.RemoveAt(oldest);
        }
        return plan;
    }

    private static string Count(int thumbnails) => thumbnails switch
    {
        0 => "no thumbnails.",
        1 => "one thumbnail.",
        _ => $"{thumbnails} thumbnails to choose from.",
    };

    /// <summary>You pick a thumbnail, while the plan waits for your decision.</summary>
    public static void Choose(PublishPlan plan, int index, string by)
    {
        RequirePlanned(plan);
        if (index < 0 || index >= plan.Thumbnails.Count) throw new InvalidOperationException("There's no such thumbnail.");
        if (plan.Chosen == index) return;
        plan.Chosen = index;
        Log(plan, by, false, $"Picked thumbnail {index + 1}.");
    }

    /// <summary>You change the text before deciding; the agent then publishes yours.</summary>
    public static void Edit(PublishPlan plan, Text text, string by)
    {
        RequirePlanned(plan);
        if (string.IsNullOrWhiteSpace(text.Title)) throw new InvalidOperationException("The title can't be empty.");
        var before = (plan.Title, plan.Description, string.Join("\n", plan.Tags), string.Join("\n", plan.Playlists), plan.When);
        Apply(plan, text);
        if (before == (plan.Title, plan.Description, string.Join("\n", plan.Tags), string.Join("\n", plan.Playlists), plan.When)) return;
        // One line for a stretch of editing, not one per keystroke.
        if (!plan.EditedByYou || plan.Log.LastOrDefault() is not { Text: "Edited the text." } last || last.By != by) Log(plan, by, false, "Edited the text.");
        plan.EditedByYou = true;
    }

    /// <summary>Your answer. Only a person gives it; approving needs a thumbnail picked when there's a choice.</summary>
    public static void Decide(PublishPlan plan, bool approve, string by)
    {
        RequirePlanned(plan);
        if (approve && plan.Thumbnails.Count > 0 && plan.ChosenThumbnail == null) throw new InvalidOperationException("Pick a thumbnail first.");
        plan.State = approve ? PublishState.Approved : PublishState.Declined;
        plan.DecidedBy = by;
        if (!approve) plan.FinishedAt = DateTime.Now;
        Log(plan, by, false, approve ? $"Approved it{(plan.ChosenThumbnail != null ? $", with thumbnail {plan.Chosen + 1}" : "")}." : "Declined it.");
    }

    /// <summary>The end of an approved plan, by the agent: it's out (with its link), or it isn't.</summary>
    public static void Finish(PublishPlan plan, bool published, string? url, string summary, string agent)
    {
        if (plan.State != PublishState.Approved)
            throw new InvalidOperationException(plan.State == PublishState.Planned
                ? "The plan waits for the user's decision: publish nothing yet." : $"The plan is {StateName(plan.State)}.");
        plan.State = published ? PublishState.Published : PublishState.Failed;
        plan.Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        plan.FinishedAt = DateTime.Now;
        var text = published ? "Published it" + (plan.Url is { } u ? $": {u}" : "") : "Couldn't publish it";
        Log(plan, agent, true, string.IsNullOrWhiteSpace(summary) ? text + "." : $"{text}. {summary.Trim()}");
    }

    /// <summary>You take the plan back, before it's out.</summary>
    public static void Withdraw(PublishPlan plan, string by)
    {
        if (!plan.IsOpen) throw new InvalidOperationException($"The plan is {StateName(plan.State)}.");
        plan.State = PublishState.Withdrawn;
        plan.FinishedAt = DateTime.Now;
        Log(plan, by, false, "Withdrew it.");
    }

    public static string StateName(PublishState state) => state switch
    {
        PublishState.Planned => "waiting for your decision",
        PublishState.Approved => "approved",
        PublishState.Published => "published",
        PublishState.Failed => "not published",
        PublishState.Declined => "declined",
        _ => "withdrawn",
    };

    /// <summary>Tags and playlists as written: comma or line separated, trimmed, without repeats.</summary>
    public static List<string> Split(string? text) => (text ?? "").Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Apply(PublishPlan plan, Text text)
    {
        plan.Title = text.Title.Trim();
        plan.Description = text.Description.Trim();
        plan.Tags = Split(string.Join(",", text.Tags));
        plan.Playlists = Split(string.Join("\n", text.Playlists));
        plan.When = string.IsNullOrWhiteSpace(text.When) ? null : text.When.Trim();
    }

    private static void RequirePlanned(PublishPlan plan)
    {
        if (plan.State != PublishState.Planned) throw new InvalidOperationException($"The plan is {StateName(plan.State)}.");
    }

    private static void Log(PublishPlan plan, string by, bool agent, string text) =>
        plan.Log.Add(new ActivityEntry { By = by, Agent = agent, Text = text });
}
