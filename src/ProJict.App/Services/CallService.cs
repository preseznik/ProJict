using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Chat;

namespace ProJict.App.Services;

/// <summary>
/// Voice calls with agents (#80a4e9c9), one at a time: you call the agents you pick in a private conversation or a
/// channel. Each one that hears Chat right away is rung with a line from ProJict about it, and picks up with answer_call.
/// In the call what you say is heard (Windows speech recognition), a phrase at a time, and posted in the conversation as
/// your message; what an agent in it answers there is read aloud in a voice of its own. Hanging up, or nobody being left,
/// ends it; what was said stays in the conversation.
/// </summary>
public sealed class CallService(AppState state, MainViewModel main, Dispatcher dispatcher)
{
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<(string Agent, string Text)> _toSay = new();
    private ChatChannel? _channel;
    private ISpeechIn? _in;
    private ISpeechOut? _out;
    private CancellationTokenSource? _stopSaying;
    private bool _saying;
    private bool _clockSet;

    /// <summary>Hears you (Windows speech recognition, unless off-screen tests put in their own).</summary>
    public Func<ISpeechIn> SpeechIn { get; set; } = () => new WindowsSpeechIn();
    /// <summary>Reads agents' answers aloud; unset, the voices Settings › Chat picked (Windows' or ElevenLabs'). Tests put in their own.</summary>
    public Func<ISpeechOut>? SpeechOut { get; set; }

    /// <summary>ElevenLabs has its key in Settings › Tools.</summary>
    public bool HasElevenLabsKey => ElevenLabsKey() != null;

    /// <summary>Where ElevenLabs' API is (off-screen tests point it at a stand-in, so nothing reaches ElevenLabs).</summary>
    public static string ElevenLabsApi { get; set; } = "https://api.elevenlabs.io";

    /// <summary>Windows' voices when ElevenLabs fails (tests put in their own).</summary>
    public Func<ISpeechOut> Fallback { get; set; } = () => new WindowsSpeechOut();

    private string? ElevenLabsKey() => main.Tools.Data.Find("ElevenLabs") is { } tool ? main.Tools.KeyOf(tool) : null;

    /// <summary>The voices Settings › Chat picked: ElevenLabs' when it has its key, else Windows'.</summary>
    private ISpeechOut NewSpeechOut() =>
        state.AppSettings.CallVoices == CallVoices.ElevenLabs && ElevenLabsKey() is { } key ? new ElevenLabsSpeechOut(key, api: ElevenLabsApi) : new WindowsSpeechOut();

    /// <summary>Windows' voices, when ElevenLabs fails in a call.</summary>
    private ISpeechOut? _fallback;

    public ChatCall? Current { get; private set; }
    public ChatChannel? Channel => _channel;
    public bool IsOn => Current is { IsOver: false };
    public bool Muted { get; private set; }
    /// <summary>Who's speaking now: an agent read aloud, or you while you're heard.</summary>
    public string? Speaking { get; private set; }
    /// <summary>Why it can't hear you (no microphone or speech recognition), if so.</summary>
    public string? Problem { get; private set; }
    /// <summary>What's been said in the call, and the lines from ProJict about it, oldest first.</summary>
    public List<(string Who, string Text)> Captions { get; } = [];
    public string Me => state.ChatMe.Name;

    /// <summary>Anything about the call changed.</summary>
    public event Action? Changed;
    /// <summary>A call started: its window opens.</summary>
    public event Action<CallService>? Started;

    private ChatRoom Room => state.Chat;

    /// <summary>Where the call is, as an agent names it in post_message: "@Tester" or "#general".</summary>
    private string Where => _channel is { Direct: true } ? "@" + Me : "#" + _channel?.Name;

