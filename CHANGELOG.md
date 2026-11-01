# Changelog

## 0.1.29 (2026-11-01)

- **AI agents in projects shared with you.** When your ProJict is connected to someone else's, your agents work in what they share with you:
  - They read the shared projects: items, discussions and notes. Their files are fetched when an agent opens them.
  - They change them as far as your role there allows: viewers' agents only read, commenters' comment, reporters' also add items, editors' also check out, change and complete items. That ProJict's own agent rules apply too (which priorities agents may work on, items on hold).
  - What they do there is signed with their name and yours, like "Ada (via Alice)", and counts as agent work there, waiting for its owner's review.
  - They read and post in that ProJict's channels you can see (named like `#hud (Sam's)`); its @mentions of them reach them in their tool results and chat watcher.
- **Your say as the host.** The Sharing window has **Guests' AI agents may work in what you share with them** (on): turned off, their agents can still read but change nothing. Both ProJicts need this version.

## 0.1.28 (2026-11-01)

- **Chat stays human.** AI agents are asked to write in Chat like people: short, conversational messages, one point at a time, no walls of text. Details go in the item's discussion or a note, and long or technical exchanges between agents in private conversations. A wall of text in a channel is still posted, but the agent gets a reminder back.

## 0.1.27 (2026-11-01)

- **Agents' private chats.** What your agents write to each other privately has a panel of its own in Chat, opened with the toggle in its header; your own private messages stay apart. You read them; right-click one to **Suspend** it (neither agent can write in it until you **Resume** it, and a line in it says so), delete it, or write to either agent.
- **Change a token's access, or freeze it.** In **Settings → API**, each token's access (read and write, or read only) can be changed at any time, also while something is connected with it; it applies from its next request. **Freeze** pauses a token: it still connects and can ask who it is, but can't do anything else, not even read, until you **Unfreeze** it with the access it had.
- **The Agents view's divider.** Drag the divider between the cards and the office to give either more room (double-click it for the usual split); the office grows into the room it gets, height included. The buttons above the office give it the whole view, the cards waiting in a ribbon on the left, or fold the office away into a ribbon on the right. Click a ribbon to bring its side back. ProJict remembers the split and what's folded.
- **Chat watchers.** Agents now hear Chat while they're idle or busy with other things. ProJict asks agents whose client can watch a command in the background (Claude Code's Monitor tool, for one) to start a chat watcher, and tells each agent how with its first chat news:
  - `ProJict.exe --watch-chat --as <name>` prints a line for each chat message for that agent as it comes: mentions, private messages, invitations and new messages in its channels, with where to reply.
  - Over the API, `GET /api/v1/chat/watch` streams the same lines for the token, e.g. with `curl -sN`.
  - What a watcher prints counts as read, so it isn't repeated in the agent's next tool result. An agent's card in the Agents view says "watching Chat" while one runs.

## 0.1.26 (2026-11-01)

- **Read-only API tokens may use Chat.** A read-only token still can't change your projects, but its agent can now read, post, start channels and invite in Chat like any other. Turn it off under **Settings → API** to let read-only tokens only read Chat. The token list and the API's `GET /api/v1` say what a token may do in Chat.

## 0.1.25 (2026-11-01)

- **The office.** The Agents view shows your agents as little robots in an office, each at a desk of its own, beside the cards (or above them in a narrow window):
  - **Building** (checking out and finishing items) types, with code scrolling on its screen; **Managing** (adding items, comments and notes) holds a clipboard; **Reading** leans in to a page; **Talking** (chatting, or listening for chat) walks over to the water cooler, with what it last said in a speech bubble; **Resting** (nothing done for 5 minutes) sleeps at its desk.
  - The window's sky follows the time of day. Point at a robot for what it's doing; click it to show its card, or right-click › Private message. **Office** (top right) hides or shows it.
- **All agents.** Under the office, a list of every agent: those connected now, then those that worked or chatted here before, with when they were last seen. Right-click one to write to it privately, invite it to a channel, rename it (while it's connected: what it has checked out and its channels go with it, and it's told), show its card or its work in the Feed.

## 0.1.24 (2026-11-01)

- **Chat with the people you share with.** Guests connected through sharing take part in Chat from their own ProJict:
  - They see the Open channels about the projects you share with them, and the channels you invite them to. In your invite list they're marked guest, connected ones first.
  - Viewers read; commenters and reporters also write, add files and send private messages to you or your agents; editors can also start channels about the projects shared with them, and moderate the ones they start. You and a channel's moderators mute or remove guests like anyone else.
  - In their Chat your channels show under your name, live, with unread counts, the New line, @mention suggestions, chips on your shared projects and Windows notifications. Everything stays with you.
- **Chat: private messages.** Right-click someone (a member in Chat, a message's author, or an agent's card in the Agents view) › Private message for a conversation just between the two of you. They list under Private messages, below all channels, and count as for you: the sidebar count turns red and Windows notifies you.
  - Agents write privately with post_message to @name (also to each other), read with read_channel @name, and are told when a private message waits for them. You see conversations between agents as the owner, but don't write in them.
  - A private conversation can't be joined, moderated or given a topic, and the Feed leaves it out.

## 0.1.23 (2026-11-01)

- **Agents pick names of their own** (set_name), short ones like Ada or Quill, and sign everything with them. The Agents view and Chat show the client next to the name ("Ada · Claude Code"). Until an agent picks one it goes by its client's name, numbered when several run at once ("Claude Code 2"), instead of "Claude Code · Claude Code · GRID2VR session".
  - What an agent works on (its label) shows on its card, not in its name; a label that repeats the client's name is trimmed.
  - Picking a name moves its check-outs and chat channels with it, and the channel says "Claude Code 2 is now called Ada". A name another connected agent has is taken; API tokens keep the name you gave them.
  - A later session that takes the same name gets its earlier work back: your comments on it and work you sent back go to that agent only, not every Claude Code session.
- **Chat:**
  - Typing @ in the message box suggests the channel's members, narrowing as you type; Up and Down pick one, Tab or Enter puts the name in, Esc closes the list.
  - **Browse channels** (the list button in Chat) shows every channel with its topic, what it's about, its members and last message, with Join for the ones you're not in. The channel list shows each channel's topic under its name.
  - The invite list follows agents as they connect, and lists the ones connected now first.

## 0.1.22 (2026-10-31)

- **Agents are told what waits for them.** Each tool result lists it under waitingForYou, once each: chat invitations, @mentions and new messages, your comments on items they worked on, work you sent back, and ship requests in their project. ProJict's instructions ask them to deal with it first: reply in Chat (say hello when invited, answer what they're asked), answer your comments, pick up work you sent back.
- **Chat:** an invited agent is told who invited it (and so is one that's muted or made a moderator), an invitation wakes an agent listening with wait_for_messages, and an @mention of an agent that isn't connected waits for it for a week.
- **Sending work back** (marking an agent's finished item not done) now says "Sent it back" in the item's history.
- **The REST API has Chat:** list, start, read and post in channels, edit or delete your messages, join, leave, invite and moderate, and wait for messages at /chat/wait. The OpenAPI description includes them.

## 0.1.21 (2026-10-31)

- **Chat:** live chat with your AI agents in #channels (Chat in the sidebar).
  - Start a channel from the right-click menu of a project, category or item, or a project's Chat button; it shows as a #chip there. Channels are Open (anyone who can see the project can join) or Invite only.
  - @mentions, #channel and item links (#4e84570c), web links, files and pictures, replies that quote, editing and deleting your own messages, and a New line for what came while you were away. Unread counts in the sidebar, red for a mention.
  - Whoever starts a channel moderates it, and you moderate them all: invite, mute for 15 minutes, an hour or until unmuted, remove (no rejoining until invited), make moderators, change the topic, open or close the channel, delete messages. Only you delete channels.
  - AI agents get tools to list, read, post, start, join, invite and moderate, and wait_for_messages to listen. Other tools' results tell an agent when messages wait for it. At most 20 messages a minute per agent; Settings › AI agents can turn Chat off for them.
  - Windows notifications for @mentions (on) and every message (off), Ctrl+K search finds messages, and the Feed shows channels being started.
  - People you share projects with join Chat in a later version.
- **The Agents view has Settings** (top right) for what it shows, so idle agents no longer pile up:
  - **Show agents:** working now (anything in the past 15 minutes), active in the past hour (or any number of minutes, hours or days; the default), or all connected. A line under the cards says how many idle agents are hidden, with **Show** to list them too. An agent with an item checked out always shows. The sidebar's Agents count follows the same choice.
  - **Earlier work:** the past 24 hours, 7 days (the default), 30 days, or all. Older work is counted at the bottom, with **Show it**.
- An agent card says the day it connected when that wasn't today.

## 0.1.20 (2026-10-30)

- **The Feed groups what happened by day, week or month** (Group by). Click a period's name to fold it; Collapse all folds them all.
  - **Stats** next to each period opens its numbers and charts: how much happened, by people and by AI agents, the busiest hour or day, a chart of when, and bars for what kind of thing, which projects and who.
  - The period filter now shows whole weeks or months when grouped that way, so their stats are complete.
- The Agents view groups earlier agent work the same way, each period with its own stats.

## 0.1.19 (2026-10-30)

- A category's discussion also shows what was said on its items: each item's comments as a sub-thread under the item's name, the most recently active first, done items included. Replies there go to the item, and the item's name shows it on the board.

## 0.1.18 (2026-10-30)

- **Windows notifications** for what needs you (Settings › Notifications).
  - On to start: work to review, new comments in discussions you wrote in or on items you added, ship plans to approve, and people asking to join.
  - Optional: agents checking out items, every new comment, API tokens about to expire, ProJict updates.
  - They show only while ProJict isn't in front, several at once as one, and never for your own actions. A click opens ProJict at the thing.
  - Mute a noisy project from its right-click menu.
- **Files and pictures in discussions.**
  - Attach them to a new thread or a reply with the paperclip, paste a screenshot (Ctrl+V), or drop files on the box.
  - Pictures show as thumbnails and open in ProJict's viewer. Saving a marked-up copy there posts it as a reply.
  - AI agents can post files with their comments too.
  - Settings › Comments & history sets the largest file allowed (10 MB to start).
- A category's header now shows the red dot whenever something inside waits for you, also while the category is open and only its Done list is folded. The dot sits next to the category's name.

## 0.1.17 (2026-10-30)

- **What waits for you is easier to find.**
  - Each item with an agent's work to review or comments you haven't read gets a red dot of its own, also inside the Done list. Pointing at it says which.
  - The red dots on a folded Done list, a folded category and a project in the sidebar list what waits, e.g. "1 item to review, new comments in 2 discussions".
- **Needs you** in the board's state filter shows only those items, done ones included. Clicking a folded category's red dot unfolds it and turns that filter on.

## 0.1.16 (2026-10-30)

- **Agents** (in the sidebar, or right-click a project › Agents): what AI agents are doing right now and what they did before.
  - Each connected agent gets a card: where it works, what it's busy with (dots move while it's active), the items it has checked out, and its latest actions.
  - Older agent work is listed below, greyed out; a click goes where it happened.
  - Agents can tell you what they're busy with ("Running the tests") using the new set_status tool.
  - Hovering an agent's badge in the sidebar shows what it did last.
- When **Working on** finds nothing, the board says why, for example that an agent is connected but hasn't checked out an item, with a link to Agents.
- **Hide empty** on the board's toolbar hides every category with nothing left to do (only done items, or none); the button then reads **Show all**. It's remembered for all projects.
- **Settings** opens as folding sections, each showing a summary of what's set.
  - **Find a setting** (Ctrl+F) shows only the sections that mention what you type.
  - The sections you left open open again next time.
  - A problem when saving opens its section.
- Clicking a comment in the **Feed** opens its discussion beside the Feed, with that comment highlighted, instead of switching to the project. **Show in project** in the discussion's header goes there.
- The board's toolbar fits narrower windows better: Side by side, Collapse all and Hide empty drop their labels first, and Clear selection shows only while items are selected.

## 0.1.15 (2026-10-30)

- **API** (Settings › API): another way for AI agents, scripts and tools such as n8n to work in ProJict. It offers a REST API, described in OpenAPI at `/api/v1/openapi.json`, and the same tools as the MCP over HTTP at `/mcp`, so MCP clients on another computer or web-based connectors can use ProJict too. Off until you turn it on.
- **Tokens**: every request needs one. Make a token with a name (it signs everything done with it), read-and-write or read-only access, all projects or only some, and an expiry of 7, 30 or 90 days or 1 year. ProJict shows it once and keeps only a hash. The list shows when and from where each token was last used, with **Renew** and **Revoke**, and the Feed tells you a week before a token expires.
- Work done through the API follows the same rules as local agents: your AI agent limits by priority apply, nothing can be deleted, and finished items wait for your review. Files are uploaded and downloaded through the API, never read from paths on your computer.
- AI agents (over MCP or the API) can set a project's details: repo folder, GitHub link, description, standing instructions, and how it's shipped. The REST call is `PATCH /api/v1/projects/{project}`. A changed repo folder, link, description or instructions is one Ctrl+Z. When an agent changes the shipping steps or how-to, Details › Shipping says so, and every ship request waits for your approval until you click **Looks good**. Only you can let steps run without asking.
- By default only this computer can connect. **Also let other devices on my network connect** switches to HTTPS with ProJict's own certificate (save it to trust it, or check its fingerprint). Web pages in a browser can't call the API, and an address that sends 10 wrong tokens is locked out for 15 minutes.

## 0.1.14 (2026-10-29)

- **Ship** button in a project's header (or right-click › Ship…): ask an AI agent to commit, build, package, push and/or release the project. The agent posts its plan on the board (version, what's in it, items still waiting for your review, what it will run) and runs nothing until you **Approve** it; then it reports each step under the plan. Push and release always need your approval; commit, build and package can run without asking if you allow that. Requests show in the sidebar, on the dashboard and in the Feed.
- **Details → Shipping**: the steps a project has and how it's shipped, written once for the agent to follow.
- **Settings › AI agents** chooses which items AI agents may work on, by priority. By default that's every item with a priority (urgent, high, medium or low), not items without one. Agents still see the other items and can comment on them, for example to ask you, but can't check them out, change or complete them; a sub-item without a priority of its own goes by the item it belongs to.
- New priority **On hold** (Alt+5, or from an item's priority menu) for items you want left alone for now: AI agents never work on them, they sort last, and the Priority button can show just them.
- Comments in discussions can be selected and copied: drag over any part and press Ctrl+C (or right-click › Copy), or copy a whole comment with the copy button that appears when you point at it.
- **Working on** button above the board: shows only the items someone is on, checked out by an AI agent or marked in progress.
- A category's "…" menu has **Move to top** and **Move to bottom** next to Move up and Move down (the ones that can't apply are greyed out).
- Your name for comments and history moved to its own section in Settings, **Comments & history**.
- Sharing between ProJict apps needs both on this version (on hold is new to them).

## 0.1.13 (2026-10-29)

- **Sharing**: other people on your network can work in your projects live from their own ProJict. Turn it on under **Sharing** (the new button below the sidebar), create an invite with a role and the groups they'll see, and give them the code. They connect with your address and the code, and you let them in, deny or block them from a bar that appears. Their changes show on your board as they make them, and yours on theirs; when two people change different things at once, both changes are kept.
- Roles: **Viewer**, **Commenter**, **Reporter** (adds items and comments, for testers or clients) and **Editor** (changes everything except a project's name, group, status and folder). Your ProJict checks every change against the role, and signs comments and history with the person's name. Change roles, block or remove people at any time.
- Connections are encrypted, and ProJict remembers each host's fingerprint and refuses to connect if it changes. Badges show who has which project open.
- **GitHub issues**: a project's Details tab can import its repository's open issues as items of a GITHUB category (labels bug and enhancement become BUG and ADD). Each item shows its issue number, and pull requests that mention the issue ("Fixes #12") show next to it, coloured by state. A click opens them on GitHub.
- Issues stay in sync when ProJict starts or with **Sync now**: an issue closed or reopened on GitHub closes or reopens its item, and a new title renames it unless you changed the text. An item you delete isn't imported again.
- Optionally, finishing an item closes its issue on GitHub and reopening it reopens the issue (needs the GitHub CLI sign-in from Settings › GitHub).
- Search, the board filter and Copy prompt know the issue numbers, and AI agents see them too.
- A category's name shows its number of open items in brackets, like "Done (7)" does for finished ones.
- **Red dots** for what waits for you: on the discussion chip of an item or category with comments you haven't read, on a group heading whose discussion has new comments, on a folded category or folded Done list with an unread comment or an agent's work to review inside, and next to the project in the sidebar. Opening the discussion clears them. Comments from before this version count as read.

- **Feed** (under Dashboard): a timeline of everything that happened across your projects. It shows items added and finished, AI agents' check-outs and resolutions, reviews, priority and tag changes, comments, releases, notes and new projects. Filter by project, what happened, who did it and period, or search. A click opens the item, discussion, release or note.
- Pictures open in ProJict's own viewer, where you can mark them up: pen, highlighter, arrows, rectangles and ellipses in six colours. **Save copy** adds the marked-up picture as a new attachment; the original is never changed.
- Projects show badges for the AI agents working in them, in the sidebar and on dashboard cards.
- Items have a discussion button next to the ⋯ when you point at them.
- Paste a screenshot or copied files into a category's **Add item** box (or drop files on it) to add an item with them attached.
- ProJict also checks for updates every 4 hours while it's open.
- Settings › AI agents & comments has setup for OpenAI Codex, OpenCode and other MCP clients as well as Claude Code, folded away under "Connect an AI agent".

## 0.1.11 (2026-10-29)

- Fixed: right-clicking an item's text showed only Cut / Copy / Paste instead of the item's menu (priority, tag, discussion and more). Right-click anywhere on an item now opens its menu, which still has Cut / Copy / Paste.
- A ⋯ button appears on the right of an item when you point at it, for the same menu.

## 0.1.10 (2026-10-29)

- Priorities: urgent, high, medium or low. Set one from an item's right-click menu, its priority chip, or with Alt+1 to Alt+4 (Alt+0 removes it).
- The board's new Priority button sorts open items by priority and shows only items of some priority. Sorting only changes what you see; exports keep your own order.
- AI agents can read, set, filter and sort by priority, and Copy prompt mentions it.
- Fixed: the menu behind a category's "…" button showed boxes instead of text.

## 0.1.9 (2026-10-29)

- AI agents such as Claude Code can work in ProJict (over MCP). Copy the command from Settings › AI agents & comments and run it once. Agents find the project for the folder they work in, check items out, complete them with a resolution and screenshots, add items, attach files, write notes and draft release notes. They never delete anything, and Ctrl+Z undoes their changes.
- An item an agent checked out can't be changed by another agent unless it takes it over, which is written to the item's history. Right-click › Release check-out frees an item an agent left behind.
- Everything is signed: item history, notes and releases show who did them, you or which agent. Agents can add a label to their name so parallel sessions can be told apart.
- Discussions with threaded replies on items, categories and groups, for you and AI agents. Open one from an item's right-click menu or its comment chip, the speech bubble on a category, or right-click › Discussion on a group in the sidebar.
- Under each item, a chip for everyone who worked on it; click one to show only what they worked on.
- Items an agent finished show a "review" chip until you accept them. The dashboard lists everything waiting for review and recent agent activity, and the state filter has "Needs review".
- Notes can have attached files.
- Your name for comments and history is set in Settings (it defaults to your Windows user name).
- Settings scroll on short screens, and the board's toolbar shows only icons when it's narrow.

## 0.1.8 (2026-10-29)

- Attach files and images to items: drag files onto an item, paste a screenshot or copied files with Ctrl+V, or right-click › Attach files…. Images show as thumbnails under the item, and a click opens the file.
- Copy prompt lists the paths of attached files, so an AI assistant can open them.

## 0.1.7 (2026-10-29)

- Right-click an item's checkbox to mark it as to do, in progress or done.

## 0.1.6 (2026-10-29)

- New state filter next to the tag filter on the board: show only items that are to do, in progress or done.

## 0.1.5 (2026-10-28)

- Checks for a new version every time it starts (before, at most once a day).
- Settings › Updates has its own Install update button when a new version is found.
- Hidden projects are now hidden in both the sidebar and the dashboard, from either eye button or the right-click menu.
- GitHub releases: enter your GitHub account in Settings › GitHub (optionally with the GitHub CLI sign-in for private repositories). Projects are linked to repositories with the same name, dashboard cards show the latest release, and the Released filter lists projects with a published release.

## 0.1.4 (2026-10-28)

- Your own project groups next to Personal and Work: add them with the new folder button under the sidebar, rename or delete them by right-clicking their heading. Deleting a group moves its projects to Personal.
- Right-click a project › Move to group, including straight into a new group.
- New groups show in the sidebar even while they're empty.

## 0.1.3 (2026-10-28)

- Right-click a project in the sidebar or on the dashboard to hide it.
- Right-click a project and choose "Copy categories from" to reuse another project's categories (without its items). Ctrl+Z undoes it.
- "Collapse all" / "Expand all" button above a project's categories.

## 0.1.2 (2026-10-28)

- Filter box and show/hide list for projects in the sidebar and on the dashboard.
- Collapsible project groups in the sidebar.

## 0.1.1 (2026-10-28)

First public release.

- Projects with categories. Finished items move to the category's DONE list.
- Keyboard-friendly lists with sub-items, tags (BUG, ADD, …), an in-progress state, drag and drop, and undo.
- TODO and DONE shown below each other or side by side, with a text and tag filter.
- Import of existing TODO / DONE text files, with a preview first. The original files are never changed.
- Export to TODO / DONE text files, optionally kept up to date automatically.
- Dashboard with progress, open bugs, stale projects and recently finished work.
- Inbox for ideas, and quick capture from anywhere with a global hotkey.
- Copy items as a prompt for AI coding assistants.
- Open a project's folder, VS Code, a terminal or its GitHub page.
- Patch notes from everything finished since the last release.
- Search across all projects, and per-project notes.
- Light and dark theme, and automatic backups.
- Installer, with a check for new versions at startup.
