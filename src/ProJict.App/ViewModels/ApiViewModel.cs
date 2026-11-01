using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services.Api;
using ProJict.Core.Api;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.ViewModels;

/// <summary>The API window (Settings › API): turning the API on, where it listens, and the tokens.</summary>
public sealed partial class ApiViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ApiViewModel(MainViewModel main)
    {
        _main = main;
        ProjectChoices = main.State.Projects.Where(p => p.Remote == null)
            .Select(p => new ProjectChoice(p)).ToList();
        AccessChoices = [new(ApiAccess.ReadWrite, "Read and write"), new(ApiAccess.ReadOnly, ReadOnlyLabel)];
        _newAccess = AccessChoices[0];
        _newExpiry = ExpiryChoices.First(e => e.Days == ApiData.DefaultExpiryDays);
        Server.Changed += Refresh;
    }

    private ApiServer Server => _main.Api;
    private ApiData Data => Server.Data;

    /// <summary>The window closed: stop listening for changes.</summary>
    public void Detach() => Server.Changed -= Refresh;

    public void Refresh()
    {
        foreach (var p in new[]
                 {
                     nameof(IsOn), nameof(PortText), nameof(AllowNetwork), nameof(Status), nameof(IsRunning), nameof(Endpoints),
                     nameof(CertificateText), nameof(HasCertificate), nameof(Tokens), nameof(HasTokens), nameof(TokensTitle),
                 })
            OnPropertyChanged(p);
    }

    // ───────────── The server ─────────────

    public bool IsOn
    {
        get => Data.Enabled;
        set
        {
            if (value == Data.Enabled) return;
            lock (Server.Store.Sync) Data.Enabled = value;
            Server.Store.Save();
            _ = value ? Server.StartAsync() : Server.StopAsync();
            Refresh();
        }
    }

    public string PortText
    {
        get => Data.Port.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (!int.TryParse(value, out var port) || port is < 1024 or > 65535 || port == Data.Port) return;
            lock (Server.Store.Sync) Data.Port = port;
            Server.Store.Save();
            if (Server.IsRunning) _ = Server.RestartAsync();
            Refresh();
        }
    }

    public bool AllowNetwork
    {
        get => Data.AllowNetwork;
        set
        {
            if (value == Data.AllowNetwork) return;
            lock (Server.Store.Sync) Data.AllowNetwork = value;
            Server.Store.Save();
            if (Server.IsRunning) _ = Server.RestartAsync();
            Refresh();
        }
    }

    public bool IsRunning => Server.IsRunning;

    public string Status => StatusOf(Server);

    /// <summary>One line on the API's state, also for Settings.</summary>
    public static string StatusOf(ApiServer server) =>
        server.Error is { } error ? "Couldn't start the API: " + error
        : server.IsStarting ? "Starting…"
        : !server.IsRunning ? server.Data.Enabled ? "Not running." : "Off: nothing can connect."
        : server.Https ? $"On for your network, over HTTPS: {string.Join(" or ", server.NetworkUrls())}"
        : $"On, for this computer only: {server.LocalUrl}";

    public string Endpoints
    {
        get
        {
            if (!Server.IsRunning) return "";
            var url = Server.Https ? Server.NetworkUrls()[0] : Server.LocalUrl;
            return $"REST API  {url}{ApiRoutes.Base}\nOpenAPI   {url}{ApiRoutes.Base}/openapi.json\nMCP       {url}/mcp";
        }
    }

    public bool HasCertificate => Server.IsRunning && Server.Https;

    public string CertificateText => HasCertificate
        ? $"Its certificate is ProJict's own. Clients either trust it (Save certificate…) or check its fingerprint: {SharingData.ShortFingerprint(Server.Fingerprint)}"
        : "";

    /// <summary>Writes the HTTPS certificate (PEM, no private key) for clients to trust.</summary>
    public void SaveCertificate(string path)
    {
        if (Server.Certificate is { } certificate) System.IO.File.WriteAllText(path, certificate.ExportCertificatePem() + "\n");
    }

    // ───────────── Making a token ─────────────

    public List<ProjectChoice> ProjectChoices { get; }
    public IReadOnlyList<AccessChoice> AccessChoices { get; }

    /// <summary>"Read only", or "Read only, Chat allowed" while Settings › API lets read-only tokens use Chat.</summary>
    internal string ReadOnlyLabel => _main.State.AppSettings is { AgentsUseChat: true, ReadOnlyTokensUseChat: true } ? "Read only, Chat allowed" : "Read only";
    public IReadOnlyList<ExpiryChoice> ExpiryChoices { get; } =
        ApiData.ExpiryChoices.Select(d => new ExpiryChoice(d, ApiData.ExpiryText(d))).ToList();

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private AccessChoice? _newAccess;
    [ObservableProperty] private bool _allProjects = true;
    [ObservableProperty] private ExpiryChoice? _newExpiry;
    [ObservableProperty] private string? _newError;

    /// <summary>A secret just made or renewed, shown once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecret), nameof(SecretTitle), nameof(SecretExamples))]
    private string? _secret;
    private string _secretFor = "";

    public bool HasSecret => Secret != null;
    public string SecretTitle => $"Copy the token for “{_secretFor}” now: ProJict keeps only a fingerprint of it and can't show it again.";

    public string SecretExamples
    {
        get
        {
            if (Secret == null) return "";
            var url = Server.IsRunning ? Server.Https ? Server.NetworkUrls()[0] : Server.LocalUrl : $"http://127.0.0.1:{Data.Port}";
            return $"curl -H \"Authorization: Bearer {Secret}\" {url}{ApiRoutes.Base}/projects\n\n" +
                   $"claude mcp add --transport http projict {url}/mcp --header \"Authorization: Bearer {Secret}\"";
        }
    }

    partial void OnNewNameChanged(string value) => NewError = null;

    [RelayCommand]
    private void CreateToken()
    {
        var projects = AllProjects ? null : ProjectChoices.Where(c => c.IsChecked).Select(c => c.Project.Id).ToList();
        try
        {
            var secret = Server.CreateToken(NewName, NewAccess?.Access ?? ApiAccess.ReadWrite, projects, NewExpiry?.Days ?? ApiData.DefaultExpiryDays);
            _secretFor = NewName.Trim();
            Secret = secret;
            NewName = "";
            NewError = null;
            foreach (var choice in ProjectChoices) choice.IsChecked = false;
            AllProjects = true;
        }
        catch (ArgumentException ex)
        {
            NewError = ex.Message;
        }
    }

    [RelayCommand] private void DismissSecret() => Secret = null;

    /// <summary>Copies the secret; false when the clipboard is busy.</summary>
    public bool CopySecret()
    {
        if (Secret == null) return false;
        try
        {
            System.Windows.Clipboard.SetText(Secret);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ───────────── Tokens ─────────────

    public IReadOnlyList<TokenRow> Tokens
    {
        get
        {
            List<ApiToken> tokens;
            lock (Server.Store.Sync) tokens = [.. Data.Tokens];
            return tokens.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new TokenRow(t, this, ProjectNames(t))).ToList();
        }
    }

    public bool HasTokens => Data.Tokens.Count > 0;
    public string TokensTitle => $"Tokens ({Data.Tokens.Count})";

    private string ProjectNames(ApiToken token) => token.Projects == null ? "All projects"
        : string.Join(", ", _main.State.Projects.Where(p => token.Projects.Contains(p.Id)).Select(p => p.Name)) is { Length: > 0 } names ? names
        : "No projects (they were deleted)";

    internal void Renew(TokenRow row)
    {
        _secretFor = row.Token.Name;
        Secret = Server.RenewToken(row.Token);
    }

    internal void Revoke(TokenRow row)
    {
        Server.RevokeToken(row.Token);
        if (_secretFor == row.Token.Name) Secret = null;
    }

    internal void SetAccess(TokenRow row, ApiAccess access) => Server.SetTokenAccess(row.Token, access);
    internal void SetFrozen(TokenRow row, bool frozen) => Server.SetTokenFrozen(row.Token, frozen);
}

