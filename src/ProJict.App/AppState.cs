using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProJict.Core.Chat;
using ProJict.Core.Export;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Storage;
using ProJict.Core.Wiki;

namespace ProJict.App;

/// <summary>Owns the loaded workspace, chat and wiki and persists them: debounced autosave, periodic backups and the optional TXT mirror.</summary>
public sealed partial class AppState : ObservableObject
{
    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(30);
    private readonly HashSet<Project> _dirty = [];
    private readonly HashSet<Project> _mirrorPending = [];
    private readonly HashSet<ChatChannel> _dirtyChannels = [];
    private readonly HashSet<WikiPage> _dirtyPages = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly bool _persistAppSettings;
    private bool _settingsDirty;
    private DateTime _lastBackup = DateTime.MinValue;

    public AppState(ProjectStore store, AppSettings appSettings, bool persistAppSettings)
    {
        Store = store;
        AppSettings = appSettings;
        _persistAppSettings = persistAppSettings;
        Workspace = store.Load();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveNow();
        // Game studios from before the UI designer and Cinematics roles get them, nobody hired yet.
        foreach (var project in Workspace.Projects.Where(StudioFlow.AddNewRoles).ToList()) MarkDirty(project, touch: false);
        Chat = new ChatRoom(store.LoadChannels(), () => UserName);
        store.CacheRoot = PrivateFilesRoot;
        // A private conversation's files, and a channel's big ones, go to the Chat cache, out of the data folder (the
        // composer puts them there itself).
        Chat.Posted += (channel, message) =>
        {
            foreach (var file in message.Attachments ?? [])
                if (AppSettings.GoesToChatCache(channel.Direct, file.Size)) Store.MoveToCache(file);
        };
        Chat.Changed += channel =>
        {
            _dirtyChannels.Add(channel);
            RestartTimer();
        };
        Chat.Removed += channel =>
        {
            _dirtyChannels.Remove(channel);
            try { Store.DeleteChannel(channel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = $"#{channel.Name}: {ex.Message}"; }
        };
        Wiki = new WikiBook(store.LoadWikiPages(), () => UserName);
        Wiki.Changed += page =>
        {
            _dirtyPages.Add(page);
            RestartTimer();
        };
        Wiki.Removed += page =>
        {
            _dirtyPages.Remove(page);
            try { Store.DeleteWikiPage(page); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = $"{page.Title}: {ex.Message}"; }
        };
        if (AppSettings.UnreadSince == null)
        {
            AppSettings.UnreadSince = DateTime.Now;
            SaveAppSettings();
        }
    }

    public ProjectStore Store { get; }
    private Services.AgentPictures? _pictures;
    /// <summary>Agents' pictures (the Agents view), in the data folder.</summary>
    public Services.AgentPictures Pictures => _pictures ??= new Services.AgentPictures(Store.DataRoot);
    /// <summary>False in tests: nothing is written to this computer's settings (or sharing) files.</summary>
    public bool PersistsSettings => _persistAppSettings;
    public AppSettings AppSettings { get; }
    public Workspace Workspace { get; }
    public WorkspaceSettings Settings => Workspace.Settings;
    public IReadOnlyList<Project> Projects => Workspace.Projects;
    public Project Inbox => Workspace.Inbox;
    /// <summary>The chat channels, saved one file each under &lt;data&gt;/chat.</summary>
    public ChatRoom Chat { get; }
    /// <summary>The wiki's pages, saved one file each under &lt;data&gt;/wiki.</summary>
    public WikiBook Wiki { get; }
    /// <summary>You in the wiki.</summary>
    public WikiAuthor WikiMe => new(UserName);
    /// <summary>You in Chat: the owner of the workspace, who moderates every channel.</summary>
    public ChatActor ChatMe => new(UserName, false, Owner: true);

    [ObservableProperty] private string _statusText = "";

    /// <summary>How you sign comments and history entries (Settings › Comments &amp; history, else the Windows user name).</summary>
    public string UserName => string.IsNullOrWhiteSpace(AppSettings.UserName) ? Environment.UserName : AppSettings.UserName.Trim();

    /// <summary>Raised after a group's discussion changed (projects' discussions raise <see cref="ProjectChanged"/>).</summary>
    public event Action<string>? GroupCommentsChanged;

    public void PostGroupComment(string group, Comment comment, Guid? replyTo)
    {
        Settings.SetComments(group, Discussions.Post(Settings.CommentsFor(group), comment, replyTo));
        MarkSettingsDirty();
        GroupCommentsChanged?.Invoke(group);
    }

    public void RemoveGroupComment(string group, Guid commentId)
    {
        Settings.SetComments(group, Discussions.Remove(Settings.CommentsFor(group), commentId));
        MarkSettingsDirty();
        GroupCommentsChanged?.Invoke(group);
    }

    // ───────────── Unread comments ─────────────

    /// <summary>Raised after you read a discussion, so its red dots go.</summary>
    public event Action? ReadMarksChanged;

    public static string ReadKey(Guid targetId) => targetId.ToString("N");
    public static string ReadKey(string group) => "group:" + group.Trim().ToLowerInvariant();

    /// <summary>True when someone else commented since you last read the discussion.</summary>
    public bool HasUnread(string key, List<Comment>? comments)
    {
        if (comments is not { Count: > 0 }) return false;
        var since = AppSettings.DiscussionsRead.TryGetValue(key, out var read) ? read : DateTime.MinValue;
        if (AppSettings.UnreadSince is { } start && start > since) since = start;
        var me = UserName;
        return comments.Walk().Any(e => !e.Comment.Deleted && e.Comment.At > since && !string.Equals(e.Comment.By, me, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The discussion is open in front of you: everything in it counts as read.</summary>
    public void MarkRead(string key, List<Comment>? comments)
    {
        if (!HasUnread(key, comments)) return;
        // A comment stamped by another computer's clock may be a little ahead of this one.
        var newest = comments.Walk().Max(e => e.Comment.At);
        AppSettings.DiscussionsRead[key] = newest > DateTime.Now ? newest : DateTime.Now;
        SaveAppSettings();
        ReadMarksChanged?.Invoke();
    }

    /// <summary>Something in the project waits for you: a comment you haven't read, or work an agent finished.</summary>
    public bool NeedsAttention(Project project) =>
        project.Walk().Any(l => l.Item.NeedsReview || HasUnread(ReadKey(l.Item.Id), l.Item.Comments)) ||
        project.Categories.Any(c => HasUnread(ReadKey(c.Id), c.Comments)) ||
        ShipFlow.WaitsForYou(project) || project.OpenCleanup?.State == CleanupState.Planned;

    /// <summary>What waits for you in a project, for its red dot's tooltip; empty when nothing does.</summary>
    public string AttentionSummary(Project project)
    {
        var review = project.Walk().Count(l => l.Item.NeedsReview);
        var discussions = project.Walk().Count(l => HasUnread(ReadKey(l.Item.Id), l.Item.Comments)) +
                          project.Categories.Count(c => HasUnread(ReadKey(c.Id), c.Comments));
        var parts = new List<string>();
        if (review > 0) parts.Add(review == 1 ? "1 item to review" : $"{review} items to review");
        if (discussions > 0) parts.Add(discussions == 1 ? "new comments in 1 discussion" : $"new comments in {discussions} discussions");
        if (ShipFlow.WaitsForYou(project)) parts.Add("a ship plan to approve");
        if (project.OpenCleanup?.State == CleanupState.Planned) parts.Add("a clean-up plan to decide on");
        return parts.Count == 0 ? "" : $"Waiting for you: {string.Join(", ", parts)}. On the board, All states › Needs you shows them.";
    }

    /// <summary>Raised after any edit to a project (sidebar counts, dashboard).</summary>
    public event Action<Project>? ProjectChanged;

    /// <summary>Raised when projects are added/removed or renamed/regrouped.</summary>
    public event Action? ProjectListChanged;

    /// <summary>Raised when a project was changed outside its open view (quick capture, import), so views rebuild.</summary>
    public event Action<Project>? ProjectReplaced;

    public void MarkDirty(Project project, bool touch = true)
    {
        if (touch) project.UpdatedAt = DateTime.Now;
        if (project.Remote != null)
        {
            // Another ProJict's project: the change goes there, nothing is saved here.
            RemoteProjectChanged?.Invoke(project);
            ProjectChanged?.Invoke(project);
            return;
        }
        _dirty.Add(project);
        RestartTimer();
        ProjectChanged?.Invoke(project);
    }

    // ───────────── Projects shared from another ProJict ─────────────

    /// <summary>A project shown from another ProJict changed here (sent to it by the guest connection).</summary>
    public event Action<Project>? RemoteProjectChanged;

    /// <summary>Where pictures and files of other ProJicts' projects are kept once fetched.</summary>
    /// <summary>Where private conversations' files are kept: Settings › Chat, else %LocalAppData%\ProJict\Chat files (by the data folder when testing).</summary>
    public string PrivateFilesRoot => AppSettings.PrivateFilesFolder is { Length: > 0 } folder ? folder : DefaultPrivateFilesRoot;
    public string DefaultPrivateFilesRoot => _persistAppSettings
        ? Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "Chat files")
        : Path.Combine(Store.DataRoot, ".chat-files");

    /// <summary>Uses another folder for private conversations' files, moving what's there; null for the usual one.</summary>
    public void MovePrivateFiles(string? folder)
    {
        var from = PrivateFilesRoot;
        AppSettings.PrivateFilesFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        Store.CacheRoot = PrivateFilesRoot;
        try { PrivateFiles.MoveAll(from, PrivateFilesRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = ex.Message; }
    }

    private DispatcherTimer? _cleanOutTimer;

    /// <summary>
    /// Deletes private conversations' files older than the days you keep them (Settings › Chat), off the UI thread: when
    /// ProJict starts, then every six hours.
    /// </summary>
    public void CleanOutPrivateFiles()
    {
        if (_cleanOutTimer == null && _persistAppSettings)
        {
            _cleanOutTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _cleanOutTimer.Tick += (_, _) => CleanOutPrivateFiles();
            _cleanOutTimer.Start();
        }
        var (root, days, cap) = (PrivateFilesRoot, AppSettings.PrivateFilesDays, (long)AppSettings.ChatCacheMaxGb * 1024 * 1024 * 1024);
        Task.Run(() =>
        {
            try { PrivateFiles.CleanOut(root, days, DateTime.Now, cap); }
            catch (Exception) { /* the next round tries again */ }
        });
    }

    public string RemoteCacheRoot => _persistAppSettings
        ? Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "remote")
        : Path.Combine(Store.DataRoot, ".remote-cache");

    /// <summary>Where pictures of wiki diagrams are kept once drawn, and WebView2's own data (it draws them).</summary>
    public string DiagramCacheRoot => _persistAppSettings
        ? Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "diagrams")
        : Path.Combine(Store.DataRoot, ".diagram-cache");
    public string WebViewDataRoot => _persistAppSettings
        ? Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "WebView2")
        : Path.Combine(Store.DataRoot, ".webview2");

    /// <summary>The local file of an attachment: in the data folder, or for another ProJict's project in the cache (null until fetched).</summary>
    public string AttachmentPath(Project? project, Attachment attachment)
    {
        // A private conversation's file, also one fetched from another ProJict, is in the cache for them.
        if (project?.Remote is not { } remote || attachment.Cached) return Store.AttachmentPath(attachment);
        var cached = Path.Combine(RemoteCacheRoot, ProjectStore.SafeFileName(remote.HostKey), attachment.Id.ToString("N"), ProjectStore.SafeFileName(attachment.FileName));
        // Files you add to such a project are copied into your own data folder first, then sent.
        var own = Store.AttachmentPath(attachment);
        return !File.Exists(cached) && File.Exists(own) ? own : cached;
    }

    /// <summary>Shows a project shared by another ProJict (kept in memory only).</summary>
    public void AddRemoteProject(Project project)
    {
        Workspace.Projects.Add(project);
        NotifyListChanged();
    }

    public void RemoveRemoteProject(Project project)
    {
        if (project.Remote == null || !Workspace.Projects.Remove(project)) return;
        NotifyListChanged();
    }

    public void MarkSettingsDirty()
    {
        _settingsDirty = true;
        RestartTimer();
    }

    public void NotifyListChanged() => ProjectListChanged?.Invoke();

    /// <summary>A project changed without anything to save here (another ProJict's version arrived).</summary>
    public void NotifyProjectChanged(Project project) => ProjectChanged?.Invoke(project);

    public void NotifyReplaced(Project project)
    {
        MarkDirty(project);
        ProjectReplaced?.Invoke(project);
    }

    public void AddProject(Project project)
    {
        Workspace.Projects.Add(project);
        Settings.ProjectOrder.Add(project.Id);
        if (!string.IsNullOrWhiteSpace(project.Group) && !project.IsInbox &&
            !Settings.Groups.Contains(project.Group, StringComparer.OrdinalIgnoreCase))
            Settings.Groups.Add(project.Group);
        MarkSettingsDirty();
        MarkDirty(project, touch: false);
        NotifyListChanged();
    }

    public void DeleteProject(Project project)
    {
        if (project.IsInbox) return;
        Workspace.Projects.Remove(project);
        Settings.ProjectOrder.Remove(project.Id);
        _dirty.Remove(project);
        _mirrorPending.Remove(project);
        try
        {
            Store.Delete(project);
            if (Settings.MirrorEnabled && !string.IsNullOrWhiteSpace(Settings.MirrorFolder))
                ExportService.RemoveProjectFiles(Settings.MirrorFolder, project.Id);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not remove project file: {ex.Message}";
        }
        MarkSettingsDirty();
        NotifyListChanged();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        var failed = new List<string>();
        foreach (var project in _dirty.ToList())
        {
            try
            {
                Store.Save(project);
                _dirty.Remove(project);
                _mirrorPending.Add(project);
            }
            catch (Exception ex)
            {
                failed.Add($"{project.Name}: {ex.Message}");
            }
        }

        foreach (var channel in _dirtyChannels.ToList())
        {
            try
            {
                Store.SaveChannel(channel);
                _dirtyChannels.Remove(channel);
            }
            catch (Exception ex)
            {
                failed.Add($"#{channel.Name}: {ex.Message}");
            }
        }

        foreach (var page in _dirtyPages.ToList())
        {
            try
            {
                Store.SaveWikiPage(page);
                _dirtyPages.Remove(page);
            }
            catch (Exception ex)
            {
                failed.Add($"{page.Title}: {ex.Message}");
            }
        }

        if (_settingsDirty)
        {
            try
            {
                Store.SaveSettings(Settings);
                _settingsDirty = false;
            }
            catch (Exception ex)
            {
                failed.Add($"workspace.json: {ex.Message}");
            }
        }

        if (failed.Count > 0)
        {
            StatusText = "Save failed — " + string.Join("; ", failed);
            RestartTimer();
            return;
        }

        var mirrorNote = RunMirror();
        MaybeBackup();
        StatusText = $"Saved {DateTime.Now:HH:mm:ss}{mirrorNote}";
    }

    public void SaveAppSettings()
    {
        if (!_persistAppSettings) return;
        try { AppSettings.Save(); }
        catch (Exception) { /* window placement is not worth an error dialog */ }
    }

    private string RunMirror()
    {
        if (_mirrorPending.Count == 0) return "";
        if (!Settings.MirrorEnabled || string.IsNullOrWhiteSpace(Settings.MirrorFolder))
        {
            _mirrorPending.Clear();
            return "";
        }
        try
        {
            var result = ExportService.Export(_mirrorPending.ToList(), Settings.MirrorFolder, Settings.Export, overwriteConflicts: false);
            _mirrorPending.Clear();
            if (result.Conflicts.Count > 0)
                return $" · mirror skipped {result.Conflicts.Count} file(s) not written by ProJict: {string.Join(", ", result.Conflicts)}";
            if (result.Errors.Count > 0) return " · mirror error: " + result.Errors[0];
            return result.Written.Count > 0 ? $" · mirrored {result.Written.Count} file(s)" : "";
        }
        catch (Exception ex)
        {
            return " · mirror error: " + ex.Message;
        }
    }

    public void BackupNow()
    {
        try
        {
            Store.Backup();
            _lastBackup = DateTime.Now;
        }
        catch (Exception ex)
        {
            StatusText = "Backup failed: " + ex.Message;
        }
    }

    /// <summary>Deletes stored attachments nothing refers to any more; off the UI thread, since it may read through the backups.</summary>
    public void CleanUpAttachments()
    {
        var inUse = Projects.SelectMany(p => p.Attachments()).Concat(Settings.GroupAttachments())
            .Concat(Chat.Channels.SelectMany(ChatRoom.Files))
            .Concat(Wiki.Pages.SelectMany(p => p.Attachments ?? [])).Select(a => a.Id).ToHashSet();
        var started = DateTime.Now;
        Task.Run(() =>
        {
            try { Store.RemoveUnusedAttachments(inUse, started); }
            catch (Exception) { /* tidying up can wait for the next start */ }
        });
    }

    private void MaybeBackup()
    {
        if (DateTime.Now - _lastBackup >= BackupInterval) BackupNow();
    }

    private void RestartTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