    /// <summary>Calls the agents in the channel; the problem when it can't, or null.</summary>
    public string? Start(ChatChannel channel, IReadOnlyList<string> agents)
    {
        if (IsOn) return "You're in a call already: hang up first.";
        if (agents.Count == 0) return "Pick at least one agent to call.";
        var call = new ChatCall(channel.Id, Me, agents.Select(a => (a, main.AgentTools.HearsChat(a))), DateTime.Now);
        if (!call.HasSomeone)
            return (agents.Count == 1 ? $"{agents[0]} can't" : "None of them can") +
                   " take a call now: an agent takes calls only while it hears Chat right away (a chat watcher runs for it, or it's waiting for messages).";
        Current = call;
        _channel = channel;
        Captions.Clear();
        Muted = false;
        Problem = null;
        Speaking = null;
        _seen.Clear();
        foreach (var message in channel.Messages) _seen.Add(message.Id);
        Room.Changed += OnChat;
        foreach (var agent in call.Agents)
            if (agent.State == CallState.Ringing)
                Line($"📞 {Me} is calling {agent.Name}…", agent.Name,
                    $"{Me} is calling you: a voice call in {Where}. Pick up with answer_call answer=yes (or answer=no). In the call, what {Me} says comes to " +
                    $"you as messages in {Where}; answer there with post_message, a sentence or two at a time in plain spoken words (no lists, code or " +
                    "links), and ProJict reads it aloud. answer_call answer=hang_up leaves the call.");
            else Captions.Add(("ProJict", $"{agent.Name} {ChatCall.Describe(agent.State)} (it doesn't hear Chat right away)."));
        if (!_clockSet)
        {
            _clock.Tick += (_, _) => Tick();
            _clockSet = true;
        }
        _clock.Start();
        Started?.Invoke(this);
        Changed?.Invoke();
        return null;
    }

    /// <summary>An agent answers (yes, no or hang_up); what to tell it, or throws with the problem.</summary>
    public string Answer(string agent, string answer)
    {
        if (Current is not { IsOver: false } call || call.Of(agent) == null) throw new InvalidOperationException("No call is ringing for you now.");
        var now = DateTime.Now;
        switch (answer.Trim().ToLowerInvariant())
        {
            case "yes" or "pick_up" or "pickup":
                if (call.Answer(agent, true, now) is { } notYes) throw new InvalidOperationException(notYes);
                Line($"📞 {agent} picked up.");
                Listen();
                Changed?.Invoke();
                return $"You're in the call with {Me}, in {Where}. What {Me} says comes to you as messages there; answer with post_message channel={Where}, " +
                       "a sentence or two at a time in plain spoken words: it's read aloud. answer_call answer=hang_up leaves the call.";
            case "no":
                if (call.Answer(agent, false, now) is { } notNo) throw new InvalidOperationException(notNo);
                Line($"📞 {agent} said no.");
                EndIfEmpty();
                Changed?.Invoke();
                return "You said no; the call goes on without you.";
            case "hang_up" or "hangup" or "leave":
                if (call.Leave(agent, now) is { } notLeft) throw new InvalidOperationException(notLeft);
                Line($"📞 {agent} hung up.");
                EndIfEmpty();
                Changed?.Invoke();
                return "You left the call.";
            default:
                throw new InvalidOperationException("answer is yes (pick up), no, or hang_up.");
        }
    }

    public void SetMuted(bool muted)
    {
        if (Muted == muted) return;
        Muted = muted;
        if (muted && Speaking == Me) Speaking = null;
        Changed?.Invoke();
    }

    /// <summary>You hang up: the agents in it, or still ringing, are told it's over.</summary>
    public void HangUp()
    {
        if (Current is not { IsOver: false } call) return;
        foreach (var agent in call.Agents.Where(a => a.State is CallState.Joined or CallState.Ringing))
            Line($"📞 {Me} hung up on {agent.Name}.", agent.Name, $"{Me} hung up: the call in {Where} is over.");
        End();
    }

    /// <summary>Missed rings, every second while there's a call.</summary>
    private void Tick()
    {
        if (Current is not { IsOver: false } call)
        {
            _clock.Stop();
            return;
        }
        foreach (var missed in call.TimeOut(DateTime.Now))
            Line($"📞 {missed.Name} didn't answer.", missed.Name, $"You missed {Me}'s call in {Where}.");
        EndIfEmpty();
        Changed?.Invoke();
    }

    private void EndIfEmpty()
    {
        if (Current is { IsOver: false, HasSomeone: false }) End();
    }

