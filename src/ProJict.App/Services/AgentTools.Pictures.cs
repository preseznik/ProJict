using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Agents' pictures, for agents: each makes its own if it can, or asks for one (wanted); the others are told once about
/// each wanted picture, and the first one to make it ends the request.
/// </summary>
public sealed partial class AgentTools
{
    private void AddPictureTools()
    {
        Add("set_picture", "Set a picture",
            "Your picture in the Agents view, or another agent's that's wanted: a square image (256 px or more; it's cut to a square in the middle). " +
            "Make it yourself if you can: a simple, friendly portrait or emblem in your badge colour, with no text and no real people or logos. If " +
            "you can't make images, call it with wanted=true and another agent will make one for you.",
            Schema(("image", "string[]", "One image file (absolute or relative to your working directory).", false),
                ("for", "string", "Another agent whose picture is wanted, instead of yours.", false),
                ("wanted", "boolean", "You can't make images: ask another agent to make yours.", false)),
            SetPicture, filesArgument: "image");
    }

    private bool PictureWanted(string agent) => _state.Settings.PicturesWanted?.Contains(agent, StringComparer.OrdinalIgnoreCase) == true;

    private string SetPicture(McpSession session, Args args, List<Attachment> files)
    {
        if (session.Name == null && session.SignAs == null) throw new McpToolException("Pick your name first with set_name: the picture goes with it.");
        var me = session.AgentName;
        var target = args.String("for")?.Trim().TrimStart('@') is { Length: > 0 } other ? other : me;
        var forOther = !string.Equals(target, me, StringComparison.OrdinalIgnoreCase);
        if (args.Bool("wanted") == true)
        {
            if (forOther) throw new McpToolException("You can only ask for your own picture.");
            if (_state.Pictures.Has(me)) throw new McpToolException("You have a picture already.", ToolProblem.Conflict);
            if (!PictureWanted(me))
            {
                (_state.Settings.PicturesWanted ??= []).Add(me);
                _state.MarkSettingsDirty();
                _main.AgentActivity.RefreshPictures();
            }
            return Json(new JsonObject { ["wanted"] = me, ["note"] = "The other agents are asked; it shows in the Agents view once one of them makes it." });
        }
        if (files.Count != 1) throw new McpToolException("Give one image file in image (or wanted=true if you can't make one).");
        if (forOther && !PictureWanted(target))
            throw new McpToolException($"{target}'s picture isn't wanted: set your own, or one that's wanted.", ToolProblem.Conflict);
        try { _state.Pictures.Set(target, _state.AttachmentPath(null, files[0])); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        if (_state.Settings.PicturesWanted is { } wanted && wanted.RemoveAll(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            if (wanted.Count == 0) _state.Settings.PicturesWanted = null;
            _state.MarkSettingsDirty();
        }
        _main.AgentActivity.RefreshPictures();
        _state.StatusText = forOther ? $"{me} made {target}'s picture." : $"{me} has a picture now.";
        return Json(new JsonObject
        {
            ["picture"] = target,
            ["note"] = forOther ? $"It's {target}'s picture now; thanks." : "It's your picture in the Agents view now.",
        });
    }

    /// <summary>For set_name: an agent without a picture is asked to make one (or ask for one).</summary>
    private string? PictureNote(McpSession session) =>
        _state.Pictures.Has(session.AgentName) || PictureWanted(session.AgentName) ? null
            : "You have no picture yet: make yourself one if you can (set_picture with a square image), or call set_picture wanted=true and another agent will.";

    /// <summary>For waitingForYou and the watcher: pictures other agents want, each once.</summary>
    private IEnumerable<(string Key, string Line)> PictureLines(McpSession session) =>
        (_state.Settings.PicturesWanted ?? []).Where(n => !string.Equals(n, session.AgentName, StringComparison.OrdinalIgnoreCase))
            .Select(n => ($"picture:{n.ToLowerInvariant()}",
                $"{n} has no picture and can't make one: if you can make images, make one (square, simple, friendly, no text) and set_picture for={n}."));
}
