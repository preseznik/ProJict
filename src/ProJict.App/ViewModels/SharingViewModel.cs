using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services.Sharing;
using ProJict.Core.Sharing;

namespace ProJict.App.ViewModels;

/// <summary>The Sharing window: sharing your projects (invites, requests, people) and connecting to other ProJicts.</summary>
public sealed partial class SharingViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private TaskCompletionSource<bool>? _trust;

    public SharingViewModel(MainViewModel main)
    {
        _main = main;
        InviteGroups = main.State.Settings.Groups.Select(g => new GroupChoice(g)).ToList();
        Host.Changed += Refresh;
        Guest.Changed += Refresh;
    }

    private HostService Host => _main.Host;
    private GuestService Guest => _main.Guest;

    /// <summary>The window closed: stop listening for changes.</summary>
    public void Detach()
    {
        Host.Changed -= Refresh;
        Guest.Changed -= Refresh;
        _trust?.TrySetResult(false);
    }

    // ───────────── You, on another computer ─────────────

    /// <summary>A ProJict you're connected to asks whether this computer is its user: the code it shows goes here.</summary>
    public HostConnection? SelfAsking => Guest.Connections.FirstOrDefault(c => c.SelfAsked);
    public string? SelfQuestion => SelfAsking is { } c ? $"{c.HostName}'s ProJict asks whether this computer is {c.HostName}. If it is, type the 6-digit code it shows." : null;
    public string? SelfMessage => SelfAsking?.SelfMessage ?? Guest.Connections.Select(c => c.SelfMessage).FirstOrDefault(m => m != null);
    [ObservableProperty] private string _selfCode = "";

    [RelayCommand]
    private void ConfirmSelf()
    {
        if (SelfAsking is not { } c || SelfCode.Trim().Length == 0) return;
        Guest.AnswerSelf(c, SelfCode);
        SelfCode = "";
    }

    [RelayCommand] private void DeclineSelf() { if (SelfAsking is { } c) Guest.AnswerSelf(c, null); }

    public void Refresh()
    {
        foreach (var p in new[]
                 {
                     nameof(IsSharing), nameof(PortText), nameof(HostStatus), nameof(Requests), nameof(HasRequests), nameof(Invites), nameof(HasInvites),
                     nameof(Guests), nameof(HasGuests), nameof(Hosts), nameof(HasHosts), nameof(SelfAsking), nameof(SelfQuestion), nameof(SelfMessage),
                 })
            OnPropertyChanged(p);
    }

    // ───────────── Sharing your projects ─────────────

    public bool IsSharing
    {
        get => Host.IsRunning;
        set
        {
            Host.Data.Enabled = value;
            _main.Sharing.Save();
            if (value) Host.Start();
            else Host.Stop();
            Refresh();
        }
    }

    /// <summary>Guests' AI agents may work in what you share, as far as the guest's role allows.</summary>
    public bool GuestAgents
    {
        get => Host.GuestAgents;
        set
        {
            Host.GuestAgents = value;
            OnPropertyChanged();
        }
    }

    public string PortText
    {
        get => Host.Data.Port.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (!int.TryParse(value, out var port) || port is < 1024 or > 65535 || port == Host.Data.Port) return;
            Host.Data.Port = port;
            _main.Sharing.Save();
            if (Host.IsRunning)
            {
                Host.Stop();
                Host.Start();
            }
            Refresh();
        }
    }

    public string HostStatus => Host.Error is { } error ? "Couldn't start sharing: " + error
        : !Host.IsRunning ? "Off: nobody can connect. Turn it on to invite people on your network."
        : $"On. Others connect to {AddressesText}. Your fingerprint, for them to check: {SharingData.ShortFingerprint(Host.Fingerprint)}";

    private string AddressesText => Host.Addresses() is { Count: > 0 } addresses ? string.Join(" or ", addresses) : $"this computer's address, port {Host.Port}";

    public IReadOnlyList<GuestRole> Roles { get; } = Enum.GetValues<GuestRole>();
    public List<GroupChoice> InviteGroups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InviteRoleText))]
    private GuestRole _inviteRole = GuestRole.Editor;

    public string InviteRoleText => GuestAccess.Describe(InviteRole);

    [ObservableProperty] private string? _newCode;
    [ObservableProperty] private string? _newCodeText;

    [RelayCommand]
    private void CreateInvite()
    {
        if (!Host.IsRunning)
        {
            NewCode = null;
            NewCodeText = "Turn sharing on first.";
            return;
        }
        var groups = InviteGroups.Where(g => g.IsChecked).Select(g => g.Name).ToList();
        var invite = Host.CreateInvite(InviteRole, groups.Count == 0 ? null : groups);
        NewCode = invite.Code;
        NewCodeText = $"Give them this code and your address ({AddressesText}). It works once, until {invite.ExpiresAt.ToString("ddd HH:mm", CultureInfo.CurrentCulture)}, " +
                      "and you'll still be asked to let them in.";
    }

    public IReadOnlyList<JoinRequest> Requests => Host.Requests.ToList();
    public bool HasRequests => Host.Requests.Count > 0;
    [RelayCommand] private void Approve(JoinRequest? request) { if (request != null) Host.Answer(request, JoinAnswer.Approve); }
    [RelayCommand]
    private void ApproveAsMe(JoinRequest? request)
    {
        if (request == null) return;
        request.Self = true;
        Host.Answer(request, JoinAnswer.Approve);
    }
    [RelayCommand] private void Deny(JoinRequest? request) { if (request != null) Host.Answer(request, JoinAnswer.Deny); }
    [RelayCommand] private void Block(JoinRequest? request) { if (request != null) Host.Answer(request, JoinAnswer.Block); }

    public IReadOnlyList<InviteRow> Invites => Host.Data.Invites.Where(i => i.IsValid(DateTime.Now)).Select(i => new InviteRow(i, () => Host.RevokeInvite(i))).ToList();
    public bool HasInvites => Invites.Count > 0;

    public IReadOnlyList<GuestRow> Guests => Host.Data.Guests.Select(g => new GuestRow(g, Host)).ToList();
    public bool HasGuests => Host.Data.Guests.Count > 0;

    // ───────────── Connecting to another ProJict ─────────────

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string? _connectMessage;
    [ObservableProperty] private string? _fingerprintToConfirm;
    [ObservableProperty] private bool _isConnecting;

    [RelayCommand]
    private async Task Connect()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        ConnectMessage = "Connecting…";
        try
        {
            var code = string.IsNullOrWhiteSpace(InviteCode) ? null : InviteCode;
            var result = await Guest.ConnectAsync(Address, code, AskTrust);
            var connection = Guest.Connections.FirstOrDefault(c => string.Equals(c.Address, Address.Trim(), StringComparison.OrdinalIgnoreCase));
            ConnectMessage = result ?? connection?.StateText;
            if (result == null) InviteCode = "";
        }
        finally
        {
            FingerprintToConfirm = null;
            IsConnecting = false;
            Refresh();
        }
    }

    /// <summary>First connection to a ProJict: you compare its fingerprint with the one its owner sees, then trust it.</summary>
    private Task<bool> AskTrust(string fingerprint)
    {
        _trust = new TaskCompletionSource<bool>();
        FingerprintToConfirm = SharingData.ShortFingerprint(fingerprint);
        return _trust.Task;
    }

    [RelayCommand] private void Trust() => _trust?.TrySetResult(true);
    [RelayCommand] private void DontTrust() => _trust?.TrySetResult(false);

    public IReadOnlyList<HostRow> Hosts => _main.Sharing.Data.Hosts.Select(h => new HostRow(h, Guest.Connections.FirstOrDefault(c => c.Host == h), this))
        .Concat(Guest.Connections.Where(c => !_main.Sharing.Data.Hosts.Contains(c.Host) && c.State != ConnectionState.Disconnected).Select(c => new HostRow(c.Host, c, this)))
        .ToList();
    public bool HasHosts => Hosts.Count > 0;

    internal async Task Reconnect(HostRow row)
    {
        ConnectMessage = await Guest.ConnectAsync(row.Host.Address, null, null) ?? $"Connected to {row.Title}.";
        Refresh();
    }

    internal void Disconnect(HostRow row, bool forget)
    {
        if (row.Connection != null) Guest.Disconnect(row.Connection, forget);
        else if (forget) _main.Sharing.Data.Hosts.Remove(row.Host);
        _main.Sharing.Save();
        Refresh();
    }

    internal void SetAutoConnect(KnownHost host, bool value)
    {
        host.AutoConnect = value;
        _main.Sharing.Save();
    }

    /// <summary>Your Open channels go to that ProJict too (at once when it's connected, or next time it is).</summary>
    internal void SetShareChatBack(KnownHost host, HostConnection? connection, bool value)
    {
        if (connection != null) _main.Guest.SetShareChatBack(connection, value);
        else
        {
            host.ShareChatBack = value;
            _main.Sharing.Save();
        }
    }
}

