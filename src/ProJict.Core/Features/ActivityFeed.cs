using ProJict.Core.Api;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.Core.Features;

public enum FeedKind { Added, Done, History, Comment, Release, Note, Project, Ship, Api, Chat }

/// <summary>
/// Something that happened in ProJict: who did it, what they did (<see cref="Action"/>), to what (<see cref="Subject"/>),
/// and for comments the comment itself (<see cref="Detail"/>). The references lead to the thing it happened to.
/// </summary>
public sealed record FeedEvent(DateTime At, FeedKind Kind, string Who, bool Agent, string Action, string Subject, string? Detail = null)
{
    public Project? Project { get; init; }
    public Category? Category { get; init; }
    public TodoItem? Item { get; init; }
    /// <summary>The item or category a comment was posted on.</summary>
    public Guid? DiscussionTargetId { get; init; }
    /// <summary>The comment itself, to point it out in its discussion.</summary>
    public Guid? CommentId { get; init; }
    public string? Group { get; init; }
    public NoteDoc? Note { get; init; }
    public Release? Release { get; init; }
    public ShipRequest? Ship { get; init; }
    public ApiToken? Token { get; init; }
    /// <summary>A chat channel that was started.</summary>
    public Guid? ChannelId { get; init; }

    /// <summary>Everything the Feed's search looks in.</summary>
    public string SearchText => $"{Who} {Action} {Subject} {Detail} {Project?.Name} {Category?.Name} {Group}";
}

/// <summary>Builds the timeline of everything recorded in the workspace, newest first.</summary>
public static class ActivityFeed
{
    /// <param name="userName">Who did the things nothing signed (items added or finished on the board, older notes and releases).</param>
    public static List<FeedEvent> Build(IEnumerable<Project> projects, WorkspaceSettings settings, string userName)
    {
        var events = new List<FeedEvent>();
        foreach (var project in projects)
        {
            // What an import brought in isn't news; it shows as the project being imported. Anything from before the
            // (last) import came with it: imported items' creation times can be hours before the files' ImportedAt.
            var importedUntil = project.ArchivedFiles.Count > 0 ? project.ArchivedFiles.Max(a => a.ImportedAt).AddMinutes(10) : DateTime.MinValue;
            bool FromImport(DateTime at) => at <= importedUntil;

            if (!project.IsInbox)
                events.Add(new FeedEvent(project.CreatedAt, FeedKind.Project, userName, false,
                    FromImport(project.CreatedAt) ? "imported the project" : "created the project", project.Name) { Project = project });

            foreach (var loc in project.Walk())
            {
                var item = loc.Item;
                var activity = item.Activity ?? [];
                var subject = item.DisplayText();
                FeedEvent ForItem(DateTime at, FeedKind kind, string who, bool agent, string action, string? detail = null) =>
                    new(at, kind, who, agent, action, subject, detail) { Project = project, Category = loc.Category, Item = item };

                // An agent's own history already says it added or finished the item, and GitHub's that the issue was closed.
                if (item.Issue is { } issue)
                    events.Add(ForItem(item.CreatedAt, FeedKind.Added, issue.Author ?? GitHubIssues.Signature, false, $"opened issue #{issue.Number} on GitHub"));
                else if (!item.Imported && !FromImport(item.CreatedAt) && !activity.Any(a => a.Agent && a.Text.StartsWith("Added", StringComparison.Ordinal)))
                    events.Add(ForItem(item.CreatedAt, FeedKind.Added, userName, false, "added"));
                if (item.IsDone && item.DoneAt is { } doneAt && !item.Imported && !FromImport(doneAt) &&
                    !activity.Any(a => (a.Agent || a.By == GitHubIssues.Signature) && IsDoneEntry(a) && Math.Abs((a.At - doneAt).TotalMinutes) < 2))
                    events.Add(ForItem(doneAt, FeedKind.Done, userName, false, "finished"));
                foreach (var entry in activity)
                {
                    var (action, detail) = Describe(entry.Text);
                    events.Add(ForItem(entry.At, FeedKind.History, entry.By, entry.Agent, action, detail));
                }
                foreach (var (comment, _) in item.Comments.Walk().Where(e => !e.Comment.Deleted))
                    events.Add(ForItem(comment.At, FeedKind.Comment, comment.By, comment.Agent, "commented on", CommentDetail(comment)) with { DiscussionTargetId = item.Id, CommentId = comment.Id });
            }

            foreach (var category in project.Categories)
                foreach (var (comment, _) in category.Comments.Walk().Where(e => !e.Comment.Deleted))
                    events.Add(new FeedEvent(comment.At, FeedKind.Comment, comment.By, comment.Agent, "commented on", category.Name, CommentDetail(comment))
                        { Project = project, Category = category, DiscussionTargetId = category.Id, CommentId = comment.Id });

            foreach (var release in project.Releases.Where(r => !r.IsBaseline))
                events.Add(new FeedEvent(release.Date, FeedKind.Release, release.CreatedBy ?? userName, false,
                    $"released {release.Version}", string.IsNullOrWhiteSpace(release.Title) ? project.Name : release.Title.Trim()) { Project = project, Release = release });

            foreach (var request in project.Ships ?? [])
                foreach (var entry in request.Log)
                {
                    var (action, detail) = Describe(entry.Text);
                    events.Add(new FeedEvent(entry.At, FeedKind.Ship, entry.By, entry.Agent, action, $"Shipping {project.Name}", detail)
                        { Project = project, Ship = request });
                }

            foreach (var note in project.Notes.Where(n => n.UpdatedBy != null || !FromImport(n.UpdatedAt)))
                events.Add(new FeedEvent(note.UpdatedAt, FeedKind.Note, note.UpdatedBy ?? userName, false, "updated the note", note.Title)
                    { Project = project, Note = note });
        }

        foreach (var group in settings.Groups)
            foreach (var (comment, _) in settings.CommentsFor(group).Walk().Where(e => !e.Comment.Deleted))
                events.Add(new FeedEvent(comment.At, FeedKind.Comment, comment.By, comment.Agent, "commented on the group", group, CommentDetail(comment)) { Group = group, CommentId = comment.Id });

        return events.OrderByDescending(e => e.At).ToList();
    }

