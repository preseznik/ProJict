using ProJict.Core.Model;

namespace ProJict.Core.Wiki;

public enum WikiSaveOutcome { Unchanged, Saved, Merged, Clashed }

/// <summary>What a save did: a new version, one merged with someone else's edit, nothing (the same text), or a clash.</summary>
public sealed record WikiSaveResult(WikiSaveOutcome Outcome, WikiPage Page, WikiClash? Clash = null)
{
    public string Describe() => Outcome switch
    {
        WikiSaveOutcome.Unchanged => "Nothing changed.",
        WikiSaveOutcome.Saved => $"Saved as version {Page.Version}.",
        WikiSaveOutcome.Merged => $"Someone else changed the page meanwhile; merged with their edit as version {Page.Version}.",
        _ => $"Someone else changed the same paragraphs meanwhile, so both versions are kept: the page shows theirs, and yours waits beside it for {(Clash?.Agent == true ? "the user" : "you")} to pick.",
    };
}

/// <summary>Something the wiki doesn't allow, in words for the person or agent who tried.</summary>
public sealed class WikiException(string message) : InvalidOperationException(message);

/// <summary>
/// The workspace's wiki: its pages and what may happen to them. Every save is a version (who and when); saving on top
/// of an older version merges by paragraph with what changed since, and a real clash keeps both. Pages AI agents change
/// wait for the owner's review (or a studio lead's, see <see cref="Features.LeadReview"/>). Each change raises
/// <see cref="Changed"/> (for saving, sharing and the views).
/// </summary>
public sealed class WikiBook
{
    /// <summary>Older versions go beyond this many (the first one stays).</summary>
    public const int KeptVersions = 200;
    public const int MaxText = 1_000_000;

    private readonly Func<string> _ownerName;
    private readonly Func<DateTime> _clock;

    public WikiBook(List<WikiPage> pages, Func<string> ownerName, Func<DateTime>? clock = null)
    {
        Pages = pages;
        _ownerName = ownerName;
        _clock = clock ?? (() => DateTime.Now);
    }

    public List<WikiPage> Pages { get; }

    /// <summary>A page was added or changed: save it and update what shows it.</summary>
    public event Action<WikiPage>? Changed;
    /// <summary>A page was deleted (or, in a copy of another ProJict's wiki, taken away).</summary>
    public event Action<WikiPage>? Removed;

    private DateTime Now => _clock();

    private bool IsOwner(WikiAuthor by) => !by.Agent && string.Equals(by.Name, _ownerName(), StringComparison.OrdinalIgnoreCase);

    // ───────────── Finding pages ─────────────