public sealed record AccessChoice(ApiAccess Access, string Label);

public sealed record ExpiryChoice(int Days, string Label);

/// <summary>A project to tick for a token limited to some projects.</summary>
public sealed partial class ProjectChoice(Project project) : ObservableObject
{
    public Project Project { get; } = project;
    public string Name => Project.Name;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>A token in the list. Renew and Revoke ask once more inline, since both stop the current secret working.</summary>
public sealed partial class TokenRow(ApiToken token, ApiViewModel owner, string projects) : ObservableObject
{
    public ApiToken Token { get; } = token;
    public string Name => Token.Name;
    public string Masked => Token.Masked;
    public string Details => (Token.Frozen
        ? $"Frozen: it connects, but can't do anything until you unfreeze it · {projects}"
        : $"{(Token.ReadOnly ? owner.ReadOnlyLabel : "Read and write")} · {projects}") +
        (Token.AgentName is { } agent ? $" · its agent goes by {agent}" : "");

    /// <summary>Its access, changeable at any time; it applies from the token's next request.</summary>
    public IReadOnlyList<AccessChoice> AccessChoices => owner.AccessChoices;
    public AccessChoice SelectedAccess
    {
        get => AccessChoices.First(c => c.Access == Token.Access);
        set
        {
            if (value != null && value.Access != Token.Access) owner.SetAccess(this, value.Access);
        }
    }