public sealed partial class GroupChoice(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isChecked;
}

public sealed class InviteRow(Invite invite, Action revoke)
{
    public string Code => invite.Code;
    public string Details => $"{invite.Role} · {(invite.Groups == null ? "all groups" : string.Join(", ", invite.Groups))} · until {invite.ExpiresAt.ToString("ddd HH:mm", CultureInfo.CurrentCulture)}";
    public IRelayCommand Revoke { get; } = new RelayCommand(revoke);
}

/// <summary>Someone allowed to connect: their role, what they see, whether they're here now.</summary>
public sealed class GuestRow(GuestAccount account, HostService host)
{
    public string Name => account.Self ? $"You, on {account.Device ?? account.Name}" : account.Name;
    public string Details =>
        (host.SelfCodeFor(account) is { } code ? $"Type {code} in its ProJict to confirm it's you · " : "") +
        $"{account.Device ?? "?"} · {(account.Groups == null ? "all groups" : string.Join(", ", account.Groups))} · " +
        (account.Blocked ? "blocked" : host.IsConnected(account) ? "connected now"
            : account.LastSeen is { } seen ? $"last here {seen.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}" : "not connected yet");

    /// <summary>"This is me…" (a code to type there) while it's connected and a guest; "Not me" once it's you.</summary>
    public string SelfText => account.Self ? "Not me" : host.SelfCodeFor(account) != null ? "Cancel code" : "This is me…";
    public bool CanSelf => account.Self || (host.IsConnected(account) && !account.Blocked);
    /// <summary>You on another computer act as an editor: its role only counts again after "Not me".</summary>
    public bool IsGuest => !account.Self;
    public string SelfToolTip => account.Self ? "It's a guest again, under its own name" : "It's you, on another computer: type the code shown here in its ProJict";
    public IRelayCommand ToggleSelf => new RelayCommand(() =>
    {
        if (account.Self) host.EndSelf(account);
        else if (host.SelfCodeFor(account) != null) host.CancelSelf(account);
        else host.StartSelf(account);
    });
    public IReadOnlyList<GuestRole> Roles { get; } = Enum.GetValues<GuestRole>();

