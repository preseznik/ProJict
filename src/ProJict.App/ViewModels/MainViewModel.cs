using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Views;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.ViewModels;

public sealed partial class ProjectListItemViewModel(Project project, MainViewModel main) : ObservableObject
{
    public Project Project { get; } = project;
    public string Name => Project.Name;
    public ProjectStatus Status => Project.Status;
    public string DisplayGroup => ProjectGroups.Of(Project);
    public int GroupOrder => ProjectGroups.Order(Project, main.State.Settings);
    public int OpenCount => Project.Categories.Sum(c => c.Items.Count(i => !i.IsDone));

    /// <summary>Badges for the AI agents working in the project right now, shown instead of the status dot.</summary>
    public IReadOnlyList<WorkerBadge> Workers => WorkerBadge.For(main.Agents.AgentsIn(Project.Id));
    public bool HasWorkers => main.Agents.AgentsIn(Project.Id).Count > 0;
    /// <summary>A red dot: an unread comment or an agent's work to review somewhere in the project.</summary>
    public bool NeedsAttention => main.State.NeedsAttention(Project);
    public string AttentionToolTip => main.State.AttentionSummary(Project);
    /// <summary>Who is there and what each did last (or the guests looking at it).</summary>
    public string WorkersToolTip => main.AgentActivity.ToolTipFor(Project) is { Length: > 0 } agents ? agents : WorkerBadge.ToolTipFor(main.Agents.AgentsIn(Project.Id));

    /// <summary>No Windows notifications from this project (right-click › Mute notifications).</summary>
    public bool IsMuted => main.State.AppSettings.Notifications.MutedProjects.Contains(Project.Id);
    public string MuteHeader => IsMuted ? "Unmute notifications" : "Mute notifications";

    /// <summary>An agent did something here: the badges' tooltip says so.</summary>
    public void RefreshWorkers() => OnPropertyChanged(nameof(WorkersToolTip));
    /// <summary>A ship request in progress shows as a glyph next to the count (and the red dot while its plan waits for you).</summary>
    public bool HasShip => Project.OpenShip != null;
    public string? ShipToolTip => Project.OpenShip is { } s ? $"Ship request ({ShipFlow.Describe(s.Steps)}): {ShipFlow.StateName(s.State)}" : null;
    /// <summary>A clean-up of its folder is open: a glyph next to the count (no other agent work there meanwhile).</summary>
    public bool HasCleanup => Project.OpenCleanup != null;
    public string? CleanupToolTip => Project.OpenCleanup is { } c ? $"Clean-up of the repo folder: {CleanupFlow.StateName(c.State)}" : null;

    /// <summary>Its icon, beside its name (null while it has none, or another ProJict's hasn't come yet).</summary>
    public System.Windows.Media.ImageSource? Icon => main.IconOf(Project);
    public bool HasIcon => Icon != null;
    /// <summary>One of yours (not another ProJict's): you can set its icon.</summary>
    public bool IsOwn => Project.Remote == null;
    public bool CanRemoveIcon => IsOwn && Project.Icon != null;
    /// <summary>Private: a lock next to the name, and right-click › Private ticked.</summary>
    public bool IsPrivate => Project.Private;
    /// <summary>Your own projects, not the Inbox (never shared) or one shared with you.</summary>
    public bool CanBePrivate => Project.Remote == null && !Project.IsInbox;
    public static string PrivateToolTip => "Only on this computer: people you share with, and their agents, don't see it or its channels";

    /// <summary>Projects offered under "Copy categories from" in the right-click menu.</summary>
    public List<TemplateSourceViewModel> TemplateSources => main.TemplateSourcesFor(Project);

    /// <summary>Groups offered under "Move to group" in the right-click menu.</summary>
    public List<GroupChoiceViewModel> GroupChoices => main.GroupChoicesFor(Project);

    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Called when the right-click menu opens, so its submenus are current.</summary>
    public void RefreshMenu()
    {
        OnPropertyChanged(nameof(TemplateSources));
        OnPropertyChanged(nameof(GroupChoices));
        OnPropertyChanged(nameof(ChatHeader));
    }

    /// <summary>"Open #grid2vr" when the project has a channel, else "Start a channel…".</summary>
    public string ChatHeader => main.ChannelsFor(Project.Id).FirstOrDefault() is { } channel ? $"Open #{channel.Name}" : "Start a chat channel…";

    /// <summary>Used as the accessible name of the sidebar entry.</summary>
    public override string ToString() => Name;
}

/// <summary>A project whose categories can be copied into another one.</summary>
public sealed class TemplateSourceViewModel(Project source, Action<Project> copy)
{
    public string Name => source.Name;
    public string Detail => source.Categories.Count == 1 ? "1 category" : $"{source.Categories.Count} categories";
    public void Copy() => copy(source);
}

/// <summary>An entry under "Move to group": an existing group, or "New group…" when the name is null.</summary>
public sealed class GroupChoiceViewModel(string? name, bool isCurrent, Action choose)
{
    public string Label => name ?? "New group…";
    public string Glyph => isCurrent ? "\uE73E" : name == null ? "\uE710" : "";
    public void Choose() => choose();
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<Guid, ProjectViewModel> _projectViewModels = [];
    private readonly PropertyGroupDescription _groupDescription =
        new(nameof(ProjectListItemViewModel.DisplayGroup), null, StringComparison.OrdinalIgnoreCase);
    private string _groupsSignature = "";
    private bool _syncingSelection;
    /// <summary>How often who's away is looked at again (you, and the agents here).</summary>
    private static readonly TimeSpan PresenceCheck = TimeSpan.FromSeconds(20);
    private readonly System.Windows.Threading.DispatcherTimer _presenceTimer;

