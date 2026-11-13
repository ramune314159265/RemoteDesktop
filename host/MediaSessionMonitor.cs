using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace RemoteDesktopHost;

/// <summary>
/// ホスト PC のメディア再生状態(Windows のシステムメディア コントロールと同じ情報)を監視し、
/// 接続中のクライアントへ配信する。クライアントからの再生・一時停止・曲送り・シークも受け付ける。
/// </summary>
internal sealed class MediaSessionMonitor
{
    private sealed class SessionEntry
    {
        public required string Id { get; init; }
        public required GlobalSystemMediaTransportControlsSession Session { get; init; }
        public Action? Detach { get; set; }
        public string ThumbKey { get; set; } = "";
        public string? ThumbJson { get; set; }
    }

    private const uint ThumbnailMaxSize = 200;
    private static readonly TimeSpan PublishDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PeriodicPublishInterval = TimeSpan.FromSeconds(10);

    public static MediaSessionMonitor Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, SessionEntry> _entries = new();
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Timer? _publishTimer;
    private Timer? _periodicTimer;
    private event Action<string>? MessagePublished;
    private string _lastStateJson = "";
    private bool _forceNextPublish;

    /// <summary>送信先を登録し、現在の状態(サムネイル含む)を送る。初回は監視も開始する。</summary>
    public async Task AttachAsync(Action<string> handler)
    {
        await EnsureStartedAsync();
        if (_manager is null) return;

        lock (_lock) { MessagePublished += handler; }

        await PublishAsync(force: true, onlyTo: handler);
    }

    public void Detach(Action<string> handler)
    {
        lock (_lock) { MessagePublished -= handler; }
    }

