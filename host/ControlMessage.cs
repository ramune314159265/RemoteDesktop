using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemoteDesktopHost;

/// <summary>
/// フロントエンドから DataChannel 経由で送られてくる制御メッセージ。
/// マウス/キーボード用の最低限のフィールドのみ。今後タッチ等を追加する際は
/// ここにフィールドを増やしていく。
/// </summary>
internal sealed class ControlMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>mouseMove 用。"absolute" | "relative"</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>absolute 移動用、0.0〜1.0 に正規化された座標。</summary>
    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    /// <summary>relative 移動量、またはホイールのデルタ値。</summary>
    [JsonPropertyName("dx")]
    public int? Dx { get; set; }

    [JsonPropertyName("dy")]
    public int? Dy { get; set; }

    /// <summary>mouseButton 用。"left" | "right" | "middle"</summary>
    [JsonPropertyName("button")]
    public string? Button { get; set; }

    /// <summary>mouseButton 用。"down" | "up"</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>keyDown/keyUp 用。ブラウザの KeyboardEvent.code。</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>setBitrate 用。x264enc の bitrate プロパティ(kbit/s)にそのまま渡す。</summary>
    [JsonPropertyName("bitrateKbps")]
    public uint? BitrateKbps { get; set; }

    /// <summary>setMute 用。</summary>
    [JsonPropertyName("muted")]
    public bool? Muted { get; set; }

    /// <summary>switchSource 用。"monitor" | "window"</summary>
    [JsonPropertyName("sourceMode")]
    public string? SourceMode { get; set; }

    /// <summary>switchSource (mode="monitor") 用。</summary>
    [JsonPropertyName("monitorIndex")]
    public int? MonitorIndex { get; set; }

    /// <summary>switchSource (mode="window") 用。HWND の値。</summary>
    [JsonPropertyName("windowHandle")]
    public ulong? WindowHandle { get; set; }

    /// <summary>
    /// touch 用。同時に変化した複数のタッチ点。
    /// InjectSyntheticPointerInput 側は「現在アクティブな全タッチ」を毎回まとめて
    /// 送る必要があるため、ここには1回のネイティブタッチイベント(touchstart/move/end/cancel)で
    /// 変化した点だけを入れて送る想定(ホスト側で現在の全状態を組み立て直す)。
    /// </summary>
    [JsonPropertyName("touches")]
    public List<TouchPointDto>? Touches { get; set; }

    /// <summary>setViewportSize 用。クライアントのvideo要素の実サイズ(devicePixelRatio考慮済み、デバイスピクセル単位)。</summary>
    [JsonPropertyName("width")]
    public int? ViewportWidth { get; set; }

    [JsonPropertyName("height")]
    public int? ViewportHeight { get; set; }

    public sealed class TouchPointDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>0.0〜1.0 に正規化された座標。</summary>
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        /// <summary>"down" | "move" | "up" | "cancel"</summary>
        [JsonPropertyName("phase")]
        public string Phase { get; set; } = "";
    }
}
