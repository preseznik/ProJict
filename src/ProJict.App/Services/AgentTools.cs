using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ProJict.App.Services.Sharing;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;
using ProJict.Core.Api;
using ProJict.Core.Chat;
using ProJict.Core.Features;
using ProJict.Core.Import;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Wiki;

namespace ProJict.App.Services;

/// <summary>
/// What AI agents can do in ProJict over MCP. Every call runs on the UI thread and edits through the project's view
/// model, so the board updates live and each change is one Ctrl+Z step. Agents sign everything they do, an item one
/// agent checked out can't be changed by another unless it takes over (which is logged), nothing here deletes, and
/// what an agent finishes waits for your review.
/// </summary>
public sealed partial class AgentTools : IMcpToolHost
{
    private delegate string Handler(McpSession session, Args args, List<Attachment> files);
    private delegate Task<string> AsyncHandler(McpSession session, Args args);

    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, (Handler Run, string? FilesArgument)> _handlers = [];
    private readonly Dictionary<string, AsyncHandler> _asyncHandlers = [];
    private readonly List<McpTool> _tools = [];
    private readonly List<McpSession> _live = [];
    /// <summary>The session whose call is running: calls run one at a time on the UI thread.</summary>
    private McpSession? _caller;
    /// <summary>What each named agent was told, kept across restarts.</summary>
    private readonly NoticedStore _noticed;

    /// <summary>Raised on the UI thread after an agent did something (it's in the session's <see cref="McpSession.Recent"/>).</summary>
    public event Action<McpSession, AgentAction>? Recorded;

    public AgentTools(AppState state, MainViewModel main, Dispatcher dispatcher)
    {
        _state = state;
        _main = main;
        _dispatcher = dispatcher;
        _noticed = new NoticedStore(state.PersistsSettings ? NoticedStore.DefaultPath : null, state.Store.DataRoot);

        Add("list_projects", "List projects", "All projects with their repo folder, GitHub link and item counts.",
            Schema(), ListProjects, readOnly: true);
        Add("current_project", "Current project",
            "Finds the project for a folder (by default your working directory): by its local repo folder, the git remote, or the folder name. " +
            "Returns the project's categories and the user's standing instructions: for all agents (instructionsForAllAgents) and for this project (instructions).",
            Schema(("path", "string", "Folder to look up; defaults to the agent's working directory.", false),
                ("project", "string", "Project name or key, instead of a folder.", false)), CurrentProject, readOnly: true);
        Add("update_project", "Update project details",
            "Changes a project's details: its local repo folder, GitHub link, description, standing instructions for agents, and how it's " +
            "shipped (the steps it has and the how-to an agent follows). Give only what changes; an empty string clears a field. " +
            "After you change the steps or how-to, every ship request needs the user's approval until they've checked your change.",
            Schema(ProjectArg,
                ("repo_path", "string", "The project's folder on the ProJict computer (it must exist).", false),
                ("github_url", "string", "The GitHub repository, e.g. https://github.com/owner/name.", false),
                ("description", "string", "What the project is.", false),
                ("instructions", "string", "Standing instructions for agents and Copy prompt: engine version, conventions, how to test…", false),
                ("ship_steps", "string[]", "The steps this project has, from commit, build, package, push and release (replaces the list).", false),
                ("ship_how_to", "string", "How to ship it: what an agent does for each step, e.g. bump the version in …, run build.ps1, test, commit, push, publish the release.", false),
                ("test_build", "string", "The current test build for the user to try: an app, installer, folder or file, full path or relative to the repo folder " +
                 "(e.g. artifacts/Game-Setup-1.2.exe). It shows on the project page with a Run button. Set it after you make a build worth trying; empty clears it.", false),
                ("test_build_note", "string", "With test_build: what it is or what to try, in a line.", false)),
            UpdateProject);
        Add("set_name", "Pick your name",
            "Picks a short name of your own that you sign everything with from now on (comments, history, check-outs, chat), e.g. Ada or Quill. " +
            "Call it when you start. If you had a name in this workspace before, take it again: your earlier work then counts as yours. " +
            "A name another connected agent has is taken.",
            Schema(("name", "string", $"One word of 2 to {AgentNaming.MaxLength} letters, digits or dashes.", true)), SetName);
        Add("set_label", "Set your label",
            "Says in a few words what you're working on in this session (e.g. the task or folder); it shows on your card in the user's Agents view. " +
            "It isn't part of your name: pick that with set_name.",
            Schema(("label", "string", "A few words; empty removes the label.", true)), SetLabel);
        Add("set_status", "Set your status",
            "Tells the user in a few words what you're doing right now, e.g. \"Running the tests\" or \"Writing the fix for the launcher\". " +
            "It shows live next to your name in ProJict's Agents view. Call it when you start a longer step; empty clears it.",
            Schema(("status", "string", "A few words (up to 120 characters); empty clears it.", true)), SetStatus);
        Add("list_items", "List items",
            "Items of a project, flattened (sub-items name their parent). Filter by state, tag, category or text. " +
            "Items the user keeps off limits to agents (by priority, or on hold) are left out unless include_off_limits is true.",
            Schema(ProjectArg,
                ("state", "enum:open|todo|in_progress|done|review|all", "open (default) = to do and in progress; review = finished by an agent, waiting for the user.", false),
                ("tag", "string", "Only items with this tag, e.g. BUG.", false),
                ("category", "string", "Only this category.", false),
                ("query", "string", "Words that must all appear in the item text or attachment names.", false),
                ("priority", "enum:urgent|high|medium|low|hold|none", "Only items at least this pressing (low = any priority set), hold = only items on hold, none = only items without one.", false),
                ("sort", "enum:board|priority", "board (default) = the order on the board; priority = most pressing first.", false),
                ("include_off_limits", "boolean", "Also list items you may not work on, marked offLimits (default false).", false),
                ("limit", "integer", "Most items to return (default 50).", false)),
            ListItems, readOnly: true);
        Add("get_item", "Get item",
            "One item with its sub-items, attachments (file paths you can open), history and discussion, plus the project's repo folder and instructions.",
            Schema(IdArg), GetItem, readOnly: true);
        Add("claim_item", "Check out item",
            "Call before you start on an item: marks it in progress with your name. While you have it checked out, other agents can't change it. Returns the item.",
            Schema(IdArg, ("note", "string", "Optional: what you plan to do.", false), ForceArg), ClaimItem);
        Add("complete_item", "Complete item",
            "Marks an item done once you finished it, with a short resolution (what changed, commit id) and optional evidence files such as screenshots. " +
            "The user reviews it afterwards.",
            Schema(IdArg, ("resolution", "string", "What you did, for the user.", true),
                ("attachments", "string[]", "Files to attach, e.g. screenshots or logs (absolute paths or relative to your working directory).", false),
                ("tokens", "integer", "Tokens you used on it, if you can tell (they add up in the project's Time tab).", false),
                ("cost", "string", "What it cost in dollars, if you can tell, e.g. 1.40.", false), ForceArg),
            CompleteItem, filesArgument: "attachments");
        Add("add_item", "Add item", "Adds a new item, e.g. a bug you found. The category is created if it doesn't exist.",
            Schema(ProjectArg, ("text", "string", "The item text; a prefix like \"BUG: \" becomes its tag.", true),
                ("category", "string", "Category name (required unless parent is given).", false),
                ("tag", "string", "Tag such as BUG, ADD, CHANGE, FIX, IDEA.", false),
                ("priority", "enum:urgent|high|medium|low|hold|none",
                    "Required, chosen on purpose: how pressing it is (urgent, high, medium, low), hold to park it (agents then leave it alone), or none to " +
                    "leave it without one for the user to decide (a sub-item with none follows its parent's).", true),
                ("parent", "string", "Id of an item to add this under as a sub-item.", false), ForceArg),
            AddItem);
        Add("update_item", "Update item",
            "Changes an item's text, tag, priority or state, or comments on it. state: todo puts it back and frees it (e.g. when you give up), " +
            "done marks it finished for review (prefer complete_item).",
            Schema(IdArg, ("text", "string", "New text.", false), ("tag", "string", "New tag; empty removes it.", false), PriorityArg,
                ("state", "enum:todo|in_progress|done", "New state.", false),
                ("comment", "string", "Posts a comment in the item's discussion (like add_comment).", false), ForceArg),
            UpdateItem);
        Add("attach_file", "Attach file", "Attaches files to an item. ProJict keeps its own copy.",
            Schema(IdArg, ("paths", "string[]", "Files to attach (absolute or relative to your working directory).", true), ForceArg),
            AttachFile, filesArgument: "paths");
        Add("list_comments", "List comments",
            "The discussion of an item, a category or a group as threads with replies. Without a target: the newest comments across a project " +
            "(its items, categories and group), so you can see what the user asked or answered.",
            Schema(("item", "string", "Item id.", false), ("category", "string", "Category name (in the project).", false),
                ("group", "string", "Group name (e.g. Work).", false), ProjectArg,
                ("since", "string", "Only comments after this date and time (yyyy-MM-dd HH:mm), when listing a whole project.", false),
                ("limit", "integer", "Most comments when listing a whole project (default 30).", false)),
            ListComments, readOnly: true);
        Add("add_comment", "Add comment",
            "Posts in the discussion of an item, a category or a group: a new thread, or a reply to a comment (reply_to). " +
            "Use it to ask the user questions, report progress, or answer them. When you give the user choices, add them as choices: they " +
            "answer with a click, and their answer comes back as their reply.",
            Schema(("text", "string", "The comment.", true), ("item", "string", "Item id.", false), ("category", "string", "Category name.", false),
                ("group", "string", "Group name.", false), ProjectArg, ("reply_to", "string", "Id of the comment to reply to.", false),
                ("attachments", "string[]", "Files to post with the comment, e.g. a screenshot (absolute paths or relative to your working directory).", false),
                ("choices", "string[]", "Questions to answer with a click, one per entry as \"question | option | option\" (your suggestion first, " +
                 "up to 6 options and 8 questions). They show as buttons under the comment.", false)),
            AddComment, filesArgument: "attachments");
        Add("read_docs", "Read notes", "Lists a project's notes (documents), or returns one note's text and attached files when a title is given.",
            Schema(ProjectArg, ("title", "string", "Note to read.", false)), ReadDocs, readOnly: true);
        Add("write_doc", "Write note",
            "Creates or updates a note (shown in the project's Notes tab) for documentation or reports meant for people, with optional files such as charts or PDFs.",
            Schema(ProjectArg, ("title", "string", "Note title; an existing note with this title is updated.", true),
                ("text", "string", "Markdown text.", true),
                ("mode", "enum:replace|append", "replace (default) or append to the existing text.", false),
                ("attachments", "string[]", "Files to attach to the note.", false)),
            WriteDoc, filesArgument: "attachments");
        Add("draft_release", "Draft release notes",
            "Patch notes from everything finished since the project's last release. With save=true the release is recorded and its items linked to it.",
            Schema(ProjectArg, ("version", "string", "Version; defaults to the next patch version.", false),
                ("title", "string", "Optional release title.", false), ("save", "boolean", "Record the release (default false).", false)),
            DraftRelease);
        Add("ship_request", "Ship request",
            "The project's ship request, if the user made one (they want it committed, built, packaged, pushed and/or released): its steps, state, " +
            "plan and log, plus the project's own instructions for shipping (howTo), the suggested next version and the items still waiting for the user's review.",
            Schema(ProjectArg), ShipRequestInfo, readOnly: true);
        Add("plan_ship", "Plan shipping",
            "Posts your plan for the ship request: the version, what's in it, items still waiting for the user's review and the commands you'll run, " +
            "following howTo. The user approves or declines it in ProJict; run nothing before it's approved. You can replace the plan until they decide.",
            Schema(ProjectArg, ("plan", "string", "The plan, for the user.", true), ("version", "string", "The version you'll ship, if any.", false)),
            PlanShip);
        Add("report_ship", "Report a ship step",
            "Once the user approved the plan: one step's outcome, e.g. the commit id, the build result or the release URL.",
            Schema(ProjectArg, ("step", "enum:commit|build|package|push|release", "The step.", true),
                ("outcome", "enum:done|failed|skipped", "What happened.", true), ("details", "string", "Commit id, output, URL, or why.", false)),
            ReportShip);
        Add("finish_ship", "Finish shipping",
            "Ends an approved ship request: shipped, or failed with the reason.",
            Schema(ProjectArg, ("outcome", "enum:shipped|failed", "Whether it shipped.", true), ("summary", "string", "A sentence or two for the user.", true)),
            FinishShip);
        AddChatTools();
        AddWikiTools();
        AddToolTools();
        AddBrowserTools();
        AddWorkTools();
        AddUsageTools();
        AddComfyTools();
        AddCleanupTools();
        AddPublishTools();
        AddMediaTools();
        AddPictureTools();
        AddStudioTools();
    }

