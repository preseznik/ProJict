using System.Globalization;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.Core.Storage;

public sealed class Workspace
{
    public WorkspaceSettings Settings { get; init; } = new();
    public List<Project> Projects { get; init; } = [];
    public Project Inbox => Projects.First(p => p.IsInbox);
}

/// <summary>
/// One JSON file per project under &lt;dataRoot&gt;/projects, one per chat channel under &lt;dataRoot&gt;/chat, one per
/// wiki page under &lt;dataRoot&gt;/wiki, workspace.json for settings, timestamped copies under &lt;dataRoot&gt;/backups.
/// </summary>
public sealed class ProjectStore
{
    private const string BackupStampFormat = "yyyyMMdd-HHmmss";
    private readonly Dictionary<Guid, string> _fileNames = [];
    private readonly List<string> _loadErrors = [];

    public ProjectStore(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }
    public string ProjectsDir => Path.Combine(DataRoot, "projects");
    public string BackupsDir => Path.Combine(DataRoot, "backups");
    public string WorkspacePath => Path.Combine(DataRoot, "workspace.json");
    public string AttachmentsDir => Path.Combine(DataRoot, "attachments");
    public string ChatDir => Path.Combine(DataRoot, "chat");
    public string WikiDir => Path.Combine(DataRoot, "wiki");
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public Workspace Load()
    {
        Directory.CreateDirectory(ProjectsDir);
        _fileNames.Clear();
        _loadErrors.Clear();

        var settings = new WorkspaceSettings();
        if (File.Exists(WorkspacePath))
        {
            try { settings = Json.Deserialize<WorkspaceSettings>(File.ReadAllText(WorkspacePath)) ?? new(); }
            catch (Exception ex) { _loadErrors.Add($"workspace.json: {ex.Message}"); }
        }

        var projects = new List<Project>();
        foreach (var file in Directory.EnumerateFiles(ProjectsDir, "*.json"))
        {
            try
            {
                var project = Json.Deserialize<Project>(File.ReadAllText(file));
                if (project == null) continue;
                if (_fileNames.ContainsKey(project.Id))
                {
                    _loadErrors.Add($"{Path.GetFileName(file)}: duplicate project id, skipped");
                    continue;
                }
                _fileNames[project.Id] = Path.GetFileName(file);
                projects.Add(project);
            }
            catch (Exception ex)
            {
                // Leave the broken file in place untouched; it is never overwritten because its id is unknown.
                _loadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        settings.MigrateOldSettings();
        settings.EnsureDefaultGroups();

        if (!projects.Any(p => p.IsInbox))
        {
            var inbox = Project.CreateInbox();
            projects.Add(inbox);
            Save(inbox);
        }

        var order = settings.ProjectOrder;
        projects = projects
            .OrderBy(p => p.IsInbox ? -1 : order.IndexOf(p.Id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Workspace { Settings = settings, Projects = projects };
    }

    public void Save(Project project)
    {
        Directory.CreateDirectory(ProjectsDir);
        var desired = UniqueFileName(project);
        Json.WriteFile(Path.Combine(ProjectsDir, desired), project);
        if (_fileNames.TryGetValue(project.Id, out var old) &&
            !string.Equals(old, desired, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = Path.Combine(ProjectsDir, old);
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        _fileNames[project.Id] = desired;
    }

    public void SaveSettings(WorkspaceSettings settings)
    {
        Directory.CreateDirectory(DataRoot);
        AtomicFile.WriteAllText(WorkspacePath, Json.Serialize(settings));
    }

    /// <summary>Moves the project's file into backups/deleted instead of erasing it.</summary>
    public void Delete(Project project)
    {
        if (!_fileNames.Remove(project.Id, out var name)) return;
        var path = Path.Combine(ProjectsDir, name);
        if (!File.Exists(path)) return;
        var deletedDir = Path.Combine(BackupsDir, "deleted");
        Directory.CreateDirectory(deletedDir);
        File.Move(path, Path.Combine(deletedDir, $"{DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture)}-{name}"), overwrite: true);
    }

    // ───────────── Chat ─────────────

    /// <summary>The chat channels; a file that can't be read is reported in <see cref="LoadErrors"/> and left alone.</summary>
    public List<ChatChannel> LoadChannels()
    {
        var channels = new List<ChatChannel>();
        if (!Directory.Exists(ChatDir)) return channels;
        foreach (var file in Directory.EnumerateFiles(ChatDir, "*.json"))
        {
            try
            {
                if (Json.Deserialize<ChatChannel>(File.ReadAllText(file)) is { } channel) channels.Add(channel);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _loadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return channels.OrderBy(c => c.CreatedAt).ToList();
    }

    public void SaveChannel(ChatChannel channel)
    {
        Directory.CreateDirectory(ChatDir);
        Json.WriteFile(Path.Combine(ChatDir, $"{channel.Id}.json"), channel);
    }

    /// <summary>Moves the channel's file into backups/deleted instead of erasing it.</summary>
    public void DeleteChannel(ChatChannel channel)
    {
        var path = Path.Combine(ChatDir, $"{channel.Id}.json");
        if (!File.Exists(path)) return;
        var deletedDir = Path.Combine(BackupsDir, "deleted");
        Directory.CreateDirectory(deletedDir);
        File.Move(path, Path.Combine(deletedDir, $"{DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture)}-chat-{channel.Name}-{channel.Id}.json"), overwrite: true);
    }

    // ───────────── Wiki ─────────────

    /// <summary>The wiki's pages; a file that can't be read is reported in <see cref="LoadErrors"/> and left alone.</summary>
    public List<WikiPage> LoadWikiPages()
    {
        var pages = new List<WikiPage>();
        if (!Directory.Exists(WikiDir)) return pages;
        foreach (var file in Directory.EnumerateFiles(WikiDir, "*.json"))
        {
            try
            {
                if (Json.Deserialize<WikiPage>(File.ReadAllText(file)) is { } page) pages.Add(page);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _loadErrors.Add($"wiki/{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return pages.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void SaveWikiPage(WikiPage page)
    {
        Directory.CreateDirectory(WikiDir);
        Json.WriteFile(Path.Combine(WikiDir, $"{page.Id}.json"), page);
    }

    /// <summary>Moves the page's file into backups/deleted instead of erasing it.</summary>
    public void DeleteWikiPage(WikiPage page)
    {
        var path = Path.Combine(WikiDir, $"{page.Id}.json");
        if (!File.Exists(path)) return;
        var deletedDir = Path.Combine(BackupsDir, "deleted");
        Directory.CreateDirectory(deletedDir);
        File.Move(path, Path.Combine(deletedDir, $"{DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture)}-wiki-{SafeFileName(page.Title)}-{page.Id}.json"), overwrite: true);
    }

    public string Backup(DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString(BackupStampFormat, CultureInfo.InvariantCulture);
        var dir = Path.Combine(BackupsDir, stamp);
        for (var i = 2; Directory.Exists(dir); i++) dir = Path.Combine(BackupsDir, $"{stamp}-{i}");
        var projectsBackup = Path.Combine(dir, "projects");
        Directory.CreateDirectory(projectsBackup);
        if (Directory.Exists(ProjectsDir))
            foreach (var file in Directory.EnumerateFiles(ProjectsDir, "*.json"))
                File.Copy(file, Path.Combine(projectsBackup, Path.GetFileName(file)));
        if (File.Exists(WorkspacePath))
            File.Copy(WorkspacePath, Path.Combine(dir, "workspace.json"));
        if (Directory.Exists(ChatDir))
        {
            var chatBackup = Path.Combine(dir, "chat");
            Directory.CreateDirectory(chatBackup);
            foreach (var file in Directory.EnumerateFiles(ChatDir, "*.json"))
                File.Copy(file, Path.Combine(chatBackup, Path.GetFileName(file)));
        }
        if (Directory.Exists(WikiDir))
        {
            var wikiBackup = Path.Combine(dir, "wiki");
            Directory.CreateDirectory(wikiBackup);
            foreach (var file in Directory.EnumerateFiles(WikiDir, "*.json"))
                File.Copy(file, Path.Combine(wikiBackup, Path.GetFileName(file)));
        }
        Prune(now ?? DateTime.Now);
        return dir;
    }

    /// <summary>Keeps the newest <paramref name="keepLatest"/> backups plus the newest one of each day for <paramref name="keepDays"/> days.</summary>
    public void Prune(DateTime now, int keepLatest = 20, int keepDays = 30)
    {
        if (!Directory.Exists(BackupsDir)) return;
        var backups = Directory.GetDirectories(BackupsDir)
            .Select(d => (Path: d, Stamp: ParseStamp(Path.GetFileName(d))))
            .Where(b => b.Stamp != null)
            .OrderByDescending(b => b.Stamp)
            .ThenByDescending(b => b.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keep = new HashSet<string>(backups.Take(keepLatest).Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var day in backups.Where(b => b.Stamp!.Value >= now.Date.AddDays(-keepDays)).GroupBy(b => b.Stamp!.Value.Date))
            keep.Add(day.First().Path);

        foreach (var backup in backups.Where(b => !keep.Contains(b.Path)))
        {
            try { Directory.Delete(backup.Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Where ProJict keeps its copy of an attached file: attachments/&lt;id&gt;/&lt;original file name&gt;.</summary>
    /// <summary>Where private conversations' files are kept (outside the data folder, cleaned out after some days); set by the app.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>Where an attachment's file is: the data folder, or the cache (by a safe name there, as it may come from another ProJict).</summary>
    public string AttachmentPath(Attachment attachment) => attachment.Cached && CacheRoot != null
        ? Path.Combine(CacheRoot, attachment.Id.ToString(), SafeFileName(attachment.FileName))
        : Path.Combine(AttachmentsDir, attachment.Id.ToString(), attachment.FileName);

    /// <summary>
    /// Moves a stored file into the cache for private conversations' files, in a folder of its own dated now (which
    /// cleaning out goes by). It stays where it is if it can't be moved.
    /// </summary>
    public void MoveToCache(Attachment attachment)
    {
        if (attachment.Cached || CacheRoot == null) return;
        var from = AttachmentPath(attachment);
        if (!File.Exists(from)) return;
        var to = Path.Combine(CacheRoot, attachment.Id.ToString(), SafeFileName(attachment.FileName));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
            attachment.Cached = true;
            Directory.Delete(Path.GetDirectoryName(from)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Copies a file into the data folder; the original is only read.</summary>
    public Attachment ImportAttachment(string sourcePath) => ImportAttachment(sourcePath, cached: false);

    /// <summary>Copies a file into the data folder (or, <paramref name="cached"/>, the cache for private conversations' files); the original is only read.</summary>
    public Attachment ImportAttachment(string sourcePath, bool cached)
    {
        var attachment = new Attachment { FileName = SafeFileName(Path.GetFileName(sourcePath)), Cached = cached && CacheRoot != null };
        var target = AttachmentPath(attachment);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target);
        attachment.Size = new FileInfo(target).Length;
        return attachment;
    }

    /// <summary>Stores a file a guest sent for an attachment it made (its id and name come from the guest).</summary>
    public void StoreAttachment(Attachment attachment, byte[] content)
    {
        var target = AttachmentPath(attachment);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
    }

    /// <summary>Stores new content (e.g. a pasted screenshot) as an attachment.</summary>
    public Attachment WriteAttachment(string fileName, byte[] content) => WriteAttachment(fileName, content, cached: false);

    /// <summary>Stores new content as an attachment (in the cache for private conversations' files, <paramref name="cached"/>).</summary>
    public Attachment WriteAttachment(string fileName, byte[] content, bool cached)
    {
        var attachment = new Attachment { FileName = SafeFileName(fileName), Size = content.Length, Cached = cached && CacheRoot != null };
        var target = AttachmentPath(attachment);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
        return attachment;
    }

    /// <summary>
    /// Deletes stored attachments that no loaded project, project file or backup (deleted projects included) refers to
    /// any more, so restoring a backup still finds its files. Folders created after <paramref name="createdBefore"/> are left alone.
    /// </summary>
    public int RemoveUnusedAttachments(IReadOnlySet<Guid> inUse, DateTime createdBefore)
    {
        if (!Directory.Exists(AttachmentsDir)) return 0;
        var unused = new List<(string Dir, string Id)>();
        foreach (var dir in Directory.EnumerateDirectories(AttachmentsDir))
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && !inUse.Contains(id) && Directory.GetCreationTime(dir) < createdBefore)
                unused.Add((dir, id.ToString()));
        if (unused.Count == 0) return 0;

        var files = new[] { ProjectsDir, ChatDir, WikiDir, BackupsDir }.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.json", SearchOption.AllDirectories));
        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            unused.RemoveAll(u => json.Contains(u.Id, StringComparison.OrdinalIgnoreCase));
            if (unused.Count == 0) return 0;
        }

        var removed = 0;
        foreach (var (dir, _) in unused)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "file" : safe;
    }

    private static DateTime? ParseStamp(string name)
    {
        if (name.Length < BackupStampFormat.Length) return null;
        return DateTime.TryParseExact(name[..BackupStampFormat.Length], BackupStampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) ? stamp : null;
    }

    private string UniqueFileName(Project project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(project.Key.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (stem.Length == 0) stem = "project";
        var candidate = stem + ".json";
        for (var i = 2; _fileNames.Any(kv => kv.Key != project.Id &&
                 string.Equals(kv.Value, candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{stem}-{i}.json";
        return candidate;
    }
}
