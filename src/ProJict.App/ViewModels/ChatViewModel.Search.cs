using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;

namespace ProJict.App.ViewModels;

/// <summary>
/// Searching Chat: the box at the top of the channel list (Ctrl+F). While it has words, the list shows the messages
/// that have them all (in the text, the author or the channel's name), newest first; a click opens the channel at the
/// message, and the results stay for the next one. Everywhere you can read, or only in the open channel.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>The most results listed: more than that, and more words narrow it down.</summary>
    public const int MostSearchResults = 100;

    [ObservableProperty] private string _searchText = "";
    /// <summary>Only in the open channel.</summary>
    [ObservableProperty] private bool _searchHere;

    public bool IsSearching => SearchText.Trim().Length > 0;
    public List<ChatSearchRow> SearchResults { get; private set; } = [];
    public bool HasSearchResults => SearchResults.Count > 0;
    public string SearchSummary => !IsSearching ? ""
        : SearchResults.Count == 0 ? (SearchHere ? "Nothing in this channel has those words." : "No messages have those words.")
        : SearchResults.Count >= MostSearchResults ? $"The newest {MostSearchResults} messages: add a word to narrow it down"
        : SearchResults.Count == 1 ? "1 message" : $"{SearchResults.Count} messages";
    public string SearchHereText => Channel is { } channel ? $"Only in {(channel.Direct ? "this conversation" : "#" + channel.Name)}" : "Only in the open channel";

    /// <summary>The message last opened from the results (it's marked in the list).</summary>
    private Guid? _searchOpened;

    partial void OnSearchTextChanged(string value) => RunSearch();
    partial void OnSearchHereChanged(bool value) => RunSearch();

    /// <summary>Everything you can read: your channels and the others, your private conversations (the agents' too while they show), and other ProJicts' shared with you.</summary>
    private IEnumerable<ChatChannel> SearchableChannels =>
        AllChannels.Where(c => !c.Direct || ChatRoom.Member(c, NameIn(c)) != null || ShowAgentChats);

    internal void RunSearch()
    {
        var channels = SearchHere && Channel != null ? [Channel] : SearchableChannels;
        SearchResults = IsSearching
            ? [.. ChatSearch.Search(channels, SearchText, MostSearchResults).Select(hit => new ChatSearchRow(hit, this, hit.Message.Id == _searchOpened))]
            : [];
        foreach (var name in new[] { nameof(IsSearching), nameof(SearchResults), nameof(HasSearchResults), nameof(SearchSummary), nameof(SearchHereText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void OpenSearchResult(ChatSearchRow? row)
    {
        if (row == null) return;
        _searchOpened = row.Hit.Message.Id;
        IsCreating = false;
        IsBrowsing = false;
        if (Channel != row.Hit.Channel) Channel = row.Hit.Channel;
        Refresh();
        HighlightMessage(row.Hit.Message.Id);
        // Refresh made the rows again: mark the opened one by its message.
        foreach (var r in SearchResults) r.IsSelected = r.Hit.Message.Id == _searchOpened;
        OnPropertyChanged(nameof(SearchHereText));
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchOpened = null;
        SearchHere = false;
        SearchText = "";
    }
}

/// <summary>A message found by Chat's search: where, who, when and the line with the words.</summary>
public sealed partial class ChatSearchRow(ChatHit hit, ChatViewModel owner, bool selected) : ObservableObject
{
    public ChatHit Hit => hit;
    public string Where => (hit.Channel.Direct ? ChatRoom.Title(hit.Channel, owner.NameIn(hit.Channel)) : "#" + hit.Channel.Name) +
                           (owner.PlaceOf(hit.Channel).Host?.OwnerName is { } source ? " · " + source : "");
    public string Who => hit.Message.By;
    public string When => hit.Message.At.ToString(hit.Message.At.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
    public string Snippet => hit.Snippet;
    public string ToolTip => $"{Where} · {Who}, {hit.Message.At.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture)}\nClick to open it in its channel";
    [ObservableProperty] private bool _isSelected = selected;
}
