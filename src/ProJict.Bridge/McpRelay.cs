using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProJict.Bridge;

/// <summary>
/// Passes an MCP client's JSON-RPC lines to ProJict over its agent pipe and back. When ProJict goes away (it restarts,
/// say for an update) the client stays connected: what it was waiting for gets an error to try again, and as soon as
/// ProJict is back the bridge connects again, replays the client's initialize and the agent's set_name (so it keeps its
/// name), and tells the client the tool list changed, so it sees ProJict's new tools in the same session.
/// </summary>
internal sealed class McpRelay(Options options)
{
    /// <summary>The bridge's protocol, in its hello; ProJict tells agents still on the old ProJict.exe --mcp to move.</summary>
    public const string Version = "2";
    private static string Gone => ProJictPipe.SetupRunning()
        ? "ProJict is being updated. Try again in a minute; you stay connected, and the bridge reconnects by itself when it's back."
        : "ProJict went away: it's restarting, perhaps for an update. Try again in a moment; you stay connected, and the bridge reconnects by itself.";
    /// <summary>After this long without ProJict a call may start it (not sooner: an update's installer may be at work).</summary>
    private static readonly TimeSpan LaunchAfter = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _toClient = new(1, 1);
    private readonly SemaphoreSlim _toProJict = new(1, 1);
    private readonly Stream _stdout = Console.OpenStandardOutput();
    // The client's requests ProJict hasn't answered, by id; and the bridge's own replays, answered to it alone.
    private readonly Dictionary<string, JsonNode?> _pending = [];
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _replays = [];
    private NamedPipeClientStream? _pipe;
    private bool _connected;
    private DateTime _lostAt;
    private bool _reconnecting;
    private JsonObject? _initialize;
    private JsonObject? _setName;
    private int _replayCount;

    public async Task<int> RunAsync()
    {
        // A client waits for its connection only so long.
        var (pipe, problem) = await ProJictPipe.OpenAsync(options, options.Launch, waitForSetup: TimeSpan.FromSeconds(20));
        if (pipe == null)
        {
            Console.Error.WriteLine("ProJict: " + problem);
            return 1;
        }
        await AttachAsync(pipe, replay: false);
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
        while (await input.ReadLineAsync() is { } line)
            if (line.TrimStart('﻿').Trim().Length > 0) await FromClientAsync(line.TrimStart('﻿'));
        // The client quit: so does the bridge.
        lock (_gate) _pipe?.Dispose();
        return 0;
    }

    private static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";

    private async Task FromClientAsync(string line)
    {
        JsonObject? message = null;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { }
        var method = message?["method"]?.GetValue<string>();
        var isRequest = message != null && method != null && message.ContainsKey("id");
        var id = message?["id"]?.DeepClone();
        lock (_gate)
        {
            if (method == "initialize") _initialize = (JsonObject)message!.DeepClone();
            else if (method == "tools/call" && message?["params"]?["name"]?.GetValue<string>() == "set_name") _setName = (JsonObject)message.DeepClone();
        }

        NamedPipeClientStream? pipe;
        lock (_gate) pipe = _connected ? _pipe : null;
        if (pipe == null && isRequest) pipe = await StartProJictAsync();
        if (pipe == null)
        {
            // Notifications meanwhile don't matter: the client's are replayed when ProJict is back.
            if (isRequest) await ToClientAsync(Error(id, Gone));
            return;
        }
        if (isRequest) lock (_gate) _pending[Key(id)] = id;
        try
        {
            await ToProJictAsync(pipe, line);
        }
        catch (Exception)
        {
            Lost(pipe);
        }
    }

