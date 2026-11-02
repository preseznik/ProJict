using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;

namespace ProJict.App.ViewModels;

/// <summary>
/// One agent in the Agents view's list of all agents: connected now (with what it's doing and where) or seen here before
/// (signed work, comments or chat). Right-click: write to it privately, invite it to a channel, rename it (while it's
/// connected), show its card or its work in the Feed.
/// </summary>
public sealed partial class AgentListRow : ObservableObject
{
    private readonly AgentActivityViewModel _owner;

    public AgentListRow(AgentActivityViewModel owner, string name, McpSession? session, string? client, DateTime? lastSeen)
    {
        _owner = owner;
        Name = name;
        Session = session;
        Seen(client, lastSeen);
    }

    public string Name { get; }
    public McpSession? Session { get; }
    /// <summary>What it runs in ("Claude Code"), when known.</summary>
    public string? Client { get; private set; }
    public DateTime? LastSeen { get; private set; }

    /// <summary>The list was rebuilt: the row stays (and so does a rename under way), with what's known now.</summary>
    internal void Seen(string? client, DateTime? lastSeen)
    {
        Client = client;
        LastSeen = lastSeen;
        if (!IsConnected && !IsVisitor) Detail = OfflineDetail(DateTime.Now);
    }
    public WorkerBadge Badge => WorkerBadge.For([Name])[0];
    public bool IsConnected => Session != null;

    /// <summary>Connected to another ProJict that's connected to this one: whose, and whether it's away there.</summary>
    public string? VisitingFrom { get; private set; }
    public bool IsVisitor => VisitingFrom != null;
    public bool IsAway { get; private set; }
    /// <summary>The green (or amber, away) dot: connected here, or to another ProJict.</summary>
    public bool ShowsDot => IsConnected || IsVisitor;

    internal void Visiting(string? from, bool away)
    {
        if (VisitingFrom == from && IsAway == away) return;
        VisitingFrom = from;
        IsAway = away;
        if (from != null) Detail = $"AI agent connected to {from}'s ProJict · {(away ? "away" : "online")}";
        else if (!IsConnected) Detail = OfflineDetail(DateTime.Now);
        foreach (var name in new[] { nameof(VisitingFrom), nameof(IsVisitor), nameof(IsAway), nameof(ShowsDot), nameof(ToolTip) }) OnPropertyChanged(name);
    }
    /// <summary>"Claude Code · Building · in GRID2VR", or "Codex · last seen 3 Oct".</summary>
    [ObservableProperty] private string _detail = "";
    public string ToolTip => IsConnected
        ? $"{Session!.FullName}, connected since {Session.ConnectedAt.ToString(Session.ConnectedAt.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture)}.\nClick to show its card; right-click for more."
        : IsVisitor ? $"{Name} is an AI agent connected to {VisitingFrom}'s ProJict ({(IsAway ? "away" : "online")}).\nRight-click to write to it or invite it to a channel."
        : $"{Name} isn't connected now.\nRight-click to write to it or invite it: it gets the message when it comes back as {Name}.";

    /// <summary>What it's doing now (connected), refreshed every second while the view shows.</summary>
    internal void Tick(string activity, IReadOnlyList<string> projects)
    {
        if (Session is not { } session) return;
        var parts = new List<string>();
        if (session.Name != null || !string.IsNullOrWhiteSpace(session.SignAs)) parts.Add(session.Client);
        parts.Add(activity);
        if (projects.Count > 0) parts.Add("in " + string.Join(", ", projects));
        Detail = string.Join(" · ", parts);
    }

    private string OfflineDetail(DateTime now)
    {
        if (IsConnected) return "";
        var seen = LastSeen is not { } at ? "not seen lately"
            : at.Date == now.Date ? $"last seen at {at.ToString("HH:mm", CultureInfo.CurrentCulture)}"
            : at.Year == now.Year ? $"last seen {at.ToString("d MMM", CultureInfo.CurrentCulture)}"
            : $"last seen {at.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        return Client != null && !string.Equals(Client, Name, StringComparison.OrdinalIgnoreCase) ? $"{Client} · {seen}" : char.ToUpper(seen[0]) + seen[1..];
    }

    // ───────────── Right-click ─────────────

    [RelayCommand] private void PrivateMessage() => _owner.MessagePrivately(Name);

    /// <summary>Your channels it isn't in yet.</summary>
    public IReadOnlyList<InviteTarget> InviteTargets => _owner.InviteTargets(Name);
    public bool HasInviteTargets => InviteTargets.Count > 0;

    /// <summary>Its menu is about to open: the channels may have changed since.</summary>
    public void RefreshMenu()
    {
        OnPropertyChanged(nameof(InviteTargets));
        OnPropertyChanged(nameof(HasInviteTargets));
        StartRenameCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void ShowWork() => _owner.ShowWorkOf(Name);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void ShowCard() => _owner.ShowCardOf(Session!);

    // Renaming happens in the row itself: Enter keeps the name, Esc cancels.
    public bool CanRename => Session != null;
    public string RenameToolTip => Session == null ? "Only while it's connected: from then on it signs with the new name."
        : !string.IsNullOrWhiteSpace(Session.SignAs)
            ? "Give it another name: what it has checked out and its channels go with it, and it's told. Its API token remembers the name; the token keeps its own in Settings › API."
            : "Give it another name: what it has checked out and its channels go with it, and it's told.";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private string? _renameProblem;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void StartRename()
    {
        RenameText = Name;
        RenameProblem = null;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming || Session is not { } session) return;
        RenameProblem = _owner.Rename(session, RenameText);
        if (RenameProblem == null) IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameProblem = null;
    }
}

/// <summary>A channel to invite an agent to, in its row's right-click menu.</summary>
public sealed partial class InviteTarget(string title, Action invite)
{
    public string Title => title;
    [RelayCommand] private void Invite() => invite();
}
