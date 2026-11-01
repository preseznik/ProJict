using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>
/// Back and Forward, like a browser's: through the views, projects and Chat channels you went to (the arrows at the top
/// of the sidebar, Alt+← and Alt+→, or the mouse's side buttons). A place that's gone since is skipped.
/// </summary>
public sealed partial class MainViewModel
{
    private const int KeptPlaces = 50;

    /// <summary>Where you were: a view, with the project or channel it was at.</summary>
    private sealed record Place(string View, Guid? Id = null);

    private readonly List<Place> _back = [];
    private readonly List<Place> _forward = [];
    private Place? _here;
    private bool _going;

    private Place? Here() => CurrentView switch
    {
        ProjectViewModel p => new Place("project", p.Project.Id),
        DashboardViewModel => new Place("dashboard"),
        FeedViewModel => new Place("feed"),
        AgentActivityViewModel => new Place("agents", State.Projects.FirstOrDefault(p => p.Name == AgentActivity.ProjectFilter)?.Id),
        ChatViewModel => new Place("chat", Chat.Channel?.Id),
        _ => null,
    };

    /// <summary>Where you are changed: the place you left goes on the way back, and the way forward is gone.</summary>
    private void Arrived()
    {
        if (_going || Here() is not { } place || place == _here) return;
        if (_here != null)
        {
            _back.Add(_here);
            if (_back.Count > KeptPlaces) _back.RemoveAt(0);
        }
        _forward.Clear();
        _here = place;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoBack() => _back.Count > 0;
    private bool CanGoForward() => _forward.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Step(_back, _forward);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Step(_forward, _back);

    /// <summary>To the latest place in <paramref name="from"/> that's still there; where you were goes on <paramref name="to"/>.</summary>
    private void Step(List<Place> from, List<Place> to)
    {
        while (from.Count > 0)
        {
            var place = from[^1];
            from.RemoveAt(from.Count - 1);
            if (!Go(place)) continue;
            if (_here != null) to.Add(_here);
            _here = place;
            break;
        }
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private bool Go(Place place)
    {
        _going = true;
        try
        {
            switch (place.View)
            {
                case "project":
                    if (State.Projects.FirstOrDefault(p => p.Id == place.Id) is not { } project) return false;
                    OpenProject(project);
                    return true;
                case "dashboard":
                    ShowDashboard();
                    return true;
                case "feed":
                    ShowFeed();
                    return true;
                case "agents":
                    ShowAgents(place.Id is { } id ? State.Projects.FirstOrDefault(p => p.Id == id) : null);
                    return true;
                case "chat":
                    var channel = place.Id is { } c
                        ? State.Chat.Channels.FirstOrDefault(x => x.Id == c) ?? Guest.Connections.SelectMany(h => h.Chat?.Channels ?? []).FirstOrDefault(x => x.Id == c)
                        : null;
                    if (place.Id != null && channel == null) return false;
                    ShowChannel(channel);
                    return true;
                default:
                    return false;
            }
        }
        finally { _going = false; }
    }
}
