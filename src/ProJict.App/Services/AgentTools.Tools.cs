using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

/// <summary>
/// Outside tools for agents (Settings › Tools): list_tools says which there are, what each is for and how to reach it;
/// use_tool_key hands over an API key once the user allows it (asked in ProJict: once, always for that agent, or no).
/// Tools in the browser give their website and how the user is signed in; agents use them with their own browser tools.
/// </summary>
public sealed partial class AgentTools
{
    private ToolService ToolBox => _main.Tools;

    private void AddToolTools()
    {
        Add("list_tools", "List outside tools",
            "The outside tools the user set up for agents (Settings › Tools), such as ElevenLabs for voices and sound or Suno for music: what each is for, " +
            "whether you reach it through its API (get the key with use_tool_key) or on its website in your browser, how the user is signed in there, " +
            "the budget they set, and which are set up for a project. Use them for the work they're for, within the budget.",
            Schema(("project", "string", "Mark the tools set up for this project (name or key); defaults to the project matching your working directory.", false)),
            ListTools, readOnly: true);
        Add("use_tool_key", "Get a tool's API key",
            "A tool's API key, for calling its API yourself (list_tools shows which tools have one). The user decides: the first time ProJict asks them " +
            "(allow once, always for you, or no), and you hear the answer in waitingForYou; then call this again. Keep the key to yourself: never write it " +
            "in files, commits, items, wiki pages or chat, and don't pass it on.",
            Schema(("tool", "string", "The tool's name, e.g. ElevenLabs.", true),
                ("why", "string", "What you need it for, in a few words; the user sees it when ProJict asks.", false)),
            UseToolKey);
    }

    private JsonObject ToolJson(ExternalTool tool, Project? project) => new()
    {
        ["tool"] = tool.Name,
        ["purpose"] = tool.Purpose.Length > 0 ? tool.Purpose : null,
        ["access"] = tool.Access == ToolAccess.Api ? "api" : "browser",
        ["website"] = tool.Website,
        ["keySet"] = tool.Access == ToolAccess.Api ? tool.HasKey : null,
        ["signedInAs"] = tool.Access == ToolAccess.Browser ? tool.SignIn : null,
        ["budget"] = tool.Budget,
        ["forThisProject"] = project != null && tool.Projects.Contains(project.Id) ? true : null,
        ["projects"] = tool.Projects.Count > 0
            ? new JsonArray(tool.Projects.Select(id => LocalProjects.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().Select(n => (JsonNode)n).ToArray())
            : null,
        ["howToUse"] = tool.Access == ToolAccess.Browser
            ? $"On its website{(tool.Website != null ? $" ({tool.Website})" : "")}, with your own browser tools; the user signs in there themself."
            : tool.HasKey ? "Through its API: get the key with use_tool_key." : "Through its API, once the user sets its key in Settings › Tools (ask them).",
    };

    private string ListTools(McpSession session, Args args, List<Attachment> files)
    {
        Project? project = args.String("project") != null ? ResolveProject(session, args)
            : ProjectMatcher.ForFolder(LocalProjects, session.WorkingDirectory)?.Project;
        var tools = ToolBox.Tools.OrderByDescending(t => project != null && t.Projects.Contains(project.Id)).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new JsonObject
        {
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)ToolJson(t, project)).ToArray()),
            ["project"] = project?.Name,
        };
        result["note"] = tools.Count == 0
            ? "The user hasn't set up any tools (Settings › Tools)."
            : "Budgets are the user's limits: stay within them, and say what you used in the item you worked on. ProJict doesn't count usage.";
        return Json(result);
    }

    private string UseToolKey(McpSession session, Args args, List<Attachment> files)
    {
        var name = args.Required("tool");
        var tool = ToolBox.Data.Find(name) ?? throw new McpToolException(
            $"No tool “{name}”. " + (ToolBox.Tools.Count > 0 ? $"Tools: {string.Join(", ", ToolBox.Tools.Select(t => t.Name))}." : "The user hasn't set up any (Settings › Tools)."),
            ToolProblem.NotFound);
        if (tool.Access == ToolAccess.Browser)
            throw new McpToolException($"{tool.Name} is used in the browser, without a key: {(tool.Website != null ? $"open {tool.Website} " : "open its website ")}" +
                                       "with your own browser tools; the user signs in there themself.");
        if (!tool.HasKey) throw new McpToolException($"The user hasn't set an API key for {tool.Name} yet: ask them to, in Settings › Tools.", ToolProblem.NotFound);
        var (key, request, refused) = ToolBox.Ask(session.AgentName, tool, args.String("why"));
        if (key != null)
        {
            _state.StatusText = $"{session.AgentName} got the {tool.Name} API key.";
            return Json(new JsonObject
            {
                ["tool"] = tool.Name,
                ["key"] = key,
                ["website"] = tool.Website,
                ["budget"] = tool.Budget,
                ["note"] = "Keep the key to yourself: never write it in files, commits, items, wiki pages or chat, and don't pass it on. " +
                           (tool.IsAlwaysAllowed(session.AgentName) ? "The user lets you have it whenever you need it." : "The user let you have it this once."),
            });
        }
        if (refused) throw new McpToolException($"The user said no to giving you the {tool.Name} key. Ask them why (add_comment or Chat) before asking again.", ToolProblem.Forbidden);
        _state.StatusText = request!.Text;
        return Json(new JsonObject
        {
            ["tool"] = tool.Name,
            ["state"] = "asked",
            ["note"] = $"ProJict asks the user whether you may have the {tool.Name} key. You hear their answer in waitingForYou; then call use_tool_key again. " +
                       "Meanwhile, go on with something else.",
        });
    }

    /// <summary>For waitingForYou and the watcher: the user's answers to the agent's key requests.</summary>
    private IEnumerable<(string Key, string Line)> ToolKeyLines(McpSession session)
    {
        foreach (var request in ToolBox.Answered.Where(r => AgentNaming.IsOwnWork(session, r.Agent)))
            yield return ($"toolkey:{request.Id}", request.Answer switch
            {
                ToolKeyAnswer.Once => $"The user lets you have the {request.Tool.Name} API key this once: call use_tool_key tool={request.Tool.Name}.",
                ToolKeyAnswer.Always => $"The user lets you have the {request.Tool.Name} API key whenever you need it: call use_tool_key tool={request.Tool.Name}.",
                _ => $"The user said no to giving you the {request.Tool.Name} API key.",
            });
    }

    /// <summary>For current_project: the tools set up for the project, by name and how they're reached.</summary>
    private JsonArray? ProjectToolsJson(Project project) =>
        ToolBox.Data.For(project.Id).ToList() is { Count: > 0 } tools
            ? new JsonArray(tools.Select(t => (JsonNode)$"{t.Name} ({t.AccessText}){(t.Purpose.Length > 0 ? ": " + t.Purpose : "")}").ToArray())
            : null;
}
