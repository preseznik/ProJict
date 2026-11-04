using System.IO;
using System.Text;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Chat watchers: one line per chat message waiting for an agent, as they come, for clients that can run a command in
/// the background and tell the agent about each line it prints (Claude Code's Monitor tool, for one). So an agent hears
/// Chat while it's idle or busy with other things. Its other news comes the same way: ship requests and your decision on
/// its plan, your comments on its work, and work you sent back. Agents on this computer start
/// <c>ProJict.exe --watch-chat --as Ada</c>; API tokens GET /api/v1/chat/watch. What a watcher prints counts as told, so
/// waitingForYou doesn't repeat it (and the other way round). When several watchers run for one agent (one left over from
/// an earlier session, say), each prints every line, so none takes some for itself.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Longest message text in a watcher's line; read_channel has the rest.</summary>
    private const int WatchTextLength = 600;
    /// <summary>A watcher checks this often anyway (Chat may have been turned on or off).</summary>
    private static readonly TimeSpan WatchRecheck = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, int> _watchers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When an agent's last watcher stopped (Claude Code's Monitor ends after 30 minutes): it's told to start it again.</summary>
    private readonly Dictionary<string, DateTime> _watchStopped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long lines stay in an agent's feed for its watchers that haven't printed them yet.</summary>
    private static readonly TimeSpan FeedKeptFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The lines found for an agent's watchers, numbered, so every watcher for the agent prints each of them; and what
    /// counts as told for them all.
    /// </summary>
    private sealed class WatchFeed
    {
        public readonly List<(long Number, DateTime At, string Line)> Lines = [];
        public long Next;
        public readonly HashSet<string> Noticed = [];
        public TaskCompletionSource Added = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, WatchFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);

    private WatchFeed FeedOf(string agent)
    {
        lock (_feeds)
        {
            if (!_feeds.TryGetValue(agent, out var feed)) _feeds[agent] = feed = new WatchFeed();
            return feed;
        }
    }

    /// <summary>Adds lines to the agent's feed and wakes its other watchers.</summary>
    private static void Publish(WatchFeed feed, IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0) return;
        TaskCompletionSource added;
        lock (feed)
        {
            var now = DateTime.Now;
            foreach (var line in lines) feed.Lines.Add((feed.Next++, now, line));
            feed.Lines.RemoveAll(l => now - l.At > FeedKeptFor);
            added = feed.Added;
            feed.Added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        added.TrySetResult();
    }

    /// <summary>The feed's lines from <paramref name="from"/> on, and where the next ones start.</summary>
    private static (List<string> Lines, long Next) Take(WatchFeed feed, long from)
    {
        lock (feed) return ([.. feed.Lines.Where(l => l.Number >= from).Select(l => l.Line)], feed.Next);
    }

    /// <summary>Whether a chat watcher runs for the agent now.</summary>
    public bool IsWatched(string agent)
    {
        lock (_watchers) return _watchers.ContainsKey(agent);
    }

    /// <summary>The command an agent on this computer runs to watch Chat (with the data folder when it isn't the usual one, as for --mcp).</summary>
    public string WatchCommand(string agent) => $"\"{BridgeInstaller.Command}\" --watch-chat --as \"{agent}\"{DataArgument}";

    /// <summary>
    /// The PermissionRequest hook's command, so agents ask you in Chat (#54ef1c15); null without the bridge (ProJict.exe
    /// itself would start the app instead).
    /// </summary>
    public string? PermissionHookCommand => BridgeInstaller.BridgePath != null ? $"\"{BridgeInstaller.BridgePath}\" --permission-hook{DataArgument}" : null;

    /// <summary>The data folder for the bridge, when it isn't the one ProJict remembers.</summary>
    private string DataArgument
    {
        get
        {
            var remembered = _state.AppSettings.DataRoot is { } root ? Path.GetFullPath(root) : null;
            return string.Equals(remembered, _state.Store.DataRoot, StringComparison.OrdinalIgnoreCase) ? "" : $" --data \"{_state.Store.DataRoot}\"";
        }
    }

    /// <summary>
    /// Writes a line per chat message for the agent as they come (a first line says it's watching), until stopped or
    /// writing fails because the other end went away.
    /// </summary>
    public async Task WatchChatAsync(McpSession identity, Func<string, Task> write, CancellationToken stop)
    {
        var agent = identity.AgentName;
        // What it was told before ProJict restarted (or by its sessions) isn't printed again.
        Recall(identity);
        WatchFeed feed;
        int watching;
        // Counted and fed together, so a watcher stopping meanwhile can't take the feed away from under it.
        lock (_watchers)
        {
            watching = _watchers[agent] = _watchers.GetValueOrDefault(agent) + 1;
            _watchStopped.Remove(agent);
            feed = FeedOf(agent);
        }
        long next;
        lock (feed) next = feed.Next;
        await _dispatcher.InvokeAsync(() =>
        {
            _main.Agents.Renamed();
            // It hears Chat now: connected ProJicts can call it (#546dab2b).
            _main.Host.SendPresence();
            _main.Guest.SendPresence();
        });
        try
        {
            await write($"Watching ProJict Chat for {agent}: a line for each message for you (reply with post_message, the line says where), " +
                        "and for ship requests, your ship plan's approval, comments on your work and work sent back.");
            if (watching > 1)
                Publish(feed, [$"[ProJict] {watching} chat watchers run for {agent} now; each prints every line. If one is left over from an earlier " +
                               "session, stop it (you'd hear things twice)."]);
            var saidOff = false;
            while (!stop.IsCancellationRequested)
            {
                Task fed;
                lock (feed) fed = feed.Added.Task;
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<ChatChannel> listener = _ => changed.TrySetResult();
                // A change in a project (a comment, a ship decision) wakes it too.
                Action<Project> touched = _ => changed.TrySetResult();
                // Put to sleep or woken: it hears at once.
                Action sleepy = () => changed.TrySetResult();
                var rooms = new List<ChatRoom>();
                var lines = await _dispatcher.InvokeAsync(() =>
                {
                    Chat.Changed += listener;
                    _state.ProjectChanged += touched;
                    SleepChanged += sleepy;
                    // A shared thing freed or taken over too (a lease running out is found by the regular recheck).
                    ResourcesChanged += sleepy;
                    // Other ProJicts' channels too: a message there wakes it as well.
                    rooms.AddRange(SharedRooms(identity));
                    foreach (var room in rooms) room.Changed += listener;
                    // What its other watchers printed counts as told for this one too.
                    identity.Noticed.UnionWith(feed.Noticed);
                    var found = WatchSleepLines(identity);
                    // Asleep: only that; chat and other news wait until it's woken.
                    if (IsAsleep) return found;
                    if (!_state.AppSettings.AgentsUseChat)
                    {
                        if (!saidOff) found.Add("Chat is off for AI agents (the user's Settings › AI agents); still watching in case it comes back on.");
                        saidOff = true;
                    }
                    else
                    {
                        saidOff = false;
                        var waiting = Waiting(identity);
                        foreach (var group in waiting.GroupBy(w => w.Channel)) Delivered(identity, group.Key, group.Select(w => w.Message).ToList());
                        found.AddRange(waiting.Select(w => WatchLine(w.Channel, w.Message, identity)));
                        found.AddRange(SharedWatchLines(identity));
                        found.AddRange(SharedRefusalLines(identity).Select(l => "[ProJict] " + l));
                        found.AddRange(WatchAnswerLines(identity));
                        found.AddRange(WatchClaimLines(identity));
                    }
                    found.AddRange(WatchWorkLines(identity));
                    feed.Noticed.UnionWith(identity.Noticed);
                    return found;
                });
                try
                {
                    // Whichever watcher found them, every one of the agent's watchers prints them.
                    Publish(feed, lines);
                    var (mine, after) = Take(feed, next);
                    next = after;
                    foreach (var line in mine) await write(line);
                    if (mine.Count == 0) await Task.WhenAny(changed.Task, fed, Task.Delay(WatchRecheck, stop));
                }
                finally
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        Chat.Changed -= listener;
                        _state.ProjectChanged -= touched;
                        SleepChanged -= sleepy;
                        ResourcesChanged -= sleepy;
                        foreach (var room in rooms) room.Changed -= listener;
                    });
                }
            }
        }
        finally
        {
            lock (_watchers)
                if (--_watchers[agent] == 0)
                {
                    _watchers.Remove(agent);
                    _watchStopped[agent] = DateTime.Now;
                    lock (_feeds) _feeds.Remove(agent);
                }
            if (!_dispatcher.HasShutdownStarted)
                await _dispatcher.InvokeAsync(() =>
                {
                    _main.Agents.Renamed();
                    _main.Host.SendPresence();
                    _main.Guest.SendPresence();
                });
        }
    }

    /// <summary>
    /// The agent's other news for its watcher, as "[ProJict] …" lines. Its sessions connected now share what's been told
    /// with the watcher, so a thing comes once, by whichever way first; ship requests come for the projects they work in.
    /// </summary>
    private List<string> WatchWorkLines(McpSession identity)
    {
        List<McpSession> same;
        lock (_live) same = _live.Where(s => string.Equals(s.AgentName, identity.AgentName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var session in same) identity.Noticed.UnionWith(session.Noticed);
        var lines = WorkLines(identity, same.SelectMany(s => _main.Agents.ProjectsOf(s)).Distinct().ToList()).Select(l => "[ProJict] " + l).ToList();
        foreach (var session in same) session.Noticed.UnionWith(identity.Noticed);
        Keep(identity);
        return lines;
    }

    /// <summary>
    /// Going to sleep and waking up, as "[ProJict] …" lines; told once, by the watcher or a tool result. The wake line comes
    /// when it was told it's asleep either way, since an agent asleep and idle hears it only from its watcher.
    /// </summary>
    private List<string> WatchSleepLines(McpSession identity)
    {
        List<McpSession> same;
        lock (_live) same = _live.Where(s => string.Equals(s.AgentName, identity.AgentName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var session in same) identity.Noticed.UnionWith(session.Noticed);
        var lines = SleepLines(identity).Select(l => "[ProJict] " + l).ToList();
        foreach (var session in same) session.Noticed.UnionWith(identity.Noticed);
        Keep(identity);
        return lines;
    }

    /// <summary>Messages another agent is answering, as "[#channel] Lark is answering …" lines; told once, by the watcher or a tool result.</summary>
    private List<string> WatchClaimLines(McpSession identity)
    {
        List<McpSession> same;
        lock (_live) same = _live.Where(s => string.Equals(s.AgentName, identity.AgentName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var session in same) identity.Noticed.UnionWith(session.Noticed);
        var lines = ClaimLines(identity, watcher: true);
        foreach (var session in same) session.Noticed.UnionWith(identity.Noticed);
        Keep(identity);
        return lines;
    }

    /// <summary>👍 and 👎 on the agent's messages, as "[answer] …" lines; told once, by the watcher or a tool result.</summary>
    private List<string> WatchAnswerLines(McpSession identity)
    {
        List<McpSession> same;
        lock (_live) same = _live.Where(s => string.Equals(s.AgentName, identity.AgentName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var session in same) identity.Noticed.UnionWith(session.Noticed);
        var lines = AnswerNews(identity).Select(a => (a.Approval ? "[approval] " : "[answer] ") + a.Text).ToList();
        foreach (var session in same) session.Noticed.UnionWith(identity.Noticed);
        Keep(identity);
        return lines;
    }

    /// <summary>A watcher on the agent pipe: lines go back down the pipe until the watcher closes its end.</summary>
    public async Task WatchOverPipeAsync(string agent, Stream pipe, TextReader input, CancellationToken stop)
    {
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(stop);
        // The watcher never writes: its end closing is how we hear it stopped.
        var closed = Task.Run(async () =>
        {
            try { while (await input.ReadLineAsync(gone.Token) != null) { } }
            catch (Exception) { }
        });
        var identity = new McpSession { Name = agent, ClientName = "watch-chat" };
        var watching = WatchChatAsync(identity, async line =>
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), gone.Token);
            await pipe.FlushAsync(gone.Token);
        }, gone.Token);
        await Task.WhenAny(closed, watching);
        gone.Cancel();
        try { await watching; }
        catch (Exception) { }
    }

    /// <summary>
    /// "[#grid2vr] Tester (mentions you): can you look at the HUD? (id 1a2b3c4d; reply: post_message channel=#grid2vr)",
    /// "[private] Tester: …", or a line about it ("[#build] Tester invited you to #build").
    /// </summary>
    private static string WatchLine(ChatChannel channel, ChatMessage message, McpSession you)
    {
        var where = Display(channel, you);
        var place = channel.Direct ? "private" : where;
        if (message.Notice)
            return $"[{place}] {OneLine(string.Equals(message.About, you.AgentName, StringComparison.OrdinalIgnoreCase) ? message.AboutText ?? message.Text : message.Text)}";
        var mention = !channel.Direct && ChatRoom.Mentions(channel, message, you.AgentName) ? " (mentions you)" : "";
        var text = OneLine(message.Text);
        if (text.Length > WatchTextLength) text = text[..WatchTextLength].TrimEnd() + $"… (read_channel {where} for the rest)";
        var files = message.Attachments is { Count: > 0 } attached ? $" [{Count(attached.Count, "file")}]" : "";
        return $"[{place}] {message.By}{mention}: {text}{files} (id {ShortId(message.Id)}; reply: post_message channel={where})";
    }

    /// <summary>Line breaks become ⏎, so each message stays one line.</summary>
    private static string OneLine(string text) =>
        string.Join(" ⏎ ", text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>
    /// The agent hears Chat on its own: a watcher runs for it, or it's waiting for messages. Otherwise it reads what came
    /// only when it next does something.
    /// </summary>
    public bool HearsChat(string agent) =>
        IsWatched(agent) || _main.Agents.Sessions.Any(s => s.Listening && string.Equals(s.AgentName, agent, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Keeping an agent hearing Chat: its watcher stopped (at most once per stop), else the tip on its first call. Only for
    /// an agent with a name of its own, that doesn't hear Chat now.
    /// </summary>
    private IEnumerable<(string Key, string Line)> ListenLines(McpSession session)
    {
        if (session.Name == null || HearsChat(session.AgentName)) yield break;
        DateTime? stopped;
        lock (_watchers) stopped = _watchStopped.TryGetValue(session.AgentName, out var at) ? at : null;
        if (stopped is { } when)
            yield return ($"watchstopped:{when.Ticks}",
                $"Your chat watcher stopped at {when.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)}, so you don't hear Chat now: " +
                (session.Remote ? $"keep GET {ProJict.Core.Api.ApiRoutes.Base}/chat/watch running in the background again" : $"run {WatchCommand(session.AgentName)} in the background again") +
                ". Start it again each time it ends (Claude Code's Monitor ends after 30 minutes), so you keep answering when you're written to.");
        else if (WatchTip(session) is { } tip)
            yield return ("tip:watch-chat:given", tip);
    }

    /// <summary>Once per session, with its first call or chat news: how to keep hearing Chat (unless a watcher already runs for it).</summary>
    private string? WatchTip(McpSession session)
    {
        if (IsWatched(session.AgentName) || !session.Noticed.Add("tip:watch-chat")) return null;
        return session.Remote
            ? $"To hear Chat while you're idle or busy, keep GET {ProJict.Core.Api.ApiRoutes.Base}/chat/watch running in the background if your client can " +
              "(e.g. curl -sN with your Authorization header, under Claude Code's Monitor tool): it prints a line per message for you."
            : $"To hear Chat while you're idle or busy, run {WatchCommand(session.AgentName)} in the background if your client can tell you about each " +
              "line a command prints (e.g. Claude Code's Monitor tool): it prints a line per message for you. Start it again each time it ends " +
              "(Claude Code's Monitor ends after 30 minutes), or you stop hearing Chat." +
              (session.Name == null ? " Pick your name with set_name first, and use it there." : "");
    }
}