    /// <summary>Freezing pauses the token without losing it: whatever it asks is refused until it's unfrozen.</summary>
    public bool IsFrozen => Token.Frozen;
    public string FreezeLabel => Token.Frozen ? "Unfreeze" : "Freeze";
    public string FreezeToolTip => Token.Frozen
        ? "Let it work again, with the access it has"
        : "Pause it: it still connects, but can't do anything, not even read, until you unfreeze it";
    [RelayCommand] private void ToggleFreeze() => owner.SetFrozen(this, !Token.Frozen);

    public string ExpiresText
    {
        get
        {
            var now = DateTime.Now;
            if (Token.IsExpired(now)) return $"Expired on {Token.ExpiresAt:yyyy-MM-dd}";
            var days = (int)Math.Ceiling((Token.ExpiresAt - now).TotalDays);
            return $"Expires {Token.ExpiresAt:yyyy-MM-dd} (in {(days == 1 ? "1 day" : $"{days} days")})";
        }
    }

    /// <summary>Expired, or expiring within a week.</summary>
    public bool NeedsRenewal => DateTime.Now >= Token.ExpiresAt - ApiData.ExpiryNotice;

    public string LastUsedText => Token.LastUsedAt is { } at
        ? $"Last used {(at.Date == DateTime.Today ? "today" : at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))} {at:HH:mm} from {Token.LastUsedFrom}"
        : "Never used";

    /// <summary>"renew" or "revoke" while asking to confirm it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming), nameof(ConfirmText), nameof(ConfirmLabel))]
    private string? _pending;

    public bool IsConfirming => Pending != null;
    public string ConfirmText => Pending == "revoke"
        ? $"Revoke “{Name}”? It stops working right away."
        : $"Renew “{Name}”? You get a new token for {ApiData.ExpiryText(Token.Days)}, and the current one stops working right away.";
    public string ConfirmLabel => Pending == "revoke" ? "Revoke" : "Renew";

    [RelayCommand] private void AskRenew() => Pending = "renew";
    [RelayCommand] private void AskRevoke() => Pending = "revoke";
    [RelayCommand] private void Cancel() => Pending = null;

    [RelayCommand]
    private void Confirm()
    {
        if (Pending == "revoke") owner.Revoke(this);
        else if (Pending == "renew") owner.Renew(this);
        Pending = null;
    }
}
