using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// Projects' icons: beside their names in the sidebar, in their header and on the Dashboard; set from the sidebar's
/// right-click menu or Details › Icon. A shared project's icon goes to the other ProJict like its other files.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The project's icon, if it has one (another ProJict's is asked for the first time it's wanted).</summary>
    public ImageSource? IconOf(Project project)
    {
        if (project.Icon is not { } icon) return null;
        var path = State.AttachmentPath(project, icon);
        if (project.Remote != null && !File.Exists(path)) Guest.Fetch(project, icon);
        return Icons.Load(path);
    }

    [RelayCommand]
    private void SetProjectIcon(ProjectListItemViewModel? item)
    {
        if (item != null) SetIcon(item.Project);
    }

    [RelayCommand]
    private void RemoveProjectIcon(ProjectListItemViewModel? item)
    {
        if (item != null) RemoveIcon(item.Project);
    }

    /// <summary>Asks for an image and makes it the project's icon.</summary>
    public void SetIcon(Project project)
    {
        if (NotYours(project) || Icons.Pick($"An icon for {project.Name}") is not { } file) return;
        try
        {
            project.Icon = Icons.Make(State.Store, file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            State.StatusText = ex.Message;
            return;
        }
        IconChanged(project);
        State.StatusText = $"{project.Name} has an icon.";
    }

    public void RemoveIcon(Project project)
    {
        if (project.Icon == null || NotYours(project)) return;
        project.Icon = null;
        IconChanged(project);
        State.StatusText = $"{project.Name} has no icon any more.";
    }

    private void IconChanged(Project project)
    {
        State.MarkDirty(project);
        IconArrived(project);
    }

    /// <summary>The project's icon is here now (set, removed, or come from another ProJict): what shows it follows.</summary>
    internal void IconArrived(Project project)
    {
        ProjectItems.FirstOrDefault(i => i.Project == project)?.Refresh();
        GetProjectViewModel(project).RefreshIcon();
        Dashboard.Refresh();
    }
}
