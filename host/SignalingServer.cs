using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace RemoteDesktopHost;

/// <summary>シグナリングサーバの TLS(wss) 設定。Port が 0 以下なら wss は無効。</summary>
internal sealed class WssOptions
{
    public int Port { get; init; }
    /// <summary>PFX もしくは PEM 証明書のパス。未指定なら自己署名証明書を自動生成/再利用する。</summary>
    public string? CertPath { get; init; }
    public string? CertPassword { get; init; }
    /// <summary>PEM 形式の秘密鍵パス (CertPath が PEM の場合のみ)。</summary>
    public string? KeyPath { get; init; }
}

/// <summary>
/// 別オリジンのフロントエンド(HTML/JS)から接続される、最低限の WebSocket シグナリングサーバ。
/// Kestrel 上で動作し、ws://(HTTP) に加えて任意で wss://(HTTPS) も同時に待ち受けられる。
/// WebSocket のハンドシェイクはブラウザの CORS プリフライトの対象外。
/// </summary>
internal sealed class SignalingServer
{
    private readonly string _wsPath;
    private readonly string _prefix;
    private readonly WssOptions? _wss;
    private WebApplication? _app;

    /// <summary>新規クライアント接続時に呼ばれる。同時接続は1つのみを想定。</summary>
    public Action<SignalingClient>? ClientConnected;

    public SignalingServer(string prefix, WssOptions? wss = null, string wsPath = "/signaling")
    {
        _wsPath = wsPath;
        _prefix = prefix;
        _wss = wss is { Port: > 0 } ? wss : null;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var uri = new Uri(_prefix.Replace("+", "0.0.0.0").Replace("*", "0.0.0.0"));
        var wsPort = uri.Port;

        X509Certificate2? cert = null;
        if (_wss is not null)
        {
            cert = LoadOrCreateCertificate(_wss);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            void Bind(Action<IPAddress> any, Action<IPAddress> specific)
            {
                if (uri.Host == "0.0.0.0") any(IPAddress.Any);
                else if (uri.Host == "localhost") specific(IPAddress.Loopback);
                else specific(IPAddress.Parse(uri.Host));
            }

            Bind(
                _ => k.ListenAnyIP(wsPort),
                ip => k.Listen(ip, wsPort));

            if (_wss is not null && cert is not null)
            {
                Bind(
                    _ => k.ListenAnyIP(_wss.Port, o => o.UseHttps(cert)),
                    ip => k.Listen(ip, _wss.Port, o => o.UseHttps(cert)));
            }
        });

        var app = builder.Build();
        _app = app;
        // KeepAlive の Ping で、電源断・Wi-Fi切断など「無言で切れた」接続を検出する
        // (放置すると古い接続が残り、再接続が "busy" で拒否される原因になる)
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Run(HandleContextAsync);