    private void End()
    {
        if (Current is not { IsOver: false } call) return;
        call.End(DateTime.Now);
        Line($"📞 Call ended · {ChatCall.Duration(call.EndedAt!.Value - call.StartedAt)}");
        Room.Changed -= OnChat;
        _clock.Stop();
        _in?.Dispose();
        _in = null;
        _stopSaying?.Cancel();
        _toSay.Clear();
        _out?.Dispose();
        _out = null;
        _fallback?.Dispose();
        _fallback = null;
        _saying = false;
        Speaking = null;
        Changed?.Invoke();
    }

    /// <summary>A line from ProJict in the conversation (and the captions); <paramref name="about"/> hears <paramref name="aboutText"/>.</summary>
    private void Line(string text, string? about = null, string? aboutText = null)
    {
        if (_channel is not { } channel) return;
        var message = Room.PostNotice(channel, state.ChatMe, text, about, aboutText);
        _seen.Add(message.Id);
        Captions.Add(("ProJict", text));
    }

    /// <summary>Starts hearing you, once someone's picked up.</summary>
    private void Listen()
    {
        if (_in != null) return;
        var heard = SpeechIn();
        heard.Heard += text => dispatcher.InvokeAsync(() => OnHeard(text));
        heard.Hearing += on => dispatcher.InvokeAsync(() =>
        {
            if (Muted || _saying || !IsOn) return;
            Speaking = on ? Me : Speaking == Me ? null : Speaking;
            Changed?.Invoke();
        });
        _in = heard;
        Problem = heard.Start();
    }

    /// <summary>A phrase of yours: posted in the conversation (not while muted, or while an agent is read aloud).</summary>
    private void OnHeard(string text)
    {
        if (!IsOn || Muted || _saying || _channel is not { } channel) return;
        Room.Post(channel, state.ChatMe, text);
    }

    /// <summary>What's new in the call's conversation: yours in the captions, an agent's in it read aloud.</summary>
    private void OnChat(ChatChannel channel)
    {
        if (channel != _channel || Current is not { IsOver: false } call) return;
        foreach (var message in channel.Messages.Where(m => !_seen.Contains(m.Id)).ToList())
        {
            _seen.Add(message.Id);
            if (message.Notice || message.Deleted) continue;
            if (!message.Agent && string.Equals(message.By, Me, StringComparison.OrdinalIgnoreCase)) Captions.Add((Me, message.Text));
            else if (message.Agent && call.Of(message.By) is { State: CallState.Joined } && ChatCall.ForVoice(message.Text) is { Length: > 0 } said)
            {
                Captions.Add((message.By, said));
                _toSay.Enqueue((message.By, said));
                _ = SayNext();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Reads the next answer aloud; one at a time, and you aren't heard meanwhile (so the speakers don't answer for you).</summary>
    private async Task SayNext()
    {
        if (_saying || _toSay.Count == 0 || !IsOn) return;
        var (agent, text) = _toSay.Dequeue();
        _saying = true;
        Speaking = agent;
        Changed?.Invoke();
        _out ??= (SpeechOut ?? NewSpeechOut)();
        _stopSaying = new CancellationTokenSource();
        try { await _out.SpeakAsync(await VoiceOfAsync(agent), text, _stopSaying.Token); }
        catch (InvalidOperationException ex) when (_out is ElevenLabsSpeechOut && IsOn)
        {
            // ElevenLabs failed (its key, credits, the network): this one in a Windows voice, and the window says why.
            Problem = ex.Message + " Its answers are read in a Windows voice for now.";
            Changed?.Invoke();
            _fallback ??= Fallback();
            try { await _fallback.SpeakAsync(await VoiceOfAsync(agent, _fallback), text, _stopSaying.Token); }
            catch (Exception again) when (again is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
        _saying = false;
        if (Speaking == agent) Speaking = null;
        Changed?.Invoke();
        await SayNext();
    }

    /// <summary>Each agent's own voice, the same every call (of the voices it reads with: Settings' choice, unless <paramref name="from"/> says).</summary>
    public async Task<string> VoiceOfAsync(string agent, ISpeechOut? from = null)
    {
        _out ??= (SpeechOut ?? NewSpeechOut)();
        var voices = await (from ?? _out).VoicesAsync(CancellationToken.None);
        return voices.Count == 0 ? "" : voices[agent.ToLowerInvariant().Sum(c => c) % voices.Count];
    }
}
