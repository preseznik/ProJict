using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services.Sharing;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.App.ViewModels;

/// <summary>
/// The Wiki view: the pages in a tree (folders like "GRID2VR / Architecture"), yours and those of ProJicts that share
/// theirs with you, and the open page: read, edited with a live preview beside it, or its history. Search filters the
/// tree; pages agents changed show a dot until you've checked them.
/// </summary>
public sealed partial class WikiViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private bool _selecting;
    private bool _shown;

    public WikiViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        state.Wiki.Changed += Changed;
        state.Wiki.Removed += page => Removed(page);
        main.Guest.WikiChanged += connection =>
        {
            if (_shown) RebuildTree();
            _main.RefreshWikiChips();
            if (Page?.Host == connection)
            {
                if (connection.Wiki?.Pages.Contains(Page.Model) != true) Page = null;
                else Page.Refresh();
            }
        };
    }

    internal AppState State => _state;
    internal MainViewModel Main => _main;

    public ObservableCollection<WikiNodeViewModel> Tree { get; } = [];
    public ObservableCollection<WikiHitViewModel> Results { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private WikiPageViewModel? _page;

    public bool IsFiltering => Filter.Trim().Length > 0;
    public bool HasPage => Page != null;
    public bool IsEmpty => _state.Wiki.Pages.Count == 0 && _main.Guest.Connections.All(c => c.Wiki is not { Pages.Count: > 0 });

    /// <summary>Pages AI agents changed that you haven't checked yet: the sidebar's count.</summary>
    public int ReviewCount => _state.Wiki.Pages.Count(p => p.NeedsReview || p.HasClashes);
    public string ReviewToolTip => ReviewCount switch
    {
        0 => "Pages about your projects and anything else worth writing down; agents read and write them too",
        1 => "1 page waits for you: an agent changed it, or two edits clashed",
        var n => $"{n} pages wait for you: agents changed them, or edits clashed",
    };

    /// <summary>The review count changed.</summary>
    public event Action? CountsChanged;

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsFiltering));
        RebuildResults();
    }

    /// <summary>History stays on from page to page (showing the open page's), until you turn it off or start editing.</summary>
    private bool _keepHistory;

    partial void OnPageChanged(WikiPageViewModel? oldValue, WikiPageViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= PagePropertyChanged;
        oldValue?.Leave();
        if (newValue != null)
        {
            if (_keepHistory) newValue.ShowHistory();
            newValue.PropertyChanged += PagePropertyChanged;
        }
        OnPropertyChanged(nameof(HasPage));
        SelectNode(newValue?.Model);
    }

    private void PagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WikiPageViewModel.IsHistory) && sender is WikiPageViewModel page && page == Page) _keepHistory = page.IsHistory;
    }

    // ───────────── Showing ─────────────

    /// <summary>Opens the view at a page (or where you were, or the first page).</summary>
    public void Show(WikiPage? page = null, HostConnection? host = null)
    {
        _shown = true;
        RebuildTree();
        if (page != null) Open(page, host ?? _main.Guest.ConnectionOf(page));
        else if (Page == null && _state.Wiki.Pages.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } first) Open(first, null);
        else if (Page != null) Page.Refresh();
    }

    /// <summary>Leaving the view: a page being edited is saved.</summary>
    public void Hide()
    {
        _shown = false;
        Page?.Leave();
    }

    public void Open(WikiPage page, HostConnection? host)
    {
        if (Page?.Model == page)
        {
            Page.Refresh();
            return;
        }
        Page = new WikiPageViewModel(this, page, host);
    }

    /// <summary>A [[link]] on a page: opens the page it names, or starts it when there's none yet.</summary>
    internal void FollowLink(string target, WikiPageViewModel from)
    {
        var book = from.Book;
        if (book.Resolve(target, from.Model) is { } page)
        {
            Open(page, from.Host);
            return;
        }
        if (!from.CanEdit)
        {
            _state.StatusText = $"There's no page “{target}” yet.";
            return;
        }
        // Started next to the page the link is on, unless the link gives a whole path.
        var path = WikiPaths.Normalize(target).Contains('/') ? target : WikiPaths.Combine(from.Model.Folder, target);
        CreatePage(path, from.Host, from.Model.ProjectId, $"# {WikiPaths.Title(WikiPaths.Normalize(target))}\n\n");
    }

    private void Changed(WikiPage page)
    {
        if (_shown) RebuildTree();
        if (Page?.Model == page) Page.Refresh();
        CountsChanged?.Invoke();
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(ReviewToolTip));
        _main.RefreshWikiChips();
    }

    /// <summary>Who reviews pages changed (a studio's lead now does, or not): what shows them follows.</summary>
    public void RefreshPages()
    {
        if (_shown) RebuildTree();
        Page?.Refresh();
    }

    /// <summary>A page of yours about one of your projects, as the project is now.</summary>
    internal Project? ProjectOf(WikiPage page) => page.ProjectId is { } id ? _state.Projects.FirstOrDefault(p => p.Id == id && p.Remote == null) : null;

    /// <summary>"waits for your review", or for the studio lead's who reviews it for you.</summary>
    internal string WaitsText(WikiPage page) =>
        LeadReview.ReviewerOf(ProjectOf(page), page) is not { } reviewer ? "waits for your review"
        : page.SentBackReview != null ? $"{reviewer.Lead} sent it back: it waits for a change, or your review"
        : $"waits for {reviewer.Lead}'s review ({reviewer.Role}), or yours";

    private void Removed(WikiPage page)
    {
        if (Page?.Model == page) Page = null;
        if (_shown) RebuildTree();
        CountsChanged?.Invoke();
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(ReviewToolTip));
        _main.RefreshWikiChips();
    }

    // ───────────── The tree ─────────────

    private void RebuildTree()
    {
        Tree.Clear();
        foreach (var node in Nodes(_state.Wiki, null, "")) Tree.Add(node);
        foreach (var host in _main.Guest.Connections.Where(c => c.Wiki is { Pages.Count: > 0 }))
        {
            var key = "host:" + host.Address;
            var root = new WikiNodeViewModel(this, $"{host.HostName}'s wiki", key, null, host, "", isHost: true) { IsExpanded = !_expanded.Contains(key + ":closed") };
            foreach (var node in Nodes(host.Wiki!, host, key + "/")) root.Children.Add(node);
            Tree.Add(root);
        }
        OnPropertyChanged(nameof(IsEmpty));
        SelectNode(Page?.Model);
        if (IsFiltering) RebuildResults();
    }

    /// <summary>A book's pages as a tree: a folder for each path part, the page itself where its path ends.</summary>
    private List<WikiNodeViewModel> Nodes(WikiBook book, HostConnection? host, string keyPrefix)
    {
        var roots = new List<WikiNodeViewModel>();
        var byKey = new Dictionary<string, WikiNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in book.Pages.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            var parts = WikiPaths.Parts(page.Path);
            WikiNodeViewModel? parent = null;
            for (var i = 0; i < parts.Length; i++)
            {
                var path = string.Join('/', parts.Take(i + 1));
                var key = keyPrefix + WikiPaths.Key(path);
                if (!byKey.TryGetValue(key, out var node))
                {
                    node = new WikiNodeViewModel(this, parts[i], key, null, host, path) { IsExpanded = _expanded.Contains(key) };
                    byKey[key] = node;
                    (parent?.Children ?? (IList<WikiNodeViewModel>)roots).Add(node);
                }
                if (i == parts.Length - 1) node.Page = page;
                parent = node;
            }
        }
        return roots;
    }

    internal void Expanded(WikiNodeViewModel node, bool expanded)
    {
        var key = node.IsHost ? node.Key + ":closed" : node.Key;
        if (expanded != node.IsHost) _expanded.Add(key);
        else _expanded.Remove(key);
    }

    internal void Selected(WikiNodeViewModel node)
    {
        if (_selecting) return;
        if (node.Page != null) Open(node.Page, node.Host);
    }

    /// <summary>Selects the page's node in the tree, opening the folders above it.</summary>
    private void SelectNode(WikiPage? page)
    {
        if (page == null) return;
        _selecting = true;
        try
        {
            bool Walk(IEnumerable<WikiNodeViewModel> nodes)
            {
                foreach (var node in nodes)
                {
                    if (node.Page == page)
                    {
                        node.IsSelected = true;
                        return true;
                    }
                    if (Walk(node.Children))
                    {
                        node.IsExpanded = true;
                        return true;
                    }
                }
                return false;
            }
            Walk(Tree);
        }
        finally { _selecting = false; }
    }

    private void RebuildResults()
    {
        Results.Clear();
        if (!IsFiltering) return;
        foreach (var hit in WikiText.Search(_state.Wiki.Pages, Filter, 100)) Results.Add(new WikiHitViewModel(this, hit, null));
        foreach (var host in _main.Guest.Connections.Where(c => c.Wiki != null))
            foreach (var hit in WikiText.Search(host.Wiki!.Pages, Filter, 50)) Results.Add(new WikiHitViewModel(this, hit, host));
    }

    // ───────────── New pages ─────────────

    /// <summary>A new page next to the open one (in its folder, about its project), opened for writing.</summary>
    [RelayCommand]
    private void NewPage()
    {
        var host = Page?.Host is { CanEditWiki: true } h ? h : null;
        CreatePage(WikiPaths.Combine(Page?.Model.Folder ?? "", "New page"), host, Page?.Model.ProjectId, "");
    }

    /// <summary>A new page about a project, in a folder named after it (a project's header › Wiki).</summary>
    public void NewPageFor(Project project)
    {
        _shown = true;
        CreatePage(WikiPaths.Combine(project.Name.Replace('/', '-'), "Overview"), null, project.Id, $"# {project.Name}\n\n");
    }

    private void CreatePage(string path, HostConnection? host, Guid? projectId, string text)
    {
        var book = host?.Wiki ?? _state.Wiki;
        path = WikiPaths.Unique(path, p => book.IsTaken(p));
        if (host != null)
        {
            if (projectId == null)
            {
                _state.StatusText = $"Pages you start in {host.HostName}'s wiki are about one of the projects they share with you: start it from a page about one.";
                return;
            }
            _main.Guest.WikiOp(host, "create", null, ("path", path), ("text", text), ("projectId", projectId.ToString()));
            _state.StatusText = $"Starting “{WikiPaths.Display(path)}” in {host.HostName}'s wiki…";
            return;
        }
        try
        {
            var page = _state.Wiki.Create(path, text, _state.WikiMe, projectId);
            Open(page, null);
            Page!.StartEditing();
        }
        catch (WikiException ex) { _state.StatusText = ex.Message; }
    }

    /// <summary>
    /// "Move to wiki" on a project's note: it becomes a page about the project (in a folder named after it), with its
    /// files. The Inbox's notes become pages about no project, in an "Ideas" folder.
    /// </summary>
    public WikiPage? MoveNote(Project project, NoteDoc note)
    {
        var folder = project.IsInbox ? "Ideas" : project.Name.Replace('/', '-');
        var path = WikiPaths.Unique(WikiPaths.Combine(folder, note.Title.Replace('/', '-')), p => _state.Wiki.IsTaken(p));
        try
        {
            var page = _state.Wiki.Create(path, note.Text, new WikiAuthor(note.UpdatedBy ?? _state.UserName), project.IsInbox ? null : project.Id,
                note: $"Moved from {project.Name}'s notes");
            if (note.Attachments is { Count: > 0 } files) _state.Wiki.Attach(page, files);
            return page;
        }
        catch (WikiException ex)
        {
            _state.StatusText = ex.Message;
            return null;
        }
    }
}

