using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project's studios in Details: setting up a game or film studio, opening or closing one, and each one's roles with
/// what each expects (what an agent is told when hired). Hiring itself happens in the Agents view. Like shipping, it's not
/// part of undo.
/// </summary>
public sealed partial class ProjectViewModel
{
    public bool CanHaveStudio => !Project.IsInbox && Project.Remote == null;
    public bool HasStudio => StudioFlow.StudiosOf(Project).Any();

    /// <summary>Each of the project's studios, open or closed: a game studio first (made again when they change).</summary>
    public IReadOnlyList<StudioDetails> Studios => _studios ??= [.. StudioFlow.StudiosOf(Project).OrderBy(s => s.Kind).Select(s => new StudioDetails(this, s))];
    private IReadOnlyList<StudioDetails>? _studios;

    public bool CanSetUpGameStudio => CanHaveStudio && StudioFlow.StudioOf(Project, StudioKind.Game) == null;
    public bool CanSetUpFilmStudio => CanHaveStudio && StudioFlow.StudioOf(Project, StudioKind.Film) == null;

    public string StudioHint => HasStudio
        ? "Each studio is one of the project's rooms in the Agents view, beside its office (which you can close there). Each role and what it expects: " +
          "an agent is told this when you hire it (right-click its robot in the Agents view), and current_project shows the latest."
        : "A room of the project's in the Agents view, with a station per role. A game studio has lead and game designers, a level designer, programmer, " +
          "artist, sound engineer, QA and producer; a film studio a director, writer, capture, editor, sound, thumbnail & graphics, publisher and producer. " +
          "A project can have both. Agents working here stay in the office until you hire them to a role, and are told what it expects.";

    /// <summary>Set up a studio: "Game" or "Film".</summary>
    [RelayCommand]
    private void SetUpStudio(string? kind)
    {
        var k = Enum.TryParse<StudioKind>(kind, out var parsed) ? parsed : StudioKind.Game;
        if (!CanHaveStudio || StudioFlow.StudioOf(Project, k) != null) return;
        var studio = StudioFlow.OpenStudio(Project, k);
        try { StudioFlow.ChannelFor(Project, studio, _state.Chat, _state.ChatMe); }
        catch (ProJict.Core.Chat.ChatException) { }
        StudiosChanged();
        _state.StatusText = $"{Project.Name} has a {StudioFlow.KindName(k)}: it's one of the project's rooms in the Agents view, where you hire the agents " +
                            "working here to its roles.";
    }

    private void StudiosChanged()
    {
        Touch();
        RefreshStudio();
        Main.AgentActivity.RefreshStudioView();
        Main.Wiki.RefreshPages();
    }

    public IReadOnlyList<string> WikiFolders => _state.Wiki.Folders();

    public void RefreshStudio()
    {
        _studios = null;
        foreach (var p in new[] { nameof(CanHaveStudio), nameof(HasStudio), nameof(Studios), nameof(CanSetUpGameStudio), nameof(CanSetUpFilmStudio), nameof(StudioHint), nameof(WikiFolders) })
            OnPropertyChanged(p);
    }

    /// <summary>One of the project's studios in Details: open or closed, the lead's wiki reviews, and its roles.</summary>
    public sealed partial class StudioDetails(ProjectViewModel owner, Studio studio) : ObservableObject
    {
        public Studio Studio => studio;
        private Project Project => owner.Project;
        private string KindName => StudioFlow.KindName(studio.Kind);

        public string Title => (studio.Kind == StudioKind.Film ? "Film studio" : "Game studio") + (studio.Closed ? " (closed)" : "");

        public IReadOnlyList<StudioRoleRow> Roles => [.. studio.Roles.Select(r => new StudioRoleRow(r, studio, owner.Touch))];

        public string OpenCloseText => studio.Closed ? "Open it" : "Close it";

        public string OpenCloseToolTip => studio.Closed
            ? "Make it one of the project's rooms in the Agents view again, with its roles and hires"
            : "It leaves the project's rooms in the Agents view; its roles and hires are kept for when you open it again";

