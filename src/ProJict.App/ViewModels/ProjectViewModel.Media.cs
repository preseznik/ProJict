using System.IO;
using System.Windows;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>Items' linked media files: big captures, edits and exports linked where they are, not copied. See MediaLinks.</summary>
public sealed partial class ProjectViewModel
{
    public void BrowseMedia(ItemRowViewModel row)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Link files (they stay where they are)", Multiselect = true };
        if (Project.RepoPath is { } repo && Directory.Exists(repo)) dialog.InitialDirectory = repo;
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) LinkMedia(row, dialog.FileNames);
    }

    /// <summary>Links the files to the row's item where they are (undo takes the links back; the files stay).</summary>
    public void LinkMedia(ItemRowViewModel row, IReadOnlyList<string> paths)
    {
        List<MediaLink> added = [];
        try
        {
            Mutate(() => added = MediaLinks.Link(Project, row.Item, paths, _state.UserName, agent: false), [row.Category]);
        }
        catch (InvalidOperationException ex)
        {
            _state.StatusText = ex.Message;
            return;
        }
        _state.StatusText = added.Count switch
        {
            0 => "Those are linked already.",
            1 => $"Linked {Path.GetFileName(added[0].Path)}: it stays where it is.",
            _ => $"Linked {added.Count} files: they stay where they are.",
        };
    }

    public void UnlinkMedia(ItemRowViewModel row, MediaLink link)
    {
        Mutate(() =>
        {
            row.Item.Media?.Remove(link);
            if (row.Item.Media is { Count: 0 }) row.Item.Media = null;
        }, [row.Category]);
        _state.StatusText = $"Unlinked {Path.GetFileName(link.Path)}; the file stays where it is (Ctrl+Z links it again).";
    }
}
