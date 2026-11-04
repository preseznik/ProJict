using System.Text.Json.Nodes;
using ProJict.Core.Chat;
using ProJict.Core.Sharing;

namespace ProJict.App.Services;

/// <summary>How a call's messages reach the other person: over their ProJict's connection to yours (you host it), or yours to theirs.</summary>
public sealed record PersonLink(string Person, Action<JsonObject> Send, bool OnHost);

/// <summary>
/// Calls with a person in another ProJict (#2356ddca): in a private conversation between you and them, over the sharing
/// connection. Ringing goes as callRing, picking up or saying no as callAnswer, hanging up as callHangUp; then your
/// voice goes as callAudio frames (16 kHz) while theirs plays here. The conversation lives in the host's ProJict, so
/// only the host writes its lines about the call. Both ProJicts need calls (0.1.43); an older one never answers.
/// </summary>
public sealed partial class CallService
{
    /// <summary>The microphone Settings › Chat picked (tests put in their own).</summary>
    public Func<ISoundIn> SoundIn { get; set; } = () => new MicrophoneIn(Microphones.DeviceOf(state.AppSettings.CallMicrophone));
    /// <summary>The speakers (likewise).</summary>
    public Func<ISoundOut> SoundOut { get; set; } = () => new SpeakersOut();
    /// <summary>A ring, every few seconds while a call rings here (tests put in their own, so nothing sounds).</summary>
    public Action Ring { get; set; } = () => System.Media.SystemSounds.Asterisk.Play();

    private PersonLink? _link;
    private ISoundIn? _mic;
    private ISoundOut? _speakers;
    private DateTime _lastLoud;

    /// <summary>They're calling you: Accept or Decline.</summary>
    public bool IsIncoming => Current is { Incoming: true, IsOver: false, Person.State: CallState.Ringing };

    /// <summary>
    /// The way to reach a person in a private conversation, while they're connected: a guest of yours (in your channels),
    /// or the owner of the ProJict whose channel it is.
    /// </summary>
    public PersonLink? LinkFor(ChatChannel channel, string person)
    {
        if (state.Chat.Channels.Contains(channel)) return main.Host.LinkTo(person);
        var connection = main.Guest.Connections.FirstOrDefault(c => c.Chat?.Channels.Contains(channel) == true);
        return connection != null && string.Equals(connection.HostName, person, StringComparison.OrdinalIgnoreCase) ? main.Guest.LinkTo(connection) : null;
    }

    private string? StartWithPerson(ChatChannel channel, string person)
    {
        if (LinkFor(channel, person) is not { } link) return $"{person} isn't connected now: you can call them while their ProJict is connected to yours.";
        var call = ChatCall.WithPerson(channel.Id, Me, person, incoming: false, DateTime.Now);
        Begin(call, channel, link);
        link.Send(Wire.Message("callRing", ("callId", call.Id.ToString()), ("channelId", channel.Id.ToString())));
        Line($"📞 {Me} is calling {person}…");
        Started?.Invoke(this);
        Changed?.Invoke();
        return null;
    }

    private void Begin(ChatCall call, ChatChannel channel, PersonLink link)
    {
        Current = call;
        _channel = channel;
        _link = link;
        Captions.Clear();
        Muted = false;
        Problem = null;
        Speaking = null;
        _seen.Clear();
        foreach (var message in channel.Messages) _seen.Add(message.Id);
        StartClock();
    }

