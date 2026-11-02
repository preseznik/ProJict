using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;

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

    public void RefreshStudio()
    {
        foreach (var p in new[] { nameof(CanHaveStudio), nameof(HasStudio), nameof(StudioRoles), nameof(StudioHint) }) OnPropertyChanged(p);
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
