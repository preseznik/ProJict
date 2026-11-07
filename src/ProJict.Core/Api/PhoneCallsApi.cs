using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;

namespace ProJict.Core.Api;

/// <summary>A phone call as the phone sees it; the app fills it from its call service.</summary>
public sealed record PhoneCallView(
    Guid Id,
    Guid ChannelId,
    Guid OriginId,
    string Agent,
    CallState AgentState,
    bool Over,
    bool Muted,
    string? Speaking,
    string? Problem,
    string? Preparing,
    string Hearing,
    string Voices,
    string? Language,
    DateTime StartedAt,
    DateTime? EndedAt,
    string? EndReason);

/// <summary>
/// Phone calls API v1 (ProJict PC #d6b701ce, Android #d03a6a2f): you call one agent from your phone while the app is
/// open. Control is plain REST on the phone token; audio and events go over one WebSocket per call. This class checks
/// requests and shapes the JSON; the app runs the call with the same call service as a call from the PC, except that
/// the phone's audio is heard instead of the PC's microphone and the agent's voice goes back to the phone instead of the
/// PC's speakers. See docs/PHONE-CALLS-API.md.
/// </summary>
public static class PhoneCallsApi
{
    public const int Version = 1;
    public const int MaxAgentName = 100;
    /// <summary>The phone opens the call's media connection within this, or the call ends (it may have lost the start's answer).</summary>
    public static readonly TimeSpan MediaWithin = TimeSpan.FromSeconds(20);

    /// <summary>Whether the token may use calls at all (throws for the phone to show), and whether it may start or change one.</summary>
    public static void Check(ApiToken? token, bool write, DateTime now)
    {
        if (token is not { ActsAsYou: true }) throw new McpToolException("Only a phone token reaches calls.", ToolProblem.Forbidden);
        if (token.Frozen) throw new McpToolException("This phone token is paused.", ToolProblem.Forbidden);
        if (token.IsExpired(now)) throw new McpToolException("This phone token expired. Pair again.", ToolProblem.Forbidden);
        if (write && token.ReadOnly) throw new McpToolException("This phone token is read only: it can't make calls.", ToolProblem.Forbidden);
    }

    /// <summary>A call's full ID as the API gave it; short prefixes aren't accepted.</summary>
    public static Guid CallId(string value)
    {
        if (Guid.TryParseExact(value, "D", out var id) || Guid.TryParseExact(value, "N", out id)) return id;
        throw new McpToolException("Use the call's full ID from the phone API.");
    }

    /// <summary>POST /me/calls: {"agent": name, "language": code or null}; anything else is refused.</summary>
    public static (string Agent, string? Language) ParseStart(JsonObject body)
    {
        Only(body, "agent", "language");
        if (body["agent"] is not JsonValue a || !a.TryGetValue<string>(out var agent) || agent.Trim() is not { Length: > 0 and <= MaxAgentName } name ||
            name.Any(char.IsControl))
            throw new McpToolException("Say which agent to call: \"agent\": its name.");
        string? language = null;
        if (body["language"] != null)
        {
            if (body["language"] is not JsonValue l || !l.TryGetValue<string>(out var code) || CallLanguage.For(code.Trim()) is not { } known)
                throw new McpToolException("\"language\" is one of the call languages ProJict knows (or null to detect it).");
            language = known.Code;
        }
        return (name, language);
    }

    /// <summary>POST /me/calls/{call}/mute: {"muted": true or false}.</summary>
    public static bool ParseMute(JsonObject body)
    {
        Only(body, "muted");
        return body["muted"] is JsonValue v && v.TryGetValue<bool>(out var muted) ? muted : throw new McpToolException("Say \"muted\": true or false.");
    }

    /// <summary>A body that must be empty (end, stop speaking).</summary>
    public static void Empty(JsonObject body) => Only(body);

    private static void Only(JsonObject body, params string[] names)
    {
        if (body.Any(p => !names.Contains(p.Key)))
            throw new McpToolException(names.Length == 0 ? "This takes no body." : $"Give only {string.Join(" and ", names)}.");
    }

