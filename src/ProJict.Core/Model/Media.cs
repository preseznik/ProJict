using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

public enum MediaKind { Video, Audio, Image, Other }

/// <summary>
/// A big file an item stands for (a capture, an edit, an export), linked where it is rather than copied into ProJict like
/// an attachment: in the project's folder (its path is then relative to the folder) or anywhere on this computer. See
/// MediaLinks.
/// </summary>
public sealed class MediaLink
{
    public string Path { get; set; } = "";
    /// <summary>Its size when it was linked.</summary>
    public long Size { get; set; }
    public string AddedBy { get; set; } = "";
    /// <summary>Linked by an AI agent rather than a person.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}
