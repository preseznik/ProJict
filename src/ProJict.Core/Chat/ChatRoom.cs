using System.Globalization;
using System.Text;
using ProJict.Core.Model;

namespace ProJict.Core.Chat;

/// <summary>
/// The channels and what may happen in them. Whoever starts a channel moderates it; you, the owner of the workspace,
/// moderate every channel. Moderators invite, remove (kick), mute and unmute members, make others moderators and
/// delete messages; everyone edits and deletes their own. Each change raises <see cref="Changed"/> (for saving and the
/// views) and each message <see cref="Posted"/>; what's not allowed throws a <see cref="ChatException"/> that says why.
/// </summary>
public sealed class ChatRoom
{
    public const int MaxText = 4000;
    public const int MaxNameLength = 40;
    /// <summary>An agent can post this many messages a minute, so two agents can't flood a channel talking to each other.</summary>
    public const int AgentPostsPerMinute = 20;

    private readonly Func<string> _ownerName;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, Queue<DateTime>> _agentPosts = new(StringComparer.OrdinalIgnoreCase);

    public ChatRoom(List<ChatChannel> channels, Func<string> ownerName, Func<DateTime>? clock = null)
    {
        Channels = channels;
        _ownerName = ownerName;
        _clock = clock ?? (() => DateTime.Now);
    }

    public List<ChatChannel> Channels { get; }
    public string OwnerName => _ownerName();

    /// <summary>A channel changed (members, settings or messages): save it and update what shows it.</summary>
    public event Action<ChatChannel>? Changed;
    /// <summary>A new message (not a notice) was posted.</summary>
    public event Action<ChatChannel, ChatMessage>? Posted;
    public event Action<ChatChannel>? Removed;

    private DateTime Now => _clock();

    // ───────────── Who may do what ─────────────

    public static ChatMember? Member(ChatChannel channel, string name) =>
        channel.Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsKicked(ChatChannel channel, string name) => channel.Kicked.Contains(name, StringComparer.OrdinalIgnoreCase);

    public bool IsOwner(string name) => string.Equals(name, OwnerName, StringComparison.OrdinalIgnoreCase);

    public static bool IsModerator(ChatChannel channel, ChatActor actor) => actor.Owner || Member(channel, actor.Name)?.Moderator == true;

    /// <summary>Whether the channel's project is within the actor's reach (an API token may be limited to some projects).</summary>
    public static bool Reaches(ChatChannel channel, ChatActor actor) =>
        channel.Link == null || actor.Projects == null || actor.Projects.Contains(channel.Link.ProjectId);

    /// <summary>You see every channel; others see the ones they're in, and Open ones in projects they reach unless they were kicked.</summary>
    public static bool CanSee(ChatChannel channel, ChatActor actor) =>
        actor.Owner || (!IsKicked(channel, actor.Name) && Reaches(channel, actor) &&
                        (channel.Access == ChannelAccess.Open || Member(channel, actor.Name) != null));

    /// <summary>Until when the member can't post; null when they can.</summary>
    public DateTime? MutedUntil(ChatChannel channel, string name) =>
        Member(channel, name)?.MutedUntil is { } until && until > Now ? until : null;

    public IEnumerable<ChatChannel> Visible(ChatActor actor) => Channels.Where(c => CanSee(c, actor));

    public ChatChannel? Find(string nameOrId)
    {
        var name = nameOrId.Trim().TrimStart('#');
        return Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? (name.Length >= 8 ? Channels.FirstOrDefault(c => c.Id.ToString().StartsWith(name, StringComparison.OrdinalIgnoreCase)) : null);
    }

    // ───────────── Names ─────────────

