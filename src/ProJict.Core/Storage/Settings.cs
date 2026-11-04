using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.Core.Storage;

/// <summary>What can notify you (Settings › Notifications).</summary>
public enum NotificationKind { Review, Comment, ShipPlan, JoinRequest, CheckOut, AnyComment, TokenExpiring, Update, ChatMention, ChatMessage }

/// <summary>Which Windows notifications you want, and the projects you muted.</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>An AI agent finished an item: it waits for your review.</summary>
    public bool Review { get; set; } = true;
    /// <summary>New comments in a discussion you wrote in, or on an item you added.</summary>
    public bool Comment { get; set; } = true;
    public bool ShipPlan { get; set; } = true;
    public bool JoinRequest { get; set; } = true;
    public bool CheckOut { get; set; }
    /// <summary>Every new comment, also in discussions you're not part of.</summary>
    public bool AnyComment { get; set; }
    public bool TokenExpiring { get; set; }
    public bool Update { get; set; }
    /// <summary>Someone @mentioned you in a chat channel.</summary>
    public bool ChatMention { get; set; } = true;
    /// <summary>Every new message in your chat channels.</summary>
    public bool ChatMessage { get; set; }
    public List<Guid> MutedProjects { get; set; } = [];

    public bool Allows(NotificationKind kind) => kind switch
    {
        NotificationKind.Review => Review,
        NotificationKind.Comment => Comment,
        NotificationKind.ShipPlan => ShipPlan,
        NotificationKind.JoinRequest => JoinRequest,
        NotificationKind.CheckOut => CheckOut,
        NotificationKind.AnyComment => AnyComment,
        NotificationKind.TokenExpiring => TokenExpiring,
        NotificationKind.ChatMention => ChatMention,
        NotificationKind.ChatMessage => ChatMessage,
        _ => Update,
    };
}

/// <summary>Machine-local bootstrap settings (%LocalAppData%\ProJict\settings.json).</summary>
/// <summary>How the dashboard shows projects (its switch, top right).</summary>
public enum DashboardMode { Cards, List, Table, Board }