        try
        {
            await app.StartAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Signaling][FATAL] サーバの起動に失敗しました (ポート使用中など)。");
            Console.WriteLine($"  詳細: {ex.Message}");
            throw;
        }

        Console.WriteLine($"[Signaling] Listening on ws port {wsPort}" +
                          (_wss is not null ? $", wss port {_wss.Port}" : " (wss: 無効)"));
        PrintReachableUrls(wsPort);
        Console.WriteLine("[Signaling] 別デバイスから接続できない場合は、Windows ファイアウォールで" +
                           "このポートへの着信 (TCP) が許可されているか確認してください。");
        Console.WriteLine("  例: New-NetFirewallRule -DisplayName \"RemoteDesktopHost\" " +
                           "-Direction Inbound -Action Allow -Protocol TCP -LocalPort <port>");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>証明書を読み込む。未指定なら自己署名証明書を selfsigned.pfx に作成/再利用する。</summary>
    private static X509Certificate2 LoadOrCreateCertificate(WssOptions o)
    {
        if (!string.IsNullOrEmpty(o.CertPath))
        {
            Console.WriteLine($"[Signaling] TLS 証明書を読み込みます: {o.CertPath}");
            X509Certificate2 loaded;
            if (!string.IsNullOrEmpty(o.KeyPath))
            {
                loaded = o.CertPassword is null
                    ? X509Certificate2.CreateFromPemFile(o.CertPath, o.KeyPath)
                    : X509Certificate2.CreateFromEncryptedPemFile(o.CertPath, o.CertPassword, o.KeyPath);
            }
            else
            {
                loaded = new X509Certificate2(o.CertPath, o.CertPassword);
            }
            // Windows(SChannel) では PEM 由来の鍵はエフェメラルだと TLS で使えないため再インポートする
            return new X509Certificate2(loaded.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        }

        var pfxPath = Path.Combine(AppContext.BaseDirectory, "selfsigned.pfx");
        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.Now.AddDays(7))
                {
                    Console.WriteLine($"[Signaling] 自己署名証明書を再利用します: {pfxPath}");
                    return existing;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Signaling] 既存の自己署名証明書を読めないため再生成します: {ex.Message}");
            }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=RemoteDesktopHost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var ip in GetLocalIPv4Addresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = created.Export(X509ContentType.Pfx);
        try { File.WriteAllBytes(pfxPath, pfx); }
        catch (Exception ex) { Console.WriteLine($"[Signaling] 自己署名証明書の保存に失敗(次回再生成されます): {ex.Message}"); }

        Console.WriteLine($"[Signaling] 自己署名証明書を生成しました: {pfxPath}");
        Console.WriteLine("  ブラウザでは最初に https://<host>:<wssポート>/ を開いて証明書の警告を承認してください。");
        Console.WriteLine("  (信頼された証明書を使う場合は --cert / --cert-password / --cert-key を指定)");
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .ToList();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>このマシンが持つ IPv4 アドレス一覧を表示する(接続トラブルの切り分け用)。</summary>
    private void PrintReachableUrls(int wsPort)
    {
        Console.WriteLine("[Signaling] このマシンで接続に使える可能性のあるアドレス:");
        foreach (var addr in GetLocalIPv4Addresses())
        {
            Console.WriteLine($"    ws://{addr}:{wsPort}{_wsPath}");
            if (_wss is not null) Console.WriteLine($"    wss://{addr}:{_wss.Port}{_wsPath}");
        }
    }

    private async Task HandleContextAsync(HttpContext ctx)
    {
        if (ctx.Request.Path != _wsPath)
        {
            // WebSocket アップグレード対象外(ヘルスチェック/OPTIONS)の場合のみ
            // CORS ヘッダーを付与する(101 応答に混ぜると Safari が拒否することがある)。
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";

            if (ctx.Request.Method == "OPTIONS")
            {
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
                ctx.Response.StatusCode = 204;
                return;
            }

            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("Remote Desktop Host signaling server is running.");
            return;
        }

        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var queryString = ctx.Request.QueryString.Value;
        var queryParams = ParseQuery(queryString);
        var client = new SignalingClient(socket, queryParams);
        var scheme = ctx.Request.IsHttps ? "wss" : "ws";
        Console.WriteLine($"[Signaling] Client connected via {scheme}" + (queryParams.Count > 0 ? $" (query: {queryString})" : ""));
        ClientConnected?.Invoke(client);
        await client.RunReceiveLoopAsync();
        Console.WriteLine("[Signaling] Client disconnected");
    }

    /// <summary>
    /// WebSocket接続URLのクエリ文字列を素朴にパースする。
    /// 例: ws://host:port/signaling?fps=60&amp;codec=hevc
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;

        query = query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                result[Uri.UnescapeDataString(pair)] = "";
                continue;
            }
            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = value;
        }
        return result;
    }

    public void Stop() => _app?.StopAsync().GetAwaiter().GetResult();
}

/// <summary>1つの WebSocket 接続の送受信をラップする。</summary>
internal sealed class SignalingClient
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private const int MaxMessageChars = 1024 * 1024;

    public Action<string>? MessageReceived;
    public Action? Disconnected;

    /// <summary>接続URLのクエリパラメータ(例: ?fps=60&amp;codec=hevc)。</summary>
    public IReadOnlyDictionary<string, string> QueryParams { get; }

    public SignalingClient(WebSocket socket, IReadOnlyDictionary<string, string>? queryParams = null)
    {
        _socket = socket;
        QueryParams = queryParams ?? new Dictionary<string, string>();
    }

    public async Task SendAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] Send failed: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task RunReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (messageBuilder.Length > MaxMessageChars)
                {
                    Console.WriteLine("[Signaling] Message too large, closing connection");
                    await _socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too large", CancellationToken.None);
                    break;
                }
                if (result.EndOfMessage)
                {
                    var msg = messageBuilder.ToString();
                    messageBuilder.Clear();
                    try
                    {
                        MessageReceived?.Invoke(msg);
                    }
                    catch (Exception ex)
                    {
                        // 1メッセージの処理失敗で接続全体(=配信セッション)を落とさない
                        Console.WriteLine($"[Signaling] MessageReceived handler error: {ex}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] Receive loop error: {ex.Message}");
        }
        finally
        {
            try { Disconnected?.Invoke(); }
            catch (Exception ex) { Console.WriteLine($"[Signaling] Disconnected handler error: {ex}"); }
        }
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] CloseAsync error: {ex.Message}");
        }
    }
}
