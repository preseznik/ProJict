namespace ProJict.Core.Features;

/// <summary>
/// The cache for private conversations' files (Settings › Chat): outside the data folder, so they aren't mirrored or
/// backed up, and cleaned out once they're older than the days you keep them. Each file has a folder of its own, named
/// by its id and dated when it came, which is what cleaning out goes by.
/// </summary>
public static class PrivateFiles
{
    public const int DefaultDays = 14;
    public const int MaxDays = 3650;

    /// <summary>How many files the cache holds, and how big they are together.</summary>
    public static (int Files, long Bytes) Measure(string root)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files++;
            bytes += file.Length;
        }
        return (files, bytes);
    }

    /// <summary>
    /// Deletes the files that came more than <paramref name="days"/> ago (every one, with null; none with 0 or less,
    /// which keeps them). Returns how many went and their size; one in use stays for next time.
    /// </summary>
    public static (int Files, long Bytes) CleanOut(string root, int? days, DateTime now)
    {
        if (!Directory.Exists(root) || days <= 0) return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            if (days != null && Directory.GetCreationTime(dir) > now.AddDays(-days.Value)) continue;
            var (count, size) = Measure(dir);
            try
            {
                Directory.Delete(dir, recursive: true);
                files += count;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (files, bytes);
    }

    /// <summary>Moves what the cache holds to another folder (when you choose a new one), keeping each file's date.</summary>
    public static void MoveAll(string from, string to)
    {
        if (!Directory.Exists(from) || string.Equals(Path.GetFullPath(from).TrimEnd('\\', '/'), Path.GetFullPath(to).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var dir in Directory.EnumerateDirectories(from).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(dir));
            try
            {
                var came = Directory.GetCreationTime(dir);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(dir)) File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                Directory.SetCreationTime(target, came);
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
