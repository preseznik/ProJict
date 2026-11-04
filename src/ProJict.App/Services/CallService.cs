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
public sealed partial class CallService(AppState state, MainViewModel main, Dispatcher dispatcher)
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

    /// <summary>Hears you: the recognizer Settings › Chat picked, from the microphone it picked (off-screen tests put in their own).</summary>
    public Func<ISpeechIn>? SpeechIn { get; set; }
    /// <summary>Windows' old offline recognizer, when the one picked can't (tests put in their own).</summary>
    public Func<ISpeechIn>? OldSpeechIn { get; set; }

    /// <summary>The recognizer Settings › Chat picked (#4980bbee); Windows' old one when that one can't start (no key, no model).</summary>
    private ISpeechIn NewSpeechIn()
    {
        switch (state.AppSettings.CallRecognizer)
        {
            case CallRecognizer.ElevenLabs when ElevenLabsKey() is { } key:
                return new PhraseSpeechIn(SoundIn(), new ElevenLabsSpeechToText(key, api: ElevenLabsApi));
            case CallRecognizer.Whisper when WhisperModel.For(state.AppSettings.WhisperModel) is var model && WhisperModels.Has(model):
                return new PhraseSpeechIn(SoundIn(), new WhisperSpeechToText(WhisperModels.PathOf(model),
                    [.. Current?.Agents.Select(a => a.Name) ?? [], .. main.AgentTools.ConnectedAgentNames(), "ProJict"]));
            case CallRecognizer.WindowsOnline:
                return new WindowsOnlineSpeechIn(SoundIn());
        }
        return NewOldSpeechIn();
    }

    private ISpeechIn NewOldSpeechIn() =>
        OldSpeechIn?.Invoke() ?? new WindowsSpeechIn(Microphones.DeviceOf(state.AppSettings.CallMicrophone) >= 0 ? SoundIn() : null);

    /// <summary>With Whisper picked, its model loads while the call rings (once, then it stays loaded): ready when you speak.</summary>
    private void PreloadWhisper()
    {
        if (SpeechIn != null || state.AppSettings.CallRecognizer != CallRecognizer.Whisper) return;
        var model = WhisperModel.For(state.AppSettings.WhisperModel);
        if (WhisperModels.Has(model)) _ = WhisperSpeechToText.Load(WhisperModels.PathOf(model)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>The recognizer picked can't be used (no key, no model): it says so, and the old one hears you meanwhile.</summary>
    private string? RecognizerNote()
    {
        var model = WhisperModel.For(state.AppSettings.WhisperModel);
        return state.AppSettings.CallRecognizer switch
        {
            CallRecognizer.ElevenLabs when ElevenLabsKey() == null =>
                "ElevenLabs' speech to text needs ElevenLabs' key in Settings › Tools: Windows' offline recognizer hears you for now.",
            CallRecognizer.Whisper when !WhisperModels.Has(model) =>
                $"Whisper's {model.Name} model isn't downloaded yet (Settings › Chat): Windows' offline recognizer hears you for now.",
            _ => null,
        };
    }
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

    // ───────────── Your microphone (#b39e8f23) ─────────────

    /// <summary>How loud your microphone is now, 0 to 1 (the bar on your tile).</summary>
    public double MicLevel { get; private set; }
    /// <summary>The microphone's level changed (often: the window moves its bar, nothing else).</summary>
    public event Action? LevelChanged;
    /// <summary>Not hearing you: which microphone it listens to and where to change it; null once it hears you.</summary>
    public string? Hint { get; private set; }
    /// <summary>How long a call hears nothing from your microphone before it says so.</summary>
    public static TimeSpan QuietFor { get; set; } = TimeSpan.FromSeconds(12);
    /// <summary>This loud (0 to 1) counts as hearing sound.</summary>
    public const double SoundLevel = 0.04;

    private DateTime? _listeningSince;
    private bool _heardSound;

    /// <summary>The microphone started listening (a call with an agent, or a person).</summary>
    private void StartedListening()
    {
        _listeningSince = DateTime.Now;
        _heardSound = false;
        Hint = null;
        MicLevel = 0;
    }

    /// <summary>How loud the microphone is (UI thread); sound clears the hint.</summary>
    private void OnLevel(double level)
    {
        if (!IsOn) return;
        MicLevel = Muted ? 0 : Math.Clamp(level, 0, 1);
        if (!Muted && level >= SoundLevel) HeardYou();
        LevelChanged?.Invoke();
    }

    /// <summary>It hears you: the hint goes.</summary>
    private void HeardYou()
    {
        _heardSound = true;
        if (Hint == null) return;
        Hint = null;
        Changed?.Invoke();
    }

    /// <summary>Every second: a while without a sound from the microphone (not muted), and the call says which one it listens to.</summary>
    private void CheckHeard()
    {
        if (_heardSound || Hint != null || Muted || _listeningSince is not { } since || DateTime.Now - since < QuietFor) return;
        var microphone = _in is WindowsOnlineSpeechIn ? Microphones.DefaultName + " (Windows' online dictation always uses it)" : Microphones.Describe(state.AppSettings.CallMicrophone);
        Hint = $"Not hearing you? ProJict listens to {microphone}, and the bar under your name moves when it " +
               "hears sound. Pick the one you talk into in Settings › Chat › Microphone for calls, and check that Windows lets desktop apps use the " +
               "microphone (Windows Settings › Privacy & security › Microphone).";
    }

    private void StoppedListening()
    {
        _listeningSince = null;
        _heardSound = false;
        Hint = null;
        MicLevel = 0;
    }
    public string Me => state.ChatMe.Name;

    /// <summary>Anything about the call changed.</summary>
    public event Action? Changed;
    /// <summary>A call started: its window opens.</summary>
    public event Action<CallService>? Started;

    /// <summary>The room the call's conversation is in: yours, or the host's copy when you call its agents in its channel (#546dab2b).</summary>
    private ChatRoom _room = state.Chat;
    private ChatRoom Room => _room;

    /// <summary>The host's connection when the call is in one of its channels; null in your own.</summary>
    private Sharing.HostConnection? _host;

    /// <summary>
    /// Whether the agent in the channel can take a call now: one of yours that hears Chat right away, a guest's agent that
    /// does there, or, in a host's channel, one of the host's agents that does there (#546dab2b).
    /// </summary>
    public bool CanTakeCall(ChatChannel channel, string agent) =>
        main.Guest.ConnectionOf(channel) is { } host ? host.ListeningThere.Contains(agent)
            : main.AgentTools.HearsChat(agent) || main.Host.AgentListening(agent);

    /// <summary>A guest's agent: it reads your channels as "#name (yours)", so its ring names the channel that way.</summary>
    private string WhereFor(string agent) =>
        _host == null && _channel is { Direct: false } channel && !main.AgentTools.HearsChat(agent) && main.Host.AgentListening(agent)
            ? $"\"#{channel.Name} ({Me}'s)\"" : Where;

    /// <summary>Where the call is, as an agent names it in post_message: "@Tester" or "#general".</summary>
    private string Where => _channel is { Direct: true } ? "@" + Me : "#" + _channel?.Name;

    /// <summary>Calls the agents in the channel; the problem when it can't, or null.</summary>
    public string? Start(ChatChannel channel, IReadOnlyList<string> agents)
    {
        if (IsOn) return "You're in a call already: hang up first.";
        if (agents.Count == 0) return "Pick at least one agent to call.";
        _host = null;
        _room = state.Chat;
        // A person in another ProJict: their voice comes over the sharing connection.
        if (agents.Count == 1 && channel.Members.FirstOrDefault(m => string.Equals(m.Name, agents[0], StringComparison.OrdinalIgnoreCase)) is { Agent: false })
            return StartWithPerson(channel, agents[0]);
        var call = new ChatCall(channel.Id, Me, agents.Select(a => (a, CanTakeCall(channel, a))), DateTime.Now);
        if (!call.HasSomeone)
            return (agents.Count == 1 ? $"{agents[0]} can't" : "None of them can") +
                   " take a call now: an agent takes calls only while it hears Chat right away (a chat watcher runs for it, or it's waiting for messages).";
        Current = call;
        _channel = channel;
        PreloadWhisper();
        _host = main.Guest.ConnectionOf(channel);
        _room = _host?.Chat ?? state.Chat;
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
                    $"{Me} is calling you: a voice call in {WhereFor(agent.Name)}. Pick up with answer_call answer=yes (or answer=no). In the call, what {Me} says " +
                    $"comes to you as messages in {WhereFor(agent.Name)}; answer there with post_message, a sentence or two at a time in plain spoken words " +
                    "(no lists, code or links), and ProJict reads it aloud. answer_call answer=hang_up leaves the call.");
            else Captions.Add(("ProJict", $"{agent.Name} {ChatCall.Describe(agent.State)} (it doesn't hear Chat right away)."));
        StartClock();
        Started?.Invoke(this);
        Changed?.Invoke();
        return null;
    }

    private void StartClock()
    {
        if (!_clockSet)
        {
            _clock.Tick += (_, _) => Tick();
            _clockSet = true;
        }
        _clock.Start();
    }

    /// <summary>The host's agent answered your call in its channel (#546dab2b).</summary>
    public void AnswerFromHost(Sharing.HostConnection host, System.Text.Json.Nodes.JsonObject message)
    {
        if (_host != host || _channel?.Id.ToString() != (string?)message["channelId"] || (string?)message["agent"] is not { } agent) return;
        try { Answer(agent, (string?)message["answer"] ?? ""); }
        catch (InvalidOperationException) { }
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
        if (call.Person != null)
        {
            HangUpPerson(call);
            return;
        }
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
        if (call.Person != null)
        {
            TickPerson(call);
            CheckHeard();
            Changed?.Invoke();
            return;
        }
        CheckHeard();
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
        _host = null;
        _room = state.Chat;
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
        StopSound();
        StoppedListening();
        Speaking = null;
        Changed?.Invoke();
    }

    /// <summary>A line from ProJict in the conversation (and the captions); <paramref name="about"/> hears <paramref name="aboutText"/>.</summary>
    private void Line(string text, string? about = null, string? aboutText = null)
    {
        if (_channel is not { } channel) return;
        Captions.Add(("ProJict", text));
        // Your call with the host's agents in its channel: the host writes your call's lines there, and rings them with them.
        if (_host != null && Current is { Person: null })
        {
            main.Guest.ChatOp(_host, "callLine", ("channel", channel.Id.ToString()), ("text", text), ("about", about), ("aboutText", aboutText));
            return;
        }
        // A conversation in another ProJict: its host writes the lines about the call there.
        if (!state.Chat.Channels.Contains(channel)) return;
        var message = state.Chat.PostNotice(channel, state.ChatMe, text, about, aboutText);
        _seen.Add(message.Id);
    }

    /// <summary>Starts hearing you, once someone's picked up.</summary>
    private void Listen()
    {
        if (_in != null) return;
        var heard = SpeechIn?.Invoke() ?? NewSpeechIn();
        Hear(heard);
        Problem = heard.Start();
        if (Problem != null && SpeechIn == null && heard is not WindowsSpeechIn) FallBack(Problem);
        else if (Problem == null)
        {
            if (SpeechIn == null) Problem = RecognizerNote();
            StartedListening();
        }
    }

    /// <summary>
    /// The recognizer picked can't go on (dictation off, no credits, no network): the call says why, and Windows' old
    /// recognizer hears you for the rest of it.
    /// </summary>
    private void FallBack(string why)
    {
        if (!IsOn) return;
        if (_in is WindowsSpeechIn || _in == null)
        {
            Problem = why;
            Changed?.Invoke();
            return;
        }
        _in.Dispose();
        var old = NewOldSpeechIn();
        Hear(old);
        Problem = why + " Windows' offline recognizer hears you for now.";
        if (old.Start() is { } problem) Problem = why + " " + problem;
        else StartedListening();
        Changed?.Invoke();
    }

    /// <summary>What the recognizer hears: your phrases, when you speak, how loud, and when it can't go on.</summary>
    private void Hear(ISpeechIn heard)
    {
        heard.Heard += text => dispatcher.InvokeAsync(() =>
        {
            if (_in == heard) OnHeard(text);
        });
        heard.Hearing += on => dispatcher.InvokeAsync(() =>
        {
            if (_in != heard || Muted || _saying || !IsOn) return;
            if (on) HeardYou();
            Speaking = on ? Me : Speaking == Me ? null : Speaking;
            Changed?.Invoke();
        });
        heard.Level += level => dispatcher.InvokeAsync(() =>
        {
            if (_in == heard) OnLevel(level);
        });
        heard.Failed += why => dispatcher.InvokeAsync(() =>
        {
            if (_in == heard) FallBack(why);
        });
        _in = heard;
    }

    /// <summary>A phrase of yours: posted in the conversation (not while muted, or while an agent is read aloud).</summary>
    private void OnHeard(string text)
    {
        if (!IsOn || Muted || _saying || _channel is not { } channel) return;
        // In the host's channel, as you write there; in yours, as you.
        if (_host != null) main.Guest.ChatPost(_host, channel, text, null, [], null);
        else Room.Post(channel, state.ChatMe, text);
    }

    /// <summary>What's new in the call's conversation: yours in the captions, an agent's in it read aloud.</summary>
    private void OnChat(ChatChannel channel)
    {
        if (channel != _channel || Current is not { IsOver: false } call) return;
        foreach (var message in channel.Messages.Where(m => !_seen.Contains(m.Id)).ToList())
        {
            _seen.Add(message.Id);
            if (message.Notice || message.Deleted) continue;
            if (!message.Agent && (string.Equals(message.By, Me, StringComparison.OrdinalIgnoreCase) || string.Equals(message.By, _host?.MyName, StringComparison.OrdinalIgnoreCase)))
                Captions.Add((Me, message.Text));
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
