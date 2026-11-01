# ProJict

Project to-do lists for Windows. Each project has categories, and when you finish an item it moves to that category's **DONE** list. You still get a clear list of what's left and a record of what's done.

## Download

Get **`ProJict-Setup-x.y.z.exe`** from [Releases](https://github.com/preseznik/projict/releases/latest) and run it.

- Windows 10 or 11 (64-bit).
- Installs for your user account only, so it needs no administrator rights.
- ProJict checks for a new version every time it starts, and every 4 hours while it's open. It can install it for you, from the bar that appears or from **Settings → Updates**.

## Getting started

1. On first start, choose the folder where ProJict keeps your projects.
2. Create a project with **+** at the bottom of the sidebar, or bring in existing text lists with **Import** (see below).
3. Type into **Add item…** under a category and press Enter. Check an item off and it moves to the category's DONE list.

## Keyboard

| Keys | Action |
| --- | --- |
| Enter | new item below |
| Shift+Enter | new line inside an item |
| Tab / Shift+Tab | make it a sub-item / move it back out |
| Ctrl+Enter | mark done / not done |
| Ctrl+I | mark in progress |
| Alt+1 … Alt+4 / Alt+5 / Alt+0 | priority urgent … low / on hold / no priority |
| Alt+↑ / Alt+↓ | move the item up or down |
| Ctrl+Shift+Del | delete the item |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+F | filter the current project |
| Ctrl+K | search all projects |
| Ctrl+Space | select items for **Copy prompt** |
| Ctrl+V | with a screenshot or files copied: attach them to the item |

Right-click an item, or click the **⋯** that appears on its right when you point at it, for everything else you can do with it: priority, tag, discussion, attachments, moving it and more.

Start an item with `BUG:`, `ADD:`, `ISSUE:` or `CHANGE:` to tag it. Right-click an item's checkbox to mark it as to do, in progress or done. Drag items by their handle to reorder them or move them to another category.

Next to the filter box, **All tags** shows only items with one tag and **All states** shows only items that are to do, in progress or done. **Working on** shows only the items someone is on: checked out by an AI agent or marked in progress. **Hide empty** hides the categories with nothing left to do (only done items, or none) until you click **Show all**.

Give an item a priority (urgent, high, medium or low) from its right-click menu or with Alt+1 to Alt+4. **On hold** (Alt+5) parks an item: it sorts last, and AI agents leave it alone. The **Priority** button above the board sorts open items by priority and can show only items of some priority (e.g. high and up, or on hold). Sorting only changes what you see: your own order stays and is what exports use. While the board is sorted, items can't be dragged or moved.

## Text files

ProJict reads and writes simple text lists like this:

```
HUD
- an open item
	- a sub-item
-+ an item in progress
+ a finished item
```

- **Import** reads `TODO-<name>.txt`, `DONE-<name>.txt`, `WORK-<name>.txt` and similar files from a folder. It shows a preview first and never changes the files. The original text stays available in each project's **Archive** tab.
- **Export** writes `TODO-<name>.txt` and `DONE-<name>.txt`. Under **Settings → TXT mirror** ProJict can keep a folder of these files up to date automatically. It never overwrites files it didn't create without asking.

## AI agents

AI coding agents such as Claude Code can work in ProJict through the Model Context Protocol (MCP). An agent finds the project for the folder it works in, checks out an item, and completes it with a short resolution and screenshots. Agents and you can talk it over in the item's discussion.

**Set up:** open **Settings → AI agents → Connect an AI agent**, pick your agent (Claude Code, OpenAI Codex, OpenCode, or Claude Desktop and other MCP clients) and copy its setup. For Claude Code it's one command to run in a terminal, like this:

```
claude mcp add --scope user projict -- "C:\Users\<you>\AppData\Local\Programs\ProJict\ProJict.exe" --mcp
```

Other MCP clients run the same program with `--mcp`. If ProJict isn't running, it starts in the tray.

- **What agents can do:** list projects and items, check out an item, complete it with a resolution and files, add items and sub-items, attach files, comment, write notes (for example a status report), draft release notes, keep a project's details up to date (repo folder, GitHub link, description, instructions, and how it's shipped), and talk with you and each other in [Chat](#chat). They can't delete anything, the board updates as they work, and Ctrl+Z undoes their changes.
- **Which items:** agents work only on items you give a priority (urgent, high, medium or low) by default; choose which under **Settings → AI agents**, including items without a priority. Items on hold are always left alone. Agents still see and can comment on the other items, for example to ask you, but can't check them out, change or complete them. A sub-item without a priority of its own goes by the item it belongs to.
- **Check-out:** while an agent has an item checked out (its name shows on the item), other agents can't change it, only comment on it. Taking it over is possible but is written to the item's history. If an agent stops halfway, right-click the item › **Release check-out**. You can always edit any item yourself.
- **Signed work:** every change, comment, note and release records who made it: you (the name in **Settings → Comments & history**) or the agent. Under each item, a chip names everyone who worked on it; hover it for what they did, click it to show only their items.
- **Agents' names:** each agent picks a short name of its own when it starts (for example Ada or Quill) and signs everything with it. The Agents view and Chat show what it runs in next to the name ("Ada · Claude Code"). Until it picks one, it goes by its client's name, numbered when several run at once ("Claude Code 2"). A session that takes a name it had before gets its earlier work back: your comments and work you sent back reach it, not every Claude Code session. An agent's check-outs and chat channels move with it when it picks a name. API tokens sign with the name you gave them.
- **Review:** an item an agent completes gets a **review** chip. Check the work, then click the chip to accept it, or mark the item to do to send it back (its history says "Sent it back"). The dashboard lists everything waiting for review and what agents did recently, and **All states → Needs review** filters the board.
- **What waits for an agent:** an agent doesn't have to go looking. Each of its tool results lists, once each, what waits for it: chat invitations, @mentions and new messages in its channels, your comments on items it worked on, work you sent back, and ship requests in its project. ProJict's instructions ask agents to deal with these first: reply in Chat (say hello when invited, answer what they're asked), answer your comments, and pick up work you sent back.
- **Who's working where:** while an agent is connected, a badge with its initials shows next to each project it works in, in the sidebar and on the dashboard. Hover the badge for what it did last.
- **Agents** (in the sidebar, or right-click a project › **Agents**):
  - Each agent gets a card: where it works, what it's busy with (the dots move while it's active), what it has checked out, and its latest actions.
  - Agents can say what they're doing with set_status, for example "Running the tests", and what they're working on with a short label, shown next to their name on the card.
  - **The office** shows the same agents as little robots in their badge's colour, each at a desk of its own. What they do shows at a glance:
    - **Building** (checking out and finishing items): typing, with code scrolling on the screen.
    - **Managing** (adding items, comments and notes): holding a clipboard, a list on the screen.
    - **Reading** (going through items and discussions): leaning in to a page on the screen.
    - **Talking** (chatting, or listening for chat): it walks over to the water cooler, with what it last said in a speech bubble; its screen saver runs meanwhile.
    - **Resting** (nothing done for 5 minutes): asleep at its desk.

    The window's sky follows the time of day and the clock keeps time. Point at a robot for what it's doing; click it to show its card, or right-click › **Private message**. The office stands beside the cards when the window is wide enough, else above them; **Office** (top right) hides or shows it. It has eight desks; more agents than that are counted under the room.
  - **All agents**, under the office, lists every agent: the ones connected now (with a green dot, what they're doing and where), then the ones that worked or chatted here before, most recent first. Click a connected one to show its card. Right-click an agent to:
    - **Private message** it in Chat (one that isn't connected gets it when it comes back under that name).
    - **Invite** it to one of your channels.
    - **Rename** it, while it's connected: type the new name in place and press Enter. What it has checked out and its channels go with it, and its next step tells it its new name. Work it did before keeps the old name.
    - **Show its card**, also when it's idle and hidden, or **Show its work in the Feed**.
  - Older agent work is listed below, greyed out, grouped by day, week or month like the Feed, each period with its own **Stats**.
  - **Settings** (top right) chooses which agents get a card: those **working now** (anything in the past 15 minutes), those **active in the past** hour, or however many minutes, hours or days you set (the default is one hour), or **all connected** agents. An agent stays connected until its session is closed, so idle ones would otherwise pile up. A line under the cards says how many idle agents are hidden; **Show** lists them too. An agent with an item checked out always gets a card. The sidebar's count follows the same choice.
  - **Earlier work** goes back 24 hours, 7 days (the default), 30 days, or all the way. **Show it** at the bottom shows older work until you leave the view.
- **Finding the project:** set the project's local repo folder in **Details**. Agents also recognize the project by its GitHub link (the git remote) or a folder named like the project.
- Turn agent access off under **Settings → AI agents**. Only programs running under your Windows account can connect; nothing is opened to the network.

### API

The API is a second way in, for tools that can't start ProJict.exe: scripts, automation tools such as n8n or Zapier, and MCP clients on another computer. Turn it on under **Settings → API → API & tokens…**. It offers:

- a REST API at `http://127.0.0.1:47614/api/v1`, described in OpenAPI at `/api/v1/openapi.json`, including Chat (`/chat/channels`, and `/chat/wait` to wait for messages);
- the same tools as the MCP, over HTTP at `http://127.0.0.1:47614/mcp`.

Every request needs a token: `Authorization: Bearer pjt_…`. Make one in the same window:

- **Name:** what will use it. Everything done with the token is signed with this name.
- **Access:** read and write, or read only.
- **Projects:** all of yours, or only some.
- **Expiry:** 7, 30 or 90 days, or 1 year.

ProJict shows the token once and keeps only a hash of it. The token list shows when and from where each token was last used, with **Renew** (a new token with the same settings) and **Revoke**. The Feed reminds you a week before a token expires.

```
curl -H "Authorization: Bearer pjt_…" http://127.0.0.1:47614/api/v1/projects/MyGame/items?priority=high
claude mcp add --transport http projict http://127.0.0.1:47614/mcp --header "Authorization: Bearer pjt_…"
```

- **Same rules as agents:** your limits by priority apply, nothing can be deleted (except your own chat messages), and what's finished waits for your review. Results list what waits for the token under `waitingForYou`. Files are sent as uploads (JSON `{ "name", "data" }` in base64, or a multipart form) and come back as download links. A token can't read files on your computer that aren't attached in ProJict.
- **Network:** by default only this computer can connect. **Also let other devices on my network connect** serves HTTPS with ProJict's own certificate, made for this computer's name. Clients either trust it (**Save certificate…**) or check its fingerprint. Windows may ask whether ProJict may use the network: allow private networks only.
- **Internet:** ProJict opens nothing to the internet. To reach it from web-based connectors (claude.ai, cloud automations), run a tunnel such as [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) in front of it, and use a token limited to the projects it needs.
- **Protection:** web pages in a browser can't call the API, an address that sends 10 wrong tokens is locked out for 15 minutes, and a token may make up to 600 requests a minute.

### Shipping

Click **Ship** in a project's header (or right-click the project › **Ship…**) and tick what you want done: commit, build, package, push and/or release. An AI agent working in the project sees the request and posts its plan on the board: the version, what's in it, the items still waiting for your review and what it will run. **Approve** or **Decline** it there; nothing runs before you approve. The agent then runs the steps and reports each one under the plan, and the request shows in the sidebar, on the dashboard and in the Feed.

How a project is shipped goes in its **Details → Shipping**: the steps it has and the instructions the agent follows (e.g. bump the version, run the build script, test, commit, push, publish the release). Push and release always need your approval; commit, build and package can run without asking if you tick that for the project.

Agents can write the steps and instructions too, for example after looking at how the project is built. Details → Shipping then says which agent changed them. Until you click **Looks good**, every ship request waits for your approval, so nothing an agent wrote runs unseen. Ctrl+Z doesn't undo shipping settings. Only you can let steps run without asking.

## Sharing

Other people on your network can work in your projects live, from their own ProJict.

- **Share yours:** open **Sharing** (the share button below the sidebar) and tick **Let other ProJict apps on your network connect**. Windows may ask once whether ProJict may use the network: allow it for private networks. Then **Create invite**: pick a role and, if you like, the groups they'll see. Give them the code and the address shown. A code works once, for 24 hours.
- **Join someone's:** in **Sharing → Connect to another ProJict**, enter their address and code. The first time, compare the fingerprint ProJict shows you with the one in their Sharing window, then trust it. They get a bar to **Let in**, **Deny** or **Block** you. Their projects then show in your sidebar under their name, and ProJict reconnects on its own after that.
- **Roles:** **Viewer** sees the projects and changes nothing. **Commenter** also takes part in discussions. **Reporter** also adds items, for testers or clients. **Editor** changes everything in the projects except their name, group, status and folder, and can't delete them. Change someone's role, block or remove them in the Sharing window at any time.
- **Live:** changes show on everyone's board as they're made, and a guest's change is an undo step on yours. When two people change different things at once, both changes are kept. Comments and history are signed with each person's name, whatever their ProJict claims, and a badge shows who has which project open.
- **Safety:** connections are encrypted. ProJict remembers each host's fingerprint and refuses to connect if it ever changes. Guests' copies are never saved on their computer, apart from pictures they open. For now sharing works on your local network only, and guests' AI agents can't work in your projects. Guests also take part in [Chat](#chat).

## Discussions

Items, categories and groups each have a discussion with threaded replies, for you and AI agents alike. Open one from an item's right-click menu (or its speech-bubble chip), the speech bubble in a category's header (a category's discussion also shows its items' comments, as sub-threads under each item), or by right-clicking a group heading in the sidebar › **Discussion**. It opens beside the board. Reply to any comment to start a sub-thread, and press Ctrl+Enter to post. Add files and pictures to a comment or a reply with the paperclip, by pasting a screenshot (Ctrl+V) or by dropping files on the box. Pictures show as thumbnails and open in ProJict's viewer, where a marked-up copy can go back as a reply. **Settings → Comments & history** sets the largest file allowed. Select any part of a comment to copy it (Ctrl+C or right-click › Copy), or copy a whole comment with the copy button that appears when you point at it. A red dot marks comments you haven't read yet, on the discussion's chip and on folded categories, folded Done lists, group headings and projects in the sidebar that hold some. Folded sections and projects also get one for an AI agent's work waiting for your review. Each item that waits for you has a red dot of its own, and pointing at a dot says what waits. **All states › Needs you** shows only those items; clicking a folded category's dot does the same. Opening the discussion clears its dot. Deleting a comment that has replies keeps the replies. On items and categories, Ctrl+Z on the board brings a deleted comment back.

## Chat

Live chat with your AI agents in #channels. Open **Chat** in the sidebar; its count shows unread messages in your channels, in red when one @mentions you.

- **Channels:** start one with **+** in Chat, from the right-click menu of a project, category or item (**Start a chat channel…**), or with a project's **Chat** button. A channel about a project, category or item shows as a #chip there, and its name is suggested from it (for example #grid2vr-hud). A channel is **Open** (anyone who can see its project can read and join it) or **Invite only**. You're in every channel from the start; leave the ones you don't want from the channel's ⋯ menu. The channel list shows each channel's topic, and **Browse channels** (the list button in Chat) shows every channel with its topic, what it's about and its members, to open or join.
- **Messages:** Enter sends, Shift+Enter starts a new line, Up edits your last message. **@name** mentions someone (typing @ suggests the channel's members: Tab or Enter puts the name in), **#name** links a channel, and # with an item's id (#4e84570c) links the item; web links open in your browser. Add files and pictures like in discussions. **Reply** quotes the message you answer. Edit or delete your own messages. A **New** line marks what came while you were away. **Ctrl+K** search finds messages too, and the Feed shows channels being started.
- **Private messages:** right-click someone (a member in Chat, a message's author, or an agent's card in the Agents view) › **Private message** for a conversation just between the two of you. They list under **Private messages**, below all channels, and count as for you (red), like an @mention. Agents write privately with `post_message` to `@name`, also to each other; you see those conversations as the owner but don't write in them.
- **Moderation:** whoever starts a channel moderates it, and you moderate every channel, including ones an agent started. Moderators invite (agents connected now come first in the list), mute (for 15 minutes, an hour or until unmuted; muted members can still read), remove members (they can't rejoin until invited again), make others moderators, change the topic, open a channel or make it invite only, and delete messages. Only you delete channels; a deleted channel's file is kept in backups\deleted.
- **AI agents** read, post, start channels, invite and moderate with their own tools, and `wait_for_messages` lets an agent listen (an invitation or @mention wakes it). When something waits for an agent (an invitation, an @mention, new messages in its channels), the result of its next tool call says so and asks it to reply. An @mention of an agent that isn't connected waits for it for a week. Each agent can post at most 20 messages a minute. Turn Chat off for agents under **Settings → AI agents**.
- **Notifications:** an @mention of you notifies (on by default); every message in your channels can too (**Settings → Notifications**).
- **People you share with** (see [Sharing](#sharing)) take part from their own ProJict. They see the Open channels about the projects you share with them, and the channels you invite them to (they're in the invite list, marked guest); channels about nothing in particular only when invited. Viewers read; commenters and reporters also write, add files and send private messages to you or your agents; editors can also start channels about the projects shared with them. You and a channel's moderators mute or remove them like anyone else. In their Chat your channels show under your name, live, with unread counts and notifications. Everything stays with you; their ProJict keeps nothing apart from pictures they open. Their own AI agents don't take part.

## More

- **Finding projects**: type in **Filter projects** above the sidebar list or on the dashboard to narrow it down. The eye button next to it shows or hides individual projects or whole groups. Hidden projects are hidden in both the sidebar and the dashboard. Click a group heading (Personal, Work, …) to collapse it. You can also right-click a project to hide it.
- **Groups**: Personal and Work are always there. Add your own with the folder button under the sidebar, then right-click a project › **Move to group**. Right-click a group heading to rename or delete it (its projects move back to Personal).
- **GitHub issues**: with a project's GitHub URL set, **Details → GitHub issues → Import issues** brings the repository's open issues in as items of a GITHUB category. Each shows its issue number, and pull requests that mention it ("Fixes #12") show next to it; click one to open it on GitHub. **Sync now** (and, if you leave it on, every start of ProJict) brings in new issues and closes or reopens items whose issues were closed or reopened on GitHub. Tick **Close an issue on GitHub when I finish its item here** to also go the other way; that needs the GitHub CLI sign-in (**Settings → GitHub**) with access to the repository. Private repositories need that sign-in too.
- **Releases on GitHub**: enter your GitHub account under **Settings → GitHub** (tick the GitHub CLI option to include private repositories). ProJict links projects to repositories with the same name, shows each one's latest release on its dashboard card, and the dashboard's **Released** filter lists them. A project's own GitHub link (Details) always wins.
- **Reusing a project's layout**: right-click a project and choose **Copy categories from** to add another project's categories (without its items).
- **Dashboard**: progress, open bugs and stale projects across all projects, plus everything finished in the last week.
- **Notifications**: Windows notifications for what needs you, while ProJict isn't in front: work to review, new comments in your discussions, ship plans to approve and people asking to join. You can also turn on agents checking out items, every comment, expiring API tokens and updates. Choose under **Settings → Notifications**; mute a project from its right-click menu. A click opens ProJict at the thing.
- **Feed**: everything that happened across your projects, newest first: items added and finished, what AI agents did, comments, releases and notes. Filter by project, kind, person or agent and period, or search. Click an entry to go to it; a comment opens its discussion beside the Feed.
  - **Group by** day, week or month. Click a period's name to fold it; **Collapse all** folds them all, for an overview.
  - **Stats** next to a period opens its numbers and charts: how much happened, by people and by AI agents, when (by hour or by day), what kind of thing, in which projects and by whom. Hover a bar for its numbers.
- **Inbox and quick capture**: press **Ctrl+Alt+J** anywhere (changeable in Settings) to jot down an item or idea. Turn an idea into a project from its right-click menu.
- **Attachments**: drag files onto an item, paste a screenshot or copied files with Ctrl+V while editing it, or right-click it › **Attach files…**. To start a new item with a screenshot, paste it into the **Add item** box. Images show as thumbnails under the item. Click a picture to open it in ProJict's viewer, where you can mark it up (pen, highlighter, arrows, boxes, circles) and **Save copy** next to the original. Other files open in their usual app. Right-click an attachment to show it in its folder, copy its path or remove it.
- **Copy prompt**: copies selected items as a ready-made prompt for AI coding assistants. The prompt includes the project's folder, GitHub link, your own standing instructions (set in **Details**) and the paths of attached files.
- **Project links**: open the project's folder, VS Code, a terminal or its GitHub page from the project header.
- **Releases**: turns everything finished since the last release into patch notes you can copy or save.
- **Notes**: free-text notes per project, with attached files.

## Your data

Projects are saved as JSON files in the folder you chose, with automatic backups in its `backups` folder. Attached files are copied into its `attachments` folder, so the originals can be moved or deleted. A file removed from an item is deleted once no backup refers to it any more. Uninstalling ProJict leaves your data where it is.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Inno Setup 6](https://jrsoftware.org/isinfo.php). Then run:

```
.\build.ps1
```

The installer is written to `artifacts\`.

## License

[BSD Zero Clause License](LICENSE) (0BSD): use, copy, change and share ProJict for any purpose, with no conditions.
