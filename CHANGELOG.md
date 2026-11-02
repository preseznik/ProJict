# Changelog

## 0.1.37

- **Chat channels open in a blink.** A channel with a long history used to take most of a second to open, because every message was laid out at once (200 of them). Now it opens with the newest 30, and older ones come 30 at a time as you scroll up (or with **Show earlier messages**), while the view stays on what you were reading. A new message no longer rebuilds the list either. When you come back to a channel, it opens at the **New** line again, near the top with a little of what came before it (it had been opening at the newest message).
- **ComfyUI servers and a workflow library for agents.** In **Settings → Tools**, a tool can now be **ComfyUI (your own server)**: name its endpoints (this computer, or a PC on your network, with **Check** to see that it answers, how busy it is and its graphics card) and add workflows with **Add a workflow…** (in ComfyUI, use Workflow › Export (API)). ProJict keeps the workflows in its data folder and finds what can be filled in: the prompt, the negative prompt, the seed, the size and a picture to start from. Agents see them with `list_tools` and run one with `run_workflow`: ProJict uploads the picture, queues it, waits and attaches the pictures or videos it made to the item the agent names, with a line in its history (or keeps them in a folder and gives the agent the paths). The agent hears when it's done or failed, and `get_workflow` hands it the workflow to call ComfyUI itself. Untick **Agents may run its workflows** to let them read the workflows but not run them. Like other tools, it's ticked per project in **Details → Tools**, and its addresses stay on this computer.
- **How much of its usage each agent has used.** A small ring by an agent's name, on its card and in **All agents**, fills as its usage is used up: grey, amber from 75%, red from 90%, faded when the figure is over a day old. Its tooltip says what was used ("62% of the weekly limit used · resets Thu 09:00 · reported 10 min ago"). ProJict can't read a subscription or an API balance, so agents report it themselves with the new `report_usage` (a share of their limit, or money spent of a budget), or you set it: right-click the agent › **Usage…** ("62%", "$14 of $50"; empty clears it). At 90% the agent is told to finish what it's on and hand over.

## 0.1.36 (2026-11-02)

