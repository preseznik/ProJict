using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Getting a shared project's test build from its host (the project's Details or its test build under the name ›
/// Get it): asked once, it comes like a big Chat file, in pieces, into this computer's folder for test builds, where
/// Run starts it. A newer test build there replaces the older one.
/// </summary>
public sealed partial class GuestService
{
    /// <summary>How long a host has to start sending before it's taken not to know how (a ProJict before 0.1.42).</summary>
    private static readonly TimeSpan TestBuildAnswer = TimeSpan.FromSeconds(30);

    /// <summary>Where test builds from other ProJicts are kept on this computer.</summary>
    public static string TestBuildsRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict", "Test builds");

    /// <summary>A test build on its way: started (the host said it's coming), how far it got (0 to 1), or why it didn't come.</summary>
    public sealed record TestBuildFetch(bool Coming, double Progress, string? Problem);

    private readonly Dictionary<Guid, TestBuildFetch> _testBuilds = [];

    /// <summary>A project's test build is on its way, came or didn't.</summary>
    public event Action<Guid>? TestBuildChanged;

    /// <summary>The project's test build getting here, or why it didn't (null when nothing's asked).</summary>
    public TestBuildFetch? TestBuildFetchOf(Project project) => _testBuilds.GetValueOrDefault(project.Id);

    private static string Safe(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>The folder for one project's test builds from its host.</summary>
    private static string TestBuildsOf(Project project) => Path.Combine(TestBuildsRoot, Safe(project.Remote?.HostName ?? "host"), Safe(project.Name));

    /// <summary>The project's current test build here, once it came (null before).</summary>
    public static string? LocalTestBuild(Project project)
    {
        if (project.TestBuild is not { } build) return null;
        var folder = Path.Combine(TestBuildsOf(project), build.Key);
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Asks the host for the project's test build; the problem if it can't, or null.</summary>
    public string? GetTestBuild(Project project)
    {
        if (project.Remote is not { } remote || project.TestBuild == null) return "It has no test build here to get.";
        var connection = Connections.FirstOrDefault(c => c.Address == remote.HostKey && c.State == ConnectionState.Connected);
        if (connection == null) return $"Not connected to {remote.HostName}'s ProJict now: it can send the test build once you are.";
        _testBuilds[project.Id] = new TestBuildFetch(false, 0, null);
        connection.Send(Wire.Message("getTestBuild", ("projectId", project.Id.ToString())));
        TestBuildChanged?.Invoke(project.Id);
        var asked = _testBuilds[project.Id];
        _ = Task.Delay(TestBuildAnswer).ContinueWith(_ => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Still not started: a host before 0.1.42 doesn't know the question.
            if (!_testBuilds.TryGetValue(project.Id, out var now) || now != asked) return;
            _testBuilds[project.Id] = new TestBuildFetch(false, 0, $"{remote.HostName}'s ProJict didn't answer: it may need ProJict 0.1.42 or newer.");
            TestBuildChanged?.Invoke(project.Id);
        }));
        return null;
    }

    private RemoteCopy? CopyOf(HostConnection connection, JsonObject message) =>
        Guid.TryParse((string?)message["projectId"], out var id) && connection.Copies.TryGetValue(id, out var copy) ? copy : null;

    /// <summary>The host said the test build is coming (a folder is being zipped first).</summary>
    private void TestBuildComing(HostConnection connection, JsonObject message)
    {
        if (CopyOf(connection, message) is not { } copy) return;
        _testBuilds[copy.Project.Id] = new TestBuildFetch(true, 0, null);
        TestBuildChanged?.Invoke(copy.Project.Id);
    }

    /// <summary>The host couldn't send it, and says why.</summary>
    private void TestBuildFailed(HostConnection connection, JsonObject message)
    {
        if (CopyOf(connection, message) is not { } copy) return;
        _testBuilds[copy.Project.Id] = new TestBuildFetch(false, 0, (string?)message["message"] ?? "It didn't come.");
        TestBuildChanged?.Invoke(copy.Project.Id);
    }

    /// <summary>The test build, whole or a piece of it, into its own folder; once it's all here, older ones go.</summary>
    private void StoreTestBuild(HostConnection connection, JsonObject message)
    {
        if (CopyOf(connection, message) is not { } copy || copy.Project.TestBuild is not { } build) return;
        var project = copy.Project;
        var name = Path.GetFileName((string?)message["name"] ?? "");
        var key = (string?)message["key"] is { } k && k.All(char.IsAsciiLetterOrDigit) && k.Length is > 0 and <= 64 ? k : build.Key;
        if (name.Length == 0) return;
        var folder = Path.Combine(TestBuildsOf(project), key);
        try
        {
            Directory.CreateDirectory(folder);
            switch (WireFiles.Receive(message, Path.Combine(folder, name), WireFiles.MaxBytes))
            {
                case FileArrival.More:
                    var size = Math.Max(1, (long?)message["size"] ?? 1);
                    var got = ((long?)message["offset"] ?? 0) + ((string?)message["data"] ?? "").Length / 4L * 3;
                    _testBuilds[project.Id] = new TestBuildFetch(true, Math.Min(1, (double)got / size), null);
                    break;
                case FileArrival.Done:
                    _testBuilds.Remove(project.Id);
                    foreach (var older in Directory.EnumerateDirectories(TestBuildsOf(project)).Where(d => !string.Equals(Path.GetFileName(d), key, StringComparison.OrdinalIgnoreCase)))
                    {
                        try { Directory.Delete(older, recursive: true); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    _state.StatusText = $"{name} came from {connection.HostName}'s ProJict: Run starts it.";
                    break;
                default:
                    _testBuilds[project.Id] = new TestBuildFetch(false, 0, $"{name} didn't come through whole. Try again.");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _testBuilds[project.Id] = new TestBuildFetch(false, 0, $"{name} couldn't be saved here: {ex.Message}");
        }
        TestBuildChanged?.Invoke(project.Id);
    }
}
