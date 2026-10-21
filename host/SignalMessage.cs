using System.Text.Json.Serialization;

namespace RemoteDesktopHost;

internal sealed class SignalMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("sdp")]
    public string? Sdp { get; set; }

    [JsonPropertyName("candidate")]
    public string? Candidate { get; set; }

    [JsonPropertyName("sdpMLineIndex")]
    public uint? SdpMLineIndex { get; set; }
}
