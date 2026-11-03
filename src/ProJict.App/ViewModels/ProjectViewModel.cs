using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.App.Views;
using ProJict.Core.Export;
using ProJict.Core.Features;
using ProJict.Core.Import;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project page. Every structural edit goes through <see cref="Mutate"/>: snapshot for undo,
/// change the model, autosave, rebuild the affected categories and restore keyboard focus.
/// </summary>
public sealed partial class ProjectViewModel : ObservableObject
{
    private const int MaxUndo = 100;
    private const string AllTags = "All tags";
    private const string NoTag = "No tag";

    private readonly AppState _state;
    /// <summary>Undo and redo, by snapshots of the project (in Core, so another front end can use it too).</summary>
    private readonly UndoHistory _history = new(MaxUndo);
    private Guid? _focusedItemId;

    public ProjectViewModel(AppState state, MainViewModel main, Project project)
    {
        _state = state;
        Main = main;
        Project = project;
        _sideBySide = state.Settings.SideBySide;
        _hideEmpty = state.Settings.HideEmptyCategories;
        _sort = state.Settings.ItemSort;
        RebuildAll();
        ReloadSideLists();
        RefreshPublish();
        state.ProjectReplaced += p =>
        {
            if (p != Project) return;
            RebuildAll();
            ReloadSideLists();
            NotifyDetails();
        };
    }

    public Project Project { get; }
    public MainViewModel Main { get; }
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<NoteViewModel> Notes { get; } = [];
    public ObservableCollection<Release> Releases { get; } = [];
    public ObservableCollection<ArchivedFile> Archive { get; } = [];

    /// <summary>The types to pick from: ProJict's own, the ones you added, then any other this project's items have.</summary>
    public IReadOnlyList<string> TagList => Core.Features.TagChoices.For(_state.Settings.CustomTags, Project.Walk().Select(l => l.Item.Tag));
    public static IReadOnlyList<ProjectStatus> Statuses { get; } = Enum.GetValues<ProjectStatus>();
    /// <summary>A copy, so raising Groups gives the header's group box a fresh list.</summary>
    public IReadOnlyList<string> Groups => [.. _state.Settings.Groups];
    public IReadOnlyList<string> TagFilters { get; private set; } = [AllTags];
    public static IReadOnlyList<StateFilterOption> StateFilters { get; } =
    [
        new("All states", null),
        new("To do", ItemState.Todo),
        new("In progress", ItemState.InProgress),
        new("Done", ItemState.Done),
        new("Needs review", ItemState.Done, ReviewOnly: true),
        new("Needs you", null, AttentionOnly: true),
    ];

    /// <summary>The state filter's "Needs you": items with an agent's work to review or comments you haven't read.</summary>
    public static StateFilterOption WaitingFilter => StateFilters[^1];

    public void ShowWaiting() => StateFilter = WaitingFilter;
    public static IReadOnlyList<PriorityFilterOption> PriorityFilters { get; } =
    [
        new("All priorities", null),
        new("Urgent", Priority.Urgent),
        new("High and up", Priority.High),
        new("Medium and up", Priority.Medium),
        new("Any priority", Priority.Low),
        new("No priority", null, Only: Priority.None),
        new("On hold", null, Only: Priority.Hold),
    ];
    /// <summary>In menu order, which is also Alt+1 … Alt+5.</summary>
    public static IReadOnlyList<Priority> PriorityChoices { get; } = [Priority.Urgent, Priority.High, Priority.Medium, Priority.Low, Priority.Hold];
    public bool IsInbox => Project.IsInbox;
    public bool IsNotInbox => !Project.IsInbox;

    /// <summary>Asks the view to focus an item's text box; caret -1 means end of text.</summary>
    public event Action<Guid, int>? FocusRequested;
    public event Action<CategoryViewModel>? FocusAddItemRequested;
    /// <summary>Set by the board: the caret position in an item's text box, so agent edits can put it back.</summary>
    public Func<Guid, int?>? CaretOf { get; set; }

    // ───────────── Details ─────────────

    public string Name
    {
        get => Project.Name;
        set => SetDetail(Project.Name, value, v => Project.Name = v ?? "", listChanged: true);
    }

    public string Key
    {
        get => Project.Key;
        set => SetDetail(Project.Key, value, v => Project.Key = Project.KeyFromName(v ?? ""));
    }

    public string FilePrefix
    {
        get => Project.FilePrefix;
        set => SetDetail(Project.FilePrefix, value, v => Project.FilePrefix = string.IsNullOrWhiteSpace(v) ? "TODO" : v.Trim().ToUpperInvariant());
    }

    public string Group
    {
        get => Project.Group;
        set
        {
            if (ProjectGroups.Same(value, ProjectGroups.Archived) || Project.Remote != null)
            {
                RefreshGroups(); // "Archived" is the status, not a group; another ProJict's project keeps its group
                return;
            }
            // A name typed here that isn't a group yet becomes one (before the sidebar rebuilds).
            if (!string.IsNullOrWhiteSpace(value) && GroupOperations.Validate(_state.Settings, value) == null)
            {
                _state.Settings.Groups.Add(value.Trim());
                _state.MarkSettingsDirty();
            }
            SetDetail(Project.Group, value, v => Project.Group = v?.Trim() ?? "", listChanged: true);
        }
    }

    public ProjectStatus Status
    {
        get => Project.Status;
        set
        {
            if (value == Project.Status) return;
            if (Project.Remote != null)
            {
                OnPropertyChanged();
                return;
            }
            Project.Status = value;
            Touch();
            OnPropertyChanged();
            _state.NotifyListChanged();
        }
    }

    public string? RepoPath { get => Project.RepoPath; set => SetDetail(Project.RepoPath, value, v => Project.RepoPath = Blank(v)); }
    public string? GitHubUrl
    {
        get => Project.GitHubUrl;
        set
        {
            SetDetail(Project.GitHubUrl, value, v => Project.GitHubUrl = Blank(v));
            RefreshIssueSync();
        }
    }
    public string? PromptPreamble { get => Project.PromptPreamble; set => SetDetail(Project.PromptPreamble, value, v => Project.PromptPreamble = Blank(v)); }
    public string? Description { get => Project.Description; set => SetDetail(Project.Description, value, v => Project.Description = Blank(v)); }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    public string ExportNamesText => $"→ {TxtExporter.TodoFileName(Project)}, {TxtExporter.DoneFileName(Project)}";

