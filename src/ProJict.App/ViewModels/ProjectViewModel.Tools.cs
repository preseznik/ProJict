using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Tools;

namespace ProJict.App.ViewModels;

/// <summary>
/// The outside tools set up for the project, ticked in Details (Settings › Tools has them all): agents working here are
/// told which they are in current_project. Kept with the tools on this computer, not in the project.
/// </summary>
public sealed partial class ProjectViewModel
{
    public bool CanHaveTools => !Project.IsInbox && Project.Remote == null;

    public IReadOnlyList<ProjectToolChoice> ToolChoices => Main.Tools.Tools.Select(t => new ProjectToolChoice(t, Project.Id, Main.Tools)).ToList();
    public bool HasToolChoices => Main.Tools.Tools.Count > 0;

    public string ToolsHint => HasToolChoices
        ? "Tick the ones agents may use here; current_project tells them, and list_tools how to reach each."
        : "No outside tools are set up yet.";

    [RelayCommand]
    private void OpenTools()
    {
        new Views.ToolsWindow(Main) { Owner = Application.Current.MainWindow }.ShowDialog();
        RefreshTools();
    }

    public void RefreshTools()
    {
        foreach (var name in new[] { nameof(ToolChoices), nameof(HasToolChoices), nameof(ToolsHint) }) OnPropertyChanged(name);
    }
}

/// <summary>A tool in a project's Details: ticked when it's set up for the project.</summary>
public sealed class ProjectToolChoice(ExternalTool tool, Guid projectId, ToolService service) : ObservableObject
{
    public string Name => tool.Name;
    public string ToolTip => (tool.Purpose.Length > 0 ? tool.Purpose + "\n" : "") + (tool.Access == ToolAccess.Api ? tool.HasKey ? "API, key set" : "API, no key yet" : "In the browser");

    public bool IsChecked
    {
        get => tool.Projects.Contains(projectId);
        set
        {
            if (value == IsChecked) return;
            if (value) tool.Projects.Add(projectId);
            else tool.Projects.Remove(projectId);
            service.Save();
            OnPropertyChanged();
        }
    }
}