        [RelayCommand]
        private void OpenClose()
        {
            if (studio.Closed) StudioFlow.OpenStudio(Project, studio.Kind);
            else StudioFlow.CloseStudio(Project, studio);
            owner.StudiosChanged();
            owner._state.StatusText = studio.Closed
                ? $"Closed {Project.Name}'s {KindName}: its roles and hires are kept for when you open it again."
                : $"{Project.Name}'s {KindName} is open again, with its roles and hires.";
        }

        /// <summary>Remove was clicked once: the button asks to be clicked again.</summary>
        [ObservableProperty] private bool _confirmRemove;
        public string RemoveText => ConfirmRemove ? "Click again to remove it, with its roles and hires" : "Remove…";

        partial void OnConfirmRemoveChanged(bool value) => OnPropertyChanged(nameof(RemoveText));

        /// <summary>Removes the studio for good: its roles and who had them go too.</summary>
        [RelayCommand]
        private void Remove()
        {
            if (!ConfirmRemove)
            {
                ConfirmRemove = true;
                return;
            }
            ConfirmRemove = false;
            StudioFlow.RemoveStudio(Project, studio);
            owner.StudiosChanged();
            owner._state.StatusText = $"{Project.Name} has no {KindName} any more" +
                                      (StudioFlow.OpenStudiosOf(Project).Any() ? "." : ": the Agents view shows its agents in the office.");
        }

        // ───────────── The lead reviews wiki pages ─────────────

        /// <summary>"Lead designer", "Director".</summary>
        private string LeadRoleName => LeadReview.LeadRole(studio) is { } role ? role.Name : "Lead";

        public string LeadReviewsWikiText => $"The {LeadRoleName.ToLowerInvariant()} reviews wiki pages";

        /// <summary>Pages about the project that the studio's other agents change wait for the lead's review instead of yours.</summary>
        public bool LeadReviewsWiki
        {
            get => studio.LeadReviewsWiki;
            set
            {
                if (studio.LeadReviewsWiki == value) return;
                studio.LeadReviewsWiki = value;
                owner.Touch();
                OnPropertyChanged();
                OnPropertyChanged(nameof(LeadReviewHint));
                owner.Main.Wiki.RefreshPages();
            }
        }

        /// <summary>Only pages in this wiki folder (and below); empty: every page about the project.</summary>
        public string LeadReviewFolder
        {
            get => studio.LeadReviewFolder ?? "";
            set
            {
                var folder = WikiPaths.Normalize(value ?? "") is { Length: > 0 } f ? f : null;
                if (folder == studio.LeadReviewFolder) return;
                studio.LeadReviewFolder = folder;
                owner.Touch();
                OnPropertyChanged();
                OnPropertyChanged(nameof(LeadReviewHint));
                owner.Main.Wiki.RefreshPages();
            }
        }

        public IReadOnlyList<string> WikiFolders => owner.WikiFolders;

        public string LeadReviewHint
        {
            get
            {
                var who = LeadReview.LeadOf(studio) ?? $"the {LeadRoleName} (nobody's hired yet)";
                var pages = studio.LeadReviewFolder is { } folder ? $"Pages in {WikiPaths.Display(folder)}" : $"Pages about {Project.Name}";
                return LeadReviewsWiki
                    ? $"{pages} that the studio's other agents change wait for {who}'s review instead of yours: accepted, or sent back with a note. Their history " +
                      "says who accepted them, and you can reopen one. The lead's own changes, clashing edits and other pages still wait for you."
                    : $"Off: you review every wiki page agents change. On: {pages.ToLowerInvariant()[..1]}{pages[1..]} that the studio's other agents change wait " +
                      $"for {who} instead.";
            }
        }
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

    public string HolderText => StudioFlow.HolderOf(studio, role) is not { } agent ? "Nobody hired yet"
        : StudioFlow.JuniorsOf(studio, role) is { Count: > 0 } juniors ? $"Hired: {agent}, with junior{(juniors.Count > 1 ? "s" : "")} {string.Join(", ", juniors)}"
        : $"Hired: {agent}";
}