    private void SetDetail(string? current, string? value, Action<string?> apply, bool listChanged = false, [CallerMemberName] string? property = null)
    {
        if (current == value) return;
        if (Project.Remote != null)
        {
            OnPropertyChanged(property); // another ProJict's details are its owner's
            return;
        }
        apply(value);
        Touch();
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(ExportNamesText));
        if (listChanged) _state.NotifyListChanged();
    }

    /// <summary>Chat channels about the project itself (its categories and items show their own).</summary>
    /// <summary>The wiki pages about the project, as chips on its header.</summary>
    public List<WikiPageChip> WikiPages => Main.WikiChips(Project);
    public bool HasWikiPages => WikiPages.Count > 0;

    public void RefreshWikiPages()
    {
        OnPropertyChanged(nameof(WikiPages));
        OnPropertyChanged(nameof(HasWikiPages));
    }

    /// <summary>Your own projects have wiki pages about them (the Inbox and others' projects don't get the Wiki button).</summary>
    public bool CanHaveWikiPages => Project.Remote == null && !Project.IsInbox;

    /// <summary>The header's Wiki button: the project's first page, or a new one about it.</summary>
    [RelayCommand]
    private void OpenWiki() => Main.ShowProjectWiki(Project);

    public List<ChannelChip> Channels => Main.ChannelChips(Project.Id);
    public bool HasChannels => Channels.Count > 0;

    /// <summary>Channels were started, deleted or read: the chips on the header, categories and items follow.</summary>
    public void RefreshChannels()
    {
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(HasChannels));
        foreach (var category in Categories)
        {
            category.RefreshChannels();
            foreach (var row in category.AllRows) row.RefreshChannels();
        }
    }

    /// <summary>The header's Chat button: the project's channel, or the form to start one.</summary>
    [RelayCommand]
    private void OpenChat()
    {
        if (Main.ChannelsFor(Project.Id).FirstOrDefault() is { } channel) Main.ShowChannel(channel);
        else Main.StartChannel(Project);
    }

    /// <summary>After the GitHub check linked this project to a repository.</summary>
    public void RefreshGitHubLink()
    {
        OnPropertyChanged(nameof(GitHubUrl));
        RefreshIssueSync();
    }

    /// <summary>Details › Private: only you change it (agents and guests can't).</summary>
    public bool IsPrivate
    {
        get => Project.Private;
        set => Main.SetPrivate(Project, value);
    }

    public bool CanBePrivate => Project.Remote == null && !Project.IsInbox;

    public void RefreshPrivate() => OnPropertyChanged(nameof(IsPrivate));

    /// <summary>After groups were added, renamed or deleted elsewhere.</summary>
    public void RefreshGroups()
    {
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(Group));
    }

    // ───────────── Test build ─────────────

    /// <summary>The current test build, on the page under the header with Run (your own projects: the paths are this computer's).</summary>
    public bool HasTestBuild => Project.TestBuild != null && Project.Remote == null;
    private string? TestBuildFullPath => Project.TestBuild?.FullPath(Project.RepoPath);
    public bool TestBuildExists => TestBuildFullPath is { } p && (File.Exists(p) || Directory.Exists(p));
    public string TestBuildName => TestBuildFullPath is { } p ? System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) : "";
    public string? TestBuildToolTip => TestBuildFullPath;
    /// <summary>"built 1 Nov 20:25 · set by Wren: 0.1.31 with the office zoom", or that it's gone.</summary>
    public string TestBuildDetails
    {
        get
        {
            if (Project.TestBuild is not { } build || TestBuildFullPath is not { } path) return "";
            if (!TestBuildExists) return "not there any more · set by " + build.SetBy;
            var built = File.Exists(path) ? File.GetLastWriteTime(path) : Directory.GetLastWriteTime(path);
            var when = built.ToString(built.Date == DateTime.Today ? "'today' HH:mm" : "d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture);
            return $"built {when} · set by {build.SetBy}" + (build.Note is { } note ? $": {note}" : "");
        }
    }
    public string RunTestBuildText => TestBuildFullPath is { } p && Directory.Exists(p) ? "Open" : "Run";

    /// <summary>Details › Test build: a path (full, or in the repo folder); empty removes it.</summary>
    public string? TestBuildPath
    {
        get => Project.TestBuild?.Path;
        set
        {
            var path = value?.Trim().Trim('"');
            if (path == (Project.TestBuild?.Path ?? "") || (string.IsNullOrEmpty(path) && Project.TestBuild == null)) return;
            Project.TestBuild = string.IsNullOrEmpty(path) ? null : new TestBuild { Path = path, Note = Project.TestBuild?.Note, SetBy = _state.UserName };
            _state.MarkDirty(Project);
            RefreshTestBuild();
        }
    }

    public string? TestBuildNote
    {
        get => Project.TestBuild?.Note;
        set
        {
            if (Project.TestBuild is not { } build || (value ?? "") == (build.Note ?? "")) return;
            build.Note = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _state.MarkDirty(Project);
            RefreshTestBuild();
        }
    }

    public void RefreshTestBuild()
    {
        foreach (var p in new[] { nameof(HasTestBuild), nameof(TestBuildExists), nameof(TestBuildName), nameof(TestBuildToolTip), nameof(TestBuildDetails),
                     nameof(RunTestBuildText), nameof(TestBuildPath), nameof(TestBuildNote) })
            OnPropertyChanged(p);
    }

    [RelayCommand]
    private void RunTestBuild()
    {
        if (TestBuildFullPath is not { } path) return;
        RefreshTestBuild();
        if (Directory.Exists(path)) Try(() => ShellLauncher.OpenFolder(path));
        else if (File.Exists(path))
        {
            Try(() => ShellLauncher.Run(path));
            _state.StatusText = $"Started {TestBuildName}.";
        }
        else _state.StatusText = $"{path} isn't there any more.";
    }

    [RelayCommand]
    private void ShowTestBuild()
    {
        if (TestBuildFullPath is not { } path) return;
        if (File.Exists(path)) Try(() => ShellLauncher.ShowInFolder(path));
        else if (Directory.Exists(path)) Try(() => ShellLauncher.OpenFolder(path));
        else _state.StatusText = $"{path} isn't there any more.";
    }

    [RelayCommand]
    private void BrowseTestBuild()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "The current test build", Filter = "Apps and installers|*.exe;*.msi;*.bat;*.cmd;*.lnk|All files|*.*" };
        var start = TestBuildFullPath is { } current ? System.IO.Path.GetDirectoryName(current) : Project.RepoPath;
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog() == true) TestBuildPath = dialog.FileName;
    }

    private void NotifyDetails()
    {
        foreach (var p in new[] { nameof(Name), nameof(Key), nameof(FilePrefix), nameof(Group), nameof(Status), nameof(RepoPath), nameof(GitHubUrl), nameof(PromptPreamble), nameof(Description) })
            OnPropertyChanged(p);
        RefreshIssueSync();
        RefreshShip();
        RefreshPublish();
        RefreshTestBuild();
        RefreshCleanup();
        RefreshStudio();
    }

    // ───────────── Board state ─────────────

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _tagFilter = AllTags;
    [ObservableProperty] private StateFilterOption _stateFilter = StateFilters[0];
    [ObservableProperty] private bool _showDone;
    [ObservableProperty] private bool _sideBySide;
    [ObservableProperty] private int _selectedTab;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnTagFilterChanged(string value) => ApplyFilter();
    partial void OnStateFilterChanged(StateFilterOption value)
    {
        OnPropertyChanged(nameof(ShowWorkingOn));
        ApplyFilter();
    }

    /// <summary>
    /// The toolbar's "Working on" toggle: only items someone is on, i.e. checked out by an AI agent or marked in progress.
    /// A shortcut for the state filter's "In progress", so the two never disagree.
    /// </summary>
    public bool ShowWorkingOn
    {
        get => StateFilter == StateFilters[2];
        set
        {
            if (value != ShowWorkingOn) StateFilter = value ? StateFilters[2] : StateFilters[0];
        }
    }

    /// <summary>"Working on" found nothing: the board says why, e.g. that a connected agent hasn't checked anything out.</summary>
    public bool ShowWorkingOnHint => ShowWorkingOn && !IsFocused && !Categories.Any(c => c.IsVisible);

    public string WorkingOnHintText
    {
        get
        {
            var agents = Main.Agents.AgentsIn(Project.Id);
            if (agents.Count == 0) return "Nothing is in progress: no item is checked out by an AI agent or marked in progress.";
            var names = agents.Count == 1 ? agents[0] : string.Join(", ", agents.Take(agents.Count - 1)) + " and " + agents[^1];
            return $"Nothing is in progress. {names} {(agents.Count == 1 ? "is" : "are")} connected to {Project.Name} but {(agents.Count == 1 ? "hasn't" : "haven't")} checked out an item.";
        }
    }

    public void RefreshWorkingOnHint()
    {
        OnPropertyChanged(nameof(ShowWorkingOnHint));
        OnPropertyChanged(nameof(WorkingOnHintText));
    }

    [RelayCommand] private void ShowAgents() => Main.ShowAgents(Project);

    partial void OnShowDoneChanged(bool value)
    {
        foreach (var c in Categories) c.IsDoneExpanded = value;
    }

    partial void OnSideBySideChanged(bool value)
    {
        _state.Settings.SideBySide = value;
        _state.MarkSettingsDirty();
        foreach (var c in Categories) c.RaiseShowDone();
    }

    /// <summary>
    /// Hide empty: the board leaves out categories with nothing left to do (only done items, or none), for every project.
    /// A category you add meanwhile stays until you toggle it again, so it doesn't vanish before you fill it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideEmptyText), nameof(HideEmptyGlyph))]
    private bool _hideEmpty;

    private readonly HashSet<Category> _keepShown = [];

    public string HideEmptyText => HideEmpty ? "Show all" : "Hide empty";
    public string HideEmptyGlyph => HideEmpty ? "\uE890" : "\uED1A";

    public string HideEmptyToolTip
    {
        get
        {
            if (!HideEmpty) return "Hide the categories with nothing left to do (only done items, or none)";
            var hidden = Categories.Count(c => IsHiddenEmpty(c.Model));
            return hidden == 0 ? "Every category has something to do; show all again" : $"{hidden} {(hidden == 1 ? "category is" : "categories are")} hidden: show all again";
        }
    }

    partial void OnHideEmptyChanged(bool value)
    {
        _state.Settings.HideEmptyCategories = value;
        _state.MarkSettingsDirty();
        _keepShown.Clear();
        ApplyFilter();
    }

    private bool IsHiddenEmpty(Category category) =>
        HideEmpty && !_keepShown.Contains(category) && !category.Walk().Any(l => !l.Item.IsDone);

    // ───────────── Priority ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityButtonText), nameof(IsPriorityViewActive))]
    private PriorityFilterOption _priorityFilter = PriorityFilters[0];

    /// <summary>
    /// How open items are listed: in their own order, most pressing first, by name, or newest or oldest first (only on
    /// screen: their own order, used by exports, stays).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityButtonText), nameof(IsPriorityViewActive), nameof(SortByPriority))]
    private BoardSort _sort;

    /// <summary>Open items are listed most pressing first.</summary>
    public bool SortByPriority
    {
        get => Sort == BoardSort.Priority;
        set => Sort = value ? BoardSort.Priority : BoardSort.Board;
    }

    partial void OnPriorityFilterChanged(PriorityFilterOption value) => ApplyFilter();

    partial void OnSortChanged(BoardSort value)
    {
        _state.Settings.ItemSort = value;
        _state.MarkSettingsDirty();
        foreach (var c in Categories) c.Refresh();
        ApplyFilter();
    }

    public bool IsPriorityViewActive => Sort != BoardSort.Board || PriorityFilter is { IsActive: true };

    /// <summary>The toolbar button's label: what the board is filtered or sorted by.</summary>
    public string PriorityButtonText
    {
        get
        {
            var filter = PriorityFilter is { IsActive: true } f ? f.Label : null;
            var sorted = Sort == BoardSort.Board ? null : BoardSorting.Label(Sort);
            return filter != null ? sorted != null ? $"{filter}, {sorted.ToLowerInvariant()}" : filter : sorted ?? "Priority";
        }
    }

    public void SetPriority(ItemRowViewModel row, Priority priority)
    {
        if (row.Item.Priority == priority) return;
        // Redrawn rather than refreshed: when sorting by priority the item moves.
        Mutate(() => row.Item.Priority = priority, [row.Category], row.Item.Id);
        _state.StatusText = priority switch
        {
            Priority.None => "Priority removed.",
            Priority.Hold => "Put on hold: AI agents leave it alone.",
            _ => $"Priority set to {priority}.",
        };
    }

    /// <summary>Reordering by hand doesn't mix with a sorted board; true (with a hint) when it's sorted.</summary>
    private bool SortedByPriority()
    {
        if (Sort == BoardSort.Board) return false;
        _state.StatusText = $"The board is sorted ({BoardSorting.Label(Sort).ToLowerInvariant()}); pick Priority › Board order to reorder items.";
        return true;
    }

    public bool IsFilterActive => Filter.IsActive;

    /// <summary>What the board's filters say, for Core's <see cref="BoardFilter"/> to apply.</summary>
    private BoardFilter Filter => new()
    {
        Text = FilterText,
        Tag = TagFilter is { } tag && tag != AllTags && tag != NoTag ? tag : null,
        Untagged = TagFilter == NoTag,
        State = StateFilter?.State,
        ReviewOnly = StateFilter?.ReviewOnly == true,
        AttentionOnly = StateFilter?.AttentionOnly == true,
        Unread = item => HasUnread(item.Id, item.Comments),
        PriorityAtLeast = PriorityFilter?.AtLeast,
        PriorityOnly = PriorityFilter?.Only,
        WorkedBy = WorkedBy,
    };

    public void Touch() => _state.MarkDirty(Project);

    /// <summary>How you sign notes, comments and history entries.</summary>
    public string UserName => _state.UserName;

    /// <summary>True when every category is folded; the board's toggle then offers "Expand all".</summary>
    public bool AllCollapsed => Categories.Count > 0 && Categories.All(c => !c.IsExpanded);
    public string ToggleAllText => AllCollapsed ? "Expand all" : "Collapse all";
    public string ToggleAllGlyph => AllCollapsed ? "\uE70D" : "\uE70E";

    [RelayCommand]
    private void ToggleAllCategories()
    {
        var expand = AllCollapsed;
        foreach (var category in Categories) category.IsExpanded = expand;
    }

    public void OnCategoryExpandedChanged()
    {
        OnPropertyChanged(nameof(AllCollapsed));
        OnPropertyChanged(nameof(ToggleAllText));
        OnPropertyChanged(nameof(ToggleAllGlyph));
    }

    public void RebuildAll()
    {
        var existing = Categories.ToDictionary(c => c.Model);
        var list = Project.Categories.Select(c => existing.TryGetValue(c, out var vm) ? vm : new CategoryViewModel(this, c)).ToList();
        foreach (var vm in list.Where(v => existing.ContainsKey(v.Model))) vm.Refresh();
        if (!Categories.SequenceEqual(list))
        {
            Categories.Clear();
            foreach (var vm in list) Categories.Add(vm);
        }
        TagFilters = [AllTags, NoTag, .. TagList];
        OnPropertyChanged(nameof(TagFilters));
        ApplyFilter();
        OnCategoryExpandedChanged();
        OnSelectionChanged();
    }

    public void ApplyFilter()
    {
        if (ApplyFocus())
        {
            OnPropertyChanged(nameof(IsFilterActive));
            OnPropertyChanged(nameof(HideEmptyToolTip));
            RefreshWorkingOnHint();
            return;
        }
        var filter = Filter;
        foreach (var category in Categories)
        {
            if (!filter.IsActive)
            {
                foreach (var row in category.AllRows) row.IsVisible = true;
                category.IsVisible = !IsHiddenEmpty(category.Model);
                category.RaiseShowDone();
                continue;
            }

            var (nameMatches, visible) = filter.Apply(category.Model);
            foreach (var row in category.AllRows) row.IsVisible = nameMatches || visible.Contains(row.Item);
            category.IsVisible = (nameMatches || visible.Count > 0) && !IsHiddenEmpty(category.Model);
            category.RaiseShowDone();
        }
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(HideEmptyToolTip));
        RefreshWorkingOnHint();
    }

    public List<ItemRowViewModel> VisibleRows()
    {
        var rows = new List<ItemRowViewModel>();
        foreach (var category in Categories.Where(c => c.IsVisible && c.IsExpanded))
        {
            rows.AddRange(category.OpenRows.Where(r => r.IsVisible));
            if (category.ShowDoneRows) rows.AddRange(category.DoneRows.Where(r => r.IsVisible));
        }
        return rows;
    }

    // ───────────── Focus & text editing ─────────────

    public ItemRowViewModel? FocusedRow { get; private set; }

    public void OnRowGotFocus(ItemRowViewModel row)
    {
        FocusedRow = row;
        _focusedItemId = row.Item.Id;
        _history.EndTyping();
    }

    public void OnRowLostFocus(ItemRowViewModel row)
    {
        if (_focusedItemId == row.Item.Id) _focusedItemId = null;
        _history.EndTyping();
        var item = row.Item;

        // "BUG: crash" typed into the text becomes a tag.
        if (string.IsNullOrEmpty(item.Tag))
        {
            var (tag, text) = TxtListParser.SplitTag(item.Text);
            if (tag != null && text.Length > 0)
            {
                Checkpoint();
                item.Tag = tag;
                item.Text = text;
                Touch();
                row.Refresh();
                RefreshTagFilters();
            }
        }

        // Empty rows left behind (Enter pressed, nothing typed) are removed once focus has really moved on.
        // (One holding only an attachment, e.g. a pasted screenshot, stays.)
        if (item.Text.Trim().Length == 0 && item.Children.Count == 0 && !item.HasAttachments)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_focusedItemId == item.Id || item.Text.Trim().Length > 0 || item.Children.Count > 0 || item.HasAttachments) return;
                if (Project.Find(item.Id) is not { } loc) return;
                loc.Siblings.Remove(item);
                Touch();
                CategoryFor(loc.Category)?.Refresh();
                ApplyFilter();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public void OnHeaderGotFocus() => _history.EndTyping();

    /// <summary>Called before a text field changes; the first change after focusing records one undo step.</summary>
    public void BeforeTextChange()
    {
        if (CanEdit && _history.BeforeTyping(() => Json.Serialize(Project))) NotifyUndo();
    }

    private void RefreshTagFilters()
    {
        TagFilters = [AllTags, NoTag, .. TagList];
        OnPropertyChanged(nameof(TagFilters));
    }

    // ───────────── Undo ─────────────

    public void Checkpoint()
    {
        _history.Checkpoint(Json.Serialize(Project));
        NotifyUndo();
    }

    private bool CanUndo() => _history.CanUndo;
    private bool CanRedo() => _history.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (_history.CanUndo) Restore(_history.Undo(Json.Serialize(Project))!);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        if (_history.CanRedo) Restore(_history.Redo(Json.Serialize(Project))!);
    }

    private void Restore(string json)
    {
        var focus = _focusedItemId;
        Project.RestoreFrom(Json.Deserialize<Project>(json)!);
        Categories.Clear();
        RebuildAll();
        ReloadSideLists();
        NotifyDetails();
        Touch();
        _state.NotifyListChanged();
        NotifyUndo();
        if (focus != null && Project.Find(focus.Value) != null) FocusRequested?.Invoke(focus.Value, -1);
    }

    private void NotifyUndo()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // ───────────── Structural edits ─────────────

    private void Mutate(Action change, IEnumerable<CategoryViewModel?> affected, Guid? focus = null, int caret = -1, EditKind kind = EditKind.Edit)
    {
        if (Refused(kind)) return;
        var hadFocus = _focusedItemId;
        Checkpoint();
        change();
        Touch();
        foreach (var c in affected.OfType<CategoryViewModel>().Distinct()) c.Refresh();
        ApplyFilter();
        var target = focus ?? hadFocus;
        if (target != null && Project.Find(target.Value) != null) FocusRequested?.Invoke(target.Value, caret);
    }

    private CategoryViewModel? CategoryFor(Category category) => Categories.FirstOrDefault(c => c.Model == category);

    private static List<TodoItem> SiblingsOf(ItemRowViewModel row) => row.Parent?.Children ?? row.Category.Model.Items;

    public void AddItem(CategoryViewModel category, string text)
    {
        var item = TxtListParser.ParseLine(text);
        if (item == null) return;
        // Focused on one item, a new one in its category would vanish: the focus widens to the category.
        if (FocusItem != null) FocusOn(category.Model);
        Mutate(() => category.Model.Items.Add(item), [category], kind: EditKind.Add);
        RefreshTagFilters();
    }

    /// <summary>
    /// Files or an image pasted (or dropped) into a category's Add item box: adds the item with what was typed so far
    /// and attaches them, then puts the cursor in it so its text can be written or finished.
    /// </summary>
    public async Task AddItemWithAttachments(CategoryViewModel category, IReadOnlyList<string> files, byte[]? png)
    {
        if (FolderZips.TakesFolders(files, zipped => _ = AddItemWithAttachments(category, zipped, png), s => _state.StatusText = s)) return;
        if (FocusItem != null) FocusOn(category.Model);
        List<Attachment> added;
        try
        {
            added = await Task.Run(() => files.Where(File.Exists).Select(_state.Store.ImportAttachment).ToList());
            if (png != null) added.Add(_state.Store.WriteAttachment($"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png", png));
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
            return;
        }
        if (added.Count == 0)
        {
            _state.StatusText = "Only files can be attached, not folders.";
            return;
        }
        var item = TxtListParser.ParseLine(category.NewItemText) ?? new TodoItem();
        item.Attachments = added;
        category.NewItemText = "";
        Mutate(() => category.Model.Items.Add(item), [category], item.Id, kind: EditKind.Add);
        RefreshTagFilters();
        _state.StatusText = added.Count == 1 ? $"Added an item with {added[0].FileName}." : $"Added an item with {added.Count} files.";
    }

    public void InsertSibling(ItemRowViewModel row, bool before)
    {
        // On a board sorted by priority, a new item with the same priority shows up right next to this one.
        var item = new TodoItem { Priority = SortByPriority && !row.InDoneSection ? row.Item.Priority : Priority.None };
        Mutate(() =>
        {
            var list = SiblingsOf(row);
            if (row.InDoneSection && row.Parent == null) row.Category.Model.Items.Add(item);
            else list.Insert(list.IndexOf(row.Item) + (before ? 0 : 1), item);
        }, [row.Category], before ? row.Item.Id : item.Id, before ? 0 : -1);
    }

    public void AddChild(ItemRowViewModel row)
    {
        var item = new TodoItem();
        Mutate(() => row.Item.Children.Add(item), [row.Category], item.Id);
    }

    public void Indent(ItemRowViewModel row, int caret)
    {
        if (SortedByPriority()) return;
        var list = SiblingsOf(row);
        if (BoardEdits.IndentTarget(list, row.Item, topLevel: row.Parent == null) is not { } under) return;
        Mutate(() => BoardEdits.Indent(list, row.Item, under), [row.Category], row.Item.Id, caret);
    }

    public void Outdent(ItemRowViewModel row, int caret)
    {
        if (row.Parent == null || Project.Find(row.Parent.Id) == null) return;
        Mutate(() => BoardEdits.Outdent(Project, row.Item, row.Parent), [row.Category], row.Item.Id, caret);
    }

    public void Move(ItemRowViewModel row, int delta, int caret)
    {
        if (SortedByPriority()) return;
        var list = SiblingsOf(row);
        if (BoardEdits.MoveTarget(list, row.Item, delta, topLevel: row.Parent == null) is { } target)
        {
            Mutate(() => BoardEdits.MoveTo(list, row.Item, target), [row.Category], row.Item.Id, caret);
            return;
        }

        // At the edge of a category, a top-level item hops into the neighbouring category.
        if (row.Parent != null) return;
        var categoryIndex = Categories.IndexOf(row.Category);
        var neighbour = categoryIndex + delta;
        if (neighbour < 0 || neighbour >= Categories.Count) return;
        var other = Categories[neighbour];
        Mutate(() =>
        {
            BoardEdits.MoveInto(list, row.Item, other.Model, delta);
            other.IsExpanded = true;
        }, [row.Category, other], row.Item.Id, caret);
    }

    public void SetState(ItemRowViewModel row, ItemState state)
    {
        if (row.Item.State == state)
        {
            row.Refresh();
            return;
        }
        Guid? focus = null;
        // Finishing the focused item moves the cursor on to the next one, like ticking down a list.
        if (_focusedItemId == row.Item.Id && row.Parent == null && (state == ItemState.Done || row.Item.IsDone))
            focus = NextRowAfter(row)?.Item.Id ?? row.Item.Id;
        // An agent's work you mark not done goes back to it: the history says so, and the agent is told.
        var sentBack = row.Item.NeedsReview && state != ItemState.Done;
        Mutate(() =>
        {
            row.Item.SetState(state, _state.UserName);
            if (sentBack) row.Item.AddActivity(_state.UserName, $"{ActivityEntry.SentBack}: not accepted yet.", agent: false);
        }, [row.Category], focus);
    }

    private ItemRowViewModel? NextRowAfter(ItemRowViewModel row)
    {
        var rows = VisibleRows();
        var i = rows.IndexOf(row);
        if (i < 0) return null;
        var j = i + 1;
        while (j < rows.Count && rows[j].Category == row.Category && rows[j].Depth > row.Depth && rows[j].InDoneSection == row.InDoneSection) j++;
        if (j < rows.Count) return rows[j];
        return i > 0 ? rows[i - 1] : null;
    }

    public void Delete(ItemRowViewModel row, bool focusPrevious)
    {
        var count = row.Item.Descendants().Count();
        if (count > 0 && MessageBox.Show(Application.Current.MainWindow!,
                $"Delete \"{Shorten(row.Item.Text)}\" and its {count} sub-item(s)?", "Delete item",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var rows = VisibleRows();
        var i = rows.IndexOf(row);
        var focus = focusPrevious ? (i > 0 ? rows[i - 1] : NextRowAfter(row)) : NextRowAfter(row);
        if (focus?.Item == row.Item) focus = null;
        Mutate(() => SiblingsOf(row).Remove(row.Item), [row.Category], focus?.Item.Id);
    }

    /// <summary>The tag picker's Other…: a type of your own, kept in the list for every project, given to the item.</summary>
    public void AddNewTag(ItemRowViewModel row)
    {
        var typed = InputDialog.Ask(Application.Current.MainWindow!, "New type", "A type for items, like BUG or ADD (it's added to the list):", "");
        if (Core.Features.TagChoices.Clean(typed) is not { } tag) return;
        if (Core.Features.TagChoices.Remember(_state.Settings.CustomTags ??= [], tag)) _state.MarkSettingsDirty();
        SetTag(row, tag);
    }

    public void SetTag(ItemRowViewModel row, string? tag)
    {
        Checkpoint();
        row.Item.Tag = tag;
        Touch();
        row.Refresh();
        RefreshTagFilters();
        ApplyFilter();
    }

    public void MoveToCategory(ItemRowViewModel row, CategoryViewModel target)
    {
        if (target.Model == row.Category.Model && row.Parent == null) return;
        Mutate(() =>
        {
            SiblingsOf(row).Remove(row.Item);
            target.Model.Items.Add(row.Item);
        }, [row.Category, target]);
    }

    /// <summary>Drag &amp; drop: place <paramref name="source"/> before or after <paramref name="target"/> as its sibling.</summary>
    public void MoveItem(ItemRowViewModel source, ItemRowViewModel target, bool after)
    {
        if (!BoardEdits.CanPlaceBeside(source.Item, target.Item) || SortedByPriority()) return;
        Mutate(() => BoardEdits.PlaceBeside(SiblingsOf(source), source.Item, SiblingsOf(target), target.Item, after), [source.Category, target.Category]);
    }

    public void MoveCategory(CategoryViewModel category, int delta)
    {
        var target = Project.Categories.IndexOf(category.Model) + delta;
        if (target < 0 || target >= Project.Categories.Count || Refused(EditKind.Edit)) return;
        Checkpoint();
        BoardEdits.MoveCategory(Project, category.Model, target);
        Touch();
        RebuildAll();
    }

    /// <summary>Moves a category to the top of the board, or to the bottom.</summary>
    public void MoveCategoryToEnd(CategoryViewModel category, bool top)
    {
        var index = Project.Categories.IndexOf(category.Model);
        var target = top ? 0 : Project.Categories.Count - 1;
        if (index < 0 || index == target || Refused(EditKind.Edit)) return;
        Checkpoint();
        BoardEdits.MoveCategory(Project, category.Model, target);
        Touch();
        RebuildAll();
    }

    public void MoveCategoryTo(CategoryViewModel category, CategoryViewModel target)
    {
        if (category == target || Refused(EditKind.Edit)) return;
        Checkpoint();
        BoardEdits.MoveCategoryBefore(Project, category.Model, target.Model);
        Touch();
        RebuildAll();
    }

    [RelayCommand]
    private void AddCategory()
    {
        var name = InputDialog.Ask(Application.Current.MainWindow!, "New category", "Category name (ALL CAPS matches your TXT files):", "");
        if (!string.IsNullOrWhiteSpace(name)) AddNamedCategory(name);
    }

    private void AddNamedCategory(string name)
    {
        Checkpoint();
        var category = new Category { Name = name.Trim() };
        if (HideEmpty) _keepShown.Add(category);
        Project.Categories.Add(category);
        Touch();
        RebuildAll();
        if (CategoryFor(category) is { } vm) FocusAddItemRequested?.Invoke(vm);
    }

    /// <summary>Adds the categories of another project that this one lacks (without items) as one undo step.</summary>
    public int CopyCategoriesFrom(Project source)
    {
        if (source.Categories.All(c => Project.FindCategory(c.Name) != null)) return 0;
        Checkpoint();
        var added = Project.CopyCategoriesFrom(source);
        Touch();
        RebuildAll();
        return added.Count;
    }

    public void DeleteCategory(CategoryViewModel category)
    {
        var count = category.Model.Walk().Count();
        if (count > 0 && MessageBox.Show(Application.Current.MainWindow!,
                $"Delete category \"{category.Name}\" with {count} item(s)?", "Delete category",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Checkpoint();
        Project.Categories.Remove(category.Model);
        Touch();
        RebuildAll();
    }

    public void RequestAddItem(CategoryViewModel category)
    {
        category.IsExpanded = true;
        FocusAddItemRequested?.Invoke(category);
    }

    public void PromoteToProject(ItemRowViewModel row)
    {
        var lines = row.Item.Text.Replace("\r\n", "\n").Split('\n');
        var name = lines[0].Trim();
        if (name.Length == 0) return;
        if (MessageBox.Show(Application.Current.MainWindow!,
                $"Create a new project \"{Shorten(name)}\" from this item? It will be removed from {Project.Name}.",
                "Promote to project", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var project = Core.Model.Project.Create(name, Project.IsInbox ? "Personal" : Project.Group);
        project.Status = ProjectStatus.Idea;
        // Made from a private project's item, it stays private.
        project.Private = Project.Private;
        project.Description = lines.Length > 1 ? string.Join("\n", lines.Skip(1)).Trim() : null;
        project.Categories.Add(new Category { Name = "GENERAL", Items = row.Item.Children.ToList() });
        Mutate(() => SiblingsOf(row).Remove(row.Item), [row.Category]);
        _state.AddProject(project);
        Main.OpenProject(project);
    }

    // ───────────── Selection & AI prompt ─────────────

    public IEnumerable<ItemRowViewModel> AllRows => Categories.SelectMany(c => c.AllRows);

    [RelayCommand]
    private void CopyPrompt()
    {
        var selected = AllRows.Where(r => r.IsSelected).Select(r => r.Item).Distinct().ToList();
        CopyPromptFor(selected.Count > 0 ? selected : Project.Categories.SelectMany(c => c.Items).Where(i => !i.IsDone));
    }

    /// <summary>The item's short id as Chat and agents write it (#4e84570c), for pasting into a message or a prompt.</summary>
    public static string ShortIdOf(TodoItem item) => "#" + item.Id.ToString()[..8];

    public void CopyIdOf(TodoItem item)
    {
        var text = ShortIdOf(item);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
            return;
        }
        _state.StatusText = $"Copied {text} to the clipboard.";
    }

    public void CopyPromptFor(IEnumerable<TodoItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            _state.StatusText = "Nothing to copy — no open items.";
            return;
        }
        var text = PromptBuilder.Build(Project, list, _state.Store.AttachmentPath, _state.Settings.AgentInstructions);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
            return;
        }
        if (_state.Settings.MarkCopiedInProgress)
        {
            var todo = list.Where(i => i.State == ItemState.Todo).ToList();
            if (todo.Count > 0) Mutate(() => todo.ForEach(i => i.State = ItemState.InProgress), Categories);
        }
        _state.StatusText = $"Copied a prompt with {list.Count} item(s) to the clipboard.";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in AllRows) row.IsSelected = false;
    }

    /// <summary>Items are selected for a prompt (Ctrl+click): the toolbar then offers to clear the selection.</summary>
    public bool HasSelection => AllRows.Any(r => r.IsSelected);

    public void OnSelectionChanged() => OnPropertyChanged(nameof(HasSelection));

    // ───────────── Attachments ─────────────

    public string AttachmentPath(Attachment attachment) => _state.AttachmentPath(Project, attachment);

    /// <summary>Another ProJict's attachment: fetched from it the first time it's shown.</summary>
    public void EnsureFile(Attachment attachment)
    {
        if (Project.Remote != null) Main.Guest.Fetch(Project, attachment);
    }

    public void BrowseAttachments(ItemRowViewModel row)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files", Multiselect = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) _ = AttachFiles(row, dialog.FileNames);
    }

    /// <summary>Copies the files into the data folder (off the UI thread, they may be large) and attaches them to the row's item.</summary>
    public async Task AttachFiles(ItemRowViewModel row, IReadOnlyList<string> paths)
    {
        if (FolderZips.TakesFolders(paths, zipped => _ = AttachFiles(row, zipped), s => _state.StatusText = s)) return;
        // Video and audio are big: linked where they are rather than copied.
        var big = paths.Where(p => File.Exists(p) && MediaLinks.IsBig(p)).ToList();
        if (big.Count > 0)
        {
            LinkMedia(row, big);
            paths = paths.Except(big).ToList();
            if (paths.Count == 0) return;
        }
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            _state.StatusText = "Only files can be attached, not folders.";
            return;
        }
        _state.StatusText = files.Count == 1 ? $"Attaching {Path.GetFileName(files[0])}…" : $"Attaching {files.Count} files…";
        List<Attachment> added;
        try
        {
            added = await Task.Run(() => files.Select(_state.Store.ImportAttachment).ToList());
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
            return;
        }
        AddAttachments(row.Item.Id, added);
    }

    /// <summary>Attaches what was copied to the clipboard: files, or an image such as a screenshot.</summary>
    public void PasteAttachments(ItemRowViewModel row)
    {
        var (files, png) = ClipboardAttachments.Read();
        if (files.Count > 0)
        {
            _ = AttachFiles(row, files);
            return;
        }
        if (png == null)
        {
            _state.StatusText = "The clipboard has no image or files to attach.";
            return;
        }
        try
        {
            AddAttachments(row.Item.Id, [_state.Store.WriteAttachment($"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png", png)]);
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
        }
    }

    private void AddAttachments(Guid itemId, List<Attachment> added)
    {
        // Found again by id: an undo while the files were copying replaces the item objects.
        if (Project.Find(itemId) is not { } loc)
        {
            _state.StatusText = "The item was removed before its files were attached.";
            return;
        }
        Mutate(() => (loc.Item.Attachments ??= []).AddRange(added), [CategoryFor(loc.Category)]);
        _state.StatusText = added.Count == 1 ? $"Attached {added[0].FileName}." : $"Attached {added.Count} files.";
    }

    public void RemoveAttachment(ItemRowViewModel row, Attachment attachment)
    {
        Mutate(() =>
        {
            row.Item.Attachments?.Remove(attachment);
            if (row.Item.Attachments is { Count: 0 }) row.Item.Attachments = null;
        }, [row.Category]);
        _state.StatusText = $"Removed {attachment.FileName} (Ctrl+Z brings it back).";
    }

    public void BrowseNoteAttachments(NoteViewModel note)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files to the note", Multiselect = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) _ = AttachFilesToNote(note.Model, dialog.FileNames);
    }

    private async Task AttachFilesToNote(NoteDoc note, IReadOnlyList<string> paths)
    {
        List<Attachment> added;
        try
        {
            added = await Task.Run(() => paths.Where(File.Exists).Select(_state.Store.ImportAttachment).ToList());
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not attach: " + ex.Message;
            return;
        }
        if (added.Count == 0 || !Project.Notes.Contains(note)) return;
        Checkpoint();
        (note.Attachments ??= []).AddRange(added);
        Touch();
        Notes.FirstOrDefault(n => n.Model == note)?.Refresh();
        _state.StatusText = added.Count == 1 ? $"Attached {added[0].FileName}." : $"Attached {added.Count} files.";
    }

    public void RemoveNoteAttachment(NoteDoc note, Attachment attachment)
    {
        Checkpoint();
        note.Attachments?.Remove(attachment);
        if (note.Attachments is { Count: 0 }) note.Attachments = null;
        Touch();
        Notes.FirstOrDefault(n => n.Model == note)?.Refresh();
        _state.StatusText = $"Removed {attachment.FileName} (Ctrl+Z brings it back).";
    }

    // Programs and scripts are shown in their folder instead of being run by a stray click.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".scr", ".lnk", ".url", ".reg", ".hta", ".cpl" };

    /// <summary>Pictures open in ProJict's own viewer (where they can be marked up); other files in their usual app.</summary>
    public void OpenAttachment(AttachmentViewModel attachment)
    {
        if (RunnableExtensions.Contains(Path.GetExtension(attachment.FileName)))
        {
            ShowAttachmentInFolder(attachment);
            return;
        }
        if (!AttachmentExists(attachment)) return;
        if (attachment.OpensInViewer)
        {
            Try(() => new ImageViewerWindow(attachment.Path, attachment.FileName, attachment.SaveCopy) { Owner = Application.Current.MainWindow }.Show());
            return;
        }
        Try(() => ShellLauncher.OpenFile(attachment.Path));
    }

    public void OpenAttachmentExternally(AttachmentViewModel attachment)
    {
        if (AttachmentExists(attachment)) Try(() => ShellLauncher.OpenFile(attachment.Path));
    }

    public Attachment WriteAttachment(string fileName, byte[] content) => _state.Store.WriteAttachment(fileName, content);

    /// <summary>Adds an attachment (e.g. a marked-up copy) to an item; one undo step.</summary>
    public void AttachToItem(Guid itemId, Attachment attachment) => AddAttachments(itemId, [attachment]);

    public void AttachToNote(NoteDoc note, Attachment attachment)
    {
        if (!Project.Notes.Contains(note)) return;
        Checkpoint();
        (note.Attachments ??= []).Add(attachment);
        Touch();
        Notes.FirstOrDefault(n => n.Model == note)?.Refresh();
        _state.StatusText = $"Attached {attachment.FileName}.";
    }

    public void ShowAttachmentInFolder(AttachmentViewModel attachment)
    {
        if (AttachmentExists(attachment)) Try(() => ShellLauncher.ShowInFolder(attachment.Path));
    }

    private bool AttachmentExists(AttachmentViewModel attachment)
    {
        if (File.Exists(attachment.Path))
        {
            // Opening one from the Chat cache counts as using it: it stays as long again.
            if (attachment.Model.Cached) PrivateFiles.Touch(attachment.Path);
            return true;
        }
        _state.StatusText = attachment.CleanedOut
            ? $"{attachment.FileName} was cleaned out: nobody had opened it for {_state.AppSettings.PrivateFilesDays} days (Settings › Chat)."
            : $"{attachment.FileName} is missing from the data folder.";
        return false;
    }

    // ───────────── Review & agent edits ─────────────

    public void AcceptReview(ItemRowViewModel row)
    {
        if (!row.Item.NeedsReview) return;
        Mutate(() =>
        {
            row.Item.NeedsReview = false;
            row.Item.AddActivity(_state.UserName, "Accepted the work.", agent: false);
        }, [row.Category]);
        _state.StatusText = $"Accepted “{Shorten(row.Item.Text)}”.";
    }

    /// <summary>Asks how long you spent on an item ("1h 30m"); tests answer for you. Null when cancelled.</summary>
    public Func<string, string?> AskTime { get; set; } = prompt =>
        Views.InputDialog.Ask(Application.Current.MainWindow!, "Log time", prompt, "");

    /// <summary>Time you spent on an item, by hand ("1h 30m"): it counts in the project's Time tab.</summary>
    public void LogTime(ItemRowViewModel row)
    {
        if (AskTime($"How long did you spend on “{Shorten(row.Item.Text)}”? For example 1h 30m, 45m or 2:15.") is not { } typed) return;
        if (WorkLog.ParseMinutes(typed) is not { } minutes || minutes is <= 0 or > 24 * 60)
        {
            _state.StatusText = $"“{typed.Trim()}” isn't a length of time up to a day; try 1h 30m, 45m or 2:15.";
            return;
        }
        Mutate(() => WorkLog.Log(row.Item, _state.UserName, agent: false, minutes), [row.Category], row.Item.Id);
        _state.StatusText = $"Logged {WorkLog.Format(TimeSpan.FromMinutes(minutes))} on “{Shorten(row.Item.Text)}”.";
        RefreshTime();
    }

    /// <summary>Frees an item an agent left checked out: back to do, so any agent can pick it up.</summary>
    public void ReleaseClaim(ItemRowViewModel row)
    {
        if (row.Item.ClaimedBy is not { } agent) return;
        Mutate(() =>
        {
            row.Item.SetState(ItemState.Todo);
            row.Item.AddActivity(_state.UserName, $"Released {agent}'s check-out.", agent: false);
        }, [row.Category]);
        _state.StatusText = $"Released “{Shorten(row.Item.Text)}”.";
    }

    /// <summary>
    /// A change from outside the board (an AI agent, the discussion panel): one undo step, the affected categories
    /// redrawn, and the cursor put back where it was if you were typing in the board (redrawn rows lose focus).
    /// </summary>
    private void MutateKeepingCursor(Action change, IEnumerable<CategoryViewModel?> affected, bool rebuildAll = false, EditKind kind = EditKind.Edit)
    {
        if (Refused(kind)) return;
        var focus = _focusedItemId;
        var caret = focus is { } focused ? CaretOf?.Invoke(focused) : null;
        Checkpoint();
        change();
        Touch();
        if (rebuildAll) RebuildAll();
        else foreach (var c in affected.OfType<CategoryViewModel>().Distinct()) c.Refresh();
        RefreshTagFilters();
        ApplyFilter();
        if (focus is { } id && caret is { } at && Project.Find(id) != null) FocusRequested?.Invoke(id, at);
    }

    /// <summary>An AI agent changes the project's details, as one undo step (shipping settings stay out of undo, as ever).</summary>
    public void AgentUpdateDetails(Action<Project> change)
    {
        Checkpoint();
        change(Project);
        Touch();
        NotifyDetails();
        _state.NotifyListChanged();
    }

    public void AgentEdit(Guid itemId, Action<TodoItem> change)
    {
        var loc = Project.Find(itemId) ?? throw new InvalidOperationException("The item is no longer in this project.");
        MutateKeepingCursor(() => change(loc.Item), [CategoryFor(loc.Category)]);
    }

    /// <summary>Adds an item under a parent item, or at the end of a category (created when missing).</summary>
    public void AgentAdd(TodoItem item, string? categoryName, Guid? parentId)
    {
        if (parentId is { } pid)
        {
            var parent = Project.Find(pid) ?? throw new InvalidOperationException("The parent item is not in this project.");
            MutateKeepingCursor(() => parent.Item.Children.Add(item), [CategoryFor(parent.Category)], kind: EditKind.Add);
            return;
        }
        var category = Project.FindCategory(categoryName ?? "");
        if (category != null)
        {
            MutateKeepingCursor(() => category.Items.Add(item), [CategoryFor(category)], kind: EditKind.Add);
            return;
        }
        MutateKeepingCursor(() => Project.Categories.Add(new Category { Name = categoryName!.Trim(), Items = [item] }), [], rebuildAll: true, kind: EditKind.Add);
    }

    /// <summary>
    /// A change from GitHub (an issue sync): one undo step if <paramref name="change"/> says it changed anything, the
    /// board redrawn and the cursor put back. Returns whether anything changed.
    /// </summary>
    public bool ExternalChange(Func<bool> change)
    {
        var snapshot = Json.Serialize(Project);
        var focus = _focusedItemId;
        var caret = focus is { } focused ? CaretOf?.Invoke(focused) : null;
        if (!change()) return false;
        _history.Checkpoint(snapshot);
        NotifyUndo();
        Touch();
        RebuildAll();
        if (focus is { } id && caret is { } at && Project.Find(id) != null) FocusRequested?.Invoke(id, at);
        return true;
    }

    // ───────────── Shared from another ProJict ─────────────

    /// <summary>False on a guest for another ProJict's project when your role doesn't allow changing it (or it's disconnected).</summary>
    public bool CanEdit => Project.Remote is not { } r || (r.Connected && GuestAccess.CanEdit(r.Role));
    public bool CanAddItems => Project.Remote is not { } r || (r.Connected && GuestAccess.CanAddItems(r.Role));
    public bool CanComment => Project.Remote is not { } r || (r.Connected && GuestAccess.CanComment(r.Role));
    public bool IsReadOnlyBoard => !CanEdit;
    public bool IsRemote => Project.Remote != null;
    public bool IsTitleReadOnly => Project.IsInbox || Project.Remote != null;
    public bool IsOwnProject => Project.Remote == null;
    /// <summary>Channels about it can be started: yours, or another ProJict's where you're an editor.</summary>
    public bool CanStartChannels => Project.Remote is not { } r || r.Role == ProJict.Core.Sharing.GuestRole.Editor;

    /// <summary>"Shared by Sam · you're an editor", under the title of another ProJict's project.</summary>
    public string? RemoteText => Project.Remote is { } r
        ? $"Shared by {r.HostName} · you're {(r.Role == GuestRole.Editor ? "an" : "a")} {r.Role.ToString().ToLowerInvariant()}" + (r.Connected ? "" : " · not connected, reconnecting…")
        : null;

    /// <summary>On a guest, whether your role allows the change; the status bar says why not.</summary>
    private bool Refused(EditKind kind)
    {
        if (Project.Remote is not { } remote) return false;
        var allowed = remote.Connected && kind switch
        {
            EditKind.Add => GuestAccess.CanAddItems(remote.Role),
            EditKind.Comment => GuestAccess.CanComment(remote.Role),
            _ => GuestAccess.CanEdit(remote.Role),
        };
        if (!allowed)
            _state.StatusText = !remote.Connected
                ? $"Not connected to {remote.HostName}'s ProJict: nothing can change until it reconnects."
                : $"As a {remote.Role.ToString().ToLowerInvariant()} in {remote.HostName}'s ProJict you can't {(kind == EditKind.Add ? "add items" : kind == EditKind.Comment ? "comment" : "change this")}.";
        return !allowed;
    }

    /// <summary>After the role or the connection changed: what may be edited.</summary>
    public void RefreshAccess()
    {
        foreach (var p in new[] { nameof(CanEdit), nameof(CanAddItems), nameof(CanComment), nameof(IsReadOnlyBoard), nameof(IsRemote), nameof(IsOwnProject), nameof(RemoteText), nameof(IsTitleReadOnly) })
            OnPropertyChanged(p);
        // Rows and categories bind Owner.CanEdit: redraw them.
        foreach (var category in Categories) category.Refresh();
    }

    /// <summary>
    /// On a guest: the host's version replaces this one. The undo history goes with it, since undoing would undo other
    /// people's work.
    /// </summary>
    public void ReplaceFromHost(Project snapshot)
    {
        var focus = _focusedItemId;
        var caret = focus is { } focused ? CaretOf?.Invoke(focused) : null;
        Project.RestoreFrom(snapshot, keepIssueLinks: false);
        _history.Clear();
        NotifyUndo();
        RebuildAll();
        ReloadSideLists();
        NotifyDetails();
        _state.NotifyProjectChanged(Project);
        if (focus is { } id && caret is { } at && Project.Find(id) != null) FocusRequested?.Invoke(id, at);
    }

    /// <summary>On the host: a guest's change (already checked and merged), as one undo step.</summary>
    public void ApplyFromGuest(Project accepted)
    {
        var focus = _focusedItemId;
        var caret = focus is { } focused ? CaretOf?.Invoke(focused) : null;
        Checkpoint();
        Project.RestoreFrom(accepted);
        RebuildAll();
        ReloadSideLists();
        NotifyDetails();
        Touch();
        if (focus is { } id && caret is { } at && Project.Find(id) != null) FocusRequested?.Invoke(id, at);
    }

    // ───────────── GitHub issues ─────────────

    private string? _issueSyncMessage;

    public bool CanSyncIssues => Project.Remote == null && GitHubReleases.ParseRepo(Project.GitHubUrl) != null && !Main.Issues.IsSyncing(Project);
    public bool HasIssueSync => Project.Issues != null;
    public string SyncIssuesText => Project.Issues == null ? "Import issues" : "Sync now";

    public string IssueSyncText => Main.Issues.IsSyncing(Project) ? "Asking GitHub…"
        : _issueSyncMessage ?? Project.Issues?.LastResult
          ?? (GitHubReleases.ParseRepo(Project.GitHubUrl) != null
              ? "Brings the repository's open issues in as items of a GITHUB category. Pull requests that mention an issue (\"Fixes #12\") show on its item."
              : "Enter the project's GitHub URL above to import its issues.");

    /// <summary>Sync when ProJict starts.</summary>
    public bool KeepIssuesInSync
    {
        get => Project.Issues?.Enabled == true;
        set
        {
            if (Project.Issues is not { } sync || sync.Enabled == value) return;
            sync.Enabled = value;
            Touch();
            OnPropertyChanged();
        }
    }

    /// <summary>Finishing an item closes its issue on GitHub, reopening it reopens the issue.</summary>
    public bool CloseIssuesOnGitHub
    {
        get => Project.Issues?.CloseOnGitHub == true;
        set
        {
            if (Project.Issues is not { } sync || sync.CloseOnGitHub == value) return;
            sync.CloseOnGitHub = value;
            Touch();
            OnPropertyChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSyncIssues))]
    private async Task SyncIssues()
    {
        _issueSyncMessage = null;
        var result = await Main.Issues.SyncAsync(Project, manual: true);
        // A first import that failed has nowhere to keep its result.
        if (Project.Issues == null) _issueSyncMessage = result;
        RefreshIssueSync();
    }

    /// <summary>After a sync started or finished, or the GitHub link changed.</summary>
    public void RefreshIssueSync()
    {
        foreach (var p in new[] { nameof(CanSyncIssues), nameof(HasIssueSync), nameof(SyncIssuesText), nameof(IssueSyncText), nameof(KeepIssuesInSync), nameof(CloseIssuesOnGitHub) })
            OnPropertyChanged(p);
        SyncIssuesCommand.NotifyCanExecuteChanged();
    }

    // ───────────── Header buttons ─────────────

    /// <summary>The header's buttons (by name) shown big, on the left of the others: Ship and Clean up until you choose with the cog.</summary>
    public IReadOnlyList<string> BigHeaderButtons => HeaderLayout.Big(_state.AppSettings);

    /// <summary>Shows a header button big or not, for every project on this computer.</summary>
    public void SetBigHeaderButton(string name, bool big)
    {
        HeaderLayout.SetBig(_state.AppSettings, name, big);
        _state.SaveAppSettings();
        OnPropertyChanged(nameof(BigHeaderButtons));
    }

    /// <summary>Back to Ship and Clean up big.</summary>
    public void ResetBigHeaderButtons()
    {
        HeaderLayout.Reset(_state.AppSettings);
        _state.SaveAppSettings();
        OnPropertyChanged(nameof(BigHeaderButtons));
    }

    // ───────────── Shipping ─────────────

    /// <summary>Your own projects can be shipped (not the Inbox, nor another ProJict's project).</summary>
    public bool CanShip => !Project.IsInbox && Project.Remote == null;

    /// <summary>The request the board's card shows: the open one, else the last one until its card is closed.</summary>
    public ShipRequest? Ship => CanShip && Project.Ships?.LastOrDefault() is { } last && (last.IsOpen || !last.Dismissed) ? last : null;

    /// <summary>Choosing the steps of a new request, in the card on the board.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShipCard), nameof(ShowShipRequest))]
    private bool _isComposingShip;

    public bool HasShipCard => IsComposingShip || Ship != null;
    public bool ShowShipRequest => !IsComposingShip && Ship != null;
    public IReadOnlyList<ShipStepChoice> ShipChoices { get; private set; } = [];

    public string ShipTitle => Ship is not { } s ? ""
        : (s.Version is { } v ? $"Shipping {v}: " : "Shipping: ") + ShipFlow.Describe(s.Steps);
    public string ShipStateText => Ship is { } s ? char.ToUpperInvariant(ShipFlow.StateName(s.State)[0]) + ShipFlow.StateName(s.State)[1..] : "";
    public string? ShipStatusText => Ship switch
    {
        null => null,
        { State: ShipState.Requested } => "An AI agent working in this project will post its plan here for you to approve. Nothing runs before that.",
        { State: ShipState.Planned } s => $"{s.PlannedBy} planned it. Nothing runs until you approve.",
        { State: ShipState.Approved, DecidedBy: null } => "Runs without approval (Details › Shipping): an AI agent working in this project can go ahead.",
        { State: ShipState.Approved } s => $"Approved by {s.DecidedBy}: {s.PlannedBy ?? "an AI agent"} can go ahead.",
        { State: ShipState.Running } => "Running: each step is reported below.",
        { State: ShipState.Done } => "Shipped.",
        { State: ShipState.Failed } => "It didn't ship; the log below says why.",
        { State: ShipState.Declined } => "The plan was declined.",
        _ => "The request was withdrawn.",
    };
    public string? ShipPlan => Ship?.Plan;
    public bool HasShipPlan => Ship?.Plan != null;
    public IReadOnlyList<ShipLogLine> ShipLog => (Ship?.Log ?? []).Select(e => new ShipLogLine(e)).ToList();
    public bool ShipWaitsForYou => Ship?.State == ShipState.Planned;
    public bool CanWithdrawShip => Ship?.IsOpen == true;
    public bool CanDismissShip => Ship is { IsOpen: false };

    [RelayCommand]
    private void StartShip()
    {
        if (!CanShip) return;
        SelectedTab = 0;
        if (Project.OpenShip != null)
        {
            _state.StatusText = "There's a ship request already; withdraw it to ask for something else.";
            return;
        }
        ShipChoices = ShipFlow.SettingsOf(Project).Steps.Order().Select(s => new ShipStepChoice(s, true)).ToList();
        OnPropertyChanged(nameof(ShipChoices));
        IsComposingShip = true;
    }

    [RelayCommand] private void CancelShipRequest() => IsComposingShip = false;

    [RelayCommand]
    private void RequestShip()
    {
        if (!CanShip) return;
        if (!TryShip(() => ShipFlow.Request(Project, ShipChoices.Where(c => c.IsChecked).Select(c => c.Step), _state.UserName))) return;
        IsComposingShip = false;
        _state.StatusText = Project.OpenShip?.State == ShipState.Approved
            ? "Asked to ship: an AI agent working in this project can go ahead."
            : "Asked to ship: an AI agent working in this project will post a plan for you to approve.";
    }

    [RelayCommand]
    private void ApproveShip()
    {
        if (Ship is { } s && TryShip(() => ShipFlow.Decide(s, approve: true, _state.UserName)))
            _state.StatusText = $"Approved: {s.PlannedBy ?? "the agent"} can ship it.";
    }

    [RelayCommand]
    private void DeclineShip()
    {
        if (Ship is { } s && TryShip(() => ShipFlow.Decide(s, approve: false, _state.UserName))) _state.StatusText = "Declined the plan.";
    }

    [RelayCommand]
    private void WithdrawShip()
    {
        if (Ship is { } s && TryShip(() => ShipFlow.Withdraw(s, _state.UserName))) _state.StatusText = "Ship request withdrawn.";
    }

    [RelayCommand]
    private void DismissShip()
    {
        if (Ship is not { IsOpen: false } s) return;
        s.Dismissed = true;
        Touch();
        RefreshShip();
    }

    /// <summary>The board shows this request's card again (from the Feed).</summary>
    public void RevealShip(ShipRequest request)
    {
        SelectedTab = 0;
        if (Project.Ships?.LastOrDefault() != request || !request.Dismissed) return;
        request.Dismissed = false;
        Touch();
        RefreshShip();
    }

    /// <summary>
    /// Changes a ship request (yours here, an agent's through its tools). Not part of undo: requests stand for commits
    /// and releases that really happened, and for what you approved. Throws InvalidOperationException with the reason.
    /// </summary>
    public void ChangeShip(Action change)
    {
        change();
        Touch();
        RefreshShip();
    }

    private bool TryShip(Action change)
    {
        try
        {
            ChangeShip(change);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _state.StatusText = ex.Message;
            return false;
        }
    }

    public void RefreshShip()
    {
        _shipSteps = null;
        foreach (var p in new[] { nameof(CanShip), nameof(Ship), nameof(HasShipCard), nameof(ShowShipRequest), nameof(ShipTitle), nameof(ShipStateText),
                     nameof(ShipStatusText), nameof(ShipPlan), nameof(HasShipPlan), nameof(ShipLog), nameof(ShipWaitsForYou), nameof(CanWithdrawShip),
                     nameof(CanDismissShip), nameof(ShipSteps), nameof(ShipHowTo), nameof(ShipLocalWithoutApproval), nameof(HasShipChange),
                     nameof(ShipChangeText) })
            OnPropertyChanged(p);
    }

    // Details › Shipping: not part of undo either, like the other details.

    private IReadOnlyList<ShipStepChoice>? _shipSteps;

    /// <summary>The steps this project has; a project without some (e.g. nothing to package) isn't offered them.</summary>
    public IReadOnlyList<ShipStepChoice> ShipSteps => _shipSteps ??= Enum.GetValues<ShipStep>()
        .Select(s => new ShipStepChoice(s, ShipFlow.SettingsOf(Project).Steps.Contains(s), c => ChangeShipSettings(settings =>
        {
            settings.Steps.Remove(c.Step);
            if (c.IsChecked) settings.Steps.Add(c.Step);
            settings.Steps.Sort();
        }))).ToList();

    public string? ShipHowTo
    {
        get => Project.Shipping?.HowTo;
        set
        {
            if (value == ShipHowTo) return;
            ChangeShipSettings(s => s.HowTo = Blank(value));
            OnPropertyChanged();
        }
    }

    public bool ShipLocalWithoutApproval
    {
        get => Project.Shipping?.LocalWithoutApproval == true;
        set
        {
            if (value == ShipLocalWithoutApproval) return;
            ChangeShipSettings(s => s.LocalWithoutApproval = value);
            OnPropertyChanged();
        }
    }

    /// <summary>An AI agent changed the steps or how-to and you haven't checked it yet.</summary>
    public bool HasShipChange => CanShip && Project.Shipping?.ChangedBy != null;

    public string ShipChangeText => Project.Shipping is { ChangedBy: { } by } s
        ? $"{by} changed the steps or how to ship{(s.ChangedAt is { } at ? at.ToString(" 'on' d MMM 'at' HH:mm", System.Globalization.CultureInfo.CurrentCulture) : "")}. " +
          "Check it: until you do, every ship request waits for your approval."
        : "";

    [RelayCommand]
    private void AcceptShipChange()
    {
        ChangeShipSettings(ShipFlow.AcceptChange);
        RefreshShip();
    }

    private void ChangeShipSettings(Action<ShipSettings> change)
    {
        if (!CanShip) return;
        change(Project.Shipping ??= new ShipSettings());
        Touch();
    }

    // ───────────── Discussions ─────────────

    /// <summary>Opens the discussion of an item or category of this project in the side panel.</summary>
    public void OpenDiscussion(Guid targetId) => Main.OpenDiscussion(DiscussionTarget.InProject(Project, targetId));

    /// <summary>The threads of an item or a category of this project; null when it has none or is gone.</summary>
    public List<Comment>? CommentsOf(Guid targetId) =>
        Project.Find(targetId)?.Item.Comments ?? Project.Categories.FirstOrDefault(c => c.Id == targetId)?.Comments;

    public bool HasUnread(Guid targetId, List<Comment>? comments) => _state.HasUnread(AppState.ReadKey(targetId), comments);

    public bool HasDiscussionTarget(Guid targetId) => Project.Find(targetId) != null || Project.Categories.Any(c => c.Id == targetId);

    /// <summary>Posts on an item or category (a new thread or a reply); one undo step.</summary>
    public void PostComment(Guid targetId, Comment comment, Guid? replyTo)
    {
        if (replyTo is { } parent && CommentsOf(targetId).Find(parent) == null)
            throw new InvalidOperationException("The comment to reply to isn't there any more.");
        if (Project.Find(targetId) is { } loc)
            MutateKeepingCursor(() => loc.Item.Comments = Discussions.Post(loc.Item.Comments, comment, replyTo), [CategoryFor(loc.Category)], kind: EditKind.Comment);
        else if (Project.Categories.FirstOrDefault(c => c.Id == targetId) is { } category)
            MutateKeepingCursor(() => category.Comments = Discussions.Post(category.Comments, comment, replyTo), [CategoryFor(category)], kind: EditKind.Comment);
        else
            throw new InvalidOperationException("The item or category isn't there any more.");
    }

    public void RemoveComment(Guid targetId, Guid commentId)
    {
        if (Project.Find(targetId) is { } loc)
            MutateKeepingCursor(() => loc.Item.Comments = Discussions.Remove(loc.Item.Comments, commentId), [CategoryFor(loc.Category)], kind: EditKind.Comment);
        else if (Project.Categories.FirstOrDefault(c => c.Id == targetId) is { } category)
            MutateKeepingCursor(() => category.Comments = Discussions.Remove(category.Comments, commentId), [CategoryFor(category)], kind: EditKind.Comment);
    }

    // ───────────── Worked on by ─────────────

    /// <summary>Shows only items this person or agent worked on (history or discussion); null shows everyone's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkedByActive))]
    private string? _workedBy;

    partial void OnWorkedByChanged(string? value) => ApplyFilter();

    public bool IsWorkedByActive => WorkedBy != null;

    /// <summary>Everyone who worked on something in this project, for the filter bar's picker.</summary>
    public IReadOnlyList<string> WorkedByChoices => Project.Walk().SelectMany(l => l.Item.Contributors()).Select(c => c.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    [RelayCommand]
    private void ShowWorkedBy(string? name)
    {
        OnPropertyChanged(nameof(WorkedByChoices));
        WorkedBy = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [RelayCommand] private void ClearWorkedBy() => WorkedBy = null;

    /// <summary>Creates the note with this title or replaces (or appends to) its text.</summary>
    public NoteDoc AgentWriteDoc(string title, string text, bool append, List<Attachment> files, string by, out bool created)
    {
        var note = Project.Notes.FirstOrDefault(n => string.Equals(n.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
        created = note == null;
        var selected = SelectedNote?.Model;
        Checkpoint();
        if (note == null)
        {
            note = new NoteDoc { Title = title.Trim() };
            Project.Notes.Add(note);
        }
        note.Text = append && note.Text.Trim().Length > 0 ? note.Text.TrimEnd() + "\n\n" + text : text;
        note.UpdatedAt = DateTime.Now;
        note.UpdatedBy = by;
        if (files.Count > 0) (note.Attachments ??= []).AddRange(files);
        Touch();
        ReloadSideLists();
        SelectedNote = Notes.FirstOrDefault(n => n.Model == (selected ?? note)) ?? Notes.FirstOrDefault();
        return note;
    }

    public Release AgentSaveRelease(string version, string? title, List<PatchNoteEntry> entries, string markdown, string by)
    {
        Checkpoint();
        var release = PatchNotesBuilder.CreateRelease(Project, version, DateTime.Now, title, entries, markdown);
        release.CreatedBy = by;
        Touch();
        ReloadSideLists();
        RebuildAll();
        return release;
    }

    // ───────────── Repo links ─────────────

    private bool RepoExists()
    {
        if (!string.IsNullOrWhiteSpace(Project.RepoPath) && Directory.Exists(Project.RepoPath)) return true;
        _state.StatusText = "Set an existing local repo folder in the Details tab first.";
        SelectedTab = 4;
        return false;
    }

    [RelayCommand] private void OpenFolder() { if (RepoExists()) ShellLauncher.OpenFolder(Project.RepoPath!); }
    [RelayCommand] private void OpenVsCode() { if (RepoExists()) Try(() => ShellLauncher.OpenInVsCode(Project.RepoPath!, _state.Settings.VsCodePath)); }
    [RelayCommand] private void OpenTerminal() { if (RepoExists()) Try(() => ShellLauncher.OpenTerminal(Project.RepoPath!)); }

    [RelayCommand]
    private void OpenGitHub()
    {
        if (string.IsNullOrWhiteSpace(Project.GitHubUrl))
        {
            _state.StatusText = "Set the GitHub URL in the Details tab first.";
            SelectedTab = 4;
            return;
        }
        Try(() => ShellLauncher.OpenUrl(Project.GitHubUrl));
    }

    [RelayCommand]
    private void BrowseRepo()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Local repository folder" };
        if (!string.IsNullOrWhiteSpace(RepoPath) && Directory.Exists(RepoPath)) dialog.InitialDirectory = RepoPath;
        if (dialog.ShowDialog() == true) RepoPath = dialog.FolderName;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { _state.StatusText = ex.Message; }
    }

    [RelayCommand]
    private void Export() => new ExportDialog(_state, Project) { Owner = Application.Current.MainWindow }.ShowDialog();

    // ───────────── Notes, releases, archive ─────────────

    [ObservableProperty] private NoteViewModel? _selectedNote;
    [ObservableProperty] private Release? _selectedRelease;
    [ObservableProperty] private ArchivedFile? _selectedArchive;

    private void ReloadSideLists()
    {
        Notes.Clear();
        foreach (var note in Project.Notes) Notes.Add(new NoteViewModel(this, note));
        SelectedNote = Notes.FirstOrDefault();
        Releases.Clear();
        foreach (var release in Project.Releases.OrderByDescending(r => r.Date)) Releases.Add(release);
        SelectedRelease = Releases.FirstOrDefault();
        Archive.Clear();
        foreach (var file in Project.ArchivedFiles) Archive.Add(file);
        SelectedArchive = Archive.FirstOrDefault();
    }

    [RelayCommand]
    private void AddNote()
    {
        var note = new NoteDoc { Title = "New note" };
        Project.Notes.Add(note);
        var vm = new NoteViewModel(this, note);
        Notes.Add(vm);
        SelectedNote = vm;
        Touch();
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (SelectedNote is not { } note) return;
        if (MessageBox.Show(Application.Current.MainWindow!, $"Delete note \"{note.Title}\"?", "Delete note",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Checkpoint();
        Project.Notes.Remove(note.Model);
        Notes.Remove(note);
        SelectedNote = Notes.FirstOrDefault();
        Touch();
    }

    /// <summary>
    /// Moves the selected note into the wiki: it becomes a page about this project (with its files and who wrote it),
    /// and the note goes (Ctrl+Z brings the note back; the page stays).
    /// </summary>
    [RelayCommand]
    private void MoveNoteToWiki()
    {
        if (SelectedNote is not { } note || Project.Remote != null) return;
        if (Main.Wiki.MoveNote(Project, note.Model) is not { } page) return;
        Checkpoint();
        Project.Notes.Remove(note.Model);
        Notes.Remove(note);
        SelectedNote = Notes.FirstOrDefault();
        Touch();
        _state.StatusText = $"“{note.Model.Title}” is a wiki page now: {ProJict.Core.Wiki.WikiPaths.Display(page.Path)}.";
        Main.ShowWikiPage(page);
    }

    [RelayCommand]
    private void NewRelease()
    {
        var window = new PatchNotesWindow(new PatchNotesViewModel(_state, this)) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        ReloadSideLists();
        RebuildAll();
        SelectedTab = 2;
    }

    [RelayCommand]
    private void CopyRelease()
    {
        if (SelectedRelease == null) return;
        try
        {
            Clipboard.SetText(SelectedRelease.Markdown);
            _state.StatusText = $"Copied patch notes for {SelectedRelease.Version}.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    [RelayCommand]
    private void DeleteRelease()
    {
        if (SelectedRelease is not { } release) return;
        if (MessageBox.Show(Application.Current.MainWindow!,
                $"Delete release \"{release.Version}\"? Its items become unreleased again and will show up in the next patch notes.",
                "Delete release", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Checkpoint();
        foreach (var loc in Project.Walk().Where(l => l.Item.ReleaseId == release.Id)) loc.Item.ReleaseId = null;
        Project.Releases.Remove(release);
        Touch();
        ReloadSideLists();
        RebuildAll();
    }

    // ───────────── Navigation helpers ─────────────

    public void FocusRelative(ItemRowViewModel row, int delta)
    {
        var rows = VisibleRows();
        var i = rows.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= rows.Count) return;
        FocusRequested?.Invoke(rows[j].Item.Id, -1);
    }

    /// <summary>Clears the filter, expands the item's category (and DONE section) and focuses it.</summary>
    /// <summary>A link to the item (Chat, the Feed, search, a notification, an agent's card): the board focuses on it.</summary>
    public void Reveal(TodoItem item) => FocusOn(item);

    public void RevealNote(NoteDoc note)
    {
        SelectedTab = 1;
        SelectedNote = Notes.FirstOrDefault(n => n.Model == note);
    }

    private static string Shorten(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > 60 ? line[..60] + "…" : line;
    }
}

/// <summary>A choice in the board's priority menu: items at least as pressing as <paramref name="AtLeast"/>, or only those with priority <paramref name="Only"/> (none, on hold).</summary>
public sealed record PriorityFilterOption(string Label, Priority? AtLeast, Priority? Only = null)
{
    public bool IsActive => AtLeast != null || Only != null;
    public bool Matches(TodoItem item) => Only is { } only ? item.Priority == only : AtLeast is not { } min || Priorities.AtLeast(item.Priority, min);
    public override string ToString() => Label;
}

/// <summary>A step to tick: in a new ship request, or among the steps a project has (Details).</summary>
public sealed partial class ShipStepChoice(ShipStep step, bool isChecked, Action<ShipStepChoice>? changed = null) : ObservableObject
{
    public ShipStep Step { get; } = step;
    public string Label => Step.ToString();
    public string ToolTip => ShipFlow.Publishes(Step) ? "Publishes: always needs your approval" : "Stays on this computer";

    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed?.Invoke(this);
}

/// <summary>One line of a ship request's log: "21:40  Claude Code  Finished the commit step: 951c7c5".</summary>
public sealed class ShipLogLine(ActivityEntry entry)
{
    public string Time => entry.At.ToString(entry.At.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    public string By => entry.By;
    public bool IsAgent => entry.Agent;
    public string Text => entry.Text;
}

/// <summary>A choice in the board's state filter; State is null for "All states".</summary>
/// <summary>What a change does, for what a guest's role allows: change existing things, add items, or comment.</summary>
public enum EditKind { Edit, Add, Comment }

public sealed record StateFilterOption(string Label, ItemState? State, bool ReviewOnly = false, bool AttentionOnly = false)
{
    public override string ToString() => Label;
}

public sealed partial class NoteViewModel(ProjectViewModel owner, NoteDoc model) : ObservableObject
{
    private IReadOnlyList<AttachmentViewModel>? _attachments;

    public NoteDoc Model { get; } = model;

    public IReadOnlyList<AttachmentViewModel> Attachments => _attachments ??=
        (Model.Attachments ?? []).Select(a => new AttachmentViewModel(owner, a, () => owner.RemoveNoteAttachment(Model, a), copy => owner.AttachToNote(Model, copy))).ToList();
    public bool HasAttachments => Model.Attachments is { Count: > 0 };

    public void Refresh()
    {
        _attachments = null;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand] private void Attach() => owner.BrowseNoteAttachments(this);

    /// <summary>"Last changed 14:02 by Claude Code"; empty for notes from before notes were signed.</summary>
    public string? EditedText => Model.UpdatedBy is { } by
        ? $"Last changed {Model.UpdatedAt.ToString(Model.UpdatedAt.Date == DateTime.Today ? "HH:mm" : "d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture)} by {by}"
        : null;

    public string Title
    {
        get => Model.Title;
        set
        {
            if (value == Model.Title || !owner.CanEdit) return;
            Model.Title = value;
            Signed();
            OnPropertyChanged();
        }
    }

    public string Text
    {
        get => Model.Text;
        set
        {
            if (value == Model.Text || !owner.CanEdit) return;
            Model.Text = value;
            Signed();
            OnPropertyChanged();
        }
    }

    private void Signed()
    {
        Model.UpdatedAt = DateTime.Now;
        Model.UpdatedBy = owner.UserName;
        owner.Touch();
        OnPropertyChanged(nameof(EditedText));
    }
}