    public IReadOnlyList<McpTool> Tools => _tools;

    /// <summary>
    /// Your own projects (projects shown from another ProJict aren't open to agents), and of those only the ones the
    /// caller's API token reaches.
    /// </summary>
    private List<Project> LocalProjects =>
        _state.Projects.Where(p => p.Remote == null && (_caller?.Projects is not { } only || only.Contains(p.Id))).ToList();

    // ───────────── Projects shared with this ProJict ─────────────

    /// <summary>
    /// The projects an agent reaches: this ProJict's own (an API token's, if it's limited to some), and the ones other
    /// ProJicts share with it, which the agent reads, and changes as far as the guest's role there allows.
    /// </summary>
    private List<Project> ReachableProjects =>
        _state.Projects.Where(p => p.Remote == null ? _caller?.Projects is not { } only || only.Contains(p.Id) : _caller?.Projects == null).ToList();

    /// <summary>The connection a shared project comes through.</summary>
    private HostConnection? HostOf(Project project) =>
        project.Remote is { } remote ? _main.Guest.Connections.FirstOrDefault(c => c.Address == remote.HostKey) : null;

    /// <summary>Who this ProJict's user is in the project's host ("Alice").</summary>
    private string GuestNameIn(Project project) => HostOf(project)?.MyName ?? _state.UserName;

    /// <summary>
    /// How the agent signs in a project: its name, or in a project shared with this ProJict "Ada (via Alice)", since it
    /// works there through Alice's connection (the host checks that). Where this computer is the host's own user, its
    /// agents are the host's too, and sign with their own names.
    /// </summary>
    private string SignAs(McpSession session, Project project) =>
        project.Remote != null && HostOf(project) is not { IsSelf: true } ? AgentNaming.Via(session.AgentName, GuestNameIn(project)) : session.AgentName;

    /// <summary>Whether something signed <paramref name="by"/> in the project is the agent's own work.</summary>
    private bool OwnIn(McpSession session, Project project, string by) =>
        AgentNaming.IsOwnWork(session, project.Remote != null && HostOf(project) is not { IsSelf: true } ? AgentNaming.WithoutVia(by, GuestNameIn(project)) ?? "" : by);

    private enum Change { Comment, AddItems, Edit, HostOnly }

    /// <summary>
    /// Refuses a change to a project shared with this ProJict that the connection, the host's rules or the guest's role
    /// there don't allow; this ProJict's own projects pass.
    /// </summary>
    private void MayChange(Project project, Change change)
    {
        // While its folder is being cleaned up, a project takes no other work (comments are fine).
        if (change is Change.Edit or Change.AddItems) CheckNoCleanup(project);
        if (project.Remote is not { } remote) return;
        var name = remote.HostName;
        if (change == Change.HostOnly)
            throw new McpToolException($"{project.Name} is shared with this ProJict by {name}: only {name} changes its details and shipping.", ToolProblem.Forbidden);
        var host = HostOf(project);
        if (host is not { State: ConnectionState.Connected })
            throw new McpToolException($"{project.Name} is shared by {name}, and this ProJict isn't connected to theirs right now: you can read it, not change it.", ToolProblem.Forbidden);
        if (!host.AgentsAllowed)
            throw new McpToolException($"{name} doesn't let guests' AI agents change what they share (or runs an older ProJict): you can read {project.Name}, not change it.", ToolProblem.Forbidden);
        var allowed = change switch
        {
            Change.Comment => GuestAccess.CanComment(host.Role),
            Change.AddItems => GuestAccess.CanAddItems(host.Role),
            _ => GuestAccess.CanEdit(host.Role),
        };
        if (!allowed)
            throw new McpToolException($"You work in {name}'s ProJict through {GuestNameIn(project)}, who is a {host.Role.ToString().ToLowerInvariant()} there " +
                                       $"({GuestAccess.Describe(host.Role)}), so you can't {change switch { Change.Comment => "comment", Change.AddItems => "add items", _ => "change items" }} in {project.Name}.",
                ToolProblem.Forbidden);
    }

    /// <summary>Which items agents may work on in a project: this ProJict's rules, or for a shared project its host's.</summary>
    private sealed record AgentRules(Project Project, Dictionary<TodoItem, Priority> Governing, IReadOnlyCollection<Priority> Allowed);

    private AgentRules RulesFor(Project project) =>
        new(project, AgentAccess.Governing(project), project.Remote != null ? HostOf(project)?.AgentPriorities ?? [] : AgentPriorities);

    public string Instructions => InstructionsFor(remote: false);

    /// <summary>What agents are told when they connect; over the API (remote) they watch Chat over HTTP, not with ProJict.exe.</summary>
    public string InstructionsFor(bool remote) =>
        "ProJict is the user's to-do list app: projects contain categories, categories contain items (with sub-items, tags such as BUG or ADD, " +
        "attachments, discussions, and a state: todo, in_progress or done). To work on items: current_project (or list_projects) to find the project, " +
        "list_items to see what is open, claim_item to check an item out before you start (other agents then can't change it), then complete_item " +
        "with a short resolution and evidence such as screenshots (and the tokens and cost you used, if you can tell; ProJict counts your time from " +
        "the check-out). Everything you do is signed with your name: when you start, pick a short name " +
        "of your own with set_name (one word, e.g. Ada), the same one as before if you had one here, so your earlier work stays yours; " +
        "set_label says what you're working on. Everything you complete waits for the user's review, so only complete what you really finished; use " +
        "update_item with state todo if you stop. The user decides which items agents may work on, by priority (current_project says which); " +
        "the others and items on hold are off limits: list_items leaves them out, and you may read and comment on them but not check them out, " +
        "change or complete them. Ask the user questions or report progress with add_comment (offering options? give them as choices, which the user " +
        "answers with a click), and check list_comments for their " +
        "answers. File problems you notice with add_item, choosing each one's priority, and put documentation or reports for people in a note with write_doc. " +
        "Knowledge that lasts (architecture, how things work, decisions) goes in the wiki: list_wiki, read_wiki and write_wiki (a whole page, or a section by heading, " +
        "in Markdown with [[Page name]] links); pass the version you read so your change merges with others', and what you write waits for the user's review. " +
        "Keep a project's details (repo folder, GitHub link, description, instructions, how it's shipped) up to date with update_project, and set its test_build when you make a build worth trying (the user runs it from the project page). " +
        "When you start a longer step (running tests, a build, a bigger change), tell the user with set_status; it shows live in ProJict. " +
        "Items with a gitHubIssue stand for that GitHub issue: mention \"Fixes #<number>\" in the commit or pull request that resolves one. " +
        "When a project shows a shipRequest, the user wants it committed, built, packaged, pushed or released: read it with ship_request, post your " +
        "plan with plan_ship and wait for their approval in ProJict; only then run the steps, report each with report_ship and end with finish_ship " +
        "(record a published release with draft_release save=true). " +
        "Videos (a film studio's Publisher) are published through a publish plan: post it with plan_publish (title, description, tags, playlists, " +
        "when, and 2–4 thumbnails to choose from) and wait for the user's decision; publish only an approved plan, with the text and thumbnail it " +
        "then has (publish_plan), and end with finish_publish. " +
        "When a result says asleep, the user put every agent to sleep: finish what you're doing, then stop and stay idle until you're told you're " +
        "woken (claim_item is refused meanwhile). " +
        "When a project shows a studio (a game or film studio; it can have both), the user hires agents to its roles: if you have one, work as it expects, and agree with the " +
        "team in the studio's channel who takes what, volunteering for what suits your model and tools. A role's first agent is its senior, the others " +
        "juniors who agree with it what they take. If you're its lead, you hire too: talk with the agents not hired there yet first, then hire_to_role. And if " +
        "the user lets the lead review wiki pages, you're told which pages wait for you: accept them or send them back with review_wiki. " +
        "Outside tools the user set up for agents (such as ElevenLabs, Suno, or ffmpeg on this computer) are in list_tools; use_tool_key gets one's API key once the user allows it, " +
        "browser_open opens a tool used in the browser in ProJict's own browser (where the user signed in), " +
        "and run_workflow runs a workflow on the user's ComfyUI server, attaching what it makes to an item. " +
        "If you can tell how much of your usage you've used, say so with report_usage. " +
        "When a project shows a cleanupRequest, the user wants its repo folder cleaned up: read it with cleanup_request, scan the folder and post your plan " +
        "with plan_cleanup; ProJict moves what they approve to the Recycle Bin, so never remove files yourself, and do no other work there until it's decided. " +
        "The user and agents also talk live in Chat #channels (list_channels, join_channel to read one, read_channel, post_message, create_channel), and privately: " +
        "post_message to channel @name writes to the user or an agent only. Keep Chat human. " + ChatStyle + " To listen, call " +
        "wait_for_messages. What waits for you shows in a tool result's waitingForYou list, each thing once: chat invitations, @mentions and new " +
        "messages, private ones included (read them with read_channel and reply with post_message: say hello when invited, and answer what you're asked, or say you " +
        "can't now), the user's comments on items you worked on (answer with add_comment), work the user sent back (get_item for why, then " +
        "claim_item to work on it again), ship requests and clean-up requests in your project. Deal with them before going on, or say why not. " +
        "Between tool calls nothing reaches you, so if your client can run a command in the background and tell you about each line it prints " +
        "(Claude Code's Monitor tool, for one), start a chat watcher once you've picked your name and keep it running: " +
        (remote
            ? $"GET {ProJict.Core.Api.ApiRoutes.Base}/chat/watch on this server with your Authorization header (e.g. curl -sN) "
            : $"{WatchCommand("<your name>")} ") +
        "prints a line for each chat message for you, as it comes, so you can answer while you're idle or busy with other things. " +
        "Projects another ProJict shares with this one are reachable too, and so are its #channels (named like #hud (Sam's)): list_projects " +
        "says what you may do there; what you do there is signed with your name and the user's, like Ada (via Alice). " +
        "Ids (items, comments and messages) may be shortened to their first 8 characters." +
        (ForAllAgents is { } mine ? " The user's instructions for all agents, in every project (a project's own instructions add to them): " + mine : "");

    /// <summary>The user's instructions for every agent in every project (Settings › AI agents); null when there are none.</summary>
    private string? ForAllAgents => _state.Settings.AgentInstructions?.Trim() is { Length: > 0 } text ? text : null;

