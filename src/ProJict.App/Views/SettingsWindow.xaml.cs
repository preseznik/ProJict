using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;
using ProJict.Core.Export;
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
        StaleDays.Text = s.StaleDays.ToString();
        Groups.Text = string.Join(", ", s.Groups);
        VsCodePath.Text = s.VsCodePath ?? "";
        CheckUpdates.IsChecked = state.AppSettings.CheckForUpdates;
        AllowAgents.IsChecked = state.AppSettings.AllowAgents;
        AgentsUseChat.IsChecked = state.AppSettings.AgentsUseChat;
        ReadOnlyTokensUseChat.IsChecked = state.AppSettings.ReadOnlyTokensUseChat;
        foreach (var box in AgentPriorities.Children.OfType<CheckBox>())
            box.IsChecked = s.AgentPriorities.Contains(Enum.Parse<Priority>((string)box.Tag));
        UserName.Text = state.UserName;
        CommentFileLimit.Text = s.CommentFileLimitMb.ToString();
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
    }

    // ───────────── Sections ─────────────

    public IEnumerable<Expander> AllSections => Sections.Children.OfType<Expander>();

    private void RefreshSummaries()
    {
        if (UpdatesSummary == null) return; // still loading
        var priorities = AgentPriorities.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => Enum.Parse<Priority>((string)b.Tag)).ToList();
        AgentsSummary.Text = AllowAgents.IsChecked != true ? "Off: agents can't connect"
            : "On · they may work on " + (AgentAccess.Describe(priorities) ?? "no items (they can still read and comment)");
        ApiSummary.Text = ApiStatus.Text;
        var kinds = NotifyKinds.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        NotificationsSummary.Text = NotifyEnabled.IsChecked != true ? "Off" : kinds.Count == 0 ? "On, but nothing is chosen" : "On · " + string.Join(", ", kinds);
        BoardSummary.Text = $"{(Theme.SelectedItem as ComboBoxItem)?.Content} theme · stale after {StaleDays.Text.Trim()} days" +
                            (MarkInProgress.IsChecked == true ? " · copying a prompt marks items in progress" : "");
        CommentsSummary.Text = "You sign as " + (string.IsNullOrWhiteSpace(UserName.Text) ? Environment.UserName : UserName.Text.Trim()) +
                               $" · files in comments up to {CommentFileLimit.Text.Trim()} MB";
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
    /// The connected agents still on the old ProJict.exe --mcp, which an update of ProJict cuts off, with what to do; null
    /// when there are none (or no bridge to move to, in a development build).
    /// </summary>
    public static string? OldBridgeText(IEnumerable<ProJict.Core.Agents.McpSession> sessions)
    {
        var old = sessions.Where(s => s.OldBridge).Select(s => s.AgentName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (old.Count == 0 || BridgeInstaller.BridgePath == null) return null;
        return $"{string.Join(", ", old)} {(old.Count == 1 ? "reaches" : "reach")} ProJict through the old ProJict.exe --mcp, which a ProJict update cuts off. " +
               "Add ProJict to them again with the command above (it starts ProJictBridge.exe, which stays connected through updates and keeps their names), " +
               "then restart them or reconnect ProJict in them (/mcp in Claude Code).";
    }

    /// <summary>The Claude Code command (kept for checks that look for it).</summary>
    public static string McpRegistrationCommand(AppState state) => AgentSetups(state)[0].Text;

    private void AgentClient_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentClient.SelectedItem is not AgentSetup setup) return;
        McpCommand.Text = setup.Text;
        McpWhere.Text = setup.Where;
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
        if (!int.TryParse(CommentFileLimit.Text, out var fileLimit) || fileLimit is < 1 or > 100)
        {
            ShowError("Files in comments can be limited to 1 to 100 MB.", CommentsSection);
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
        var notify = _state.AppSettings.Notifications;
        notify.Enabled = NotifyEnabled.IsChecked == true;
        foreach (var box in NotifyKinds.Children.OfType<CheckBox>())
            typeof(NotificationSettings).GetProperty((string)box.Tag)!.SetValue(notify, box.IsChecked == true);
        s.Theme = ((ComboBoxItem)Theme.SelectedItem).Content.ToString()!;
        s.VsCodePath = string.IsNullOrWhiteSpace(VsCodePath.Text) ? null : VsCodePath.Text.Trim();
        var groups = Groups.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count > 0) s.Groups = groups;
        s.EnsureDefaultGroups();
        s.AgentPriorities = AgentPriorities.Children.OfType<CheckBox>().Where(b => b.IsChecked == true)
            .Select(b => Enum.Parse<Priority>((string)b.Tag)).ToList();

        App.ApplyTheme(s.Theme);
        _state.AppSettings.CheckForUpdates = CheckUpdates.IsChecked == true;
        _state.AppSettings.AllowAgents = AllowAgents.IsChecked == true;
        _state.AppSettings.AgentsUseChat = AgentsUseChat.IsChecked == true;
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
}
