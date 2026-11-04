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
        _newToolSet = ToolSetChoices[0];
        NewTools = DefaultToolChoices();
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

    /// <summary>Which tools a token's agent is offered: it sees only those, so it isn't shown ones it would be refused.</summary>
    public IReadOnlyList<ToolSetChoice> ToolSetChoices { get; } =
    [
        new(ApiToolSet.Agent, "All an agent needs", "Everything an agent does here, except clean-ups, shipping and linking local files, which need this computer."),
        new(ApiToolSet.ChatOnly, "Chat only", "Channels and private messages, nothing else."),
        new(ApiToolSet.Custom, "Only the tools I tick", "Tick them below. Picking a name and saying what it's doing are always allowed."),
        new(ApiToolSet.You, "You, on your phone",
            "ProJict on your phone, acting as you, not as an agent: it answers what agents ask, approves ship plans, accepts or sends back finished work, " +
            "and chats and adds items as you (at " + ApiRoutes.Base + "/me). It reaches all your projects. Pair the phone with the QR code shown when it's made."),
    ];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNewCustom))] private ToolSetChoice? _newToolSet;
    public bool IsNewCustom => NewToolSet?.Set == ApiToolSet.Custom;
    /// <summary>For a new token with ticked tools: what an agent gets, to start from.</summary>
    [ObservableProperty] private IReadOnlyList<ToolChoice> _newTools;

    /// <summary>The tools a token can be given, ticked as for an agent (not the ones that need this computer).</summary>
    private List<ToolChoice> DefaultToolChoices() =>
        [.. Server.TickableTools.Select(t => new ToolChoice(t.Name, t.Title, !ApiServer.ApiToolSets.NeedThisComputer.Contains(t.Name)))];

    internal IReadOnlyList<(string Name, string Title)> TickableTools => Server.TickableTools;
    internal static IEnumerable<string> AgentTools(IEnumerable<(string Name, string Title)> tools) =>
        tools.Select(t => t.Name).Where(n => !ApiServer.ApiToolSets.NeedThisComputer.Contains(n));
    [ObservableProperty] private bool _allProjects = true;
    [ObservableProperty] private ExpiryChoice? _newExpiry;
    [ObservableProperty] private string? _newError;

    /// <summary>A secret just made or renewed, shown once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecret), nameof(SecretTitle), nameof(SetupText), nameof(HasSetup), nameof(IsPhoneSecret), nameof(IsAgentSecret),
        nameof(PairingImage), nameof(PairingText))]
    private string? _secret;
    private string _secretFor = "";
    /// <summary>The secret just shown is a "You, on your phone" token's: it's paired with a QR code, not set up in an agent.</summary>
    private bool _secretIsPhone;

    public bool HasSecret => Secret != null;
    public string SecretTitle => _secretIsPhone
        ? $"Pair your phone with “{_secretFor}” now: ProJict keeps only a fingerprint of the token and can't show it again."
        : $"Copy the token for “{_secretFor}” now: ProJict keeps only a fingerprint of it and can't show it again.";
    public bool IsPhoneSecret => Secret != null && _secretIsPhone;
    public bool IsAgentSecret => Secret != null && !_secretIsPhone;

    // ───────────── Pairing your phone ─────────────

    /// <summary>
    /// The QR code ProJict on your phone scans: this computer's addresses (Tailscale's first), the token and the
    /// certificate's fingerprint. Only while the API serves the network over HTTPS: the phone reaches it no other way.
    /// </summary>
    public System.Windows.Media.ImageSource? PairingImage => IsPhoneSecret && Server is { IsRunning: true, Https: true } && PairingAddresses().Count > 0
        ? PairingCode.Draw(ApiPairing.Payload(_secretFor, Secret!, PairingAddresses(), Server.Port, Server.Fingerprint))
        : null;

    /// <summary>Where the phone can find this computer, best first; tests set it.</summary>
    public Func<IReadOnlyList<System.Net.IPAddress>> PairingAddresses { get; set; } = ApiPairing.Addresses;

    public string PairingText
    {
        get
        {
            if (!IsPhoneSecret) return "";
            if (Server is not { IsRunning: true, Https: true })
                return "Your phone reaches ProJict over the network: tick “Let other devices on the network connect” above, then renew this token to get its QR code.";
            var addresses = PairingAddresses();
            if (addresses.Count == 0) return "This computer has no network address a phone could reach. Connect it to your network (or Tailscale), then renew this token.";
            return "Scan it in ProJict on your phone: it pairs with this computer only, and tries " +
                   string.Join(", ", addresses.Select(a => ApiPairing.Url(a, Server.Port))) + ". " +
                   (addresses.Any(ApiPairing.IsPrivateVpn)
                       ? "The first is Tailscale's, so it works away from home too."
                       : "Away from home, put Tailscale on this computer and your phone, then renew this token: its address comes first.");
        }
    }

    // ───────────── Connecting an agent ─────────────

    /// <summary>The agent programs a token's setup is shown for.</summary>
    public IReadOnlyList<string> SetupClients { get; } = ["Claude Code", "Cursor", "VS Code", "Another MCP client"];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SetupText))] private string _setupClient = "Claude Code";
    /// <summary>The token whose setup is shown from its row ("Connect an agent…"); its secret isn't known then.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSetup), nameof(SetupTitle), nameof(SetupText))] private string? _setupFor;
    public bool HasSetup => SetupFor != null && Secret == null;
    public string SetupTitle => $"Connect an agent with “{SetupFor}”: put its token where it says <token>. ProJict shows a token only when it's made or renewed.";
    /// <summary>The setup for the chosen program, with the secret just made, or a place for it.</summary>
    public string SetupText => Setup(SetupClient, Secret ?? "<token>");

    internal void ShowSetup(TokenRow row)
    {
        Secret = null;
        SetupFor = row.Name;
    }

    [RelayCommand] private void CloseSetup() => SetupFor = null;

    private string BaseUrl => Server.IsRunning ? Server.Https ? Server.NetworkUrls()[0] : Server.LocalUrl : $"http://127.0.0.1:{Data.Port}";

    internal string Setup(string client, string token)
    {
        var mcp = $"{BaseUrl}/mcp";
        var https = BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            ? "\n\nOver your network it's HTTPS with ProJict's own certificate: save it here (Save certificate…) and trust it on the agent's computer." : "";
        return client switch
        {
            "Claude Code" => $"claude mcp add --transport http projict {mcp} --header \"Authorization: Bearer {token}\"" + https,
            "Cursor" => "In ~/.cursor/mcp.json (or .cursor/mcp.json in a project):\n" +
                        $"{{\n  \"mcpServers\": {{\n    \"projict\": {{\n      \"url\": \"{mcp}\",\n      \"headers\": {{ \"Authorization\": \"Bearer {token}\" }}\n    }}\n  }}\n}}" + https,
            "VS Code" => "In .vscode/mcp.json:\n" +
                         $"{{\n  \"servers\": {{\n    \"projict\": {{\n      \"type\": \"http\",\n      \"url\": \"{mcp}\",\n      \"headers\": {{ \"Authorization\": \"Bearer {token}\" }}\n    }}\n  }}\n}}" + https,
            _ => $"MCP (Streamable HTTP): {mcp}\nHeader: Authorization: Bearer {token}\n\n" +
                 $"A client that can only start a command (stdio) can go through mcp-remote:\nnpx -y mcp-remote {mcp} --header \"Authorization: Bearer {token}\"\n\n" +
                 $"Or the REST API, described at {BaseUrl}{ApiRoutes.Base}/openapi.json:\ncurl -H \"Authorization: Bearer {token}\" {BaseUrl}{ApiRoutes.Base}/projects" + https,
        };
    }

    /// <summary>Copies the setup shown; false when the clipboard is busy.</summary>
    public bool CopySetup()
    {
        try
        {
            System.Windows.Clipboard.SetText(SetupText);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    partial void OnNewNameChanged(string value) => NewError = null;

    [RelayCommand]
    private void CreateToken()
    {
        var projects = AllProjects ? null : ProjectChoices.Where(c => c.IsChecked).Select(c => c.Project.Id).ToList();
        try
        {
            var phone = NewToolSet?.Set == ApiToolSet.You;
            // Your phone acts as you, in all your projects.
            var secret = Server.CreateToken(NewName, NewAccess?.Access ?? ApiAccess.ReadWrite, phone ? null : projects, NewExpiry?.Days ?? ApiData.DefaultExpiryDays,
                NewToolSet?.Set ?? ApiToolSet.Agent, NewTools.Where(t => t.IsChecked).Select(t => t.Name).ToList());
            _secretFor = NewName.Trim();
            _secretIsPhone = phone;
            SetupFor = null;
            Secret = secret;
            NewName = "";
            NewError = null;
            foreach (var choice in ProjectChoices) choice.IsChecked = false;
            AllProjects = true;
            NewToolSet = ToolSetChoices[0];
            NewTools = DefaultToolChoices();
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
        _secretIsPhone = row.Token.ActsAsYou;
        Secret = Server.RenewToken(row.Token);
    }

    internal void Revoke(TokenRow row)
    {
        Server.RevokeToken(row.Token);
        if (_secretFor == row.Token.Name) Secret = null;
    }

    internal void SetAccess(TokenRow row, ApiAccess access) => Server.SetTokenAccess(row.Token, access);
    internal void SetTools(TokenRow row, ApiToolSet set, IReadOnlyCollection<string>? custom) => Server.SetTokenTools(row.Token, set, custom);
    internal void SetFrozen(TokenRow row, bool frozen) => Server.SetTokenFrozen(row.Token, frozen);
}

public sealed record AccessChoice(ApiAccess Access, string Label);

public sealed record ToolSetChoice(ApiToolSet Set, string Label, string Description);

/// <summary>A tool to tick for a token that offers only the ones you choose.</summary>
public sealed partial class ToolChoice(string name, string title, bool isChecked, Action? changed = null) : ObservableObject
{
    public string Name => name;
    public string Title => title;
    public string ToolTip => name;
    [ObservableProperty] private bool _isChecked = isChecked;
    partial void OnIsCheckedChanged(bool value) => changed?.Invoke();
}

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
        : $"{(Token.ReadOnly ? owner.ReadOnlyLabel : "Read and write")} · {SelectedToolSet.Label} · {projects}") +
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

    /// <summary>Which tools its agent is offered, changeable at any time; it applies from the token's next request.</summary>
    public IReadOnlyList<ToolSetChoice> ToolSetChoices => owner.ToolSetChoices;
    public ToolSetChoice SelectedToolSet
    {
        get => ToolSetChoices.First(c => c.Set == Token.ToolSet);
        set
        {
            if (value == null || value.Set == Token.ToolSet) return;
            // Ticking starts from what an agent gets.
            owner.SetTools(this, value.Set, value.Set == ApiToolSet.Custom ? ApiViewModel.AgentTools(owner.TickableTools).ToList() : null);
        }
    }
    public bool IsCustom => Token.ToolSet == ApiToolSet.Custom;
    private IReadOnlyList<ToolChoice>? _customTools;
    /// <summary>The tools ticked for it (only with "Only the tools I tick").</summary>
    public IReadOnlyList<ToolChoice> CustomTools => _customTools ??= owner.TickableTools
        .Select(t => new ToolChoice(t.Name, t.Title, Token.CustomTools?.Contains(t.Name) == true, SaveTicks)).ToList();
    public string CustomToolsTitle => $"Its tools ({Token.CustomTools?.Count ?? 0} ticked)";

    private void SaveTicks() => owner.SetTools(this, ApiToolSet.Custom, CustomTools.Where(t => t.IsChecked).Select(t => t.Name).ToList());

    /// <summary>Freezing pauses the token without losing it: whatever it asks is refused until it's unfrozen.</summary>
    public bool IsFrozen => Token.Frozen;
    public string FreezeLabel => Token.Frozen ? "Unfreeze" : "Freeze";
    public string FreezeToolTip => Token.Frozen
        ? "Let it work again, with the access it has"
        : "Pause it: it still connects, but can't do anything, not even read, until you unfreeze it";
    [RelayCommand] private void ToggleFreeze() => owner.SetFrozen(this, !Token.Frozen);
    /// <summary>Shows how to set an agent up with this token (with a place for its secret).</summary>
    [RelayCommand] private void ConnectAgent() => owner.ShowSetup(this);

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
