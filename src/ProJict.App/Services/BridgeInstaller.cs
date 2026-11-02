using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ProJict.App.Services;

/// <summary>
/// ProJictBridge.exe, what AI agents' MCP clients start (and chat watchers run): ProJict keeps a copy in its folder
/// under AppData, outside the install folder, so updating ProJict doesn't close the agents' connections; the bridge then
/// reconnects by itself when ProJict is back. The copy is refreshed when ProJict starts, also while agents run it.
/// </summary>
public static class BridgeInstaller
{
    public const string FileName = "ProJictBridge.exe";

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict");
    private static string Installed => Path.Combine(Folder, FileName);
    /// <summary>The one installed with ProJict (beside ProJict.exe; not in a development build).</summary>
    private static string Shipped => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The bridge agents should start, or null when there's none (a development build: then ProJict.exe --mcp).</summary>
    public static string? BridgePath => File.Exists(Installed) && File.Exists(Shipped) ? Installed : null;

    /// <summary>Puts the shipped bridge in place (if it changed), and tells it where ProJict is, to start it.</summary>
    public static void Refresh()
    {
        try
        {
            if (!File.Exists(Shipped)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "bridge.json"), new JsonObject { ["projict"] = Environment.ProcessPath }.ToJsonString());
            Place(Shipped, Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Puts <paramref name="shipped"/> in <paramref name="folder"/> as the bridge, if it changed. Agents keep their bridge
    /// running, and a running program can't be replaced or deleted, but it can be renamed: so it's moved aside and carries
    /// on, and every new start gets the new one. Copies moved aside earlier go once nothing runs them.
    /// </summary>
    internal static void Place(string shipped, string folder)
    {
        var installed = Path.Combine(folder, FileName);
        foreach (var old in Directory.EnumerateFiles(folder, "ProJictBridge.old-*.exe"))
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (File.Exists(installed) && Same(shipped, installed)) return;
        var fresh = installed + ".new";
        File.Copy(shipped, fresh, overwrite: true);
        try
        {
            File.Move(fresh, installed, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(installed, Path.Combine(folder, $"ProJictBridge.old-{DateTime.Now:yyyyMMdd-HHmmss-fff}.exe"));
            File.Move(fresh, installed);
        }
    }

    private static bool Same(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var first = File.OpenRead(a);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    /// <summary>The command for an agent: the bridge (or ProJict.exe in a development build) and its arguments.</summary>
    public static string Command => BridgePath ?? Environment.ProcessPath!;
}