    /// <summary>A call message from the other person's ProJict (on the UI thread).</summary>
    public void OnPersonMessage(PersonLink link, JsonObject message)
    {
        var id = Guid.TryParse((string?)message["callId"], out var g) ? g : Guid.Empty;
        var ours = Current is { IsOver: false } now && now.Id == id && now.Person is { } person &&
                   string.Equals(person.Name, link.Person, StringComparison.OrdinalIgnoreCase);
        switch ((string?)message["type"])
        {
            case "callRing":
                var channel = ChannelOf((string?)message["channelId"]);
                if (IsOn || channel == null)
                {
                    link.Send(Wire.Message("callAnswer", ("callId", id.ToString()), ("yes", false), ("busy", IsOn)));
                    return;
                }
                Begin(ChatCall.WithPerson(channel.Id, Me, link.Person, incoming: true, DateTime.Now, id), channel, link);
                Line($"📞 {link.Person} is calling {Me}…");
                Ring();
                Started?.Invoke(this);
                break;
            case "callAnswer" when ours && Current!.Person!.State == CallState.Ringing && !Current.Incoming:
                if ((bool?)message["yes"] == true)
                {
                    Current.Answer(link.Person, true, DateTime.Now);
                    Line($"📞 {link.Person} picked up.");
                    StartSound();
                }
                else
                {
                    Current.Answer(link.Person, false, DateTime.Now);
                    Line((bool?)message["busy"] == true ? $"📞 {link.Person} is in another call." : $"📞 {link.Person} said no.");
                    End();
                }
                break;
            case "callHangUp" when ours:
                Current!.Leave(link.Person, DateTime.Now);
                Line($"📞 {link.Person} hung up.");
                End();
                break;
            case "callAudio" when ours && Current!.Person!.State == CallState.Joined:
                if ((string?)message["data"] is not { } data) return;
                byte[] pcm;
                try { pcm = Convert.FromBase64String(data); }
                catch (FormatException) { return; }
                _speakers?.Play(pcm);
                Heard(link.Person, pcm);
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }

    /// <summary>The conversation a call rings in: one of yours, or of the ProJict whose channels you see.</summary>
    private ChatChannel? ChannelOf(string? id) =>
        Guid.TryParse(id, out var channelId)
            ? state.Chat.Channels.FirstOrDefault(c => c.Id == channelId) ??
              main.Guest.Connections.SelectMany(c => c.Chat?.Channels ?? []).FirstOrDefault(c => c.Id == channelId)
            : null;

    /// <summary>You pick up a call that's ringing here.</summary>
    public void Accept()
    {
        if (!IsIncoming || _link is not { } link || Current is not { } call) return;
        call.Answer(link.Person, true, DateTime.Now);
        link.Send(Wire.Message("callAnswer", ("callId", call.Id.ToString()), ("yes", true)));
        Line($"📞 {Me} picked up.");
        StartSound();
        Changed?.Invoke();
    }

    /// <summary>You say no to a call that's ringing here.</summary>
    public void Decline()
    {
        if (!IsIncoming || _link is not { } link || Current is not { } call) return;
        call.Answer(link.Person, false, DateTime.Now);
        link.Send(Wire.Message("callAnswer", ("callId", call.Id.ToString()), ("yes", false)));
        Line($"📞 {Me} said no.");
        End();
    }

    /// <summary>Starts your microphone and the speakers, once you're both in it.</summary>
    private void StartSound()
    {
        if (_mic != null) return;
        var mic = SoundIn();
        mic.Captured += pcm => dispatcher.InvokeAsync(() => SendSound(pcm));
        _mic = mic;
        var speakers = SoundOut();
        _speakers = speakers;
        Problem = mic.Start() ?? speakers.Start();
        if (Problem == null) StartedListening();
    }

    /// <summary>A frame of your voice to them (not while muted).</summary>
    private void SendSound(byte[] pcm)
    {
        // Its level moves the bar on your tile.
        OnLevel(CallVoice.Loudness(pcm));
        if (Muted || _link is not { } link || Current is not { IsOver: false, Person.State: CallState.Joined } call) return;
        link.Send(Wire.Message("callAudio", ("callId", call.Id.ToString()), ("data", Convert.ToBase64String(pcm))));
        Heard(Me, pcm);
    }

    /// <summary>Whose tile lights up: whoever's frame was loud, for a moment after.</summary>
    private void Heard(string who, byte[] pcm)
    {
        if (CallVoice.Level(pcm) < CallVoice.Speaking) return;
        _lastLoud = DateTime.Now;
        if (Speaking == who) return;
        Speaking = who;
        Changed?.Invoke();
    }

    /// <summary>Every second while there's a call with a person: ringing (here), rings unanswered, and speaking fading out.</summary>
    private void TickPerson(ChatCall call)
    {
        var now = DateTime.Now;
        if (call.Person is not { } person) return;
        if (Speaking != null && now - _lastLoud > TimeSpan.FromMilliseconds(600)) Speaking = null;
        if (person.State != CallState.Ringing) return;
        if (now - person.Since >= ChatCall.RingFor)
        {
            if (!call.Incoming)
            {
                _link?.Send(Wire.Message("callHangUp", ("callId", call.Id.ToString())));
                Line($"📞 {person.Name} didn't answer.");
            }
            else Line($"📞 {Me} missed {person.Name}'s call.");
            person.State = CallState.Missed;
            End();
            return;
        }
        if (call.Incoming && (int)(now - person.Since).TotalSeconds % 3 == 0) Ring();
    }

    /// <summary>You hang up on the person.</summary>
    private void HangUpPerson(ChatCall call)
    {
        _link?.Send(Wire.Message("callHangUp", ("callId", call.Id.ToString())));
        Line($"📞 {Me} hung up.");
        End();
    }

    /// <summary>The microphone and speakers go, with the link.</summary>
    private void StopSound()
    {
        _mic?.Dispose();
        _mic = null;
        _speakers?.Dispose();
        _speakers = null;
        _link = null;
    }
}
