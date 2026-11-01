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
        ? "Each role and what it expects: an agent is told this when you hire it (right-click its robot in the Agents view's Game studio), and current_project shows the latest."
        : "A room of its own in the Agents view: a station per role (lead and game designers, level designer, programmer, artist, sound engineer, QA, " +
          "producer). Agents working here wait in its lobby until you hire them to a role, and are told what it expects.";

    [RelayCommand]
    private void SetUpStudio()
    {
        if (!CanHaveStudio || HasStudio) return;
        Project.Studio = StudioFlow.NewGame();
        Touch();
        RefreshStudio();
        _state.StatusText = $"{Project.Name} has a game studio: see it in the Agents view (Game studio), and hire the agents working here to its roles.";
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
