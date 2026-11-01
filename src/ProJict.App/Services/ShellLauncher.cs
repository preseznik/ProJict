using System.Diagnostics;
using System.IO;

namespace ProJict.App.Services;

/// <summary>Opens a project's repo in Explorer, VS Code, Windows Terminal or the browser, and attached files.</summary>
public static class ShellLauncher
{
    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });

    public static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Starts an app or opens a file as Explorer would, in its own folder (so a game finds its data next to it).</summary>
    public static void Run(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });

    public static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,{Quote(path)}") { UseShellExecute = true });

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenInVsCode(string path, string? configuredExe)
    {
        var candidates = new[]
        {
            configuredExe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        };
        var exe = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
        if (exe != null)
        {
            Process.Start(new ProcessStartInfo(exe, Quote(path)) { UseShellExecute = false });
            return;
        }
        // Fall back to the "code" command on PATH.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c code {Quote(path)}") { CreateNoWindow = true, UseShellExecute = false });
    }

    public static void OpenTerminal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d {Quote(path)}") { UseShellExecute = true });
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { UseShellExecute = true, WorkingDirectory = path });
        }
    }

    /// <summary>Quotes a path for a command line; a trailing backslash would otherwise escape the closing quote.</summary>
    private static string Quote(string path)
    {
        var trimmed = path.TrimEnd('\\');
        if (trimmed.EndsWith(':')) trimmed += "\\.";
        return $"\"{trimmed}\"";
    }
}