public sealed class AppSettings
{
    public string? DataRoot { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public Guid? LastProjectId { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Let AI agents (MCP clients started with ProJict.exe --mcp) read and update projects.</summary>
    public bool AllowAgents { get; set; } = true;
    /// <summary>Let AI agents read and post in Chat (Settings › AI agents).</summary>
    public bool AgentsUseChat { get; set; } = true;
    /// <summary>
    /// When an agent needs your permission (its PermissionRequest hook runs ProJictBridge.exe --permission-hook), it asks
    /// you in Chat too, and your 👍 or 👎 answers it (Settings › AI agents).
    /// </summary>
    public bool ApproveInChat { get; set; } = true;
    /// <summary>
    /// The Chat cache on this computer (Settings › Chat): files nobody used for this many days are cleaned out (0 keeps
    /// them). It holds private conversations' files, and channels' big ones.
    /// </summary>
    public int PrivateFilesDays { get; set; } = ProJict.Core.Features.PrivateFiles.DefaultDays;
    /// <summary>Where that cache is; null for %LocalAppData%\ProJict\Chat files.</summary>
    public string? PrivateFilesFolder { get; set; }
    /// <summary>A channel's files bigger than this (MB) go to the Chat cache too (0: none do).</summary>
    public int BigChannelFileMb { get; set; } = 50;
    /// <summary>Past this size (GB) the cache cleans out its least recently used files (0: no limit).</summary>
    public int ChatCacheMaxGb { get; set; } = 10;
    /// <summary>How Chat's list is sorted (its sort button), on this computer.</summary>
    public Chat.ChatOrder ChatOrder { get; set; }
    /// <summary>Whose voices read agents' answers in calls (Settings › Chat).</summary>
    public Chat.CallVoices CallVoices { get; set; }
    /// <summary>Whether a file of this size, posted in such a channel, goes to the Chat cache.</summary>
    public bool GoesToChatCache(bool direct, long size) => direct || (BigChannelFileMb > 0 && size > (long)BigChannelFileMb * 1024 * 1024);
    /// <summary>Read-only API tokens may still use Chat fully: post, start channels, invite (Settings › API).</summary>
    public bool ReadOnlyTokensUseChat { get; set; } = true;
    /// <summary>Chat lists the agents' private conversations with each other (the toggle in its header).</summary>
    public bool ShowAgentChats { get; set; }
    /// <summary>The sections of Chat's sidebar you folded ("yours", "others", "private", "host:" and its owner).</summary>
    public List<string>? FoldedChatSections { get; set; }
    /// <summary>How you sign comments and history entries; empty means the Windows user name.</summary>
    public string? UserName { get; set; }
    /// <summary>Ask the GitHub CLI (gh) for its sign-in, so release checks include private repositories.</summary>
    public bool UseGitHubCliSignIn { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>Release tag the user chose to skip; no reminder until a newer one appears.</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>When you last read each discussion (key: AppState.ReadKey), for the red unread dots.</summary>
    public Dictionary<string, DateTime> DiscussionsRead { get; set; } = [];
    /// <summary>Comments from before this count as read: set the first time ProJict marked unread comments.</summary>
    public DateTime? UnreadSince { get; set; }
    /// <summary>The Settings sections you left open, opened again next time.</summary>
    public List<string>? OpenSettingsSections { get; set; }
    /// <summary>How the dashboard shows projects: cards, a list, a table or a board by status.</summary>
    public DashboardMode DashboardMode { get; set; }
    /// <summary>The dashboard's List and Table show projects under their groups (Personal, Work, …), like the sidebar.</summary>
    public bool DashboardGroupByGroup { get; set; } = true;
    /// <summary>The dashboard's side panels (waiting for review, agent activity, done this week) are folded away.</summary>
    public bool DashboardPanelsFolded { get; set; }
    /// <summary>
    /// The project header's buttons shown big, on the left of the others (their names: "Ship", "Clean up", "Folder"…),
    /// chosen with the cog there. Null until you choose: Ship and Clean up.
    /// </summary>
    public List<string>? BigHeaderButtons { get; set; }
    /// <summary>How wide the wiki's list of pages is, dragged by its edge; null for the usual 270.</summary>
    public double? WikiListWidth { get; set; }
    /// <summary>The wiki editor's share of the room it has with its preview (0.5 is half), dragged between them; null for half.</summary>
    public double? WikiEditorShare { get; set; }
    /// <summary>Windows notifications for what needs you (Settings › Notifications).</summary>
    public NotificationSettings Notifications { get; set; } = new();
    /// <summary>How the Feed groups what happened: by day, week or month.</summary>
    public ProJict.Core.Features.ActivityGrouping FeedGrouping { get; set; }
    /// <summary>How the Agents view groups its earlier agent work.</summary>
    public ProJict.Core.Features.ActivityGrouping AgentsGrouping { get; set; }
    /// <summary>Which agents the Agents view shows and how far back its earlier work goes.</summary>
    public AgentsViewSettings AgentsView { get; set; } = new();
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Json.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, Json.Serialize(this));
    }

    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProJict");
}

public sealed class ExportOptions
{
    /// <summary>Write every category header even when it has no items (like the hand-made DONE files).</summary>
    public bool IncludeEmptyCategories { get; set; } = true;
    /// <summary>Also list done items as "+ ..." in the TODO file.</summary>
    public bool IncludeDoneInTodo { get; set; }
    public bool ExportNotes { get; set; }
}

/// <summary>Workspace-wide settings stored next to the data (workspace.json).</summary>
/// <summary>You put every agent to sleep (the Agents view): they finish what they're doing and stop, until you wake them.</summary>
public sealed class AgentsSleep
{
    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    /// <summary>When you woke them up again; null while they sleep.</summary>
    public DateTime? WokenAt { get; set; }
    public string? WokenBy { get; set; }
}

public sealed class WorkspaceSettings
{
    public const string PersonalGroup = "Personal";
    public const string WorkGroup = "Work";
    /// <summary>Not a real group: the sidebar section for archived projects.</summary>
    public const string ArchivedGroup = "Archived";

