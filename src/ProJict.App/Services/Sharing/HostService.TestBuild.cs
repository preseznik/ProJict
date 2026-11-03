using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// A shared project's test build, for a guest to try (its Details › Get it): on request the host sends the file the
/// project's test build names, in pieces like a big Chat file, or a folder as a zip of it. Only for a project the guest
/// sees, and only that file; nothing else on this computer is sent.
/// </summary>
public sealed partial class HostService
{
    private void SendTestBuild(GuestSession session, JsonObject message)
    {
        var id = Guid.TryParse((string?)message["projectId"], out var g) ? g : Guid.Empty;
        void Fail(string why) => session.Send(Wire.Message("testBuildError", ("projectId", id.ToString()), ("message", why)));
        var project = Visible(session.Account).FirstOrDefault(p => p.Id == id);
        if (project?.TestBuild is not { } build)
        {
            Fail("It has no test build to send.");
            return;
        }
        var path = build.FullPath(project.RepoPath);
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var me = _state.UserName;
        // Off the UI thread: a folder is zipped first, and a big file goes piece by piece.
        _ = Task.Run(async () =>
        {
            string? zip = null;
            try
            {
                if (Directory.Exists(path))
                {
                    zip = Path.Combine(Path.GetTempPath(), $"ProJict test build {Guid.NewGuid():N}.zip");
                    name += ".zip";
                    session.Send(Wire.Message("testBuildComing", ("projectId", id.ToString()), ("name", name)));
                    ZipFile.CreateFromDirectory(path, zip);
                }
                else if (!File.Exists(path))
                {
                    Fail($"{name} isn't there any more on {me}'s computer.");
                    return;
                }
                else session.Send(Wire.Message("testBuildComing", ("projectId", id.ToString()), ("name", name)));
                var file = new OutgoingFile(name, Path: zip ?? path);
                if (file.Size > WireFiles.LimitFor(session.TakesPieces))
                    Fail(session.TakesPieces ? $"{name} is bigger than 4 GB." : $"{name} is too big for your ProJict: update it to take big files.");
                else if (!await session.SendFileAsync(Wire.Message("testBuild", ("projectId", id.ToString()), ("name", name), ("key", build.Key)), file))
                    Fail($"{name} didn't come through. Try again.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Fail($"{name} couldn't be read on {me}'s computer: {ex.Message}");
            }
            finally
            {
                if (zip != null)
                {
                    try { File.Delete(zip); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        });
    }
}
