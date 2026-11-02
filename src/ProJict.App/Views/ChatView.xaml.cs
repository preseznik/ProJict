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
    private bool _showingEarlier;
    private bool _opening;
    private bool _scrollQueued;
    /// <summary>Where the view itself last scrolled to, so that isn't taken for you scrolling up.</summary>
    private double _scrolledTo = double.NaN;

    public ChatView()
    {
        InitializeComponent();
        // Ctrl+F searches Chat.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        };
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
        // Opening a channel syncs its messages twice (the New line comes the second time): one scroll, after both.
        _opening |= _vm.Channel != _shownChannel;
        _shownChannel = _vm.Channel;
        var mine = _vm.Messages.LastOrDefault() is { IsMine: true } last && DateTime.Now - last.Model.At < TimeSpan.FromSeconds(5);
        if ((!_opening && !_atBottom && !mine) || _scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollQueued = false;
            var opening = _opening;
            _opening = false;
            // The New line near the top, with a little of what came before it; otherwise the newest message.
            if (opening && _vm?.Messages.FirstOrDefault(m => m.IsFirstUnread) is { } first && Container(first) is { } element)
                MessageScroller.ScrollToVerticalOffset(_scrolledTo = Math.Max(0, element.TranslatePoint(new Point(0, 0), (UIElement)MessageScroller.Content).Y - 40));
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

    private void MessageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _atBottom = MessageScroller.VerticalOffset >= MessageScroller.ScrollableHeight - 40;
        // Scrolling up near the top brings the older messages in (not when the list changed size, or the view went to the New line).
        var ours = Math.Abs(MessageScroller.VerticalOffset - _scrolledTo) < 1;
        _scrolledTo = double.NaN;
        if (e.VerticalChange < 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && !ours && MessageScroller.VerticalOffset < 300 && _vm is { HasEarlier: true })
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                // Several scrolls may ask: once one brought them in, the view isn't near the top any more.
                if (MessageScroller.VerticalOffset < 300) ShowEarlier();
            });
    }

    private void ShowEarlier_Click(object sender, RoutedEventArgs e) => ShowEarlier();

    /// <summary>Older messages go in front, and the view stays on what you were reading, before it's drawn again.</summary>
    private void ShowEarlier()
    {
        if (_vm is not { HasEarlier: true } || _showingEarlier) return;
        _showingEarlier = true;
        var fromBottom = MessageScroller.ExtentHeight - MessageScroller.VerticalOffset;
        _vm.ShowEarlierCommand.Execute(null);
        MessageScroller.UpdateLayout();
        MessageScroller.ScrollToVerticalOffset(MessageScroller.ExtentHeight - fromBottom);
        _showingEarlier = false;
    }

    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (_vm.HasMentionChoices)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Up:
                    _vm.MoveMention(e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                    return;
                case Key.Tab or Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    CompleteMention(_vm.SelectedMention!);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _vm.ClearMentions();
                    e.Handled = true;
                    return;
            }
        }
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

    /// <summary>Typing or moving the caret: the @mention suggestions follow.</summary>
    private void DraftBox_Changed(object sender, RoutedEventArgs e) => _vm?.UpdateMentions(DraftBox.Text, DraftBox.CaretIndex);

    private void DraftBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking a suggestion takes the focus first; it puts the name in itself.
        if (!MentionList.IsMouseOver) _vm?.ClearMentions();
    }

    private void MentionChoice_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MentionChoice choice })
        {
            CompleteMention(choice.Name);
            e.Handled = true;
        }
    }

    /// <summary>Replaces the "@na" before the caret with the name.</summary>
    public void CompleteMention(string name)
    {
        var (text, caret) = ChatViewModel.InsertMention(DraftBox.Text, DraftBox.CaretIndex, name);
        DraftBox.Text = text;
        DraftBox.CaretIndex = caret;
        _vm?.ClearMentions();
    }

    private void InviteBox_DropDownOpened(object sender, EventArgs e) => _vm?.RefreshInviteChoices();

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
        if (_vm.IsDirect) { }
        else if (_vm.IsMember) menu.Items.Add(new MenuItem { Header = "Leave the channel", Command = _vm.LeaveCommand });
        else menu.Items.Add(new MenuItem { Header = "Join the channel", Command = _vm.JoinCommand });
        if (_vm.IsModerator && !_vm.IsDirect)
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
        menu.Items.Add(new MenuItem { Header = "Private message", Command = member.PrivateMessageCommand });
        menu.Items.Add(new Separator());
        if (member.IsMuted) menu.Items.Add(new MenuItem { Header = "Unmute", Command = member.UnmuteCommand });
        menu.Items.Add(new MenuItem { Header = "Mute for 15 minutes", Command = member.MuteCommand, CommandParameter = "15" });
        menu.Items.Add(new MenuItem { Header = "Mute for 1 hour", Command = member.MuteCommand, CommandParameter = "60" });
        menu.Items.Add(new MenuItem { Header = "Mute until unmuted", Command = member.MuteCommand, CommandParameter = "0" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = member.ModeratorHeader, Command = member.ToggleModeratorCommand });
        menu.Items.Add(new MenuItem { Header = "Remove from the channel", Command = member.KickCommand, ToolTip = "They can't rejoin until invited again" });
        menu.IsOpen = true;
    }

    /// <summary>Esc clears the search (and the channels come back); Enter opens the first message found.</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Escape && _vm.IsSearching)
        {
            _vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.SearchResults.FirstOrDefault() is { } first)
        {
            _vm.OpenSearchResultCommand.Execute(first);
            e.Handled = true;
        }
    }
}
