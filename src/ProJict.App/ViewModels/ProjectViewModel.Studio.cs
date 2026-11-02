using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project's game studio in Details: setting one up, and its roles with what each expects (what an agent is told
/// when hired). Hiring itself happens in the Agents view's Game studio. Like shipping, it's not part of undo.
/// </summary>
public sealed partial class ProjectViewModel
{
    public bool CanHaveStudio => !Project.IsInbox && Project.Remote == null;
    public bool HasStudio => Project.Studio != null;

    public IReadOnlyList<StudioRoleRow> StudioRoles =>
        Project.Studio is { } studio ? studio.Roles.Select(r => new StudioRoleRow(r, studio, Touch)).ToList() : [];

    public string StudioHint => HasStudio
        ? $"A {StudioFlow.KindName(Project.Studio!.Kind)}. Each role and what it expects: an agent is told this when you hire it (right-click its robot in the " +
          "Agents view), and current_project shows the latest."
        : "The project's room in the Agents view, with a station per role. A game studio has lead and game designers, a level designer, programmer, artist, " +
          "sound engineer, QA and producer; a film studio a director, writer, capture, editor, sound, thumbnail & graphics, publisher and producer. Agents " +
          "working here wait in its lobby until you hire them to a role, and are told what it expects.";

    /// <summary>Set up a studio: "Game" or "Film".</summary>
    [RelayCommand]
    private void SetUpStudio(string? kind)
    {
        if (!CanHaveStudio || HasStudio) return;
        Project.Studio = StudioFlow.New(Enum.TryParse<StudioKind>(kind, out var k) ? k : StudioKind.Game);
        try { StudioFlow.ChannelFor(Project, Project.Studio, _state.Chat, _state.ChatMe); }
        catch (ProJict.Core.Chat.ChatException) { }
        Touch();
        RefreshStudio();
        _state.StatusText = $"{Project.Name} has a {StudioFlow.KindName(Project.Studio.Kind)}: it's the project's room in the Agents view, where you hire the agents " +
                            "working here to its roles.";
    }

    /// <summary>Remove the studio was clicked once: the button asks to be clicked again.</summary>
    [ObservableProperty] private bool _confirmRemoveStudio;
    public string RemoveStudioText => ConfirmRemoveStudio ? "Click again to remove it, with its roles and hires" : "Remove the studio…";

    partial void OnConfirmRemoveStudioChanged(bool value) => OnPropertyChanged(nameof(RemoveStudioText));

    /// <summary>Back to the office for this project; the roles and who had them go too.</summary>
    [RelayCommand]
    private void RemoveStudio()
    {
        if (Project.Studio == null) return;
        if (!ConfirmRemoveStudio)
        {
            ConfirmRemoveStudio = true;
            return;
        }
        ConfirmRemoveStudio = false;
        Project.Studio = null;
        Touch();
        RefreshStudio();
        Main.AgentActivity.RefreshStudioView();
        _state.StatusText = $"{Project.Name} has no studio any more: the Agents view shows its agents in the office.";
    }

    // ───────────── The lead reviews wiki pages ─────────────

    /// <summary>"Lead designer", "Director".</summary>
    private string LeadRoleName => Project.Studio is { } studio && LeadReview.LeadRole(studio) is { } role ? role.Name : "Lead";

    public string LeadReviewsWikiText => $"The {LeadRoleName.ToLowerInvariant()} reviews wiki pages";

    /// <summary>Pages about the project that the studio's other agents change wait for the lead's review instead of yours.</summary>
    public bool LeadReviewsWiki
    {
        get => Project.Studio?.LeadReviewsWiki == true;
        set
        {
            if (Project.Studio is not { } studio || studio.LeadReviewsWiki == value) return;
            studio.LeadReviewsWiki = value;
            Touch();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeadReviewHint));
            Main.Wiki.RefreshPages();
        }
    }

    /// <summary>Only pages in this wiki folder (and below); empty: every page about the project.</summary>
    public string LeadReviewFolder
    {
        get => Project.Studio?.LeadReviewFolder ?? "";
        set
        {
            if (Project.Studio is not { } studio) return;
            var folder = WikiPaths.Normalize(value ?? "") is { Length: > 0 } f ? f : null;
            if (folder == studio.LeadReviewFolder) return;
            studio.LeadReviewFolder = folder;
            Touch();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeadReviewHint));
            Main.Wiki.RefreshPages();
        }
    }

    public IReadOnlyList<string> WikiFolders => _state.Wiki.Folders();

    public string LeadReviewHint
    {
        get
        {
            var lead = Project.Studio is { } studio ? LeadReview.LeadOf(studio) : null;
            var who = lead ?? $"the {LeadRoleName} (nobody's hired yet)";
            var pages = Project.Studio?.LeadReviewFolder is { } folder ? $"Pages in {WikiPaths.Display(folder)}" : $"Pages about {Project.Name}";
            return LeadReviewsWiki
                ? $"{pages} that the studio's other agents change wait for {who}'s review instead of yours: accepted, or sent back with a note. Their history " +
                  "says who accepted them, and you can reopen one. The lead's own changes, clashing edits and other pages still wait for you."
                : $"Off: you review every wiki page agents change. On: {pages.ToLowerInvariant()[..1]}{pages[1..]} that the studio's other agents change wait " +
                  $"for {who} instead.";
        }
    }

    public void RefreshStudio()
    {
        foreach (var p in new[]
                 {
                     nameof(CanHaveStudio), nameof(HasStudio), nameof(StudioRoles), nameof(StudioHint), nameof(LeadReviewsWikiText), nameof(LeadReviewsWiki),
                     nameof(LeadReviewFolder), nameof(LeadReviewHint), nameof(WikiFolders),
                 })
            OnPropertyChanged(p);
    }
}

/// <summary>A role in Details: its name and what it expects, and who has it.</summary>
public sealed class StudioRoleRow(StudioRole role, Studio studio, Action changed) : ObservableObject
{
    public string Name
    {
        get => role.Name;
        set
        {
            var name = (value ?? "").Trim();
            if (name.Length == 0 || name == role.Name) return;
            role.Name = name;
            changed();
            OnPropertyChanged();
        }
    }

    public string Definition
    {
        get => role.Definition;
        set
        {
            var text = (value ?? "").Trim();
            if (text == role.Definition) return;
            role.Definition = text;
            changed();
            OnPropertyChanged();
        }
    }

    public string HolderText => StudioFlow.HolderOf(studio, role) is { } agent ? $"Hired: {agent}" : "Nobody hired yet";
}