    public void SessionStarted(McpSession session)
    {
        lock (_live)
        {
            // Sessions of the same client without a name of their own are numbered: Claude Code, Claude Code 2, …
            if (string.IsNullOrWhiteSpace(session.SignAs))
            {
                var taken = _live.Where(s => s.Name == null && string.IsNullOrWhiteSpace(s.SignAs) && s.Client == session.Client).Select(s => s.Number).ToHashSet();
                while (taken.Contains(session.Number)) session.Number++;
            }
            _live.Add(session);
        }
        _dispatcher.InvokeAsync(() =>
        {
            _state.StatusText = $"{session.AgentName} connected to ProJict.";
            _main.Agents.Connected(session, ProjectMatcher.ForFolder(LocalProjects, session.WorkingDirectory)?.Project);
        });
    }

    public void SessionEnded(McpSession session)
    {
        lock (_live) _live.Remove(session);
        if (!_dispatcher.HasShutdownStarted) _dispatcher.InvokeAsync(() => _main.Agents.Disconnected(session));
    }

    /// <summary>What a frozen API token is told whatever it asks.</summary>
    public const string FrozenMessage = "The user froze this API token in ProJict (Settings › API): it still connects, but can't do anything, not even read, until they unfreeze it.";

    public async Task<string> CallAsync(McpSession session, string name, JsonElement arguments)
    {
        if (session.Frozen) throw new McpToolException(FrozenMessage, ToolProblem.Forbidden);
        // What it was told lives on the UI thread, like everything else about sessions.
        await _dispatcher.InvokeAsync(() => Recall(session));
        if (name == "wait_for_messages")
        {
            var heard = await WaitForMessagesAsync(session, arguments);
            await _dispatcher.InvokeAsync(() => Keep(session));
            return heard;
        }
        var args = new Args(arguments);
        if (!Allows(session, name, arguments))
            throw new McpToolException(ChatTools.Contains(name)
                    ? $"This API token is read only, so it can only read Chat, not {name.Replace('_', ' ')}. The user can let read-only tokens use Chat (Settings › API)."
                    : $"This API token is read only, so it can't {name.Replace('_', ' ')}. The user can make a token that may change things in ProJict (Settings › API).",
                ToolProblem.Forbidden);
        if (_asyncHandlers.TryGetValue(name, out var runAsync))
        {
            // Started on the UI thread (the agent's projects apply up to its first wait); its waits come back there.
            var running = await _dispatcher.InvokeAsync(() =>
            {
                _caller = session;
                try { return runAsync(session, args); }
                finally { _caller = null; }
            });
            var result = await running;
            return await _dispatcher.InvokeAsync(() =>
            {
                Record(session, name, args);
                var told = WithWaiting(session, name, result);
                Keep(session);
                return told;
            });
        }
        var (run, filesArgument) = _handlers[name];
        // Files are copied off the UI thread; the edit itself then runs on it.
        var files = filesArgument == null ? []
            : session.Remote ? await StoreUploadsAsync(args.Uploads(filesArgument))
            : await ImportFilesAsync(args.Strings(filesArgument), session.WorkingDirectory);
        var (text, error) = await _dispatcher.InvokeAsync(() =>
        {
            _caller = session;
            try
            {
                var result = run(session, args, files);
                Record(session, name, args);
                var told = WithWaiting(session, name, result);
                Keep(session);
                return ((string?)told, (Exception?)null);
            }
            catch (Exception ex) { return (null, ex); }
            finally { _caller = null; }
        });
        if (error != null) ExceptionDispatchInfo.Throw(error);
        return text!;
    }

    /// <summary>
    /// (UI thread.) A named agent picks up what it was told before, in another session or before ProJict restarted (say for an update),
    /// so that isn't said again. Unnamed sessions ("Claude Code 2") aren't anyone in particular, so they start afresh.
    /// </summary>
    private void Recall(McpSession session)
    {
        if (!HasOwnName(session) || session.RecalledFor == session.AgentName) return;
        session.Noticed.UnionWith(_noticed.For(session.AgentName));
        session.RecalledFor = session.AgentName;
    }

    /// <summary>Notes what a named agent has been told by now, for after a restart.</summary>
    private void Keep(McpSession session)
    {
        if (!HasOwnName(session)) return;
        _noticed.Remember(session.AgentName, [.. session.Noticed]);
    }

    private static bool HasOwnName(McpSession session) => session.Name != null || !string.IsNullOrWhiteSpace(session.SignAs);

    /// <summary>Whether the session may make the call: a read-only API token only reads, apart from Chat when Settings › API lets it.</summary>
    public bool Allows(McpSession session, string name, JsonElement arguments) =>
        !session.ReadOnly || !Changes(name, arguments) || (ChatTools.Contains(name) && _state.AppSettings.ReadOnlyTokensUseChat);

    /// <summary>What an API token may do in Chat: "read and write", "read only" (a read-only token, when Settings › API says so) or "off".</summary>
    public string ChatAccess(bool readOnly) =>
        !_state.AppSettings.AgentsUseChat ? "off" : readOnly && !_state.AppSettings.ReadOnlyTokensUseChat ? "read only" : "read and write";

    /// <summary>Whether a call changes anything in ProJict (a read-only API token can't make it).</summary>
    public bool Changes(string name, JsonElement arguments) => name switch
    {
        "set_label" or "set_status" => false,
        "draft_release" => new Args(arguments).Bool("save") == true,
        _ => _tools.FirstOrDefault(t => t.Name == name) is not { ReadOnly: true },
    };

    /// <summary>The tool whose argument takes files, if any (paths locally, uploads over the API).</summary>
    public string? FilesArgument(string tool) => _handlers.TryGetValue(tool, out var h) ? h.FilesArgument : null;

    /// <summary>An attachment the session may download (it's on an item or note in a project it reaches), and its file.</summary>
    public async Task<(Attachment Attachment, string Path)?> FindAttachmentAsync(McpSession session, Guid id) =>
        await _dispatcher.InvokeAsync(() =>
        {
            _caller = session;
            try
            {
                // Group discussions are outside a token limited to some projects.
                if (ReachableProjects.Where(p => p.Remote != null).SelectMany(p => p.Attachments().Select(a => (Project: p, Attachment: a)))
                        .FirstOrDefault(x => x.Attachment.Id == id) is { Project: { } shared } found)
                {
                    var path = _state.AttachmentPath(shared, found.Attachment);
                    if (!File.Exists(path)) _main.Guest.Fetch(shared, found.Attachment);
                    return (found.Attachment, path);
                }
                var attachment = LocalProjects.SelectMany(p => p.Attachments())
                    .Concat(session.Projects == null ? _state.Settings.GroupAttachments() : [])
                    .Concat(Chat.Visible(ActorOf(session)).SelectMany(c => c.Messages).SelectMany(m => m.Attachments ?? []))
                    .Concat(WikiPagesFor(session).SelectMany(p => p.Attachments ?? []))
                    .FirstOrDefault(a => a.Id == id);
                return attachment == null ? ((Attachment, string)?)null : (attachment, _state.Store.AttachmentPath(attachment));
            }
            finally { _caller = null; }
        });

    // ───────────── What agents do ─────────────

    /// <summary>Notes what the agent just did, in a few words, for the Agents view.</summary>
    private void Record(McpSession session, string tool, Args args)
    {
        if (ChatTools.Contains(tool))
        {
            var key = args.String("channel")?.Trim();
            var channel = key is { } k ? k.StartsWith('@') ? Chat.FindDirect(session.AgentName, k[1..].Trim()) : Chat.Find(k)
                : tool == "create_channel" ? Chat.Channels.LastOrDefault(c => string.Equals(c.CreatedBy, session.AgentName, StringComparison.OrdinalIgnoreCase)) : null;
            var name = channel is { Direct: true } ? "a private conversation with " + ChatRoom.Title(channel, session.AgentName)
                : "#" + (channel?.Name ?? key?.TrimStart('#') ?? "a channel");
            RecordText(session, tool switch
            {
                "list_channels" => "Looked at the chat channels",
                "create_channel" => $"Started {name}",
                "read_channel" => $"Read {name}",
                "post_message" => $"Wrote in {name}",
                "edit_message" => args.Bool("delete") == true ? $"Deleted a message in {name}" : $"Edited a message in {name}",
                "join_channel" => args.Bool("leave") == true ? $"Left {name}" : $"Joined {name}",
                "invite_to_channel" => $"Invited {args.String("who")} to {name}",
                _ => $"Moderated {name} ({args.String("action")?.Replace('_', ' ')})",
            }, channel, tool);
            return;
        }
        if (WikiTools.Contains(tool))
        {
            var page = args.String("page") is { } key ? (FindPageQuietly(session, key) ?? _state.Wiki.Find(key)) : null;
            var name = page != null ? $"“{page.Title}”" : args.String("page") is { } typed ? $"“{WikiPaths.Title(WikiPaths.Normalize(typed))}”" : "";
            var wikiText = tool switch
            {
                "list_wiki" => "Looked through the wiki",
                "read_wiki" => $"Read the wiki page {name}",
                "review_wiki" => args.String("decision") == "send_back" ? $"Sent the wiki page {name} back" : $"Accepted the wiki page {name}",
                _ => $"Wrote the wiki page {name}",
            };
            Remember(session, new AgentAction(DateTime.Now, wikiText, page?.ProjectId, null, tool));
            return;
        }
        (Project Project, ItemLocation Loc)? found = null;
        if ((args.String("id") ?? args.String("item")) is { } id)
        {
            try { found = FindItemIn(id); }
            catch (McpToolException) { }
        }
        var project = found?.Project;
        if (project == null)
        {
            try { project = ResolveProject(session, args); }
            catch (McpToolException) { }
        }
        var item = found?.Loc.Item;
        var what = item != null ? $"“{Short(item.Text)}”" : "an item";
        var text = tool switch
        {
            "list_projects" => "Looked through the projects",
            "current_project" => project != null ? $"Opened {project.Name}" : "Looked for its project",
            "update_project" => $"Changed the details of {project?.Name}",
            "set_name" => $"Now called {session.AgentName}",
            "set_label" => session.Label != null ? $"Working on: {session.Label}" : "Cleared its label",
            "set_status" => session.Status ?? "Cleared its status",
            "list_items" => project != null ? $"Went through the items of {project.Name}" : "Went through the items",
            "get_item" => $"Read {what}",
            "claim_item" => $"Checked out {what}",
            "complete_item" => $"Finished {what}",
            "log_work" => $"Logged work on {what}",
            "report_usage" => "Reported its usage",
            "run_workflow" => $"Ran the ComfyUI workflow “{args.String("workflow")}”",
            "get_workflow" => $"Read the ComfyUI workflow “{args.String("workflow")}”",
            "workflow_run" => "Checked a ComfyUI run",
            "add_item" => $"Added “{Short(args.String("text") ?? "")}”",
            "update_item" => args.String("state") == "todo" ? $"Put back {what}" : $"Changed {what}",
            "attach_file" => $"Attached files to {what}",
            "list_comments" => item != null ? $"Read the discussion of {what}" : "Read the latest comments",
            "add_comment" => item != null ? $"Commented on {what}" : $"Commented on {args.String("category") ?? args.String("group") ?? "a discussion"}",
            "read_docs" => args.String("title") is { } title ? $"Read the note “{title}”" : "Looked at the notes",
            "write_doc" => $"Wrote the note “{args.String("title")}”",
            "draft_release" => args.Bool("save") == true ? "Recorded a release" : "Drafted release notes",
            "ship_request" => "Read the ship request",
            "plan_ship" => "Posted a plan for shipping",
            "report_ship" => $"Reported the {args.String("step")} step: {args.String("outcome")}",
            "finish_ship" => args.String("outcome") == "shipped" ? "Shipped it" : "Couldn't ship it",
            "cleanup_request" => "Read the clean-up request",
            "plan_cleanup" => "Posted a clean-up plan",
            "hire_to_role" => $"Hired {args.String("agent")?.Trim().TrimStart('@')} as {(args.String("as") == "junior" ? "a junior " : "")}{args.String("role")?.Trim()}",
            "set_picture" => args.Bool("wanted") == true ? "Asked for a picture" : args.String("for") is { Length: > 0 } other ? $"Made {other}'s picture" : "Set its picture",
            "link_media" => args.Bool("unlink") == true ? $"Unlinked files from {what}" : $"Linked files to {what}",
            "plan_publish" => $"Planned publishing “{Short(args.String("title") ?? "")}”",
            "publish_plan" => "Read a publish plan",
            "finish_publish" => args.String("outcome") == "published" ? "Published a video" : "Couldn't publish a video",
            _ => tool.Replace('_', ' '),
        };
        Remember(session, new AgentAction(DateTime.Now, text, project?.Id, item?.Id, tool));
    }

