using System.Text.Json.Serialization;

namespace DirectLink.Common.Dto;

public class FileTransferMeta
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("fileHash")]
    public string? FileHash { get; set; }

    [JsonPropertyName("resumeOffset")]
    public long ResumeOffset { get; set; }
}
