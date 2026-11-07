using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Api;
using ProJict.Core.Chat;

namespace ProJict.App.Services;

/// <summary>
/// Calls from your phone (#d6b701ce), at /api/v1/me/calls: you call one of your agents in your private conversation
/// with it, the same call as from this computer (see <see cref="CallService"/>), with its audio over the call's media
/// connection. A phone call belongs to the token that started it: only that token sees and controls it. One call at a
/// time on this computer, as always. The call ends when you hang up, the agent leaves, the media connection goes, or the
/// token is paused, renewed or revoked. See PhoneCallsApi and docs/PHONE-CALLS-API.md.
/// </summary>
public sealed partial class AgentTools
{
    private sealed class PhoneCall(Guid callId, ApiToken token, PhoneCallMedia media, DateTime startedAt)
    {
        public Guid CallId { get; } = callId;
        public ApiToken Token { get; } = token;
        public Guid TokenId => Token.Id;
        public PhoneCallMedia Media { get; } = media;
        public DateTime StartedAt { get; } = startedAt;
        public bool MediaAttached { get; set; }
        public string? EndReason { get; set; }
        public int CaptionsSent { get; set; }
        public string? LastState { get; set; }
        public PhoneCallView? Last { get; set; }
        public bool Finished { get; set; }
    }

    private PhoneCall? _phoneCall;
    private bool _phoneCallsHeard;

    private CallService Calls => _main.Calls;

    /// <summary>GET /me/calls.</summary>
    private JsonObject MeCalls(ApiToken? token)
    {
        PhoneCallsApi.Check(token, write: false, DateTime.Now);
        var mine = _phoneCall is { } c && c.TokenId == token!.Id ? c : null;
        var view = mine == null ? null : ViewOf(mine);
        var busy = Calls.IsOn && (mine == null || Calls.Current?.Id != mine.CallId);
        return PhoneCallsApi.Overview(ConnectedAgentNames().Select(n => (n, HearsChat(n))), view, busy, canCall: !token!.ReadOnly);
    }