    /// <summary>A call while ProJict has been away a while: start it (if allowed) and wait for the bridge to be back in.</summary>
    private async Task<NamedPipeClientStream?> StartProJictAsync()
    {
        lock (_gate)
            if (!options.Launch || DateTime.Now - _lostAt < LaunchAfter) return null;
        if (!ProJictPipe.IsRunning(options.DataRoot) && !ProJictPipe.Launch(options)) return null;
        for (var waited = 0; waited < 30_000; waited += 250)
        {
            lock (_gate)
                if (_connected) return _pipe;
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>Uses this connection to ProJict; after a restart, first replays what ProJict needs to know about the agent.</summary>
    private async Task AttachAsync(NamedPipeClientStream pipe, bool replay)
    {
        var hello = new JsonObject { ["projictBridge"] = 2, ["bridge"] = Version, ["cwd"] = Environment.CurrentDirectory };
        await ToProJictAsync(pipe, hello.ToJsonString());
        lock (_gate) _pipe = pipe;
        _ = Task.Run(() => FromProJictAsync(pipe));
        if (replay)
        {
            JsonObject? initialize, setName;
            lock (_gate) (initialize, setName) = (_initialize, _setName);
            if (initialize != null)
            {
                await ReplayAsync(pipe, initialize);
                await ToProJictAsync(pipe, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString());
            }
            if (setName != null) await ReplayAsync(pipe, setName);
        }
        lock (_gate) _connected = true;
        // The client asks for the tools again: ProJict may be a newer version now.
        if (replay) await ToClientAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
    }

    /// <summary>Sends one of the client's earlier requests again under an id of the bridge's own, and waits for the answer.</summary>
    private async Task ReplayAsync(NamedPipeClientStream pipe, JsonObject original)
    {
        var request = (JsonObject)original.DeepClone();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            request["id"] = $"projict-bridge-{++_replayCount}";
            _replays[Key(request["id"])] = tcs;
        }
        await ToProJictAsync(pipe, request.ToJsonString());
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    }

    private async Task FromProJictAsync(NamedPipeClientStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync() is { } line)
            {
                JsonObject? message = null;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { }
                if (message != null && message.ContainsKey("id") && !message.ContainsKey("method"))
                {
                    var key = Key(message["id"]);
                    TaskCompletionSource<JsonObject>? replay;
                    lock (_gate)
                    {
                        _replays.Remove(key, out replay);
                        if (replay == null) _pending.Remove(key);
                    }
                    if (replay != null)
                    {
                        replay.TrySetResult(message);
                        continue;
                    }
                }
                await ToClientAsync(line);
            }
        }
        catch (Exception) { }
        Lost(pipe);
    }

    /// <summary>ProJict went away: what the client waits for fails (to try again), and the bridge waits for ProJict to come back.</summary>
    private void Lost(NamedPipeClientStream pipe)
    {
        List<JsonNode?> waiting;
        lock (_gate)
        {
            if (_pipe != pipe) return;
            _pipe = null;
            _connected = false;
            _lostAt = DateTime.Now;
            waiting = [.. _pending.Values];
            _pending.Clear();
            foreach (var replay in _replays.Values) replay.TrySetCanceled();
            _replays.Clear();
        }
        pipe.Dispose();
        _ = Task.Run(async () =>
        {
            foreach (var id in waiting) await ToClientAsync(Error(id, Gone));
        });
        lock (_gate)
        {
            if (_reconnecting) return;
            _reconnecting = true;
        }
        _ = Task.Run(ReconnectAsync);
    }

    private async Task ReconnectAsync()
    {
        while (true)
        {
            if (await ProJictPipe.ConnectAsync(options.DataRoot, TimeSpan.FromSeconds(1)) is { } pipe)
            {
                try
                {
                    await AttachAsync(pipe, replay: true);
                    lock (_gate) _reconnecting = false;
                    return;
                }
                catch (Exception)
                {
                    await pipe.DisposeAsync();
                }
            }
            await Task.Delay(1000);
        }
    }

    private static JsonObject Error(JsonNode? id, string text) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = text } };

    private Task ToClientAsync(JsonObject message) => ToClientAsync(message.ToJsonString());

    private async Task ToClientAsync(string line)
    {
        await _toClient.WaitAsync();
        try
        {
            await _stdout.WriteAsync(Utf8.GetBytes(line + "\n"));
            await _stdout.FlushAsync();
        }
        catch (IOException)
        {
            // The client is gone.
            Environment.Exit(0);
        }
        finally
        {
            _toClient.Release();
        }
    }

    private async Task ToProJictAsync(NamedPipeClientStream pipe, string line)
    {
        await _toProJict.WaitAsync();
        try
        {
            await pipe.WriteAsync(Utf8.GetBytes(line + "\n"));
            await pipe.FlushAsync();
        }
        finally
        {
            _toProJict.Release();
        }
    }
}
