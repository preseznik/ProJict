using System.Globalization;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// The rules of a clean-up of a project's folder: you ask, an agent posts a plan of paths inside the repo folder (never
/// the current test build, never files with uncommitted changes, files git tracks only as Thorough), you approve Safe
/// or everything ticked, or decline. Breaking a rule throws <see cref="InvalidOperationException"/> with the reason.
/// </summary>
public static class CleanupFlow
{
    private const int Kept = 10;
    public const int MaxEntries = 500;

    /// <summary>What git says about a path relative to the repo folder: tracked (or holding tracked files), uncommitted changes.</summary>
    public delegate (bool Tracked, bool Changed) GitState(string relativePath, bool folder);

    public static CleanupRequest Request(Project project, string by)
    {
        if (project.IsInbox) throw new InvalidOperationException("The Inbox has no folder to clean up.");
        if (string.IsNullOrWhiteSpace(project.RepoPath) || !Directory.Exists(project.RepoPath))
            throw new InvalidOperationException($"Set {project.Name}'s local repo folder in Details first: that's the folder that gets cleaned up.");
        if (project.OpenCleanup != null) throw new InvalidOperationException($"A clean-up of {project.Name} is open already.");
        var request = new CleanupRequest { State = CleanupState.Requested, RequestedBy = by };
        Log(request, by, false, "Asked to clean up the project's folder.");
        var all = project.Cleanups ??= [];
        all.Add(request);
        while (all.Count > Kept && all.FirstOrDefault(c => !c.IsOpen) is { } old) all.Remove(old);
        return request;
    }