    private async Task EnsureStartedAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_manager is not null) return;
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                manager.SessionsChanged += (_, _) => RefreshSessions();
                manager.CurrentSessionChanged += (_, _) => SchedulePublish();
                _manager = manager;
                RefreshSessions();
                _periodicTimer = new Timer(_ => SchedulePublish(), null, PeriodicPublishInterval, PeriodicPublishInterval);
                Console.WriteLine("[Media] メディア再生情報の監視を開始しました");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Media] メディア再生情報を取得できません: {ex.Message}");
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void RefreshSessions()
    {
        if (_manager is null) return;
        try
        {
            var sessions = _manager.GetSessions();
            var ids = new HashSet<string>(sessions.Select(s => s.SourceAppUserModelId));

            lock (_lock)
            {
                foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    _entries[id].Detach?.Invoke();
                    _entries.Remove(id);
                }

                foreach (var session in sessions)
                {
                    var id = session.SourceAppUserModelId;
                    if (_entries.ContainsKey(id)) continue;
                    _entries[id] = CreateEntry(id, session);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Media] セッション一覧の更新に失敗: {ex.Message}");
        }
        SchedulePublish();
    }

    private SessionEntry CreateEntry(string id, GlobalSystemMediaTransportControlsSession session)
    {
        var entry = new SessionEntry { Id = id, Session = session };

        TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProperties = (_, _) => SchedulePublish();
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlayback = (_, _) => SchedulePublish();
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTimeline = (_, _) => SchedulePublish();

        session.MediaPropertiesChanged += onProperties;
        session.PlaybackInfoChanged += onPlayback;
        session.TimelinePropertiesChanged += onTimeline;
        entry.Detach = () =>
        {
            session.MediaPropertiesChanged -= onProperties;
            session.PlaybackInfoChanged -= onPlayback;
            session.TimelinePropertiesChanged -= onTimeline;
        };
        return entry;
    }

    /// <summary>短時間に続けて届く変更通知をまとめて1回の配信にする。</summary>
    private void SchedulePublish()
    {
        lock (_lock)
        {
            _publishTimer?.Dispose();
            _publishTimer = new Timer(_ =>
            {
                var task = PublishAsync(force: false, onlyTo: null);
            }, null, PublishDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task PublishAsync(bool force, Action<string>? onlyTo)
    {
        await _publishLock.WaitAsync();
        try
        {
            List<SessionEntry> entries;
            lock (_lock) { entries = _entries.Values.ToList(); }

            var sessions = new List<object>();
            foreach (var entry in entries)
            {
                var item = await DescribeAsync(entry);
                if (item is not null) sessions.Add(item);
            }

            var currentId = _manager?.GetCurrentSession()?.SourceAppUserModelId;
            var stateJson = JsonSerializer.Serialize(new { type = "mediaState", sessions, current = currentId });

            lock (_lock)
            {
                var forced = force || _forceNextPublish;
                _forceNextPublish = false;
                if (onlyTo is null && !forced && stateJson == _lastStateJson) return;
                _lastStateJson = stateJson;
            }

            Dispatch(stateJson, onlyTo);
            if (onlyTo is not null)
            {
                foreach (var entry in entries)
                {
                    var thumb = entry.ThumbJson;
                    if (thumb is not null) onlyTo(thumb);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Media] 状態の配信に失敗: {ex.Message}");
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private void Dispatch(string json, Action<string>? onlyTo)
    {
        if (onlyTo is not null)
        {
            SafeInvoke(onlyTo, json);
            return;
        }

        Action<string>? handlers;
        lock (_lock) { handlers = MessagePublished; }
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            SafeInvoke(handler, json);
        }
    }

    private static void SafeInvoke(Action<string> handler, string json)
    {
        try { handler(json); }
        catch (Exception ex) { Console.WriteLine($"[Media] 送信に失敗: {ex.Message}"); }
    }

    private async Task<object?> DescribeAsync(SessionEntry entry)
    {
        try
        {
            var session = entry.Session;
            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback.Controls;

            var status = playback.PlaybackStatus;
            var rate = playback.PlaybackRate ?? 1.0;
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                position += (DateTimeOffset.Now - timeline.LastUpdatedTime) * rate;
            }
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            var thumbKey = $"{properties.Title}|{properties.Artist}|{properties.AlbumTitle}";
            if (thumbKey != entry.ThumbKey)
            {
                entry.ThumbKey = thumbKey;
                entry.ThumbJson = null;
                var task = LoadThumbnailAsync(entry, properties.Thumbnail, thumbKey);
            }

            return new
            {
                id = entry.Id,
                app = entry.Id,
                title = properties.Title,
                artist = properties.Artist,
                album = properties.AlbumTitle,
                status = status.ToString().ToLowerInvariant(),
                canPlay = controls.IsPlayEnabled,
                canPause = controls.IsPauseEnabled,
                canNext = controls.IsNextEnabled,
                canPrevious = controls.IsPreviousEnabled,
                canSeek = controls.IsPlaybackPositionEnabled,
                positionMs = (long)position.TotalMilliseconds,
                durationMs = duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : 0,
                rate,
                thumbKey,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Media] セッション情報の取得に失敗 ({entry.Id}): {ex.Message}");
            return null;
        }
    }

    private async Task LoadThumbnailAsync(SessionEntry entry, IRandomAccessStreamReference? reference, string thumbKey)
    {
        string? image = null;
        try
        {
            if (reference is not null) image = await EncodeThumbnailAsync(reference);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Media] サムネイルの取得に失敗: {ex.Message}");
        }

        if (entry.ThumbKey != thumbKey) return;

        var json = JsonSerializer.Serialize(new { type = "mediaThumbnail", id = entry.Id, thumbKey, image });
        entry.ThumbJson = json;
        Dispatch(json, null);
    }

    /// <summary>サムネイルを縮小して JPEG の data URL にする(DataChannel の送信サイズを抑えるため)。</summary>
    private static async Task<string> EncodeThumbnailAsync(IRandomAccessStreamReference reference)
    {
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);

        var scale = Math.Min(1.0, (double)ThumbnailMaxSize / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
    }

    /// <summary>クライアントからの操作(play / pause / toggle / next / previous / seek)を実行する。</summary>
    public async Task ControlAsync(string? sessionId, string? action, double? positionMs)
    {
        try
        {
            SessionEntry? entry;
            lock (_lock) { _entries.TryGetValue(sessionId ?? "", out entry); }
            if (entry is null) return;

            var session = entry.Session;
            switch (action)
            {
                case "play": await session.TryPlayAsync(); break;
                case "pause": await session.TryPauseAsync(); break;
                case "toggle": await session.TryTogglePlayPauseAsync(); break;
                case "next": await session.TrySkipNextAsync(); break;
                case "previous": await session.TrySkipPreviousAsync(); break;
                case "seek" when positionMs is { } ms:
                    var start = session.GetTimelineProperties().StartTime;
                    await session.TryChangePlaybackPositionAsync((start + TimeSpan.FromMilliseconds(ms)).Ticks);
                    break;
                default:
                    Console.WriteLine($"[Media] 未対応の操作: {action}");
                    return;
            }
            lock (_lock) { _forceNextPublish = true; }
            SchedulePublish();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Media] 操作に失敗 ({action}): {ex.Message}");
        }
    }
}
