using System.Text.Json.Serialization;
using ProJict.Core.Model;

namespace ProJict.Core.Chat;

/// <summary>Open: anyone who can see its project can read and join. Invite only: just its members.</summary>
public enum ChannelAccess { Open, InviteOnly }

/// <summary>What a channel is about: a project, one of its categories, or an item.</summary>
public sealed class ChannelLink
{
    public Guid ProjectId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? ItemId { get; set; }
}

/// <summary>Someone in a channel: you, an AI agent (by the name it signs with), later people through sharing.</summary>
public sealed class ChatMember
{
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Moderator { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.Now;
    /// <summary>Muted: can read but not post until then (<see cref="DateTime.MaxValue"/>: until unmuted).</summary>
    public DateTime? MutedUntil { get; set; }
    /// <summary>The newest message they've seen, for unread counts and what waits for an agent.</summary>
    public DateTime? LastRead { get; set; }
    /// <summary>A private conversation they put in their archive: nothing new in it counts as unread for them until they take it out.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Archived { get; set; }
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string By { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    /// <summary>The message this one answers; it's quoted above it.</summary>
    public Guid? ReplyTo { get; set; }
    public DateTime? EditedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; set; }
    /// <summary>A line from ProJict about the channel ("Codex joined"); <see cref="By"/> is who did it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Notice { get; set; }
    /// <summary>For a notice about a member (invited, muted, …): who, and the line as said to them ("Codex invited you to #hud").</summary>
    public string? About { get; set; }
    public string? AboutText { get; set; }
    public List<Attachment>? Attachments { get; set; }
    /// <summary>Thumbs up or down, one per person or agent; null while there are none. On an agent's message they're a yes or no to it.</summary>
    public List<ChatReaction>? Reactions { get; set; }
    /// <summary>
    /// The one change this message asks a person to approve (ask_approval, #848d7643), e.g. "commit #9113ea6a"; null for
    /// other messages. The first person to answer it (Approve or Decline, or a 👍 or 👎 on it) decides.
    /// </summary>
    public string? Approval { get; set; }
    /// <summary>Who approved or declined what the message asks, and when; null while nobody has.</summary>
    public ChatDecision? Decision { get; set; }
    /// <summary>
    /// On a person's message: the agent answering it (claim_reply, or its first reply), so the others leave it (#1581da50);
    /// null while none has said it is.
    /// </summary>
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    /// <summary>When the agent answering it replied.</summary>
    public DateTime? AnsweredAt { get; set; }
}

/// <summary>A person's answer to what a message asks them to approve.</summary>
public sealed class ChatDecision
{
    public string By { get; set; } = "";
    public bool Approved { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>A 👍 (yes) or 👎 (no) on a message.</summary>
public sealed class ChatReaction
{
    public string By { get; set; } = "";
    public bool Up { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>A #channel: its members and moderators, who was kicked, and its messages (oldest first).</summary>
public sealed class ChatChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Topic { get; set; }
    public ChannelAccess Access { get; set; }
    /// <summary>A private conversation between two (you and an agent, or two agents): no name of its own, no moderation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Direct { get; set; }
    /// <summary>A private conversation the owner stopped: neither can write in it until it's resumed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Suspended { get; set; }
    /// <summary>A call's own chat (#28be649a): the conversation the call was started from; null for every other channel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CallFrom { get; set; }
    /// <summary>A call's chat: listed under Calls, not with the channels.</summary>
    [JsonIgnore] public bool IsCall => CallFrom != null;
    public ChannelLink? Link { get; set; }
    /// <summary>The channel's small icon (a square picture), shown instead of its # in Chat; null while it has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Attachment? Icon { get; set; }
    public string CreatedBy { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CreatedByAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<ChatMember> Members { get; set; } = [];
    /// <summary>Kicked out: they can't see or rejoin it until a moderator invites them again.</summary>
    public List<string> Kicked { get; set; } = [];
    public List<ChatMessage> Messages { get; set; } = [];
    /// <summary>Up to when agents that aren't members were given their @mentions here (key: name, lowercase).</summary>
    public Dictionary<string, DateTime>? MentionsRead { get; set; }
}

/// <summary>
/// Who is acting in Chat. You, the owner of the workspace, moderate every channel. An agent reaches the projects its
/// API token allows (null: all of them). A guest is a person connected through sharing: it reaches the projects shared
/// with it, and sees channels about nothing in particular only when it's in them. Channels about a project in
/// <paramref name="Hidden"/> (your private projects, for a guest) it never sees, even ones it's in.
/// </summary>
public sealed record ChatActor(string Name, bool Agent, bool Owner = false, IReadOnlySet<Guid>? Projects = null, bool Guest = false,
    IReadOnlySet<Guid>? Hidden = null);

/// <summary>Something Chat doesn't allow, in words for the person or agent who tried.</summary>
public sealed class ChatException(string message) : InvalidOperationException(message);