    /// <summary>
    /// The agent's plan: lines "safe | path | why" or "thorough | path | why", paths relative to the repo folder. Checks
    /// each against the folder (inside it, there, not the test build, no uncommitted changes) and replaces any earlier plan.
    /// </summary>
    public static void Plan(Project project, CleanupRequest request, IEnumerable<string> lines, string agent, GitState? git = null)
    {
        if (request.State is not (CleanupState.Requested or CleanupState.Planned)) throw new InvalidOperationException("That clean-up isn't waiting for a plan.");
        var repo = RepoFolder(project);
        var protectedPath = project.TestBuild?.FullPath(project.RepoPath) is { } tb ? Path.GetFullPath(tb) : null;
        var entries = new List<CleanupEntry>();
        var problems = new List<string>();
        foreach (var raw in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var parts = raw.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !Enum.TryParse<CleanupLevel>(parts[0], true, out var level) || parts[2].Length == 0)
            {
                problems.Add($"\"{raw}\": write it as level | path | why (level safe or thorough)");
                continue;
            }
            var relative = parts[1].Trim('"').Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(repo, relative));
            if (!full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{parts[1]}: outside the repo folder");
                continue;
            }
            var folder = Directory.Exists(full);
            if (!folder && !File.Exists(full))
            {
                problems.Add($"{parts[1]}: not there");
                continue;
            }
            if (protectedPath != null && (string.Equals(full, protectedPath, StringComparison.OrdinalIgnoreCase) ||
                                          protectedPath.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{parts[1]}: that's (or holds) the current test build");
                continue;
            }
            if (MediaLinks.LinkedIn(project, full).FirstOrDefault() is { Item: { } linked })
            {
                var text = linked.Text.Length > 60 ? linked.Text[..60] + "…" : linked.Text;
                problems.Add($"{parts[1]}: {(folder ? "holds files" : "it's")} linked to the item “{text}” (unlink it there first)");
                continue;
            }
            relative = Path.GetRelativePath(repo, full);
            if (entries.Any(e => string.Equals(e.Path, relative, StringComparison.OrdinalIgnoreCase))) continue;
            var (tracked, changed) = git?.Invoke(relative, folder) ?? (false, false);
            if (changed)
            {
                problems.Add($"{parts[1]}: git has uncommitted changes in it");
                continue;
            }
            entries.Add(new CleanupEntry
            {
                Path = relative, Folder = folder, Size = SizeOf(full, folder), Reason = parts[2],
                // Whatever git tracks is never Safe.
                Level = tracked ? CleanupLevel.Thorough : level, Tracked = tracked,
            });
        }
        if (problems.Count > 0) throw new InvalidOperationException("Fix these lines and post the plan again: " + string.Join("; ", problems) + ".");
        if (entries.Count == 0) throw new InvalidOperationException("The plan is empty: one line per file or folder, level | path | why.");
        if (entries.Count > MaxEntries) throw new InvalidOperationException($"Keep the plan to {MaxEntries} lines: list folders rather than every file in them.");
        // Nothing listed twice inside a folder that's listed too.
        entries.RemoveAll(e => entries.Any(f => f != e && f.Folder && e.Path.StartsWith(f.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        request.Entries = [.. entries.OrderBy(e => e.Level).ThenByDescending(e => e.Size)];
        request.PlannedBy = agent;
        request.PlannedAt = DateTime.Now;
        request.State = CleanupState.Planned;
        Log(request, agent, true, $"Planned it: {Summary(request)}.");
    }

    /// <summary>What a decision would move: Safe ones, or everything ticked.</summary>
    public static List<CleanupEntry> Selected(CleanupRequest request, CleanupLevel level) =>
        (request.Entries ?? []).Where(e => e.Checked && (level == CleanupLevel.Thorough || e.Level == CleanupLevel.Safe)).ToList();

    /// <summary>You approved: from here ProJict moves the selected paths (<see cref="Finish"/> records what happened).</summary>
    public static List<CleanupEntry> Approve(CleanupRequest request, CleanupLevel level, string by)
    {
        if (request.State != CleanupState.Planned) throw new InvalidOperationException("That clean-up has no plan waiting for you.");
        var selected = Selected(request, level);
        if (selected.Count == 0) throw new InvalidOperationException("Nothing is ticked at that level.");
        request.Level = level;
        request.DecidedBy = by;
        Log(request, by, false, $"Approved {(level == CleanupLevel.Safe ? "the safe part" : "everything ticked")}: {Count(selected.Count, "path")}, {Size(selected.Sum(e => e.Size))}.");
        return selected;
    }

    /// <summary>After ProJict moved them: how many went and how much space it freed (each entry's Result says).</summary>
    public static void Finish(CleanupRequest request, IReadOnlyCollection<CleanupEntry> done)
    {
        var moved = done.Where(e => e.Result == Moved).ToList();
        request.Moved = moved.Count;
        request.Freed = moved.Sum(e => e.Size);
        request.State = CleanupState.Done;
        request.FinishedAt = DateTime.Now;
        var failed = done.Count - moved.Count;
        Log(request, request.DecidedBy ?? "", false, $"Moved {Count(moved.Count, "path")} ({Size(request.Freed)}) to the Recycle Bin" +
                                                     (failed > 0 ? $"; {failed} couldn't be moved" : "") + ".");
    }

    public const string Moved = "moved to the Recycle Bin";

    public static void Decline(CleanupRequest request, string by)
    {
        if (request.State != CleanupState.Planned) throw new InvalidOperationException("That clean-up has no plan waiting for you.");
        request.State = CleanupState.Declined;
        request.DecidedBy = by;
        request.FinishedAt = DateTime.Now;
        Log(request, by, false, "Declined the plan.");
    }

    public static void Withdraw(CleanupRequest request, string by)
    {
        if (!request.IsOpen) throw new InvalidOperationException("That clean-up is finished.");
        request.State = CleanupState.Withdrawn;
        request.FinishedAt = DateTime.Now;
        Log(request, by, false, "Withdrew the request.");
    }

    /// <summary>"12 paths, 1.4 GB (safe 3, 120 MB)".</summary>
    public static string Summary(CleanupRequest request)
    {
        var all = request.Entries ?? [];
        var safe = all.Where(e => e.Level == CleanupLevel.Safe).ToList();
        return $"{Count(all.Count, "path")}, {Size(all.Sum(e => e.Size))} (safe {safe.Count}, {Size(safe.Sum(e => e.Size))})";
    }

    public static string StateName(CleanupState state) => state switch
    {
        CleanupState.Requested => "waiting for a plan",
        CleanupState.Planned => "waiting for your approval",
        CleanupState.Done => "done",
        CleanupState.Declined => "declined",
        _ => "withdrawn",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(".0 ", " "),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string RepoFolder(Project project) =>
        project.RepoPath is { Length: > 0 } repo && Directory.Exists(repo)
            ? Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar)
            : throw new InvalidOperationException($"{project.Name} has no local repo folder to clean up.");

    private static long SizeOf(string full, bool folder)
    {
        try
        {
            return folder ? new DirectoryInfo(full).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
                : new FileInfo(full).Length;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static void Log(CleanupRequest request, string by, bool agent, string text) =>
        request.Log.Add(new ActivityEntry { By = by, Agent = agent, Text = text });
}
