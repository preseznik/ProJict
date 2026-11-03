using ProJict.Bridge;

// ProJictBridge.exe --mcp [--data <folder>] [--no-launch]: an AI agent's MCP client starts it to reach ProJict.
// ProJictBridge.exe --watch-chat --as <agent> [...]: prints a line per chat message for that agent.
// ProJictBridge.exe --permission-hook [--as <agent>] [--wait <seconds>]: Claude Code's or Codex's PermissionRequest hook,
// so you approve an agent's request in ProJict Chat.
// ProJict keeps a copy of it outside its install folder, so updating ProJict doesn't close it; when ProJict restarts it
// reconnects by itself.
var options = Options.From(args);
if (options.Watch) return await Watcher.RunAsync(options);
if (args.Contains("--permission-hook")) return await PermissionHook.RunAsync(options, args);
if (args.Contains("--mcp")) return await new McpRelay(options).RunAsync();
Console.Error.WriteLine("ProJictBridge: start it with --mcp (for an MCP client) or --watch-chat --as <agent name>.");
return 2;