/// <summary>A folder or page in the tree (a page can have pages under it, then it's both).</summary>
public sealed partial class WikiNodeViewModel(WikiViewModel owner, string name, string key, WikiPage? page, HostConnection? host, string path, bool isHost = false)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Key { get; } = key;
    public WikiPage? Page { get; set; } = page;
    public HostConnection? Host { get; } = host;
    public string Path { get; } = path;
    public bool IsHost { get; } = isHost;
    public ObservableCollection<WikiNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public string Glyph => IsHost ? "" : Page != null ? "" : "";
    public bool NeedsAttention => Page is { } p && Host == null && (p.NeedsReview || p.HasClashes);
    public string? ToolTip => Page is { } p
        ? WikiPaths.Display(p.Path) + (p.HasClashes ? "\nTwo edits clashed: pick one" : p.NeedsReview ? $"\nChanged by {p.UpdatedBy}: {owner.WaitsText(p)}" : "")
        : IsHost ? $"Pages {Host!.HostName} shares with you" : WikiPaths.Display(Path);

    partial void OnIsExpandedChanged(bool value) => owner.Expanded(this, value);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) owner.Selected(this);
    }
}

/// <summary>A page search found.</summary>
public sealed partial class WikiHitViewModel(WikiViewModel owner, WikiHit hit, HostConnection? host)
{
    public string Title => hit.Page.Title;
    public string Location => (host != null ? $"{host.HostName}'s wiki › " : "") + (hit.Page.Folder.Length > 0 ? WikiPaths.Display(hit.Page.Folder) : "Top level");
    public string Snippet => hit.Snippet;

