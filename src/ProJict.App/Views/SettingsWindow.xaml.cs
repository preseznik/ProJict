using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;
using ProJict.Core.Export;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.Views;

/// <summary>
/// Settings, in folding sections: each shows a summary of what's set while folded, the ones you left open open again
/// next time, and "Find a setting" shows just the sections that mention what you type.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppState _state;
    /// <summary>The sections open before a search, put back when it's cleared.</summary>
    private HashSet<Expander>? _openBeforeFind;

    public SettingsWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        var s = state.Settings;
        DataRoot.Text = state.Store.DataRoot;
        MirrorEnabled.IsChecked = s.MirrorEnabled;
        MirrorFolder.Text = s.MirrorFolder ?? Path.Combine(Path.GetDirectoryName(state.Store.DataRoot) ?? "", "ProJict-mirror");
        Hotkey.Text = s.Hotkey;
        CloseToTray.IsChecked = s.CloseToTray;
        MarkInProgress.IsChecked = s.MarkCopiedInProgress;
        Theme.SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ShowColors(s);
        StaleDays.Text = s.StaleDays.ToString();
        Groups.Text = string.Join(", ", s.Groups);
        VsCodePath.Text = s.VsCodePath ?? "";
        CheckUpdates.IsChecked = state.AppSettings.CheckForUpdates;
        AllowAgents.IsChecked = state.AppSettings.AllowAgents;
        AgentsUseChat.IsChecked = state.AppSettings.AgentsUseChat;
        ApproveInChat.IsChecked = state.AppSettings.ApproveInChat;
        HookCommand.Text = App.Current.MainVm.AgentTools.PermissionHookCommand ?? "(it needs ProJictBridge.exe, which comes with ProJict's installer)";
        ShowClaudeHook();
        ReadOnlyTokensUseChat.IsChecked = state.AppSettings.ReadOnlyTokensUseChat;
        foreach (var box in AgentPriorities.Children.OfType<CheckBox>())
            box.IsChecked = s.AgentPriorities.Contains(Enum.Parse<Priority>((string)box.Tag));
        AgentInstructions.Text = s.AgentInstructions ?? "";
        UserName.Text = state.UserName;
        ShowYourPicture();
        CommentFileLimit.Text = s.CommentFileLimitMb.ToString();
        ChatFileLimit.Text = s.ChatFileLimitMb.ToString();
        PrivateFilesDays.Text = state.AppSettings.PrivateFilesDays.ToString();
        BigChannelFile.Text = state.AppSettings.BigChannelFileMb.ToString();
        ChatCacheMax.Text = state.AppSettings.ChatCacheMaxGb.ToString();
        ShowMicrophones(state.AppSettings.CallMicrophone);
        WindowsVoices.IsChecked = state.AppSettings.CallVoices == ProJict.Core.Chat.CallVoices.Windows;
        ElevenLabsVoices.IsChecked = state.AppSettings.CallVoices == ProJict.Core.Chat.CallVoices.ElevenLabs;
        ElevenLabsVoicesNote.Text = App.Current.MainVm.Calls.HasElevenLabsKey
            ? "With your ElevenLabs key (Settings › Tools): each agent gets one of your ElevenLabs voices. It uses its credits, a little per answer."
            : "It needs ElevenLabs' API key in Settings › Tools first; until then Windows' voices read them.";
        PrivateFilesFolder.Text = state.PrivateFilesRoot;
        ShowPrivateFiles();
        var notifications = state.AppSettings.Notifications;
        NotifyEnabled.IsChecked = notifications.Enabled;
        foreach (var box in NotifyKinds.Children.OfType<CheckBox>())
            box.IsChecked = notifications.Allows(Enum.Parse<NotificationKind>((string)box.Tag));
        var muted = state.Projects.Where(p => notifications.MutedProjects.Contains(p.Id)).Select(p => p.Name).ToList();
        MutedText.Text = muted.Count == 0 ? "" : $"Muted: {string.Join(", ", muted)} (unmute from the project's right-click menu).";
        MutedText.Visibility = muted.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AgentClient.ItemsSource = AgentSetups(state);
        AgentClient.SelectedIndex = 0;
        if (OldBridgeText(App.Current.MainVm.Agents.Sessions) is { } old)
        {
            OldBridgeNote.Text = old;
            OldBridgeNote.Visibility = Visibility.Visible;
            ConnectAgent.IsExpanded = true;
        }
        GitHubAccount.Text = s.GitHubAccount ?? "";
        UseGitHubCli.IsChecked = state.AppSettings.UseGitHubCliSignIn;
        VersionText.Text = $"ProJict {UpdateService.CurrentVersionText}";
        UpdatePanel.DataContext = App.Current.MainVm.Updates;
        ApiStatus.Text = ApiViewModel.StatusOf(App.Current.MainVm.Api);

        foreach (var section in AllSections)
            section.IsExpanded = state.AppSettings.OpenSettingsSections?.Contains((string)section.Tag) == true;
        if (App.Current.MainVm.Updates.IsAvailable) UpdatesSection.IsExpanded = true;
        // The summaries follow what you tick, type and choose.
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => RefreshSummaries()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => RefreshSummaries()));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RefreshSummaries()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => RefreshSummaries()));
        RefreshSummaries();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !Hotkey.IsKeyboardFocused)
            {
                Find.Focus();
                e.Handled = true;
            }
        };
        // Opening a section grows the window: keep its bottom (Save, Cancel) on the screen.
        SizeChanged += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        };
        Closed += (_, _) => RememberSections();
        // Colors show as you pick them; closed without saving, the saved ones come back.
        Closed += (_, _) =>
        {
            if (_lookPreviewed && DialogResult != true) App.ApplyLook(_state.Settings);
        };
        _lookReady = true;
    }

    // ───────────── Microphone for calls (#b39e8f23) ─────────────

    /// <summary>Windows' default first, then each microphone Windows has; one picked earlier that isn't there now stays, marked so.</summary>
    private void ShowMicrophones(string? picked)
    {
        CallMicrophone.Items.Add(new ComboBoxItem { Content = Microphones.DefaultName, Tag = null });
        var names = Microphones.List();
        foreach (var name in names) CallMicrophone.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        if (picked != null && !names.Contains(picked, StringComparer.OrdinalIgnoreCase))
            CallMicrophone.Items.Add(new ComboBoxItem { Content = picked + " (not connected now)", Tag = picked });
        CallMicrophone.SelectedItem = CallMicrophone.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, picked, StringComparison.OrdinalIgnoreCase))
                                      ?? CallMicrophone.Items[0];
    }

    // ───────────── Colors (#ed53e5d4) ─────────────

    /// <summary>Your own colors as you pick them (saved with Save).</summary>
    private CustomColors _customDraft = new();
    private readonly Dictionary<string, ColorField> _customFields = [];
    private bool _lookReady;
    private bool _lookPreviewed;

    /// <summary>The Custom scheme's colors: what each one changes.</summary>
    private static readonly (string Property, string Label, string Hint)[] CustomColorRows =
    [
        (nameof(CustomColors.Accent), "Accent", "Buttons, links, selections, check boxes and toggles"),
        (nameof(CustomColors.Background), "Background", "Mixed into the windows' background and title bars"),
        (nameof(CustomColors.Notice), "Notice bars", "The bars above the board: an agent asking, a tool key, the browser"),
        (nameof(CustomColors.Warning), "Warnings", "Waiting counts, high priority, items to change"),
        (nameof(CustomColors.Error), "Errors", "Problems, urgent items and bugs"),
        (nameof(CustomColors.Success), "Success", "Active projects and new features"),
        (nameof(CustomColors.AgentActivity), "Agents at work", "Agents' marks in the Agents view, the Feed and the board"),
    ];

    private void ShowColors(WorkspaceSettings s)
    {
        foreach (var name in ColorScheme.Names)
        {
            var scheme = ColorScheme.For(name, s.CustomColors);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            // Its accent and its background, as swatches (Windows: your accent; Custom: what you picked).
            row.Children.Add(Swatch());
            row.Children.Add(Swatch());
            ShowSwatches(row, scheme);
            row.Children.Add(new TextBlock { Text = name, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            ColorSchemePicker.Items.Add(new ComboBoxItem { Content = row, Tag = name, ToolTip = scheme.Description });
        }
        var current = ColorScheme.For(s.ColorScheme, s.CustomColors).Name;
        ColorSchemePicker.SelectedItem = ColorSchemePicker.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == current);
        _customDraft = s.CustomColors?.Copy() ?? new CustomColors();
        var rows = (Grid)CustomColorsPanel.Children[1];
        for (var i = 0; i < CustomColorRows.Length; i++)
        {
            var (property, label, hint) = CustomColorRows[i];
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new TextBlock { Text = label, ToolTip = hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            var field = new ColorField { Label = label, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            field.Value = (string?)typeof(CustomColors).GetProperty(property)!.GetValue(_customDraft);
            field.Changed += (_, _) =>
            {
                typeof(CustomColors).GetProperty(property)!.SetValue(_customDraft, field.Value);
                PreviewLook();
            };
            Grid.SetRow(caption, i);
            Grid.SetRow(field, i);
            Grid.SetColumn(field, 1);
            rows.Children.Add(caption);
            rows.Children.Add(field);
            _customFields[property] = field;
        }
        CustomColorsPanel.Visibility = current == ColorScheme.CustomName ? Visibility.Visible : Visibility.Collapsed;

        static Border Swatch() => new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0) };
    }

    /// <summary>A scheme's swatches in the list: its accent (Windows: yours), and its background tint when it has one.</summary>
    private static void ShowSwatches(StackPanel row, ColorScheme scheme)
    {
        var accent = scheme.Accent is { } a ? Color.FromRgb(a.R, a.G, a.B) : SystemColors.AccentColor;
        ((Border)row.Children[0]).Background = new SolidColorBrush(accent);
        ((Border)row.Children[1]).Background = scheme.Tint is { } t ? new SolidColorBrush(Color.FromRgb(t.R, t.G, t.B)) : null;
    }

    private string ThemeName => (Theme.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System";

    private string SchemeName => (ColorSchemePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? ColorScheme.WindowsName;

    /// <summary>The theme or the colors changed: they show at once (Cancel puts the saved ones back).</summary>
    private void Look_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_lookReady) return;
        var custom = SchemeName == ColorScheme.CustomName;
        // Custom for the first time: it starts from the scheme you had picked, so you change what you want of it.
        if (custom && sender == ColorSchemePicker && IsEmpty(_customDraft) &&
            e.RemovedItems.OfType<ComboBoxItem>().FirstOrDefault()?.Tag is string previous)
        {
            var from = ColorScheme.For(previous, null);
            var accent = from.Accent ?? new Rgb(SystemColors.AccentColor.R, SystemColors.AccentColor.G, SystemColors.AccentColor.B);
            _customDraft.Accent = accent.ToString();
            _customDraft.Background = from.Tint?.ToString();
            foreach (var (property, field) in _customFields) field.Value = (string?)typeof(CustomColors).GetProperty(property)!.GetValue(_customDraft);
        }
        CustomColorsPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        PreviewLook();
    }

    private static bool IsEmpty(CustomColors c) => CustomColorRows.All(r => typeof(CustomColors).GetProperty(r.Property)!.GetValue(c) == null);

    private void PreviewLook()
    {
        _lookPreviewed = true;
        Look.Apply(ThemeName, ColorScheme.For(SchemeName, _customDraft));
        // Custom's swatches follow what you pick.
        if (ColorSchemePicker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == ColorScheme.CustomName)?.Content is StackPanel row)
            ShowSwatches(row, ColorScheme.FromCustom(_customDraft));
    }

    // ───────────── Sections ─────────────

    public IEnumerable<Expander> AllSections => Sections.Children.OfType<Expander>();

    private void RefreshSummaries()
    {
        if (UpdatesSummary == null) return; // still loading
        var priorities = AgentPriorities.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => Enum.Parse<Priority>((string)b.Tag)).ToList();
        AgentsSummary.Text = AllowAgents.IsChecked != true ? "Off: agents can't connect"
            : "On · they may work on " + (AgentAccess.Describe(priorities) ?? "no items (they can still read and comment)") +
              (string.IsNullOrWhiteSpace(AgentInstructions.Text) ? "" : " · with your instructions");
        ApiSummary.Text = ApiStatus.Text;
        ToolsSummary.Text = ToolsSummaryOf(App.Current.MainVm.Tools.Tools);
        var kinds = NotifyKinds.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        NotificationsSummary.Text = NotifyEnabled.IsChecked != true ? "Off" : kinds.Count == 0 ? "On, but nothing is chosen" : "On · " + string.Join(", ", kinds);
        BoardSummary.Text = $"{(Theme.SelectedItem as ComboBoxItem)?.Content} theme, {SchemeName} colors · stale after {StaleDays.Text.Trim()} days" +
                            (MarkInProgress.IsChecked == true ? " · copying a prompt marks items in progress" : "");
        CommentsSummary.Text = "You sign as " + (string.IsNullOrWhiteSpace(UserName.Text) ? Environment.UserName : UserName.Text.Trim()) +
                               $" · files in comments up to {CommentFileLimit.Text.Trim()} MB";
        ChatSummary.Text = $"Files up to {ChatFileLimit.Text.Trim()} MB · the cache cleans out files " +
                           (int.TryParse(PrivateFilesDays.Text, out var days) && days > 0 ? $"unopened for {days} day{(days == 1 ? "" : "s")}" : "never") +
                           (int.TryParse(ChatCacheMax.Text, out var gb) && gb > 0 ? $", under {gb} GB" : "");
        CaptureSummary.Text = (string.IsNullOrEmpty(Hotkey.Text) ? "No hotkey" : "Hotkey " + Hotkey.Text) + " · " +
                              (CloseToTray.IsChecked == true ? "closing keeps it in the tray" : "closing quits");
        GitHubSummary.Text = string.IsNullOrWhiteSpace(GitHubAccount.Text) ? "No account"
            : GitHubAccount.Text.Trim() + (UseGitHubCli.IsChecked == true ? " · with your gh sign-in" : "");
        DataSummary.Text = DataRoot.Text.Trim();
        MirrorSummary.Text = MirrorEnabled.IsChecked == true ? "On · " + MirrorFolder.Text.Trim() : "Off";
        var updates = App.Current.MainVm.Updates;
        UpdatesSummary.Text = (updates.Available is { } available ? $"ProJict {available.Version.ToString(3)} is available"
            : CheckUpdates.IsChecked == true ? "Checks automatically" : "Checks only when you ask") + $" · this is {UpdateService.CurrentVersionText}";
    }

    private void Find_TextChanged(object sender, TextChangedEventArgs e)
    {
        var terms = Find.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        FindHint.Visibility = Find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (terms.Length == 0)
        {
            foreach (var section in AllSections)
            {
                section.Visibility = Visibility.Visible;
                if (_openBeforeFind != null) section.IsExpanded = _openBeforeFind.Contains(section);
            }
            _openBeforeFind = null;
            NothingFound.Visibility = Visibility.Collapsed;
            return;
        }
        _openBeforeFind ??= AllSections.Where(s => s.IsExpanded).ToHashSet();
        var found = 0;
        foreach (var section in AllSections)
        {
            var text = SearchText(section);
            var match = terms.All(t => text.Contains(t, StringComparison.CurrentCultureIgnoreCase));
            section.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            section.IsExpanded = match;
            if (match) found++;
        }
        NothingFound.Text = $"No setting mentions “{Find.Text.Trim()}”.";
        NothingFound.Visibility = found == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Find_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Find.Text.Length == 0) return;
        Find.Text = "";
        e.Handled = true; // Esc clears the search first, then closes the window
    }

    /// <summary>Everything a section says: titles, labels, check boxes, choices, notes and tooltips.</summary>
    private static string SearchText(Expander section)
    {
        var text = new StringBuilder();
        void Collect(object node)
        {
            switch (node)
            {
                case string s:
                    text.Append(s).Append(' ');
                    return;
                case TextBlock t:
                    text.Append(t.Text).Append(' ');
                    break;
            }
            if (node is FrameworkElement { ToolTip: string tip }) text.Append(tip).Append(' ');
            if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Collect(child);
        }
        Collect(section);
        return text.ToString();
    }

    /// <summary>Shows a problem in the section it's about.</summary>
    private void ShowError(string message, Expander section)
    {
        Error.Text = message;
        Find.Text = "";
        section.IsExpanded = true;
        section.BringIntoView();
    }

    private void RememberSections()
    {
        _state.AppSettings.OpenSettingsSections = (_openBeforeFind ?? AllSections.Where(s => s.IsExpanded)).Select(s => (string)s.Tag).ToList();
        _state.SaveAppSettings();
    }

    private void OpenApi_Click(object sender, RoutedEventArgs e)
    {
        new ApiWindow(App.Current.MainVm) { Owner = this }.ShowDialog();
        ApiStatus.Text = ApiViewModel.StatusOf(App.Current.MainVm.Api);
        RefreshSummaries();
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        new ToolsWindow(App.Current.MainVm) { Owner = this }.ShowDialog();
        RefreshSummaries();
    }

    /// <summary>"3 tools · ElevenLabs (API, key set), Suno, Tripo3D".</summary>
    public static string ToolsSummaryOf(IReadOnlyList<ProJict.Core.Tools.ExternalTool> tools) =>
        tools.Count == 0 ? "None yet"
            : $"{tools.Count} {(tools.Count == 1 ? "tool" : "tools")} · " + string.Join(", ", tools.Select(t =>
                t.Access == ProJict.Core.Tools.ToolAccess.Api ? $"{t.Name} (API{(t.HasKey ? ", key set" : ", no key yet")})"
                : t.Access == ProJict.Core.Tools.ToolAccess.ComfyUI ? $"{t.Name} (ComfyUI)" : t.Name));

    /// <summary>How to add ProJict to one AI agent: what to run or paste, and where.</summary>
    public sealed record AgentSetup(string Client, string Text, string Where);

    /// <summary>
    /// Setup for each supported agent, all starting "ProJictBridge.exe --mcp" (plus this data folder when it isn't the
    /// remembered one): the bridge ProJict keeps outside its install folder, so its updates don't cut agents off (in a
    /// development build, ProJict.exe). Formats: Claude Code and Codex CLI commands, Codex's config.toml, OpenCode's
    /// opencode.json and the mcpServers JSON most other clients use.
    /// </summary>
    public static IReadOnlyList<AgentSetup> AgentSetups(AppState state)
    {
        var exe = BridgeInstaller.Command;
        var remembered = state.AppSettings.DataRoot is { } r ? Path.GetFullPath(r) : null;
        string[] args = string.Equals(remembered, state.Store.DataRoot, StringComparison.OrdinalIgnoreCase)
            ? ["--mcp"] : ["--mcp", "--data", state.Store.DataRoot];
        var cliArgs = string.Join(" ", args.Select(a => a.Contains(' ') || a.Contains('\\') ? $"\"{a}\"" : a));
        // TOML literal strings ('…') take Windows paths as they are.
        var tomlArgs = string.Join(", ", args.Select(a => $"'{a}'"));
        var pretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var openCode = new System.Text.Json.Nodes.JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["mcp"] = new System.Text.Json.Nodes.JsonObject
            {
                ["projict"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new System.Text.Json.Nodes.JsonArray([exe, .. args.Select(a => (System.Text.Json.Nodes.JsonNode)a)]),
                    ["enabled"] = true,
                },
            },
        }.ToJsonString(pretty);
        var mcpServers = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["projict"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = exe,
                    ["args"] = new System.Text.Json.Nodes.JsonArray(args.Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray()),
                },
            },
        }.ToJsonString(pretty);
        return
        [
            new("Claude Code", $"claude mcp add --scope user projict -- \"{exe}\" {cliArgs}", "Run this once in a terminal."),
            new("OpenAI Codex", $"codex mcp add projict -- \"{exe}\" {cliArgs}\n\n# or add to ~/.codex/config.toml:\n[mcp_servers.projict]\ncommand = '{exe}'\nargs = [{tomlArgs}]",
                "Run the command once in a terminal, or add the table to ~/.codex/config.toml (the CLI and the IDE extension share it)."),
            new("OpenCode", openCode, "Merge into ~/.config/opencode/opencode.json (for all projects) or opencode.json in a project folder."),
            new("Claude Desktop & other MCP clients", mcpServers, "Merge into the client's MCP settings, e.g. claude_desktop_config.json for Claude Desktop."),
        ];
    }

    /// <summary>
    /// The agents connected to this ProJict on this computer that still use the old ProJict.exe --mcp, which an update of
    /// ProJict cuts off, with what to do; null when there are none (or no bridge to move to, in a development build). It's
    /// worked out from who is connected right now, so nobody else sees it, and it goes once they've moved.
    /// </summary>
    public static string? OldBridgeText(IEnumerable<ProJict.Core.Agents.McpSession> sessions)
    {
        var old = sessions.Where(s => s.OldBridge).Select(s => s.AgentName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (old.Count == 0 || BridgeInstaller.BridgePath == null) return null;
        return $"On this computer, {string.Join(", ", old)} {(old.Count == 1 ? "reaches" : "reach")} ProJict through the old ProJict.exe --mcp, which a ProJict update " +
               "cuts off. Add ProJict to them again with the command above (it starts ProJictBridge.exe, which stays connected through updates and keeps their " +
               "names), then restart them or reconnect ProJict in them (/mcp in Claude Code). This note is only about your agents here, and goes once they've moved.";
    }

    /// <summary>The Claude Code command (kept for checks that look for it).</summary>
    public static string McpRegistrationCommand(AppState state) => AgentSetups(state)[0].Text;

    private void AgentClient_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentClient.SelectedItem is not AgentSetup setup) return;
        McpCommand.Text = setup.Text;
        McpWhere.Text = setup.Where;
    }

    /// <summary>Where Claude Code keeps its settings: your user folder (a test points it elsewhere).</summary>
    internal static Func<string> ClaudeHome = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private string ClaudeSettings => ProJict.Core.Agents.ClaudeHookSetup.SettingsPath(ClaudeHome());

    /// <summary>Whether Claude Code has ProJict's hook: the button sets it up or takes it out.</summary>
    private void ShowClaudeHook(string? said = null)
    {
        string? json = null;
        try { json = File.Exists(ClaudeSettings) ? File.ReadAllText(ClaudeSettings) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var setUp = ProJict.Core.Agents.ClaudeHookSetup.IsSetUp(json);
        ClaudeHook.Content = setUp ? "Remove from Claude Code" : "Set up in Claude Code";
        ClaudeHook.IsEnabled = App.Current.MainVm.AgentTools.PermissionHookCommand != null;
        ClaudeHookStatus.Text = said ?? (setUp ? "Set up in Claude Code." : "Not set up in Claude Code yet.");
    }

    private void ClaudeHook_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainVm.AgentTools.PermissionHookCommand is not { } command) return;
        try
        {
            var path = ClaudeSettings;
            var json = File.Exists(path) ? File.ReadAllText(path) : null;
            var setUp = ProJict.Core.Agents.ClaudeHookSetup.IsSetUp(json);
            var changed = setUp ? ProJict.Core.Agents.ClaudeHookSetup.Remove(json!) : ProJict.Core.Agents.ClaudeHookSetup.Add(json, command);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (json != null) File.WriteAllText(path + ".projict-backup", json);
            File.WriteAllText(path, changed);
            ShowClaudeHook(setUp ? "Taken out of Claude Code; start its sessions again." : "Set up in Claude Code: start its sessions again to use it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowClaudeHook(ex.Message);
        }
    }

    private void CopyHookCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(HookCommand.Text);
            _state.StatusText = "Copied the permission hook's command.";
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void CopyMcpCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(McpCommand.Text);
            _state.StatusText = $"Copied the setup for {(AgentClient.SelectedItem as AgentSetup)?.Client ?? "the agent"}.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNow.IsEnabled = false;
        UpdateResult.Text = "Checking…";
        var updates = App.Current.MainVm.Updates;
        var result = await updates.CheckAsync(manual: true);
        // A found update shows in the panel below, with its install button.
        UpdateResult.Text = updates.IsAvailable ? "" : result;
        CheckNow.IsEnabled = true;
    }

    private async void CheckReleases_Click(object sender, RoutedEventArgs e)
    {
        // Checking uses what is typed here, so keep it even if the window is then cancelled.
        ApplyGitHubSettings();
        _state.MarkSettingsDirty();
        _state.SaveAppSettings();
        CheckReleases.IsEnabled = false;
        ReleasesResult.Text = "Checking GitHub…";
        ReleasesResult.Text = await App.Current.MainVm.GitHub.RefreshAsync(force: true);
        CheckReleases.IsEnabled = true;
    }

    private void ApplyGitHubSettings()
    {
        _state.Settings.GitHubAccount = string.IsNullOrWhiteSpace(GitHubAccount.Text) ? null : GitHubAccount.Text.Trim();
        _state.AppSettings.UseGitHubCliSignIn = UseGitHubCli.IsChecked == true;
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            Error.Text = "Use at least one modifier (Ctrl, Alt, Shift or Win).";
            return;
        }
        Error.Text = "";
        Hotkey.Text = HotkeyService.Format(Keyboard.Modifiers, key);
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => Hotkey.Text = "";

    private void BrowseData_Click(object sender, RoutedEventArgs e) => BrowseFolder(DataRoot, "ProJict data folder");
    private void BrowseMirror_Click(object sender, RoutedEventArgs e) => BrowseFolder(MirrorFolder, "TXT mirror folder");
    private void BrowsePrivateFiles_Click(object sender, RoutedEventArgs e) => BrowseFolder(PrivateFilesFolder, "Folder for private conversations' files");

    private void OpenPrivateFiles_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_state.PrivateFilesRoot);
        ShellLauncher.OpenFolder(_state.PrivateFilesRoot);
    }

    /// <summary>How much the cache for private conversations' files holds.</summary>
    private void ShowPrivateFiles()
    {
        var (files, bytes) = PrivateFiles.Measure(_state.PrivateFilesRoot);
        PrivateFilesHolds.Text = files == 0 ? "It's empty." : $"It holds {files} file{(files == 1 ? "" : "s")}, {AttachmentViewModel.FormatSize(bytes)}.";
        CleanOutPrivate.IsEnabled = files > 0;
        CleanOutPrivate.Content = "Clean out now";
    }

    /// <summary>Clean out now: the first click asks again; the second deletes every file there.</summary>
    private void CleanOutPrivate_Click(object sender, RoutedEventArgs e)
    {
        if ((string)CleanOutPrivate.Content == "Clean out now")
        {
            CleanOutPrivate.Content = "Click again to delete them (not the ones you keep)";
            return;
        }
        var (files, bytes) = PrivateFiles.CleanOut(_state.PrivateFilesRoot, null, DateTime.Now);
        _state.StatusText = $"Cleaned out {files} file{(files == 1 ? "" : "s")} ({AttachmentViewModel.FormatSize(bytes)}) from the Chat cache.";
        ShowPrivateFiles();
    }

    private void BrowseFolder(TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void BrowseCode_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Code.exe|Code.exe|Programs|*.exe", Title = "VS Code executable" };
        if (dialog.ShowDialog(this) == true) VsCodePath.Text = dialog.FileName;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => ShellLauncher.OpenFolder(_state.Store.DataRoot);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(StaleDays.Text, out var stale) || stale < 0)
        {
            ShowError("Stale days must be a whole number (0 turns it off).", BoardSection);
            return;
        }
        if (!int.TryParse(CommentFileLimit.Text, out var fileLimit) || fileLimit is < 1 or > WorkspaceSettings.MaxFileLimitMb)
        {
            ShowError($"Files in comments can be limited to 1 to {WorkspaceSettings.MaxFileLimitMb} MB.", CommentsSection);
            return;
        }
        if (!int.TryParse(ChatFileLimit.Text, out var chatLimit) || chatLimit is < 1 or > WorkspaceSettings.MaxFileLimitMb)
        {
            ShowError($"Files in Chat can be limited to 1 to {WorkspaceSettings.MaxFileLimitMb} MB.", ChatSection);
            return;
        }
        if (!int.TryParse(PrivateFilesDays.Text, out var privateDays) || privateDays is < 0 or > PrivateFiles.MaxDays)
        {
            ShowError("The Chat cache keeps files for a number of days (0 keeps them).", ChatSection);
            return;
        }
        if (!int.TryParse(BigChannelFile.Text, out var bigChannelFile) || bigChannelFile is < 0 or > WorkspaceSettings.MaxFileLimitMb)
        {
            ShowError($"Big channel files can start from 1 to {WorkspaceSettings.MaxFileLimitMb} MB (0: none go to the cache).", ChatSection);
            return;
        }
        if (!int.TryParse(ChatCacheMax.Text, out var cacheMax) || cacheMax is < 0 or > 10000)
        {
            ShowError("Keep the Chat cache under a number of GB (0: no limit).", ChatSection);
            return;
        }
        if (string.IsNullOrWhiteSpace(PrivateFilesFolder.Text))
        {
            ShowError("Choose a folder for private conversations' files.", ChatSection);
            return;
        }
        var mirrorOn = MirrorEnabled.IsChecked == true;
        if (mirrorOn && string.IsNullOrWhiteSpace(MirrorFolder.Text))
        {
            ShowError("Choose a folder for the mirror.", MirrorSection);
            return;
        }

        var s = _state.Settings;
        var wasMirroring = s.MirrorEnabled && s.MirrorFolder == MirrorFolder.Text.Trim();
        s.MirrorEnabled = mirrorOn;
        s.MirrorFolder = string.IsNullOrWhiteSpace(MirrorFolder.Text) ? null : MirrorFolder.Text.Trim();
        s.CloseToTray = CloseToTray.IsChecked == true;
        s.MarkCopiedInProgress = MarkInProgress.IsChecked == true;
        s.StaleDays = stale;
        s.CommentFileLimitMb = fileLimit;
        s.ChatFileLimitMb = chatLimit;
        _state.AppSettings.PrivateFilesDays = privateDays;
        _state.AppSettings.BigChannelFileMb = bigChannelFile;
        _state.AppSettings.ChatCacheMaxGb = cacheMax;
        _state.AppSettings.CallVoices = ElevenLabsVoices.IsChecked == true ? ProJict.Core.Chat.CallVoices.ElevenLabs : ProJict.Core.Chat.CallVoices.Windows;
        _state.AppSettings.CallMicrophone = (CallMicrophone.SelectedItem as ComboBoxItem)?.Tag as string;
        var privateFolder = PrivateFilesFolder.Text.Trim();
        if (!string.Equals(privateFolder, _state.PrivateFilesRoot, StringComparison.OrdinalIgnoreCase))
            _state.MovePrivateFiles(string.Equals(privateFolder, _state.DefaultPrivateFilesRoot, StringComparison.OrdinalIgnoreCase) ? null : privateFolder);
        _state.CleanOutPrivateFiles();
        var notify = _state.AppSettings.Notifications;
        notify.Enabled = NotifyEnabled.IsChecked == true;
        foreach (var box in NotifyKinds.Children.OfType<CheckBox>())
            typeof(NotificationSettings).GetProperty((string)box.Tag)!.SetValue(notify, box.IsChecked == true);
        s.Theme = ((ComboBoxItem)Theme.SelectedItem).Content.ToString()!;
        s.ColorScheme = SchemeName;
        s.CustomColors = IsEmpty(_customDraft) ? null : _customDraft.Copy();
        s.VsCodePath = string.IsNullOrWhiteSpace(VsCodePath.Text) ? null : VsCodePath.Text.Trim();
        var groups = Groups.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count > 0) s.Groups = groups;
        s.EnsureDefaultGroups();
        s.AgentPriorities = AgentPriorities.Children.OfType<CheckBox>().Where(b => b.IsChecked == true)
            .Select(b => Enum.Parse<Priority>((string)b.Tag)).ToList();
        s.AgentInstructions = string.IsNullOrWhiteSpace(AgentInstructions.Text) ? null : AgentInstructions.Text.Replace("\r\n", "\n").Trim();

        App.ApplyLook(s);
        _state.AppSettings.CheckForUpdates = CheckUpdates.IsChecked == true;
        _state.AppSettings.AllowAgents = AllowAgents.IsChecked == true;
        _state.AppSettings.AgentsUseChat = AgentsUseChat.IsChecked == true;
        _state.AppSettings.ApproveInChat = ApproveInChat.IsChecked == true;
        _state.AppSettings.ReadOnlyTokensUseChat = ReadOnlyTokensUseChat.IsChecked == true;
        _state.AppSettings.UserName = string.IsNullOrWhiteSpace(UserName.Text) || UserName.Text.Trim() == Environment.UserName ? null : UserName.Text.Trim();
        App.Current.SetAgentAccess(_state.AppSettings.AllowAgents);
        ApplyGitHubSettings();
        _state.SaveAppSettings();

        if (Hotkey.Text != s.Hotkey || !string.IsNullOrEmpty(Hotkey.Text))
        {
            if (!App.Current.RegisterHotkey(Hotkey.Text, out var error))
            {
                ShowError(error ?? "That hotkey couldn't be registered.", CaptureSection);
                App.Current.RegisterHotkey(s.Hotkey, out _);
                return;
            }
            s.Hotkey = Hotkey.Text;
        }

        _state.MarkSettingsDirty();
        _state.NotifyListChanged();

        if (mirrorOn && !wasMirroring)
        {
            _state.SaveNow();
            var result = ExportService.Export(_state.Projects, s.MirrorFolder!, s.Export, overwriteConflicts: false);
            _state.StatusText = $"Mirror: {result.Written.Count} written" +
                                (result.Conflicts.Count > 0 ? $", {result.Conflicts.Count} skipped (files not written by ProJict)" : "");
        }

        var newRoot = DataRoot.Text.Trim();
        if (!string.Equals(Path.GetFullPath(newRoot), _state.Store.DataRoot, StringComparison.OrdinalIgnoreCase))
        {
            _state.AppSettings.DataRoot = newRoot;
            _state.SaveAppSettings();
            MessageBox.Show(this, "Restart ProJict to use the new data folder.", "Data folder changed", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DialogResult = true;
    }

    // ───────────── Your picture ─────────────

    /// <summary>Yours, beside your messages and comments (kept like the agents' pictures, by your name).</summary>
    private void ShowYourPicture()
    {
        var picture = _state.Pictures.Of(_state.UserName);
        YourPictureBrush.ImageSource = picture;
        YourPictureImage.Visibility = RemovePicture.Visibility = picture != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChoosePicture_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Icons.Pick("Your picture") is not { } file) return;
        try
        {
            _state.Pictures.Set(_state.UserName, file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            _state.StatusText = ex.Message;
            return;
        }
        _state.StatusText = "Your picture shows beside your messages and comments.";
        ShowYourPicture();
    }

    private void RemovePicture_Click(object sender, RoutedEventArgs e)
    {
        _state.Pictures.Remove(_state.UserName);
        _state.StatusText = "Your initials show beside your messages again.";
        ShowYourPicture();
    }
}
