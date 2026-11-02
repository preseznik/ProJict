using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>A file linked to an item where it is (a capture, an edit, an export): its preview frame, name and size.</summary>
public sealed partial class MediaLinkViewModel
{
    // Rows are rebuilt on every edit: each file's preview is made once (again when the file changes).
    private static readonly Dictionary<(string, DateTime), ImageSource?> Previews = [];
    private const int PreviewPixels = 256;

    private readonly Action _unlink;

    public MediaLinkViewModel(Project project, MediaLink model, Action unlink)
    {
        Model = model;
        _unlink = unlink;
        FullPath = MediaLinks.FullPath(project, model);
        Kind = MediaLinks.KindOf(model.Path);
        var file = new FileInfo(FullPath);
        Exists = file.Exists;
        if (Exists) Preview = Kind is MediaKind.Video or MediaKind.Image ? LoadPreview(file) : null;
        SizeText = Exists ? AttachmentViewModel.FormatSize(file.Length) : "missing";
    }

    public MediaLink Model { get; }
    public string FullPath { get; }
    public MediaKind Kind { get; }
    public bool Exists { get; }
    public ImageSource? Preview { get; }
    public bool HasPreview => Preview != null;
    public string FileName => Path.GetFileName(FullPath);
    public string SizeText { get; }
    public string Glyph => Kind switch
    {
        MediaKind.Video => "",
        MediaKind.Audio => "",
        MediaKind.Image => "",
        _ => "",
    };
    public string ToolTip => (Exists ? $"{FullPath}\n{SizeText} · linked, not copied" : $"{FullPath}\nNot there any more")
                             + $" · {Model.AddedBy}\nClick to open · right-click for more";

    [RelayCommand]
    private void Open()
    {
        if (!Exists) return;
        try { Process.Start(new ProcessStartInfo(FullPath) { UseShellExecute = true }); }
        catch (Exception) { /* nothing to open it with: the path is in the tooltip */ }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Exists ? $"/select,\"{FullPath}\"" : $"\"{Path.GetDirectoryName(FullPath)}\"") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    [RelayCommand]
    private void CopyPath()
    {
        try { Clipboard.SetText(FullPath); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    [RelayCommand] private void Unlink() => _unlink();

    private ImageSource? LoadPreview(FileInfo file)
    {
        var key = (file.FullName.ToLowerInvariant(), file.LastWriteTimeUtc);
        if (!Previews.TryGetValue(key, out var preview)) Previews[key] = preview = ShellThumbnails.Get(file.FullName, PreviewPixels);
        return preview;
    }
}
