using System.IO;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>Items' linked media files, for agents: big captures, edits and exports linked where they are, not copied.</summary>
public sealed partial class AgentTools
{
    private void AddMediaTools()
    {
        Add("link_media", "Link media files",
            "Links big files (captures, edits, exports, music, voice) to an item where they are: nothing is copied, unlike attach_file. A file in the " +
            "project's folder is kept relative to it, and Clean up leaves linked files alone. With unlink=true, takes links off instead (the files stay).",
            Schema(IdArg, ("paths", "string[]", "Files (absolute or relative to your working directory); to unlink, their paths or file names.", true),
                ("unlink", "boolean", "Take these links off instead.", false), ForceArg),
            LinkMediaFiles);
    }

    private string LinkMediaFiles(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("id"));
        var paths = args.Strings("paths");
        if (paths.Count == 0) throw new McpToolException("Give at least one file in paths.");
        MayChange(project, Change.HostOnly);
        if (session.Remote)
            throw new McpToolException("Linked files are on this computer, so only agents running here can link them; attach_file copies a file instead.", ToolProblem.Forbidden);
        CheckMayWork(project, loc.Item);
        var unlink = args.Bool("unlink") == true;
        var full = unlink ? paths : paths.Select(p => Path.IsPathRooted(p) ? p : Path.Combine(session.WorkingDirectory ?? Environment.CurrentDirectory, p)).ToList();
        // Tried on a copy first, so nothing half-done is left (or undone) when a path is wrong.
        try
        {
            var trial = new TodoItem { Media = loc.Item.Media?.ToList() };
            if (unlink) foreach (var p in full) MediaLinks.Unlink(project, trial, p);
            else MediaLinks.Link(project, trial, full, session.AgentName, agent: true);
        }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        var takenFrom = CheckOut(session, project, loc.Item, args);
        var agent = SignAs(session, project);
        List<string> done = [];
        Edit(project, loc.Item.Id, i =>
        {
            if (takenFrom != null)
            {
                i.AddActivity(agent, $"Took it over from {takenFrom}.", agent: true);
                i.ClaimedBy = agent;
            }
            done = unlink ? full.Select(p => Path.GetFileName(MediaLinks.Unlink(project, i, p).Path)).ToList()
                : MediaLinks.Link(project, i, full, agent, agent: true).Select(m => Path.GetFileName(m.Path)).ToList();
            if (done.Count > 0) i.AddActivity(agent, (unlink ? "Unlinked " : "Linked ") + string.Join(", ", done) + ".", agent: true);
        });
        if (takenFrom != null) session.Claimed.Add(loc.Item.Id);
        var item = project.Find(loc.Item.Id)!.Item;
        _state.StatusText = done.Count == 0 ? $"{agent}: those files are linked already."
            : $"{agent} {(unlink ? "unlinked" : "linked")} {string.Join(", ", done)} {(unlink ? "from" : "to")} “{Short(item.Text)}”.";
        return Json(new JsonObject { ["id"] = ShortId(item.Id), ["media"] = MediaJson(item.Media ?? [], project) });
    }

    private static JsonArray MediaJson(IEnumerable<MediaLink> media, Project project) =>
        new(media.Select(m =>
        {
            var full = MediaLinks.FullPath(project, m);
            var file = new FileInfo(full);
            var json = new JsonObject
            {
                ["path"] = full,
                ["kind"] = MediaLinks.KindOf(m.Path).ToString().ToLowerInvariant(),
                ["size"] = file.Exists ? file.Length : m.Size,
                ["linkedBy"] = m.AddedBy,
            };
            if (!file.Exists) json["missing"] = true;
            return (JsonNode)json;
        }).ToArray());
}
