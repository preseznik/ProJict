using System.Text.Json.Nodes;

namespace ProJict.Core.Chat;

/// <summary>Whether someone is here: connected and doing things, connected but idle a while, or not connected.</summary>
public enum PresenceState { Offline, Away, Online }

/// <summary>
/// Who's online, away or offline. People are away when nothing was typed or clicked on their computer for a while, agents
/// when they haven't used a tool for a while; offline when their ProJict (or the agent) isn't connected. ProJicts that
/// share pass on who's here by name, the offline left out.
/// </summary>
public static class Presence
{
    /// <summary>Idle this long, and they're away.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(10);

    public static PresenceState Of(bool connected, TimeSpan idle) =>
        !connected ? PresenceState.Offline : idle >= AwayAfter ? PresenceState.Away : PresenceState.Online;

    /// <summary>The more present of the two (someone connected twice, say, idle on one computer and busy on the other).</summary>
    public static PresenceState Best(PresenceState a, PresenceState b) => a > b ? a : b;

    /// <summary>"online", "away", "offline".</summary>
    public static string Word(PresenceState state) => state.ToString().ToLowerInvariant();

    /// <summary>Who's online or away, by name: what one ProJict tells another.</summary>
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
            else if (state == "away") people[name] = PresenceState.Away;
        }
        return people;
    }

    /// <summary>A key that changes when anyone's presence does, so it's passed on only then.</summary>
    public static string Signature(IReadOnlyDictionary<string, PresenceState> people) => ToJson(people).ToJsonString();
}
