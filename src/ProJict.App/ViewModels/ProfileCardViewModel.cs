using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Chat;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>Something on a profile card that opens with a click: a channel, an item, a project's board.</summary>
public sealed partial class ProfileLink(string text, string? toolTip, Action open)
{
    public string Text => text;
    public string? ToolTip => toolTip;
    [RelayCommand] private void Open() => open();
}

/// <summary>A line of numbers on a profile card: "Messages  214 · last 3 hours ago".</summary>
public sealed record ProfileStat(string Label, string Value, string? ToolTip = null);

/// <summary>What a profile card can do: open what's on it, and write to them.</summary>
public sealed record ProfileActions(Action<ChatChannel> OpenChannel, Action<TodoItem> ShowItem, Action<Project> ShowWork, Action? PrivateMessage);

/// <summary>
/// A person's or agent's profile card (#490e5810), opened by clicking their name or picture in Chat (or an @mention):
/// their picture, name, what they are (Claude Code, a guest, the owner) and whether they're online; the channels they're
/// in; how much they've written and when last; and their work on items as the Time tab counts it, with what they're on now.
/// </summary>
public sealed partial class ProfileCardViewModel : ObservableObject
{
    private readonly ProfileActions _actions;

    public ProfileCardViewModel(ChatProfile profile, ImageSource? picture, string kind, PresenceState presence, ProfileActions actions, DateTime now)
    {
        Profile = profile;
        Picture = picture;
        KindText = kind;
        Presence = presence;
        _actions = actions;
        var culture = CultureInfo.CurrentCulture;

        Channels = profile.Channels.Select(c => new ProfileLink("#" + c.Name, c.Topic is { Length: > 0 } topic ? topic : "Open the channel", () => Do(() => actions.OpenChannel(c)))).ToList();
        Working = profile.Working.Select(i => new ProfileLink(Short(i.Text), "Show it on its board", () => Do(() => actions.ShowItem(i)))).ToList();
        Projects = profile.Projects.Select(p => new ProfileLink(p.Time > TimeSpan.Zero ? $"{p.Project.Name} · {WorkLog.Format(p.Time)}" : p.Project.Name,
            $"Show {profile.Name}'s work on its board", () => Do(() => actions.ShowWork(p.Project)))).ToList();

        var stats = new List<ProfileStat>
        {
            new("Messages", profile.Messages == 0 ? "None yet"
                : profile.Messages.ToString("N0", culture) + (profile.LastMessage is { } last ? " · last " + ChatProfile.Ago(last, now, culture) : ""),
                profile.LastMessage?.ToString("dddd d MMMM yyyy, HH:mm", culture)),
        };
        if (profile.Agent || profile.ItemsDone > 0 || profile.Time > TimeSpan.Zero)
            stats.Add(new("Items done", profile.ItemsDone.ToString("N0", culture), "Items they worked on that are done, as the Time tab counts them"));
        if (profile.Time > TimeSpan.Zero) stats.Add(new("Time on items", WorkLog.Format(profile.Time)));
        if (profile.Tokens > 0 || profile.Cost > 0)
            stats.Add(new("Used", string.Join(" · ", new[]
            {
                profile.Tokens > 0 ? WorkLog.FormatTokens(profile.Tokens) + " tokens" : null,
                profile.Cost > 0 ? WorkLog.FormatCost(profile.Cost) : null,
            }.Where(s => s != null)), "What they reported using on items"));
        if (profile.Since is { } since)
            stats.Add(new("Here since", since.ToString(since.Year == now.Year ? "d MMMM" : "d MMMM yyyy", culture), "Their first message, channel or work here"));
        Stats = stats;
    }

    public ChatProfile Profile { get; }
    public string Name => Profile.Name;
    public bool IsAgent => Profile.Agent;
    public WorkerBadge Badge => WorkerBadge.For([Profile.Name])[0];
    public ImageSource? Picture { get; }
    public bool HasPicture => Picture != null;
    /// <summary>"Claude Code", "AI agent", "Owner of this ProJict", "Guest", "You".</summary>
    public string KindText { get; }

    public PresenceState Presence { get; }
    public bool IsBusy => Presence == PresenceState.Busy;
    public bool IsAway => Presence == PresenceState.Away;
    public bool IsOffline => Presence == PresenceState.Offline;
    public string PresenceText => Presence switch
    {
        PresenceState.Online => "Online",
        PresenceState.Busy => "Busy",
        PresenceState.Away => "Away",
        _ => "Offline",
    };

    public List<ProfileLink> Channels { get; }
    public bool HasChannels => Channels.Count > 0;
    public List<ProfileStat> Stats { get; }
    public List<ProfileLink> Working { get; }
    public bool HasWorking => Working.Count > 0;
    public List<ProfileLink> Projects { get; }
    public bool HasProjects => Projects.Count > 0;

    public bool CanMessage => _actions.PrivateMessage != null;
    public string PrivateMessageText => IsAgent ? "Message it privately" : "Message privately";

    /// <summary>Raised when something on the card was opened: the card closes.</summary>
    public event Action? Done;

    [RelayCommand] private void PrivateMessage() => Do(() => _actions.PrivateMessage?.Invoke());
    [RelayCommand] private void Close() => Done?.Invoke();

    private void Do(Action action)
    {
        Done?.Invoke();
        action();
    }

    private static string Short(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 70 ? text : text[..67].TrimEnd() + "…";
    }
}
