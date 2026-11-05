using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Calls across the connection, from the host's side: with people (#2356ddca), a guest's call messages and how to reach a
/// guest to call it; with agents (#546dab2b), which guests' agents hear Chat (so they can be called from here), their
/// answers, and a guest's calls that ring your agents.
/// </summary>
public sealed partial class HostService
{
    /// <summary>The way to a guest connected now, by the name it goes by in your Chat; null while it isn't (or it's you, from another computer).</summary>
    internal PersonLink? LinkTo(string person)
    {
        var session = _sessions.FirstOrDefault(s => !s.Account.Self && string.Equals(NameOf(s.Account), person, StringComparison.OrdinalIgnoreCase));
        return session == null ? null : new PersonLink(NameOf(session.Account), session.Send, OnHost: true);
    }

    /// <summary>callRing, callAnswer, callHangUp or callAudio from a guest.</summary>
    private void HandleCall(GuestSession session, JsonObject message) =>
        _main.Calls.OnPersonMessage(new PersonLink(NameOf(session.Account), session.Send, OnHost: true), message);

    // ───────────── Calls with agents across the connection (#546dab2b) ─────────────

    /// <summary>Calls a guest makes that ring your agents (an agent here → the guest whose call it is, and in which channel).</summary>
    private readonly Dictionary<string, (GuestSession Session, Guid Channel)> _guestCalls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A guest's agent that hears Chat right away there, so it can take a call from here; <paramref name="agent"/> is its
    /// name here ("Ada (via Alice)", or "Ada" from a ProJict linked as you).
    /// </summary>
    internal bool AgentListening(string agent) =>
        _sessions.Any(s => s.ListeningAgents.Contains(s.Account.Self ? agent : AgentNaming.WithoutVia(agent, NameOf(s.Account)) ?? ""));

    /// <summary>A guest's agent answers your call, or a guest writes the lines of its call that rings your agents.</summary>
    private JsonObject? CallOp(GuestSession session, ChatChannel channel, ChatActor actor, string op, JsonObject message, Func<string, JsonObject> refuse)
    {
        if (op == "callAnswer")
        {
            if (!actor.Agent) return refuse("Only an agent answers a call.");
            if (_main.Calls.Channel != channel) return refuse("No call is ringing for you there now.");
            try { _main.Calls.Answer(actor.Name, (string?)message["answer"] ?? ""); }
            catch (InvalidOperationException ex) { return refuse(ex.Message); }
            return null;
        }
        // The guest's call: its lines go in the channel as notices, and the agents it rings here answer back to it.
        if (actor.Agent || !GuestAccess.CanComment(RoleOf(session.Account))) return refuse("Only a person here calls agents.");
        var text = ((string?)message["text"] ?? "").Trim();
        if (!text.StartsWith("📞 ", StringComparison.Ordinal) || text.Length > 300) return refuse("That isn't a line about a call.");
        var about = (string?)message["about"];
        var aboutText = (string?)message["aboutText"];
        if (about != null && (!_main.AgentTools.KnownAgentNames().Contains(about, StringComparer.OrdinalIgnoreCase) || aboutText?.Length > 1000))
            return refuse("That isn't an agent here.");
        _state.Chat.PostNotice(channel, actor, text, about, aboutText);
        if (about != null && text.Contains(" is calling ", StringComparison.Ordinal)) _guestCalls[about] = (session, channel.Id);
        if (text.StartsWith("📞 Call ended", StringComparison.Ordinal))
            foreach (var key in _guestCalls.Where(c => c.Value.Session == session && c.Value.Channel == channel.Id).Select(c => c.Key).ToList()) _guestCalls.Remove(key);
        return null;
    }

    /// <summary>
    /// Your agent answers a guest's call that rang it here: the answer goes to the guest's ProJict. What to tell the agent,
    /// or null when no guest's call rings it.
    /// </summary>
    internal string? ForwardAnswer(string agent, string answer)
    {
        if (!_guestCalls.TryGetValue(agent, out var call) || !_sessions.Contains(call.Session)) return null;
        var word = answer.Trim().ToLowerInvariant();
        if (word is not ("yes" or "pick_up" or "pickup" or "no" or "hang_up" or "hangup" or "leave")) throw new InvalidOperationException("answer is yes (pick up), no, or hang_up.");
        call.Session.Send(Wire.Message("agentCallAnswer", ("channelId", call.Channel.ToString()), ("agent", agent), ("answer", word)));
        var who = NameOf(call.Session.Account);
        var channel = _state.Chat.Channels.FirstOrDefault(c => c.Id == call.Channel);
        // A private conversation is "@" its person, as the agent writes in it (#ae8d5cd1); a channel its "#name".
        var where = channel == null ? "the channel" : channel.Direct ? "@" + who : "#" + channel.Name;
        if (word is "no" or "hang_up" or "hangup" or "leave") _guestCalls.Remove(agent);
        return word switch
        {
            "no" => $"You said no to {who}'s call.",
            "yes" or "pick_up" or "pickup" => $"You're in the call with {who}, in {where}. What {who} says comes to you as messages there; answer with post_message " +
                                              $"channel={where}, a sentence or two at a time in plain spoken words: it's read aloud. answer_call answer=hang_up leaves the call.",
            _ => "You left the call.",
        };
    }
}
