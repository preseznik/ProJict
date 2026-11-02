using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

public enum ItemState { Todo, InProgress, Done }

/// <summary>
/// How pressing an item is; None (the default) means not set. Hold parks an item on purpose: it sorts last and AI agents
/// leave it alone. Compare with Priorities.Rank, not the numbers (Hold came last).
/// </summary>
public enum Priority { None, Low, Medium, High, Urgent, Hold }

public enum ProjectStatus { Idea, Active, Paused, Released, Archived }

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    /// <summary>Prefix such as BUG / ADD / ISSUES, kept verbatim so exports round-trip ("BUG: text").</summary>
    public string? Tag { get; set; }
    public ItemState State { get; set; }
    /// <summary>Left out of the JSON while not set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Priority Priority { get; set; }
    public List<TodoItem> Children { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? DoneAt { get; set; }
    public Guid? ReleaseId { get; set; }
    /// <summary>True for items that came from an imported TXT file (their DoneAt is only the file date).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Imported { get; set; }
    /// <summary>Files attached to the item; null (and left out of the JSON) while there are none.</summary>
    public List<Attachment>? Attachments { get; set; }
    /// <summary>Big files linked where they are (captures, edits, exports), not copied; null while there are none.</summary>
    public List<MediaLink>? Media { get; set; }
    /// <summary>The AI agent working on the item while it is in progress; any state change clears it.</summary>
    public string? ClaimedBy { get; set; }
    /// <summary>Finished by an AI agent and not checked by you yet; any state change clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsReview { get; set; }
    /// <summary>What happened to the item (a claim, a resolution, a review), signed by who did it; null while empty.</summary>
    public List<ActivityEntry>? Activity { get; set; }
    /// <summary>Discussion threads about the item; null while there are none.</summary>
    public List<Comment>? Comments { get; set; }
    /// <summary>The GitHub issue the item was imported from; null for items of your own.</summary>
    public IssueLink? Issue { get; set; }
    /// <summary>Time spent on it (while it was in progress, or logged by hand) and what agents used; null while none.</summary>
    public List<WorkSpan>? Work { get; set; }

    [JsonIgnore] public bool IsDone => State == ItemState.Done;
    [JsonIgnore] public bool HasAttachments => Attachments is { Count: > 0 };

    /// <summary>
    /// Changes the state. Leaving "in progress" ends the time anyone was spending on it; going into progress with
    /// <paramref name="by"/> starts theirs.
    /// </summary>
    public void SetState(ItemState state, string? by = null, bool agent = false)
    {
        if (State == state) return;
        if (State == ItemState.InProgress) StopWork();
        if (state == ItemState.InProgress && !string.IsNullOrWhiteSpace(by)) StartWork(by, agent);
        State = state;
        DoneAt = state == ItemState.Done ? DateTime.Now : null;
        if (state != ItemState.Done) ReleaseId = null;
        ClaimedBy = null;
        NeedsReview = false;
        // From now on the dates are real, not taken from an imported file.
        Imported = false;
    }

    public TodoItem DeepClone()
    {
        var copy = (TodoItem)MemberwiseClone();
        copy.Children = Children.Select(c => c.DeepClone()).ToList();
        copy.Attachments = Attachments?.ToList();
        copy.Activity = Activity?.ToList();
        copy.Comments = Comments?.Select(c => c.DeepClone()).ToList();
        copy.Issue = Issue?.DeepClone();
        copy.Work = Work?.Select(w => w.Clone()).ToList();
        return copy;
    }

    /// <summary>Starts counting time someone spends on the item (once: if they already are, it goes on).</summary>
    public void StartWork(string by, bool agent, DateTime? at = null)
    {
        if (Work?.Any(w => w.End == null && w.Minutes == null && string.Equals(w.By, by, StringComparison.OrdinalIgnoreCase)) == true) return;
        (Work ??= []).Add(new WorkSpan { By = by, Agent = agent, Start = at ?? DateTime.Now });
    }

    /// <summary>Stops counting the time someone (or, without a name, everyone) is spending on it.</summary>
    public void StopWork(string? by = null, DateTime? at = null)
    {
        foreach (var span in Work ?? [])
            if (span.End == null && span.Minutes == null && (by == null || string.Equals(span.By, by, StringComparison.OrdinalIgnoreCase)))
                span.End = at ?? DateTime.Now;
    }

    public void AddActivity(string by, string text, bool agent) =>
        (Activity ??= []).Add(new ActivityEntry { By = by, Text = text, Agent = agent });
}

