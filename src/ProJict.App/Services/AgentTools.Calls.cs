using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Voice calls (#80a4e9c9): the user calls an agent that hears Chat right away; a line from ProJict says it's ringing,
/// and answer_call picks up, says no or hangs up. In the call the user's words come as messages, and the agent's
/// answers in that conversation are read aloud.
/// </summary>
public sealed partial class AgentTools
{
    private void AddCallTools()
    {
        Add("answer_call", "Answer a voice call",
            "When the user calls you (a line from ProJict says it's ringing): answer=yes picks up, answer=no says no, answer=hang_up leaves the call. " +
            "In the call, what they say comes to you as messages in that conversation; answer there with post_message, a sentence or two at a time in " +
            "plain spoken words (no lists, code or links): ProJict reads it aloud. Keep listening (your chat watcher, or wait_for_messages) while it lasts.",
            Schema(("answer", "string", "yes, no or hang_up.", true)), AnswerCall);
    }

    /// <summary>
    /// A call in a host's channel that rings the agent there (its newest call line about it, not ended since): the answer
    /// goes to the host. What to tell the agent, or null when no host's call rings it.
    /// </summary>
    private string? AnswerHostCall(McpSession session, string answer)
    {
        var word = answer.Trim().ToLowerInvariant();
        foreach (var host in ReachableHosts(session, ChatHosts))
        {
            var me = ViaIn(session, host);
            foreach (var channel in host.Chat!.Channels.Where(c => !c.Direct && ChatRoom.Member(c, me) != null))
            {
                var ring = channel.Messages.LastOrDefault(m => m.Notice && string.Equals(m.About, me, StringComparison.OrdinalIgnoreCase) && m.Text.Contains(" is calling ", StringComparison.Ordinal));
                if (ring == null || channel.Messages.Any(m => m.Notice && m.At > ring.At && m.Text.StartsWith("📞 Call ended", StringComparison.Ordinal))) continue;
                if (word is not ("yes" or "pick_up" or "pickup" or "no" or "hang_up" or "hangup" or "leave")) throw new McpToolException("answer is yes (pick up), no, or hang_up.");
                _main.Guest.ChatOp(host, "callAnswer", ("channel", channel.Id.ToString()), ("answer", word), ("as", session.AgentName));
                var where = SharedName(host, channel);
                return word switch
                {
                    "no" => $"You said no to {host.HostName}'s call.",
                    "yes" or "pick_up" or "pickup" => $"You're in {host.HostName}'s call, in \"{where}\". What they say comes to you as messages there; answer with " +
                                                      $"post_message channel=\"{where}\", a sentence or two at a time in plain spoken words: it's read aloud. " +
                                                      "answer_call answer=hang_up leaves the call.",
                    _ => "You left the call.",
                };
            }
        }
        return null;
    }

    private string AnswerCall(McpSession session, Args args, List<Attachment> files)
    {
        string told;
        var answer = args.Required("answer");
        try { told = _main.Calls.Answer(session.AgentName, answer); }
        catch (InvalidOperationException ex)
        {
            // Not a call of this ProJict's: one in a host's channel that rings you there, or a guest's call that rings you here (#546dab2b).
            try { told = AnswerHostCall(session, answer) ?? _main.Host.ForwardAnswer(session.AgentName, answer) ?? throw new McpToolException(ex.Message); }
            catch (InvalidOperationException again) { throw new McpToolException(again.Message); }
            return Json(new JsonObject { ["note"] = told });
        }
        var call = _main.Calls.Current;
        return Json(new JsonObject
        {
            ["call"] = call?.IsOver == false ? "on" : "over",
            ["inIt"] = call == null ? null : new JsonArray([.. call.InIt.Select(n => (JsonNode)n!)]),
            ["note"] = told,
        });
    }
}
