using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.Core.Chat;

namespace ProJict.App.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _vm;
    private ChatChannel? _shownChannel;
    private bool _atBottom = true;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ChatViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as ChatViewModel);
    }

    private void Attach(ChatViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null)
        {
            _vm.MessagesChanged -= OnMessagesChanged;
            _vm.Reveal -= OnReveal;
            _vm.FocusComposer -= OnFocusComposer;
        }
        _vm = vm;
        if (_vm == null) return;
        _vm.MessagesChanged += OnMessagesChanged;
        _vm.Reveal += OnReveal;
        _vm.FocusComposer += OnFocusComposer;
        _shownChannel = null;
        OnMessagesChanged();
    }

    /// <summary>
    /// A channel just opened: to the "New" line, or the newest message. Later: follows new messages while you were at the
    /// bottom (or wrote one yourself), and stays put while you read further up.
    /// </summary>
    private void OnMessagesChanged()
    {
        if (_vm == null) return;
        var opened = _vm.Channel != _shownChannel;
        _shownChannel = _vm.Channel;
        var mine = _vm.Messages.LastOrDefault() is { IsMine: true } last && DateTime.Now - last.Model.At < TimeSpan.FromSeconds(5);
        if (!opened && !_atBottom && !mine) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (opened && _vm?.Messages.FirstOrDefault(m => m.IsFirstUnread) is { } first && Container(first) is { } element)
                element.BringIntoView(new Rect(0, 0, 1, 1));
            else MessageScroller.ScrollToEnd();
        });
    }

    private void OnReveal(ChatMessageViewModel message) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Container(message)?.BringIntoView());

    private FrameworkElement? Container(ChatMessageViewModel message) => MessageList.ItemContainerGenerator.ContainerFromItem(message) as FrameworkElement;

    private void OnFocusComposer()
    {
        // Only within ProJict's own window, when you asked for it (Reply or Edit).
        if (IsVisible) DraftBox.Focus();
    }

    private void MessageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        _atBottom = MessageScroller.VerticalOffset >= MessageScroller.ScrollableHeight - 40;

    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        switch (e.Key)
        {
            case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _vm.IsReplying || _vm.IsEditing:
                _vm.CancelReplyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when DraftBox.Text.Length == 0:
                e.Handled = _vm.EditLast();
                if (e.Handled) DraftBox.CaretIndex = DraftBox.Text.Length;
                break;
            case Key.V when Keyboard.Modifiers == ModifierKeys.Control && ClipboardAttachments.HasAttachment():
                _vm.PasteFiles();
                e.Handled = true;
                break;
        }
    }

    private void Draft_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Draft_PreviewDrop(object sender, DragEventArgs e)
    {
        if (_vm == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _vm.AddFiles(files);
        e.Handled = true;
    }

    /// <summary>The channel's ⋯ menu. Its own font is set, so it doesn't pick up the button's icon font.</summary>
    private void ChannelMore_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.Channel == null || sender is not FrameworkElement button) return;
        var menu = new ContextMenu { FontFamily = FontFamily, PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_vm.LinkText != null) menu.Items.Add(new MenuItem { Header = "Show what it's about", Command = _vm.OpenLinkCommand });
        if (_vm.IsMember) menu.Items.Add(new MenuItem { Header = "Leave the channel", Command = _vm.LeaveCommand });
        else menu.Items.Add(new MenuItem { Header = "Join the channel", Command = _vm.JoinCommand });
        if (_vm.IsModerator)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = _vm.IsInviteOnly ? "Open it to everyone who can see its project" : "Make it invite only", Command = _vm.ToggleAccessCommand });
        }
        if (_vm.IsOwner)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Delete channel…", Command = _vm.DeleteChannelCommand });
        }
        menu.IsOpen = true;
    }

    /// <summary>A member's ⋯ menu, for moderators.</summary>
    private void MemberMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMemberViewModel member } button) return;
        var menu = new ContextMenu { FontFamily = FontFamily, PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (member.IsMuted) menu.Items.Add(new MenuItem { Header = "Unmute", Command = member.UnmuteCommand });
        menu.Items.Add(new MenuItem { Header = "Mute for 15 minutes", Command = member.MuteCommand, CommandParameter = "15" });
        menu.Items.Add(new MenuItem { Header = "Mute for 1 hour", Command = member.MuteCommand, CommandParameter = "60" });
        menu.Items.Add(new MenuItem { Header = "Mute until unmuted", Command = member.MuteCommand, CommandParameter = "0" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = member.ModeratorHeader, Command = member.ToggleModeratorCommand });
        menu.Items.Add(new MenuItem { Header = "Remove from the channel", Command = member.KickCommand, ToolTip = "They can't rejoin until invited again" });
        menu.IsOpen = true;
    }
}