/// <summary>One line in an item's history, e.g. an agent's resolution: "Fixed in 615d691".</summary>
public sealed class ActivityEntry
{
    /// <summary>What the history says when you mark an item an agent finished as not done: you didn't accept it (yet).</summary>
    public const string SentBack = "Sent it back";

    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    /// <summary>Written by an AI agent rather than a person.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A message in a discussion; replies nest to any depth (sub-threads).</summary>
public sealed class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string By { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    /// <summary>Removed while it had replies: the text is gone, the thread below it stays.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; set; }
    /// <summary>Files and pictures posted with the comment; null (and left out of the JSON) while there are none.</summary>
    public List<Attachment>? Attachments { get; set; }
    /// <summary>Questions an agent asks with the comment, answered with a click (see CommentChoices); null while none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CommentChoice>? Choices { get; set; }
    public List<Comment>? Replies { get; set; }

    public Comment DeepClone()
    {
        var copy = (Comment)MemberwiseClone();
        copy.Attachments = Attachments?.ToList();
        copy.Choices = Choices?.Select(c => new CommentChoice { Question = c.Question, Options = [.. c.Options], Answer = c.Answer }).ToList();
        copy.Replies = Replies?.Select(r => r.DeepClone()).ToList();
        return copy;
    }
}

/// <summary>A question asked with a comment, with the options to click (the asker's suggestion first) and, once given, the answer.</summary>
public sealed class CommentChoice
{
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Answer { get; set; }
}

/// <summary>A GitHub issue an item stands for, as last seen on GitHub.</summary>
public sealed class IssueLink
{
    public int Number { get; set; }
    public string Url { get; set; } = "";
    /// <summary>Who opened the issue on GitHub.</summary>
    public string? Author { get; set; }
    /// <summary>The issue's title; while the item's text still equals it, a new title on GitHub renames the item.</summary>
    public string Title { get; set; } = "";
    /// <summary>Closed on GitHub when last synced; a change on GitHub since then is applied to the item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; set; }
    /// <summary>Pull requests that mention the issue (e.g. "Fixes #12"); null while there are none.</summary>
    public List<PullRequestLink>? PullRequests { get; set; }

    public IssueLink DeepClone()
    {
        var copy = (IssueLink)MemberwiseClone();
        copy.PullRequests = PullRequests?.Select(p => p.Clone()).ToList();
        return copy;
    }
}

public enum PullRequestState { Open, Merged, Closed }

public sealed class PullRequestLink
{
    public int Number { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public PullRequestState State { get; set; }

    public PullRequestLink Clone() => (PullRequestLink)MemberwiseClone();
}

/// <summary>A project's link to its GitHub repository's issues (Details › GitHub issues).</summary>
public sealed class IssueSync
{
    /// <summary>Sync when ProJict starts (turned on by the first import).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Finishing an item here closes its issue on GitHub (and reopening it reopens the issue); needs the GitHub CLI sign-in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CloseOnGitHub { get; set; }
    /// <summary>When the last sync started; the next one asks only for issues changed since.</summary>
    public DateTime? SyncedAt { get; set; }
    /// <summary>Issues imported so far, so one whose item you deleted doesn't come back.</summary>
    public List<int> Imported { get; set; } = [];
    /// <summary>What the last sync did or why it failed, shown under the button.</summary>
    public string? LastResult { get; set; }

    public IssueSync DeepClone()
    {
        var copy = (IssueSync)MemberwiseClone();
        copy.Imported = [.. Imported];
        return copy;
    }
}

/// <summary>Where a project shown on a guest comes from, and what the guest may do in it.</summary>
public sealed class RemoteOrigin
{
    /// <summary>Identifies the connection (the host's address).</summary>
    public string HostKey { get; init; } = "";
    /// <summary>Who shares it: the host's user name.</summary>
    public string HostName { get; init; } = "";
    public Sharing.GuestRole Role { get; set; }
    /// <summary>False while the connection is down: the project is then read only.</summary>
    public bool Connected { get; set; } = true;
}

/// <summary>A file attached to an item. ProJict keeps its own copy in the data folder (see ProjectStore.AttachmentPath).</summary>
public sealed class Attachment
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp" };

    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    /// <summary>
    /// A private conversation's file, kept in the cache for them (Settings › Chat) rather than the data folder, so it
    /// isn't mirrored or backed up; it's cleaned out after some days.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cached { get; set; }

