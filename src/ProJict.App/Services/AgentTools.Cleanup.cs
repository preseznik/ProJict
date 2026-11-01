using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Clean-ups of a project's folder, for agents: you ask (Clean up… on the project), an agent reads the request, scans the
/// repo folder and posts the plan (cleanup_request, plan_cleanup); you tick and approve, and ProJict moves exactly those
/// to the Recycle Bin. Agents never remove files themselves, and do no other work in the project until it's decided.
/// </summary>
public sealed partial class AgentTools
{
    private void AddCleanupTools()
    {
        Add("cleanup_request", "Clean-up request",
            "The project's clean-up request, if the user made one: they want its repo folder cleaned up (old test builds and labs, duplicate copies, " +
            "build leftovers, logs and temp files). It has the folder, the current test build (never part of it), the plan if there's one, and what to do next.",
            Schema(ProjectArg), CleanupRequestInfo, readOnly: true);
        Add("plan_cleanup", "Plan a clean-up",
            "Posts your clean-up plan for the user: one line per file or folder in the repo folder, as \"safe | path | why\" or \"thorough | path | why\" " +
            "(paths relative to the repo folder; list a folder rather than every file in it). Safe: what you're sure about (build leftovers, temp files, " +
            "duplicates whose identical copy stays). Thorough: also old builds, labs and doubtful things. Never the current test build; files git tracks " +
            "only as thorough, and none with uncommitted changes. The user ticks and approves, and ProJict moves them to the Recycle Bin: never remove " +
            "files yourself. Posting again replaces the plan.",
            Schema(ProjectArg, ("entries", "string[]", "The lines: level | path | why.", true)), PlanCleanup);
    }

    private string CleanupRequestInfo(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var request = project.OpenCleanup;
        var json = new JsonObject
        {
            ["project"] = project.Name,
            ["repoFolder"] = project.RepoPath,
            ["testBuild"] = project.TestBuild?.Path,
            ["request"] = request != null ? CleanupJson(request) : null,
            ["next"] = request?.State switch
            {
                CleanupState.Requested => "Scan the repo folder (sizes, duplicates, build leftovers, old builds and labs, logs, temp files) and post your plan with plan_cleanup. " +
                                          "Do no other work in this project meanwhile, and remove nothing yourself.",
                CleanupState.Planned => "Wait for the user to approve or decline; you can post the plan again to change it. Remove nothing yourself.",
                _ => "No clean-up is open: only the user asks for one (Clean up… on the project).",
            },
        };
        return Json(json);
    }

    private static JsonObject CleanupJson(CleanupRequest request) => new()
    {
        ["state"] = CleanupFlow.StateName(request.State),
        ["requestedBy"] = request.RequestedBy,
        ["requestedAt"] = Time(request.RequestedAt),
        ["plan"] = request.Entries is { } entries
            ? new JsonArray(entries.Select(e => (JsonNode)$"{e.Level.ToString().ToLowerInvariant()} | {e.Path} | {e.Reason} ({CleanupFlow.Size(e.Size)}{(e.Tracked ? ", tracked" : "")})").ToArray())
            : null,
        ["plannedBy"] = request.PlannedBy,
        ["log"] = new JsonArray(request.Log.Select(l => (JsonNode)$"{l.At:yyyy-MM-dd HH:mm} {l.By}: {l.Text}").ToArray()),
    };

    private string PlanCleanup(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        var request = project.OpenCleanup ?? throw new McpToolException($"No clean-up of {project.Name} is open: only the user asks for one.", ToolProblem.NotFound);
        var lines = args.Strings("entries").ToList();
        var git = GitFiles.For(project.RepoPath);
        try
        {
            _main.GetProjectViewModel(project).ChangeCleanup(() => CleanupFlow.Plan(project, request, lines, session.AgentName, git));
        }
        catch (InvalidOperationException ex)
        {
            throw new McpToolException(ex.Message, ToolProblem.Conflict);
        }
        _state.StatusText = $"{session.AgentName} posted a clean-up plan for {project.Name}: {CleanupFlow.Summary(request)}.";
        return Json(new JsonObject
        {
            ["state"] = CleanupFlow.StateName(request.State),
            ["plan"] = CleanupFlow.Summary(request),
            ["next"] = "Wait for the user's decision; ProJict moves what they approve to the Recycle Bin and tells you. Remove nothing yourself." +
                       (request.Entries!.Any(e => e.Tracked) ? " Lines git tracks were put in the thorough list." : ""),
        });
    }

    /// <summary>A clean-up is open: until it's decided, agents only read, comment and work on it there.</summary>
    private static void CheckNoCleanup(Project project)
    {
        if (project.OpenCleanup is { } cleanup)
            throw new McpToolException($"A clean-up of {project.Name}'s folder is open ({CleanupFlow.StateName(cleanup.State)}): no other work there until it's decided. " +
                                       "Read it with cleanup_request; you can still read and comment.", ToolProblem.Conflict);
    }

    /// <summary>For waitingForYou and the watcher: a clean-up asked for, and the user's decision on your plan.</summary>
    private static IEnumerable<(string Key, string Line)> CleanupLines(McpSession session, Project project)
    {
        if (project.OpenCleanup is { State: CleanupState.Requested } asked)
            yield return ($"cleanup:{asked.Id}:requested", $"{asked.RequestedBy} wants {project.Name}'s folder cleaned up: read cleanup_request project={project.Name}, " +
                                                           "then post your plan with plan_cleanup. No other work in that project until it's decided.");
        if (project.Cleanups?.LastOrDefault() is { PlannedBy: { } planner } last && Core.Agents.AgentNaming.IsOwnWork(session, planner))
        {
            if (last.State == CleanupState.Done)
                yield return ($"cleanup:{last.Id}:done", $"{last.DecidedBy} approved your clean-up plan for {project.Name}: ProJict moved {last.Moved} path(s) " +
                                                         $"({CleanupFlow.Size(last.Freed)}) to the Recycle Bin. The project is open for work again.");
            else if (last.State == CleanupState.Declined)
                yield return ($"cleanup:{last.Id}:declined", $"{last.DecidedBy} declined your clean-up plan for {project.Name}: nothing was removed. The project is open for work again.");
        }
    }
}
