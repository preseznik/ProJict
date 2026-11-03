using System.Diagnostics;

namespace ProJict.Core.Tools;

/// <summary>
/// Whether a program on this computer is where a tool says (Settings › Tools › Check): the program itself, with its
/// version when its file has one, or the folder it's in. It's never started, so nothing opens.
/// </summary>
public static class ProgramCheck
{
    /// <summary>Found or not, and in a line for people: "Found ffmpeg.exe, version 7.1." or "Not found: …".</summary>
    public static (bool Found, string Text) Of(string? path)
    {
        path = Clean(path);
        if (path == null) return (false, "No path yet: Browse… to the program (or its folder), or paste where it is.");
        try
        {
            if (File.Exists(path))
            {
                var version = FileVersionInfo.GetVersionInfo(path) is { } info && (info.ProductVersion ?? info.FileVersion) is { Length: > 0 } v ? v.Trim() : null;
                return (true, $"Found {Path.GetFileName(path)}{(version != null ? $", version {version}" : "")}.");
            }
            if (Directory.Exists(path))
            {
                var programs = Directory.EnumerateFiles(path, "*.exe").Select(Path.GetFileName).Take(3).ToList();
                return (true, $"Found the folder {Path.GetFileName(path.TrimEnd('\\', '/'))}" +
                              (programs.Count > 0 ? $", with {string.Join(", ", programs)}." : "."));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return (false, $"Not found: {path}");
    }

    /// <summary>A path as people paste it (in quotes, with spaces around) made plain; null when there's none.</summary>
    public static string? Clean(string? path)
    {
        path = path?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>Where Windows would find a program by its name (in a folder of PATH), or null.</summary>
    public static string? FindOnPath(string program, string? pathVariable = null)
    {
        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), program);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