    [JsonIgnore] public bool IsImage => ImageExtensions.Contains(Path.GetExtension(FileName));
}

public sealed class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Text after the header, e.g. "CHARACTERS - need to more prominently feature this".</summary>
    public string? Description { get; set; }
    public bool Collapsed { get; set; }
    public List<TodoItem> Items { get; set; } = [];
    /// <summary>Discussion threads about the category; null while there are none.</summary>
    public List<Comment>? Comments { get; set; }
}

public sealed class NoteDoc
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Note";
    public string Text { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    /// <summary>Who last changed the note: you or an AI agent.</summary>
    public string? UpdatedBy { get; set; }
    /// <summary>Files that go with the note (charts, PDFs, screenshots); null while there are none.</summary>
    public List<Attachment>? Attachments { get; set; }
}

public sealed class Release
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public string? Title { get; set; }
    public List<Guid> ItemIds { get; set; } = [];
    public string Markdown { get; set; } = "";
    /// <summary>Who recorded the release: you or an AI agent.</summary>
    public string? CreatedBy { get; set; }
    /// <summary>Marks everything done before import, so later patch notes only contain new work.</summary>
    public bool IsBaseline { get; set; }
}

public sealed class ArchivedFile
{
    public string FileName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTime LastWriteTime { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
}

/// <summary>The newest published release of a project's GitHub repository, as last checked.</summary>
public sealed class GitHubReleaseInfo
{
    /// <summary>The repository link that was checked; a different link means the info is out of date.</summary>
    public string Repository { get; set; } = "";
    /// <summary>Tag of the newest published release; null when the repository has none.</summary>
    public string? Tag { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? Url { get; set; }
    /// <summary>Only set when the repository has nothing but pre-releases.</summary>
    public bool Prerelease { get; set; }
    public DateTime CheckedAt { get; set; }
    /// <summary>Why the check failed (e.g. a private repository without sign-in); null when it worked.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// The project's current test build: an app, installer, folder or file to try, set by you or an agent after building.
/// A relative path is in the project's repo folder.
/// </summary>
public sealed class TestBuild
{
    public string Path { get; set; } = "";
    /// <summary>What it is or what to try, in a line ("0.1.31 with the office zoom").</summary>
    public string? Note { get; set; }
    public DateTime SetAt { get; set; } = DateTime.Now;
    public string? SetBy { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }

    /// <summary>Where it is on this computer: as given, or in the repo folder when relative.</summary>
    public string FullPath(string? repoPath) =>
        System.IO.Path.IsPathRooted(Path) || string.IsNullOrWhiteSpace(repoPath) ? Path : System.IO.Path.GetFullPath(System.IO.Path.Combine(repoPath, Path));
}

public sealed class Project
{
    public const string InboxKey = "Inbox";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>File-name stem used for exports, e.g. "MyGame" → TODO-MyGame.txt.</summary>
    public string Key { get; set; } = "";
    /// <summary>Prefix of the exported open-items file: TODO or WORK.</summary>
    public string FilePrefix { get; set; } = "TODO";
    public string Group { get; set; } = "Personal";
    /// <summary>
    /// Kept on this computer: people connected through sharing (and their agents) never see it, even when its group is
    /// shared with them. Only you change it, and undo doesn't, so undoing never shares it again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Private { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public bool IsInbox { get; set; }
    public string? RepoPath { get; set; }
    public string? GitHubUrl { get; set; }
    public string? PromptPreamble { get; set; }
    public string? Description { get; set; }
    public List<Category> Categories { get; set; } = [];
    public List<NoteDoc> Notes { get; set; } = [];
    public List<Release> Releases { get; set; } = [];
    public List<ArchivedFile> ArchivedFiles { get; set; } = [];
    /// <summary>What GitHub said about the project's repository the last time ProJict asked (not part of undo).</summary>
    public GitHubReleaseInfo? GitHubRelease { get; set; }
    /// <summary>Set once the repository's issues were imported; null for projects that don't use them.</summary>
    public IssueSync? Issues { get; set; }
    /// <summary>How the project is shipped; null until set in Details (then all steps, no instructions).</summary>
    public ShipSettings? Shipping { get; set; }
    /// <summary>The build to try now (shown on the project page with Run); null while there's none. Not part of undo.</summary>
    public TestBuild? TestBuild { get; set; }
    /// <summary>Clean-ups of the project's folder, oldest first (the last few are kept); null while there were none. Not part of undo.</summary>
    public List<CleanupRequest>? Cleanups { get; set; }
    /// <summary>The clean-up waiting for a plan or your decision: agents do no other work here meanwhile.</summary>
    [JsonIgnore] public CleanupRequest? OpenCleanup => Cleanups?.LastOrDefault(c => c.IsOpen);
    /// <summary>The project's game studio, if it has one: its roles and the agents hired to them.</summary>
    public Studio? Studio { get; set; }
    /// <summary>Ship requests, oldest first (the last few are kept); null while there were none. Not part of undo.</summary>
    public List<ShipRequest>? Ships { get; set; }
    /// <summary>Videos' publish plans, oldest first (the last few are kept); null while there were none. Not part of undo.</summary>
    public List<PublishPlan>? Publishes { get; set; }
    /// <summary>On a guest: the ProJict this project is shown from. Such projects live only in memory and are never saved here.</summary>
    [JsonIgnore] public RemoteOrigin? Remote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>The ship request in progress (asked, planned, approved or running), if any.</summary>
    [JsonIgnore] public ShipRequest? OpenShip => Ships?.LastOrDefault(s => s.IsOpen);
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public static Project Create(string name, string? group = null) => new()
    {
        Name = name.Trim(),
        Key = KeyFromName(name),
        Group = string.IsNullOrWhiteSpace(group) ? "Personal" : group.Trim(),
    };

    public static Project CreateInbox() => new()
    {
        Name = InboxKey,
        Key = InboxKey,
        Group = "",
        IsInbox = true,
        Categories = [new Category { Name = "IDEAS" }],
    };

    /// <summary>
    /// Replaces this project's content with a snapshot's (used by undo, keeps object identity). Issue links keep what
    /// GitHub last said unless <paramref name="keepIssueLinks"/> is false (a guest taking the host's version).
    /// </summary>
    public void RestoreFrom(Project snapshot, bool keepIssueLinks = true)
    {
        // Issue links say what GitHub said when last synced, which undo can't change: keep the current ones.
        var seen = this.Walk().Select(l => l.Item.Issue).OfType<IssueLink>().GroupBy(i => i.Number).ToDictionary(g => g.Key, g => g.First());

        Name = snapshot.Name;
        Key = snapshot.Key;
        FilePrefix = snapshot.FilePrefix;
        Group = snapshot.Group;
        Status = snapshot.Status;
        RepoPath = snapshot.RepoPath;
        GitHubUrl = snapshot.GitHubUrl;
        PromptPreamble = snapshot.PromptPreamble;
        Description = snapshot.Description;
        Categories = snapshot.Categories;
        Notes = snapshot.Notes;
        Releases = snapshot.Releases;
        ArchivedFiles = snapshot.ArchivedFiles;
        if (keepIssueLinks)
            foreach (var item in this.Walk().Select(l => l.Item).Where(i => i.Issue != null))
                if (seen.TryGetValue(item.Issue!.Number, out var current)) item.Issue = current.DeepClone();
        // With its items: undoing an import forgets it, so the next sync brings those issues back.
        Issues = snapshot.Issues;
        // Shipping and publishing are about commits, releases and videos that really happened (and what you approved), which undo can't take
        // back; a guest takes the host's version as it is, with everything else only the host changes. (A guest that
        // kept its own test build never matched the host's version again, and kept sending its copy back.)
        if (!keepIssueLinks)
        {
            Shipping = snapshot.Shipping;
            Ships = snapshot.Ships;
            Publishes = snapshot.Publishes;
            TestBuild = snapshot.TestBuild;
            Cleanups = snapshot.Cleanups;
            Studio = snapshot.Studio;
            GitHubRelease = snapshot.GitHubRelease;
        }
    }

    /// <summary>Keeps letters, digits, '.', '_' and '-' so the key is a valid file name.</summary>
    public static string KeyFromName(string name)
    {
        var chars = name.Trim().Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray();
        var key = new string(chars).Trim('.');
        return key.Length == 0 ? "Project" : key;
    }
}
