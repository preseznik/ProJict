using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// Items' linked media files: big captures, edits and exports stay where they are (nothing is copied), a file in the
/// project's folder is kept relative to it, and Clean up leaves what's linked alone. Breaking a rule throws
/// InvalidOperationException with the reason.
/// </summary>
public static class MediaLinks
{
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aif", ".aiff" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic" };

    public static MediaKind KindOf(string path) => Path.GetExtension(path) switch
    {
        var e when Videos.Contains(e) => MediaKind.Video,
        var e when Audio.Contains(e) => MediaKind.Audio,
        var e when Images.Contains(e) => MediaKind.Image,
        _ => MediaKind.Other,
    };

    /// <summary>Video and audio: linked rather than copied when you drop them on an item.</summary>
    public static bool IsBig(string path) => KindOf(path) is MediaKind.Video or MediaKind.Audio;

    /// <summary>Where the linked file is on this computer.</summary>
    public static string FullPath(Project project, MediaLink link) =>
        Path.IsPathRooted(link.Path) || project.RepoPath is not { } repo ? link.Path : Path.GetFullPath(Path.Combine(repo, link.Path));

    /// <summary>How a file is kept: relative to the project's folder when it's in it, else as it is.</summary>
    public static string Stored(Project project, string fullPath)
    {
        if (project.RepoPath is not { Length: > 0 } repo) return fullPath;
        var folder = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(folder, fullPath) : fullPath;
    }

    /// <summary>Links files (full paths) to the item where they are; each once. Not folders, nothing that isn't there.</summary>
    public static List<MediaLink> Link(Project project, TodoItem item, IEnumerable<string> paths, string by, bool agent)
    {
        var added = new List<MediaLink>();
        var problems = new List<string>();
        foreach (var raw in paths)
        {
            var full = Path.GetFullPath(raw.Trim().Trim('"'));
            if (Directory.Exists(full))
            {
                problems.Add($"{raw}: a folder (link the files in it)");
                continue;
            }
            if (!File.Exists(full))
            {
                problems.Add($"{raw}: not there");
                continue;
            }
            var stored = Stored(project, full);
            if ((item.Media ?? []).Concat(added).Any(m => string.Equals(FullPath(project, m), full, StringComparison.OrdinalIgnoreCase))) continue;
            added.Add(new MediaLink { Path = stored, Size = new FileInfo(full).Length, AddedBy = by, Agent = agent });
        }
        if (problems.Count > 0) throw new InvalidOperationException("Couldn't link: " + string.Join("; ", problems) + ".");
        if (added.Count > 0) (item.Media ??= []).AddRange(added);
        return added;
    }

    /// <summary>Takes a link off the item (the file stays); a path, full or as stored, or just the file name.</summary>
    public static MediaLink Unlink(Project project, TodoItem item, string path)
    {
        var wanted = path.Trim().Trim('"');
        var link = (item.Media ?? []).FirstOrDefault(m => string.Equals(m.Path, wanted, StringComparison.OrdinalIgnoreCase) ||
                                                         string.Equals(FullPath(project, m), wanted, StringComparison.OrdinalIgnoreCase))
                   ?? (item.Media ?? []).Where(m => string.Equals(Path.GetFileName(m.Path), wanted, StringComparison.OrdinalIgnoreCase)).ToList() switch
                   {
                       [var only] => only,
                       [] => null,
                       _ => throw new InvalidOperationException($"More than one linked file is called {wanted}: give its path."),
                   }
                   ?? throw new InvalidOperationException($"No linked file {wanted} on that item.");
        item.Media!.Remove(link);
        if (item.Media.Count == 0) item.Media = null;
        return link;
    }

    /// <summary>The project's items that link this file, or files in this folder (full paths).</summary>
    public static IEnumerable<(TodoItem Item, MediaLink Link)> LinkedIn(Project project, string fullPath)
    {
        var path = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var item in project.Walk().Select(l => l.Item))
            foreach (var link in item.Media ?? [])
            {
                var linked = FullPath(project, link);
                if (string.Equals(linked, path, StringComparison.OrdinalIgnoreCase) ||
                    linked.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    yield return (item, link);
            }
    }
}
