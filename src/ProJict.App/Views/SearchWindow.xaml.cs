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

    /// <summary>An item, note, or chat message that matches.</summary>
    public sealed record ResultRow(SearchHit? Hit, ProJict.Core.Chat.ChatHit? Chat = null)
    {
        public string Snippet => Hit?.Snippet ?? Chat!.Snippet;
        public string Location => Hit?.Location ?? (Chat!.Channel.Direct
            ? $"Chat › private: {string.Join(" & ", Chat.Channel.Members.Select(m => m.Name))} › {Chat.Message.By}, {Chat.Message.At:d MMM HH:mm}"
            : $"Chat › #{Chat.Channel.Name} › {Chat.Message.By}, {Chat.Message.At:d MMM HH:mm}");
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
        var hits = GlobalSearch.Search(_state.Projects, Query.Text, 150);
        var chat = ProJict.Core.Chat.ChatSearch.Search(_state.Chat.Channels, Query.Text, 50);
        var rows = hits.Select(h => new ResultRow(h)).Concat(chat.Select(c => new ResultRow(null, c))).ToList();
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
        else _main.ShowChannel(row.Chat!.Channel, row.Chat.Message.Id);
    }
}