    /// <summary>GET /me/calls: the agents you can call, your call (if any), and whether the PC is busy with another.</summary>
    public static JsonObject Overview(IEnumerable<(string Name, bool CanTakeCall)> agents, PhoneCallView? call, bool busy, bool canCall) => new()
    {
        ["apiVersion"] = Version,
        ["canCall"] = canCall,
        ["busy"] = busy,
        ["agents"] = new JsonArray(agents.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["canTakeCall"] = a.CanTakeCall }).ToArray()),
        ["call"] = call == null ? null : Describe(call),
    };

    /// <summary>A started call: its state, and how to reach its media connection.</summary>
    public static JsonObject Started(PhoneCallView call) => new()
    {
        ["apiVersion"] = Version,
        ["call"] = Describe(call),
        ["media"] = Media(call.Id),
    };

    public static JsonObject Detail(PhoneCallView call) => new() { ["apiVersion"] = Version, ["call"] = Describe(call) };

    /// <summary>How the phone talks: one WebSocket, PCM both ways, JSON events as text.</summary>
    public static JsonObject Media(Guid call) => new()
    {
        ["path"] = $"{ApiRoutes.Base}/me/calls/{call}/media",
        ["encoding"] = "pcm_s16le",
        ["sampleRate"] = PhoneCallMedia.SampleRate,
        ["channels"] = 1,
        ["frameMs"] = PhoneCallMedia.FrameMs,
        ["maxFrameBytes"] = PhoneCallMedia.MaxUplinkFrameBytes,
        ["connectWithinSeconds"] = (int)MediaWithin.TotalSeconds,
    };

    public static JsonObject Describe(PhoneCallView call) => new()
    {
        ["id"] = call.Id.ToString(),
        ["channelId"] = call.ChannelId.ToString(),
        ["originId"] = call.OriginId.ToString(),
        ["agent"] = call.Agent,
        ["state"] = StateOf(call),
        ["agentState"] = AgentStateOf(call.AgentState),
        ["muted"] = call.Muted,
        ["speaking"] = call.Over ? null : call.Speaking,
        ["problem"] = call.Problem,
        ["preparing"] = call.Over ? null : call.Preparing,
        ["hearing"] = call.Hearing,
        ["voices"] = call.Voices,
        ["language"] = call.Language,
        ["startedAt"] = call.StartedAt.ToString("O", CultureInfo.InvariantCulture),
        ["endedAt"] = call.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["endReason"] = call.Over ? call.EndReason ?? EndReasonOf(call.AgentState) : null,
    };

    /// <summary>ringing, in_call or ended.</summary>
    public static string StateOf(PhoneCallView call) => call.Over ? "ended" : call.AgentState switch
    {
        CallState.Joined => "in_call",
        CallState.Ringing => "ringing",
        _ => "ending",
    };

    public static string AgentStateOf(CallState state) => state switch
    {
        CallState.Ringing => "ringing",
        CallState.Joined => "joined",
        CallState.Declined => "declined",
        CallState.Missed => "missed",
        CallState.Left => "left",
        _ => "cant_now",
    };

    /// <summary>Why it ended, when the app didn't say: from where the agent got to.</summary>
    public static string EndReasonOf(CallState state) => state switch
    {
        CallState.Declined => "declined",
        CallState.Missed => "no_answer",
        CallState.Left => "agent_left",
        CallState.CantNow => "cant_take_calls",
        _ => "ended",
    };

    // ───────────── Media events (text frames on the call's WebSocket) ─────────────

    public static JsonObject StateEvent(PhoneCallView call) => new() { ["type"] = "state", ["call"] = Describe(call) };

    /// <summary>A line of the call: what you said (as heard), what the agent said, or a note from ProJict.</summary>
    public static JsonObject CaptionEvent(string who, string? name, string text) => new()
    {
        ["type"] = "caption",
        ["who"] = who,
        ["name"] = name,
        ["text"] = text,
    };
}
