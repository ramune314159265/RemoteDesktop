using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesktopHost;

/// <summary>
/// 実行ファイルと同じディレクトリの settings.json から読み込む起動設定。
/// 起動引数と同じ内容を指定でき、同じ項目を起動引数でも指定した場合は起動引数が優先される。
/// 全項目省略可能。コメント(// や /* */)と末尾カンマも許可する。
///
/// 例:
/// {
///   "prefix": "http://+:8787/",
///   "wssPort": 8788,
///   "cert": "C:\\certs\\host.pfx",
///   "certPassword": "secret",
///   "stun": "stun://stun.l.google.com:19302",
///   "fps": 60,
///   "codec": "h264",
///   "bitrate": 6000,
///   "windowMonitor": "DELL U2720Q"
/// }
/// </summary>
internal sealed class HostSettings
{
    /// <summary>待ち受け URL プレフィックス (--prefix)。</summary>
    public string? Prefix { get; set; }

    /// <summary>STUN サーバ (--stun)。</summary>
    public string? Stun { get; set; }

    /// <summary>true で videotestsrc/audiotestsrc を使う (--test-source)。</summary>
    public bool? TestSource { get; set; }

    /// <summary>既定のフレームレート (--fps)。</summary>
    public int? Fps { get; set; }

    /// <summary>既定のコーデック "h264" / "hevc" (--codec)。</summary>
    public string? Codec { get; set; }

    /// <summary>既定の映像ビットレート kbps (--bitrate)。</summary>
    public uint? Bitrate { get; set; }

    /// <summary>
    /// ウィンドウ配信時にウィンドウを移動するモニター (--window-monitor)。
    /// 番号(0始まり)またはモニター名(例: "DELL U2720Q")を指定できる。
    /// 名前は部分一致で、同名モニターが複数ある場合は "名前#2" のように番号を付ける。
    /// </summary>
    public JsonElement? WindowMonitor { get; set; }

    /// <summary>wss(TLS) の待ち受けポート (--wss-port)。0/未指定で wss 無効。</summary>
    public int? WssPort { get; set; }

    /// <summary>TLS 証明書(PFX/PEM)のパス (--cert)。</summary>
    public string? Cert { get; set; }

    /// <summary>証明書のパスワード (--cert-password)。</summary>
    public string? CertPassword { get; set; }

    /// <summary>PEM 秘密鍵のパス (--cert-key)。</summary>
    public string? CertKey { get; set; }

    public static HostSettings Load(string? explicitPath)
    {
        var path = !string.IsNullOrEmpty(explicitPath)
            ? explicitPath
            : Path.Combine(AppContext.BaseDirectory, "settings.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"[Settings] 設定ファイルは見つかりませんでした (使用しません): {path}");
            return new HostSettings();
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var loaded = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path), options) ?? new HostSettings();
            Console.WriteLine($"[Settings] 設定ファイルを読み込みました: {path}");
            return loaded;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings][ERROR] 設定ファイルの読み込みに失敗したため無視します: {path}");
            Console.WriteLine($"  詳細: {ex.Message}");
            return new HostSettings();
        }
    }
}