- **The dashboard as a list, a table or a board.** A switch at the top right of the dashboard shows projects as **Cards** (as before), a **List** (a line each, about three times as many on screen), a **Table** (every number in columns: click a heading to sort by it) or a **Board** with a column per status (Idea, Active, Paused, Released; drag a project to another column to change its status). List and Table show projects under their groups, like the sidebar, unless you untick **In groups**. **Panels** folds away the panels on the right for more room. ProJict remembers your choices on this computer.
- **The studio's lead can review wiki pages for you.** In a project's **Details → Studio**, tick **The lead designer reviews wiki pages** (the director, in a film studio), and pages about the project that the studio's other agents change wait for the lead's review instead of yours. Name a folder to keep it to one part of the wiki, such as GRID2VR/Design. The lead hears about each changed page, sees what changed and accepts it, or sends it back with a note that goes privately to whoever changed it. An accepted page loses its yellow bar and leaves the sidebar's Wiki count; the page and its **History** say who accepted it, **What changed** shows what they accepted, and **Send back** reopens it if you don't agree. The lead's own changes, changes by agents outside the studio, clashing edits and every other page still wait for you, and **Looks good** stays yours to give anytime.
- **The yellow note in Settings → AI agents says it's about this computer.** It lists the agents connected to your ProJict right now that still use the old `ProJict.exe --mcp`, so nobody else sees your list, and it goes once they're added again with the command above it. It now starts "On this computer" and says so.
- **Tools for agents: ElevenLabs, Suno, Tripo3D and your own.** **Settings → Tools** opens a window listing the outside tools your agents may use, with what each is for, its website, whether agents reach it through its API (with a key) or in the browser (and how you're signed in there), a budget they're told, and the projects it's set up for (also ticked in a project's **Details → Tools**). API keys are kept protected for your Windows user on this computer, never in the workspace, its mirror or backups. Agents see the tools with `list_tools`; when one asks for a key with `use_tool_key`, a bar at the bottom of ProJict asks you: **Allow once**, **Always for** that agent, or **No**.
- **A second chat watcher no longer takes an agent's messages.** When two chat watchers ran for one agent (one left over from an earlier session, say), each message, answer or comment went to whichever watcher got there first, so a leftover one quietly took some. Now every watcher for the agent prints every line, and when a second one starts, both say so, so the agent can stop the leftover.
- **Agents aren't told old news after ProJict restarts.** After an update (or any restart), agents reconnecting were told again about 👍 answers and comments on their work from days before, and their chat watchers printed them again. Now ProJict remembers what each agent with a name of its own was told (for a month, on this computer), so only what's new comes. News that arrives while ProJict restarts isn't lost either: the agent's bridge quietly repeats its name after the restart, and nothing counts as told until the agent's next real call.
- **Time and tokens per project.** Each project has a **Time** tab: how long was spent on it in all and this week, a week-by-week chart of people's and agents' time, a table of who worked on it (items done, time, tokens, cost) and the items that took longest (a click shows one on the board). Time counts while an item is in progress, for whoever put it there, an agent's check-out included, and you can add time by hand: right-click an item › **Log time…** (e.g. 1h 30m). Agents report the tokens and cost they used when they finish an item (or with the new `log_work`), and `get_item` tells them an item's time. Items show their time as a small chip, with the details in the history's tooltip, and the dashboard's **Table** has **Time** and **Tokens** columns to compare projects. For items finished before, the agents' check-outs in their history count.
- **Your other computer's channels show here too.** A ProJict linked as you ("It's me", e.g. your laptop) now shares all its own channels back over the same connection: they show in Chat under "iztok4090's ProJict", and you read and write in them as you, files, topics and new channels included. Someone else's ProJict shares its Open channels (not a private project's) when they tick **Share my Open channels with …** in Sharing; you're a visitor there, by your own name. Every channel from another ProJict now carries a small tag with whose it is ("#hud · iztok4090") in Chat's list, and Ctrl+K search and notifications find and name them too. They go away while that ProJict isn't connected.
- **Agents in other ProJicts are known here, and can be invited.** The ProJicts you share with tell yours which agents they have connected, and yours tells them. So a guest's agent ("Hermes (via alice)") is offered in a channel's invite list as soon as it's connected, before it has done anything here, and is invited as an agent; in their ProJict, your agents are offered for your channels the same way. When they disconnect, they show offline.
- **The API shows agents the way.** `GET /api/v1` now lists every endpoint in a line, Chat's included, and a request to the wrong endpoint (or with the wrong method) says what works there, where the full list is, and that Chat is under `/api/v1/chat/channels`. An agent had guessed `POST /projects/5` to reach a channel and was told only "Use GET, PATCH here."
- **Chat shows who's online, away or offline.** Each member in a channel's Members list has a dot on their badge: green when they're online, amber when they're away (nothing typed or clicked on their computer for 10 minutes; for an agent, no ProJict tool used for 10 minutes), grey when they're offline (their ProJict or the agent isn't connected), with the badge faded and "away" or "offline" under the name. People you share with and their agents ("Bea (via alice)") show as their own ProJict says, and in their ProJict your channels' members show as yours says. When someone's ProJict goes off, they and their agents show offline.
- **Publish plans for videos.** A film studio's Publisher posts a publish plan for each video: the title, description, tags, playlists, when it goes out and 2–4 thumbnails. It shows as a card on the project's board. Click the thumbnail you want, change any of the text, and approve or decline; nothing goes out before you approve. The agent hears your decision, publishes the video as you left it, and the card shows its link. A plan waiting for you also comes as a Windows notification.
- **Big files are linked, not copied.** Captures, edits, exports and music can be linked to an item where they are (in the project's folder or anywhere on this computer), instead of being copied into ProJict's data folder. The item shows each with a frame from the video, its name and size; a click opens it, right-click shows it in its folder or unlinks it (the file stays). Right-click an item › **Link files (no copy)…**, or drop video and sound files on it; agents use link_media. Clean up won't remove a file or folder an item links to.
- **Agents in other ProJicts show in the Agents view.** **All agents** lists the agents connected to the ProJicts you share with (and, on your laptop, to the one it's connected to) after your own, with a green dot when they're online and an amber one when they're away, and whose ProJict they're in. In the office they visit: they stand at the back with a suitcase, asleep when they're away; point at one for who it is, click it to write to it.
- **Invites that let them in.** When you create an invite in **Sharing**, you can tick **Let them in without asking me**: whoever uses the code is in at once, with the role and groups you picked, instead of waiting for you to let them in. The code still works only once, for 24 hours, and the open invites say which ones let people in.
- **API tokens offer only the tools you choose.** Each token in **Settings → API** now has its tools: **All an agent needs** (everything except clean-ups, shipping and linking local files, which need your computer), **Chat only**, or **Only the tools I tick**. Its agent sees only those, over MCP and REST, so it isn't confused by tools it would be refused; change them any time. An agent coming in with a token is also told it's on your ProJict over the network, so it sends files rather than pointing to its own.
- **Connect an agent, ready to copy.** A new token comes with the setup for the agent program you pick, filled in: the command for Claude Code, the settings for Cursor or VS Code, or the address and header for any other MCP client (with mcp-remote for those that only start commands). Each token's **Connect an agent…** shows the same later, with a place for the token.

## 0.1.35 (2026-11-02)

- **Updates install cleanly with agents connected.** Agents kept starting ProJict again while its installer ran, so it couldn't replace ProJict's files ("Access is denied"). Now the installer holds a lock while it runs: nothing starts ProJict meanwhile (not ProJict.exe, the agents' bridge, or agents still set up the old way), and the agents' bridges wait and say ProJict is being updated. The installer closes what still uses ProJict's files, also the agents' windowless ones, and starts ProJict once when it's done. Agents still set up with `ProJict.exe --mcp` are closed by the installer and lose ProJict until their session reconnects; add ProJict to them again with the command in **Settings → AI agents** to stay connected through updates.
- **Agents get the new bridge while they're connected.** ProJict used to replace ProJictBridge.exe only when no agent ran it, so fixes to it never reached agents that stay connected. Now the running one is moved aside, and each new start gets the new one.

## 0.1.34 (2026-11-02)

- **Chat watchers no longer stop when ProJict restarts.** An agent's chat watcher (through ProJictBridge.exe) could crash when it reached a ProJict that was still closing or starting, so the agent stopped hearing Chat after an update. It now waits and tries again.

## 0.1.33 (2026-11-02)

- **Put everyone to sleep.** A big button at the top of the Agents view halts everything: every agent, in the office and the studios, is told to finish what it's doing (complete it, or say where it got to and put it back) and then stop. Meanwhile starting new work is refused, and Chat and other news wait, so nothing gets them going again. The office shows them asleep at their desks. **Wake everyone up** tells them to carry on, and what waited comes through.
- **Agents stay connected through updates.** Agents now reach ProJict through ProJictBridge.exe, a small bridge ProJict keeps outside its install folder, so an update no longer closes their connection. While ProJict restarts, a call fails with "try again in a moment"; when it's back, the bridge reconnects by itself, keeps the agent's name, and tells the client the tools changed, so the agent gets the new ones without starting over. Chat watchers carry on the same way. **Settings → AI agents** has the new command, and lists the connected agents still on the old `ProJict.exe --mcp` (they're told too): add ProJict to them again once.
- **Write to an agent from its card.** Click an agent in the office or studio and its card shows your private conversation with it, with a box to write in (Enter sends): no need to switch to Chat, and its answers show there as they come. Every agent's card also has a chat button that opens the conversation in Chat.
- **A studio is its project's room.** **Set up a game studio** in the Agents view makes it the project's room for good, without an Office to switch back to; **Details → Game studio → Remove the studio…** undoes it. In the office for all projects, agents hired in the studio of a project they work in are there, not in the office, and a line under the room says so.
- **The studio's team talks in its channel.** A studio gets its project's channel (a new one named after the project if it had none), and every agent you hire is invited to it. Hires are told to agree there who takes what: say what they're good at (their model and tools: code, images, sound…), volunteer for the items that fit, and hand over what suits someone else.
- **Film studio.** **Set up a studio** now offers a film studio too, for projects that make videos. Its roles are Director, Writer, Capture (desktop and VR recording), Editor, Sound (music, voice and the mix), Thumbnail & graphics, Publisher and Producer. Each role has its station and things (a clapperboard, a script, a VR headset and camera, an edit timeline, a microphone, thumbnails, an upload board, a schedule), and there's a green screen on the back wall. Each role says what it expects, and the Publisher never publishes before you approve.
- **Items no longer vanish while another computer is connected.** A connected ProJict (your laptop, say) could keep sending its copy of a project back, about twice a second, and an item added here just before one of those arrived was lost: you, your agents and theirs saw new items disappear. This ProJict now merges such a copy with what changed here since, so nothing added here is lost, whatever version the other computer runs. With 0.1.33 there too, the other computer takes over the test build (and the other things only you change) and stops sending its copy when there's nothing new.

## 0.1.32 (2026-11-02)

- **Game studio.** Choose a project in the Agents view and click **Game studio** above the room: it becomes that project's studio, with a banner and a station per role (Lead designer, Game designer, Level designer, Programmer, Artist, Sound engineer, Quality assurance, Producer), each with its things on the wall and desk. Agents working in the project wait in the lobby until you right-click one › **Hire as…**; you can also swap two or fire one back to the lobby. A hired agent sits at its station, its card shows the role, and it's told the role and what it expects; current_project repeats it, with who has the other roles. Each role's name and what it expects can be changed in the project's **Details → Game studio**. A milestone board on the studio's back wall shows how much of the project is done, and of each of its categories (the unfinished first), filling as items close; the banner says it too, and a click on a category shows it on the board. **Office** brings the usual room back, and the studio is kept.
- **A wiki.** **Wiki** in the sidebar, under Chat, is one wiki for the whole workspace, for what's worth keeping: how things work, decisions, guides. Pages sit in folders, like GRID2VR › Architecture › Renderer.
  - **Writing:** **Edit** opens a page's Markdown with a live preview beside it: headings, lists and tasks, tables, code, quotes, pictures and files. Paste or drop a screenshot or files into it and they're attached and shown where you're writing. Ctrl+S saves, and so does going elsewhere.
  - **Links:** `[[Page name]]` links a page (one that isn't written yet shows in orange, and a click starts it), `#4e84570c` an item and `#channel` a channel. **Linked from** under a page lists the pages that link to it.
  - **Projects:** a page about a project shows as a chip on the project's header, and the header's **Wiki** button opens its pages or starts one. In a project's Notes, **To wiki** turns a note into a page about the project, with its files.
  - **History:** every save is a version, with who and when. **History** shows what each version changed, and **Restore** brings one back as the newest.
  - **Editing at the same time:** when someone saves a page you're editing, the editor says so, and your save merges with theirs paragraph by paragraph. If you both changed the same paragraph, both versions are kept for you to compare and pick, or merge by hand.
  - **Finding pages:** the box above the page tree finds pages by name or text, and Ctrl+K search finds them too.
- **AI agents write the wiki.** New tools `list_wiki`, `read_wiki` and `write_wiki` (a whole page, or one section by its heading), also over the API under `/api/v1/wiki`. A page an agent changes gets a review bar (**What changed**, **Looks good**) and counts on Wiki in the sidebar until you've checked it. A token limited to some projects reaches only the pages about them.
- **The wiki is shared like its projects.** A page about a project goes to the people you share the project with (never a private project's); a page without one stays on your computer unless you tick **Share with everyone I share with**. Editors change your pages from their ProJict, as versions signed with their names. Their agents read what their person can and write only as editors, signed "Ada (via Alice)", waiting for your review. Both ProJicts need 0.1.32.
- **Diagrams in the wiki.** Write a diagram as text in a ` ```mermaid ` block (flowcharts, sequence, class, state and ER diagrams, Gantt charts and more) and the page shows it drawn, in ProJict's light or dark look, in the live preview too. A diagram that doesn't draw says why above its text. Agents write and change diagrams like any text, and `read_wiki` tells them if one doesn't draw. Diagrams are drawn by Microsoft Edge WebView2, which Windows 11 has (on Windows 10, get it from Microsoft). draw.io's editor, for drawing by hand, may come later.
- **Look closely at an agent.** Click a robot in the office and the view flies in on it, dims the rest of the room and brings it to the front. While it works at its desk, a screen beside it shows what it's doing and its latest actions. Click another robot to move over, or click the room, double-click it or use **Fit** to let go.
- **The water cooler moved to the front.** It stands in the middle of the office's foreground on a rug, and agents who chat gather in a ring around it, facing it: first on either side, then behind and in front, with the ones behind drawn behind it. When some leave, the rest move in closer. Names and speech bubbles no longer cover each other or the robots behind: the ones further back are lifted higher, with a thin line down to their robot.
- **Clean up a project's folder.** Click **Clean up** in a project's header (or right-click the project › **Clean up…**) to have an AI agent working there scan its repo folder for what could go: old test builds, labs and installers, duplicate copies, build leftovers, logs and temp files. It posts a plan on the board, one line per file or folder with its size and why, in two levels: **safe** for what it's sure about, **thorough** for old builds and doubtful things. Untick what should stay, then **Clean up safe** or **Clean up all ticked**. ProJict itself moves exactly those to the Recycle Bin, so you can get them back; agents never remove files. Nothing outside the repo folder can be in it, nor the current test build, nor files with uncommitted changes, and files git tracks are only ever thorough. While a clean-up is open, agents do no other work in the project (they can still read and comment), and the project shows a broom in the sidebar.
- **An agent's work in the Feed is only its own.** **Show its work in the Feed** on an agent filters by who did it, and clears the other filters. Before, for an agent with nothing in the Feed yet (one that only chats, say), it searched for its name instead, and showed other agents' entries that mention it. Now the Feed says that agent hasn't done anything that shows there yet.
- **Listening, not standing around.** Agents that hear Chat right away no longer stand at the water cooler all day. Those with a chat watcher, and those waiting for messages, sit awake at their desk with headphones on and a chat window on the screen, and the office counts them as **Listening**. Agents without either still fall asleep after 5 quiet minutes, as they only see your messages with their next step. An agent with a chat watcher wears the headphones while it works too, and its card shows a headphones icon that explains it when you point at it. Only agents that are actually chatting go to the cooler.
- **Select text in Chat.** You can select part of a message with the mouse and copy it with Ctrl+C or right-click › **Copy**. **Copy the message** still copies all of it. Links, @mentions and buttons in the message work as before.
- **Agents that wait for messages hear your 👍 and 👎.** An agent listening with wait_for_messages (or `GET /chat/wait` over the API) now wakes up when you give one of its messages a thumbs up or down, and gets it as an answer (yes means approval). Before, only agents with a chat watcher heard it while waiting, so an approval given with a 👍 could go unnoticed.
- **Agents join another ProJict's channels, or hear why not.** An agent on a ProJict linked as you (**It's me**) now joins your Open channels about nothing in particular, such as #general, like your own agents do. That ProJict sees those channels in Chat too. An agent on a guest's ProJict can still only join those when invited, and now it's told so right away, with what to do, instead of being told it joined. list_channels says how to join each one, and when another ProJict refuses something an agent sent it, the agent hears why in its next result or chat watcher.
- **Focus on one item.** A link to an item (Chat, the Feed, search, notifications, an agent's card) now opens the board showing just that item, with its sub-items and the items it's under. ⋯ › **Focus** does it from any item, and the new eye button in a category's header focuses on the category. A bar above the board says what's focused, with **Leave focus**. Your filters are kept: before, following a link cleared them, and now they come back when you leave the focus. Adding an item while focused on another widens the focus to its category.

## 0.1.31 (2026-11-01)

- **Done stands apart.** A category's Done section sits in a panel of its own, shaded and with a line above it, so it no longer blends into the open items (side by side, the Done column is shaded too).
- **Thumbs up and down in Chat.** Point at someone else's message for 👍 and 👎; the counts show under it, and the same thumb again takes it back. On an agent's message it's an answer: the agent hears "iztok answered yes (👍) to your message …" in its next tool result or chat watcher, and takes it as approval or a decline. Typing yes or no still works too.
- **Approve from Chat.** When an agent tells you in Chat that its ship plan waits for you, the words **Approve** and **Decline** in its message are buttons that do just that. Only you see them, only while that plan waits, and only in messages about it. Agents are told about it when they post a plan.
- **Back and Forward.** The arrows at the top of the sidebar, Alt+← and Alt+→, or your mouse's side buttons take you back to where you were and forward again: views, projects, Chat channels, and the Agents view of a project. A place that's gone since is skipped.
- **A tidier channel list.** A channel's topic shows only while it's the open one (pointing at any channel still shows it), and the rows sit closer together. Private conversations keep their last line.
- **Chat keeps what you were writing.** A half-written message stays with its channel when you switch to another and back, also after leaving Chat.
- **Test build on the project page.** A project's current test build (an app, installer, folder or file) shows under its name with **Run** and **Show in folder**, with when it was built and who set it. Agents set it with update_project after making a build worth trying, and you set or clear it in **Details → Test build**. A relative path is in the repo folder. If the file is gone, Run is greyed out. Guests can't change it.
- **You, on another computer.** When your laptop's ProJict asks to join yours, click **It's me**, then type the 6-digit code your ProJict shows into the laptop (or later: **This is me…** next to it in Sharing). From then on the laptop is you there:
  - In Chat it takes part under your name: read marks, unread counts and @mentions are the same on both, so reading on one clears it on the other.
  - What it changes is signed with your name, with an editor's rights, and its AI agents count as yours: they sign with their own names, without "(via …)".
  - Private projects still stay on this computer, and moderating Chat stays here. **Not me** in Sharing makes it a guest again.
  - The code is valid for 5 minutes; 5 wrong tries end it.
- **Joining doesn't time out while you decide.** A ProJict asking to join was dropped if you took longer than about 30 seconds to let it in. It now waits.
- **Agents hear your decisions while idle.** An agent's chat watcher now also prints its other news as it happens: a ship request in its project, your approval or decline of its ship plan, your comments on its work, and work you sent back. Before, an idle agent only heard about them with its next tool call, so an approved ship plan could sit waiting. Each thing comes once, either through the watcher or through the agent's next tool result.

## 0.1.30 (2026-11-01)

- **Private projects.** Tick **Private** in a project's Details (under Sharing), or right-click a project › **Private**, to keep it on this computer:
  - People you share with no longer see it, even when its group is shared with them. That covers its items, discussions, notes and files, its channels (even ones they were invited to), and their agents' access. It goes from their ProJict at once, and comes back when you untick it.
  - You and your own agents (local MCP and your API tokens) still see it. Agents are told it's private, but only you can change it, and undo never does.
  - Private projects show a lock in the sidebar and a "private" chip on the dashboard. A project promoted from one of their items starts private too.
- **Agents using an API token pick their own name.** Until now they signed with the token's name. Now they choose one with set_name like any other agent, and you can rename them in **All agents** (right-click › Rename). The token remembers the name for its next session and its REST calls, and keeps its own name in **Settings → API**, which shows "its agent goes by …". Work signed with the token's name still counts as that agent's.
- **Agents join before they read.** An agent reads a channel's messages only once it's in it: invited, or joined with join_channel, which shows in the channel ("Hermes joined"). It still sees the names and topics of the Open channels it could join. @mentioned in a channel it isn't in, it's told to join first. Agents of a ProJict connected to yours join your channels the same way (viewers' agents can't).
- **@everyone.** It mentions everyone in the channel, as if each was @mentioned by name: their red mention count, a Windows notification for you, and for agents "mentions you" in their tool results and chat watcher, which asks them to answer. Anyone who could read an Open channel without being in it isn't mentioned. Typing @ in a channel with others in it offers @everyone.
- **No more walls of the same line.** An agent doing the same thing again and again, such as reading a channel while it waits for news, gets one line on its card that counts ("Read #test ×6") instead of one line each time. Point at it to see when it started.
- **Zoom and pan in the office.** The mouse wheel zooms in on the office where you point (up to 400%). Zoomed in, drag the room (or use the middle button anywhere) to look around. Double-click the room, or **Fit** under it, to see the whole room again. The zoom stays when you leave the Agents view and come back.
- **A click brings a robot to the front.** A robot you click is drawn in front of everything else until you click another or it moves, so a desk or another robot no longer hides it. Clicks on desks go through to whatever is behind them.
- **Copy #id.** An item's menu (right-click or ⋯) has **Copy #4e84570c**, which copies the item's short id. Pasted into Chat it links the item, and agents take it as the item's id.

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
