namespace ProJict.Core.Features;

/// <summary>
/// The Chat cache (Settings › Chat): private conversations' files, and channels' big ones, kept outside the data folder
/// so they aren't mirrored or backed up. Each file has a folder of its own, named by its id. What counts is when it was
/// last used: it came, or someone opened or saved it, or another ProJict fetched it (<see cref="Touch"/>). Files nobody
/// used for the days you keep them go; past the size you allow, the least recently used go first, but never one used in
/// the last two days. A file you keep (<see cref="Keep"/>) never goes.
/// </summary>
public static class PrivateFiles
{
    public const int DefaultDays = 14;
    public const int MaxDays = 3650;
    /// <summary>Recently used files stay even when the cache is over its size.</summary>
    public static readonly TimeSpan AlwaysKeptFor = TimeSpan.FromDays(2);
    /// <summary>Beside a file you keep: it's never cleaned out.</summary>
    public const string KeepMarker = ".keep";

    /// <summary>How many files the cache holds, and how big they are together.</summary>
    public static (int Files, long Bytes) Measure(string root)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Name == KeepMarker || file.Name.EndsWith(".part", StringComparison.Ordinal)) continue;
            files++;
            bytes += file.Length;
        }
        return (files, bytes);
    }

    /// <summary>A file in the cache was used: its clock starts again.</summary>
    public static void Touch(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Directory.SetLastWriteTime(dir, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>When the file was last used (its folder's time).</summary>
    public static DateTime LastUsed(string folder) => Directory.GetLastWriteTime(folder);

    /// <summary>Whether you keep the file, so it's never cleaned out.</summary>
    public static bool IsKept(string path) => Path.GetDirectoryName(path) is { } dir && File.Exists(Path.Combine(dir, KeepMarker));

    /// <summary>Keeps the file for good, or lets it be cleaned out again.</summary>
    public static void Keep(string path, bool keep)
    {
        if (Path.GetDirectoryName(path) is not { } dir || !Directory.Exists(dir)) return;
        var marker = Path.Combine(dir, KeepMarker);
        if (keep) File.WriteAllText(marker, "");
        else File.Delete(marker);
    }

    /// <summary>
    /// Deletes the files nobody used for <paramref name="days"/> (every one with null, as Clean out now; none with 0 or
    /// less, which keeps them), then, while the cache is over <paramref name="maxBytes"/>, the least recently used of
    /// the rest (not ones used in the last two days). Kept files stay. Returns how many went and their size; one in use
    /// stays for next time.
    /// </summary>
    public static (int Files, long Bytes) CleanOut(string root, int? days, DateTime now, long? maxBytes = null)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var folders = Directory.EnumerateDirectories(root)
            .Where(d => !File.Exists(Path.Combine(d, KeepMarker)))
            .Select(d => (Dir: d, Used: LastUsed(d), Size: Measure(d)))
            .OrderBy(f => f.Used).ToList();
        var files = 0;
        long bytes = 0;
        bool Delete((string Dir, DateTime Used, (int Files, long Bytes) Size) folder)
        {
            try
            {
                Directory.Delete(folder.Dir, recursive: true);
                files += folder.Size.Files;
                bytes += folder.Size.Bytes;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        var left = new List<(string Dir, DateTime Used, (int Files, long Bytes) Size)>();
        foreach (var folder in folders)
        {
            var old = days == null || (days > 0 && folder.Used < now.AddDays(-days.Value));
            if (!(old && Delete(folder))) left.Add(folder);
        }
        if (maxBytes is > 0 and var cap)
        {
            var total = Measure(root).Bytes;
            foreach (var folder in left)
            {
                if (total <= cap) break;
                if (folder.Used > now - AlwaysKeptFor) break;
                if (Delete(folder)) total -= folder.Size.Bytes;
            }
        }
        return (files, bytes);
    }

    /// <summary>Moves what the cache holds to another folder (when you choose a new one), keeping when each was last used.</summary>
    public static void MoveAll(string from, string to)
    {
        if (!Directory.Exists(from) || string.Equals(Path.GetFullPath(from).TrimEnd('\\', '/'), Path.GetFullPath(to).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var dir in Directory.EnumerateDirectories(from).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(dir));
            try
            {
                var used = LastUsed(dir);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(dir)) File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                Directory.SetLastWriteTime(target, used);
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
