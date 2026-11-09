using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProJict.Core.Storage;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The same, without the indenting: for what only ProJict reads back, such as undo steps.</summary>
    public static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, CompactOptions);

    /// <summary>Writes the JSON (as <see cref="Serialize{T}"/> makes it) straight to a file, without a copy of it all in memory first.</summary>
    public static void WriteFile<T>(string path, T value) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, Options));

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public static class AtomicFile
{
    /// <summary>Like <see cref="WriteAllText"/>, for content written to a stream.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        var tmp = path + ".tmp";
        try
        {
            using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            write(file);
        }
        catch
        {
            // Nothing half-written is left behind; the file that was there stays as it was.
            try { File.Delete(tmp); }
            catch (IOException) { }
            throw;
        }
        Swap(tmp, path);
    }

    /// <summary>Writes to a temp file first, then swaps it in, so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        Swap(tmp, path);
    }

    private static void Swap(string tmp, string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
