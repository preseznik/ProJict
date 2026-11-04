using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.App.Services.Sharing;
using ProJict.Core.Chat;

namespace ProJict.App.ViewModels;

/// <summary>
/// Channels' icons (in the list instead of the #, and beside the title; moderators set them in the ⋯ menu) and people's and
/// agents' pictures beside their messages.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>The channel's icon, if it has one: yours from the data folder, another ProJict's once it's come.</summary>
    internal ImageSource? IconOf(ChatChannel channel)
    {
        if (channel.Icon is not { } icon || channel.Direct) return null;
        switch (PlaceOf(channel).Host)
        {
            case null:
                return Icons.Load(_state.Store.AttachmentPath(icon));
            case HostConnection connection:
                var path = _main.Guest.ChatFilePath(connection, icon);
                if (!File.Exists(path)) _main.Guest.FetchChatFile(connection, icon);
                return Icons.Load(path);
            default:
                return null;
        }
    }

    /// <summary>A person's or agent's picture (one you or an agent set), for the badge beside their messages.</summary>
    internal ImageSource? PictureOf(string name) => _state.Pictures.Of(name);

    public ImageSource? Icon => Channel != null ? IconOf(Channel) : null;
    public bool HasIcon => Icon != null;
    /// <summary>One of your channels that you moderate: you can set its icon.</summary>
    public bool CanSetIcon => Channel is { Direct: false } && IsModerator && Here.Host == null;
    public bool CanRemoveIcon => CanSetIcon && Channel?.Icon != null;

    [RelayCommand]
    private void SetIcon()
    {
        if (!CanSetIcon || Channel is not { } channel || Icons.Pick($"An icon for #{channel.Name}") is not { } file) return;
        try
        {
            var icon = Icons.Make(_state.Store, file);
            _state.Chat.SetIcon(channel, _state.ChatMe, icon);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ChatException)
        {
            _state.StatusText = ex.Message;
            return;
        }
        _state.StatusText = $"#{channel.Name} has an icon.";
        RefreshIcons();
    }

    [RelayCommand]
    private void RemoveIcon()
    {
        if (!CanRemoveIcon || Channel is not { } channel) return;
        try { _state.Chat.SetIcon(channel, _state.ChatMe, null); }
        catch (ChatException ex)
        {
            _state.StatusText = ex.Message;
            return;
        }
        _state.StatusText = $"#{channel.Name} has no icon any more.";
        RefreshIcons();
    }

    private void RefreshIcons()
    {
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(CanRemoveIcon));
        Refresh();
    }
}
