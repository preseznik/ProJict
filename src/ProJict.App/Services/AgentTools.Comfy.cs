using System.IO;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

public enum ComfyRunState { Queued, Running, Done, Failed }

/// <summary>A workflow an agent ran: where, for which item, how it went and what it made.</summary>
public sealed class ComfyRun
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Agent { get; init; }
    public required string Tool { get; init; }
    public required string Workflow { get; init; }
    public required string Endpoint { get; init; }
    public Project? Project { get; init; }
    public Guid? ItemId { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    public ComfyRunState State { get; set; }
    /// <summary>The files it made: their names on the item, or their paths when it ran for no item.</summary>
    public List<string> Files { get; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// ComfyUI for agents: a tool's workflows (list_tools, get_workflow) and running one (run_workflow): ProJict uploads any
/// picture it takes, queues it on the endpoint, waits, and attaches what it made to the item the agent names, noting the
/// run in its history. A run goes on in the background; the agent hears when it's done (waitingForYou, workflow_run).
/// </summary>
public sealed partial class AgentTools
{
    private readonly List<ComfyRun> _comfyRuns = [];
    private const int ComfyRunsKept = 100;
    internal ComfyClient Comfy { get; } = new();

    private void AddComfyTools()
    {
        Add("get_workflow", "Get a ComfyUI workflow",
            "One ComfyUI workflow from the library (list_tools shows them): what it makes, the inputs to fill in (prompt, seed, size, picture…), the endpoints " +
            "it runs on with their addresses, and its graph in ComfyUI's API format, for calling ComfyUI yourself (POST <address>/prompt). Usually run_workflow is simpler.",
            Schema(("workflow", "string", "The workflow's name (or id).", true), ("tool", "string", "The ComfyUI tool it's in, when several have one of that name.", false)),
            GetWorkflow, readOnly: true);
        Add("run_workflow", "Run a ComfyUI workflow",
            "Runs a workflow from the library on one of its tool's ComfyUI endpoints: fill its inputs (key=value; a picture input takes a file path, a seed " +
            "\"random\"), and ProJict uploads the picture, queues it, waits and attaches the pictures or videos it made to the item you name (with a line in its " +
            "history), or keeps them in a folder and gives you the paths. It runs in the background: you hear when it's done (waitingForYou), or ask with workflow_run.",
            Schema(("workflow", "string", "The workflow's name (or id).", true),
                ("inputs", "string[]", "Inputs as key=value, e.g. prompt=a red car at dusk, seed=random, width=1024 (get_workflow lists them).", false),
                ("item", "string", "The item to attach what it makes to (8-character id).", false),
                ("endpoint", "string", "Which endpoint, by name; defaults to the first the workflow runs on.", false),
                ("tool", "string", "The ComfyUI tool, when several have a workflow of that name.", false)),
            RunWorkflow);
        Add("workflow_run", "Check a workflow run",
            "How a run_workflow you started is going: queued, running, done (with the files it made) or failed (with why).",
            Schema(("run", "string", "The run's id (run_workflow gave it).", true)),
            WorkflowRun, readOnly: true);
    }

    private WorkflowLibrary Library => _main.Workflows;

    private JsonObject ComfyJson(ExternalTool tool) => new()
    {
        ["endpoints"] = new JsonArray((tool.Endpoints ?? []).Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["address"] = e.Address }).ToArray()),
        ["workflows"] = new JsonArray(Library.Of(tool.Name).Select(w => (JsonNode)new JsonObject
        {
            ["workflow"] = w.Name,
            ["purpose"] = w.Purpose.Length > 0 ? w.Purpose : null,
            ["inputs"] = new JsonArray(w.Inputs.Select(i => (JsonNode)i.Key).ToArray()),
            ["endpoints"] = w.Endpoints.Count > 0 ? new JsonArray(w.Endpoints.Select(e => (JsonNode)e).ToArray()) : null,
        }).ToArray()),
        ["runs"] = tool.RunsOff ? "off: the user lets agents read these workflows, not run them" : null,
    };

    /// <summary>The workflow an agent names, and its tool; throws saying which there are when it can't tell.</summary>
    private (ComfyWorkflow Workflow, ExternalTool Tool) WorkflowFor(Args args)
    {
        var key = args.Required("workflow");
        var toolName = args.String("tool")?.Trim();
        var comfy = ToolBox.Tools.Where(t => t.Access == ToolAccess.ComfyUI && (toolName == null || string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase))).ToList();
        var matches = comfy.Select(t => (Workflow: Library.Find(key, t.Name), Tool: t)).Where(m => m.Workflow != null).ToList();
        return matches.Count switch
        {
            1 => (matches[0].Workflow!, matches[0].Tool),
            0 => throw new McpToolException($"No workflow “{key}”. " + (comfy.SelectMany(t => Library.Of(t.Name)).Select(w => w.Name).ToList() is { Count: > 0 } names
                ? $"Workflows: {string.Join(", ", names)}." : "The user hasn't added any (Settings › Tools, a ComfyUI tool)."), ToolProblem.NotFound),
            _ => throw new McpToolException($"Several ComfyUI tools have a workflow “{key}”: {string.Join(", ", matches.Select(m => m.Tool.Name))}. Say which with tool."),
        };
    }

    private string GetWorkflow(McpSession session, Args args, List<Attachment> files)
    {
        var (workflow, tool) = WorkflowFor(args);
        return Json(new JsonObject
        {
            ["workflow"] = workflow.Name,
            ["tool"] = tool.Name,
            ["purpose"] = workflow.Purpose.Length > 0 ? workflow.Purpose : null,
            ["inputs"] = new JsonArray(workflow.Inputs.Select(i => (JsonNode)i.Describe()).ToArray()),
            ["endpoints"] = new JsonArray((tool.Endpoints ?? []).Where(workflow.RunsOn).Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["address"] = e.Address }).ToArray()),
            ["graph"] = workflow.Graph.DeepClone(),
            ["note"] = tool.RunsOff ? "The user lets agents read this workflow, not run it." : "run_workflow runs it for you; or POST {\"prompt\": graph} to <address>/prompt yourself.",
        });
    }

    private string RunWorkflow(McpSession session, Args args, List<Attachment> files)
    {
        var (workflow, tool) = WorkflowFor(args);
        if (tool.RunsOff) throw new McpToolException($"The user lets agents read {tool.Name}'s workflows, not run them (Settings › Tools).", ToolProblem.Forbidden);
        var endpointName = args.String("endpoint");
        var endpoint = (endpointName != null ? tool.Endpoint(endpointName) : tool.Endpoints?.FirstOrDefault(workflow.RunsOn))
                       ?? throw new McpToolException(endpointName != null
                           ? $"{tool.Name} has no endpoint “{endpointName}”. Its endpoints: {string.Join(", ", (tool.Endpoints ?? []).Select(e => e.Name))}."
                           : $"{tool.Name} has no endpoint this workflow runs on: ask the user to add one (Settings › Tools).", ToolProblem.NotFound);
        if (!workflow.RunsOn(endpoint)) throw new McpToolException($"“{workflow.Name}” runs on {string.Join(", ", workflow.Endpoints)}, not {endpoint.Name}.");

        // key=value, checked now (a picture by its file, which is uploaded when the run starts).
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in args.Strings("inputs"))
        {
            var at = pair.IndexOf('=');
            if (at <= 0) throw new McpToolException($"Inputs go as key=value; “{pair}” isn't one.");
            values[pair[..at].Trim()] = pair[(at + 1)..].Trim();
        }
        var pictures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in workflow.Inputs.Where(i => i.Kind == WorkflowInputKind.Image && values.ContainsKey(i.Key)))
        {
            var path = values[input.Key];
            var full = Path.GetFullPath(Path.IsPathRooted(path) || session.WorkingDirectory == null ? path : Path.Combine(session.WorkingDirectory, path));
            if (session.Remote || !File.Exists(full)) throw new McpToolException($"{input.Key} takes a picture file on this computer; “{path}” isn't one.");
            pictures[input.Key] = full;
        }
        try { ComfyGraph.Fill(workflow.Graph, values); }
        catch (ArgumentException ex) { throw new McpToolException(ex.Message); }

        Project? project = null;
        Guid? itemId = null;
        if (args.String("item") is { } id)
        {
            // What it makes goes on the item, so the item must be one the agent may change (as with attach_file).
            var (p, loc) = FindItem(session, id);
            MayChange(p, Change.Edit);
            CheckMayWork(p, loc.Item);
            CheckOut(session, p, loc.Item, args);
            (project, itemId) = (p, loc.Item.Id);
        }
        var agent = project != null ? SignAs(session, project) : session.AgentName;
        var run = new ComfyRun { Agent = agent, Tool = tool.Name, Workflow = workflow.Name, Endpoint = endpoint.Name, Project = project, ItemId = itemId };
        _comfyRuns.Add(run);
        if (_comfyRuns.Count > ComfyRunsKept) _comfyRuns.RemoveRange(0, _comfyRuns.Count - ComfyRunsKept);
        _ = Task.Run(() => ComfyRunAsync(run, workflow, endpoint, values, pictures));
        _state.StatusText = $"{agent} is running the workflow “{workflow.Name}” on {endpoint.Name}.";
        return Json(new JsonObject
        {
            ["run"] = ShortId(run.Id),
            ["state"] = "queued",
            ["workflow"] = workflow.Name,
            ["endpoint"] = $"{endpoint.Name} ({endpoint.Address})",
            ["note"] = (itemId != null ? "What it makes is attached to the item. " : "What it makes is kept in a folder; workflow_run gives the paths. ") +
                       "You hear when it's done in waitingForYou, or ask with workflow_run.",
        });
    }

    /// <summary>The run itself, in the background: upload, queue, wait, fetch; then on the UI thread the files go on the item.</summary>
    private async Task ComfyRunAsync(ComfyRun run, ComfyWorkflow workflow, ComfyEndpoint endpoint, Dictionary<string, string> values, Dictionary<string, string> pictures)
    {
        var made = new List<(string Name, byte[] Data)>();
        try
        {
            foreach (var (key, path) in pictures) values[key] = await Comfy.UploadImageAsync(endpoint, path);
            var graph = ComfyGraph.Fill(workflow.Graph, values);
            var promptId = await Comfy.QueueAsync(endpoint, graph);
            run.State = ComfyRunState.Running;
            foreach (var output in await Comfy.WaitAsync(endpoint, promptId))
                made.Add((output.FileName, await Comfy.FetchAsync(endpoint, output)));
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
        }
        await _dispatcher.InvokeAsync(() => ComfyRunEnded(run, made));
    }

    private void ComfyRunEnded(ComfyRun run, List<(string Name, byte[] Data)> made)
    {
        run.Finished = DateTime.Now;
        var took = Took(run.Finished.Value - run.Started);
        if (run.Error == null)
        {
            try
            {
                if (run.Project != null && run.ItemId is { } itemId && run.Project.Find(itemId) != null)
                {
                    var attachments = made.Select(m => _state.Store.WriteAttachment(m.Name, m.Data)).ToList();
                    Edit(run.Project, itemId, i =>
                    {
                        if (attachments.Count > 0) (i.Attachments ??= []).AddRange(attachments);
                        i.AddActivity(run.Agent, $"Ran the ComfyUI workflow “{run.Workflow}” on {run.Endpoint}: {Files(made.Count)} in {took}.", agent: true);
                    });
                    run.Files.AddRange(attachments.Select(a => a.FileName));
                }
                else
                {
                    var folder = Path.Combine(_state.Store.DataRoot, "comfyui", run.Id.ToString("N")[..8]);
                    Directory.CreateDirectory(folder);
                    foreach (var (name, data) in made)
                    {
                        var path = Path.Combine(folder, ProJict.Core.Storage.ProjectStore.SafeFileName(name));
                        File.WriteAllBytes(path, data);
                        run.Files.Add(path);
                    }
                }
                run.State = ComfyRunState.Done;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.Error = "Couldn't keep what it made: " + ex.Message;
            }
        }
        if (run.Error != null)
        {
            run.State = ComfyRunState.Failed;
            if (run.Project != null && run.ItemId is { } itemId && run.Project.Find(itemId) != null)
                Edit(run.Project, itemId, i => i.AddActivity(run.Agent, $"Ran the ComfyUI workflow “{run.Workflow}” on {run.Endpoint}; it failed: {run.Error}", agent: true));
        }
        _state.StatusText = run.State == ComfyRunState.Done
            ? $"{run.Agent}'s workflow “{run.Workflow}” is done: {Files(run.Files.Count)} in {took}."
            : $"{run.Agent}'s workflow “{run.Workflow}” failed on {run.Endpoint}" + (run.ItemId != null ? " (the item's history says why)." : ".");
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

    /// <summary>"40s" for a run under a minute; "3m", "1h 5m" after that.</summary>
    private static string Took(TimeSpan time) => time.TotalSeconds < 59.5 ? $"{Math.Max(1, (int)Math.Round(time.TotalSeconds))}s" : WorkLog.Format(time);

    private JsonObject RunJson(ComfyRun run) => new()
    {
        ["run"] = ShortId(run.Id),
        ["workflow"] = run.Workflow,
        ["endpoint"] = run.Endpoint,
        ["state"] = run.State.ToString().ToLowerInvariant(),
        ["item"] = run.ItemId is { } item ? ShortId(item) : null,
        ["files"] = run.Files.Count > 0 ? new JsonArray(run.Files.Select(f => (JsonNode)f).ToArray()) : null,
        ["error"] = run.Error,
        ["took"] = run.Finished is { } end ? Took(end - run.Started) : null,
    };

    private string WorkflowRun(McpSession session, Args args, List<Attachment> files)
    {
        var key = args.Required("run").Trim();
        var run = _comfyRuns.LastOrDefault(r => r.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase))
                  ?? throw new McpToolException($"No run {key} (ProJict keeps the last {ComfyRunsKept} until it restarts).", ToolProblem.NotFound);
        return Json(RunJson(run));
    }

    /// <summary>For waitingForYou and the watcher: the agent's workflow runs that ended.</summary>
    private IEnumerable<(string Key, string Line)> ComfyRunLines(McpSession session)
    {
        foreach (var run in _comfyRuns.Where(r => r.State is ComfyRunState.Done or ComfyRunState.Failed && AgentNaming.IsOwnWork(session, r.Agent)).ToList())
            yield return ($"comfyrun:{run.Id}", run.State == ComfyRunState.Done
                ? $"Your ComfyUI run of “{run.Workflow}” on {run.Endpoint} is done: {Files(run.Files.Count)}" +
                  (run.ItemId is { } item ? $" attached to #{ShortId(item)}." : $": {string.Join(", ", run.Files)}.")
                : $"Your ComfyUI run of “{run.Workflow}” on {run.Endpoint} failed: {run.Error}");
    }
}
