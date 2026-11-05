namespace ProJict.Core.Tools;

public static partial class BrowserRules
{
    /// <summary>
    /// Why an agent may not put this file into a page in ProJict's browser (browser_upload, #ae854b1c), or null when it
    /// may: the file must exist and lie inside one of <paramref name="roots"/> (its working folder, your projects' folders,
    /// ProJict's attachments and Chat files), not reached through a link, so a page never gets anything else from this computer.
    /// </summary>
    public static string? UploadProblem(string path, IEnumerable<string?> roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return $"“{path}” isn't a file's path."; }
        var file = new FileInfo(full);
        if (!file.Exists) return Directory.Exists(full) ? $"{full} is a folder: give the files in it." : $"There's no file {full}.";
        if (file.LinkTarget != null) return $"{full} is a link to another file: give the file itself.";
        var allowed = roots.OfType<string>().Where(r => r.Trim().Length > 0)
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar);
        return allowed.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ? null
            : $"{full} isn't in your project's folder or ProJict's attachments: agents upload only files from there.";
    }
}
