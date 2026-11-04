using System.Text.RegularExpressions;

namespace ProJict.Core.Chat;

/// <summary>Whose voices read agents' answers in a call: Windows' own (offline), or ElevenLabs' (with its key in Settings › Tools).</summary>
public enum CallVoices { Windows, ElevenLabs }

/// <summary>Where an agent is in a call: ringing, in it, said no, didn't answer, left, or couldn't be rung (it doesn't hear Chat now).</summary>
public enum CallState { Ringing, Joined, Declined, Missed, Left, CantNow }

/// <summary>An agent in a call, and since when it's in that state.</summary>
public sealed class CallParticipant(string name, CallState state, DateTime since)
{
    public string Name { get; } = name;
    public CallState State { get; set; } = state;
    public DateTime Since { get; set; } = since;
}

/// <summary>
/// A voice call with agents, in a private conversation or a channel (#80a4e9c9): you call the agents you pick; each one
/// that hears Chat right away is rung and picks up or says no (or doesn't answer in <see cref="RingFor"/>). In the call
/// what you say comes to them as messages, and what they answer is read aloud. It's over when you hang up, or when
/// nobody is left ringing or in it. Kept in memory while it lasts; what was said stays in the conversation.
/// </summary>
public sealed class ChatCall
{
    public static readonly TimeSpan RingFor = TimeSpan.FromSeconds(45);

    public ChatCall(Guid channelId, string caller, IEnumerable<(string Name, bool CanTakeCall)> agents, DateTime now)
    {
        ChannelId = channelId;
        Caller = caller;
        StartedAt = now;
        Agents = [.. agents.Select(a => new CallParticipant(a.Name, a.CanTakeCall ? CallState.Ringing : CallState.CantNow, now))];
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Guid ChannelId { get; }
    public string Caller { get; }
    public DateTime StartedAt { get; }
    public DateTime? EndedAt { get; private set; }
    public List<CallParticipant> Agents { get; }

    public bool IsOver => EndedAt != null;
    /// <summary>Someone's still ringing or in it.</summary>
    public bool HasSomeone => Agents.Any(a => a.State is CallState.Ringing or CallState.Joined);
    public IEnumerable<string> InIt => Agents.Where(a => a.State == CallState.Joined).Select(a => a.Name);

    public CallParticipant? Of(string agent) => Agents.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase));

    /// <summary>The agent picks up (yes) or says no; the problem when it can't now, or null.</summary>
    public string? Answer(string agent, bool yes, DateTime now)
    {
        if (IsOver) return "The call is over.";
        if (Of(agent) is not { } you) return "You aren't in this call.";
        if (you.State == CallState.Joined) return yes ? null : "You're in the call: answer_call answer=hang_up to leave it.";
        if (you.State != CallState.Ringing) return "It isn't ringing for you any more.";
        you.State = yes ? CallState.Joined : CallState.Declined;
        you.Since = now;
        return null;
    }

    /// <summary>The agent hangs up; the problem when it isn't in the call, or null.</summary>
    public string? Leave(string agent, DateTime now)
    {
        if (IsOver) return "The call is over.";
        if (Of(agent) is not { State: CallState.Joined or CallState.Ringing } you) return "You aren't in this call.";
        you.State = CallState.Left;
        you.Since = now;
        return null;
    }

    /// <summary>The agents still ringing after <see cref="RingFor"/>: they missed it.</summary>
    public IReadOnlyList<CallParticipant> TimeOut(DateTime now)
    {
        var missed = Agents.Where(a => a.State == CallState.Ringing && now - a.Since >= RingFor).ToList();
        foreach (var agent in missed)
        {
            agent.State = CallState.Missed;
            agent.Since = now;
        }
        return missed;
    }

    public void End(DateTime now) => EndedAt ??= now;

    /// <summary>"3 min 05 s", "42 s".</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds:00} s" : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    /// <summary>"ringing", "in the call", "said no", "didn't answer", "left", "can't take calls now".</summary>
    public static string Describe(CallState state) => state switch
    {
        CallState.Ringing => "ringing",
        CallState.Joined => "in the call",
        CallState.Declined => "said no",
        CallState.Missed => "didn't answer",
        CallState.Left => "left",
        _ => "can't take calls now",
    };

    /// <summary>
    /// A message as it's read aloud: Markdown marks, code and links left out, item and channel links said plainly, one
    /// line; empty when nothing's left to say.
    /// </summary>
    public static string ForVoice(string text)
    {
        var s = Regex.Replace(text, "```.*?```", " (some code) ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"https?://\S+", "a link");
        s = Regex.Replace(s, @"`([^`]*)`", "$1");
        s = Regex.Replace(s, @"(?<!\w)#([0-9a-f]{8})\b", "item $1");
        s = Regex.Replace(s, @"[*_~#>]+", "");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }
}
