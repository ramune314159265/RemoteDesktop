using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace RemoteDesktopHost;

internal enum ApprovalResult
{
    Approved,
    Denied,
    Busy,
}

/// <summary>
/// 端末認証。未登録の端末はホスト PC にトースト通知を出して承認を求め、承認された端末には
/// ランダムなトークンを発行する。トークンはブラウザ側に保存され、次回以降はそれを送るだけで認証できる。
/// ホスト側にはトークンのハッシュだけを保存する。
/// </summary>
internal sealed class DeviceAuthorizer
{
    private sealed class DeviceRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("tokenHash")] public string TokenHash { get; set; } = "";
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
        [JsonPropertyName("lastSeenAt")] public DateTime LastSeenAt { get; set; }
        [JsonPropertyName("remoteAddress")] public string RemoteAddress { get; set; } = "";
    }

    private const int MaxDenialsBeforeLock = 3;
    private static readonly TimeSpan DenialWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AuthMessageTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<DeviceRecord> _devices;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly Dictionary<string, List<DateTime>> _denials = new();
    private bool _approvalInProgress;

    public DeviceAuthorizer(string path)
    {
        _path = path;
        _devices = Load(path);
        Console.WriteLine($"[Auth] 登録済み端末: {_devices.Count} 台 ({path})");
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        try { ToastNotificationManagerCompat.History.Clear(); }
        catch (Exception ex) { Console.WriteLine($"[Auth] 古い通知の削除に失敗: {ex.Message}"); }
    }

    /// <summary>
    /// 接続直後のクライアントに認証を要求する。認証できたら onAuthenticated を呼ぶ。
    /// 認証できない場合は接続を閉じる。
    /// </summary>
    public void Attach(SignalingClient client, Action onAuthenticated)
    {
        var cts = new CancellationTokenSource();
        var state = 0;

        var timeout = new Timer(_ =>
        {
            if (Volatile.Read(ref state) != 0) return;
            Console.WriteLine("[Auth] 認証メッセージが届かないため接続を閉じます");
            cts.Cancel();
            _ = client.CloseAsync("auth timeout");
        }, null, AuthMessageTimeout, Timeout.InfiniteTimeSpan);

        client.MessageReceived += json =>
        {
            if (Volatile.Read(ref state) != 0) return;

            SignalMessage? msg;
            try { msg = JsonSerializer.Deserialize<SignalMessage>(json); }
            catch (JsonException) { return; }
            if (msg is null || msg.Type != "auth") return;
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;

            timeout.Dispose();
            var task = AuthenticateAsync(client, msg.Token, cts.Token, () =>
            {
                Volatile.Write(ref state, 2);
                onAuthenticated();
            });
        };

        client.Disconnected += () =>
        {
            timeout.Dispose();
            if (Volatile.Read(ref state) != 2) cts.Cancel();
        };
    }

    private async Task AuthenticateAsync(SignalingClient client, string? token, CancellationToken ct, Action onAuthenticated)
    {
        try
        {
            if (TryValidateToken(token, client.RemoteAddress, out var deviceName))
            {
                Console.WriteLine($"[Auth] 認証済み端末が接続しました: {deviceName}");
                await client.SendAsync(JsonSerializer.Serialize(new { type = "authOk" }));
                onAuthenticated();
                return;
            }

            var name = DescribeUserAgent(client.UserAgent);
            if (IsLockedOut(client.RemoteAddress))
            {
                Console.WriteLine($"[Auth] 拒否が続いたため要求を受け付けません: {client.RemoteAddress}");
                await client.SendAsync(JsonSerializer.Serialize(new { type = "authDenied", reason = "locked" }));
                await client.CloseAsync("locked");
                return;
            }

            await client.SendAsync(JsonSerializer.Serialize(new { type = "authPending" }));
            var (result, newToken) = await RequestApprovalAsync(name, client.RemoteAddress, ct);

            if (ct.IsCancellationRequested) return;

            if (result == ApprovalResult.Approved)
            {
                await client.SendAsync(JsonSerializer.Serialize(new { type = "authOk", token = newToken }));
                onAuthenticated();
                return;
            }

            await client.SendAsync(JsonSerializer.Serialize(new
            {
                type = "authDenied",
                reason = result == ApprovalResult.Busy ? "busy" : "denied",
            }));
            await client.CloseAsync("denied");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] 認証処理でエラー: {ex}");
            await client.CloseAsync("auth error");
        }
    }

    private bool TryValidateToken(string? token, string remoteAddress, out string deviceName)
    {
        deviceName = "";
        if (string.IsNullOrEmpty(token)) return false;

        var hash = HashToken(token);
        lock (_lock)
        {
            foreach (var device in _devices)
            {
                if (!FixedTimeHexEquals(device.TokenHash, hash)) continue;
                device.LastSeenAt = DateTime.Now;
                device.RemoteAddress = remoteAddress;
                deviceName = device.Name;
                Save();
                return true;
            }
        }
        return false;
    }

    private async Task<(ApprovalResult Result, string? Token)> RequestApprovalAsync(string deviceName, string remoteAddress, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_approvalInProgress) return (ApprovalResult.Busy, null);
            _approvalInProgress = true;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            Console.WriteLine($"[Auth] 承認を要求します: {deviceName} ({remoteAddress})");
            ShowApprovalToast(requestId, deviceName, remoteAddress, tcs);

            using var registration = ct.Register(() => tcs.TrySetCanceled());
            bool approved;
            try { approved = await tcs.Task; }
            catch (OperationCanceledException) { return (ApprovalResult.Denied, null); }

            if (!approved)
            {
                RecordDenial(remoteAddress);
                Console.WriteLine("[Auth] 拒否されました");
                return (ApprovalResult.Denied, null);
            }

            var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            AddDevice(token, deviceName, remoteAddress);
            Console.WriteLine($"[Auth] 端末を登録しました: {deviceName}");
            return (ApprovalResult.Approved, token);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            try { ToastNotificationManagerCompat.History.Remove(requestId); }
            catch { }
            lock (_lock) { _approvalInProgress = false; }
        }
    }

    /// <summary>自動では閉じないトースト通知(Reminder)で、許可/拒否を尋ねる。</summary>
    private static void ShowApprovalToast(string requestId, string deviceName, string remoteAddress, TaskCompletionSource<bool> tcs)
    {
        new ToastContentBuilder()
            .AddText("新しい端末が接続を求めています")
            .AddText($"{deviceName}  /  {remoteAddress}")
            .AddText("許可すると、この端末から画面の閲覧と操作ができるようになります。")
            .SetToastScenario(ToastScenario.Reminder)
            .AddButton(new ToastButton()
                .SetContent("許可")
                .AddArgument("requestId", requestId)
                .AddArgument("decision", "allow")
                .SetBackgroundActivation())
            .AddButton(new ToastButton()
                .SetContent("拒否")
                .AddArgument("requestId", requestId)
                .AddArgument("decision", "deny")
                .SetBackgroundActivation())
            .Show(toast =>
            {
                toast.Tag = requestId;
                toast.Dismissed += (_, e) =>
                {
                    if (e.Reason == ToastDismissalReason.UserCanceled) tcs.TrySetResult(false);
                };
            });
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue("requestId", out var requestId)) return;
            if (!_pending.TryGetValue(requestId, out var tcs)) return;
            tcs.TrySetResult(args.TryGetValue("decision", out var decision) && decision == "allow");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] 通知の操作の処理に失敗: {ex.Message}");
        }
    }

    private void AddDevice(string token, string name, string remoteAddress)
    {
        lock (_lock)
        {
            _devices.Add(new DeviceRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                TokenHash = HashToken(token),
                CreatedAt = DateTime.Now,
                LastSeenAt = DateTime.Now,
                RemoteAddress = remoteAddress,
            });
            Save();
        }
    }

    private bool IsLockedOut(string remoteAddress)
    {
        lock (_lock)
        {
            if (!_denials.TryGetValue(remoteAddress, out var times)) return false;
            times.RemoveAll(t => DateTime.UtcNow - t > DenialWindow);
            return times.Count >= MaxDenialsBeforeLock;
        }
    }

    private void RecordDenial(string remoteAddress)
    {
        lock (_lock)
        {
            if (!_denials.TryGetValue(remoteAddress, out var times))
            {
                times = new List<DateTime>();
                _denials[remoteAddress] = times;
            }
            times.Add(DateTime.UtcNow);
        }
    }

    private static List<DeviceRecord> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<DeviceRecord>();
            return JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(path)) ?? new List<DeviceRecord>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] 登録済み端末の読み込みに失敗: {ex.Message}");
            return new List<DeviceRecord>();
        }
    }

    /// <summary>呼び出し側で _lock を取得していること。</summary>
    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_devices, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] 登録済み端末の保存に失敗: {ex.Message}");
        }
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool FixedTimeHexEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DescribeUserAgent(string? userAgent)
    {
        var ua = userAgent ?? "";
        var os = ua.Contains("iPhone") ? "iPhone"
            : ua.Contains("iPad") ? "iPad"
            : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS X") ? "Mac"
            : ua.Contains("Linux") ? "Linux"
            : "不明な端末";
        var browser = ua.Contains("Edg/") ? "Edge"
            : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari"
            : "不明なブラウザ";
        return $"{os} / {browser}";
    }
}