    public List<Guid> ProjectOrder { get; set; } = [];
    /// <summary>Sidebar groups in order. Personal and Work are always there; the rest can be added and removed.</summary>
    public List<string> Groups { get; set; } = [PersonalGroup, WorkGroup];
    public string? ExportFolder { get; set; }
    public ExportOptions Export { get; set; } = new();
    public bool MirrorEnabled { get; set; }
    public string? MirrorFolder { get; set; }
    /// <summary>Folders the TXT files were imported from; exporting into them would overwrite the archive.</summary>
    public List<string> ImportSourceFolders { get; set; } = [];
    public string Hotkey { get; set; } = "Ctrl+Alt+J";
    /// <summary>Every agent put to sleep (and woken again); null when it never happened.</summary>
    public AgentsSleep? Sleep { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int StaleDays { get; set; } = 30;
    public string? VsCodePath { get; set; }
    /// <summary>GitHub user or organization whose repositories are matched to projects by name.</summary>
    public string? GitHubAccount { get; set; }
    public string Theme { get; set; } = "System";
    public bool SideBySide { get; set; }
    /// <summary>The board leaves out categories with nothing left to do (Hide empty).</summary>
    public bool HideEmptyCategories { get; set; }
    /// <summary>The largest file you, a guest or an agent may post in a discussion, in MB.</summary>
    public int CommentFileLimitMb { get; set; } = DefaultCommentFileLimitMb;
    /// <summary>Item types you added (the tag picker's Other…), offered in every project after ProJict's own.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CustomTags { get; set; }
    /// <summary>People and agents whose messages you don't see in Chat's channels (see <see cref="Chat.ChatIgnoring"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IgnoredInChat { get; set; }
    public const int DefaultCommentFileLimitMb = 10;
    [System.Text.Json.Serialization.JsonIgnore] public long CommentFileLimitBytes => (long)Math.Max(1, CommentFileLimitMb) * 1024 * 1024;
    /// <summary>The largest file you, a guest or an agent may post in Chat, in MB (Settings › Chat).</summary>
    public int ChatFileLimitMb { get; set; } = DefaultChatFileLimitMb;
    public const int DefaultChatFileLimitMb = 500;
    /// <summary>The most either file limit can be set to, in MB.</summary>
    public const int MaxFileLimitMb = 2048;
    [System.Text.Json.Serialization.JsonIgnore] public long ChatFileLimitBytes => (long)Math.Clamp(ChatFileLimitMb, 1, MaxFileLimitMb) * 1024 * 1024;

    /// <summary>The files posted in the groups' discussions (kept here, not in a project).</summary>
    public IEnumerable<Attachment> GroupAttachments() => (GroupComments ?? []).Values.SelectMany(threads => threads.Attachments());
    /// <summary>The board lists open items by priority (most pressing first) instead of in their own order (kept for older ProJicts: see <see cref="BoardSort"/>).</summary>
    public bool SortByPriority { get; set; }
    /// <summary>How the board lists open items: in their own order, or by priority, name or date added.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public BoardSort BoardSort { get; set; }

    /// <summary>How the board lists open items now, also when an older ProJict only set <see cref="SortByPriority"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BoardSort ItemSort
    {
        get => BoardSort == BoardSort.Board && SortByPriority ? BoardSort.Priority : BoardSort;
        set
        {
            BoardSort = value;
            SortByPriority = value == BoardSort.Priority;
        }
    }
    public bool MarkCopiedInProgress { get; set; }
    /// <summary>The priorities of items AI agents may work on (None = items without one); items on hold never. See AgentAccess.</summary>
    public List<Priority> AgentPriorities { get; set; } = [.. AgentAccess.DefaultPriorities];
    /// <summary>
    /// Your instructions for every AI agent in every project (Settings › AI agents), e.g. not to add themselves as co-authors of
    /// commits: told to each agent when it connects and with current_project, and put at the top of Copy prompt.
    /// </summary>
    public string? AgentInstructions { get; set; }
    public Guid? LastCaptureProjectId { get; set; }
    public string? LastCaptureCategory { get; set; }
    /// <summary>Projects left out of the sidebar list and the dashboard (shown again from either eye dropdown).</summary>
    public List<Guid> HiddenProjects { get; set; } = [];
    /// <summary>Where 0.1.2–0.1.4 kept the sidebar's hidden projects; moved into HiddenProjects on load.</summary>
    public List<Guid>? SidebarHiddenProjects { get; set; }
    /// <summary>Sidebar groups the user collapsed.</summary>
    public List<string> CollapsedGroups { get; set; } = [];
    /// <summary>Discussion threads per group name; null while no group has any.</summary>
    public Dictionary<string, List<Comment>>? GroupComments { get; set; }

    public List<Comment>? CommentsFor(string group) =>
        GroupComments?.FirstOrDefault(kv => string.Equals(kv.Key, group.Trim(), StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Stores a group's threads under its current name; null or empty removes them.</summary>
    public void SetComments(string group, List<Comment>? threads)
    {
        if (GroupComments != null)
            foreach (var key in GroupComments.Keys.Where(k => string.Equals(k, group.Trim(), StringComparison.OrdinalIgnoreCase)).ToList())
                GroupComments.Remove(key);
        if (threads is { Count: > 0 }) (GroupComments ??= [])[group.Trim()] = threads;
        if (GroupComments is { Count: 0 }) GroupComments = null;
    }

    /// <summary>The names agents picked for themselves, and the client each runs in ("Ada" → "Claude Code").</summary>
    public Dictionary<string, string>? AgentNames { get; set; }

    /// <summary>The name each client last went by in each folder it worked in (AgentFolderNames); null while none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, AgentFolder>? AgentFolders { get; set; }

    /// <summary>Shared things agents take turns with, such as a test PC (claim_resource, #bb95b66d); null while none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SharedResource>? SharedResources { get; set; }

    /// <summary>Agents that can't make their own picture and asked another agent to (set_picture wanted=true); null while none.</summary>
    public List<string>? PicturesWanted { get; set; }

    /// <summary>How much of its usage each agent has used, by name, as it last reported (or you set it); null while none.</summary>
    public Dictionary<string, ProJict.Core.Agents.AgentUsage>? AgentUsage { get; set; }

    /// <summary>An agent's usage, whatever the case of its name.</summary>
    public ProJict.Core.Agents.AgentUsage? UsageOf(string agent) =>
        AgentUsage?.FirstOrDefault(u => string.Equals(u.Key, agent.Trim(), StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Keeps an agent's usage (replacing what it had), or with null forgets it.</summary>
    public void SetUsage(string agent, ProJict.Core.Agents.AgentUsage? usage)
    {
        if (AgentUsage != null)
            foreach (var key in AgentUsage.Keys.Where(k => string.Equals(k, agent.Trim(), StringComparison.OrdinalIgnoreCase)).ToList()) AgentUsage.Remove(key);
        if (usage != null) (AgentUsage ??= [])[agent.Trim()] = usage;
        if (AgentUsage is { Count: 0 }) AgentUsage = null;
    }

    public static bool IsDefaultGroup(string? name) =>
        string.Equals(name?.Trim(), PersonalGroup, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), WorkGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>Brings settings written by older versions up to date.</summary>
    public void MigrateOldSettings()
    {
        if (SidebarHiddenProjects is { } old)
        {
            HiddenProjects = HiddenProjects.Union(old).ToList();
            SidebarHiddenProjects = null;
        }
    }

    /// <summary>Puts Personal and Work back if a hand-edited workspace.json dropped them, and drops blanks and duplicates.</summary>
    public void EnsureDefaultGroups()
    {
        Groups = Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!Groups.Contains(WorkGroup, StringComparer.OrdinalIgnoreCase)) Groups.Insert(0, WorkGroup);
        if (!Groups.Contains(PersonalGroup, StringComparer.OrdinalIgnoreCase)) Groups.Insert(0, PersonalGroup);
    }

    /// <summary>The last export folder, else "ProJict-export" next to the data folder.</summary>
    public string DefaultExportFolder(string dataRoot) =>
        ExportFolder ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataRoot).TrimEnd('\\')) ?? dataRoot, "ProJict-export");
}

/// <summary>The name an agent went by in a folder, and when it last did (AgentFolderNames).</summary>
public sealed class AgentFolder
{
    public string Name { get; set; } = "";
    public DateTime At { get; set; }
}
