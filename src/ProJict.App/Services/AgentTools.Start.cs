using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Getting an agent started (#5b549380). Clients show only so much of what a server tells them when they connect (Claude
/// Code cuts it at 4,096 characters, with "… [truncated]"), and ProJict's whole guide is longer than that, so agents
/// never read its end: Chat, what waits for them, the user's instructions for all agents. So the instructions are a short
/// core that fits: what ProJict is, the first steps, and what matters on every call. get_started gives the rest, for the
/// agent that asks: its name, its project and instructions, what it may work on, the channel to join, how to keep
/// hearing Chat, and the whole guide by topic.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>
    /// The most the instructions may be (Claude Code shows 4,096 characters of them): the user's instructions for all
    /// agents go with them only while they fit, and otherwise come with get_started.
    /// </summary>
    public const int InstructionsLimit = 3900;

    private void AddStartTools()
    {
        Add("get_started", "Get started",
            "Call it first, when you start or are told to join ProJict, and again whenever you want to look something up: your name here, " +
            "your project and its instructions, the user's instructions for all agents, which items you may work on, the channel to join, " +
            "how to keep hearing Chat, and the whole guide to ProJict by topic (items, chat, approvals, shared machines, helpers, wiki, " +
            "shipping, studios, publishing, outside tools, clean-ups, sharing). What waits for you comes with it, as with every call.",
            Schema(("project", "string", "Project name or key; defaults to the project matching your working directory.", false)),
            GetStarted, readOnly: true, output: GetStartedOutput);
    }

    public string Instructions => InstructionsFor(remote: false);

    /// <summary>
    /// What agents are told when they connect: the core, and the user's instructions for all agents while everything
    /// fits in <see cref="InstructionsLimit"/> with <paramref name="more"/> characters the API adds after it.
    /// </summary>
    public string InstructionsFor(bool remote, int more = 0)
    {
        var core = CoreInstructions;
        if (ForAllAgents is not { } mine) return core;
        var whole = core + " The user's instructions for all agents, in every project (a project's own instructions add to them): " + mine;
        return whole.Length + more <= InstructionsLimit ? whole
            : core + " The user has instructions for all agents: get_started (and current_project) gives them. Read them before you do anything else.";
    }

    private const string CoreInstructions =
        "ProJict is the user's to-do list app, where they and AI agents work together. Projects hold categories of items; an item has a state " +
        "(todo, in_progress or done), perhaps a tag such as BUG or ADD, sub-items, attachments and a discussion. People and agents also talk in Chat. " +
        "To start, or when you're told to join ProJict: pick a short name of your own with set_name (one word, e.g. Ada; the one you had " +
        "before if you worked here, so your earlier work stays yours), then call get_started and read all of it: your project and its " +
        "instructions, the user's instructions for all agents, what you may work on, what waits for you, the channel to join, how to keep " +
        "hearing Chat, and the whole guide (shipping, studios, publishing, outside tools, clean-ups, sharing and more). " +
        "Working on items: list_items shows what's open for you; claim_item checks one out before you start (other agents then can't change " +
        "it); complete_item marks it done with a short resolution and evidence such as screenshots once it's really finished, for the user to " +
        "review. If you stop, update_item with state todo. The user decides by priority which items agents may work on: the others, and items " +
        "on hold, are off limits (read and comment only). File problems you notice with add_item, choosing each one's priority on purpose. Ask " +
        "the user or report progress with add_comment, and read their answers with list_comments. Tell the user about a longer step (tests, a " +
        "build) with set_status. Before anything that needs the user's go-ahead (a commit, a push, spending), ask with ask_approval and wait: " +
        "only its answer counts as approval, not a 👍 on another message. " +
        "What waits for you comes first: a tool result may list it in waitingForYou (chat invitations, @mentions and private messages, the " +
        "user's comments on your work, work sent back, ship and clean-up requests). Deal with each before going on, or say why not. " +
        "Chat has #channels and private conversations (channel @name): list_channels, join_channel, read_channel and post_message. If a " +
        "person's message could be for several agents, claim_reply it before answering, and leave it if another agent has it. Write like " +
        "a person in a chat: a few short sentences, one point at a time; details go in an item's discussion or a note (write_doc). Between " +
        "tool calls nothing reaches you, so if your client can run a command in the background and tell you about each line it prints " +
        "(Claude Code's Monitor tool, for one), keep a chat watcher running: get_started says how. " +
        "When a result says asleep, finish what you're doing, then stay idle until you're told you're woken. Ids may be shortened to their " +
        "first 8 characters.";

    private string GetStarted(McpSession session, Args args, List<Attachment> files)
    {
        var named = session.Name != null;
        var name = session.AgentName;
        Project? project;
        string? matchedBy = null, noProject = null;
        if (args.String("project") != null) (project, matchedBy) = (ResolveProject(session, args), "name");
        else if (ProjectMatcher.ForFolder(LocalProjects, session.WorkingDirectory) is { } match) (project, matchedBy) = (match.Project, match.How);
        else
        {
            project = null;
            noProject = (session.Remote ? "Say which project with \"project\"" : "No project matches your working directory: pass \"project\"") +
                        $" (projects: {ProjectNames()}), or ask the user to set the project's repo folder in its Details tab.";
        }
        if (project != null) _main.Agents.WorkingIn(session, project);
        // The watcher's command is in the steps below: no need for the tip as well.
        session.Noticed.Add("tip:watch-chat");

        var json = new JsonObject
        {
            ["you"] = new JsonObject
            {
                ["name"] = name,
                ["client"] = session.Client,
                ["note"] = named ? $"Everything you do is signed {name}." : $"You go by {name} until you pick a name of your own with set_name.",
            },
        };

        var steps = new JsonArray();
        void Step(string step, bool? done, string? how = null)
        {
            var line = new JsonObject { ["step"] = step };
            if (done != null) line["done"] = done;
            if (done != true && how != null) line["how"] = how;
            steps.Add(line);
        }
        Step("Pick your name", named, "set_name with one word of your own, e.g. Ada: the one you had before if you worked here, so your earlier work stays yours.");
        Step("Find your project", project != null, noProject);
        var channel = project is { Remote: null } ? ProjectChannel(session, project) : null;
        if (channel != null)
            Step($"Join #{channel.Name}, the project's channel", ChatRoom.Member(channel, name) != null,
                $"join_channel #{channel.Name}, then read_channel to catch up and say hello.");
        Step("Keep hearing Chat", IsWatched(name), WatchHow(session));
        Step("Deal with what waits for you first", null, "It comes in waitingForYou, in this and every result: answer, pick up work sent back, or say why not now.");
        if (project != null)
        {
            var rules = RulesFor(project);
            var open = project.Walk().Count(l => !l.Item.IsDone && MayWork(rules, l.Item));
            Step("Pick your work", null, open > 0
                ? $"list_items: {open} open item(s) in {project.Name} you may work on; claim_item the one you take."
                : $"Nothing in {project.Name} is open to agents right now (the user decides by priority): you can still read, comment and add items.");
        }
        json["steps"] = steps;

        if (project != null) json["project"] = ProjectBrief(session, project, matchedBy!);
        else if (ForAllAgents is { } forAll) json["instructionsForAllAgents"] = forAll;
        json["guide"] = Guide(session);
        return Json(json);
    }

    /// <summary>The channel about a project as a whole (not one of its categories or items) the agent can see; the oldest if there are several.</summary>
    private ChatChannel? ProjectChannel(McpSession session, Project project) =>
        Chat.Visible(ActorOf(session))
            .Where(c => !c.Direct && c.Link is { CategoryId: null, ItemId: null } link && link.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefault();

    /// <summary>How the agent keeps hearing Chat: the watcher command with its name, or the API's watch.</summary>
    private string WatchHow(McpSession session) => session.Remote
        ? $"Keep GET {ProJict.Core.Api.ApiRoutes.Base}/chat/watch running in the background with your Authorization header (e.g. curl -sN, under " +
          "Claude Code's Monitor tool): it prints a line for each chat message for you, as it comes. Start it again each time it ends."
        : (session.Name == null ? "Once you've picked your name, run " : "Run ") + WatchCommand(session.Name ?? "<your name>") +
          " in the background (Claude Code: the Monitor tool, with its longest timeout): it prints a line for each chat message for you, as it comes. " +
          "Start it again each time it ends (Claude Code's Monitor ends after 30 minutes), or you stop hearing Chat.";

    /// <summary>The whole guide, by topic: what the instructions were before they had to fit.</summary>
    private JsonObject Guide(McpSession session) => new()
    {
        ["items"] =
            "Projects contain categories, categories contain items (with sub-items, tags such as BUG or ADD, attachments, discussions, and a state: " +
            "todo, in_progress or done). To work on items: current_project (or list_projects) to find the project, list_items to see what is open, " +
            "claim_item to check an item out before you start (other agents then can't change it), then complete_item with a short resolution and " +
            "evidence such as screenshots (and the tokens and cost you used, if you can tell; ProJict counts your time from the check-out). Everything " +
            "you complete waits for the user's review, so only complete what you really finished; use update_item with state todo if you stop. The " +
            "user decides which items agents may work on, by priority (current_project says which); the others and items on hold are off limits: " +
            "list_items leaves them out, and you may read and comment on them but not check them out, change or complete them. Ask the user " +
            "questions or report progress with add_comment (offering options? give them as choices, which the user answers with a click), and check " +
            "list_comments for their answers. File problems you notice with add_item, choosing each one's priority, and put documentation or reports " +
            "for people in a note with write_doc. Items with a gitHubIssue stand for that GitHub issue: mention \"Fixes #<number>\" in the commit or " +
            "pull request that resolves one.",
        ["you"] =
            "Everything you do is signed with your name: pick a short name of your own with set_name (one word, e.g. Ada), the same one as before " +
            "if you had one here, so your earlier work stays yours; set_label says what you're working on. When you start a longer step (running " +
            "tests, a build, a bigger change), tell the user with set_status; it shows live in ProJict. If you can tell how much of your usage " +
            "you've used, say so with report_usage. When a result says asleep, the user put every agent to sleep: finish what you're doing, then " +
            "stop and stay idle until you're told you're woken (claim_item is refused meanwhile).",
        ["waitingForYou"] =
            "What waits for you shows in a tool result's waitingForYou list, each thing once: chat invitations, @mentions and new messages, private " +
            "ones included (read them with read_channel and reply with post_message: say hello when invited, and answer what you're asked, or say " +
            "you can't now), the user's comments on items you worked on (answer with add_comment), work the user sent back (get_item for why, then " +
            "claim_item to work on it again), ship requests and clean-up requests in your project. Deal with them before going on, or say why not.",
        ["chat"] =
            "The user and agents also talk live in Chat #channels (list_channels, join_channel to read one, read_channel, post_message, " +
            "create_channel), and privately: post_message to channel @name writes to the user or an agent only. Only one agent answers a " +
            "person's message: one that @mentions only you is yours; otherwise claim_reply it first, and if another agent has it, leave it to them " +
            "(you're told who's answering what). Keep Chat human. " + ChatStyle +
            " To listen, call wait_for_messages. Between tool calls nothing reaches you, so if your client can run a command in the background " +
            "and tell you about each line it prints (Claude Code's Monitor tool, for one), keep a chat watcher running. " + WatchHow(session),
        ["approvals"] =
            "To get the user's go-ahead for one change (a commit, a push, deleting or spending something), ask with ask_approval in Chat, naming " +
            "exactly that change; the message gets Approve and Decline. Do nothing of it before you're told \"approved\" (in a result, or [approval] " +
            "from your chat watcher). A 👍 on any other message is only agreement, not approval. One change per question.",
        ["sharedMachines"] =
            "Shared things only one agent should use at a time (a test PC, a GPU, a game install, a device) are claimed with claim_resource, by " +
            "the name others use (list_resources shows them), for about how long you need it; claim it again to extend it, and release_resource " +
            "as soon as you're done. If another agent has it, you're told who, what for and until when, and you're told when it's free: don't " +
            "use it meanwhile.",
        ["helpers"] =
            "A lead agent whose subagents share its connection gives each its own session with start_session (with the subagent's name): every " +
            "call made as that subagent passes the token it returns as \"session\", so its check-outs, status and private messages are its own. " +
            "Keep the token secret; only this connection can use it.",
        ["projectDetails"] =
            "Keep a project's details (repo folder, GitHub link, description, instructions, how it's shipped) up to date with update_project, and " +
            "set its test_build when you make a build worth trying (the user runs it from the project page).",
        ["wiki"] =
            "Knowledge that lasts (architecture, how things work, decisions) goes in the wiki: list_wiki, read_wiki and write_wiki (a whole page, or " +
            "a section by heading, in Markdown with [[Page name]] links); pass the version you read so your change merges with others', and what " +
            "you write waits for the user's review.",
        ["shipping"] =
            "When a project shows a shipRequest, the user wants it committed, built, packaged, pushed or released: read it with ship_request, post " +
            "your plan with plan_ship and wait for their approval in ProJict; only then run the steps, report each with report_ship and end with " +
            "finish_ship (record a published release with draft_release save=true).",
        ["publishing"] =
            "Videos (a film studio's Publisher) are published through a publish plan: post it with plan_publish (title, description, tags, " +
            "playlists, when, and 2–4 thumbnails to choose from) and wait for the user's decision; publish only an approved plan, with the text and " +
            "thumbnail it then has (publish_plan), and end with finish_publish.",
        ["studios"] =
            "When a project shows a studio (a game or film studio; it can have both), the user hires agents to its roles: if you have one, work as " +
            "it expects, and agree with the team in the studio's channel who takes what, volunteering for what suits your model and tools. A role's " +
            "first agent is its senior, the others juniors who agree with it what they take. If you're its lead, you hire too: talk with the agents " +
            "not hired there yet first, then hire_to_role. And if the user lets the lead review wiki pages, you're told which pages wait for you: " +
            "accept them or send them back with review_wiki.",
        ["outsideTools"] =
            "Outside tools the user set up for agents (such as ElevenLabs, Suno, or ffmpeg on this computer) are in list_tools; use_tool_key gets " +
            "one's API key once the user allows it, browser_open opens a tool used in the browser in ProJict's own browser (where the user signed " +
            "in), and run_workflow runs a workflow on the user's ComfyUI server, attaching what it makes to an item.",
        ["cleanups"] =
            "When a project shows a cleanupRequest, the user wants its repo folder cleaned up: read it with cleanup_request, scan the folder and post " +
            "your plan with plan_cleanup; ProJict moves what they approve to the Recycle Bin, so never remove files yourself, and do no other work " +
            "there until it's decided.",
        ["sharing"] =
            "Projects another ProJict shares with this one are reachable too, and so are its #channels (named like #hud (Sam's)): list_projects " +
            "says what you may do there; what you do there is signed with your name and the user's, like Ada (via Alice).",
    };
}
