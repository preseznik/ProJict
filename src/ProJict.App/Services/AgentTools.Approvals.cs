using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;

namespace ProJict.App.Services;

/// <summary>
/// Approving agents' requests in ProJict Chat (#54ef1c15): Claude Code's or Codex's PermissionRequest hook
/// (<c>ProJictBridge.exe --permission-hook</c>) brings a request here, the agent asks you privately what it wants to do,
/// and only your 👍 or 👎 (as this ProJict's owner) answers it. Unanswered, the usual prompt decides.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>A request waiting for your answer: its message, and the answer when it comes.</summary>
    private sealed class Asked(ChatChannel channel, ChatMessage message, ChatActor agent)
    {
        public ChatChannel Channel { get; } = channel;
        public ChatMessage Message { get; } = message;
        public ChatActor Agent { get; } = agent;
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<ChatChannel>? Listener { get; set; }
    }

    /// <summary>The bridge's request on the pipe: asks you in Chat, waits for your answer, and sends it back as the hook's decision.</summary>
    public async Task ApproveOverPipeAsync(JsonObject hook, string? agent, Stream pipe, TextReader input, CancellationToken stop)
    {
        var asked = await _dispatcher.InvokeAsync(() => Ask(hook, agent));
        if (asked == null) return;
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(stop);
        // The bridge only writes its request: its end closing means it stopped waiting (the hook's time ran out).
        _ = Task.Run(async () =>
        {
            try { while (await input.ReadLineAsync(gone.Token) != null) { } }
            catch (Exception) { }
            try { gone.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        bool? answer;
        try { answer = await asked.Answer.Task.WaitAsync(gone.Token); }
        catch (OperationCanceledException) { answer = null; }
        await _dispatcher.InvokeAsync(() => Settle(asked, answer));
        if (answer is not { } allow) return;
        var reply = new JsonObject { ["hookOutput"] = PermissionAsk.HookOutput(allow, allow ? null : $"{_state.UserName} refused in ProJict Chat.") };
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n"), stop);
            await pipe.FlushAsync(stop);
        }
        catch (Exception) { }
    }

    /// <summary>(UI thread.) The agent's private message asking you; null when asking in Chat is off.</summary>
    private Asked? Ask(JsonObject hook, string? named)
    {
        if (!_state.AppSettings.ApproveInChat || !_state.AppSettings.AgentsUseChat) return null;
        var cwd = (hook["cwd"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null;
        // Which agent: the one named in the hook, else the one connected from that folder (the latest), else its program.
        var session = named == null && cwd != null
            ? _main.Agents.Sessions.Where(s => SameFolder(s.WorkingDirectory, cwd)).OrderByDescending(s => s.LastActive ?? s.ConnectedAt).FirstOrDefault()
            : null;
        var client = hook.ContainsKey("turn_id") ? "Codex" : session?.Client ?? "Claude Code";
        var name = named ?? session?.AgentName ?? client;
        var actor = new ChatActor(name, true);
        try
        {
            var channel = Chat.OpenDirect(actor, _state.ChatMe.Name, otherIsAgent: false);
            var message = Chat.Post(channel, actor, PermissionAsk.Question(hook, client));
            var asked = new Asked(channel, message, actor);
            asked.Listener = changed =>
            {
                if (changed != channel || asked.Answer.Task.IsCompleted) return;
                // Only you answer it, as this ProJict's owner: not an agent, nor someone you share with.
                if (message.Reactions?.LastOrDefault(r => string.Equals(r.By, _state.ChatMe.Name, StringComparison.OrdinalIgnoreCase)) is { } yours)
                    asked.Answer.TrySetResult(yours.Up);
            };
            Chat.Changed += asked.Listener;
            _state.StatusText = $"{name} asks you in Chat: may it {PermissionAsk.Describe(hook).What}?";
            return asked;
        }
        catch (ChatException) { return null; }
    }

    /// <summary>(UI thread.) The request's message says how it ended.</summary>
    private void Settle(Asked asked, bool? answer)
    {
        if (asked.Listener != null) Chat.Changed -= asked.Listener;
        var outcome = answer switch
        {
            true => $"✓ {_state.UserName} allowed it.",
            false => $"✗ {_state.UserName} refused it.",
            null => "No answer here in time, so it's asked in the agent's own window.",
        };
        try { Chat.Edit(asked.Channel, asked.Agent, asked.Message.Id, asked.Message.Text + "\n\n" + outcome); }
        catch (ChatException) { }
    }

    private static bool SameFolder(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
