using System.Text.Json.Nodes;

namespace ProJict.Core.Sharing;

/// <summary>A file to send: its contents in memory, or where it is.</summary>
public sealed record OutgoingFile(string Name, byte[]? Data = null, string? Path = null)
{
    public long Size => Data?.LongLength ?? new FileInfo(Path!).Length;
}

/// <summary>How far a received file got.</summary>
public enum FileArrival
{
    /// <summary>It's there whole.</summary>
    Done,
    /// <summary>A piece of it came; more follow.</summary>
    More,
    /// <summary>Too big, broken or out of order: nothing was kept.</summary>
    Failed,
}

/// <summary>
/// Files between ProJicts. One before 0.1.40 takes a file whole, in a single message of at most 15 MB. One that says
/// "pieces" in its hello (a guest) or welcome (a host) takes bigger files in pieces of 4 MB, each with its offset and the
/// file's size; they're put together in a ".part" file beside where the file goes, which becomes it with the last piece.
/// </summary>
public static class WireFiles
{
    /// <summary>The most a ProJict that doesn't take pieces reads in one message.</summary>
    public const long WholeLimit = 15 * 1024 * 1024;
    public const int PieceBytes = 4 * 1024 * 1024;
    /// <summary>The most any ProJict sends or takes, in pieces.</summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The biggest file the other ProJict takes.</summary>
    public static long LimitFor(bool pieces) => pieces ? MaxBytes : WholeLimit;

    /// <summary>
    /// Sends the file as <paramref name="message"/> (which names it): whole when it's small or the other side doesn't take
    /// pieces, else piece by piece, each sent before the next is read. False when it's too big for the other side.
    /// </summary>
    public static async Task<bool> SendAsync(JsonObject message, OutgoingFile file, bool pieces, Func<JsonObject, Task> send, CancellationToken ct = default)
    {
        var size = file.Size;
        if (size > LimitFor(pieces)) return false;
        if (size <= PieceBytes || !pieces)
        {
            var whole = (JsonObject)message.DeepClone();
            whole["data"] = Convert.ToBase64String(file.Data ?? await File.ReadAllBytesAsync(file.Path!, ct).ConfigureAwait(false));
            await send(whole).ConfigureAwait(false);
            return true;
        }
        await using Stream stream = file.Data != null
            ? new MemoryStream(file.Data, writable: false)
            : new FileStream(file.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        var buffer = new byte[PieceBytes];
        for (long offset = 0; offset < size;)
        {
            var read = await stream.ReadAtLeastAsync(buffer, (int)Math.Min(PieceBytes, size - offset), throwOnEndOfStream: true, ct).ConfigureAwait(false);
            read = (int)Math.Min(read, size - offset);
            var piece = (JsonObject)message.DeepClone();
            piece["offset"] = offset;
            piece["size"] = size;
            piece["data"] = Convert.ToBase64String(buffer, 0, read);
            await send(piece).ConfigureAwait(false);
            offset += read;
        }
        return true;
    }

    /// <summary>Whether the message is a piece of a file (rather than all of it).</summary>
    public static bool IsPiece(JsonObject message) => message["offset"] != null;

    /// <summary>Whether the message starts a file: a whole one, or its first piece.</summary>
    public static bool Starts(JsonObject message) => !IsPiece(message) || (long?)message["offset"] == 0;

    /// <summary>The file's size as the message says (a whole one's from its data).</summary>
    public static long SizeOf(JsonObject message) => IsPiece(message) ? (long?)message["size"] ?? 0 : ((string?)message["data"] ?? "").Length / 4L * 3;

    /// <summary>
    /// Writes what the message carries to <paramref name="path"/>: a whole file at once; a piece into path + ".part",
    /// which the first piece starts and the last one moves into place (replacing what's there).
    /// </summary>
    public static FileArrival Receive(JsonObject message, string path, long limit)
    {
        byte[] data;
        try { data = Convert.FromBase64String((string?)message["data"] ?? ""); }
        catch (FormatException) { return FileArrival.Failed; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (!IsPiece(message))
        {
            if (data.Length > limit) return FileArrival.Failed;
            File.WriteAllBytes(path, data);
            return FileArrival.Done;
        }
        var part = path + ".part";
        var offset = (long?)message["offset"] ?? -1;
        var size = (long?)message["size"] ?? -1;
        if (size > limit || offset < 0 || offset + data.Length > size || data.Length == 0 ||
            (offset > 0 && (!File.Exists(part) || new FileInfo(part).Length != offset)))
        {
            File.Delete(part);
            return FileArrival.Failed;
        }
        using (var stream = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
            stream.Write(data);
        if (offset + data.Length < size) return FileArrival.More;
        File.Move(part, path, overwrite: true);
        return FileArrival.Done;
    }
}