    /// <summary>POST /me/calls: rings the agent; the phone then opens the call's media connection.</summary>
    private JsonObject MeStartCall(ApiToken? token, JsonObject body)
    {
        var now = DateTime.Now;
        PhoneCallsApi.Check(token, write: true, now);
        var (asked, language) = PhoneCallsApi.ParseStart(body);
        if (Calls.IsOn) throw new McpToolException("ProJict is in a call already: hang up there first.", ToolProblem.Conflict);
        var agent = ConnectedAgentNames().FirstOrDefault(n => string.Equals(n, asked, StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpToolException($"{asked} isn't connected to ProJict now: GET /me/calls lists the agents you can call.", ToolProblem.NotFound);
        if (!HearsChat(agent))
            throw new McpToolException($"{agent} can't take a call now: an agent takes calls only while it hears Chat right away.", ToolProblem.Conflict);
        ChatChannel direct;
        try { direct = _state.Chat.OpenDirect(_state.ChatMe, agent, otherIsAgent: true); }
        catch (ChatException ex) { throw new McpToolException(ex.Message, ToolProblem.Conflict); }
        var media = new PhoneCallMedia();
        if (Calls.StartFromPhone(direct, agent, media, language) is { } problem) throw new McpToolException(problem, ToolProblem.Conflict);
        if (!_phoneCallsHeard)
        {
            Calls.Changed += OnPhoneCallChanged;
            _phoneCallsHeard = true;
        }
        var call = new PhoneCall(media.CallId, token!, media, now);
        _phoneCall = call;
        OnPhoneCallChanged();
        return PhoneCallsApi.Started(ViewOf(call));
    }

    /// <summary>GET /me/calls/{call}.</summary>
    private JsonObject MeCall(ApiToken? token, string id)
    {
        PhoneCallsApi.Check(token, write: false, DateTime.Now);
        return PhoneCallsApi.Detail(ViewOf(MyPhoneCall(token!, id)));
    }

    /// <summary>POST /me/calls/{call}/mute, /stop-speaking or /end.</summary>
    private JsonObject MeCallAction(ApiToken? token, string id, string action, JsonObject body)
    {
        PhoneCallsApi.Check(token, write: true, DateTime.Now);
        var call = MyPhoneCall(token!, id);
        switch (action)
        {
            case "end":
                PhoneCallsApi.Empty(body);
                // Ending one that's over already says how it ended: the phone may be retrying after a lost answer.
                if (!call.Finished) HangUpPhoneCall(call, "hung_up");
                break;
            case "mute":
                var muted = PhoneCallsApi.ParseMute(body);
                Live(call);
                call.Media.Muted = muted;
                Calls.SetMuted(muted);
                break;
            case "stop-speaking":
                PhoneCallsApi.Empty(body);
                Live(call);
                Calls.StopSpeaking();
                break;
            default:
                throw new McpToolException("A call takes POST /mute, /stop-speaking or /end.", ToolProblem.NotFound);
        }
        return PhoneCallsApi.Detail(ViewOf(call));
    }

    private static void Live(PhoneCall call)
    {
        if (call.Finished) throw new McpToolException("That call is over.", ToolProblem.Conflict);
    }

    private PhoneCall MyPhoneCall(ApiToken token, string id)
    {
        var key = PhoneCallsApi.CallId(id);
        return _phoneCall is { } call && call.CallId == key && call.TokenId == token.Id
            ? call : throw new McpToolException("No such call for this phone.", ToolProblem.NotFound);
    }

    /// <summary>
    /// (UI thread.) The call's media connection opens: the phone's microphone is heard from now on and the call's events
    /// go to it. One connection per call; throws for what the phone should be told.
    /// </summary>
    public PhoneCallMedia AttachPhoneMedia(ApiToken token, string id)
    {
        PhoneCallsApi.Check(token, write: true, DateTime.Now);
        var call = MyPhoneCall(token, id);
        Live(call);
        if (call.MediaAttached) throw new McpToolException("This call's media is connected already.", ToolProblem.Conflict);
        call.MediaAttached = true;
        call.LastState = null; // the new connection hears where the call is at once
        OnPhoneCallChanged();
        return call.Media;
    }

    /// <summary>(UI thread.) The media connection closed: without it the call can't go on.</summary>
    public void PhoneMediaClosed(PhoneCallMedia media, string reason)
    {
        if (_phoneCall is { } call && call.Media == media && !call.Finished) HangUpPhoneCall(call, reason);
    }

    /// <summary>(UI thread.) The token was paused, renewed or revoked: its call ends.</summary>
    public void EndPhoneCalls(ApiToken token, string reason)
    {
        if (_phoneCall is { } call && call.TokenId == token.Id && !call.Finished) HangUpPhoneCall(call, reason);
    }

    private void HangUpPhoneCall(PhoneCall call, string reason)
    {
        call.EndReason ??= reason;
        if (Calls.Current?.Id == call.CallId && Calls.IsOn) Calls.HangUp();
        else FinishPhoneCall(call, ViewOf(call));
    }

    /// <summary>Anything about the call changed (UI thread): the phone hears the new state and the call's new lines.</summary>
    private void OnPhoneCallChanged()
    {
        if (_phoneCall is not { Finished: false } call) return;
        if (Calls.Current?.Id != call.CallId)
        {
            FinishPhoneCall(call, ViewOf(call));
            return;
        }
        var view = ViewOf(call);
        call.Media.Muted = Calls.Muted;
        var captions = Calls.Captions;
        for (; call.CaptionsSent < captions.Count; call.CaptionsSent++)
        {
            var (who, text) = captions[call.CaptionsSent];
            var role = who == "ProJict" ? "projict" : string.Equals(who, Calls.Me, StringComparison.OrdinalIgnoreCase) ? "you" : "agent";
            call.Media.Post(PhoneCallsApi.CaptionEvent(role, role == "agent" ? who : null, text));
        }
        if (view.Over)
        {
            FinishPhoneCall(call, view);
            return;
        }
        if (!call.MediaAttached && DateTime.Now - call.StartedAt > PhoneCallsApi.MediaWithin)
        {
            HangUpPhoneCall(call, "media_not_connected");
            return;
        }
        // Expiry has no event of its own: the call's clock (every second) notices it.
        if (call.Token.Frozen || call.Token.IsExpired(DateTime.Now))
        {
            HangUpPhoneCall(call, "token_invalid");
            return;
        }
        var state = PhoneCallsApi.Describe(view).ToJsonString();
        if (state == call.LastState) return;
        call.LastState = state;
        call.Media.Post(PhoneCallsApi.StateEvent(view));
    }

    private static void FinishPhoneCall(PhoneCall call, PhoneCallView view)
    {
        if (call.Finished) return;
        call.Finished = true;
        call.Last = view with { Over = true, EndedAt = view.EndedAt ?? DateTime.Now, EndReason = view.EndReason ?? call.EndReason };
        call.Media.End(PhoneCallsApi.StateEvent(call.Last));
    }

    /// <summary>The call as the phone sees it: live from the call service while it's the call going on, else as it ended.</summary>
    private PhoneCallView ViewOf(PhoneCall call)
    {
        if (call.Finished && call.Last != null) return call.Last;
        if (Calls.Current is not { } current || current.Id != call.CallId)
            return call.Last ?? new PhoneCallView(call.CallId, Guid.Empty, Guid.Empty, "", CallState.Left, true, false, null, null, null,
                Calls.PhoneHearing, Calls.PhoneVoices, null, call.StartedAt, DateTime.Now, call.EndReason ?? "ended");
        var agent = current.Agents.FirstOrDefault();
        var speaking = Calls.Speaking == null ? null : string.Equals(Calls.Speaking, Calls.Me, StringComparison.OrdinalIgnoreCase) ? "you" : "agent";
        var view = new PhoneCallView(current.Id, Calls.Channel?.Id ?? Guid.Empty, Calls.Origin?.Id ?? Guid.Empty, agent?.Name ?? "", agent?.State ?? CallState.Left,
            current.IsOver, Calls.Muted, speaking, Calls.Problem ?? Calls.Hint, Calls.Preparing, Calls.PhoneHearing, Calls.PhoneVoices, Calls.Language,
            current.StartedAt, current.EndedAt, call.EndReason);
        call.Last = view;
        return view;
    }
}
