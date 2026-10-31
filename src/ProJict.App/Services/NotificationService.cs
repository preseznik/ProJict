using System.ComponentModel;
using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Api;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.Services;

/// <summary>A Windows notification to show: what happened, and what a click opens.</summary>
public sealed record Notice(string Title, string Text, Action? Open);

/// <summary>
/// Windows notifications for what needs you (Settings › Notifications): work to review, new comments in your
/// discussions, ship plans to approve and people asking to join, and if you like check-outs, every comment, API tokens
/// about to expire and updates. It compares what waits with what it saw before, so only new things notify: never
/// what was there when ProJict started, never your own actions, nothing while ProJict is in front, nothing from a
/// muted project; a burst comes as one notification.
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan Gather = TimeSpan.FromSeconds(2);

    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly Action<Notice> _show;
    private readonly Func<bool> _inFront;
    /// <summary>What was seen, per project (Guid.Empty: group discussions and the rest).</summary>
    private readonly Dictionary<Guid, HashSet<string>> _seen = [];
    private readonly List<Event> _pending = [];
    private readonly DispatcherTimer _gather;
    private readonly DispatcherTimer _hourly;

    /// <summary>Something new that may notify; <see cref="Key"/> tells it apart from what was seen.</summary>
    public sealed record Event(NotificationKind Kind, string Key, Guid? ProjectId, string Where, string Title, string Text, Action Open);

    public NotificationService(AppState state, MainViewModel main, Action<Notice> show, Func<bool> inFront)
    {
        _state = state;
        _main = main;
        _show = show;
        _inFront = inFront;
        _gather = new DispatcherTimer { Interval = Gather };
        _gather.Tick += (_, _) => Flush();
        _hourly = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _hourly.Tick += (_, _) => CheckOthers();

        // What waits already when ProJict starts doesn't notify.
        foreach (var project in state.Projects) _seen[project.Id] = Keys(EventsOf(project));
        _seen[Guid.Empty] = Keys(OtherEvents());

        state.ProjectChanged += Check;
        state.ProjectListChanged += () =>
        {
            foreach (var project in state.Projects.Where(p => !_seen.ContainsKey(p.Id))) _seen[project.Id] = Keys(EventsOf(project));
        };
        state.GroupCommentsChanged += _ => CheckOthers();
        state.Chat.Changed += _ => CheckOthers();
        main.Host.Requests.CollectionChanged += (_, _) => CheckOthers();
        main.Updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateService.IsAvailable)) CheckOthers();
        };
        _hourly.Start();
    }

    private NotificationSettings Settings => _state.AppSettings.Notifications;

    /// <summary>Shows what gathered (normally after a short pause, so a burst comes as one).</summary>
    public void Flush()
    {
        _gather.Stop();
        if (_pending.Count == 0) return;
        var events = _pending.ToList();
        _pending.Clear();
        // While you're looking at ProJict, its red dots say it already.
        if (_inFront()) return;
        _show(NoticeFor(events));
    }

    /// <summary>Group discussions, join requests, tokens and updates (also checked every hour, for tokens).</summary>
    public void CheckOthers() => Compare(Guid.Empty, OtherEvents());

    private void Check(Project project) => Compare(project.Id, EventsOf(project));

    private void Compare(Guid id, List<Event> events)
    {
        var seen = _seen.GetValueOrDefault(id) ?? [];
        var fresh = events.Where(e => !seen.Contains(e.Key)).ToList();
        _seen[id] = Keys(events);
        foreach (var e in fresh.Where(Wanted))
        {
            _pending.Add(e);
            _gather.Stop();
            _gather.Start();
        }
    }

    private bool Wanted(Event e) =>
        Settings.Enabled && Settings.Allows(e.Kind) && (e.ProjectId is not { } id || !Settings.MutedProjects.Contains(id));

    private static HashSet<string> Keys(IEnumerable<Event> events) => events.Select(e => e.Key).ToHashSet();

    // ───────────── What may notify ─────────────

    private bool IsMe(string? name) => string.Equals(name, _state.UserName, StringComparison.OrdinalIgnoreCase);

    /// <summary>You wrote in the discussion.</summary>
    private bool WroteIn(List<Comment>? threads) => threads.Walk().Any(e => !e.Comment.Agent && IsMe(e.Comment.By));

    /// <summary>An item you added (or imported); one an agent added starts its history with the agent.</summary>
    private static bool Yours(TodoItem item) => item.Activity is not { Count: > 0 } log || !log[0].Agent;

    private List<Event> EventsOf(Project project)
    {
        var events = new List<Event>();
        var where = project.Name;
        foreach (var loc in project.Walk())
        {
            var item = loc.Item;
            var text = Short(item.DisplayText());
            if (item.NeedsReview)
            {
                var by = item.Activity?.LastOrDefault(a => a.Agent)?.By ?? "An AI agent";
                events.Add(new Event(NotificationKind.Review, $"review:{item.Id}", project.Id, where, "Waiting for your review", $"{by} finished “{text}” in {where}.",
                    () => Reveal(project, item.Id)));
            }
            if (item.State == ItemState.InProgress && item.ClaimedBy is { } claimer && !IsMe(claimer))
                events.Add(new Event(NotificationKind.CheckOut, $"checkout:{item.Id}:{claimer}", project.Id, where, $"{claimer} is working on “{text}”", where,
                    () => Reveal(project, item.Id)));
            var mine = WroteIn(item.Comments) || Yours(item);
            foreach (var (comment, _) in item.Comments.Walk().Where(e => !e.Comment.Deleted && !IsMe(e.Comment.By)))
                events.Add(CommentEvent(comment, mine, project.Id, where, $"“{text}”", () => OpenDiscussion(project, item.Id, comment.Id)));
        }
        foreach (var category in project.Categories)
        {
            var mine = WroteIn(category.Comments);
            foreach (var (comment, _) in category.Comments.Walk().Where(e => !e.Comment.Deleted && !IsMe(e.Comment.By)))
                events.Add(CommentEvent(comment, mine, project.Id, where, category.Name, () => OpenDiscussion(project, category.Id, comment.Id)));
        }
        if (project.OpenShip is { State: ShipState.Planned } ship)
            events.Add(new Event(NotificationKind.ShipPlan, $"ship:{ship.Id}", project.Id, where, "A ship plan waits for your approval",
                $"{ship.PlannedBy ?? "An AI agent"} planned shipping {where}{(ship.Version is { } v ? " " + v : "")}.", () => _main.OpenProject(project).RevealShip(ship)));
        return events;
    }

    private Event CommentEvent(Comment comment, bool mine, Guid? projectId, string where, string on, Action open)
    {
        var text = comment.Text.Trim().Length > 0 ? Short(comment.Text, 120)
            : comment.Attachments is { Count: > 0 } files ? "📎 " + string.Join(", ", files.Select(f => f.FileName)) : "";
        return new Event(mine ? NotificationKind.Comment : NotificationKind.AnyComment, $"comment:{comment.Id}", projectId, where,
            $"{comment.By} commented on {on}", text, open);
    }

    private List<Event> OtherEvents()
    {
        var events = new List<Event>();
        // Chat: what you haven't read in your channels; an @mention is its own kind.
        foreach (var channel in _state.Chat.Channels)
            foreach (var message in ProJict.Core.Chat.ChatRoom.Unread(channel, _state.UserName).Where(m => !m.Notice))
            {
                var mention = ProJict.Core.Chat.ChatText.Mentions(message.Text, _state.UserName);
                var where = "#" + channel.Name;
                var open = () => _main.ShowChannel(channel, message.Id);
                events.Add(new Event(mention ? NotificationKind.ChatMention : NotificationKind.ChatMessage, $"chat:{message.Id}", channel.Link?.ProjectId, where,
                    mention ? $"{message.By} mentioned you in {where}" : $"{message.By} in {where}",
                    message.Text.Length > 0 ? Short(message.Text, 120) : "Posted a file.", open));
            }
        foreach (var group in _state.Settings.Groups)
        {
            var threads = _state.Settings.CommentsFor(group);
            var mine = WroteIn(threads);
            foreach (var (comment, _) in threads.Walk().Where(e => !e.Comment.Deleted && !IsMe(e.Comment.By)))
                events.Add(CommentEvent(comment, mine, null, $"{group} group", $"the {group} group", () =>
                {
                    _main.OpenDiscussion(DiscussionTarget.ForGroup(group));
                    _main.Discussion?.Highlight(comment.Id);
                }));
        }
        foreach (var request in _main.Host.Requests)
            events.Add(new Event(NotificationKind.JoinRequest, $"join:{request.Name}|{request.Device}|{request.Address}|{request.Invite.Code}", null, "Sharing",
                $"{request.Name} asks to join your ProJict", request.Text, () => _main.OpenSharingCommand.Execute(null)));
        List<ApiToken> tokens;
        lock (_main.ApiStore.Sync) tokens = [.. _main.ApiStore.Data.Tokens];
        var now = DateTime.Now;
        foreach (var token in tokens.Where(t => !t.IsExpired(now) && now >= t.ExpiresAt - ApiData.ExpiryNotice))
            events.Add(new Event(NotificationKind.TokenExpiring, $"token:{token.Id}:{token.ExpiresAt:O}", null, "API", "An API token expires soon",
                $"“{token.Name}” expires on {token.ExpiresAt:yyyy-MM-dd}. Renew it in Settings › API.", () => _main.OpenApiCommand.Execute(null)));
        if (_main.Updates.Available is { } update)
            events.Add(new Event(NotificationKind.Update, $"update:{update.Version}", null, "ProJict", $"ProJict {update.Version.ToString(3)} is ready",
                "Install it from Settings › Updates.", () => _main.OpenSettingsCommand.Execute(null)));
        return events;
    }

    // ───────────── What it says, and where a click leads ─────────────

    /// <summary>One thing as it is; several in one place as "3 things wait for you in GRID2VR"; more places as a count each.</summary>
    public Notice NoticeFor(IReadOnlyList<Event> events)
    {
        if (events.Count == 1) return new Notice(events[0].Title, events[0].Text, events[0].Open);
        var places = events.GroupBy(e => e.Where).ToList();
        if (places.Count == 1)
        {
            var place = places[0].First();
            var project = place.ProjectId is { } id ? _state.Projects.FirstOrDefault(p => p.Id == id) : null;
            return new Notice($"{events.Count} things wait for you in {place.Where}",
                string.Join("\n", events.Take(3).Select(e => "• " + e.Title)) + (events.Count > 3 ? "\n…" : ""),
                project != null ? () => _main.OpenProject(project) : place.Open);
        }
        return new Notice($"{events.Count} things wait for you",
            string.Join(", ", places.Select(g => $"{g.Key} ({g.Count()})")), () => _main.ShowDashboard());
    }

    private void Reveal(Project project, Guid itemId)
    {
        var board = _main.OpenProject(project);
        if (project.Find(itemId) is { } loc) board.Reveal(loc.Item);
    }

    private void OpenDiscussion(Project project, Guid targetId, Guid commentId)
    {
        var board = _main.OpenProject(project);
        if (project.Find(targetId) is { } loc) board.Reveal(loc.Item);
        _main.OpenDiscussion(DiscussionTarget.InProject(project, targetId));
        _main.Discussion?.Highlight(commentId);
    }

    private static string Short(string text, int max = 60)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }
}
