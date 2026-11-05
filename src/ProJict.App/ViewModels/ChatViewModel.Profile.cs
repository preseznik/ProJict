using ProJict.App.Services;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>Profile cards (#490e5810): clicking someone's name, picture or @mention in Chat shows theirs.</summary>
public sealed partial class ChatViewModel
{
    /// <summary>The profile card showing; null while none.</summary>
    public ProfileCardViewModel? Profile { get; private set; }

    /// <summary>A card shows (the popup is open); setting it false closes it.</summary>
    public bool HasProfile
    {
        get => Profile != null;
        set
        {
            if (value || Profile == null) return;
            Profile = null;
            OnPropertyChanged(nameof(Profile));
            OnPropertyChanged(nameof(HasProfile));
        }
    }

    /// <summary>
    /// Shows someone's card, as they are where the channel lives: here, with your projects' work; in another ProJict's
    /// channel, with what you see of its Chat.
    /// </summary>
    internal void ShowProfile(ChatChannel? channel, string name, bool agent)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var place = PlaceOf(channel);
        var me = place.Me.Name;
        // The channels you can read there: not others' private conversations.
        var readable = place.Room.Channels.Where(c => !c.Direct || ChatRoom.Member(c, me) != null);
        IEnumerable<Project> projects = place.Host == null ? _state.Projects : [];
        var now = DateTime.Now;
        var profile = ChatProfile.Of(name, agent, readable, projects, now);
        var isMe = !agent && string.Equals(name, me, StringComparison.OrdinalIgnoreCase);
        var owner = !agent && place.Room.IsOwner(name);
        var kind = owner ? (isMe ? "You · owner of this ProJict" : "Owner of this ProJict")
            : agent ? ClientOf(name)
            : isMe ? "You"
            : place.Host == null && IsGuestName(name) ? "Guest" : "Person";
        var presence = PresenceOf(place, new ChatMember { Name = name, Agent = agent });
        var actions = new ProfileActions(
            OpenChannel: OpenChannel,
            ShowItem: item =>
            {
                foreach (var project in _state.Projects)
                    if (project.Find(item.Id) is { } loc)
                    {
                        _main.OpenProject(project).Reveal(loc.Item);
                        return;
                    }
            },
            ShowWork: project => _main.OpenProject(project).ShowWorkedByCommand.Execute(profile.Name),
            PrivateMessage: isMe || !MayWrite(channel) ? null : () => OpenDirectIn(channel, name, agent));
        var card = new ProfileCardViewModel(profile, PictureOf(name), kind, presence, actions, now);
        card.Done += () => HasProfile = false;
        Profile = card;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(HasProfile));
    }

    /// <summary>An @mention's card: a member's as they are in the channel, else a person's if the name is one.</summary>
    private void ShowMentioned(string name)
    {
        if (string.Equals(name, "everyone", StringComparison.OrdinalIgnoreCase)) return;
        var place = Here;
        if (Channel is { } channel && channel.Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is { } member)
        {
            ShowProfile(channel, member.Name, member.Agent);
            return;
        }
        var person = place.Room.IsOwner(name) || string.Equals(name, place.Me.Name, StringComparison.OrdinalIgnoreCase) || IsGuestName(name);
        ShowProfile(Channel, name, !person);
    }
}
