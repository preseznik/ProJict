using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Tools;

namespace ProJict.App.ViewModels;

/// <summary>
/// A program on this computer (ffmpeg, VRCapture): where it is, with a Check that it's there (it's never started), its
/// docs and the folder where it saves what it makes. Agents read these in list_tools and run it themselves.
/// </summary>
public sealed partial class ToolEditorViewModel
{
    public bool IsProgram
    {
        get => tool.Access == ToolAccess.Program;
        set
        {
            if (!value || tool.Access == ToolAccess.Program) return;
            tool.Access = ToolAccess.Program;
            Changed(nameof(IsApi), nameof(IsBrowser), nameof(IsComfy), nameof(IsProgram), nameof(CheckText));
        }
    }

    /// <summary>"on this computer · found", "· not found" or "· no path yet", for the list.</summary>
    private string ProgramSubtitle => "on this computer · " + (ProgramCheck.Clean(tool.ProgramPath) == null ? "no path yet" : ProgramCheck.Of(tool.ProgramPath).Found ? "found" : "not found");

    public string ProgramPath
    {
        get => tool.ProgramPath ?? "";
        set
        {
            var path = ProgramCheck.Clean(value);
            if (path == tool.ProgramPath) return;
            tool.ProgramPath = path;
            Changed(nameof(ProgramPath), nameof(CheckText), nameof(ProgramFound));
        }
    }

    /// <summary>Whether it's there, in a line ("Found ffmpeg.exe, version 7.1.").</summary>
    public string CheckText => ProgramCheck.Of(tool.ProgramPath).Text;
    public bool ProgramFound => ProgramCheck.Of(tool.ProgramPath).Found;

    /// <summary>Looks again (it may have been installed or moved meanwhile).</summary>
    [RelayCommand]
    private void CheckProgram()
    {
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(ProgramFound));
        OnPropertyChanged(nameof(Subtitle));
        main.State.StatusText = $"{tool.Name}: {CheckText}";
    }

    [RelayCommand]
    private void BrowseProgram()
    {
        var current = ProgramCheck.Clean(tool.ProgramPath);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Where is {tool.Name}?", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = current != null ? Directory.Exists(current) ? current : Path.GetDirectoryName(current) : null,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) ProgramPath = dialog.FileName;
    }

    public string Docs
    {
        get => tool.Docs ?? "";
        set
        {
            var text = ProgramCheck.Clean(value);
            if (text != null && !text.Contains("://", StringComparison.Ordinal) && !Path.IsPathRooted(text)) text = ToolData.Website(text) ?? text;
            if (text == tool.Docs) return;
            tool.Docs = text;
            Changed(nameof(Docs), nameof(HasDocs));
        }
    }
    public bool HasDocs => tool.Docs != null;

    /// <summary>Its docs: a web page in the browser, a file or folder on this computer in its app.</summary>
    [RelayCommand]
    private void OpenDocs()
    {
        if (tool.Docs is not { } docs) return;
        try
        {
            if (docs.Contains("://", StringComparison.Ordinal)) ShellLauncher.OpenUrl(docs);
            else if (Directory.Exists(docs)) ShellLauncher.OpenFolder(docs);
            else ShellLauncher.OpenFile(docs);
        }
        catch (Exception) { main.State.StatusText = $"Couldn't open {docs}."; }
    }

    public string OutputFolder
    {
        get => tool.OutputFolder ?? "";
        set
        {
            var path = ProgramCheck.Clean(value);
            if (path == tool.OutputFolder) return;
            tool.OutputFolder = path;
            Changed(nameof(OutputFolder), nameof(HasOutputFolder));
        }
    }
    public bool HasOutputFolder => tool.OutputFolder != null && Directory.Exists(tool.OutputFolder);

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Where does {tool.Name} save what it makes?" };
        if (tool.OutputFolder != null && Directory.Exists(tool.OutputFolder)) dialog.InitialDirectory = tool.OutputFolder;
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) OutputFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (tool.OutputFolder is not { } folder) return;
        try { ShellLauncher.OpenFolder(folder); }
        catch (Exception) { main.State.StatusText = $"Couldn't open {folder}."; }
    }
}