    /// <summary>Chat channels being started (not every message).</summary>
    public static IEnumerable<FeedEvent> Channels(IEnumerable<ProJict.Core.Chat.ChatChannel> channels, IEnumerable<Project> projects)
    {
        var byId = projects.ToDictionary(p => p.Id);
        foreach (var channel in channels.Where(c => !c.Direct))
        {
            var project = channel.Link != null ? byId.GetValueOrDefault(channel.Link.ProjectId) : null;
            var item = channel.Link?.ItemId is { } i ? project?.Find(i)?.Item : null;
            yield return new FeedEvent(channel.CreatedAt, FeedKind.Chat, channel.CreatedBy, channel.CreatedByAgent, "started the chat channel", "#" + channel.Name,
                channel.Topic ?? (item != null ? $"About “{item.DisplayText()}”" : null))
            {
                Project = project,
                Category = channel.Link?.CategoryId is { } c ? project?.Categories.FirstOrDefault(x => x.Id == c) : null,
                Item = item,
                ChannelId = channel.Id,
            };
        }
    }

    /// <summary>API tokens you made or renewed, and the ones about to expire (a week ahead) or expired.</summary>
    public static IEnumerable<FeedEvent> ApiTokens(IEnumerable<ApiToken> tokens, DateTime now, string userName)
    {
        foreach (var token in tokens)
        {
            var until = $"Until {token.ExpiresAt:yyyy-MM-dd}";
            yield return new FeedEvent(token.CreatedAt, FeedKind.Api, userName, false, "made an API token", token.Name,
                $"{(token.ReadOnly ? "Read only" : "Read and write")}, {(token.Projects == null ? "all projects" : "some projects")}. {until}.") { Token = token };
            if (token.RenewedAt is { } renewed)
                yield return new FeedEvent(renewed, FeedKind.Api, userName, false, "renewed an API token", token.Name, until + ".") { Token = token };
            if (token.IsExpired(now))
                yield return new FeedEvent(token.ExpiresAt, FeedKind.Api, "ProJict", false, "API token expired", token.Name,
                    $"On {token.ExpiresAt:yyyy-MM-dd HH:mm}. Renew it in Settings › API, or revoke it.") { Token = token };
            else if (now >= token.ExpiresAt - ApiData.ExpiryNotice)
                yield return new FeedEvent(token.ExpiresAt - ApiData.ExpiryNotice, FeedKind.Api, "ProJict", false, "API token expires soon", token.Name,
                    $"On {token.ExpiresAt:yyyy-MM-dd HH:mm}. Renew it in Settings › API before then.") { Token = token };
        }
    }

    /// <summary>The comment's text, and the files posted with it ("📎 screenshot.png").</summary>
    private static string CommentDetail(Comment comment)
    {
        if (comment.Attachments is not { Count: > 0 } files) return comment.Text;
        var names = "📎 " + string.Join(", ", files.Select(f => f.FileName));
        return comment.Text.Trim().Length == 0 ? names : $"{comment.Text} · {names}";
    }

    private static bool IsDoneEntry(ActivityEntry entry) =>
        entry.Text.StartsWith("Done", StringComparison.Ordinal) || entry.Text.StartsWith("Marked it done", StringComparison.Ordinal) ||
        entry.Text.StartsWith("Closed the issue", StringComparison.Ordinal);

    /// <summary>
    /// A history entry as it reads after the name: "Checked it out: reproducing" → "checked it out" with the detail
    /// "reproducing"; "Done: Fixed in abc1234" → "finished it" with "Fixed in abc1234".
    /// </summary>
    public static (string Action, string? Detail) Describe(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries);
        var first = lines[0];
        string? detail = null;
        var colon = first.IndexOf(": ", StringComparison.Ordinal);
        if (colon is > 0 and < 30)
        {
            detail = first[(colon + 2)..].Trim();
            first = first[..colon];
        }
        if (lines.Length > 1) detail = string.Join(" ", new[] { detail }.Concat(lines.Skip(1)).Where(l => !string.IsNullOrWhiteSpace(l)));
        var action = first.TrimEnd('.');
        if (action == "Done") action = "finished it";
        else if (action.Length > 1 && char.IsUpper(action[0]) && !char.IsUpper(action[1])) action = char.ToLowerInvariant(action[0]) + action[1..];
        return (action, string.IsNullOrWhiteSpace(detail) ? null : detail);
    }
}
