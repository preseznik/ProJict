using System.Text.Json.Nodes;

namespace ProJict.Core.Chat;

/// <summary>What you set your own availability to (#c1c1a259): Online, Away, Busy, or Offline (appear offline).</summary>
public enum UserStatus { Online, Away, Busy, Offline }

/// <summary>Whether someone is here: connected and available, busy, away, or not connected (or appearing offline).</summary>
public enum PresenceState { Offline, Away, Busy, Online }

/// <summary>
/// Who's online, busy, away or offline. People are away when nothing was typed or clicked on their computer for a while
/// (or when they set their status to Away), busy when they set Busy, and offline when their ProJict isn't connected or they
/// chose Appear offline. ProJicts that share pass on who's here by name, the offline left out.
/// </summary>
public static class Presence
{
    /// <summary>Idle this long, and they're away.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(10);

    public static PresenceState Of(bool connected, TimeSpan idle, UserStatus status = UserStatus.Online) =>
        !connected || status == UserStatus.Offline ? PresenceState.Offline
        : status == UserStatus.Busy ? PresenceState.Busy
        : status == UserStatus.Away || idle >= AwayAfter ? PresenceState.Away
        : PresenceState.Online;

    /// <summary>The more present of the two (someone connected twice, say, idle on one computer and active on the other).</summary>
    public static PresenceState Best(PresenceState a, PresenceState b) => a > b ? a : b;

    /// <summary>"online", "busy", "away", "offline".</summary>
    public static string Word(PresenceState state) => state.ToString().ToLowerInvariant();

    /// <summary>How the status picker labels each choice (#c1c1a259).</summary>
    public static string Label(UserStatus status) => status switch
    {
        UserStatus.Away => "Away",
        UserStatus.Busy => "Busy",
        UserStatus.Offline => "Appear offline",
        _ => "Online",
    };

    /// <summary>Parses "online", "busy", "away" or "offline" (case-insensitive); null for anything else.</summary>
    public static PresenceState? ParseState(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "online" => PresenceState.Online,
        "busy" => PresenceState.Busy,
        "away" => PresenceState.Away,
        "offline" => PresenceState.Offline,
        _ => null,
    };

    /// <summary>What AI agents are told when the user isn't available at the moment (#c1c1a259); null while they are.</summary>
    public static string? UnavailableNotice(string userName, UserStatus status, PresenceState effectiveState) => status switch
    {
        UserStatus.Away => $"{userName} is away right now and not available at the moment. Don't expect quick replies, approvals or manual tests until they're back; keep working on what doesn't need them.",
        UserStatus.Busy => $"{userName} is busy right now and not available at the moment. Don't interrupt or expect replies, approvals or manual tests until they're available again; keep working on what doesn't need them.",
        UserStatus.Offline => $"{userName} is offline right now and not available at the moment. Don't address them in Chat or wait on approvals or manual tests until they're back; keep working on what doesn't need them.",
        _ when effectiveState == PresenceState.Away => $"{userName} is away from their computer right now and not available at the moment.",
        _ => null,
    };

    /// <summary>
    /// What an agent is told after writing to people or agents who aren't here (#4507be3d): it waits for them, but no answer
    /// should be waited on. Null when everyone named is online.
    /// </summary>
    public static string? NotHereNotice(IEnumerable<(string Name, PresenceState State)> people)
    {
        var notHere = people.Where(p => p.State != PresenceState.Online)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Name} is {Word(p.State)}").ToList();
        if (notHere.Count == 0) return null;
        var who = notHere.Count == 1 ? notHere[0] : string.Join(", ", notHere[..^1]) + " and " + notHere[^1];
        return $"{who}: your message waits for them, but don't wait on an answer; carry on with what doesn't need them " +
               "(check_presence says who's here).";
    }

    /// <summary>What check_presence advises, by how present someone is.</summary>
    public static string Advice(PresenceState state) => state switch
    {
        PresenceState.Online => "here: you can write to them",
        PresenceState.Busy => "busy: don't interrupt; write only what can wait",
        PresenceState.Away => "away: a message waits for them, but don't expect a quick answer",
        _ => "offline: a message waits until they're back; don't wait on an answer",
    };

    /// <summary>Who's online, busy or away, by name: what one ProJict tells another.</summary>
    public static JsonObject ToJson(IReadOnlyDictionary<string, PresenceState> people)
    {
        var json = new JsonObject();
        foreach (var (name, state) in people.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            if (state != PresenceState.Offline) json[name] = Word(state);
        return json;
    }

    /// <summary>What another ProJict said (anything it doesn't say is offline; nonsense is left out).</summary>
    public static Dictionary<string, PresenceState> FromJson(JsonNode? node)
    {
        var people = new Dictionary<string, PresenceState>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject json) return people;
        foreach (var (name, value) in json)
        {
            if (name.Trim().Length == 0 || name.Length > 200) continue;
            var state = value is JsonValue v && v.TryGetValue<string>(out var word) ? word : null;
            if (state == "online") people[name] = PresenceState.Online;
            else if (state == "busy") people[name] = PresenceState.Busy;
            else if (state == "away") people[name] = PresenceState.Away;
        }
        return people;
    }

    /// <summary>A key that changes when anyone's presence does, so it's passed on only then.</summary>
    public static string Signature(IReadOnlyDictionary<string, PresenceState> people) => ToJson(people).ToJsonString();
}