    /// <summary>Notes a chat action, in the channel's project.</summary>
    private void RecordText(McpSession session, string text, ProJict.Core.Chat.ChatChannel? channel, string? tool = null) =>
        Remember(session, new AgentAction(DateTime.Now, text, channel?.Link?.ProjectId, channel?.Link?.ItemId, tool));

    private void Remember(McpSession session, AgentAction action)
    {
        session.LastActive = action.At;
        Recorded?.Invoke(session, session.Remember(action));
    }

    private string SetStatus(McpSession session, Args args, List<Attachment> files)
    {
        var status = args.String("status")?.Trim();
        if (status is { Length: > 120 }) throw new McpToolException("Keep the status short (120 characters at most).");
        session.Status = string.IsNullOrEmpty(status) ? null : status;
        session.StatusAt = session.Status == null ? null : DateTime.Now;
        return Json(new JsonObject { ["status"] = session.Status });
    }

    // ───────────── Projects ─────────────

    private string ListProjects(McpSession session, Args args, List<Attachment> files) => Json(new JsonObject
    {
        ["projects"] = new JsonArray(ReachableProjects.Select(p => (JsonNode)WithStudio(ProjectJson(p), p, session)).ToArray()),
    });

    private string CurrentProject(McpSession session, Args args, List<Attachment> files)
    {
        var folder = args.String("path") ?? session.WorkingDirectory;
        if (session.Remote && args.String("path") != null)
            throw new McpToolException("Over the API, say which project with \"project\" (folders on the ProJict computer aren't looked up).");
        var match = args.String("project") != null ? new FolderMatch(ResolveProject(session, args), "name")
            : ProjectMatcher.ForFolder(LocalProjects, folder)
            ?? throw new McpToolException($"No project matches {folder ?? "your working directory"}. Projects: {ProjectNames()}. " +
                                          "Pass \"project\" to the other tools, or ask the user to set the project's local repo folder in ProJict (Details tab).",
                ToolProblem.NotFound);
        _main.Agents.WorkingIn(session, match.Project);
        var json = WithStudio(ProjectJson(match.Project), match.Project, session);
        json["matchedBy"] = match.How;
        if (ProjectToolsJson(match.Project) is { } tools)
        {
            json["tools"] = tools;
            json["toolsNote"] = "Outside tools set up for this project; list_tools says how to reach each (use_tool_key gets an API key, with the user's OK; run_workflow runs a ComfyUI workflow).";
        }
        if (ForAllAgents is { } forAll) json["instructionsForAllAgents"] = forAll;
        if (!string.IsNullOrWhiteSpace(match.Project.PromptPreamble)) json["instructions"] = match.Project.PromptPreamble.Trim();
        json["agentsMayWorkOn"] = (AgentAccess.Describe(RulesFor(match.Project).Allowed) ?? "no items at the moment (you can still read, comment and add items)") +
                                  "; never items on hold";
        if (!string.IsNullOrWhiteSpace(match.Project.Description)) json["description"] = match.Project.Description.Trim();
        if (!match.Project.IsInbox)
        {
            var shipping = ShipFlow.SettingsOf(match.Project);
            json["shipping"] = new JsonObject
            {
                ["steps"] = new JsonArray(shipping.Steps.Select(s => (JsonNode)ShipFlow.Name(s)).ToArray()),
                ["howTo"] = shipping.HowTo,
                ["localStepsWithoutApproval"] = shipping.LocalWithoutApproval,
                ["changedBy"] = shipping.ChangedBy,
            };
        }
        json["categories"] = new JsonArray(match.Project.Categories.Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Name,
            ["open"] = c.Items.Count(i => !i.IsDone),
            ["done"] = c.Items.Count(i => i.IsDone),
        }).ToArray());
        return Json(json);
    }

    private const int MaxDetailLength = 20000;

    private string UpdateProject(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        var changed = new List<string>();
        string? Text(string name, string label)
        {
            if (args.String(name) is not { } value) return null;
            if (value.Length > MaxDetailLength) throw new McpToolException($"{label} can be up to {MaxDetailLength} characters.");
            changed.Add(label);
            return value.Trim();
        }

        var repoPath = Text("repo_path", "repo folder");
        if (repoPath is { Length: > 0 })
        {
            if (!Path.IsPathFullyQualified(repoPath)) throw new McpToolException("Give the repo folder as a full path, e.g. C:\\Code\\Game.");
            repoPath = Path.GetFullPath(repoPath);
            if (!Directory.Exists(repoPath)) throw new McpToolException($"There's no folder {repoPath} on the ProJict computer.");
        }
        var gitHub = Text("github_url", "GitHub link");
        if (gitHub is { Length: > 0 } && GitHubReleases.ParseRepo(gitHub) == null)
            throw new McpToolException("The GitHub link should be a repository, like https://github.com/owner/name.");
        var description = Text("description", "description");
        var instructions = Text("instructions", "instructions");
        List<ShipStep>? steps = null;
        if (args.String("ship_steps") != null)
        {
            steps = args.Strings("ship_steps").Select(s => Enum.TryParse<ShipStep>(s.Trim(), true, out var step) ? step
                : throw new McpToolException("ship_steps can be commit, build, package, push and release.")).ToList();
            changed.Add("shipping steps");
        }
        var howTo = args.String("ship_how_to");
        if (howTo != null)
        {
            if (howTo.Length > MaxDetailLength) throw new McpToolException($"how to ship can be up to {MaxDetailLength} characters.");
            changed.Add("how to ship");
        }
        var testBuild = args.String("test_build")?.Trim();
        var testNote = args.String("test_build_note")?.Trim();
        if (testBuild is { Length: > 0 })
        {
            var full = new TestBuild { Path = testBuild }.FullPath(project.RepoPath);
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new McpToolException($"There's no file or folder {full} on the ProJict computer" +
                                           (Path.IsPathRooted(testBuild) || project.RepoPath != null ? "." : " (a relative path needs the project's repo folder)."));
        }
        if (testBuild != null) changed.Add("test build");
        else if (testNote != null) throw new McpToolException("Give test_build_note together with test_build.");
        if (changed.Count == 0)
            throw new McpToolException("Nothing to change: give repo_path, github_url, description, instructions, ship_steps, ship_how_to or test_build.");
        var ships = steps != null || howTo != null;
        if (ships)
        {
            try { ShipFlow.CheckAgentChange(project, steps); }
            catch (InvalidOperationException ex) { throw new McpToolException(ex.Message, ToolProblem.Conflict); }
        }

        var agent = session.AgentName;
        _main.GetProjectViewModel(project).AgentUpdateDetails(p =>
        {
            if (repoPath != null) p.RepoPath = repoPath.Length == 0 ? null : repoPath;
            if (gitHub != null) p.GitHubUrl = gitHub.Length == 0 ? null : gitHub;
            if (description != null) p.Description = description.Length == 0 ? null : description;
            if (instructions != null) p.PromptPreamble = instructions.Length == 0 ? null : instructions;
            if (ships) ShipFlow.AgentChange(p, steps, howTo, agent, DateTime.Now);
            if (testBuild != null)
                p.TestBuild = testBuild.Length == 0 ? null : new TestBuild { Path = testBuild, Note = testNote is { Length: > 0 } ? testNote : null, SetBy = agent, Agent = true };
        });
        _state.StatusText = $"{agent} changed {project.Name}'s {string.Join(", ", changed)}.";
        var result = new JsonObject { ["project"] = project.Name, ["changed"] = new JsonArray(changed.Select(c => (JsonNode)c).ToArray()) };
        if (ships) result["note"] = "Until the user checks your shipping change in Details › Shipping, every ship request needs their approval.";
        return Json(result);
    }

    private string SetLabel(McpSession session, Args args, List<Attachment> files)
    {
        // Older agents put their client's name in the label; the card shows that already.
        var label = AgentNaming.TrimLabel(args.String("label"), session.Client);
        if (label is { Length: > 40 }) throw new McpToolException("Keep the label short (40 characters at most).");
        session.Label = label;
        _main.Agents.Renamed();
        return Json(new JsonObject { ["label"] = session.Label, ["name"] = session.AgentName });
    }

    private string SetName(McpSession session, Args args, List<Attachment> files)
    {
        var name = args.Required("name").Trim().TrimStart('@');
        if (AgentNaming.Problem(name) is { } problem) throw new McpToolException(problem);
        if (string.Equals(name, _state.UserName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, session.Client, StringComparison.OrdinalIgnoreCase))
            throw new McpToolException($"{name} can't be your name; pick one of your own.");
        List<McpSession> others;
        lock (_live) others = _live.Where(s => s != session).ToList();
        if (others.Any(s => string.Equals(s.AgentName, name, StringComparison.OrdinalIgnoreCase)))
            throw new McpToolException($"{name} is taken by another agent connected now; pick another name.", ToolProblem.Conflict);
        var before = TakeName(session, name);
        session.RenamedBy = null;
        return Json(new JsonObject
        {
            ["name"] = session.AgentName,
            ["client"] = session.Client,
            ["note"] = before ? $"You were {session.AgentName} here before: your earlier work counts as yours." : $"Everything you do is now signed {session.AgentName}.",
            ["picture"] = PictureNote(session),
        });
    }

    /// <summary>
    /// The user renames a connected agent (the Agents view's list): as if it had picked the name with set_name; its next
    /// tool result tells it. Returns why it can't have that name, or null when it's done.
    /// </summary>
    public string? RenameAgent(McpSession session, string name)
    {
        name = name.Trim().TrimStart('@');
        if (string.Equals(name, session.AgentName, StringComparison.Ordinal)) return null;
        if (AgentNaming.Problem(name) is { } problem) return problem;
        if (string.Equals(name, _state.UserName, StringComparison.OrdinalIgnoreCase)) return "That's your own name; pick another one for the agent.";
        if (string.Equals(name, session.Client, StringComparison.OrdinalIgnoreCase)) return $"{name} is what it runs in; pick a name of its own.";
        List<McpSession> others;
        lock (_live) others = _live.Where(s => s != session).ToList();
        if (others.Any(s => string.Equals(s.AgentName, name, StringComparison.OrdinalIgnoreCase))) return $"{name} is taken by another agent connected now.";
        TakeName(session, name);
        session.RenamedBy = _state.UserName;
        return null;
    }

    /// <summary>
    /// Gives the session the name; what it has checked out and the channels it's in go with it, and an API token remembers
    /// it for the next time. True when the name was known here before.
    /// </summary>
    private bool TakeName(McpSession session, string name)
    {
        var known = _state.Settings.AgentNames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var before = known.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        var old = session.AgentName;
        var held = LocalProjects.SelectMany(p => p.Walk().Select(l => (Project: p, l.Item))).Where(x => Holds(session, x.Item)).ToList();
        // A name it picked before, or its token's.
        var former = session.Name ?? (string.IsNullOrWhiteSpace(session.SignAs) ? null : session.SignAs.Trim());
        if (former != null && !string.Equals(former, before ?? name, StringComparison.OrdinalIgnoreCase)) session.FormerNames.Add(former);
        session.Name = before ?? name;
        if (session.TokenId is { } tokenId && _main.ApiStore.Data.Tokens.FirstOrDefault(t => t.Id == tokenId) is { } token)
        {
            token.AgentName = session.Name;
            _main.ApiStore.Save();
        }
        foreach (var (project, item) in held) Edit(project, item.Id, i => i.ClaimedBy = session.AgentName);
        // A name of its own goes with all its channels; the client's default name ("Claude Code") only with those it joined
        // in this session: other sessions went by that name before (#97eefe11).
        Chat.RenameMember(old, session.AgentName, former == null ? session.ConnectedAt : null);
        known[session.AgentName] = session.Client;
        _state.MarkSettingsDirty();
        _main.Agents.Renamed();
        return before != null;
    }

    // ───────────── Items ─────────────

    private string ListItems(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var state = args.String("state") ?? "open";
        Func<TodoItem, bool> stateOk = state switch
        {
            "open" => i => !i.IsDone,
            "todo" => i => i.State == ItemState.Todo,
            "in_progress" => i => i.State == ItemState.InProgress,
            "done" => i => i.IsDone,
            "review" => i => i.NeedsReview,
            "all" => _ => true,
            _ => throw new McpToolException("state must be open, todo, in_progress, done, review or all."),
        };
        var tag = args.String("tag");
        var category = args.String("category");
        if (category != null && project.FindCategory(category) == null)
            throw new McpToolException($"No category \"{category}\" in {project.Name}. Categories: {string.Join(", ", project.Categories.Select(c => c.Name))}.", ToolProblem.NotFound);
        var terms = (args.String("query") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limit = Math.Clamp(args.Int("limit") ?? 50, 1, 500);
        Func<TodoItem, bool> priorityOk = PriorityArgument(args) switch
        {
            null => _ => true,
            Priority.None => i => i.Priority == Priority.None,
            Priority.Hold => i => i.Priority == Priority.Hold,
            Priority min => i => Priorities.AtLeast(i.Priority, min),
        };
        var includeOffLimits = args.Bool("include_off_limits") == true;
        var byPriority = args.String("sort") switch
        {
            null or "board" => false,
            "priority" => true,
            _ => throw new McpToolException("sort must be board or priority."),
        };

        var matches = project.Walk().Where(l =>
            stateOk(l.Item) && priorityOk(l.Item) &&
            (tag == null || string.Equals(l.Item.Tag, tag, StringComparison.OrdinalIgnoreCase)) &&
            (category == null || ProjectExtensions.CategoryNamesEqual(l.Category.Name, category)) &&
            terms.All(t => l.Item.SearchText().Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var rules = RulesFor(project);
        var offLimits = matches.Count(l => !MayWork(rules, l.Item));
        if (!includeOffLimits) matches = matches.Where(l => MayWork(rules, l.Item)).ToList();
        if (byPriority) matches = matches.OrderByDescending(l => Priorities.Rank(l.Item.Priority)).ToList();
        var result = new JsonObject
        {
            ["project"] = project.Name,
            ["state"] = state,
            ["total"] = matches.Count,
            ["items"] = new JsonArray(matches.Take(limit).Select(l => (JsonNode)ItemJson(l, details: false, rules)).ToArray()),
            ["truncated"] = matches.Count > limit,
        };
        if (offLimits > 0)
        {
            result["offLimits"] = offLimits;
            result["note"] = includeOffLimits
                ? $"{offLimits} of these are off limits to agents (offLimits: true): you may read and comment on them, but not work on them."
                : $"{offLimits} more matching item(s) are off limits to agents (the user's choice by priority, or on hold), so they aren't listed; include_off_limits=true lists them.";
        }
        return Json(result);
    }

    private string GetItem(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("id"));
        return Json(ItemDetails(project, loc));
    }

    private string ClaimItem(McpSession session, Args args, List<Attachment> files)
    {
        CheckAwake();
        var (project, loc) = FindItem(session, args.Required("id"));
        var item = loc.Item;
        if (item.IsDone) throw new McpToolException("This item is already done. Use update_item with state todo to reopen it first.", ToolProblem.Conflict);
        MayChange(project, Change.Edit);
        CheckMayWork(project, item);
        var takenFrom = CheckOut(session, project, item, args);
        var agent = SignAs(session, project);
        var note = args.String("note");
        Edit(project, item.Id, i =>
        {
            if (takenFrom != null)
            {
                i.AddActivity(agent, $"Took it over from {takenFrom}.", agent: true);
                i.StopWork(takenFrom);
            }
            if (i.State != ItemState.InProgress) i.SetState(ItemState.InProgress);
            // Its time on the item counts from now (alongside a person's who had it in progress).
            i.StartWork(agent, agent: true);
            i.ClaimedBy = agent;
            i.AddActivity(agent, string.IsNullOrWhiteSpace(note) ? "Checked it out." : "Checked it out: " + note.Trim(), agent: true);
        });
        session.Claimed.Add(item.Id);
        _state.StatusText = $"{agent} is working on “{Short(item.Text)}” ({project.Name}).";
        return Json(ItemDetails(project, project.Find(item.Id)!));
    }

    private string CompleteItem(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("id"));
        var resolution = args.Required("resolution").Trim();
        var (tokens, cost) = UsageArgs(args);
        MayChange(project, Change.Edit);
        CheckMayWork(project, loc.Item);
        var takenFrom = CheckOut(session, project, loc.Item, args);
        var agent = SignAs(session, project);
        Edit(project, loc.Item.Id, i =>
        {
            if (takenFrom != null) i.AddActivity(agent, $"Took it over from {takenFrom}.", agent: true);
            if (!i.IsDone) i.SetState(ItemState.Done);
            i.NeedsReview = true;
            i.AddActivity(agent, "Done: " + resolution, agent: true);
            WorkLog.Report(i, agent, tokens, cost);
            if (files.Count > 0) (i.Attachments ??= []).AddRange(files);
        });
        session.Claimed.Remove(loc.Item.Id);
        _state.StatusText = $"{agent} finished “{Short(loc.Item.Text)}” ({project.Name}); it waits for your review.";
        var result = new JsonObject { ["id"] = ShortId(loc.Item.Id), ["state"] = "done", ["needsReview"] = true, ["attached"] = files.Count };
        var open = loc.Item.Descendants().Count(d => !d.IsDone);
        if (open > 0) result["note"] = $"{open} sub-item(s) are still open; complete or update them separately.";
        return Json(result);
    }

    private string AddItem(McpSession session, Args args, List<Attachment> files)
    {
        var text = args.Required("text").Trim();
        // Every item an agent adds says how pressing it is, chosen on purpose ("none" too): asked before anything else
        // happens, such as checking out the parent.
        var priority = PriorityArgument(args) ?? throw new McpToolException(PriorityNeeded(
            args.String("parent") is { } under ? FindItem(session, under).Project : ProjectFor(session, args), subItem: args.String("parent") != null));
        Project project;
        Guid? parentId = null;
        if (args.String("parent") is { } parentText)
        {
            // A sub-item goes into its parent's project.
            var (parentProject, parentLoc) = FindItem(session, parentText);
            if (args.String("project") != null && ProjectFor(session, args) != parentProject) throw new McpToolException("The parent item is in another project.");
            MayChange(parentProject, Change.AddItems);
            CheckMayWork(parentProject, parentLoc.Item);
            CheckOut(session, parentProject, parentLoc.Item, args);
            project = parentProject;
            parentId = parentLoc.Item.Id;
        }
        else project = ProjectFor(session, args);
        MayChange(project, Change.AddItems);
        var category = args.String("category");
        if (parentId == null && string.IsNullOrWhiteSpace(category))
            throw new McpToolException($"Say which category (existing ones: {string.Join(", ", project.Categories.Select(c => c.Name))}), or give a parent item.");

        var (parsedTag, parsedText) = TxtListParser.SplitTag(text);
        var tag = args.String("tag") is { } explicitTag ? (explicitTag.Trim().Length == 0 ? null : explicitTag.Trim().ToUpperInvariant()) : parsedTag;
        var item = new TodoItem { Text = parsedTag != null && parsedText.Length > 0 ? parsedText : text, Tag = tag, Priority = priority };
        item.AddActivity(SignAs(session, project), "Added it.", agent: true);
        _main.GetProjectViewModel(project).AgentAdd(item, category, parentId);
        _state.StatusText = $"{SignAs(session, project)} added “{Short(item.Text)}” to {project.Name}.";
        return Json(ItemJson(project.Find(item.Id)!, details: false, RulesFor(project)));
    }

    /// <summary>add_item without a priority: the choices, what each means, and which the user lets agents work on.</summary>
    private string PriorityNeeded(Project project, bool subItem) =>
        "Choose the new item's priority (priority): urgent (needs doing now), high (next), medium (soon), low (when there's time), " +
        "hold (parked: agents leave it alone) or " + (subItem ? "none (it follows its parent's priority)." : "none (no priority: the user decides).") +
        $" In {project.Name}, the user lets AI agents work on {AgentAccess.Describe(RulesFor(project).Allowed) ?? "no items at the moment"}.";

    private string UpdateItem(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("id"));
        var text = args.String("text");
        var tag = args.String("tag");
        var comment = args.String("comment");
        var priority = PriorityArgument(args);
        ItemState? state = args.String("state") switch
        {
            null => null,
            "todo" => ItemState.Todo,
            "in_progress" => ItemState.InProgress,
            "done" => ItemState.Done,
            _ => throw new McpToolException("state must be todo, in_progress or done."),
        };
        if (state == ItemState.InProgress) CheckAwake();
        if (text == null && tag == null && priority == null && state == null && string.IsNullOrWhiteSpace(comment))
            throw new McpToolException("Nothing to change: give text, tag, priority, state or comment.");
        if (text != null && text.Trim().Length == 0) throw new McpToolException("The text can't be empty.");

        var agent = SignAs(session, project);
        var changes = text != null || tag != null || priority != null || state != null;
        MayChange(project, changes ? Change.Edit : Change.Comment);
        // Putting an item back is always allowed, e.g. one the user put on hold while you had it.
        var onlyPutsBack = text == null && tag == null && priority == null && state == ItemState.Todo;
        if (changes && !onlyPutsBack) CheckMayWork(project, loc.Item);
        var takenFrom = changes ? CheckOut(session, project, loc.Item, args) : null;
        if (changes)
        {
            Edit(project, loc.Item.Id, i =>
            {
                if (takenFrom != null) i.AddActivity(agent, $"Took it over from {takenFrom}.", agent: true);
                if (text != null && text.Trim() != i.Text)
                {
                    i.Text = text.Trim();
                    i.AddActivity(agent, "Changed the text.", agent: true);
                }
                var newTag = tag == null ? i.Tag : tag.Trim().Length == 0 ? null : tag.Trim().ToUpperInvariant();
                if (newTag != i.Tag)
                {
                    i.AddActivity(agent, newTag == null ? $"Removed the tag {i.Tag}." : $"Tagged it {newTag}.", agent: true);
                    i.Tag = newTag;
                }
                if (priority is { } p && p != i.Priority)
                {
                    i.AddActivity(agent, p == Priority.None ? "Removed the priority." : $"Set the priority to {p}.", agent: true);
                    i.Priority = p;
                }
                if (state is { } s && s != i.State)
                {
                    i.SetState(s, agent, agent: true);
                    if (s == ItemState.InProgress) i.ClaimedBy = agent;
                    if (s == ItemState.Done) i.NeedsReview = true;
                    i.AddActivity(agent, s switch { ItemState.Done => "Marked it done.", ItemState.InProgress => "Checked it out.", _ => "Put it back to do." }, agent: true);
                }
            });
            if (state == ItemState.InProgress) session.Claimed.Add(loc.Item.Id);
            else if (state != null) session.Claimed.Remove(loc.Item.Id);
        }
        if (!string.IsNullOrWhiteSpace(comment))
            _main.GetProjectViewModel(project).PostComment(loc.Item.Id, NewComment(session, project, comment), null);
        _state.StatusText = $"{agent} updated “{Short(loc.Item.Text)}” ({project.Name}).";
        return Json(ItemJson(project.Find(loc.Item.Id)!, details: false, RulesFor(project)));
    }

    private string AttachFile(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("id"));
        if (files.Count == 0) throw new McpToolException("Give at least one file in paths.");
        MayChange(project, Change.Edit);
        CheckMayWork(project, loc.Item);
        var takenFrom = CheckOut(session, project, loc.Item, args);
        var agent = SignAs(session, project);
        Edit(project, loc.Item.Id, i =>
        {
            if (takenFrom != null)
            {
                i.AddActivity(agent, $"Took it over from {takenFrom}.", agent: true);
                i.ClaimedBy = agent;
            }
            (i.Attachments ??= []).AddRange(files);
            i.AddActivity(agent, "Attached " + string.Join(", ", files.Select(f => f.FileName)) + ".", agent: true);
        });
        if (takenFrom != null) session.Claimed.Add(loc.Item.Id);
        _state.StatusText = $"{agent} attached {files.Count} file(s) to “{Short(loc.Item.Text)}”.";
        return Json(new JsonObject { ["id"] = ShortId(loc.Item.Id), ["attachments"] = Paths(project.Find(loc.Item.Id)!.Item.Attachments, project) });
    }

    /// <summary>
    /// Another agent's check-out blocks changes. The same agent may continue its own check-out, also from a new session
    /// once the old one is gone. With force=true it takes over: returns whose check-out it was, for the history.
    /// </summary>
    private string? CheckOut(McpSession session, Project project, TodoItem item, Args args)
    {
        if (item.State != ItemState.InProgress || item.ClaimedBy == null || Holds(session, item)) return null;
        McpSession? holder;
        lock (_live) holder = _live.FirstOrDefault(s => s != session && Holds(s, item));
        if (holder == null && OwnIn(session, project, item.ClaimedBy))
        {
            session.Claimed.Add(item.Id);
            // Its own check-out from an earlier session, under the name it had then ("Claude Code 2"): it carries on under this one.
            var mine = SignAs(session, project);
            if (!string.Equals(item.ClaimedBy, mine, StringComparison.OrdinalIgnoreCase)) Edit(project, item.Id, i => i.ClaimedBy = mine);
            return null;
        }
        if (args.Bool("force") == true) return item.ClaimedBy;
        throw new McpToolException(holder == null
            ? $"{item.ClaimedBy} has this item checked out but isn't connected any more. Pass force=true to take it over, or leave it for the user to release."
            : holder.AgentName == session.AgentName
                ? $"Another session signing as {session.AgentName} has this item checked out. Pick another item, or pass force=true to take it over."
                : $"{item.ClaimedBy} has this item checked out and is still connected. Talk to it or the user with add_comment, pick another item, or pass force=true to take it over.",
            ToolProblem.Conflict);
    }

    /// <summary>The priorities of items agents may work on (Settings › AI agents).</summary>
    private IReadOnlyCollection<Priority> AgentPriorities => _state.Settings.AgentPriorities;

    private static bool MayWork(AgentRules rules, TodoItem item) =>
        AgentAccess.Allows(rules.Allowed, rules.Governing.GetValueOrDefault(item));

    /// <summary>Refuses a change to an item the user (or a shared project's host) keeps off limits to agents; reading and commenting stay open.</summary>
    private void CheckMayWork(Project project, TodoItem item)
    {
        var rules = RulesFor(project);
        if (AgentAccess.Refusal(rules.Allowed, rules.Governing.GetValueOrDefault(item)) is { } why)
            throw new McpToolException(why + " You can still read it, and comment on it with add_comment (e.g. to ask the user).", ToolProblem.Forbidden);
    }

    /// <summary>The session checked the item out, and nobody (another agent, the user) changed that since.</summary>
    private static bool Holds(McpSession session, TodoItem item) =>
        session.Claimed.Contains(item.Id) && item.State == ItemState.InProgress && item.ClaimedBy is { } by &&
        (string.Equals(by, session.AgentName, StringComparison.OrdinalIgnoreCase) || AgentNaming.WithoutVia(by) is { } agent &&
         string.Equals(agent, session.AgentName, StringComparison.OrdinalIgnoreCase));

    // ───────────── Discussions ─────────────

    private sealed record CommentTarget(string Label, Project? Project, Guid? TargetId, string? Group, List<Comment>? Threads);

    private CommentTarget? TargetFor(McpSession session, Args args)
    {
        var given = new[] { "item", "category", "group" }.Count(n => args.String(n) != null);
        if (given > 1) throw new McpToolException("Give only one of item, category or group.");
        if (args.String("item") is { } id)
        {
            var (project, loc) = FindItem(session, id);
            return new CommentTarget($"item {ShortId(loc.Item.Id)} “{Short(loc.Item.Text)}” ({project.Name})", project, loc.Item.Id, null, loc.Item.Comments);
        }
        if (args.String("category") is { } name)
        {
            var project = ProjectFor(session, args);
            var category = project.FindCategory(name)
                ?? throw new McpToolException($"No category \"{name}\" in {project.Name}. Categories: {string.Join(", ", project.Categories.Select(c => c.Name))}.", ToolProblem.NotFound);
            return new CommentTarget($"category {category.Name} ({project.Name})", project, category.Id, null, category.Comments);
        }
        if (args.String("group") is { } group)
        {
            if (session.Projects != null)
                throw new McpToolException("This API token reaches only some projects, so group discussions are outside it.", ToolProblem.Forbidden);
            var match = _state.Settings.Groups.FirstOrDefault(g => string.Equals(g, group.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new McpToolException($"No group \"{group}\". Groups: {string.Join(", ", _state.Settings.Groups)}.", ToolProblem.NotFound);
            return new CommentTarget($"group {match}", null, null, match, _state.Settings.CommentsFor(match));
        }
        return null;
    }

    private string ListComments(McpSession session, Args args, List<Attachment> files)
    {
        if (TargetFor(session, args) is { } target)
        {
            return Json(new JsonObject
            {
                ["target"] = target.Label,
                ["threads"] = new JsonArray((target.Threads ?? []).Select(c => (JsonNode)CommentJson(c, nested: true, target.Project)).ToArray()),
            });
        }

        // The newest comments across a project: its items, its categories and its group.
        var project = ProjectFor(session, args);
        DateTime? since = args.String("since") is { } s
            ? DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t
                : throw new McpToolException("since must be a date and time like 2026-10-29 14:00.")
            : null;
        var limit = Math.Clamp(args.Int("limit") ?? 30, 1, 200);
        var all = new List<(Comment Comment, Comment? Parent, JsonObject Where)>();
        foreach (var loc in project.Walk())
            foreach (var (comment, parent) in loc.Item.Comments.Walk())
                all.Add((comment, parent, new JsonObject { ["item"] = ShortId(loc.Item.Id), ["itemText"] = Short(loc.Item.Text) }));
        foreach (var category in project.Categories)
            foreach (var (comment, parent) in category.Comments.Walk())
                all.Add((comment, parent, new JsonObject { ["category"] = category.Name }));
        // A shared project's group discussion stays with its host.
        if (!project.IsInbox && project.Remote == null)
            foreach (var (comment, parent) in _state.Settings.CommentsFor(project.Group).Walk())
                all.Add((comment, parent, new JsonObject { ["group"] = project.Group }));
        var recent = all.Where(e => !e.Comment.Deleted && (since == null || e.Comment.At > since)).OrderByDescending(e => e.Comment.At).ToList();
        return Json(new JsonObject
        {
            ["project"] = project.Name,
            ["total"] = recent.Count,
            ["comments"] = new JsonArray(recent.Take(limit).Select(e =>
            {
                var json = CommentJson(e.Comment, nested: false, project);
                json["on"] = e.Where;
                if (e.Parent != null) json["replyTo"] = ShortId(e.Parent.Id);
                return (JsonNode)json;
            }).ToArray()),
        });
    }

    private string AddComment(McpSession session, Args args, List<Attachment> files)
    {
        var text = args.Required("text").Trim();
        var target = TargetFor(session, args) ?? throw new McpToolException("Say what to comment on: item, category or group.");
        Guid? replyTo = null;
        if (args.String("reply_to") is { } replyText)
        {
            var matches = target.Threads.Walk().Select(e => e.Comment)
                .Where(c => c.Id.ToString().StartsWith(replyText.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            replyTo = matches.Count == 1 ? matches[0].Id
                : throw new McpToolException(matches.Count == 0 ? $"No comment {replyText} on this {target.Label}; list_comments shows the ids." : "Give more of the comment id.");
        }
        var limit = _state.Settings.CommentFileLimitBytes;
        if (files.FirstOrDefault(f => f.Size > limit) is { } big)
            throw new McpToolException($"{big.FileName} is {AttachmentViewModel.FormatSize(big.Size)}; files in comments can be up to {AttachmentViewModel.FormatSize(limit)} " +
                                       "(the user's setting). Attach it to the item instead (attach_file), or send a smaller file.");
        if (target.Project != null) MayChange(target.Project, Change.Comment);
        var comment = NewComment(session, target.Project, text);
        if (files.Count > 0) comment.Attachments = files;
        if (args.Strings("choices") is { Count: > 0 } choices)
        {
            try { comment.Choices = CommentChoices.Parse(choices); }
            catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        }
        if (target.Group != null) _state.PostGroupComment(target.Group, comment, replyTo);
        else _main.GetProjectViewModel(target.Project!).PostComment(target.TargetId!.Value, comment, replyTo);
        _state.StatusText = $"{comment.By} {(replyTo == null ? "commented on" : "replied on")} {target.Label}.";
        var result = new JsonObject { ["id"] = ShortId(comment.Id), ["on"] = target.Label };
        if (files.Count > 0) result["attached"] = files.Count;
        return Json(result);
    }

    private Comment NewComment(McpSession session, Project? project, string text) =>
        new() { By = project != null ? SignAs(session, project) : session.AgentName, Agent = true, Text = text.Trim() };

    // ───────────── Notes & releases ─────────────

    private string ReadDocs(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var title = args.String("title");
        if (title == null)
        {
            return Json(new JsonObject
            {
                ["project"] = project.Name,
                ["notes"] = new JsonArray(project.Notes.Select(n => (JsonNode)new JsonObject
                {
                    ["title"] = n.Title,
                    ["updated"] = n.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    ["updatedBy"] = n.UpdatedBy,
                    ["characters"] = n.Text.Length,
                    ["attachments"] = n.Attachments?.Count ?? 0,
                }).ToArray()),
            });
        }
        var candidates = project.Notes.Where(n => string.Equals(n.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) candidates = project.Notes.Where(n => n.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count != 1)
            throw new McpToolException(candidates.Count == 0
                ? $"No note \"{title}\" in {project.Name}. Notes: {string.Join(", ", project.Notes.Select(n => n.Title))}."
                : $"Several notes match \"{title}\": {string.Join(", ", candidates.Select(n => n.Title))}.",
                candidates.Count == 0 ? ToolProblem.NotFound : ToolProblem.Invalid);
        var note = candidates[0];
        return Json(new JsonObject { ["title"] = note.Title, ["updatedBy"] = note.UpdatedBy, ["text"] = note.Text, ["attachments"] = Paths(note.Attachments, project) });
    }

    private string WriteDoc(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var title = args.Required("title").Trim();
        var text = args.Required("text");
        var append = args.String("mode") switch
        {
            null or "replace" => false,
            "append" => true,
            _ => throw new McpToolException("mode must be replace or append."),
        };
        MayChange(project, Change.Edit);
        var note = _main.GetProjectViewModel(project).AgentWriteDoc(title, text, append, files, SignAs(session, project), out var created);
        _state.StatusText = $"{SignAs(session, project)} {(created ? "wrote" : "updated")} the note “{note.Title}” ({project.Name}).";
        return Json(new JsonObject
        {
            ["title"] = note.Title,
            ["created"] = created,
            ["characters"] = note.Text.Length,
            ["attachments"] = Paths(note.Attachments, project),
        });
    }

    private string DraftRelease(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var entries = PatchNotesBuilder.Candidates(project);
        if (entries.Count == 0) throw new McpToolException($"Nothing in {project.Name} was finished since its last release.");
        var version = args.String("version")?.Trim() is { Length: > 0 } v ? v : PatchNotesBuilder.SuggestNextVersion(project);
        var title = args.String("title");
        var markdown = PatchNotesBuilder.Build(project, version, DateTime.Now, title, entries, markdown: true);
        var unreviewed = entries.Count(e => e.Item.NeedsReview || e.Item.Descendants().Any(d => d.NeedsReview));
        var save = args.Bool("save") == true;
        if (save)
        {
            MayChange(project, Change.Edit);
            _main.GetProjectViewModel(project).AgentSaveRelease(version, title, entries, markdown, SignAs(session, project));
            _state.StatusText = $"{SignAs(session, project)} recorded release {version} of {project.Name}.";
        }
        var result = new JsonObject { ["version"] = version, ["items"] = entries.Count, ["saved"] = save, ["markdown"] = markdown };
        if (unreviewed > 0) result["note"] = $"{unreviewed} of these items still wait for the user's review.";
        return Json(result);
    }

    // ───────────── Shipping ─────────────

    private string ShipRequestInfo(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        var settings = ShipFlow.SettingsOf(project);
        var request = project.OpenShip;
        var json = new JsonObject
        {
            ["project"] = project.Name,
            ["request"] = request == null ? null : ShipJson(request),
            ["steps"] = new JsonArray(settings.Steps.Select(s => (JsonNode)ShipFlow.Name(s)).ToArray()),
            ["howTo"] = settings.HowTo ?? "(not written yet: find out from the repository, e.g. its build scripts and README, and say so in your plan)",
        };
        if (request == null && project.Ships?.LastOrDefault() is { } last) json["lastRequest"] = ShipJson(last);
        json["suggestedVersion"] = PatchNotesBuilder.SuggestNextVersion(project);
        json["finishedSinceLastRelease"] = PatchNotesBuilder.Candidates(project).Count;
        json["waitingForReview"] = new JsonArray(project.Walk().Where(l => l.Item.NeedsReview)
            .Select(l => (JsonNode)new JsonObject { ["id"] = ShortId(l.Item.Id), ["text"] = Short(l.Item.Text) }).ToArray());
        json["next"] = request?.State switch
        {
            null => "No ship request: only the user can ask for one (the Ship button in ProJict).",
            ShipState.Requested => "Prepare a plan following howTo (version, what's in it, the items still waiting for review, the commands you'll run) " +
                                   "and post it with plan_ship. Run nothing yet.",
            ShipState.Planned => "Your plan waits for the user's approval in ProJict. Run nothing yet; ask them to look at it if they're around.",
            _ => "Approved: run the steps following howTo, report each with report_ship, then finish_ship.",
        };
        return Json(json);
    }

    private static JsonObject ShipJson(ShipRequest request)
    {
        var json = new JsonObject
        {
            ["state"] = request.State.ToString().ToLowerInvariant(),
            ["steps"] = new JsonArray(request.Steps.Select(s => (JsonNode)ShipFlow.Name(s)).ToArray()),
            ["requestedBy"] = request.RequestedBy,
            ["requestedAt"] = request.RequestedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
        if (request.State == ShipState.Approved && request.DecidedBy == null) json["withoutApproval"] = true;
        if (request.Plan != null) json["plan"] = request.Plan;
        if (request.Version != null) json["version"] = request.Version;
        if (request.DecidedBy != null) json["decidedBy"] = request.DecidedBy;
        json["log"] = new JsonArray(request.Log.Select(e => (JsonNode)$"{e.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {e.By}: {e.Text}").ToArray());
        return json;
    }

    private string PlanShip(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        var request = OpenShip(project);
        var plan = args.Required("plan");
        ChangeShip(project, () => ShipFlow.Plan(request, plan, args.String("version"), session.AgentName));
        var waits = request.State == ShipState.Planned;
        _state.StatusText = waits ? $"{session.AgentName} planned shipping {project.Name}: it waits for your approval."
            : $"{session.AgentName} planned shipping {project.Name}.";
        return Json(new JsonObject
        {
            ["state"] = request.State.ToString().ToLowerInvariant(),
            ["next"] = waits ? "Wait for the user's approval (ship_request shows it); run nothing yet. If you tell them in Chat, the words Approve and Decline in your message become buttons for them."
                : "It runs without approval: go ahead, report each step with report_ship, then finish_ship.",
        });
    }

    private string ReportShip(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        var request = OpenShip(project);
        var step = Enum.TryParse<ShipStep>(args.Required("step"), ignoreCase: true, out var s) && Enum.IsDefined(s) ? s
            : throw new McpToolException("step must be commit, build, package, push or release.");
        var outcome = args.Required("outcome").Trim().ToLowerInvariant() switch
        {
            "done" => ShipFlow.Outcome.Done,
            "failed" => ShipFlow.Outcome.Failed,
            "skipped" => ShipFlow.Outcome.Skipped,
            _ => throw new McpToolException("outcome must be done, failed or skipped."),
        };
        ChangeShip(project, () => ShipFlow.Report(request, step, outcome, args.String("details"), session.AgentName));
        _state.StatusText = $"{session.AgentName}: {request.Log[^1].Text}";
        return Json(new JsonObject { ["state"] = request.State.ToString().ToLowerInvariant(), ["logged"] = request.Log[^1].Text });
    }

    private string FinishShip(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        MayChange(project, Change.HostOnly);
        var request = OpenShip(project);
        var shipped = args.Required("outcome").Trim().ToLowerInvariant() switch
        {
            "shipped" => true,
            "failed" => false,
            _ => throw new McpToolException("outcome must be shipped or failed."),
        };
        ChangeShip(project, () => ShipFlow.Finish(request, shipped, args.Required("summary"), session.AgentName));
        _state.StatusText = shipped ? $"{session.AgentName} shipped {project.Name}." : $"{session.AgentName} couldn't ship {project.Name}.";
        return Json(new JsonObject { ["state"] = request.State.ToString().ToLowerInvariant() });
    }

    private static ShipRequest OpenShip(Project project) =>
        project.OpenShip ?? throw new McpToolException($"{project.Name} has no ship request: only the user can ask for one (the Ship button in ProJict).", ToolProblem.NotFound);

    private void ChangeShip(Project project, Action change)
    {
        try { _main.GetProjectViewModel(project).ChangeShip(change); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message, ToolProblem.Conflict); }
    }

    // ───────────── Lookups ─────────────

    /// <summary>The project a call is about; the agent counts as working in it from then on.</summary>
    private Project ProjectFor(McpSession session, Args args)
    {
        var project = ResolveProject(session, args);
        _main.Agents.WorkingIn(session, project);
        return project;
    }

    private Project ResolveProject(McpSession session, Args args)
    {
        if (args.String("project") is { } name)
        {
            name = name.Trim();
            // Your own projects first, so a shared one with the same name doesn't take its place.
            var reachable = ReachableProjects.OrderBy(p => p.Remote != null).ToList();
            return reachable.FirstOrDefault(p => p.Id.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? reachable.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? reachable.FirstOrDefault(p => GitHubReleases.MatchKey(p.Name) == GitHubReleases.MatchKey(name))
                   ?? throw new McpToolException($"No project \"{name}\". Projects: {ProjectNames()}.", ToolProblem.NotFound);
        }
        return ProjectMatcher.ForFolder(LocalProjects, session.WorkingDirectory)?.Project
               ?? throw new McpToolException(session.Remote ? $"Say which project with \"project\". Projects: {ProjectNames()}."
                   : $"Say which project with \"project\" (no project matches your working directory). Projects: {ProjectNames()}.");
    }

    private (Project Project, ItemLocation Loc) FindItem(McpSession session, string id)
    {
        var found = FindItemIn(id);
        _main.Agents.WorkingIn(session, found.Project);
        return found;
    }

    private (Project Project, ItemLocation Loc) FindItemIn(string id)
    {
        id = id.Trim();
        var exact = Guid.TryParse(id, out var guid);
        if (!exact && id.Length < 6) throw new McpToolException("Give the item id (at least its first 6 characters).");
        var found = ReachableProjects
            .SelectMany(p => p.Walk().Select(l => (Project: p, Loc: l)))
            .Where(x => exact ? x.Loc.Item.Id == guid : x.Loc.Item.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new McpToolException($"No item with id {id}. Use list_items to find it.", ToolProblem.NotFound),
            _ => throw new McpToolException($"Several items start with {id}; give more of the id."),
        };
    }

    private static Priority? PriorityArgument(Args args) => args.String("priority") is { } text
        ? Priorities.Parse(text) ?? throw new McpToolException("priority must be urgent, high, medium, low, hold or none.")
        : null;

    private void Edit(Project project, Guid itemId, Action<TodoItem> change) =>
        _main.GetProjectViewModel(project).AgentEdit(itemId, change);

    private async Task<List<Attachment>> ImportFilesAsync(List<string> paths, string? workingDirectory)
    {
        var full = paths.Select(p => Path.GetFullPath(Path.IsPathRooted(p) || workingDirectory == null ? p : Path.Combine(workingDirectory, p))).ToList();
        var missing = full.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0) throw new McpToolException("File not found: " + string.Join(", ", missing));
        return await Task.Run(() => full.Select(_state.Store.ImportAttachment).ToList());
    }

    private async Task<List<Attachment>> StoreUploadsAsync(List<(string Name, byte[] Content)> uploads) =>
        await Task.Run(() => uploads.Select(u => _state.Store.WriteAttachment(u.Name, u.Content)).ToList());

    // ───────────── JSON ─────────────

    private JsonObject ProjectJson(Project p)
    {
        var stats = PortfolioStats.For(p);
        var json = new JsonObject { ["name"] = p.Name, ["key"] = p.Key };
        if (p.IsInbox) json["inbox"] = true;
        else
        {
            json["group"] = p.Group;
            json["status"] = p.Status.ToString();
        }
        if (p.Remote != null) json["sharedBy"] = SharedText(p);
        if (p.TestBuild is { } build)
            json["testBuild"] = $"{build.Path}{(build.Note != null ? $" ({build.Note})" : "")}, set by {build.SetBy} {build.SetAt:yyyy-MM-dd HH:mm}";
        // Only the user changes it (Details › Private); update_project has no say.
        if (p.Private) json["private"] = "only on this computer: people this ProJict shares with, and their agents, don't see it or its channels";
        if (!string.IsNullOrWhiteSpace(p.RepoPath)) json["repoPath"] = p.RepoPath;
        if (!string.IsNullOrWhiteSpace(p.GitHubUrl)) json["gitHub"] = p.GitHubUrl;
        json["open"] = stats.Open;
        json["inProgress"] = stats.InProgress;
        json["done"] = stats.Done;
        if (stats.ToReview > 0) json["toReview"] = stats.ToReview;
        if (p.OpenShip is { } ship) json["shipRequest"] = $"{ShipFlow.Describe(ship.Steps)}, {ShipFlow.StateName(ship.State)} (ship_request has it all)";
        if (p.OpenCleanup is { } cleanup) json["cleanupRequest"] = $"{CleanupFlow.StateName(cleanup.State)}: no other work here until it's decided (cleanup_request has it all)";
        return json;
    }

    /// <summary>The item for an agent; <paramref name="rules"/> mark what is off limits.</summary>
    private JsonObject ItemJson(ItemLocation loc, bool details, AgentRules rules)
    {
        var item = loc.Item;
        var json = new JsonObject
        {
            ["id"] = ShortId(item.Id),
            ["category"] = loc.Category.Name,
            ["text"] = item.Text,
            ["state"] = StateName(item.State),
        };
        if (item.Tag != null) json["tag"] = item.Tag;
        if (item.Priority != Priority.None) json["priority"] = item.Priority.ToString().ToLowerInvariant();
        if (loc.Parent != null) json["parent"] = ShortId(loc.Parent.Id);
        if (item.ClaimedBy != null && item.State == ItemState.InProgress) json["checkedOutBy"] = item.ClaimedBy;
        if (item.NeedsReview) json["needsReview"] = true;
        if (!MayWork(rules, item)) json["offLimits"] = true;
        if (item.HasAttachments) json["attachments"] = Paths(item.Attachments, rules.Project);
        if (item.Media is { Count: > 0 } media) json["media"] = MediaJson(media, rules.Project);
        if (item.Issue is { } issue)
        {
            var link = new JsonObject { ["number"] = issue.Number, ["url"] = issue.Url };
            if (issue.Closed) link["closed"] = true;
            if (issue.PullRequests is { Count: > 0 } pulls)
                link["pullRequests"] = new JsonArray(pulls.Select(p => (JsonNode)$"#{p.Number} {p.State.ToString().ToLowerInvariant()}: {p.Title}").ToArray());
            json["gitHubIssue"] = link;
        }
        if (details)
        {
            if (item.Activity is { Count: > 0 } log)
                json["history"] = new JsonArray(log.Select(a => (JsonNode)$"{a.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {a.By}: {a.Text}").ToArray());
            if (TimeText(item) is { } time) json["time"] = time;
            if (item.Comments is { Count: > 0 } threads)
                json["discussion"] = new JsonArray(threads.Select(c => (JsonNode)CommentJson(c, nested: true, rules.Project)).ToArray());
            if (item.Children.Count > 0)
                json["subItems"] = new JsonArray(item.Children.Select(c => (JsonNode)ChildJson(c, rules)).ToArray());
        }
        else
        {
            if (item.Children.Count > 0) json["subItems"] = item.Children.Count;
            if (item.Comments.CountComments() is > 0 and var count) json["comments"] = count;
        }
        return json;
    }

    private JsonObject ChildJson(TodoItem item, AgentRules rules)
    {
        var json = new JsonObject { ["id"] = ShortId(item.Id), ["text"] = item.Text, ["state"] = StateName(item.State) };
        if (item.Tag != null) json["tag"] = item.Tag;
        if (item.Priority != Priority.None) json["priority"] = item.Priority.ToString().ToLowerInvariant();
        if (!MayWork(rules, item)) json["offLimits"] = true;
        if (item.HasAttachments) json["attachments"] = Paths(item.Attachments, rules.Project);
        if (item.Comments.CountComments() is > 0 and var count) json["comments"] = count;
        if (item.Children.Count > 0) json["subItems"] = new JsonArray(item.Children.Select(c => (JsonNode)ChildJson(c, rules)).ToArray());
        return json;
    }

    private JsonObject CommentJson(Comment comment, bool nested, Project? project = null)
    {
        var json = new JsonObject
        {
            ["id"] = ShortId(comment.Id),
            ["by"] = comment.By,
            ["at"] = comment.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["text"] = comment.Deleted ? "(deleted)" : comment.Text,
        };
        if (comment.Agent) json["agent"] = true;
        if (comment.Attachments is { Count: > 0 } posted) json["attachments"] = Paths(posted, project);
        if (comment.Choices is { Count: > 0 } choices)
            json["choices"] = new JsonArray(choices.Select(c =>
            {
                var choice = new JsonObject { ["question"] = c.Question, ["options"] = new JsonArray(c.Options.Select(o => (JsonNode)o).ToArray()) };
                if (c.Answer != null) choice["answer"] = c.Answer;
                return (JsonNode)choice;
            }).ToArray());
        if (nested && comment.Replies is { Count: > 0 } replies)
            json["replies"] = new JsonArray(replies.Select(r => (JsonNode)CommentJson(r, nested: true, project)).ToArray());
        return json;
    }

    private JsonObject ItemDetails(Project project, ItemLocation loc)
    {
        var rules = RulesFor(project);
        var json = ItemJson(loc, details: true, rules);
        // Why, instead of just true, so the agent can tell the user.
        if (AgentAccess.Refusal(rules.Allowed, rules.Governing.GetValueOrDefault(loc.Item)) is { } why) json["offLimits"] = why;
        var context = new JsonObject { ["name"] = project.Name };
        if (project.Remote != null) context["sharedBy"] = SharedText(project);
        if (!string.IsNullOrWhiteSpace(project.RepoPath)) context["repoPath"] = project.RepoPath;
        if (!string.IsNullOrWhiteSpace(project.GitHubUrl)) context["gitHub"] = project.GitHubUrl;
        if (!string.IsNullOrWhiteSpace(project.PromptPreamble)) context["instructions"] = project.PromptPreamble.Trim();
        json["project"] = context;
        return json;
    }

    /// <summary>
    /// Where the agent finds attached files: paths on this computer (a shared project's files are fetched from its host
    /// as they're listed, so they're there a moment later), or download links over the API.
    /// </summary>
    private JsonArray Paths(List<Attachment>? attachments, Project? project = null)
    {
        if (project?.Remote != null)
            foreach (var a in attachments ?? [])
                if (!File.Exists(_state.AttachmentPath(project, a))) _main.Guest.Fetch(project, a);
        return new((attachments ?? []).Select(a => _caller is { Remote: true } remote
            ? new JsonObject { ["name"] = a.FileName, ["size"] = a.Size, ["url"] = $"{remote.BaseUrl}{ApiRoutes.Base}/attachments/{a.Id:N}" }
            : (JsonNode)_state.AttachmentPath(project, a)).ToArray());
    }

    private string ProjectNames() =>
        string.Join(", ", ReachableProjects.Where(p => !p.IsInbox).Select(p => p.Remote is { } r ? $"{p.Name} (shared by {r.HostName})" : p.Name));

    /// <summary>"Sam's ProJict, where you work through Alice (an editor): you may comment and change items".</summary>
    private string SharedText(Project project)
    {
        var remote = project.Remote!;
        var host = HostOf(project);
        var may = host is not { State: ConnectionState.Connected } ? "it's not connected right now, so you can only read it"
            : !host.AgentsAllowed ? "its owner doesn't let guests' agents change anything, so you can only read it"
            : GuestAccess.CanEdit(host.Role) ? "you may comment, add items and work on items its agent rules allow"
            : GuestAccess.CanAddItems(host.Role) ? "you may comment and add items"
            : GuestAccess.CanComment(host.Role) ? "you may comment"
            : "you can only read it";
        if (host is { IsSelf: true })
            return $"{remote.HostName}'s ProJict, where this computer is {remote.HostName} too: {may}; what you do there is signed with your own name.";
        return $"{remote.HostName}'s ProJict, where you work through {GuestNameIn(project)} ({(host?.Role ?? remote.Role).ToString().ToLowerInvariant()}): {may}; " +
               $"what you do there is signed {AgentNaming.Via("<your name>", GuestNameIn(project))}.";
    }

    // The agent reads this text as is, so letters like č or ž stay readable instead of becoming č.
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(JsonObject json) => json.ToJsonString(ReadableJson);

    private static string ShortId(Guid id) => id.ToString()[..8];

    private static string StateName(ItemState state) => state switch
    {
        ItemState.InProgress => "in_progress",
        ItemState.Done => "done",
        _ => "todo",
    };

    private static string Short(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    // ───────────── Tool definitions ─────────────

    private static readonly (string, string, string, bool) ProjectArg =
        ("project", "string", "Project name or key; defaults to the project matching your working directory.", false);
    private static readonly (string, string, string, bool) IdArg =
        ("id", "string", "Item id (the first 8 characters are enough).", true);
    private static readonly (string, string, string, bool) PriorityArg =
        ("priority", "enum:urgent|high|medium|low|hold|none", "How pressing it is; hold parks it (agents then leave it alone), none removes the priority.", false);
    private static readonly (string, string, string, bool) ForceArg =
        ("force", "boolean", "Take the item over from another agent that has it checked out (recorded in its history).", false);

    private void Add(string name, string title, string description, JsonObject schema, Handler run, bool readOnly = false, string? filesArgument = null)
    {
        _tools.Add(new McpTool(name, title, description, schema, readOnly));
        _handlers[name] = (run, filesArgument);
    }

    /// <summary>A tool whose work waits on something (ProJict's browser): it starts on the UI thread and its waits come back there.</summary>
    private void AddAsync(string name, string title, string description, JsonObject schema, AsyncHandler run, bool readOnly = false)
    {
        _tools.Add(new McpTool(name, title, description, schema, readOnly));
        _asyncHandlers[name] = run;
    }

    /// <summary>Types: string, integer, boolean, string[] or enum:a|b|c.</summary>
    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, type, description, _) in properties)
        {
            JsonObject property = type switch
            {
                "string[]" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                _ when type.StartsWith("enum:", StringComparison.Ordinal) => new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(type[5..].Split('|').Select(v => (JsonNode)v).ToArray()),
                },
                _ => new JsonObject { ["type"] = type },
            };
            property["description"] = description;
            props[name] = property;
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        var required = properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }

    /// <summary>Tool arguments, tolerant of numbers or booleans sent as strings.</summary>
    private readonly struct Args(JsonElement json)
    {
        private JsonElement? Get(string name) =>
            json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

        public string? String(string name) => Get(name) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

        public string Required(string name) =>
            String(name) is { } s && s.Trim().Length > 0 ? s : throw new McpToolException($"\"{name}\" is required.");

        public int? Int(string name)
        {
            if (Get(name) is not { } v) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            return int.TryParse(v.ToString(), out n) ? n : null;
        }

        public bool? Bool(string name) => Get(name) is { } v
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => bool.TryParse(v.ToString(), out var b) ? b : null,
            }
            : null;

        public List<string> Strings(string name) => Get(name) is not { } v ? []
            : v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.ToString()).Where(s => s.Trim().Length > 0).ToList()
            : [v.ToString()];

        /// <summary>Files sent over the API: objects with a name and the content in base64.</summary>
        public List<(string Name, byte[] Content)> Uploads(string name)
        {
            if (Get(name) is not { } v) return [];
            var files = new List<(string, byte[])>();
            foreach (var e in v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [v])
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String ||
                    !e.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String)
                    throw new McpToolException($"Over the API, send each file in \"{name}\" as an object with \"name\" and \"data\" (the content in base64); " +
                                               "paths on the ProJict computer can't be used.");
                try { files.Add((n.GetString()!, Convert.FromBase64String(d.GetString()!))); }
                catch (FormatException) { throw new McpToolException($"The data of {n.GetString()} isn't valid base64."); }
            }
            return files;
        }
    }
}
