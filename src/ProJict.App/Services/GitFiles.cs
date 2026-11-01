using System.Diagnostics;
using System.IO;
using ProJict.Core.Features;

namespace ProJict.App.Services;

/// <summary>
/// What git says about a repo folder's files, for a clean-up plan: which it tracks and which have uncommitted changes
/// (staged or not). Null when git isn't there or the folder isn't in a repository.
/// </summary>
public static class GitFiles
{
    public static CleanupFlow.GitState? For(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var tracked = Run(folder, "ls-files", "-z");
        if (tracked == null) return null;
        var changed = (Run(folder, "ls-files", "-z", "-m") ?? []).Concat(Run(folder, "diff", "--cached", "--name-only", "--relative", "-z") ?? []).ToList();
        var trackedSet = tracked.Select(Normal).ToList();
        var changedSet = changed.Select(Normal).ToList();
        return (path, isFolder) =>
        {
            var p = Normal(path);
            bool Hits(List<string> set) => set.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase) ||
                                                       (isFolder && f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            return (Hits(trackedSet), Hits(changedSet));
        };
    }

    private static string Normal(string path) => path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>The NUL-separated paths git prints (relative to the folder), or null when git fails.</summary>
    private static List<string>? Run(string folder, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            using var git = Process.Start(start);
            if (git == null) return null;
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(10_000))
            {
                try { git.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return git.ExitCode == 0 ? output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
