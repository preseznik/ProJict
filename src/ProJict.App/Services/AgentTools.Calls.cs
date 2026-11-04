using System.Text.Json.Nodes;
using ProJict.Core.Agents;
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

    private string AnswerCall(McpSession session, Args args, List<Attachment> files)
    {
        string told;
        try { told = _main.Calls.Answer(session.AgentName, args.Required("answer")); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        var call = _main.Calls.Current;
        return Json(new JsonObject
        {
            ["call"] = call?.IsOver == false ? "on" : "over",
            ["inIt"] = call == null ? null : new JsonArray([.. call.InIt.Select(n => (JsonNode)n!)]),
            ["note"] = told,
        });
    }
}
