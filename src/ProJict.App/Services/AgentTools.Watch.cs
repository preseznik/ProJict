using System.IO;
using System.Text;
using ProJict.Core.Agents;
using ProJict.Core.Chat;

namespace ProJict.App.Services;

/// <summary>
/// Chat watchers: one line per chat message waiting for an agent, as they come, for clients that can run a command in
/// the background and tell the agent about each line it prints (Claude Code's Monitor tool, for one). So an agent hears
/// Chat while it's idle or busy with other things. Agents on this computer start <c>ProJict.exe --watch-chat --as Ada</c>;
/// API tokens GET /api/v1/chat/watch. What a watcher prints counts as read, so waitingForYou doesn't repeat it.
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
            await write($"Watching ProJict Chat for {agent}: a line for each message for you. Reply with post_message (the line says where).");
            var saidOff = false;
            while (!stop.IsCancellationRequested)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<ChatChannel> listener = _ => changed.TrySetResult();
                var lines = await _dispatcher.InvokeAsync(() =>
                {
                    Chat.Changed += listener;
                    if (!_state.AppSettings.AgentsUseChat)
                    {
                        if (saidOff) return new List<string>();
                        saidOff = true;
                        return new List<string> { "Chat is off for AI agents (the user's Settings › AI agents); still watching in case it comes back on." };
                    }
                    saidOff = false;
                    var waiting = Waiting(identity);
                    foreach (var group in waiting.GroupBy(w => w.Channel)) Delivered(identity, group.Key, group.Select(w => w.Message).ToList());
                    return waiting.Select(w => WatchLine(w.Channel, w.Message, identity)).ToList();
                });
                try
                {
                    foreach (var line in lines) await write(line);
                    if (lines.Count == 0) await Task.WhenAny(changed.Task, Task.Delay(WatchRecheck, stop));
                }
                finally
                {
                    await _dispatcher.InvokeAsync(() => Chat.Changed -= listener);
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
        var mention = !channel.Direct && ChatText.Mentions(message.Text, you.AgentName) ? " (mentions you)" : "";
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
