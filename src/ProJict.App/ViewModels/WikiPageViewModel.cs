using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.App.Services.Sharing;
using ProJict.App.Views;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.App.ViewModels;

/// <summary>
/// The open page. Reading shows it drawn; Edit opens the Markdown beside a live preview, and Save (Ctrl+S) makes a
/// version, merged with anything saved meanwhile. History lists every version with a diff and Restore. A page of
/// another ProJict is changed there, if you're an editor.
/// </summary>
public sealed partial class WikiPageViewModel : ObservableObject
{
    private readonly WikiViewModel _owner;
    private readonly DispatcherTimer _previewTimer;
    private int _baseVersion;
    private string _baseText = "";
    private FlowDocument? _document;

    public WikiPageViewModel(WikiViewModel owner, WikiPage page, HostConnection? host)
    {
        _owner = owner;
        Model = page;
        Host = host;
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _preview = null;
            OnPropertyChanged(nameof(Preview));
        };
        FetchFiles();
    }

    /// <summary>Another ProJict's page: its files are fetched from there as it opens, so they open at once.</summary>
    private void FetchFiles()
    {
        if (Host == null) return;
        foreach (var attachment in Model.Attachments ?? []) Main.Guest.FetchWikiFile(Host, attachment);
    }

    private AppState State => _owner.State;
    private MainViewModel Main => _owner.Main;

    public WikiPage Model { get; }
    /// <summary>The ProJict the page comes from; null for your own.</summary>
    public HostConnection? Host { get; }
    public WikiBook Book => Host?.Wiki ?? State.Wiki;
    public bool IsOwn => Host == null;
    public bool CanEdit => Host == null || Host.CanEditWiki;
    public bool IsReadOnly => !CanEdit;
    public string? ReadOnlyText => CanEdit ? null
        : Host!.State != ConnectionState.Connected ? $"{Host.HostName}'s ProJict isn't connected: you can read this page, not change it."
        : $"From {Host.HostName}'s wiki. You're {(Host.Role == Core.Sharing.GuestRole.Editor ? "an" : "a")} {Host.Role.ToString().ToLowerInvariant()} there, so you can read it, not change it.";

    // ───────────── What it is ─────────────

    public string Title
    {
        get => Model.Title;
        set
        {
            var title = (value ?? "").Replace('/', '-').Trim();
            if (title.Length == 0 || title == Model.Title || !CanEdit) return;
            MoveTo(WikiPaths.Combine(Model.Folder, title));
            OnPropertyChanged();
        }
    }

    public string FolderText => (Host != null ? $"{Host.HostName}'s wiki" + (Model.Folder.Length > 0 ? " › " : "") : "") +
                                (Model.Folder.Length > 0 ? WikiPaths.Display(Model.Folder) : Host == null ? "Wiki" : "");

    public string InfoText
    {
        get
        {
            var by = Model.UpdatedBy ?? Model.CreatedBy;
            var when = Model.UpdatedAt.Date == DateTime.Today ? Model.UpdatedAt.ToString("HH:mm", CultureInfo.CurrentCulture)
                : Model.UpdatedAt.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
            return $"Version {Model.Version} · {by}{(Model.UpdatedByAgent ? " (AI agent)" : "")}, {when}";
        }
    }

    /// <summary>"[[GRID2VR/Renderer]]", to paste into another page.</summary>
    public string LinkText => $"[[{Model.Path}]]";

    // ───────────── Project and sharing ─────────────

    public IReadOnlyList<WikiProjectChoice> ProjectChoices =>
    [
        new(null, "No project"),
        .. State.Projects.Where(p => p.Remote == null && !p.IsInbox).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new WikiProjectChoice(p.Id, p.Name)),
    ];

    public WikiProjectChoice? Project
    {
        get => ProjectChoices.FirstOrDefault(c => c.Id == Model.ProjectId) ?? ProjectChoices[0];
        set
        {
            if (!IsOwn || value == null || value.Id == Model.ProjectId) return;
            State.Wiki.LinkProject(Model, value.Id);
            State.StatusText = value.Id == null ? $"“{Model.Title}” isn't about a project any more." : $"“{Model.Title}” is about {value.Name} now: it shows in its header.";
        }
    }

    public string? ProjectName => Model.ProjectId is { } id ? (Host != null ? Main.State.Projects.FirstOrDefault(p => p.Id == id && p.Remote != null) : State.Projects.FirstOrDefault(p => p.Id == id))?.Name : null;

    public bool IsLinked => Model.ProjectId != null;
    public bool CanShareWithEveryone => IsOwn && Model.ProjectId == null;

    public bool SharedWithEveryone
    {
        get => Model.SharedWithEveryone;
        set
        {
            if (!CanShareWithEveryone || value == Model.SharedWithEveryone) return;
            State.Wiki.SetShared(Model, value);
            State.StatusText = value ? $"“{Model.Title}” is shared with everyone you share with." : $"“{Model.Title}” stays on this computer.";
        }
    }

    public string SharingText
    {
        get
        {
            if (Host != null) return $"Shared by {Host.HostName}";
            if (Model.ProjectId is not { } id) return "";
            var project = State.Projects.FirstOrDefault(p => p.Id == id);
            return project == null ? "Its project is gone, so it stays on this computer."
                : project.Private ? $"{project.Name} is private, so this page stays on this computer."
                : $"Shared as {project.Name} is.";
        }
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (Model.ProjectId is { } id && State.Projects.FirstOrDefault(p => p.Id == id) is { } project) Main.OpenProject(project);
    }

    // ───────────── Review and clashes ─────────────

    public bool NeedsReview => IsOwn && Model.NeedsReview;

    public string ReviewText
    {
        get
        {
            var since = $"since you last checked (version {Model.ReviewedVersion ?? 0})";
            if (Model.SentBackReview is { } back)
                return $"{back.Who} sent the changes {since} back: “{back.Note}”{(back.Note is [.., '.' or '!' or '?'] ? "" : ".")} " +
                       $"It waits for {string.Join(", ", LeadReview.Authors(Model))} to change it, or for your review.";
            if (LeadReview.ReviewerOf(_owner.ProjectOf(Model), Model) is { } reviewer)
                return $"Changed by {Model.UpdatedBy} {since}: {reviewer.Lead} ({reviewer.Role}) reviews it for you, or you can.";
            return $"Changed by {Model.UpdatedBy} {since}: it waits for your review.";
        }
    }

    /// <summary>A studio's lead accepted the page as it is now, for you.</summary>
    public bool IsAccepted => IsOwn && Model.AcceptedReview != null;

    public string AcceptedText => Model.AcceptedReview is { } review
        ? $"Accepted by {review.Who}, {review.At.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)}: the changes since " +
          (review.Since > 0 ? $"version {review.Since}" : "the page was new") + (review.Note != null ? $" (“{review.Note}”)" : "") + "."
        : "";

    /// <summary>The history, showing what the lead accepted.</summary>
    [RelayCommand]
    private void ShowAcceptedChanges()
    {
        if (Model.AcceptedReview is not { } review) return;
        ShowHistory();
        var since = review.Since > 0 ? Model.VersionAt(review.Since) : null;
        SelectedVersion = Versions.FirstOrDefault();
        ShowDiff(since?.Text ?? "", Model.Text, $"What {review.By} accepted: the changes since {(since != null ? $"version {since.Number}" : "the page was new")}");
    }

    /// <summary>You don't go along with what the lead accepted: it waits for a review again, theirs or yours.</summary>
    [RelayCommand]
    private void Reopen()
    {
        if (!IsOwn || Model.AcceptedReview is not { } review) return;
        try
        {
            State.Wiki.Reopen(Model, State.WikiMe);
            State.StatusText = $"“{Model.Title}” waits for a review again: {review.By}'s, or yours.";
        }
        catch (WikiException ex) { State.StatusText = ex.Message; }
    }

    [RelayCommand]
    private void Accept()
    {
        if (!IsOwn) return;
        State.Wiki.Accept(Model, State.UserName);
        State.StatusText = $"Checked “{Model.Title}”.";
    }

    /// <summary>The history, showing what changed since you last checked.</summary>
    [RelayCommand]
    private void ShowChanges()
    {
        ShowHistory();
        var since = Model.ReviewedVersion is > 0 and var v ? Model.VersionAt(v) : null;
        SelectedVersion = Versions.FirstOrDefault();
        if (SelectedVersion != null) ShowDiff(since?.Text ?? "", Model.Text, since != null ? $"What changed since version {since.Number} (when you last checked)" : "The whole page (new since you last checked)");
    }

    public IReadOnlyList<WikiClashViewModel> Clashes => (Model.Clashes ?? []).Select(c => new WikiClashViewModel(this, c)).ToList();
    public bool HasClashes => Model.HasClashes;

    internal void CompareClash(WikiClash clash)
    {
        ShowHistory();
        ShowDiff(Model.Text, clash.Text, $"{clash.By}'s clashing version (+) against the page as it is (−)");
    }

    internal void ResolveClash(WikiClash clash, bool take)
    {
        if (!CanEdit) return;
        if (Host != null)
        {
            Main.Guest.WikiOp(Host, "resolve", null, ("page", Model.Id.ToString()), ("clash", clash.Id.ToString()), ("take", take));
            return;
        }
        try
        {
            State.Wiki.ResolveClash(Model, clash.Id, take, State.WikiMe);
            State.StatusText = take ? $"“{Model.Title}” has {clash.By}'s version now." : $"Dropped {clash.By}'s clashing version.";
        }
        catch (WikiException ex) { State.StatusText = ex.Message; }
    }

    /// <summary>Opens the editor with both versions one after the other, to merge by hand; saving settles the clash.</summary>
    internal void MergeByHand(WikiClash clash)
    {
        if (!CanEdit) return;
        StartEditing();
        EditText = $"{Model.Text.TrimEnd()}\n\n**{clash.By}'s version, from here (keep what you want, delete the rest and this line, then save):**\n\n{clash.Text.TrimEnd()}\n";
        _resolving = clash;
    }

    private WikiClash? _resolving;

    // ───────────── Reading ─────────────

    public bool IsReading => !IsEditing && !IsHistory;

    /// <summary>The page drawn (rebuilt when it changes).</summary>
    public FlowDocument Document => _document ??= Render(Model.Text);

    public IReadOnlyList<WikiLinkChip> LinkedFrom => WikiText.LinksTo(Book, Model).Select(p => new WikiLinkChip(_owner, p, Host)).ToList();
    public bool HasLinkedFrom => LinkedFrom.Count > 0;

    private FlowDocument Render(string text) => WikiRenderer.Render(text, new WikiRenderContext
    {
        ResolvePage = target => Book.Resolve(target, Model),
        FindItem = token => State.Projects.SelectMany(p => p.Walk()).FirstOrDefault(l => l.Item.Id.ToString().StartsWith(token, StringComparison.OrdinalIgnoreCase)) is { Item: { } item }
            ? (item.Id, item.Text) : null,
        IsChannel = name => (Host?.Chat ?? State.Chat).Find(name) is { Direct: false },
        FilePath = FilePath,
        OpenPage = target => _owner.FollowLink(target, this),
        OpenItem = id =>
        {
            foreach (var project in State.Projects)
                if (project.Find(id) is { } loc)
                {
                    Main.OpenProject(project).Reveal(loc.Item);
                    return;
                }
        },
        OpenChannel = name =>
        {
            if ((Host?.Chat ?? State.Chat).Find(name) is { } channel) Main.ShowChannel(channel);
        },
        OpenLink = OpenFile,
    });

    // ───────────── Editing ─────────────

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isHistory;
    [ObservableProperty] private string _editText = "";

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(IsReading));
    partial void OnIsHistoryChanged(bool value) => OnPropertyChanged(nameof(IsReading));

    partial void OnEditTextChanged(string value)
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private FlowDocument? _preview;

    /// <summary>The live preview beside the editor, drawn again a moment after you stop typing.</summary>
    public FlowDocument Preview => _preview ??= Render(EditText);

    /// <summary>Someone saved the page while you're editing it: your save will be merged with theirs.</summary>
    public string? EditNotice => IsEditing && Model.Version != _baseVersion
        ? $"{Model.UpdatedBy} saved version {Model.Version} while you were editing. Saving merges your changes with theirs, paragraph by paragraph."
        : null;

    public bool HasUnsavedChanges => IsEditing && EditText != _baseText;

    [RelayCommand]
    public void StartEditing()
    {
        if (!CanEdit) return;
        IsHistory = false;
        if (IsEditing) return;
        _baseVersion = Model.Version;
        _baseText = Model.Text;
        EditText = Model.Text;
        IsEditing = true;
        _preview = null;
        OnPropertyChanged(nameof(EditNotice));
        OnPropertyChanged(nameof(Preview));
    }

    [RelayCommand]
    private void Save() => SaveEdits(stayEditing: false);

    /// <summary>Saves what's in the editor as a new version (merged with anything saved meanwhile).</summary>
    public void SaveEdits(bool stayEditing)
    {
        if (!IsEditing) return;
        var text = EditText;
        var resolving = _resolving;
        _resolving = null;
        if (Host != null)
        {
            if (text != _baseText) Main.Guest.WikiOp(Host, "save", null, ("page", Model.Id.ToString()), ("text", text), ("base", _baseVersion));
            if (resolving != null) Main.Guest.WikiOp(Host, "resolve", null, ("page", Model.Id.ToString()), ("clash", resolving.Id.ToString()), ("take", false));
            State.StatusText = text != _baseText ? $"Sent “{Model.Title}” to {Host.HostName}'s ProJict." : "Nothing changed.";
        }
        else
        {
            try
            {
                var result = State.Wiki.Save(Model, text, _baseVersion, State.WikiMe);
                if (resolving != null && Model.Clashes?.Any(c => c.Id == resolving.Id) == true) State.Wiki.ResolveClash(Model, resolving.Id, take: false, State.WikiMe);
                State.StatusText = result.Describe();
            }
            catch (WikiException ex)
            {
                State.StatusText = ex.Message;
                return;
            }
        }
        if (stayEditing)
        {
            _baseVersion = Model.Version;
            _baseText = text;
            OnPropertyChanged(nameof(EditNotice));
            return;
        }
        IsEditing = false;
        Refresh();
    }

    [RelayCommand]
    private void Cancel()
    {
        _resolving = null;
        IsEditing = false;
        Refresh();
    }

    /// <summary>Going to another page or view: what you were writing is saved, not lost.</summary>
    public void Leave()
    {
        if (HasUnsavedChanges) SaveEdits(stayEditing: false);
        else if (IsEditing) IsEditing = false;
        _previewTimer.Stop();
    }

    /// <summary>Asks for the page's new place ("GRID2VR/Architecture/Renderer").</summary>
    [RelayCommand]
    private void Move()
    {
        if (!CanEdit || Application.Current.MainWindow is not { } window) return;
        var path = InputDialog.Ask(window, "Move page", "Where the page goes: folders and its name, separated by /", Model.Path);
        if (path != null) MoveTo(path);
    }

    private void MoveTo(string path)
    {
        if (Host != null)
        {
            Main.Guest.WikiOp(Host, "move", null, ("page", Model.Id.ToString()), ("path", path));
            return;
        }
        try
        {
            State.Wiki.Move(Model, path);
            State.StatusText = $"Moved to {WikiPaths.Display(Model.Path)}.";
        }
        catch (WikiException ex) { State.StatusText = ex.Message; }
        Refresh();
    }

    [RelayCommand]
    private void Delete()
    {
        if (!IsOwn || Application.Current.MainWindow is not { } window) return;
        if (MessageBox.Show(window, $"Delete the page “{WikiPaths.Display(Model.Path)}” and its {Model.Versions.Count} version(s)?\n\nA copy is kept in the data folder's backups.",
                "Delete page", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsEditing = false;
        State.Wiki.Delete(Model);
        State.StatusText = $"Deleted “{Model.Title}”.";
    }

    [RelayCommand]
    private void CopyLink()
    {
        try
        {
            Clipboard.SetText(LinkText);
            State.StatusText = $"Copied {LinkText}: paste it into another page to link this one.";
        }
        catch (System.Runtime.InteropServices.ExternalException) { State.StatusText = "Clipboard is busy, try again."; }
    }

    // ───────────── History ─────────────

    public IReadOnlyList<WikiVersionRowViewModel> Versions =>
        Model.Versions.Select((v, i) => new WikiVersionRowViewModel(v, i > 0 ? Model.Versions[i - 1] : null, i == Model.Versions.Count - 1,
            Model.Reviews?.Where(r => r.Version == v.Number).ToList())).Reverse().ToList();

    [ObservableProperty] private WikiVersionRowViewModel? _selectedVersion;
    [ObservableProperty] private IReadOnlyList<DiffLineViewModel> _diff = [];
    [ObservableProperty] private string _diffTitle = "";

    public bool CanRestore => CanEdit && SelectedVersion != null && SelectedVersion.Model.Number != Model.Version;

    partial void OnSelectedVersionChanged(WikiVersionRowViewModel? value)
    {
        OnPropertyChanged(nameof(CanRestore));
        if (value == null) return;
        ShowDiff(value.Previous?.Text ?? "", value.Model.Text,
            value.Previous != null ? $"What version {value.Model.Number} changed (against version {value.Previous.Number})" : $"Version {value.Model.Number}, as the page started");
    }

    private void ShowDiff(string from, string to, string title)
    {
        DiffTitle = title;
        Diff = TextMerge.Lines(from, to).Select(l => new DiffLineViewModel(l)).ToList();
    }

    [RelayCommand]
    private void ToggleHistory()
    {
        if (IsHistory)
        {
            IsHistory = false;
            return;
        }
        ShowHistory();
    }

    /// <summary>The page's versions, newest first (the wiki opens pages this way while you keep History on).</summary>
    internal void ShowHistory()
    {
        if (HasUnsavedChanges) SaveEdits(stayEditing: false);
        IsEditing = false;
        IsHistory = true;
        OnPropertyChanged(nameof(Versions));
        SelectedVersion ??= Versions.FirstOrDefault();
    }

    [RelayCommand]
    private void CompareWithCurrent()
    {
        if (SelectedVersion is not { } version) return;
        ShowDiff(version.Model.Text, Model.Text, $"The page as it is (+) against version {version.Model.Number} (−)");
    }

    [RelayCommand]
    private void Restore()
    {
        if (!CanRestore || SelectedVersion is not { } version) return;
        var number = version.Model.Number;
        if (Host != null) Main.Guest.WikiOp(Host, "restore", null, ("page", Model.Id.ToString()), ("version", number));
        else
        {
            try
            {
                State.Wiki.Restore(Model, number, State.WikiMe);
                State.StatusText = $"Restored version {number} of “{Model.Title}” (as version {Model.Version}).";
            }
            catch (WikiException ex) { State.StatusText = ex.Message; }
        }
        IsHistory = false;
    }

    // ───────────── Files ─────────────

    public IReadOnlyList<WikiFileViewModel> Files => (Model.Attachments ?? []).Select(a => new WikiFileViewModel(this, a)).ToList();
    public bool HasFiles => Model.Attachments is { Count: > 0 };

    /// <summary>The local file a picture or file link names: a page's attachment by file name (or id).</summary>
    /// <summary>Where a file of this ProJict's own page is; null for another ProJict's (fetched only when opened).</summary>
    internal string? LocalFilePath(Attachment attachment) => Host == null ? State.Store.AttachmentPath(attachment) : null;

    private string? FilePath(string name)
    {
        var attachment = FindFile(name);
        if (attachment == null) return null;
        if (Host == null) return State.Store.AttachmentPath(attachment);
        var path = Main.Guest.WikiFilePath(Host, attachment);
        if (!File.Exists(path)) Main.Guest.FetchWikiFile(Host, attachment);
        return path;
    }

    private Attachment? FindFile(string name)
    {
        var key = Uri.UnescapeDataString(name.Trim());
        return Model.Attachments?.FirstOrDefault(a => string.Equals(a.FileName, key, StringComparison.OrdinalIgnoreCase))
               ?? Model.Attachments?.FirstOrDefault(a => a.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase) && key.Length >= 8);
    }

    /// <summary>A link on the page: a web page in the browser, an attached picture in the viewer, another file in its app.</summary>
    internal void OpenFile(string link)
    {
        if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            ShellLauncher.OpenUrl(link);
            return;
        }
        if (FindFile(link) is not { } attachment || FilePath(link) is not { } path)
        {
            State.StatusText = $"No file “{link}” is attached to this page.";
            return;
        }
        if (!File.Exists(path))
        {
            State.StatusText = Host != null ? $"Fetching {attachment.FileName} from {Host.HostName}'s ProJict…" : $"{attachment.FileName} is missing from the data folder.";
            return;
        }
        try
        {
            if (attachment.IsImage && Application.Current.MainWindow is { } window)
                new ImageViewerWindow(path, attachment.FileName, (png, fileName) => SaveMarkedUpCopy(png, fileName)) { Owner = window }.Show();
            else ShellLauncher.OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            State.StatusText = ex.Message;
        }
    }

    /// <summary>A marked-up copy of a picture from the viewer: attached to the page next to it.</summary>
    private string SaveMarkedUpCopy(byte[] png, string fileName)
    {
        if (Host != null)
        {
            Main.Guest.WikiAttach(Host, Model, [(fileName, png)]);
            return fileName;
        }
        var copy = State.Store.WriteAttachment(fileName, png);
        State.Wiki.Attach(Model, [copy]);
        return copy.FileName;
    }

    [RelayCommand]
    private void AttachFiles()
    {
        if (!CanEdit) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files to the page", Multiselect = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) _ = AddFilesAsync(dialog.FileNames, insert: IsEditing);
    }

    /// <summary>
    /// Adds files (dropped, pasted or picked) to the page; while editing, a link to each goes in where you're typing
    /// (pictures as ![](name), so they show).
    /// </summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths, bool insert, byte[]? png = null)
    {
        if (!CanEdit) return;
        var added = new List<Attachment>();
        if (Host != null)
        {
            var files = new List<(string, byte[])>();
            if (png != null) files.Add(($"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png", png));
            foreach (var path in paths)
            {
                try { files.Add((Path.GetFileName(path), await File.ReadAllBytesAsync(path))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { State.StatusText = ex.Message; }
            }
            Main.Guest.WikiAttach(Host, Model, files);
            if (insert) foreach (var (name, _) in files) InsertText(LinkFor(name));
            return;
        }
        try
        {
            if (png != null) added.Add(State.Store.WriteAttachment($"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png", png));
            var store = State.Store;
            added.AddRange(await Task.Run(() => paths.Where(File.Exists).Select(store.ImportAttachment).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            State.StatusText = $"Couldn't attach: {ex.Message}";
        }
        if (added.Count == 0 || !State.Wiki.Pages.Contains(Model)) return;
        State.Wiki.Attach(Model, added);
        if (insert) foreach (var file in added) InsertText(LinkFor(file.FileName));
        State.StatusText = added.Count == 1 ? $"Attached {added[0].FileName}." : $"Attached {added.Count} files.";
    }

    private static string LinkFor(string fileName)
    {
        var target = fileName.Contains(' ') || fileName.Contains(')') ? $"<{fileName}>" : fileName;
        return new Attachment { FileName = fileName }.IsImage ? $"![]({target})" : $"[{fileName}]({target})";
    }

    internal void InsertFileLink(Attachment attachment)
    {
        if (!CanEdit) return;
        StartEditing();
        InsertText(LinkFor(attachment.FileName));
    }

    /// <summary>Where the editor's caret is; the view keeps it up to date, and inserting puts text there.</summary>
    public int Caret { get; set; } = -1;

    /// <summary>Asks the view to put text where the caret is (the view does it, so undo in the box keeps working).</summary>
    public event Action<string>? InsertRequested;

    private void InsertText(string text)
    {
        if (InsertRequested != null) InsertRequested(text);
        else EditText = EditText.TrimEnd() + "\n\n" + text + "\n";
    }

    internal void RemoveFile(Attachment attachment)
    {
        if (!IsOwn) return;
        State.Wiki.RemoveAttachment(Model, attachment);
        State.StatusText = $"Removed {attachment.FileName} from the page (it's kept in the data folder while an older version may show it).";
    }

    // ───────────── Updates ─────────────

    /// <summary>A diagram's picture is ready (or didn't draw): the page and the preview show it.</summary>
    public void RedrawDiagrams()
    {
        if (Model.Text.Contains("```mermaid", StringComparison.OrdinalIgnoreCase))
        {
            _document = null;
            OnPropertyChanged(nameof(Document));
        }
        if (IsEditing && EditText.Contains("```mermaid", StringComparison.OrdinalIgnoreCase))
        {
            _preview = null;
            OnPropertyChanged(nameof(Preview));
        }
    }

    /// <summary>The page changed (here or by someone else): what shows it follows; the editor keeps what you're writing.</summary>
    public void Refresh()
    {
        _document = null;
        FetchFiles();
        foreach (var name in new[]
                 {
                     nameof(Title), nameof(FolderText), nameof(InfoText), nameof(LinkText), nameof(Project), nameof(ProjectChoices), nameof(ProjectName), nameof(IsLinked),
                     nameof(CanShareWithEveryone), nameof(SharedWithEveryone), nameof(SharingText), nameof(NeedsReview), nameof(ReviewText), nameof(IsAccepted), nameof(AcceptedText), nameof(Clashes),
                     nameof(HasClashes), nameof(Document), nameof(LinkedFrom), nameof(HasLinkedFrom), nameof(EditNotice), nameof(Files), nameof(HasFiles),
                     nameof(CanEdit), nameof(IsReadOnly), nameof(ReadOnlyText), nameof(Versions), nameof(CanRestore),
                 })
            OnPropertyChanged(name);
    }
}
