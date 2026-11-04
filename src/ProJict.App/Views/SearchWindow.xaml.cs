using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.ViewModels;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Views;

public partial class SearchWindow : Window
{
    private readonly AppState _state;
    private readonly MainViewModel _main;

    /// <summary>An item, note, wiki page or chat message that matches.</summary>
    public sealed record ResultRow(SearchHit? Hit, ProJict.Core.Chat.ChatHit? Chat = null, ProJict.Core.Wiki.WikiHit? Wiki = null, string? Source = null)
    {
        public string Snippet => Hit?.Snippet ?? (Wiki != null ? $"{Wiki.Page.Title}: {Wiki.Snippet}" : Chat!.Snippet);
        public string Location => Hit?.Location ?? (Wiki != null ? "Wiki › " + ProJict.Core.Wiki.WikiPaths.Display(Wiki.Page.Path) : Chat!.Channel.Direct
            ? $"Chat › private: {string.Join(" & ", Chat.Channel.Members.Select(m => m.Name))} › {Chat.Message.By}, {Chat.Message.At:d MMM HH:mm}"
            : $"Chat › #{Chat.Channel.Name}{(Source != null ? " · " + Source : "")} › {Chat.Message.By}, {Chat.Message.At:d MMM HH:mm}");
        public string StateText => Hit?.Item switch
        {
            null => "",
            { IsDone: true } => " · done",
            { State: ItemState.InProgress } => " · in progress",
            _ => "",
        };
    }

    public SearchWindow(AppState state, MainViewModel main)
    {
        InitializeComponent();
        _state = state;
        _main = main;
        Loaded += (_, _) => Query.Focus();
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueryHint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // As the sidebar's Showing box says: your own, one connected ProJict's, or everything.
        var own = _main.ShowsOwn;
        var hits = GlobalSearch.Search(_state.Projects.Where(_main.Shows), Query.Text, 150);
        var chat = (own ? ProJict.Core.Chat.ChatSearch.Search(_state.Chat.Channels, Query.Text, 50) : []).Select(c => new ResultRow(null, c))
            // And the channels of the ProJicts you're connected to, said whose.
            .Concat(_main.Guest.Connections.Where(c => c.Chat != null).Cast<Services.Sharing.IChatRemote>().Concat(_main.Host.BackChats)
                .Where(_main.ShowsChat)
                .SelectMany(r => ProJict.Core.Chat.ChatSearch.Search(r.Chat!.Channels, Query.Text, 20).Select(c => new ResultRow(null, c, Source: r.OwnerName))))
            .Take(70).ToList();
        var wiki = own ? ProJict.Core.Wiki.WikiText.Search(_state.Wiki.Pages, Query.Text, 50) : [];
        // Pages whose name matches come right after the items.
        var rows = hits.Select(h => new ResultRow(h)).Concat(wiki.Select(w => new ResultRow(null, Wiki: w))).Concat(chat).ToList();
        Results.ItemsSource = rows;
        Count.Text = Query.Text.Trim().Length == 0 ? "" : hits.Count == 150 ? "150+ results" : $"{rows.Count} result(s)";
        if (rows.Count > 0) Results.SelectedIndex = 0;
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Results.Items.Count > 0:
                Results.Focus();
                (Results.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Results.SelectedIndex)) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                Open();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Open();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open();

    private void Open()
    {
        if (Results.SelectedItem is not ResultRow row) return;
        Close();
        Owner?.Activate();
        if (row.Hit != null) _main.Navigate(row.Hit);
        else if (row.Wiki != null) _main.Navigate(row.Wiki);
        else _main.ShowChannel(row.Chat!.Channel, row.Chat.Message.Id);
    }
}