    public MainViewModel(AppState state)
    {
        State = state;
        Updates = new Services.UpdateService(state);
        GitHub = new Services.GitHubService(state);
        Issues = new Services.IssueSyncService(state, this);
        Sharing = new Services.Sharing.SharingStore(state.PersistsSettings ? ProJict.Core.Sharing.SharingData.DefaultPath : null);
        Tools = new Services.ToolService(state.PersistsSettings ? ProJict.Core.Tools.ToolData.DefaultPath : null);
        Workflows = new ProJict.Core.Tools.WorkflowLibrary(System.IO.Path.Combine(state.Store.DataRoot, "workflows"));
        Tools.Changed += () =>
        {
            OnPropertyChanged(nameof(PendingToolKey));
            OnPropertyChanged(nameof(HasPendingToolKey));
            OnPropertyChanged(nameof(AlwaysForText));
        };
        Browsers = new Services.BrowserService(Tools, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Browsers.Changed += () =>
        {
            OnPropertyChanged(nameof(BrowserInUse));
            OnPropertyChanged(nameof(HasBrowserInUse));
            OnPropertyChanged(nameof(BrowserInUseText));
        };
        Host = new Services.Sharing.HostService(state, this, Sharing);
        Guest = new Services.Sharing.GuestService(state, this, Sharing);
        AgentTools = new Services.AgentTools(state, this, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Calls = new Services.CallService(state, this, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        AgentTools.ResourcesChanged += RefreshResources;
        RefreshResources();
        ApiStore = new Services.Api.ApiStore(state.PersistsSettings ? ProJict.Core.Api.ApiData.DefaultPath : null);
        Api = new Services.Api.ApiServer(AgentTools, ApiStore, () => State.Projects.Where(p => p.Remote == null).ToList(),
            Services.UpdateService.CurrentVersionText);
        Host.Changed += () =>
        {
            OnPropertyChanged(nameof(PendingJoin));
            OnPropertyChanged(nameof(HasPendingJoin));
            OnPropertyChanged(nameof(SelfCodeText));
        };
        Issues.Synced += p =>
        {
            if (_projectViewModels.TryGetValue(p.Id, out var vm)) vm.RefreshIssueSync();
        };
        Dashboard = new DashboardViewModel(state, this);
        Feed = new FeedViewModel(state, this);
        AgentActivity = new AgentActivityViewModel(state, this);
        Chat = new ChatViewModel(state, this);
        // Another channel opened from Chat's own list is a new place too.
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.Channel) && CurrentView == Chat) Arrived();
        };
        Guest.ChatChanged += connection => Chat.OnRemoteChanged(connection);
        Host.BackChatChanged += back => Chat.OnRemoteChanged(back);
        Guest.PresenceChanged += _ => Chat.RefreshPresence();
        // A shared project's test build on its way, here or not.
        Guest.TestBuildChanged += id =>
        {
            if (state.Projects.FirstOrDefault(p => p.Id == id) is { } project) GetProjectViewModel(project).RefreshTestBuild();
        };
        Agents.Me = state.UserName;
        Agents.PresenceChanged += () =>
        {
            Chat.RefreshPresence();
            Host.SendPresence();
            Guest.SendPresence();
        };
        _presenceTimer = new System.Windows.Threading.DispatcherTimer { Interval = PresenceCheck };
        _presenceTimer.Tick += (_, _) =>
        {
            Agents.Me = State.UserName;
            Agents.Tick(Services.UserIdle.Time);
            Browsers.CloseIdle(DateTime.Now);
        };
        _presenceTimer.Start();
        Wiki = new WikiViewModel(state, this);
        Diagrams = new Services.DiagramService(state.DiagramCacheRoot, state.WebViewDataRoot, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Diagrams.Drawn += () => Wiki.Page?.RedrawDiagrams();
        // One ProJict per process; a test making a second one draws with the newest.
        Views.WikiRenderer.Diagrams.RemoveAll(d => d is Views.MermaidDiagram);
        Views.WikiRenderer.Diagrams.Add(new Views.MermaidDiagram(Diagrams));
        // Another page opened from the wiki's own tree or links is a new place too.
        Wiki.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WikiViewModel.Page) && CurrentView == Wiki) Arrived();
        };
        Wiki.CountsChanged += () =>
        {
            OnPropertyChanged(nameof(WikiReviewCount));
            OnPropertyChanged(nameof(HasWikiReview));
            OnPropertyChanged(nameof(WikiToolTip));
        };
        Chat.CountsChanged += () =>
        {
            foreach (var name in new[] { nameof(ChatUnread), nameof(HasChatUnread), nameof(ChatHasMention), nameof(ChatToolTip) })
                OnPropertyChanged(name);
            RefreshChannelChips();
        };
        AgentActivity.CountChanged += () =>
        {
            OnPropertyChanged(nameof(LiveAgentCount));
            OnPropertyChanged(nameof(LiveAgentToolTip));
        };
        AgentTools.Recorded += (session, action) =>
        {
            if (CurrentView == AgentActivity) AgentActivity.OnRecorded(session);
            AgentActivity.CheckCount();
            if (action.ProjectId is { } id) ProjectItems.FirstOrDefault(i => i.Project.Id == id)?.RefreshWorkers();
        };
        Api.Changed += () =>
        {
            if (CurrentView is FeedViewModel) Feed.Refresh();
        };
        Agents.Changed += () =>
        {
            foreach (var item in ProjectItems) item.Refresh();
            if (CurrentView == Chat) Chat.RefreshInviteChoices();
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
            if (CurrentView == AgentActivity) AgentActivity.Refresh();
            CurrentProject?.RefreshWorkingOnHint();
            OnPropertyChanged(nameof(LiveAgentCount));
            OnPropertyChanged(nameof(LiveAgentToolTip));
        };
        GitHub.Updated += () =>
        {
            foreach (var vm in _projectViewModels.Values) vm.RefreshGitHubLink();
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
        };
        ProjectsView = CollectionViewSource.GetDefaultView(ProjectItems);
        ProjectsView.GroupDescriptions.Add(_groupDescription);
        ProjectsView.SortDescriptions.Add(new SortDescription(nameof(ProjectListItemViewModel.GroupOrder), ListSortDirection.Ascending));
        ProjectsView.SortDescriptions.Add(new SortDescription(nameof(ProjectListItemViewModel.Name), ListSortDirection.Ascending));
        SidebarFilter = new ProjectFilterViewModel(state, state.Settings.HiddenProjects);
        SidebarFilter.HiddenChanged += Dashboard.Filter.OnHiddenChangedElsewhere;
        Dashboard.Filter.HiddenChanged += SidebarFilter.OnHiddenChangedElsewhere;
        // The Agents view's project list leaves out the projects hidden there.
        SidebarFilter.HiddenChanged += AgentActivity.RefreshProjectChoices;
        Dashboard.Filter.HiddenChanged += AgentActivity.RefreshProjectChoices;
        ProjectsView.Filter = o => o is ProjectListItemViewModel i && SidebarFilter.Matches(i.Project) && Shows(i.Project);
        SidebarFilter.Changed += () =>
        {
            SyncGroupNames();
            ProjectsView.Refresh();
            SyncSelection();
            OnPropertyChanged(nameof(NothingToShow));
        };
        SidebarFilter.Accepted += () =>
        {
            if (ProjectsView.OfType<ProjectListItemViewModel>().FirstOrDefault() is { } first) OpenProject(first.Project);
        };
        WatchShowing();
        RebuildList();

        state.ProjectListChanged += RebuildList;
        state.ReadMarksChanged += () =>
        {
            OnPropertyChanged(nameof(UnreadGroups));
            foreach (var item in ProjectItems) item.Refresh();
            foreach (var vm in _projectViewModels.Values)
                foreach (var category in vm.Categories) category.RefreshUnread();
        };
        state.ProjectChanged += p =>
        {
            if (p.IsInbox) OnPropertyChanged(nameof(InboxCount));
            ProjectItems.FirstOrDefault(i => i.Project == p)?.Refresh();
            // While the dashboard or the feed shows, changes come from elsewhere (an AI agent, quick capture); keep it current.
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
            if (CurrentView is FeedViewModel) Feed.Refresh();
            if (Discussion is { } open && open.Target.ProjectId == p.Id) open.Sync();
        };
        state.GroupCommentsChanged += group =>
        {
            OnPropertyChanged(nameof(GroupCommentCounts));
            OnPropertyChanged(nameof(UnreadGroups));
            if (CurrentView is DashboardViewModel) Dashboard.Refresh();
            if (CurrentView is FeedViewModel) Feed.Refresh();
            if (Discussion is { Target.Kind: DiscussionKind.Group } open && string.Equals(open.Target.Group, group, StringComparison.OrdinalIgnoreCase))
                open.Sync();
        };

        var last = state.AppSettings.LastProjectId is { } id ? state.Projects.FirstOrDefault(p => p.Id == id) : null;
        if (last != null) OpenProject(last);
        else ShowDashboard();
    }

    public AppState State { get; }
    public Services.UpdateService Updates { get; }
    public Services.GitHubService GitHub { get; }
    public Services.IssueSyncService Issues { get; }
    public Services.Sharing.SharingStore Sharing { get; }
    /// <summary>Sharing your projects with other ProJict apps.</summary>
    public Services.Sharing.HostService Host { get; }
    /// <summary>Other ProJict apps' projects shown here.</summary>
    public Services.Sharing.GuestService Guest { get; }
    /// <summary>What AI agents can do in ProJict, over MCP and the API.</summary>
    public Services.AgentTools AgentTools { get; }
    /// <summary>Voice calls with agents (a phone in Chat's channel header).</summary>
    public Services.CallService Calls { get; }
    public Services.Api.ApiStore ApiStore { get; }
    /// <summary>ProJict's API (Settings › API).</summary>
    public Services.Api.ApiServer Api { get; }
    public DashboardViewModel Dashboard { get; }
    public FeedViewModel Feed { get; }
    /// <summary>The Agents view: what connected agents are doing, and what they did.</summary>
    public AgentActivityViewModel AgentActivity { get; }
    /// <summary>Connected agents, for the count next to Agents in the sidebar.</summary>
    public ChatViewModel Chat { get; }
    /// <summary>The workspace's wiki.</summary>
    public WikiViewModel Wiki { get; }
    /// <summary>Draws the wiki's Mermaid diagrams (in a WebView2 nobody sees).</summary>
    public Services.DiagramService Diagrams { get; }
    /// <summary>The sidebar's Wiki count: pages agents changed (or with clashing edits) that you haven't checked.</summary>
    public string WikiReviewCount => Wiki.ReviewCount is > 0 and var n ? n.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
    public bool HasWikiReview => Wiki.ReviewCount > 0;
    public string WikiToolTip => Wiki.ReviewToolTip;
    /// <summary>The sidebar's Chat count: unread messages in your channels.</summary>
    public string ChatUnread => Chat.UnreadCount is > 0 and var n ? n.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
    public bool HasChatUnread => Chat.UnreadCount > 0;
    public bool ChatHasMention => Chat.HasMention;
    public string ChatToolTip => Chat.UnreadToolTip;

    /// <summary>The agents the Agents view shows (by default those active in the past hour).</summary>
    public int LiveAgentCount => AgentActivity.ShownCount;
    public string LiveAgentToolTip => AgentActivity.CountToolTip;
    /// <summary>Which AI agents are connected and where they work.</summary>
    public Services.AgentPresence Agents { get; } = new();
    public ObservableCollection<ProjectListItemViewModel> ProjectItems { get; } = [];
    public ICollectionView ProjectsView { get; }
    public ProjectFilterViewModel SidebarFilter { get; }
    /// <summary>A search found nothing (otherwise the group headings still show).</summary>
    public bool NothingToShow => SidebarFilter.IsSearching && ProjectItems.Count > 0 && ProjectsView.IsEmpty;

    /// <summary>Replaced (not mutated) on every toggle, so the group headers' bindings update.</summary>
    public IReadOnlyList<string> CollapsedGroups => State.Settings.CollapsedGroups;
    public int InboxCount => State.Inbox.Categories.Sum(c => c.Items.Count(i => !i.IsDone));

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private ProjectListItemViewModel? _selectedProjectItem;
    [ObservableProperty] private bool _isDashboardActive;
    [ObservableProperty] private bool _isFeedActive;
    [ObservableProperty] private bool _isAgentsActive;
    [ObservableProperty] private bool _isChatActive;
    [ObservableProperty] private bool _isWikiActive;

    partial void OnCurrentViewChanged(object? oldValue, object? newValue)
    {
        if (oldValue == Wiki && newValue != Wiki) Wiki.Hide();
        if (newValue != Wiki) IsWikiActive = false;
        if (oldValue == AgentActivity && newValue != AgentActivity) AgentActivity.Hide();
        if (oldValue == Chat && newValue != Chat) Chat.Hide();
        if (newValue != Chat) IsChatActive = false;
    }
    [ObservableProperty] private bool _isInboxActive;

    public ProjectViewModel? CurrentProject => CurrentView as ProjectViewModel;

    partial void OnSelectedProjectItemChanged(ProjectListItemViewModel? value)
    {
        if (!_syncingSelection && value != null) OpenProject(value.Project);
    }

    partial void OnCurrentViewChanged(object? value)
    {
        OnPropertyChanged(nameof(CurrentProject));
        SendViewing();
        // Chat is a place once its channel is set (ShowChannel says so).
        if (value != Chat) Arrived();
    }

    // ───────────── Sharing ─────────────

    private Views.SharingWindow? _sharingWindow;
    private Views.ApiWindow? _apiWindow;

    /// <summary>The API window, e.g. from the Feed; Settings opens it as a dialog of its own.</summary>
    [RelayCommand]
    private void OpenApi()
    {
        if (_apiWindow is { IsLoaded: true })
        {
            _apiWindow.Activate();
            return;
        }
        _apiWindow = new Views.ApiWindow(this) { Owner = Application.Current.MainWindow };
        _apiWindow.Closed += (_, _) => _apiWindow = null;
        _apiWindow.Show();
    }

    /// <summary>The oldest request to join your ProJict, shown in a bar until you answer it.</summary>
    // ───────────── Outside tools ─────────────

    /// <summary>The outside tools agents may use (Settings › Tools), and their requests for API keys.</summary>
    public Services.ToolService Tools { get; }
    /// <summary>ProJict's browser for agents, a browser per tool in the browser (Suno, Tripo3D).</summary>
    public Services.BrowserService Browsers { get; }
    /// <summary>The ComfyUI workflows of the tools (in the data folder, so they're on every computer).</summary>
    public ProJict.Core.Tools.WorkflowLibrary Workflows { get; }

    /// <summary>An agent is using a tool's browser: the bar at the bottom says who and which, with Watch and Stop.</summary>
    public Services.BrowserSession? BrowserInUse => Browsers.Sessions.OrderByDescending(s => s.LastUsed).FirstOrDefault();
    public bool HasBrowserInUse => BrowserInUse != null;
    public string BrowserInUseText => BrowserInUse is { } s
        ? $"{s.Agent} is using {s.Tool.Name} in ProJict's browser" + (Browsers.Sessions.Count > 1 ? $" (and {Browsers.Sessions.Count - 1} more)." : ".")
        : "";

    /// <summary>Shared things agents hold now (claim_resource, #bb95b66d): the bar at the bottom says who has what and until when, with Free.</summary>
    public IReadOnlyList<ResourceBarItem> ResourcesHeld { get; private set; } = [];
    public bool HasResourcesHeld => ResourcesHeld.Count > 0;
    private System.Windows.Threading.DispatcherTimer? _resourceClock;

    private void RefreshResources()
    {
        var now = DateTime.Now;
        ResourcesHeld = [.. (State.Settings.SharedResources ?? []).Where(r => ResourceLocks.IsHeld(r, now)).Select(r => new ResourceBarItem(r.Name,
            $"“{r.Name}”: {r.HeldBy}" + (r.For is { } why ? $" ({why})" : "") + $" until {ResourceLocks.UntilText(r, now)}" +
            (r.Waiting.Count > 0 ? $" · {r.Waiting.Count} waiting" : ""),
            ResourceLocks.IsOverdue(r, now), () => AgentTools.FreeResource(r.Name)))];
        OnPropertyChanged(nameof(ResourcesHeld));
        OnPropertyChanged(nameof(HasResourcesHeld));
        // Time goes by: the minutes left (and a holder leaving for good) show without anything else happening.
        if (HasResourcesHeld)
        {
            if (_resourceClock == null)
            {
                _resourceClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _resourceClock.Tick += (_, _) => RefreshResources();
            }
            _resourceClock.Start();
        }
        else _resourceClock?.Stop();
    }

    /// <summary>Shows the browser it's using, where you can click too (take over); closing it hides it again.</summary>
    [RelayCommand]
    private void WatchBrowser()
    {
        if (BrowserInUse is not { } session) return;
        if (session.Watching is { } open)
        {
            open.Activate();
            return;
        }
        new Views.BrowserWatchWindow(session) { Owner = System.Windows.Application.Current.MainWindow }.Show();
    }

    /// <summary>Stops the agent's use of the browser: it's closed, and the agent is told.</summary>
    [RelayCommand]
    private void StopBrowser()
    {
        if (BrowserInUse is not { } session) return;
        Browsers.Stop(session);
        State.StatusText = $"Stopped {session.Agent}'s use of {session.Tool.Name}'s browser; it's told on its next try.";
    }

    /// <summary>An agent asks for a tool's API key: the bar at the bottom asks you.</summary>
    public Services.ToolKeyRequest? PendingToolKey => Tools.Waiting.FirstOrDefault();
    public bool HasPendingToolKey => PendingToolKey != null;
    public string AlwaysForText => PendingToolKey is { } r ? $"Always for {r.Agent}" : "";

    [RelayCommand] private void AllowToolKeyOnce() => AnswerToolKey(Services.ToolKeyAnswer.Once);
    [RelayCommand] private void AllowToolKeyAlways() => AnswerToolKey(Services.ToolKeyAnswer.Always);
    [RelayCommand] private void DenyToolKey() => AnswerToolKey(Services.ToolKeyAnswer.No);

    private void AnswerToolKey(Services.ToolKeyAnswer answer)
    {
        if (PendingToolKey is not { } request) return;
        Tools.Answer(request, answer);
        State.StatusText = answer switch
        {
            Services.ToolKeyAnswer.Once => $"{request.Agent} may have the {request.Tool.Name} key this once.",
            Services.ToolKeyAnswer.Always => $"{request.Agent} may have the {request.Tool.Name} key whenever it needs it (Settings › Tools to change that).",
            _ => $"{request.Agent} doesn't get the {request.Tool.Name} key.",
        };
        OnPropertyChanged(nameof(AlwaysForText));
    }

    public Services.Sharing.JoinRequest? PendingJoin => Host.Requests.FirstOrDefault();
    public bool HasPendingJoin => Host.Requests.Count > 0;

    [RelayCommand] private void ApproveJoin() { if (PendingJoin is { } r) Host.Answer(r, Services.Sharing.JoinAnswer.Approve); }
    /// <summary>It's you, on another computer: let in, then confirmed with a code typed there.</summary>
    [RelayCommand]
    private void ApproveJoinAsMe()
    {
        if (PendingJoin is not { } r) return;
        r.Self = true;
        Host.Answer(r, Services.Sharing.JoinAnswer.Approve);
    }

    /// <summary>The codes to type on your other computers, while they wait (a bar under the join bar).</summary>
    public string? SelfCodeText => Host.SelfCodeLines is { Count: > 0 } lines ? string.Join("\n", lines) : null;
    [RelayCommand] private void DenyJoin() { if (PendingJoin is { } r) Host.Answer(r, Services.Sharing.JoinAnswer.Deny); }
    [RelayCommand] private void BlockJoin() { if (PendingJoin is { } r) Host.Answer(r, Services.Sharing.JoinAnswer.Block); }

    [RelayCommand]
    private void OpenSharing()
    {
        if (_sharingWindow is { IsLoaded: true })
        {
            _sharingWindow.Activate();
            return;
        }
        _sharingWindow = new Views.SharingWindow(this) { Owner = Application.Current.MainWindow };
        _sharingWindow.Closed += (_, _) => _sharingWindow = null;
        _sharingWindow.Show();
    }

    /// <summary>Tells the hosts which of their projects you have open.</summary>
    public void SendViewing() => Guest?.Viewing((CurrentView as ProjectViewModel)?.Project);

    /// <summary>A project shown from another ProJict went away (unshared or disconnected).</summary>
    public void ForgetProject(Project project)
    {
        _projectViewModels.Remove(project.Id);
        if (Discussion is { } open && open.Target.ProjectId == project.Id) CloseDiscussion();
        if (CurrentProject?.Project == project) ShowDashboard();
    }

    /// <summary>Renaming, moving, archiving or deleting another ProJict's project is up to its owner.</summary>
    private bool NotYours(Project project)
    {
        if (project.Remote is not { } remote) return false;
        State.StatusText = $"{project.Name} belongs to {remote.HostName}'s ProJict; only they can rename, move, archive or delete it.";
        return true;
    }

    private void RebuildList()
    {
        var selected = SelectedProjectItem?.Project;
        _syncingSelection = true;
        ProjectItems.Clear();
        var projects = State.Projects.Where(p => !p.IsInbox).ToList();
        SidebarFilter.Rebuild(projects);
        foreach (var project in projects)
            ProjectItems.Add(new ProjectListItemViewModel(project, this));
        SyncGroups();
        SelectedProjectItem = ProjectItems.FirstOrDefault(i => i.Project == selected);
        _syncingSelection = false;
        OnPropertyChanged(nameof(InboxCount));
        OnPropertyChanged(nameof(NothingToShow));
        if (CurrentView is DashboardViewModel) Dashboard.Refresh();
    }

    /// <summary>After any list change: refreshes group pickers when the groups changed and lists empty groups too.</summary>
    private void SyncGroups()
    {
        var signature = string.Join("\n", State.Settings.Groups);
        if (signature != _groupsSignature)
        {
            _groupsSignature = signature;
            foreach (var vm in _projectViewModels.Values) vm.RefreshGroups();
        }
        if (SyncGroupNames()) ProjectsView.Refresh();
    }

    /// <summary>Every group from the settings gets a heading, even an empty one, except while searching.</summary>
    private bool SyncGroupNames()
    {
        List<string> names = SidebarFilter.IsSearching || !ShowsOwn ? [] : State.Settings.Groups;
        if (_groupDescription.GroupNames.OfType<string>().SequenceEqual(names)) return false;
        _groupDescription.GroupNames.Clear();
        foreach (var name in names) _groupDescription.GroupNames.Add(name);
        return true;
    }

    /// <summary>Re-selects the open project in the sidebar after the filter hid and showed it again.</summary>
    private void SyncSelection()
    {
        _syncingSelection = true;
        SelectedProjectItem = CurrentProject is { IsInbox: false } current ? ProjectItems.FirstOrDefault(i => i.Project == current.Project) : null;
        _syncingSelection = false;
    }

    [RelayCommand]
    private void ToggleGroup(string? group)
    {
        if (string.IsNullOrEmpty(group)) return;
        var collapsed = State.Settings.CollapsedGroups;
        State.Settings.CollapsedGroups = collapsed.Contains(group, StringComparer.OrdinalIgnoreCase)
            ? collapsed.Where(g => !string.Equals(g, group, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. collapsed, group];
        State.MarkSettingsDirty();
        OnPropertyChanged(nameof(CollapsedGroups));
    }

    // ───────────── Discussions ─────────────

    /// <summary>The discussion shown in the side panel; null while it is closed.</summary>
    [ObservableProperty] private DiscussionViewModel? _discussion;

    public void OpenDiscussion(DiscussionTarget target) => Discussion = new DiscussionViewModel(this, State, target);

    public void CloseDiscussion() => Discussion = null;

    [RelayCommand]
    private void OpenGroupDiscussion(string? group)
    {
        if (!string.IsNullOrWhiteSpace(group)) OpenDiscussion(DiscussionTarget.ForGroup(group));
    }

    /// <summary>Comments per group name, for the sidebar headings.</summary>
    /// <summary>Groups whose discussion has comments you haven't read (1), for the red dot on their heading.</summary>
    public IReadOnlyDictionary<string, int> UnreadGroups =>
        (State.Settings.GroupComments ?? []).Where(kv => State.HasUnread(AppState.ReadKey(kv.Key), kv.Value))
            .ToDictionary(kv => kv.Key, _ => 1, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> GroupCommentCounts =>
        (State.Settings.GroupComments ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.CountComments(), StringComparer.OrdinalIgnoreCase);

    public ProjectViewModel GetProjectViewModel(Project project)
    {
        if (!_projectViewModels.TryGetValue(project.Id, out var vm))
        {
            vm = new ProjectViewModel(State, this, project);
            _projectViewModels[project.Id] = vm;
        }
        return vm;
    }

    public ProjectViewModel OpenProject(Project project)
    {
        var vm = GetProjectViewModel(project);
        CurrentView = vm;
        IsDashboardActive = false;
        IsFeedActive = false;
        IsAgentsActive = false;
        IsInboxActive = project.IsInbox;
        _syncingSelection = true;
        SelectedProjectItem = ProjectItems.FirstOrDefault(i => i.Project == project);
        _syncingSelection = false;
        State.AppSettings.LastProjectId = project.Id;
        return vm;
    }

    [RelayCommand]
    public void ShowDashboard()
    {
        Dashboard.Refresh();
        CurrentView = Dashboard;
        IsDashboardActive = true;
        IsFeedActive = false;
        IsAgentsActive = false;
        IsInboxActive = false;
        _syncingSelection = true;
        SelectedProjectItem = null;
        _syncingSelection = false;
        State.AppSettings.LastProjectId = null;
    }

    [RelayCommand]
    public void ShowFeed()
    {
        Feed.Refresh();
        CurrentView = Feed;
        IsFeedActive = true;
        IsDashboardActive = false;
        IsAgentsActive = false;
        IsInboxActive = false;
        _syncingSelection = true;
        SelectedProjectItem = null;
        _syncingSelection = false;
        State.AppSettings.LastProjectId = null;
    }

    [RelayCommand] private void ShowInbox() => OpenProject(State.Inbox);

    /// <summary>Right-click a project › Mute notifications (or unmute).</summary>
    [RelayCommand]
    private void ToggleMute(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        var muted = State.AppSettings.Notifications.MutedProjects;
        if (!muted.Remove(item.Project.Id)) muted.Add(item.Project.Id);
        State.SaveAppSettings();
        item.Refresh();
        State.StatusText = item.IsMuted ? $"No more Windows notifications from {item.Name}." : $"Windows notifications from {item.Name} again.";
    }

    /// <summary>The Agents view, for one project (right-click a project › Agents) or all of them (the sidebar).</summary>
    [RelayCommand]
    public void ShowAgents(object? target)
    {
        var project = target switch
        {
            ProjectListItemViewModel item => item.Project,
            Project p => p,
            _ => null,
        };
        AgentActivity.Show(project);
        CurrentView = AgentActivity;
        IsAgentsActive = true;
        IsDashboardActive = false;
        IsFeedActive = false;
        IsInboxActive = false;
        _syncingSelection = true;
        SelectedProjectItem = null;
        _syncingSelection = false;
        // From one project's Agents to another's: the view stays, the place changes.
        Arrived();
    }

    // ───────────── Chat ─────────────

    /// <summary>The Chat view, at a channel or where you were.</summary>
    [RelayCommand]
    public void ShowChat(object? target) => ShowChannel(target as ProJict.Core.Chat.ChatChannel);

    public void ShowChannel(ProJict.Core.Chat.ChatChannel? channel, Guid? message = null)
    {
        CurrentView = Chat;
        IsChatActive = true;
        IsAgentsActive = false;
        IsDashboardActive = false;
        IsFeedActive = false;
        IsInboxActive = false;
        _syncingSelection = true;
        SelectedProjectItem = null;
        _syncingSelection = false;
        Chat.Show(channel, message);
        // Another channel in Chat: the view stays, the place changes.
        Arrived();
    }

    // ───────────── Wiki ─────────────

    /// <summary>The Wiki view, at a page or where you were.</summary>
    [RelayCommand]
    public void ShowWiki(object? target) => ShowWikiPage(target as ProJict.Core.Wiki.WikiPage);

    public void ShowWikiPage(ProJict.Core.Wiki.WikiPage? page)
    {
        CurrentView = Wiki;
        IsWikiActive = true;
        IsAgentsActive = false;
        IsDashboardActive = false;
        IsFeedActive = false;
        IsInboxActive = false;
        _syncingSelection = true;
        SelectedProjectItem = null;
        _syncingSelection = false;
        Wiki.Show(page);
        Arrived();
    }

    /// <summary>A project's header › Wiki: its first page, or a new one about it.</summary>
    public void ShowProjectWiki(Project project)
    {
        if (State.Wiki.LinkedTo(project.Id).FirstOrDefault() is { } page) ShowWikiPage(page);
        else
        {
            ShowWikiPage(null);
            Wiki.NewPageFor(project);
        }
    }

    /// <summary>The pages about a project, as chips for its header.</summary>
    public List<WikiPageChip> WikiChips(Project project)
    {
        IEnumerable<ProJict.Core.Wiki.WikiPage> pages = project.Remote is { } remote
            ? Guest.Connections.FirstOrDefault(c => c.Address == remote.HostKey)?.Wiki?.LinkedTo(project.Id) ?? []
            : State.Wiki.LinkedTo(project.Id);
        return pages.Select(p => new WikiPageChip(p, this)).ToList();
    }

    /// <summary>Pages were started, linked, renamed or changed: the chips on the projects' headers follow.</summary>
    public void RefreshWikiChips()
    {
        foreach (var vm in _projectViewModels.Values) vm.RefreshWikiPages();
    }

    /// <summary>Opens Chat with the form to start a channel about a project, category or item, its name suggested from it.</summary>
    public void StartChannel(Project project, Category? category = null, TodoItem? item = null)
    {
        var link = new ProJict.Core.Chat.ChannelLink { ProjectId = project.Id, CategoryId = category?.Id, ItemId = item?.Id };
        if (item != null && link.CategoryId == null) link.CategoryId = project.Find(item.Id)?.Category.Id;
        // About another ProJict's project: started there, if you're an editor and connected.
        var room = State.Chat;
        if (project.Remote != null)
        {
            if (Chat.HostOfProject(project.Id) is not { Chat: { } remote, Role: ProJict.Core.Sharing.GuestRole.Editor })
            {
                State.StatusText = $"Only editors connected to {project.Remote.HostName}'s ProJict start channels about its projects.";
                return;
            }
            room = remote;
        }
        var name = room.SuggestName(project.Name, item == null ? category?.Name : null, item?.DisplayText());
        ShowChannel(null);
        Chat.BeginNewChannel(link, name);
    }

    /// <summary>Channels linked to exactly this project, category or item.</summary>
    /// <summary>Your channels and those of the ProJicts you're connected to (their projects' chips show theirs).</summary>
    private IEnumerable<ProJict.Core.Chat.ChatChannel> AllChannels => State.Chat.Channels.Concat(Guest.Connections.SelectMany(c => c.Chat?.Channels ?? []));

    public List<ProJict.Core.Chat.ChatChannel> ChannelsFor(Guid projectId, Guid? categoryId = null, Guid? itemId = null) =>
        AllChannels.Where(c => !c.Direct && c.Link is { } l && l.ProjectId == projectId &&
                                       (itemId != null ? l.ItemId == itemId : l.ItemId == null && l.CategoryId == categoryId))
            .OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    public List<ChannelChip> ChannelChips(Guid projectId, Guid? categoryId = null, Guid? itemId = null) =>
        ChannelsFor(projectId, categoryId, itemId).Select(c => new ChannelChip(c, this, ProJict.Core.Chat.ChatRoom.Unread(c, Chat.NameIn(c)).Any())).ToList();

    private string _chipSignature = "";

    /// <summary>The #channel chips on the boards follow channels being started, renamed, deleted or read.</summary>
    public void RefreshChannelChips()
    {
        var signature = string.Join("|", AllChannels.Where(c => c.Link != null)
            .Select(c => $"{c.Id}:{c.Name}:{c.Link!.ProjectId}:{c.Link.CategoryId}:{c.Link.ItemId}:{ProJict.Core.Chat.ChatRoom.Unread(c, Chat.NameIn(c)).Any()}"));
        if (signature == _chipSignature) return;
        _chipSignature = signature;
        foreach (var vm in _projectViewModels.Values) vm.RefreshChannels();
    }

    /// <summary>Right-click a project › its chat channel: opens it, or starts one.</summary>
    [RelayCommand]
    private void ProjectChat(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        if (ChannelsFor(item.Project.Id).FirstOrDefault() is { } channel) ShowChannel(channel);
        else StartChannel(item.Project);
    }

    [RelayCommand]
    private void NewProject()
    {
        var name = InputDialog.Ask(Application.Current.MainWindow!, "New project", "Project name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = CurrentProject is { IsInbox: false } current ? current.Project.Group : "Personal";
        var project = Project.Create(name, group);
        project.Categories.Add(new Category { Name = "GENERAL" });
        State.AddProject(project);
        OpenProject(project);
    }

    [RelayCommand]
    private void Import()
    {
        var window = new ImportWizardWindow(new ImportWizardViewModel(State)) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (CurrentView is DashboardViewModel) Dashboard.Refresh();
    }

    [RelayCommand]
    private void Export() =>
        new ExportDialog(State, CurrentProject?.Project) { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void OpenSettings() => new SettingsWindow(State) { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void Search() => new SearchWindow(State, this) { Owner = Application.Current.MainWindow }.Show();

    [RelayCommand] private void QuickCapture() => App.Current.ShowQuickCapture();

    [RelayCommand] private void SaveNow() => State.SaveNow();

    [RelayCommand]
    private void DeleteProject(ProjectListItemViewModel? item)
    {
        if (item == null || NotYours(item.Project)) return;
        if (MessageBox.Show(Application.Current.MainWindow!,
                $"Delete project \"{item.Name}\"?\n\nIts file is moved to the backups folder, not erased.",
                "Delete project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _projectViewModels.Remove(item.Project.Id);
        var wasCurrent = CurrentProject?.Project == item.Project;
        State.DeleteProject(item.Project);
        if (wasCurrent) ShowDashboard();
    }

    [RelayCommand]
    private void ArchiveProject(ProjectListItemViewModel? item)
    {
        if (item == null || NotYours(item.Project)) return;
        var vm = GetProjectViewModel(item.Project);
        vm.Status = item.Project.Status == ProjectStatus.Archived ? ProjectStatus.Active : ProjectStatus.Archived;
    }

    [RelayCommand]
    private void RenameProject(ProjectListItemViewModel? item)
    {
        if (item == null || NotYours(item.Project)) return;
        var name = InputDialog.Ask(Application.Current.MainWindow!, "Rename project", "Project name:", item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        GetProjectViewModel(item.Project).Name = name.Trim();
    }

    [RelayCommand] private void NewGroup() => AskNewGroup();

    /// <summary>Asks for a group name until it is new (or the user cancels), adds the group and returns its name.</summary>
    private string? AskNewGroup()
    {
        var name = "";
        while (true)
        {
            name = InputDialog.Ask(Application.Current.MainWindow!, "New group", "Group name:", name);
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (GroupOperations.Validate(State.Settings, name) is not { } error) break;
            MessageBox.Show(Application.Current.MainWindow!, error, "New group", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        name = name.Trim();
        GroupOperations.Add(State.Settings, name);
        State.MarkSettingsDirty();
        State.NotifyListChanged();
        State.StatusText = $"Added the group {name}. Right-click a project › Move to group to fill it.";
        return name;
    }

    /// <summary>Your own groups other than Personal and Work (another ProJict's groups are its owner's).</summary>
    private bool CanEditGroup(string? group) =>
        !string.IsNullOrWhiteSpace(group) && !WorkspaceSettings.IsDefaultGroup(group) && !ProjectGroups.Same(group, ProjectGroups.Archived) &&
        State.Settings.Groups.Any(g => ProjectGroups.Same(g, group));

    [RelayCommand(CanExecute = nameof(CanEditGroup))]
    private void RenameGroup(string? group)
    {
        if (!CanEditGroup(group)) return;
        var name = group!;
        while (true)
        {
            name = InputDialog.Ask(Application.Current.MainWindow!, "Rename group", "Group name:", name) ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == group) return;
            if (GroupOperations.Validate(State.Settings, name, renaming: group) is not { } error) break;
            MessageBox.Show(Application.Current.MainWindow!, error, "Rename group", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        ApplyGroupChange(GroupOperations.Rename(State.Workspace, group!, name));
        if (Discussion is { Target.Kind: DiscussionKind.Group } open && ProjectGroups.Same(open.Target.Group, group))
            OpenDiscussion(DiscussionTarget.ForGroup(name));
        State.StatusText = $"Renamed the group {group} to {name.Trim()}.";
    }

    [RelayCommand(CanExecute = nameof(CanEditGroup))]
    private void DeleteGroup(string? group)
    {
        if (!CanEditGroup(group)) return;
        var count = State.Projects.Count(p => !p.IsInbox && ProjectGroups.Same(p.Group, group));
        var comments = State.Settings.CommentsFor(group!).CountComments();
        var moves = count == 1 ? "Its project moves to Personal." : $"Its {count} projects move to Personal.";
        var lost = comments == 1 ? "Its discussion (1 comment) is deleted." : $"Its discussion ({comments} comments) is deleted.";
        var details = string.Join(" ", new[] { count > 0 ? moves : null, comments > 0 ? lost : null }.OfType<string>());
        if (details.Length > 0 && MessageBox.Show(Application.Current.MainWindow!, $"Delete the group \"{group}\"?\n\n{details}",
                "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyGroupChange(GroupOperations.Delete(State.Workspace, group!));
        State.StatusText = count == 0 ? $"Deleted the group {group}." : $"Deleted the group {group}; its projects moved to Personal.";
    }

    private void ApplyGroupChange(List<Project> changedProjects)
    {
        foreach (var project in changedProjects) State.MarkDirty(project, touch: false);
        State.MarkSettingsDirty();
        OnPropertyChanged(nameof(CollapsedGroups));
        OnPropertyChanged(nameof(GroupCommentCounts));
        Discussion?.Sync();
        State.NotifyListChanged();
    }

    public List<GroupChoiceViewModel> GroupChoicesFor(Project project)
    {
        var current = string.IsNullOrWhiteSpace(project.Group) ? WorkspaceSettings.PersonalGroup : project.Group;
        return
        [
            .. State.Settings.Groups.Select(g => new GroupChoiceViewModel(g, ProjectGroups.Same(g, current), () => MoveToGroup(project, g))),
            new GroupChoiceViewModel(null, false, () =>
            {
                if (AskNewGroup() is { } group) MoveToGroup(project, group);
            }),
        ];
    }

    public void MoveToGroup(Project project, string group)
    {
        if (NotYours(project)) return;
        var current = string.IsNullOrWhiteSpace(project.Group) ? WorkspaceSettings.PersonalGroup : project.Group;
        if (ProjectGroups.Same(current, group)) return;
        project.Group = group;
        State.MarkDirty(project, touch: false);
        if (_projectViewModels.TryGetValue(project.Id, out var vm)) vm.RefreshGroups();
        State.NotifyListChanged();
        State.StatusText = $"Moved {project.Name} to {group}.";
    }

    /// <summary>
    /// Right-click a project › Private, or the check box in its Details: kept from everyone you share with (their
    /// sidebars lose it at once), or shared again through its group.
    /// </summary>
    public void SetPrivate(Project project, bool isPrivate)
    {
        if (NotYours(project) || project.IsInbox || project.Private == isPrivate) return;
        project.Private = isPrivate;
        State.MarkDirty(project, touch: false);
        if (_projectViewModels.TryGetValue(project.Id, out var vm)) vm.RefreshPrivate();
        State.NotifyListChanged();
        State.StatusText = isPrivate
            ? $"{project.Name} is private: people you share with no longer see it."
            : $"{project.Name} isn't private any more: people its group ({ProjectGroups.Of(project)}) is shared with see it again.";
    }

    [RelayCommand]
    private void TogglePrivate(ProjectListItemViewModel? item)
    {
        if (item != null) SetPrivate(item.Project, !item.Project.Private);
    }

    /// <summary>Right-click › Clean up…: asks the agents working in the project for a clean-up plan of its folder.</summary>
    [RelayCommand]
    private void CleanUpProject(ProjectListItemViewModel? item)
    {
        if (item == null || NotYours(item.Project) || item.Project.IsInbox) return;
        OpenProject(item.Project).StartCleanupCommand.Execute(null);
    }

    /// <summary>Right-click › Ship…: opens the project with a new ship request to fill in.</summary>
    [RelayCommand]
    private void ShipProject(ProjectListItemViewModel? item)
    {
        if (item == null || NotYours(item.Project) || item.Project.IsInbox) return;
        OpenProject(item.Project).StartShipCommand.Execute(null);
    }

    [RelayCommand]
    private void HideProject(ProjectListItemViewModel? item)
    {
        if (item == null) return;
        SidebarFilter.SetShown([item.Project], false);
        State.StatusText = $"Hid {item.Name}. The eye button next to a filter box shows it again.";
    }

    public List<TemplateSourceViewModel> TemplateSourcesFor(Project target) =>
        State.Projects
            .Where(p => !p.IsInbox && p != target && p.Categories.Count > 0)
            .OrderBy(p => ProjectGroups.Order(p, State.Settings))
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new TemplateSourceViewModel(p, source => CopyCategories(target, source)))
            .ToList();

    /// <summary>Opens the target project and adds the source's categories it lacks (undoable there).</summary>
    public void CopyCategories(Project target, Project source)
    {
        if (NotYours(target)) return;
        var added = OpenProject(target).CopyCategoriesFrom(source);
        State.StatusText = added switch
        {
            0 => $"{target.Name} already has all of {source.Name}'s categories.",
            1 => $"Copied 1 category from {source.Name} (Ctrl+Z undoes it).",
            _ => $"Copied {added} categories from {source.Name} (Ctrl+Z undoes it).",
        };
    }

    [RelayCommand]
    private void ExportProject(ProjectListItemViewModel? item)
    {
        if (item != null) new ExportDialog(State, item.Project) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    public void Navigate(ProJict.Core.Wiki.WikiHit hit) => ShowWikiPage(hit.Page);

    public void Navigate(SearchHit hit)
    {
        var vm = OpenProject(hit.Project);
        if (hit.Item != null) vm.Reveal(hit.Item);
        else if (hit.Note != null) vm.RevealNote(hit.Note);
    }
}