    [RelayCommand] private void Open() => owner.Open(hit.Page, host);
}

/// <summary>A project a page can be linked to (or none).</summary>
public sealed record WikiProjectChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A version in a page's history: who saved it and when, what it changed.</summary>
public sealed class WikiVersionRowViewModel(WikiVersion version, WikiVersion? previous, bool current, IReadOnlyList<WikiReview>? reviews = null)
{
    public WikiVersion Model => version;
    public WikiVersion? Previous => previous;
    public string Title => $"Version {version.Number}" + (current ? " (current)" : "");
    public string Who => version.By + (version.Agent ? " (AI agent)" : "");
    public string When => version.At.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string? Note => version.Note;
    public bool HasNote => version.Note != null;

    /// <summary>What a studio's lead (or you) decided about this version: "Accepted by Ada (Lead designer), 2 Nov, 10:42".</summary>
    public string? ReviewText => reviews is { Count: > 0 } ? string.Join("\n", reviews.Select(Describe)) : null;
    public bool HasReview => reviews is { Count: > 0 };

    private static string Describe(WikiReview review)
    {
        var when = review.At.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
        var note = review.Note != null ? $": “{review.Note}”" : "";
        return review.Outcome switch
        {
            WikiReviewOutcome.Accepted => $"Accepted by {review.Who}, {when}{note}",
            WikiReviewOutcome.SentBack => $"Sent back by {review.Who}, {when}{note}",
            _ => $"Reopened by {review.Who}, {when}{note}",
        };
    }
    public string Changes
    {
        get
        {
            if (previous == null) return "first version";
            var (added, removed) = TextMerge.Count(previous.Text, version.Text);
            return $"+{added} −{removed} lines";
        }
    }
}