    /// <summary>
    /// The page a name points to: its whole path ("GRID2VR/Architecture/Renderer"), its id (8 characters are enough), or
    /// its title when only one page has it. Null when none or several match.
    /// </summary>
    public WikiPage? Find(string key)
    {
        var matches = FindAll(key);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The pages a name could mean, best first: an exact path or id, else every page with that title.</summary>
    public List<WikiPage> FindAll(string key)
    {
        var path = WikiPaths.Key(key);
        if (path.Length == 0) return [];
        if (Pages.FirstOrDefault(p => WikiPaths.Key(p.Path) == path) is { } exact) return [exact];
        var trimmed = key.Trim().TrimStart('#');
        if (trimmed.Length >= 8 && trimmed.All(char.IsAsciiHexDigit) || Guid.TryParse(trimmed, out _))
            if (Pages.Where(p => p.Id.ToString().StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } byId) return byId;
        return Pages.Where(p => WikiPaths.Key(p.Title) == path).ToList();
    }

    /// <summary>
    /// The page a [[link]] on <paramref name="from"/> points to: a whole path, a page next to it in its folder, or a
    /// page with that title (the closest one to <paramref name="from"/> when several have it).
    /// </summary>
    public WikiPage? Resolve(string link, WikiPage? from = null)
    {
        var key = WikiPaths.Key(link);
        if (key.Length == 0) return null;
        if (Pages.FirstOrDefault(p => WikiPaths.Key(p.Path) == key) is { } exact) return exact;
        if (from != null && from.Folder.Length > 0 &&
            Pages.FirstOrDefault(p => WikiPaths.Key(p.Path) == WikiPaths.Key(from.Folder + "/" + link)) is { } sibling) return sibling;
        var titled = Pages.Where(p => WikiPaths.Key(p.Title) == key).ToList();
        if (titled.Count <= 1 || from == null) return titled.FirstOrDefault();
        // The one sharing the most folders with the page the link is on.
        return titled.OrderByDescending(p => SharedDepth(p.Path, from.Path)).ThenBy(p => p.Path.Length).First();
    }

    private static int SharedDepth(string a, string b)
    {
        var x = WikiPaths.Parts(WikiPaths.Key(a));
        var y = WikiPaths.Parts(WikiPaths.Key(b));
        var n = 0;
        while (n < x.Length - 1 && n < y.Length - 1 && x[n] == y[n]) n++;
        return n;
    }

    public bool IsTaken(string path, WikiPage? except = null) =>
        Pages.Any(p => p != except && WikiPaths.Key(p.Path) == WikiPaths.Key(path));

    /// <summary>Why a page can't go at that path (a bad name, or another page is there); null when it can.</summary>
    public string? PathProblem(string path, WikiPage? except = null) =>
        WikiPaths.Problem(path) ?? (IsTaken(path, except) ? $"There is already a page “{WikiPaths.Display(WikiPaths.Normalize(path))}”." : null);

    /// <summary>The folders in the tree (every folder some page is in), sorted.</summary>
    public IReadOnlyList<string> Folders() =>
        Pages.SelectMany(p =>
            {
                var parts = WikiPaths.Parts(p.Path);
                return Enumerable.Range(1, Math.Max(0, parts.Length - 1)).Select(n => string.Join('/', parts.Take(n)));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public IEnumerable<WikiPage> LinkedTo(Guid projectId) => Pages.Where(p => p.ProjectId == projectId).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase);

    // ───────────── Writing ─────────────

    public WikiPage Create(string path, string text, WikiAuthor by, Guid? projectId = null, bool sharedWithEveryone = false, string? note = null)
    {
        path = WikiPaths.Normalize(path);
        if (PathProblem(path) is { } problem) throw new WikiException(problem);
        CheckSize(text);
        var now = Now;
        var page = new WikiPage
        {
            Path = path, Text = text, ProjectId = projectId, SharedWithEveryone = projectId == null && sharedWithEveryone,
            CreatedAt = now, CreatedBy = by.Name, UpdatedAt = now, UpdatedBy = by.Name, UpdatedByAgent = by.Agent,
            Versions = [new WikiVersion { Number = 1, At = now, By = by.Name, Agent = by.Agent, Text = text, Note = note }],
        };
        if (by.Agent)
        {
            page.NeedsReview = true;
            page.ReviewedVersion = 0;
        }
        Pages.Add(page);
        Changed?.Invoke(page);
        return page;
    }

    /// <summary>
    /// Saves new text for a page that was read at <paramref name="baseVersion"/> (0: on top of whatever it is now). When
    /// it changed since, the two edits are merged by paragraph; if they changed the same paragraphs, the page keeps the
    /// other save and this one is kept beside it as a <see cref="WikiClash"/>.
    /// </summary>
    public WikiSaveResult Save(WikiPage page, string text, int baseVersion, WikiAuthor by, string? note = null)
    {
        CheckSize(text);
        if (text == page.Text) return new WikiSaveResult(WikiSaveOutcome.Unchanged, page);
        if (baseVersion <= 0 || baseVersion == page.Version)
        {
            AddVersion(page, text, by, note);
            return new WikiSaveResult(WikiSaveOutcome.Saved, page);
        }
        var start = page.VersionAt(baseVersion);
        var merge = start != null ? TextMerge.Merge(start.Text, text, page.Text) : new MergeResult(false, page.Text, 1);
        if (merge.Clean)
        {
            if (merge.Text == page.Text) return new WikiSaveResult(WikiSaveOutcome.Unchanged, page);
            var other = page.UpdatedBy ?? "someone";
            AddVersion(page, merge.Text, by, note ?? $"Merged with {other}'s edit (version {page.Version})");
            return new WikiSaveResult(WikiSaveOutcome.Merged, page);
        }
        var clash = new WikiClash { At = Now, By = by.Name, Agent = by.Agent, Text = text, BaseVersion = baseVersion, AgainstVersion = page.Version };
        (page.Clashes ??= []).Add(clash);
        if (by.Agent) MarkForReview(page);
        Changed?.Invoke(page);
        return new WikiSaveResult(WikiSaveOutcome.Clashed, page, clash);
    }

    private void AddVersion(WikiPage page, string text, WikiAuthor by, string? note)
    {
        var now = Now;
        if (by.Agent) MarkForReview(page);
        else if (IsOwner(by))
        {
            // You wrote it yourself, so you've seen what's there.
            page.NeedsReview = false;
            page.ReviewedVersion = null;
        }
        page.Versions.Add(new WikiVersion { Number = page.Version + 1, At = now, By = by.Name, Agent = by.Agent, Text = text, Note = note });
        if (page.Versions.Count > KeptVersions) page.Versions.RemoveRange(1, page.Versions.Count - KeptVersions);
        page.Text = text;
        page.UpdatedAt = now;
        page.UpdatedBy = by.Name;
        page.UpdatedByAgent = by.Agent;
        Changed?.Invoke(page);
    }

    private static void MarkForReview(WikiPage page)
    {
        if (!page.NeedsReview) page.ReviewedVersion = page.Version;
        page.NeedsReview = true;
    }

    private static void CheckSize(string text)
    {
        if (text.Length > MaxText) throw new WikiException($"A page holds at most {MaxText:N0} characters.");
    }

    /// <summary>A version's text becomes the newest version again.</summary>
    public WikiSaveResult Restore(WikiPage page, int number, WikiAuthor by)
    {
        var version = page.VersionAt(number) ?? throw new WikiException($"The page has no version {number} (any more).");
        if (version.Text == page.Text) return new WikiSaveResult(WikiSaveOutcome.Unchanged, page);
        AddVersion(page, version.Text, by, $"Restored version {number}");
        return new WikiSaveResult(WikiSaveOutcome.Saved, page);
    }

    /// <summary>
    /// Settles a clash: <paramref name="take"/> makes the clashing save the page's text (a new version), otherwise it's
    /// dropped and the page stays as it is.
    /// </summary>
    public void ResolveClash(WikiPage page, Guid clashId, bool take, WikiAuthor by)
    {
        var clash = page.Clashes?.FirstOrDefault(c => c.Id == clashId) ?? throw new WikiException("That clash was settled already.");
        page.Clashes!.Remove(clash);
        if (page.Clashes.Count == 0) page.Clashes = null;
        if (take && clash.Text != page.Text) AddVersion(page, clash.Text, by, $"Took {clash.By}'s clashing edit");
        else Changed?.Invoke(page);
    }

    /// <summary>Moves (or renames) a page; its versions go with it.</summary>
    public void Move(WikiPage page, string path)
    {
        path = WikiPaths.Normalize(path);
        if (WikiPaths.Key(path) == WikiPaths.Key(page.Path) && path == page.Path) return;
        if (PathProblem(path, page) is { } problem) throw new WikiException(problem);
        page.Path = path;
        Changed?.Invoke(page);
    }

    /// <summary>Renames a folder: every page in it (and below) moves along. Returns how many moved.</summary>
    public int MoveFolder(string folder, string to)
    {
        folder = WikiPaths.Normalize(folder);
        to = WikiPaths.Normalize(to);
        if (WikiPaths.Problem(to) is { } problem) throw new WikiException(problem);
        var moving = Pages.Where(p => WikiPaths.IsIn(p.Path, folder) && !string.Equals(p.Path, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        var targets = moving.ToDictionary(p => p, p => to + p.Path[folder.Length..]);
        foreach (var (page, target) in targets)
            if (WikiPaths.Problem(target) is { } bad) throw new WikiException(bad);
            else if (Pages.Except(moving).Any(p => WikiPaths.Key(p.Path) == WikiPaths.Key(target)))
                throw new WikiException($"There is already a page “{WikiPaths.Display(target)}”.");
        foreach (var (page, target) in targets)
        {
            page.Path = target;
            Changed?.Invoke(page);
        }
        return moving.Count;
    }

    public void Delete(WikiPage page)
    {
        if (!Pages.Remove(page)) return;
        Removed?.Invoke(page);
    }

    /// <summary>Links the page to a project (it then shows there and is shared as the project is), or unlinks it.</summary>
    public void LinkProject(WikiPage page, Guid? projectId)
    {
        if (page.ProjectId == projectId) return;
        page.ProjectId = projectId;
        if (projectId != null) page.SharedWithEveryone = false;
        Changed?.Invoke(page);
    }

    /// <summary>A page without a project: shared with everyone you share with, or kept on this computer.</summary>
    public void SetShared(WikiPage page, bool shared)
    {
        if (page.ProjectId != null) throw new WikiException("A page linked to a project is shared as the project is.");
        if (page.SharedWithEveryone == shared) return;
        page.SharedWithEveryone = shared;
        Changed?.Invoke(page);
    }

    /// <summary>You've checked what agents changed: noted, so the agents who wrote it hear that you accepted it.</summary>
    public void Accept(WikiPage page, string? by = null)
    {
        if (!page.NeedsReview) return;
        page.LastAccepted = new WikiAcceptance { By = by ?? "", Version = page.Version, Since = page.ReviewedVersion ?? 0 };
        page.NeedsReview = false;
        page.ReviewedVersion = null;
        Changed?.Invoke(page);
    }

    /// <summary>Reviews kept per page (the oldest go).</summary>
    public const int KeptReviews = 100;

    /// <summary>
    /// Someone who reviews for you (a studio's lead) accepted what changed since you last checked: the review mark goes,
    /// and the page's history says who accepted it.
    /// </summary>
    public void AcceptFor(WikiPage page, WikiAuthor by, string? role, string? note = null)
    {
        if (!page.NeedsReview) throw new WikiException("The page doesn't wait for a review: nothing changed since it was last checked.");
        if (page.HasClashes) throw new WikiException("Two edits of the page clashed: the user picks which to keep.");
        AddReview(page, by, role, WikiReviewOutcome.Accepted, page.ReviewedVersion ?? 0, note);
        page.NeedsReview = false;
        page.ReviewedVersion = null;
        Changed?.Invoke(page);
    }

    /// <summary>They send what changed back, with a note: the page keeps waiting for a review (yours too).</summary>
    public void SendBack(WikiPage page, WikiAuthor by, string? role, string note)
    {
        if (!page.NeedsReview) throw new WikiException("The page doesn't wait for a review: nothing changed since it was last checked.");
        AddReview(page, by, role, WikiReviewOutcome.SentBack, page.ReviewedVersion ?? 0, note);
        Changed?.Invoke(page);
    }

    /// <summary>You don't go along with what the lead accepted: the page waits for a review again, of the same changes.</summary>
    public void Reopen(WikiPage page, WikiAuthor by, string? note = null)
    {
        if (page.AcceptedReview is not { } accepted) throw new WikiException("Nobody accepted the page as it is now.");
        page.NeedsReview = true;
        page.ReviewedVersion = accepted.Since;
        AddReview(page, by, null, WikiReviewOutcome.Reopened, accepted.Since, note);
        Changed?.Invoke(page);
    }

    private void AddReview(WikiPage page, WikiAuthor by, string? role, WikiReviewOutcome outcome, int since, string? note)
    {
        (page.Reviews ??= []).Add(new WikiReview
        {
            At = Now, By = by.Name, Agent = by.Agent, Role = role, Outcome = outcome, Version = page.Version, Since = since,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        });
        if (page.Reviews.Count > KeptReviews) page.Reviews.RemoveRange(0, page.Reviews.Count - KeptReviews);
    }

    public void Attach(WikiPage page, IEnumerable<Attachment> files)
    {
        var added = false;
        foreach (var file in files)
        {
            (page.Attachments ??= []).Add(file);
            added = true;
        }
        if (added) Changed?.Invoke(page);
    }

    public void RemoveAttachment(WikiPage page, Attachment file)
    {
        if (page.Attachments?.Remove(file) != true) return;
        if (page.Attachments.Count == 0) page.Attachments = null;
        Changed?.Invoke(page);
    }

    // ───────────── Mirrors ─────────────

    /// <summary>For a copy of another ProJict's wiki: takes the host's version of a page, into the same object when there is one.</summary>
    public WikiPage Upsert(WikiPage incoming)
    {
        var existing = Pages.FirstOrDefault(p => p.Id == incoming.Id);
        if (existing == null) Pages.Add(existing = incoming);
        else
        {
            existing.Path = incoming.Path;
            existing.Text = incoming.Text;
            existing.ProjectId = incoming.ProjectId;
            existing.SharedWithEveryone = incoming.SharedWithEveryone;
            existing.CreatedAt = incoming.CreatedAt;
            existing.CreatedBy = incoming.CreatedBy;
            existing.UpdatedAt = incoming.UpdatedAt;
            existing.UpdatedBy = incoming.UpdatedBy;
            existing.UpdatedByAgent = incoming.UpdatedByAgent;
            existing.NeedsReview = incoming.NeedsReview;
            existing.ReviewedVersion = incoming.ReviewedVersion;
            existing.Attachments = incoming.Attachments;
            existing.Versions = incoming.Versions;
            existing.Clashes = incoming.Clashes;
            existing.Reviews = incoming.Reviews;
        }
        Changed?.Invoke(existing);
        return existing;
    }

    /// <summary>For a copy: the host took the page away (deleted, or no longer shared).</summary>
    public void Drop(Guid id)
    {
        if (Pages.FirstOrDefault(p => p.Id == id) is { } page)
        {
            Pages.Remove(page);
            Removed?.Invoke(page);
        }
    }

    /// <summary>What a guest gets of a page: everything but the oldest versions (the newest <paramref name="versions"/>).</summary>
    public static WikiPage ForGuest(WikiPage page, int versions = 50)
    {
        var copy = page.DeepClone();
        if (copy.Versions.Count > versions) copy.Versions = copy.Versions.Skip(copy.Versions.Count - versions).ToList();
        return copy;
    }
}