    /// <summary>"GRID2VR › HUD stuff" → "grid2vr-hud-stuff": lowercase letters, digits and dashes.</summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxNameLength) slug = slug[..MaxNameLength].TrimEnd('-');
        return slug;
    }

    /// <summary>A free name for a channel about a project, category or item: "grid2vr", "grid2vr-hud", "grid2vr-crash-when-pausing".</summary>
    public string SuggestName(string project, string? category = null, string? item = null)
    {
        var words = item != null ? string.Join(' ', item.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4)) : category;
        var name = Slug(words != null ? $"{project} {words}" : project);
        if (name.Length == 0) name = "channel";
        var candidate = name;
        for (var i = 2; Find(candidate) != null; i++) candidate = $"{name[..Math.Min(name.Length, MaxNameLength - 3)]}-{i}";
        return candidate;
    }

    /// <summary>Why the name can't be used, or null when it can.</summary>
    public string? NameProblem(string name, ChatChannel? except = null)
    {
        name = name.Trim().TrimStart('#');
        if (name.Length == 0) return "Give the channel a name.";
        if (name.Length > MaxNameLength) return $"Channel names can be up to {MaxNameLength} characters.";
        if (Slug(name) != name) return "Channel names use lowercase letters, digits and dashes, e.g. grid2vr-hud.";
        if (Channels.Any(c => c != except && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) return $"There is already a #{name}.";
        return null;
    }

    // ───────────── Channels ─────────────

    public ChatChannel Create(ChatActor by, string name, ChannelAccess access, ChannelLink? link = null, string? topic = null)
    {
        name = name.Trim().TrimStart('#');
        if (NameProblem(name) is { } problem) throw new ChatException(problem);
        if (link != null && by.Projects != null && !by.Projects.Contains(link.ProjectId))
            throw new ChatException("That project is outside what you can reach.");
        var now = Now;
        var channel = new ChatChannel
        {
            Name = name, Access = access, Link = link, Topic = Clean(topic), CreatedBy = by.Name, CreatedByAgent = by.Agent, CreatedAt = now,
            Members = [new ChatMember { Name = by.Name, Agent = by.Agent, Moderator = true, JoinedAt = now, LastRead = now }],
        };
        // You're in every channel from the start; leave the ones you don't want.
        if (!by.Owner) channel.Members.Add(new ChatMember { Name = OwnerName, JoinedAt = now });
        AddNotice(channel, by, $"{by.Name} started #{name}");
        Channels.Add(channel);
        Changed?.Invoke(channel);
        return channel;
    }

    public void SetTopic(ChatChannel channel, ChatActor by, string? topic)
    {
        RequireModerator(channel, by);
        channel.Topic = Clean(topic);
        AddNotice(channel, by, channel.Topic == null ? $"{by.Name} cleared the topic" : $"{by.Name} set the topic: {channel.Topic}");
        Changed?.Invoke(channel);
    }

    public void SetAccess(ChatChannel channel, ChatActor by, ChannelAccess access)
    {
        RequireModerator(channel, by);
        if (channel.Access == access) return;
        channel.Access = access;
        AddNotice(channel, by, access == ChannelAccess.Open ? $"{by.Name} opened the channel to everyone" : $"{by.Name} made the channel invite only");
        Changed?.Invoke(channel);
    }

    /// <summary>Only you delete channels; they go to backups/deleted.</summary>
    public void Delete(ChatChannel channel, ChatActor by)
    {
        if (!by.Owner) throw new ChatException("Only the owner of the workspace can delete a channel.");
        Channels.Remove(channel);
        Removed?.Invoke(channel);
    }

    // ───────────── Members ─────────────

    public void Join(ChatChannel channel, ChatActor actor)
    {
        if (Member(channel, actor.Name) != null) return;
        if (!CanSee(channel, actor) || (channel.Access == ChannelAccess.InviteOnly && !actor.Owner))
            throw new ChatException(IsKicked(channel, actor.Name) ? $"You were removed from #{channel.Name}; a moderator can invite you back."
                : $"#{channel.Name} is invite only; ask a moderator to invite you.");
        channel.Members.Add(new ChatMember { Name = actor.Name, Agent = actor.Agent, JoinedAt = Now, LastRead = channel.Messages.LastOrDefault()?.At });
        AddNotice(channel, actor, $"{actor.Name} joined");
        Changed?.Invoke(channel);
    }

    public void Leave(ChatChannel channel, ChatActor actor)
    {
        if (Member(channel, actor.Name) is not { } member) return;
        channel.Members.Remove(member);
        AddNotice(channel, actor, $"{actor.Name} left");
        Changed?.Invoke(channel);
    }

    /// <summary>Moderators invite; in an Open channel any member may. An invitation also lifts a kick.</summary>
    public void Invite(ChatChannel channel, ChatActor by, string name, bool agent)
    {
        name = name.Trim().TrimStart('@');
        if (name.Length == 0) throw new ChatException("Say whom to invite.");
        if (!IsModerator(channel, by) && !(channel.Access == ChannelAccess.Open && Member(channel, by.Name) != null))
            throw new ChatException($"Only moderators of #{channel.Name} can invite.");
        channel.Kicked.RemoveAll(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (Member(channel, name) != null) return;
        channel.Members.Add(new ChatMember { Name = name, Agent = agent, JoinedAt = Now, LastRead = channel.Messages.LastOrDefault()?.At });
        AddNotice(channel, by, $"{by.Name} invited {name}");
        Changed?.Invoke(channel);
    }

    /// <summary>Removes a member, who can't rejoin until invited again.</summary>
    public void Kick(ChatChannel channel, ChatActor by, string name)
    {
        var member = Target(channel, by, name, "remove");
        channel.Members.Remove(member);
        if (!IsKicked(channel, member.Name)) channel.Kicked.Add(member.Name);
        AddNotice(channel, by, $"{by.Name} removed {member.Name} from the channel");
        Changed?.Invoke(channel);
    }

    /// <summary>Mutes a member for a while, or until unmuted (null).</summary>
    public void Mute(ChatChannel channel, ChatActor by, string name, TimeSpan? duration)
    {
        var member = Target(channel, by, name, "mute");
        member.MutedUntil = duration is { } d ? Now + d : DateTime.MaxValue;
        AddNotice(channel, by, duration is { } span ? $"{by.Name} muted {member.Name} for {Span(span)}" : $"{by.Name} muted {member.Name} until unmuted");
        Changed?.Invoke(channel);
    }

    public void Unmute(ChatChannel channel, ChatActor by, string name)
    {
        RequireModerator(channel, by);
        if (Member(channel, name) is not { MutedUntil: not null } member) return;
        member.MutedUntil = null;
        AddNotice(channel, by, $"{by.Name} unmuted {member.Name}");
        Changed?.Invoke(channel);
    }

    public void SetModerator(ChatChannel channel, ChatActor by, string name, bool moderator)
    {
        RequireModerator(channel, by);
        var member = Member(channel, name) ?? throw new ChatException($"{name} isn't in #{channel.Name}.");
        if (member.Moderator == moderator) return;
        if (!moderator && !by.Owner && !string.Equals(member.Name, by.Name, StringComparison.OrdinalIgnoreCase))
            throw new ChatException("Only the owner of the workspace can take a moderator's rights away.");
        member.Moderator = moderator;
        if (moderator) member.MutedUntil = null;
        AddNotice(channel, by, moderator ? $"{by.Name} made {member.Name} a moderator" : $"{member.Name} is no longer a moderator");
        Changed?.Invoke(channel);
    }

    private ChatMember Target(ChatChannel channel, ChatActor by, string name, string what)
    {
        RequireModerator(channel, by);
        var member = Member(channel, name.Trim().TrimStart('@')) ?? throw new ChatException($"{name} isn't in #{channel.Name}.");
        if (IsOwner(member.Name) && !member.Agent) throw new ChatException($"You can't {what} the owner of the workspace.");
        if (string.Equals(member.Name, by.Name, StringComparison.OrdinalIgnoreCase)) throw new ChatException($"You can't {what} yourself.");
        if (member.Moderator && !by.Owner) throw new ChatException($"Only the owner of the workspace can {what} a moderator.");
        return member;
    }

    private static void RequireModerator(ChatChannel channel, ChatActor by)
    {
        if (!IsModerator(channel, by)) throw new ChatException($"Only moderators of #{channel.Name} can do that.");
    }

    // ───────────── Messages ─────────────

    /// <summary>Posts a message; posting in an Open channel joins it.</summary>
    public ChatMessage Post(ChatChannel channel, ChatActor actor, string text, Guid? replyTo = null, List<Attachment>? files = null)
    {
        text = text.Trim();
        if (text.Length == 0 && files is not { Count: > 0 }) throw new ChatException("The message is empty.");
        if (text.Length > MaxText) throw new ChatException($"Messages can be up to {MaxText} characters.");
        if (!CanSee(channel, actor)) throw new ChatException($"You can't post in #{channel.Name}.");
        if (MutedUntil(channel, actor.Name) is { } until && !actor.Owner)
            throw new ChatException(until == DateTime.MaxValue ? $"You're muted in #{channel.Name} until a moderator unmutes you."
                : $"You're muted in #{channel.Name} until {until.ToString("HH:mm", CultureInfo.CurrentCulture)}.");
        if (replyTo is { } r && channel.Messages.All(m => m.Id != r)) throw new ChatException("The message to reply to isn't there any more.");
        if (actor.Agent) Throttle(actor.Name);
        if (Member(channel, actor.Name) == null) Join(channel, actor);
        var message = new ChatMessage { By = actor.Name, Agent = actor.Agent, At = NextTime(channel), Text = text, ReplyTo = replyTo, Attachments = files is { Count: > 0 } ? files : null };
        channel.Messages.Add(message);
        Member(channel, actor.Name)!.LastRead = message.At;
        Changed?.Invoke(channel);
        Posted?.Invoke(channel, message);
        return message;
    }

    public void Edit(ChatChannel channel, ChatActor actor, Guid id, string text)
    {
        var message = Message(channel, id);
        if (!string.Equals(message.By, actor.Name, StringComparison.OrdinalIgnoreCase) || message.Notice) throw new ChatException("You can only edit your own messages.");
        if (message.Deleted) throw new ChatException("That message was deleted.");
        text = text.Trim();
        if (text.Length == 0 && message.Attachments is not { Count: > 0 }) throw new ChatException("The message is empty; delete it instead.");
        if (text.Length > MaxText) throw new ChatException($"Messages can be up to {MaxText} characters.");
        if (text == message.Text) return;
        message.Text = text;
        message.EditedAt = Now;
        Changed?.Invoke(channel);
    }

    /// <summary>Your own messages, or any as a moderator; the text and files go, a "deleted" line stays.</summary>
    public void DeleteMessage(ChatChannel channel, ChatActor actor, Guid id)
    {
        var message = Message(channel, id);
        if (message.Notice) throw new ChatException("Lines from ProJict can't be deleted.");
        if (!string.Equals(message.By, actor.Name, StringComparison.OrdinalIgnoreCase) && !IsModerator(channel, actor))
            throw new ChatException($"Only moderators of #{channel.Name} can delete other people's messages.");
        if (message.Deleted) return;
        message.Deleted = true;
        message.Text = "";
        message.Attachments = null;
        message.EditedAt = Now;
        Changed?.Invoke(channel);
    }

    public static ChatMessage Message(ChatChannel channel, Guid id) =>
        channel.Messages.FirstOrDefault(m => m.Id == id) ?? throw new ChatException("That message isn't there any more.");

    public static ChatMessage? FindMessage(ChatChannel channel, string id) =>
        Guid.TryParse(id, out var guid) ? channel.Messages.FirstOrDefault(m => m.Id == guid)
        : id.Length >= 6 ? channel.Messages.FirstOrDefault(m => m.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase)) : null;

    // ───────────── Read and unread ─────────────

    /// <summary>Messages the member hasn't seen: newer than what they read, by others, not deleted, not notices.</summary>
    public static IEnumerable<ChatMessage> Unread(ChatChannel channel, string name)
    {
        if (Member(channel, name) is not { } member) return [];
        var since = member.LastRead ?? DateTime.MinValue;
        return channel.Messages.Where(m => m.At > since && !m.Notice && !m.Deleted && !string.Equals(m.By, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Unread messages that @mention the member.</summary>
    public static IEnumerable<ChatMessage> UnreadMentions(ChatChannel channel, string name) =>
        Unread(channel, name).Where(m => ChatText.Mentions(m.Text, name));

    /// <summary>Marks everything up to the newest message (or <paramref name="upTo"/>) as seen; returns whether anything changed.</summary>
    public bool MarkRead(ChatChannel channel, string name, DateTime? upTo = null)
    {
        if (Member(channel, name) is not { } member || channel.Messages.Count == 0) return false;
        var last = upTo ?? channel.Messages[^1].At;
        if (member.LastRead >= last) return false;
        member.LastRead = last;
        Changed?.Invoke(channel);
        return true;
    }

    // ───────────── Helpers ─────────────

    private void AddNotice(ChatChannel channel, ChatActor by, string text) =>
        channel.Messages.Add(new ChatMessage { By = by.Name, Agent = by.Agent, At = NextTime(channel), Text = text, Notice = true });

    /// <summary>Now, but always after the channel's newest message, so "read up to" never hides a message posted in the same tick.</summary>
    private DateTime NextTime(ChatChannel channel)
    {
        var now = Now;
        return channel.Messages.Count > 0 && channel.Messages[^1].At >= now ? channel.Messages[^1].At.AddTicks(1) : now;
    }

    private void Throttle(string agent)
    {
        var now = Now;
        if (!_agentPosts.TryGetValue(agent, out var posts)) _agentPosts[agent] = posts = new Queue<DateTime>();
        while (posts.Count > 0 && now - posts.Peek() >= TimeSpan.FromMinutes(1)) posts.Dequeue();
        if (posts.Count >= AgentPostsPerMinute)
            throw new ChatException($"You've posted {AgentPostsPerMinute} messages in the past minute, the most an agent may; wait a little.");
        posts.Enqueue(now);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Replace("\r\n", " ").Replace('\n', ' ');

    public static string Span(TimeSpan span) => span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} minutes"
        : span.TotalHours < 24 ? (span.TotalHours == 1 ? "1 hour" : $"{span.TotalHours:0.#} hours")
        : span.TotalDays == 1 ? "1 day" : $"{span.TotalDays:0.#} days";
}
