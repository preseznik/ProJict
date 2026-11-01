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
/// waitingForYou doesn't repeat it (and the other way round).
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Longest message text in a watcher's line; read_channel has the rest.</summary>
    private const int WatchTextLength = 600;
    /// <summary>A watcher checks this often anyway (Chat may have been turned on or off).</summary>
    private static readonly TimeSpan WatchRecheck = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, int> _watchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a chat watcher runs for the agent now.</summary>
    public bool IsWatched(string agent)
    {
        lock (_watchers) return _watchers.ContainsKey(agent);
    }

    /// <summary>The command an agent on this computer runs to watch Chat (with the data folder when it isn't the usual one, as for --mcp).</summary>
    public string WatchCommand(string agent)
    {
        var remembered = _state.AppSettings.DataRoot is { } root ? Path.GetFullPath(root) : null;
        var data = string.Equals(remembered, _state.Store.DataRoot, StringComparison.OrdinalIgnoreCase) ? "" : $" --data \"{_state.Store.DataRoot}\"";
        return $"\"{Environment.ProcessPath}\" --watch-chat --as \"{agent}\"{data}";
    }

    /// <summary>
    /// Writes a line per chat message for the agent as they come (a first line says it's watching), until stopped or
    /// writing fails because the other end went away.
    /// </summary>
    public async Task WatchChatAsync(McpSession identity, Func<string, Task> write, CancellationToken stop)
    {
        var agent = identity.AgentName;
        lock (_watchers) _watchers[agent] = _watchers.GetValueOrDefault(agent) + 1;
        await _dispatcher.InvokeAsync(() => _main.Agents.Renamed());
        try
        {
            await write($"Watching ProJict Chat for {agent}: a line for each message for you (reply with post_message, the line says where), " +
                        "and for ship requests, your ship plan's approval, comments on your work and work sent back.");
            var saidOff = false;
            while (!stop.IsCancellationRequested)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<ChatChannel> listener = _ => changed.TrySetResult();
                // A change in a project (a comment, a ship decision) wakes it too.
                Action<Project> touched = _ => changed.TrySetResult();
                var rooms = new List<ChatRoom>();
                var lines = await _dispatcher.InvokeAsync(() =>
                {
                    Chat.Changed += listener;
                    _state.ProjectChanged += touched;
                    // Other ProJicts' channels too: a message there wakes it as well.
                    rooms.AddRange(SharedRooms(identity));
                    foreach (var room in rooms) room.Changed += listener;
                    var found = new List<string>();
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
                    }
                    found.AddRange(WatchWorkLines(identity));
                    return found;
                });
                try
                {
                    foreach (var line in lines) await write(line);
                    if (lines.Count == 0) await Task.WhenAny(changed.Task, Task.Delay(WatchRecheck, stop));
                }
                finally
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        Chat.Changed -= listener;
                        _state.ProjectChanged -= touched;
                        foreach (var room in rooms) room.Changed -= listener;
                    });
                }
            }
        }
        finally
        {
            lock (_watchers)
                if (--_watchers[agent] == 0) _watchers.Remove(agent);
            if (!_dispatcher.HasShutdownStarted) await _dispatcher.InvokeAsync(() => _main.Agents.Renamed());
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
        return lines;
    }

    /// <summary>👍 and 👎 on the agent's messages, as "[answer] …" lines; told once, by the watcher or a tool result.</summary>
    private List<string> WatchAnswerLines(McpSession identity)
    {
        List<McpSession> same;
        lock (_live) same = _live.Where(s => string.Equals(s.AgentName, identity.AgentName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var session in same) identity.Noticed.UnionWith(session.Noticed);
        var lines = AnswerLines(identity).Select(l => "[answer] " + l).ToList();
        foreach (var session in same) session.Noticed.UnionWith(identity.Noticed);
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

    /// <summary>Once per session, with its first chat news: how to keep hearing Chat (unless a watcher already runs for it).</summary>
    private string? WatchTip(McpSession session)
    {
        if (IsWatched(session.AgentName) || !session.Noticed.Add("tip:watch-chat")) return null;
        return session.Remote
            ? $"To hear Chat while you're idle or busy, keep GET {ProJict.Core.Api.ApiRoutes.Base}/chat/watch running in the background if your client can " +
              "(e.g. curl -sN with your Authorization header, under Claude Code's Monitor tool): it prints a line per message for you."
            : $"To hear Chat while you're idle or busy, run {WatchCommand(session.AgentName)} in the background if your client can tell you about each " +
              "line a command prints (e.g. Claude Code's Monitor tool): it prints a line per message for you." +
              (session.Name == null ? " Pick your name with set_name first, and use it there." : "");
    }
}
