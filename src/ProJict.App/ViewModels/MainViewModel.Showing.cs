using System.ComponentModel;
using ProJict.App.Services.Sharing;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.ViewModels;

/// <summary>One thing the sidebar's Showing box offers: everything, this ProJict's own, or one connected ProJict's.</summary>
public sealed record ShowingChoice(string? From, string Label, string ToolTip);

/// <summary>
/// Switching what a connected ProJict shows (#45694fa0): everything together, only its own projects and channels, or
/// only those of one ProJict it's connected to. The sidebar, the dashboard, the feed, Chat's list, the wiki and Search
/// follow it; the choice is kept on this computer.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>What the Showing box offers; it shows only while this ProJict is connected to another.</summary>
    public IReadOnlyList<ShowingChoice> ShowingChoices { get; private set; } = [];

    public bool CanChooseShowing => Guest.Connections.Count > 0;

    /// <summary>What the lists show now, so a change of connections that changes it refreshes them.</summary>
    private string? _showing;

    /// <summary>What's shown now (null: everything): the choice on this computer, while its connection is still there.</summary>
    public string? ShowingFromNow => ShowingFrom.Effective(State.AppSettings.ShowingFrom, Guest.Connections.Select(c => c.Address));

    public ShowingChoice? Showing
    {
        get => ShowingChoices.FirstOrDefault(c => c.From == ShowingFromNow) ?? ShowingChoices.FirstOrDefault();
        set
        {
            if (value == null || value.From == ShowingFromNow) return;
            State.AppSettings.ShowingFrom = value.From;
            State.SaveAppSettings();
            ShowingChanged();
        }
    }

    /// <summary>Whether a project is shown, as the Showing box says.</summary>
    public bool Shows(Project project) => ShowingFrom.Shows(ShowingFromNow, project);

    /// <summary>This ProJict's own channels and wiki are shown.</summary>
    public bool ShowsOwn => ShowingFrom.ShowsOwn(ShowingFromNow);

    /// <summary>A connected ProJict's (or a guest's sharing its channels back) channels and wiki are shown.</summary>
    public bool ShowsChat(IChatRemote remote) =>
        remote is HostConnection connection ? ShowingFrom.ShowsHost(ShowingFromNow, connection.Address) : ShowingFromNow == null;

    /// <summary>Keeps the choices up to date as connections are added, removed or renamed.</summary>
    private void WatchShowing()
    {
        _showing = ShowingFromNow;
        void Changed()
        {
            RebuildShowingChoices();
            // The connection it showed was removed (or one it showed came back): the lists follow.
            if (ShowingFromNow != _showing) ShowingChanged();
        }
        void Renamed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HostConnection.HostName)) RebuildShowingChoices();
        }
        foreach (var connection in Guest.Connections) connection.PropertyChanged += Renamed;
        Guest.Connections.CollectionChanged += (_, e) =>
        {
            foreach (var connection in e.NewItems?.OfType<HostConnection>() ?? []) connection.PropertyChanged += Renamed;
            foreach (var connection in e.OldItems?.OfType<HostConnection>() ?? []) connection.PropertyChanged -= Renamed;
            Changed();
        };
        RebuildShowingChoices();
    }

    private void RebuildShowingChoices()
    {
        ShowingChoices =
        [
            new(null, "Everything", "Your own projects and channels, and those of every ProJict you're connected to"),
            new(ShowingFrom.Own, "This ProJict", "Only your own projects and channels"),
            .. Guest.Connections.Select(c => new ShowingChoice(c.Address, $"{c.HostName}'s ProJict", $"Only what {c.HostName}'s ProJict ({c.Address}) shares with you")),
        ];
        OnPropertyChanged(nameof(ShowingChoices));
        OnPropertyChanged(nameof(Showing));
        OnPropertyChanged(nameof(CanChooseShowing));
    }

    /// <summary>Everything that lists projects or channels shows the new choice.</summary>
    private void ShowingChanged()
    {
        _showing = ShowingFromNow;
        OnPropertyChanged(nameof(Showing));
        SyncGroupNames();
        ProjectsView.Refresh();
        SyncSelection();
        OnPropertyChanged(nameof(NothingToShow));
        Dashboard.Refresh();
        if (CurrentView == Feed) Feed.Refresh();
        Chat.Refresh();
        Wiki.RefreshPages();
        // A project that isn't shown any more isn't left open.
        if (CurrentProject is { IsInbox: false } open && !Shows(open.Project)) ShowDashboard();
        State.StatusText = Showing is { } now ? $"Showing {(now.From == null ? "everything" : now.Label)}." : "";
    }
}