    /// <summary>Changing it reconnects them with the new role.</summary>
    public GuestRole Role
    {
        get => account.Role;
        set
        {
            if (value != account.Role) host.Update(account, value, account.Groups);
        }
    }

    public string BlockText => account.Blocked ? "Unblock" : "Block";
    public IRelayCommand ToggleBlock => new RelayCommand(() => host.SetBlocked(account, !account.Blocked));
    public IRelayCommand Remove => new RelayCommand(() => host.Remove(account));
}

/// <summary>A ProJict you connect to.</summary>
public sealed class HostRow(KnownHost host, HostConnection? connection, SharingViewModel owner)
{
    public KnownHost Host { get; } = host;
    public HostConnection? Connection { get; } = connection;
    public string Title => $"{Host.HostName ?? Host.Address}'s ProJict ({Host.Address})";
    public string StateText => Connection?.StateText ?? "Not connected";
    public string? Message => Connection?.Message;
    public bool IsActive => Connection is { State: not ConnectionState.Disconnected };

    public bool AutoConnect
    {
        get => Host.AutoConnect;
        set => owner.SetAutoConnect(Host, value);
    }

    /// <summary>Linked as you, your channels always go there; for someone else's ProJict, when you tick it.</summary>
    public bool IsSelf => Connection?.IsSelf == true;
    public string ShareChatBackText => $"Share my Open channels with {Host.HostName ?? "them"}";

    public bool ShareChatBack
    {
        get => Host.ShareChatBack;
        set => owner.SetShareChatBack(Host, Connection, value);
    }

    public IRelayCommand Disconnect => new RelayCommand(() => owner.Disconnect(this, forget: false));
    public IAsyncRelayCommand Reconnect => new AsyncRelayCommand(() => owner.Reconnect(this));
    public IRelayCommand Forget => new RelayCommand(() => owner.Disconnect(this, forget: true));
}