/// <summary>A line of a diff, colored by what happened to it.</summary>
public sealed class DiffLineViewModel(DiffLine line)
{
    public string Text => (line.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "− ", _ => "  " }) + line.Text;
    public bool IsAdded => line.Kind == DiffKind.Added;
    public bool IsRemoved => line.Kind == DiffKind.Removed;
}

/// <summary>A save that clashed: whose, when, and the choice between it and the page as it is.</summary>
public sealed partial class WikiClashViewModel(WikiPageViewModel owner, WikiClash clash)
{
    public WikiClash Model => clash;
    public string Text => $"{clash.By}{(clash.Agent ? " (AI agent)" : "")} saved at {clash.At:HH:mm} on top of version {clash.BaseVersion}, " +
                          $"but version {clash.AgainstVersion} had changed the same paragraphs. Both are kept: the page shows version {clash.AgainstVersion}.";
    public string TakeText => $"Use {clash.By}'s";

    [RelayCommand] private void Compare() => owner.CompareClash(clash);
    [RelayCommand] private void Take() => owner.ResolveClash(clash, take: true);
    [RelayCommand] private void Keep() => owner.ResolveClash(clash, take: false);
    [RelayCommand] private void EditBoth() => owner.MergeByHand(clash);
}

/// <summary>A file on a page: a click opens it, the cross removes it from the page.</summary>
public sealed partial class WikiFileViewModel(WikiPageViewModel owner, Attachment attachment) : IPlayable
{
    public Attachment Model => attachment;
    public string FileName => attachment.FileName;
    /// <summary>Audio or video on this ProJict's own page: it plays in place (another ProJict's files open as before).</summary>
    public MediaKind MediaKind => MediaLinks.KindOf(attachment.FileName);
    public string? PlayPath => owner.LocalFilePath(attachment);
    public bool Plays => MediaKind is MediaKind.Audio or MediaKind.Video && PlayPath != null;
    public string SizeText => AttachmentViewModel.FormatSize(attachment.Size);
    public string ToolTip => $"{FileName} · {SizeText}\nClick to open · {(attachment.IsImage ? "![](" : "[" + FileName + "](")}{FileName}) shows it on the page";
    public bool CanRemove => owner.IsOwn;

    [RelayCommand] private void Open() => owner.OpenFile(attachment.FileName);
    [RelayCommand] private void Insert() => owner.InsertFileLink(attachment);
    [RelayCommand] private void Remove() => owner.RemoveFile(attachment);
}

/// <summary>A page about a project, on the project's header; a click opens it in the wiki.</summary>
public sealed partial class WikiPageChip(WikiPage page, MainViewModel main)
{
    public WikiPage Page => page;
    public string Text => page.Title;
    public bool NeedsAttention => page.NeedsReview || page.HasClashes;
    public string ToolTip => $"Open “{WikiPaths.Display(page.Path)}” in the wiki" +
                             (page.HasClashes ? "\nTwo edits clashed: pick one" : page.NeedsReview ? $"\nChanged by {page.UpdatedBy}: {main.Wiki.WaitsText(page)}" : "");

    [RelayCommand] private void Open() => main.ShowWikiPage(page);
}

/// <summary>A page that links here.</summary>
public sealed partial class WikiLinkChip(WikiViewModel owner, WikiPage page, HostConnection? host)
{
    public string Text => page.Title;
    public string ToolTip => WikiPaths.Display(page.Path);

    [RelayCommand] private void Open() => owner.Open(page, host);
}
