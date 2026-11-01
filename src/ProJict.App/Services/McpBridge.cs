using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;

namespace ProJict.App.Services;

/// <summary>
/// <c>ProJict.exe --mcp</c>: the command an MCP client (Claude Code, Claude Desktop, …) starts. It relays the client's
/// stdin/stdout to the running ProJict over its agent pipe, starting ProJict in the tray first if it isn't running.
/// <c>ProJict.exe --watch-chat --as Ada</c> instead prints a line per chat message for that agent until stopped.
/// </summary>
public static class McpBridge
{
    public static async Task<int> RunAsync(string dataRoot, bool launch, string? dataArgument)
    {
        var (pipe, problem) = await OpenAsync(dataRoot, launch, dataArgument);
        if (pipe == null) return Fail(problem!);

        await using (pipe)
        {
            var hello = new JsonObject { [McpServer.BridgeHelloKey] = 1, ["cwd"] = Environment.CurrentDirectory }.ToJsonString() + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(hello));
            await pipe.FlushAsync();

            await using var stdin = Console.OpenStandardInput();
            await using var stdout = Console.OpenStandardOutput();
            var toProJict = stdin.CopyToAsync(pipe);
            var toClient = pipe.CopyToAsync(stdout);
            // Either side closing ends the session: the client quit, or ProJict exited.
            await Task.WhenAny(toProJict, toClient);
        }
        return 0;
    }

    /// <summary>A chat watcher: asks ProJict for the agent's chat messages and prints them, a line each, until either side stops.</summary>
    public static async Task<int> WatchAsync(string dataRoot, bool launch, string? dataArgument, string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent)) return Fail("Say whose chat to watch: ProJict.exe --watch-chat --as <agent name>.");
        var (pipe, problem) = await OpenAsync(dataRoot, launch, dataArgument);
        if (pipe == null) return Fail(problem!);
        await using (pipe)
        {
            var hello = new JsonObject { [McpServer.WatchHelloKey] = 1, ["as"] = agent.Trim() }.ToJsonString() + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(hello));
            await pipe.FlushAsync();
            await using var stdout = Console.OpenStandardOutput();
            await pipe.CopyToAsync(stdout);
        }
        return 0;
    }

    /// <summary>The running ProJict's agent pipe, starting ProJict in the tray first if it isn't running (and may be).</summary>
    private static async Task<(NamedPipeClientStream? Pipe, string? Problem)> OpenAsync(string dataRoot, bool launch, string? dataArgument)
    {
        var pipe = await ConnectAsync(dataRoot, TimeSpan.FromSeconds(1));
        if (pipe != null) return (pipe, null);
        if (SingleInstance.IsRunning(dataRoot))
            return (null, "ProJict is running but doesn't accept AI agents. Turn on Settings > AI agents in ProJict.");
        if (!launch) return (null, "ProJict isn't running.");
        // Shell execute, so ProJict doesn't inherit (and keep open) the client's stdio pipes.
        var args = "--background" + (dataArgument != null ? $" --data \"{dataArgument}\"" : "");
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true });
        pipe = await ConnectAsync(dataRoot, TimeSpan.FromSeconds(30));
        return pipe != null ? (pipe, null) : (null, "ProJict didn't start in time.");
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(string dataRoot, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", AgentPipeServer.PipeName(dataRoot), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    private static int Fail(string message)
    {
        try { Console.Error.WriteLine("ProJict: " + message); }
        catch (IOException) { }
        return 1;
    }
}
