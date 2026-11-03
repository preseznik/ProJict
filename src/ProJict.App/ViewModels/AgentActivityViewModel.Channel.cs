using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;
using ProJict.Core.Features;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project's channel under the office (a toggle in its header): the last messages and a box to write in, with All
/// agents beside it at the bottom right. The channel is the studio's, else the project's own (an Open one first, the
/// oldest); with all projects, the one you last had open in Chat.
/// </summary>
public sealed partial class AgentActivityViewModel
{
    /// <summary>How many of its last messages show.</summary>
    private const int ChannelLinesShown = 12;

    public bool ShowChannel
    {
        get => View.ShowChannel;
        set
        {
            if (View.ShowChannel == value) return;
            View.ShowChannel = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
            RefreshChannel();
        }
    }

    public string ShowChannelToolTip => ShowChannel ? "Hide the channel under the office"
        : FilterProject is { } project ? $"Show {project.Name}'s channel under the office, to follow and write in it here"
        : "Show a channel under the office (the one you last had open in Chat; pick a project for its own)";

    private ChatChannel? _officeChannel;

    /// <summary>The channel it shows: the studio's, the project's own, or (all projects) the last one open in Chat.</summary>
    private ChatChannel? FindOfficeChannel()
    {
        var room = _state.Chat;
        if (FilterProject is not { } project)
            return _main.Chat.Channel is { Direct: false } open && room.Channels.Contains(open) ? open
                : room.Channels.Where(c => !c.Direct && ChatRoom.Member(c, _state.UserName) != null).OrderBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
        if ((ShownStudio ?? StudioFlow.OpenStudiosOf(project).FirstOrDefault())?.ChannelId is { } id && room.Channels.FirstOrDefault(c => c.Id == id) is { } studio)
            return studio;
        return room.Channels.Where(c => !c.Direct && c.Link is { CategoryId: null, ItemId: null } link && link.ProjectId == project.Id)
            .OrderBy(c => c.Access == ChannelAccess.Open ? 0 : 1).ThenBy(c => c.CreatedAt).FirstOrDefault();
    }

    public bool HasOfficeChannel => _officeChannel != null;
    public string ChannelTitle => _officeChannel is { } c ? "#" + c.Name : "";
    public string? ChannelTopic => _officeChannel?.Topic;
    /// <summary>No channel to show: why, and where to start one.</summary>
    public string ChannelHint => FilterProject is { } project
        ? $"{project.Name} has no channel yet. Start one with its Chat button, or from its right-click menu › Start a chat channel…"
        : "You have no channels yet. Start one in Chat.";
    public IReadOnlyList<PrivateLine> ChannelLines { get; private set; } = [];
    public bool HasChannelLines => ChannelLines.Count > 0;

    /// <summary>The panel changed (shown, another project, a new message): rebuilds what it shows.</summary>
    internal void RefreshChannel()
    {
        _officeChannel = ShowChannel ? FindOfficeChannel() : null;
        // Not the messages of those you ignore in Chat (unless they mention you).
        ChannelLines = _officeChannel is not { } shown ? [] : [.. shown.Messages
            .Where(m => !m.Notice && !m.Deleted && !ChatIgnoring.Hides(shown, m, _state.UserName, _state.Settings.IgnoredInChat)).TakeLast(ChannelLinesShown).Select(m =>
        {
            var mine = !m.Agent && string.Equals(m.By, _state.UserName, StringComparison.OrdinalIgnoreCase);
            var text = string.Join(' ', m.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (text.Length == 0 && m.Attachments is { Count: > 0 } files) text = files.Count == 1 ? "[1 file]" : $"[{files.Count} files]";
            return new PrivateLine(mine ? "You" : m.By, text.Length > 300 ? text[..299].TrimEnd() + "…" : text,
                m.At.ToString(m.At.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture), mine);
        })];
        foreach (var name in new[] { nameof(HasOfficeChannel), nameof(ChannelTitle), nameof(ChannelTopic), nameof(ChannelHint), nameof(ChannelLines),
                     nameof(HasChannelLines), nameof(ShowChannelToolTip) })
            OnPropertyChanged(name);
        SendToChannelCommand.NotifyCanExecuteChanged();
        ChannelRefreshed?.Invoke();
    }

    /// <summary>The panel shows new lines (the view scrolls to the newest).</summary>
    public event Action? ChannelRefreshed;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendToChannelCommand))] private string _channelDraft = "";

    private bool CanSendToChannel() => _officeChannel != null && ChannelDraft.Trim().Length > 0;

    /// <summary>Send (or Enter): to the channel, as in Chat, staying here.</summary>
    [RelayCommand(CanExecute = nameof(CanSendToChannel))]
    private void SendToChannel()
    {
        if (_officeChannel is not { } channel) return;
        try
        {
            var room = _state.Chat;
            if (ChatRoom.Member(channel, _state.UserName) == null) room.Join(channel, _state.ChatMe);
            room.Post(channel, _state.ChatMe, ChannelDraft.Trim());
        }
        catch (ChatException ex)
        {
            Say(ex.Message);
            return;
        }
        ChannelDraft = "";
        RefreshChannel();
    }

    [RelayCommand]
    private void OpenChannelInChat()
    {
        if (_officeChannel is { } channel) _main.ShowChannel(channel);
    }
}
