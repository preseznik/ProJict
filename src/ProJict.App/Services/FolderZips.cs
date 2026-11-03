using System.IO;
using System.IO.Compression;
using System.Windows;

namespace ProJict.App.Services;

/// <summary>
/// Folders can't be attached as they are. Dropped or pasted among files (on an item, in a discussion, in Chat or on a wiki
/// page), ProJict asks whether to zip each one, with everything in it, into a .zip of its name and attach that instead, or
/// cancel. The zips are made in the temp folder, and the ones from before yesterday are cleaned out as new ones are made.
/// </summary>
public static class FolderZips
{
    /// <summary>Asks you whether to zip them (a message box; off-screen tests answer it themselves).</summary>
    public static Func<string, bool> Ask { get; set; } = question =>
        MessageBox.Show(Application.Current.MainWindow!, question, "Attach a folder", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private static string Root => Path.Combine(Path.GetTempPath(), "ProJict-zips");

    /// <summary>
    /// True when <paramref name="paths"/> has folders and this takes over: once you said yes and they're zipped,
    /// <paramref name="attach"/> gets the files with each folder's zip in its place (never, if you cancel). False when there
    /// are no folders, for the caller to go on as usual.
    /// </summary>
    public static bool TakesFolders(IEnumerable<string> paths, Action<string[]> attach, Action<string> status)
    {
        var list = paths.ToList();
        var folders = list.Where(Directory.Exists).ToList();
        if (folders.Count == 0) return false;
        var names = folders.Select(f => $"“{NameOf(f)}”").ToList();
        var what = folders.Count == 1 ? $"the folder {names[0]}" : $"the folders {string.Join(", ", names[..^1])} and {names[^1]}";
        var one = folders.Count == 1;
        if (!Ask($"Folders can't be attached as they are. Zip {what}, with everything in {(one ? "it" : "them")}, and attach {(one ? "the zip" : "the zips")}?"))
        {
            status($"Not attached: {what} {(one ? "wasn't" : "weren't")} zipped.");
            return true;
        }
        status($"Zipping {what}…");
        _ = ZipThenAttach(list, folders, attach, status);
        return true;
    }

    private static async Task ZipThenAttach(List<string> paths, List<string> folders, Action<string[]> attach, Action<string> status)
    {
        Dictionary<string, string> zips;
        try
        {
            zips = await Task.Run(() => folders.ToDictionary(f => f, Zip));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status($"Couldn't zip it: {ex.Message}");
            return;
        }
        status(zips.Count == 1 ? $"Zipped “{NameOf(folders[0])}”: attaching the zip." : $"Zipped {zips.Count} folders: attaching the zips.");
        attach([.. paths.Select(p => zips.TryGetValue(p, out var zip) ? zip : p)]);
    }

    /// <summary>"Renders" for C:\GoRacer\Renders\ (a drive's root: its letter).</summary>
    private static string NameOf(string folder) =>
        Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name
        : folder.TrimEnd(':', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } drive ? drive : "folder";

    /// <summary>Zips the folder (itself, with everything in it) into Renders.zip, in a folder of its own in the temp folder.</summary>
    private static string Zip(string folder)
    {
        CleanUp();
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, NameOf(folder) + ".zip");
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zip;
    }

    /// <summary>Zips from before yesterday go: what they were attached to has its own copy.</summary>
    private static void CleanUp()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
                if (Directory.GetCreationTime(dir) < DateTime.Now.AddDays(-1)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
