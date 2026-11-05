using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Gst;
using Task = System.Threading.Tasks.Task;
using DateTime = System.DateTime;
using Timer = System.Threading.Timer;

namespace RemoteDesktopHost;

/// <summary>
/// クライアント1接続につき1つのパイプライン(プライマリモニタ映像 + ループバック音声)を
/// 立ち上げ、webrtcbin のネゴシエーションを仲介する。
/// </summary>
internal sealed class WebRtcSession : IDisposable
{
    private readonly SignalingClient _client;
    private readonly HostConfig _config;

    private Pipeline? _pipeline;
    private IntPtr _webrtcHandle;

    private GstNativeInterop.NegotiationNeededCallback? _onNegotiationNeeded;
    private GstNativeInterop.IceCandidateCallback? _onIceCandidate;
    private GstNativeInterop.PromiseChangeFunc? _onOfferCreated;
    private GstNativeInterop.DataChannelSimpleCallback? _onControlChannelOpen;
    private GstNativeInterop.DataChannelSimpleCallback? _onControlChannelClose;
    private GstNativeInterop.DataChannelMessageStringCallback? _onControlChannelMessage;

    private IntPtr _controlChannelHandle;
    private bool _isControlChannelOpen;
    private IntPtr _videoEncoderHandle;
    private IntPtr _volumeHandle;
    private IntPtr _captureSourceHandle;

    private IntPtr _videoSelectorHandle;
    private Pad? _captureSelPad;
    private Pad? _blackSelPad;
    private bool _burstActive;
    private bool _videoDisabled;
    private volatile int _captureWidth;
    private volatile int _captureHeight;
    private int _currentMonitorIndex;
    private Element? _captureSourceElement;
    private Element? _videoQueue1Element;
    private Element? _videoEncoderElement;
    private Element? _videoPayloaderElement;
    private PlayoutDelayExtension? _playoutDelay;
    private int _offeredPlayoutExtId;
    private Element? _videoQueue2Element;
    private Element? _videoConvertElement;
    private Element? _videoCropElement;
    private Element? _videoConvert2Element;

    private GstNativeInterop.NotifyCallback? _onVideoCapsNotify;
    private IntPtr _videoCropHandle;

    /// <summary>
    /// ウィンドウ配信時にタイトルバー分として videocrop の "top" に設定している
    /// ピクセル数。OnVideoCapsNotify 側で「top クロップ後の高さ」が偶数になるよう
    /// bottom を調整するために参照する。</summary>
    private int _videoCropTopPixels;

    private uint _currentBitrateKbps;

    private DateTime _lastVideoBranchRecovery = DateTime.MinValue;
    private static readonly TimeSpan VideoBranchRecoveryDebounce = TimeSpan.FromSeconds(2);

    private Timer? _resizeWatchTimer;
    private IntPtr? _currentWindowHandle;
    private ScreenGeometry.Rect? _lastKnownWindowRect;
    private static readonly TimeSpan ResizeWatchInterval = TimeSpan.FromMilliseconds(500);

    private ScreenGeometry.Rect? _originalWindowRect;
    private (int Width, int Height)? _lastClientRequestedSize;
    private (int Width, int Height)? _lastRequestedWindowSize;
    private (int Width, int Height)? _independentWindowSize;
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _controlWork = new();
    private readonly object _viewportLock = new();
    private (int Width, int Height)? _pendingViewport;
    private const int WindowSizeTolerance = 2;
    private const int MaxResizeAttempts = 3;
    private const int ResizeSettleDelayMs = 120;

    private IntPtr? _trackedWindowHandle;
    private int _originalStyleBits;
    private bool _stripSucceeded;
    private readonly object _windowLock = new();
    private bool _windowStateDisposed;
    private int _styleStripAttempts;
    private const int MaxStyleStripAttempts = 2;

    public WebRtcSession(SignalingClient client, HostConfig config)
    {
        _client = client;
        _config = config;
        _currentBitrateKbps = config.BitrateKbps;
        _client.MessageReceived += OnSignalingMessage;
    }

    public void Start()
    {
        var pipelineDesc = BuildPipelineDescription(_config);
        Console.WriteLine("[Session] Launching pipeline:");
        Console.WriteLine(pipelineDesc);

        var element = Parse.Launch(pipelineDesc);
        _pipeline = (Pipeline)element;

        var webrtcbin = _pipeline.GetByName("sendrecv")
            ?? throw new InvalidOperationException("webrtcbin element 'sendrecv' not found in pipeline");
        _webrtcHandle = webrtcbin.Handle;

        _videoEncoderHandle = _pipeline.GetByName("venc")?.Handle ?? IntPtr.Zero;
        _volumeHandle = _pipeline.GetByName("vol")?.Handle ?? IntPtr.Zero;
        _captureSourceElement = _pipeline.GetByName("capturesrc");
        _captureSourceHandle = _captureSourceElement?.Handle ?? IntPtr.Zero;
        _videoQueue1Element = _pipeline.GetByName("vqueue1");
        _videoEncoderElement = _pipeline.GetByName("venc");
        _videoPayloaderElement = _pipeline.GetByName("vpay");
        _videoQueue2Element = _pipeline.GetByName("vqueue2");
        _videoConvertElement = _pipeline.GetByName("vconv");
        _videoCropElement = _pipeline.GetByName("vcrop");
        _videoConvert2Element = _pipeline.GetByName("vconv2");
        _videoCropHandle = _videoCropElement?.Handle ?? IntPtr.Zero;

        _videoSelectorHandle = _pipeline.GetByName("vsel")?.Handle ?? IntPtr.Zero;
        _captureSelPad = _pipeline.GetByName("capdl")?.GetStaticPad("src")?.Peer;
        _blackSelPad = _pipeline.GetByName("blackcaps")?.GetStaticPad("src")?.Peer;

        _burstActive = _config.StartupBurstMs > 0 && _config.EnablePlayoutDelay && !_config.UseTestSource;

        var vcropSinkPad = _videoCropElement?.GetStaticPad("sink");
        if (vcropSinkPad is not null)
        {
            _onVideoCapsNotify = OnVideoCapsNotify;
            GstNativeInterop.ConnectNotifyCaps(vcropSinkPad.Handle, _onVideoCapsNotify);
        }

        _onNegotiationNeeded = OnNegotiationNeeded;
        _onIceCandidate = OnIceCandidate;
        GstNativeInterop.ConnectNegotiationNeeded(_webrtcHandle, _onNegotiationNeeded);
        GstNativeInterop.ConnectIceCandidate(_webrtcHandle, _onIceCandidate);

        _pipeline.Bus.AddSignalWatch();
        _pipeline.Bus.Message += OnBusMessage;

        _pipeline.SetState(State.Playing);
        Console.WriteLine("[Session] Pipeline is PLAYING");

        InputInjector.SetCaptureRegion(ScreenGeometry.GetMonitorRect(0) ?? ScreenGeometry.PrimaryMonitorFallback);

        StartControlWorker();
        CreateControlDataChannel();

        _resizeWatchTimer = new Timer(OnResizeWatchTick, null, ResizeWatchInterval, ResizeWatchInterval);
    }

    /// <summary>
    /// 対象ウィンドウの「クライアント領域」(タイトルバー等を除いた実描画範囲)を求め、
    /// videocrop の "top" プロパティにタイトルバー分のピクセル数を設定する。
    /// 戻り値はマウス座標マッピングに使うべきクライアント領域の矩形。
    /// </summary>
    private ScreenGeometry.Rect? ApplyWindowChromeCrop(IntPtr hwnd)
    {
        var windowRect = ScreenGeometry.GetWindowRectSafe(hwnd);
        var clientRect = ScreenGeometry.GetClientRectOnScreen(hwnd);

        if (windowRect is null || clientRect is null)
        {
            _videoCropTopPixels = 0;
            if (_videoCropHandle != IntPtr.Zero) GstNativeInterop.SetInt(_videoCropHandle, "top", 0);
            return windowRect;
        }

        var top = Math.Max(0, clientRect.Value.Y - windowRect.Value.Y);
        _videoCropTopPixels = top;
        if (_videoCropHandle != IntPtr.Zero)
        {
            GstNativeInterop.SetInt(_videoCropHandle, "top", top);
        }

        return clientRect;
    }

    private void ResetWindowChromeCrop()
    {
        _videoCropTopPixels = 0;
        if (_videoCropHandle != IntPtr.Zero) GstNativeInterop.SetInt(_videoCropHandle, "top", 0);
    }

    private void OnResizeWatchTick(object? state)
    {
        try
        {
            if (!Monitor.TryEnter(_windowLock)) return;
            try
            {
                if (_windowStateDisposed) return;
                OnResizeWatchTickLocked();
            }
            finally
            {
                Monitor.Exit(_windowLock);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config][FATAL in OnResizeWatchTick] {ex}");
        }
    }

    private void OnResizeWatchTickLocked()
    {
        try
        {
            if (_currentWindowHandle is not { } hwnd) return;
            if (!ScreenGeometry.IsWindow(hwnd)) return;

            if (_stripSucceeded && ScreenGeometry.HasOverlappedWindowStyle(hwnd))
            {
                if (_styleStripAttempts < MaxStyleStripAttempts)
                {
                    _styleStripAttempts++;
                    Console.WriteLine($"[Config] Window title bar reappeared, removing again ({_styleStripAttempts}/{MaxStyleStripAttempts})");
                    ScreenGeometry.RemoveOverlappedWindowStyle(hwnd);
                }
                else
                {
                    Console.WriteLine("[Config] Window title bar keeps reappearing, giving up on removing it");
                }
            }

            var clientRect = ApplyWindowChromeCrop(hwnd);
            if (clientRect is null) return;

            if (!clientRect.Equals(_lastKnownWindowRect))
            {
                var sizeChanged = _lastKnownWindowRect is null
                    || clientRect.Value.Width != _lastKnownWindowRect.Value.Width
                    || clientRect.Value.Height != _lastKnownWindowRect.Value.Height;

                _lastKnownWindowRect = clientRect;
                InputInjector.SetCaptureRegion(clientRect.Value);

                if (sizeChanged)
                {
                    var windowRect = ScreenGeometry.GetWindowRectSafe(hwnd);
                    if (windowRect is not null && _stripSucceeded && ScreenGeometry.HasOverlappedWindowStyle(hwnd))
                    {
                        Console.WriteLine("[Config] Window title bar reappeared after resize, removing again");
                        _styleStripAttempts = 0;
                        ScreenGeometry.RemoveOverlappedWindowStyle(hwnd);
                        clientRect = ApplyWindowChromeCrop(hwnd) ?? clientRect;
                        windowRect = ScreenGeometry.GetWindowRectSafe(hwnd) ?? windowRect;
                        _lastKnownWindowRect = clientRect;
                        InputInjector.SetCaptureRegion(clientRect.Value);
                    }

                    if (windowRect is not null)
                    {
                        var matchesRequested = _lastRequestedWindowSize is { } req
                            && IsSimilarSize((windowRect.Value.Width, windowRect.Value.Height), req);

                        if (!matchesRequested)
                        {
                            _lastRequestedWindowSize = (windowRect.Value.Width, windowRect.Value.Height);
                            _independentWindowSize = _lastRequestedWindowSize;
                            Console.WriteLine($"[Config] Window resized independently to {clientRect.Value.Width}x{clientRect.Value.Height} (client area), notifying client");
                            SendWindowResized(clientRect.Value.Width, clientRect.Value.Height);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config][FATAL in OnResizeWatchTick] {ex}");
        }
    }

    /// <summary>
    /// 実験機能: 接続開始時からテスト映像(動くボール)とテスト音(サイン波)を流し、
    /// DataChannel が開いてから指定ミリ秒後にキャプチャ映像/実音声へ切り替える。
    /// (最初のテスト映像に切り替わっている状態はパイプライン構築時に作ってある。)
    /// </summary>
    private async Task StartupBurstAsync()
    {
        try
        {
            var vsel = _pipeline?.GetByName("vsel");
            var capPad = _pipeline?.GetByName("capdl")?.GetStaticPad("src")?.Peer;
            var asel = _pipeline?.GetByName("asel");
            var realAudioPad = _pipeline?.GetByName("arealcaps")?.GetStaticPad("src")?.Peer;
            if (vsel is null || capPad is null)
            {
                Console.WriteLine("[Burst] input-selector 構成を取得できないためスキップします");
                _burstActive = false;
                return;
            }

            Console.WriteLine($"[Burst] テスト映像/テスト音を {_config.StartupBurstMs}ms 継続します");
            await Task.Delay(_config.StartupBurstMs);
            _burstActive = false;

            if (!_videoDisabled)
            {
                GstNativeInterop.SetObject(vsel.Handle, "active-pad", capPad.Handle);
            }
            if (asel is not null && realAudioPad is not null)
            {
                GstNativeInterop.SetObject(asel.Handle, "active-pad", realAudioPad.Handle);
            }
            Console.WriteLine("[Burst] キャプチャ映像/実音声へ切り替えました");
            if (_controlChannelHandle != IntPtr.Zero && _isControlChannelOpen)
            {
                GstNativeInterop.DataChannelSendString(_controlChannelHandle, JsonSerializer.Serialize(new { type = "burstEnd" }));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Burst] failed: {ex}");
        }
    }

    private void CreateControlDataChannel()
    {
        _controlChannelHandle = GstNativeInterop.CreateDataChannel(_webrtcHandle, "control");
        Console.WriteLine($"[Input] CreateDataChannel returned handle: {_controlChannelHandle}");
        if (_controlChannelHandle == IntPtr.Zero)
        {
            Console.WriteLine("[Input] Failed to create control data channel (handle is null)");
            return;
        }

        _onControlChannelOpen = (channel, _) =>
        {
            try
            {
                _isControlChannelOpen = true;
                Console.WriteLine("[Input] Control data channel open");
                if (_config.StartupBurstMs > 0 && _config.EnablePlayoutDelay)
                {
                    var burstTask = StartupBurstAsync();
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Input][FATAL in onControlChannelOpen] {ex}"); }
        };
        _onControlChannelClose = (channel, _) =>
        {
            try
            {
                _isControlChannelOpen = false;
                Console.WriteLine("[Input] Control data channel closed");
            }
            catch (Exception ex) { Console.WriteLine($"[Input][FATAL in onControlChannelClose] {ex}"); }
        };
        _onControlChannelMessage = OnControlChannelMessage;

        GstNativeInterop.ConnectDataChannelOpen(_controlChannelHandle, _onControlChannelOpen);
        GstNativeInterop.ConnectDataChannelClose(_controlChannelHandle, _onControlChannelClose);
        GstNativeInterop.ConnectDataChannelMessageString(_controlChannelHandle, _onControlChannelMessage);
    }

    private void OnControlChannelMessage(IntPtr channel, IntPtr strPtr, IntPtr userData)
    {
        try
        {
            var json = Marshal.PtrToStringUTF8(strPtr);
            if (string.IsNullOrEmpty(json)) return;

            var msg = JsonSerializer.Deserialize<ControlMessage>(json);
            if (msg is null) return;

            switch (msg.Type)
            {
                case "mouseMove":
                case "mouseButton":
                case "mouseWheel":
                case "keyDown":
                case "keyUp":
                case "touch":
                    InputInjector.Handle(msg);
                    break;

                case "setBitrate":
                    SetBitrate(msg.BitrateKbps);
                    break;

                case "setMute":
                    SetMute(msg.Muted);
                    break;

                case "switchSource":
                    EnqueueControlWork(() => SwitchSource(msg));
                    break;

                case "listSources":
                    EnqueueControlWork(SendSourceList);
                    break;

                case "screenshot":
                    var screenshotTask = Task.Run(TakeScreenshot);
                    break;

                case "setViewportSize":
                    QueueViewportSize(msg.ViewportWidth, msg.ViewportHeight);
                    break;

                default:
                    Console.WriteLine($"[Input] Unhandled control message type: {msg.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Input][FATAL in OnControlChannelMessage] {ex}");
        }
    }

    /// <summary>
    /// 切り替えやリサイズはキャプチャ要素の状態変更などで時間がかかる。DataChannel の受信スレッド
    /// (SCTP スレッド)で実行すると受信が止まり、接続が切れる原因になるため、専用スレッドで順番に処理する。
    /// </summary>
    private void StartControlWorker()
    {
        var worker = new Thread(() =>
        {
            foreach (var work in _controlWork.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Console.WriteLine($"[Input][FATAL in control worker] {ex}"); }
            }
        })
        {
            IsBackground = true,
            Name = "control-worker",
        };
        worker.Start();
    }

    private void EnqueueControlWork(Action work)
    {
        try { _controlWork.Add(work); }
        catch (InvalidOperationException) { }
    }

    /// <summary>連続して届くサイズ変更は最新の1件だけを処理する。</summary>
    private void QueueViewportSize(int? width, int? height)
    {
        if (width is null || height is null || width <= 0 || height <= 0) return;

        bool schedule;
        lock (_viewportLock)
        {
            schedule = _pendingViewport is null;
            _pendingViewport = (width.Value, height.Value);
        }
        if (!schedule) return;

        EnqueueControlWork(() =>
        {
            (int Width, int Height) size;
            lock (_viewportLock)
            {
                size = _pendingViewport!.Value;
                _pendingViewport = null;
            }
            HandleViewportSize(size.Width, size.Height);
        });
    }

    private void SetBitrate(uint? kbps)
    {
        if (kbps is null)
        {
            Console.WriteLine("[Config] setBitrate: bitrateKbps is missing");
            return;
        }
        if (_videoEncoderHandle == IntPtr.Zero)
        {
            Console.WriteLine("[Config] setBitrate: video encoder element not found");
            return;
        }

        GstNativeInterop.SetUInt(_videoEncoderHandle, "bitrate", kbps.Value);
        _currentBitrateKbps = kbps.Value;
        Console.WriteLine($"[Config] Bitrate set to {kbps.Value} kbps");
    }

    private void SetMute(bool? muted)
    {
        if (muted is null)
        {
            Console.WriteLine("[Config] setMute: muted is missing");
            return;
        }
        if (_volumeHandle == IntPtr.Zero)
        {
            Console.WriteLine("[Config] setMute: volume element not found");
            return;
        }

        GstNativeInterop.SetBool(_volumeHandle, "mute", muted.Value);
        Console.WriteLine($"[Config] Mute set to {muted.Value}");
    }

    /// <summary>
    /// 配信元(モニター全体 / 特定ウィンドウ)を切り替える。
    ///
    /// 重要: パイプライン全体(webrtcbin を含む)の状態は絶対に変更しない。
    /// webrtcbin を READY に落とすと ICE/DTLS/SCTP の接続状態がリセットされ、
    /// 再ネゴシエーションなしには PLAYING に戻しても映像が復帰しなくなる
    /// (実際にこの問題が発生した)。
    ///
    /// 代わりに、キャプチャ元の要素だけを gst_element_set_locked_state() で
    /// 親(パイプライン)の自動状態遷移から一時的に切り離し、その要素だけを
    /// READY -> (プロパティ変更) -> PLAYING と遷移させる。
    /// webrtcbin 以降の接続状態には触れないため、切り替え後も同じ
    /// PeerConnection のまま映像だけが数秒後に復帰する。
    ///
    /// 切り替え前に対象(ウィンドウ/モニター)の妥当性を検証し、
    /// 万が一無効な対象が指定されてもパイプラインには触れずにエラー応答するだけに留める。
    /// それでも実際のキャプチャが失敗した場合の保険として、OnBusMessage 側で
    /// エラー検知時の自動復旧(プライマリモニタへのフォールバック)も行っている。
    /// </summary>
    private void SwitchSource(ControlMessage msg)
    {
        lock (_windowLock)
        {
            if (_windowStateDisposed) return;
            SwitchSourceLocked(msg);
        }
    }

    private void SwitchSourceLocked(ControlMessage msg)
    {
        if (_captureSourceElement is null || _captureSourceHandle == IntPtr.Zero)
        {
            Console.WriteLine("[Config] switchSource: capture source not ready");
            return;
        }

        if (msg.SourceMode == "window" && msg.WindowHandle.HasValue)
        {
            var hwnd = new IntPtr((long)msg.WindowHandle.Value);
            if (!ScreenGeometry.IsWindow(hwnd))
            {
                Console.WriteLine($"[Config] switchSource: window handle {msg.WindowHandle} no longer exists");
                SendSourceError("指定されたウィンドウは既に存在しません。ウィンドウ一覧を再取得してください。");
                return;
            }

            WindowEnumerator.RestoreIfMinimized(hwnd);

            if (_currentWindowHandle != hwnd)
            {
                RestoreOriginalWindowRectIfNeeded();
                _originalWindowRect = ScreenGeometry.GetWindowRectSafe(hwnd);
                _lastRequestedWindowSize = null;
            _independentWindowSize = null;

                _trackedWindowHandle = hwnd;
                _originalStyleBits = ScreenGeometry.GetOverlappedStyleBits(hwnd);
                _stripSucceeded = false;
                WindowStyleRecovery.Save(hwnd, _originalStyleBits, _originalWindowRect);

                MoveWindowToTargetMonitor(hwnd);

                _styleStripAttempts = 0;
                if (_originalStyleBits != 0)
                {
                    _stripSucceeded = ScreenGeometry.RemoveOverlappedWindowStyle(hwnd);
                }
            }

            ApplySourceSwitch(() =>
            {
                GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", msg.WindowHandle.Value);
            });
            EnableCaptureInput();

            var clientRect = ApplyWindowChromeCrop(hwnd);
            InputInjector.SetCaptureRegion(clientRect ?? ScreenGeometry.PrimaryMonitorFallback);
            _currentWindowHandle = hwnd;
            _lastKnownWindowRect = clientRect;

            _lastRequestedWindowSize = ScreenGeometry.GetWindowRectSafe(hwnd) is { } wr ? (wr.Width, wr.Height) : null;
            SendWindowResized(clientRect.Value.Width, clientRect.Value.Height);
        }
        else if (msg.SourceMode == "monitor" && msg.MonitorIndex.HasValue)
        {
            var monitorCount = ScreenGeometry.GetMonitorCount();
            if (msg.MonitorIndex.Value < 0 || msg.MonitorIndex.Value >= monitorCount)
            {
                Console.WriteLine($"[Config] switchSource: monitor index {msg.MonitorIndex} out of range (count={monitorCount})");
                SendSourceError($"モニター番号 {msg.MonitorIndex} は存在しません(検出台数: {monitorCount})。");
                return;
            }

            RestoreOriginalWindowRectIfNeeded();

            var targetMonitorRect = ScreenGeometry.GetMonitorRect(msg.MonitorIndex.Value);
            SwitchCaptureViaBlack(targetMonitorRect?.Width ?? 0, targetMonitorRect?.Height ?? 0, () =>
            {
                GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", 0);
                GstNativeInterop.SetInt(_captureSourceHandle, "monitor-index", msg.MonitorIndex.Value);
            });
            _currentMonitorIndex = msg.MonitorIndex.Value;

            var rect = ScreenGeometry.GetMonitorRect(msg.MonitorIndex.Value);
            InputInjector.SetCaptureRegion(rect ?? ScreenGeometry.PrimaryMonitorFallback);
            _currentWindowHandle = null;
            _lastKnownWindowRect = null;
            ResetWindowChromeCrop();
        }
        else if (msg.SourceMode == "none")
        {
            RestoreOriginalWindowRectIfNeeded();
            _currentWindowHandle = null;
            _lastKnownWindowRect = null;
            ResetWindowChromeCrop();

            _videoDisabled = true;
            InputInjector.Enabled = false;
            if (_videoSelectorHandle != IntPtr.Zero && _blackSelPad is not null)
            {
                GstNativeInterop.SetObject(_videoSelectorHandle, "active-pad", _blackSelPad.Handle);
            }

            _captureSourceElement.SetLockedState(true);
            _captureSourceElement.SetState(State.Ready);
            Console.WriteLine("[Config] 映像なし(音声のみ)に切り替えました");
        }
        else
        {
            Console.WriteLine("[Config] switchSource: invalid parameters (sourceMode/monitorIndex/windowHandle)");
            SendSourceError("switchSource のパラメータが不正です。");
        }
    }

    /// <summary>
    /// 指定モニター(固定、DPI100%・十分な大きさを想定)へウィンドウを移動する。
    /// この時点ではサイズは変えず、位置だけ移す(サイズはクライアントからのサイズ指定を待つ)。
    /// </summary>
    private bool _monitorNameWarned;

    /// <summary>
    /// ウィンドウ配信先モニターの番号を求める。モニター名が指定されていれば名前で解決する
    /// (接続・切断でモニター構成が変わっても名前なら追従できるよう、毎回解決する)。
    /// </summary>
    private int ResolveWindowTargetMonitorIndex()
    {
        var spec = _config.WindowTargetMonitorName?.Trim();
        if (string.IsNullOrEmpty(spec)) return _config.WindowTargetMonitorIndex;

        var nth = 1;
        var hash = spec.LastIndexOf('#');
        if (hash > 0 && int.TryParse(spec[(hash + 1)..], out var n) && n >= 1)
        {
            nth = n;
            spec = spec[..hash].Trim();
        }

        var infos = ScreenGeometry.GetMonitorInfos();
        var matches = infos.Where(m => string.Equals(m.Name, spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = infos.Where(m => m.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count >= nth) return matches[nth - 1].Index;

        if (!_monitorNameWarned)
        {
            _monitorNameWarned = true;
            Console.WriteLine($"[Config] ウィンドウ配信先モニター \"{_config.WindowTargetMonitorName}\" が見つかりません。" +
                              $"モニター番号 {_config.WindowTargetMonitorIndex} を使います。");
        }
        return _config.WindowTargetMonitorIndex;
    }

    private void MoveWindowToTargetMonitor(IntPtr hwnd)
    {
        var monitorRect = ScreenGeometry.GetMonitorRect(ResolveWindowTargetMonitorIndex()) ?? ScreenGeometry.PrimaryMonitorFallback;
        var currentRect = ScreenGeometry.GetWindowRectSafe(hwnd);
        if (currentRect is null) return;

        Console.WriteLine($"[Config] Moving window to target monitor {ResolveWindowTargetMonitorIndex()} at ({monitorRect.X},{monitorRect.Y})");
        ScreenGeometry.MoveResizeWindow(hwnd, monitorRect.X + 20, monitorRect.Y + 20, currentRect.Value.Width, currentRect.Value.Height);
    }

    /// <summary>
    /// クライアントから通知された「クライアント領域(配信される映像そのもの)」の
    /// 希望サイズ(デバイスピクセル)を受け取る。
    /// アスペクト比の計算は行わない(クライアント側の責務)。指定されたサイズを
    /// そのままクライアント領域のサイズとして適用する。
    /// </summary>
    private void HandleViewportSize(int? width, int? height)
    {
        if (width is null || height is null || width <= 0 || height <= 0) return;

        _lastClientRequestedSize = (width.Value, height.Value);
        Console.WriteLine($"[Config] Client requested client-area size: {width}x{height}");

        if (_currentWindowHandle is { } hwnd)
        {
            ApplyRequestedClientSize(hwnd, width.Value, height.Value);
        }
    }

    /// <summary>
    /// クライアントが希望する「クライアント領域」のサイズをそのまま適用する。
    /// Win32 の SetWindowPos は「ウィンドウ全体」のサイズを指定する必要があるため、
    /// タイトルバー分(_videoCropTopPixels)だけこちらで足し戻して変換する。
    /// 適用後は、ポーリングを待たずその場で WGC セッションを再初期化し、
    /// マウス座標マッピングも更新する(体感のガタつきを減らすため)。
    /// </summary>
    private void ApplyRequestedClientSize(IntPtr hwnd, int clientWidth, int clientHeight)
    {
        lock (_windowLock)
        {
            if (_windowStateDisposed) return;
            ApplyRequestedClientSizeLocked(hwnd, clientWidth, clientHeight);
        }
    }

    private void ApplyRequestedClientSizeLocked(IntPtr hwnd, int clientWidth, int clientHeight)
    {
        var monitorRect = ScreenGeometry.GetMonitorRect(ResolveWindowTargetMonitorIndex()) ?? ScreenGeometry.PrimaryMonitorFallback;

        var windowWidth = clientWidth;
        var windowHeight = clientHeight + _videoCropTopPixels;

        var x = monitorRect.X + Math.Max(0, (monitorRect.Width - windowWidth) / 2);
        var y = monitorRect.Y + Math.Max(0, (monitorRect.Height - windowHeight) / 2);

        var independentSize = _independentWindowSize;
        var requested = (Width: windowWidth, Height: windowHeight);
        var before = ScreenGeometry.GetWindowRectSafe(hwnd);
        (int Width, int Height)? actual = null;

        for (var attempt = 1; attempt <= MaxResizeAttempts; attempt++)
        {
            Console.WriteLine($"[Config] Resizing window: clientArea={clientWidth}x{clientHeight} -> window={windowWidth}x{windowHeight} (attempt {attempt}/{MaxResizeAttempts})");
            ScreenGeometry.MoveResizeWindow(hwnd, x, y, windowWidth, windowHeight);
            Thread.Sleep(ResizeSettleDelayMs);

            var rect = ScreenGeometry.GetWindowRectSafe(hwnd);
            if (rect is null) break;
            actual = (rect.Value.Width, rect.Value.Height);

            if (IsSimilarSize(actual.Value, requested)) break;

            if (independentSize is { } independent && IsSimilarSize(actual.Value, independent))
            {
                Console.WriteLine($"[Config] Window returned to its own size {actual.Value.Width}x{actual.Value.Height}, giving up resizing");
                break;
            }

            Console.WriteLine($"[Config] Window size is {actual.Value.Width}x{actual.Value.Height}, differs from requested, retrying");
        }

        _independentWindowSize = null;
        _lastRequestedWindowSize = actual ?? requested;

        var clientRect = ApplyWindowChromeCrop(hwnd);
        InputInjector.SetCaptureRegion(clientRect ?? ScreenGeometry.PrimaryMonitorFallback);
        _lastKnownWindowRect = clientRect;

        var finalSize = actual ?? requested;
        var sizeChanged = before is null
            || !IsSimilarSize((before.Value.Width, before.Value.Height), finalSize);
        if (!sizeChanged) return;

        ApplySourceSwitch(() =>
        {
            GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", (ulong)hwnd.ToInt64());
        });
    }

    private static bool IsSimilarSize((int Width, int Height) a, (int Width, int Height) b)
        => Math.Abs(a.Width - b.Width) <= WindowSizeTolerance
        && Math.Abs(a.Height - b.Height) <= WindowSizeTolerance;

    /// <summary>ウィンドウが(こちらの意図とは無関係に)新しいサイズになったことをクライアントへ知らせる。</summary>
    private void SendWindowResized(int clientWidth, int clientHeight)
    {
        if (_controlChannelHandle == IntPtr.Zero || !_isControlChannelOpen) return;
        var json = JsonSerializer.Serialize(new { type = "windowResized", width = clientWidth, height = clientHeight });
        GstNativeInterop.DataChannelSendString(_controlChannelHandle, json);
    }

    /// <summary>配信終了時、追跡していたウィンドウを元の位置・サイズ・スタイルへ戻す。</summary>
    private void RestoreOriginalWindowRectIfNeeded()
    {
        lock (_windowLock)
        {
            if (_trackedWindowHandle is { } hwnd)
            {
                if (ScreenGeometry.IsWindow(hwnd))
                {
                    if (_originalStyleBits != 0)
                    {
                        Console.WriteLine("[Config] Restoring window title bar");
                        ScreenGeometry.RestoreOverlappedStyleBits(hwnd, _originalStyleBits);
                    }

                    if (_originalWindowRect is { } orig)
                    {
                        Console.WriteLine("[Config] Restoring window to its original position/size");
                        ScreenGeometry.MoveResizeWindow(hwnd, orig.X, orig.Y, orig.Width, orig.Height);
                    }
                }

                WindowStyleRecovery.Remove(hwnd);
            }

            _trackedWindowHandle = null;
            _originalStyleBits = 0;
            _stripSucceeded = false;
            _originalWindowRect = null;
            _styleStripAttempts = 0;
            _lastRequestedWindowSize = null;
            _independentWindowSize = null;
        }
    }

    /// <summary>
    /// キャプチャ元の切り替え。切り替え前後で解像度が同じ(例: 同じ解像度のモニター同士)だと
    /// エンコーダ/下流が「流れが途切れた」ことに気付けず映像が固まることがあったため、
    /// 一旦黒画面(解像度が異なる)を経由して、エンコーダを確実に再ネゴシエーションさせる。
    /// (解像度が変わる切り替え=ウィンドウ経由では固まらないことが確認できているため、同じ経路にする)
    /// </summary>
    private void SwitchCaptureViaBlack(int targetWidth, int targetHeight, Action applyProperties)
    {
        var sameResolution = targetWidth <= 0 || targetHeight <= 0
            || (_captureWidth == targetWidth && _captureHeight == targetHeight);

        var bridged = false;
        if (sameResolution && !_videoDisabled && !_burstActive && _videoSelectorHandle != IntPtr.Zero && _blackSelPad is not null)
        {
            GstNativeInterop.SetObject(_videoSelectorHandle, "active-pad", _blackSelPad.Handle);
            bridged = true;
            Thread.Sleep(200);
        }

        ApplySourceSwitch(applyProperties);

        if (bridged) _videoDisabled = true;
        EnableCaptureInput();
    }

    /// <summary>capturesrc だけを READY に落とし、プロパティ変更後 PLAYING へ戻す共通処理。</summary>
    private void ApplySourceSwitch(Action applyProperties)
    {
        if (_captureSourceElement is null) return;

        Console.WriteLine("[Config] Switching capture source ...");
        _captureSourceElement.SetLockedState(true);
        _captureSourceElement.SetState(State.Ready);

        applyProperties();

        _captureSourceElement.SetState(State.Playing);
        _captureSourceElement.SetLockedState(false);
        Console.WriteLine("[Config] Source switch complete");
    }

    private void SendSourceError(string message)
    {
        if (_controlChannelHandle == IntPtr.Zero || !_isControlChannelOpen) return;
        var json = JsonSerializer.Serialize(new { type = "sourceError", message });
        GstNativeInterop.DataChannelSendString(_controlChannelHandle, json);
    }

    /// <summary>
    /// キャプチャ/エンコードに失敗した場合の保険的な自動復旧。
    ///
    /// video encoder は一度初期化に失敗すると、単純に NULL -> PLAYING と状態を
    /// 遷移させるだけでは内部状態が正しくリセットされず、同じ映像が来ても
    /// 再度失敗し続けることが実機で確認された。そのため video encoder だけは
    /// 要素そのものを破棄して新しいインスタンスを作り直す(パイプラインに
    /// 差し込み直す)方式にしている。capturesrc/queue/payloader は
    /// 通常の NULL -> PLAYING サイクルで十分なため、そちらはそのまま。
    /// webrtcbin には一切触れない。
    /// </summary>
    private void RecoverVideoBranch()
    {
        var now = DateTime.UtcNow;
        if (now - _lastVideoBranchRecovery < VideoBranchRecoveryDebounce) return;
        _lastVideoBranchRecovery = now;

        try
        {
            Console.WriteLine("[Config] Recovering video branch after error, falling back to monitor 0 ...");

            var stateCycleElements = new[] { _captureSourceElement, _videoConvertElement, _videoCropElement, _videoQueue1Element, _videoConvert2Element, _videoPayloaderElement, _videoQueue2Element };

            foreach (var el in stateCycleElements)
            {
                el?.SetLockedState(true);
                el?.SetState(State.Null);
            }

            if (_captureSourceHandle != IntPtr.Zero)
            {
                GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", 0);
                GstNativeInterop.SetInt(_captureSourceHandle, "monitor-index", 0);
            }

            var newVenc = RecreateVideoEncoderLinkOnly();

            foreach (var el in stateCycleElements)
            {
                el?.SetState(State.Playing);
                el?.SetLockedState(false);
            }

            if (newVenc is not null)
            {
                newVenc.SetLockedState(false);
                newVenc.SetState(State.Playing);
            }

            InputInjector.SetCaptureRegion(ScreenGeometry.GetMonitorRect(0) ?? ScreenGeometry.PrimaryMonitorFallback);
            _currentMonitorIndex = 0;
            EnableCaptureInput();
            RestoreOriginalWindowRectIfNeeded();
            _currentWindowHandle = null;
            _lastKnownWindowRect = null;
            ResetWindowChromeCrop();
            SendSourceError("配信元の切り替えに失敗したため、プライマリモニタに戻しました。");
            Console.WriteLine("[Config] Video branch recovery complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config][FATAL in RecoverVideoBranch] {ex}");
        }
    }

    /// <summary>
    /// video encoder 要素を破棄し、同じプロパティで新しいインスタンスを作り直してパイプラインに
    /// 繋ぎ直す。ここでは PLAYING への遷移は行わない(呼び出し元が周囲の要素を
    /// PLAYING に戻した後、最後にこの戻り値を PLAYING にすること)。
    /// gst_parse_bin_from_description を使うことで、tune 等の enum/flags プロパティも
    /// 文字列指定のまま安全に再現できる。
    /// </summary>
    private Element? RecreateVideoEncoderLinkOnly()
    {
        if (_pipeline is null || _videoConvert2Element is null || _videoEncoderElement is null)
        {
            Console.WriteLine("[Config] RecreateVideoEncoder: required elements missing, skipping");
            return null;
        }

        Console.WriteLine("[Config] Recreating video encoder element (full reset) ...");

        var oldVenc = _videoEncoderElement;
        var beforePad = _videoConvert2Element.GetStaticPad("src");
        var oldSink = oldVenc.GetStaticPad("sink");
        var oldSrc = oldVenc.GetStaticPad("src");
        var afterPad = oldSrc?.Peer;

        oldVenc.SetLockedState(true);
        oldVenc.SetState(State.Null);

        if (beforePad is not null && oldSink is not null) beforePad.Unlink(oldSink);
        if (afterPad is not null && oldSrc is not null) oldSrc.Unlink(afterPad);

        _pipeline.Remove(oldVenc);

        var desc = BuildVideoEncoderDescription(_config, _currentBitrateKbps);
        var newVenc = Parse.BinFromDescription(desc, true);
        newVenc.SetLockedState(true);
        _pipeline.Add(newVenc);

        var newSink = newVenc.GetStaticPad("sink");
        var newSrc = newVenc.GetStaticPad("src");

        if (beforePad is not null && newSink is not null) beforePad.Link(newSink);
        if (afterPad is not null && newSrc is not null) newSrc.Link(afterPad);

        _videoEncoderElement = newVenc;
        _videoEncoderHandle = (newVenc as Bin)?.GetByName("venc")?.Handle ?? IntPtr.Zero;

        Console.WriteLine("[Config] video encoder recreated (linked, not yet playing)");
        return newVenc;
    }

    /// <summary>
    /// 配信元として選べるもの(モニター / ウィンドウ)を1つのメッセージにまとめてクライアントへ送る。
    /// current は現在の配信元("monitor:N" / "window:HANDLE" / "none")で、クライアントの選択状態の復元に使う。
    /// </summary>
    private void SendSourceList()
    {
        if (_controlChannelHandle == IntPtr.Zero || !_isControlChannelOpen) return;

        var monitors = ScreenGeometry.GetMonitorInfos();
        var windows = WindowEnumerator.ListVisibleWindows();
        var current = _videoDisabled
            ? "none"
            : _currentWindowHandle is { } h ? $"window:{h.ToInt64()}" : $"monitor:{_currentMonitorIndex}";

        var json = JsonSerializer.Serialize(new { type = "sourceList", monitors, windows, current });
        GstNativeInterop.DataChannelSendString(_controlChannelHandle, json);
        Console.WriteLine($"[Config] Sent source list ({monitors.Count} monitors, {windows.Count} windows)");
    }

    /// <summary>配信範囲(モニター全体 / 配信中ウィンドウのクライアント領域)を撮影して保存し、結果をクライアントへ返す。</summary>
    private void TakeScreenshot()
    {
        try
        {
            bool ok;
            string result;
            if (_videoDisabled)
            {
                ok = false;
                result = "「映像なし」の間は撮影できません。";
            }
            else
            {
                IntPtr? hwnd = _currentWindowHandle is { } h && ScreenGeometry.IsWindow(h) ? h : null;
                var monitorRect = ScreenGeometry.GetMonitorRect(_currentMonitorIndex) ?? ScreenGeometry.PrimaryMonitorFallback;
                ok = ScreenshotCapturer.TryCapture(hwnd, monitorRect, out result);
            }

            Console.WriteLine(ok ? $"[Screenshot] 保存しました: {result}" : $"[Screenshot] 失敗: {result}");
            if (_controlChannelHandle != IntPtr.Zero && _isControlChannelOpen)
            {
                var json = JsonSerializer.Serialize(new { type = "screenshotResult", ok, message = result });
                GstNativeInterop.DataChannelSendString(_controlChannelHandle, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Screenshot][FATAL] {ex}");
        }
    }

    /// <summary>映像入力をキャプチャ映像へ切り替え、入力操作も有効に戻す(「映像なし」からの復帰用)。</summary>
    private void EnableCaptureInput()
    {
        var wasDisabled = _videoDisabled;
        _videoDisabled = false;
        InputInjector.Enabled = true;

        if (!wasDisabled && !_burstActive) return;

        if (_videoSelectorHandle != IntPtr.Zero && _captureSelPad is not null)
        {
            GstNativeInterop.SetObject(_videoSelectorHandle, "active-pad", _captureSelPad.Handle);
        }
    }

    private static string BuildPipelineDescription(HostConfig config)
    {
        var videoSource = config.UseTestSource
            ? "videotestsrc name=capturesrc is-live=true pattern=ball !"
            : $"d3d11screencapturesrc name=capturesrc capture-api=1 monitor-index=0 show-cursor=true do-timestamp=true ! video/x-raw(memory:D3D11Memory),framerate={config.Framerate}/1 ! d3d11convert ! video/x-raw(memory:D3D11Memory),format=NV12 ! d3d11download !";

        var audioSource = config.UseTestSource
            ? "audiotestsrc name=audiosrc is-live=true wave=sine !"
            : "wasapisrc name=audiosrc loopback=true low-latency=true !";

        var videoCapsAndPay = config.Codec == VideoCodec.Hevc
            ? "rtph265pay name=vpay config-interval=-1 pt=96 aggregate-mode=zero-latency !"
            : "video/x-h264,profile=baseline ! rtph264pay name=vpay config-interval=-1 pt=96 aggregate-mode=zero-latency !";

        var burst = config.StartupBurstMs > 0 && config.EnablePlayoutDelay && !config.UseTestSource;
        var useSelector = !config.UseTestSource;
        var videoHead = useSelector
            ? "input-selector name=vsel cache-buffers=true !"
            : videoSource;

        var parts = new List<string>
        {
            $"webrtcbin name=sendrecv bundle-policy=max-bundle stun-server={config.StunServer}",

            videoHead,
            "videoconvert name=vconv !",
            "videocrop name=vcrop !",
            "video/x-raw,format=I420 !",
            "queue name=vqueue1 max-size-buffers=2 leaky=downstream !",
            "videoconvert name=vconv2 !",
            BuildVideoEncoderDescription(config, config.BitrateKbps) + " !",
            videoCapsAndPay,
            "queue name=vqueue2 max-size-buffers=0 max-size-bytes=0 max-size-time=100000000 ! sendrecv.",
        };

        var audioTail = new[]
        {
            "queue max-size-buffers=0 max-size-bytes=0 max-size-time=200000000 leaky=downstream !",
            "opusenc bitrate=64000 !",
            "rtpopuspay pt=97 !",
            "queue max-size-buffers=0 max-size-bytes=0 max-size-time=100000000 leaky=downstream ! sendrecv.",
        };

        if (useSelector)
        {
            if (burst)
            {
                var rect = ScreenGeometry.GetMonitorRect(0) ?? ScreenGeometry.PrimaryMonitorFallback;
                var bw = rect.Width - rect.Width % 2;
                var bh = rect.Height - rect.Height % 2;
                parts.Add($"videotestsrc name=burstsrc is-live=true pattern=ball ! capsfilter name=burstcaps caps=\"video/x-raw,format=NV12,width={bw},height={bh},framerate={config.Framerate}/1\" ! vsel.");
            }

            parts.Add(videoSource.Replace("d3d11download !", "d3d11download name=capdl ! vsel."));

            parts.Add($"videotestsrc name=blacksrc is-live=true pattern=black ! capsfilter name=blackcaps caps=\"video/x-raw,format=NV12,width=640,height=360,framerate={Math.Max(config.Framerate, 1)}/1\" ! vsel.");
        }

        if (burst)
        {
            parts.Add("input-selector name=asel ! volume name=vol !");
            parts.AddRange(audioTail);
            parts.Add("audiotestsrc name=atestsrc is-live=true wave=sine volume=0.02 ! audioconvert ! audioresample ! capsfilter name=atestcaps caps=\"audio/x-raw,format=S16LE,rate=48000,channels=2\" ! asel.");
            parts.Add(audioSource);
            parts.Add("audioconvert ! audioresample ! capsfilter name=arealcaps caps=\"audio/x-raw,format=S16LE,rate=48000,channels=2\" ! asel.");
        }
        else
        {
            parts.Add(audioSource);
            parts.Add("audioconvert ! audioresample !");
            parts.Add("volume name=vol !");
            parts.AddRange(audioTail);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 映像エンコーダ要素の記述文字列を組み立てる。パイプライン初期構築時と
    /// エラー復旧時のエンコーダ再生成(RecreateVideoEncoderLinkOnly)の両方から使う。
    ///
    /// レイテンシ低減のため zerolatency / rc-mode=cbr / 低遅延プリセットを指定している。
    /// これらのプロパティ名・値は GStreamer / ドライバのバージョンによって異なる場合があるため、
    /// `gst-inspect-1.0 nvh264enc` (または nvh265enc) で実際に存在するか確認すること。
    /// 存在しないプロパティを指定した場合、パイプラインの構築(gst_parse_launch)が
    /// 失敗するのでその際はここを調整する。
    /// </summary>
    private static string BuildVideoEncoderDescription(HostConfig config, uint bitrateKbps)
    {
        return config.Codec == VideoCodec.Hevc
            ? $"nvh265enc name=venc bitrate={bitrateKbps} zerolatency=true rc-mode=cbr preset=low-latency-hq"
            : $"nvh264enc name=venc bitrate={bitrateKbps} zerolatency=true rc-mode=cbr preset=low-latency-hq";
    }

    private void OnNegotiationNeeded(IntPtr webrtcbin, IntPtr userData)
    {
        try
        {
            Console.WriteLine("[WebRTC] on-negotiation-needed -> create-offer");
            _onOfferCreated = OnOfferCreated;
            var promise = GstNativeInterop.PromiseNewWithChangeFunc(_onOfferCreated);
            GstNativeInterop.EmitCreateOffer(_webrtcHandle, promise);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC][FATAL in OnNegotiationNeeded] {ex}");
        }
    }

    private void OnOfferCreated(IntPtr promise, IntPtr userData)
    {
        try
        {
            var reply = GstNativeInterop.PromiseGetReply(promise);
            var offerDescPtr = GstNativeInterop.StructureGetBoxed(reply, "offer");
            if (offerDescPtr == IntPtr.Zero)
            {
                Console.WriteLine("[WebRTC] create-offer produced no 'offer' field");
                return;
            }

            var sdpText = GstNativeInterop.SessionDescriptionToSdpText(offerDescPtr);
            Console.WriteLine("[WebRTC] Offer SDP:\n" + sdpText);

            if (_config.EnablePlayoutDelay)
            {
                var munged = PlayoutDelayExtension.AddExtmapToOffer(sdpText, out var offeredId);
                if (munged is not null)
                {
                    sdpText = munged;
                    _offeredPlayoutExtId = offeredId;
                    Console.WriteLine($"[PlayoutDelay] オファーに extmap を追加 (id={offeredId})");
                }
                else
                {
                    Console.WriteLine("[PlayoutDelay] オファーへの extmap 追加をスキップ(映像セクション無し/空きID無し)");
                }
            }

            var localPromise = GstNativeInterop.PromiseNew();
            GstNativeInterop.EmitSetLocalDescription(_webrtcHandle, offerDescPtr, localPromise);
            GstNativeInterop.gst_promise_interrupt(localPromise);
            GstNativeInterop.gst_promise_unref(localPromise);
            GstNativeInterop.gst_webrtc_session_description_free(offerDescPtr);

            var json = JsonSerializer.Serialize(new { type = "offer", sdp = sdpText });
            _ = SafeSendAsync(json);
            Console.WriteLine("[WebRTC] Offer sent to client");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC][FATAL in OnOfferCreated] {ex}");
        }
        finally
        {
            GstNativeInterop.gst_promise_unref(promise);
        }
    }

    private void OnIceCandidate(IntPtr webrtcbin, uint mlineIndex, IntPtr candidatePtr, IntPtr userData)
    {
        try
        {
            var candidate = Marshal.PtrToStringUTF8(candidatePtr) ?? "";
            Console.WriteLine($"[WebRTC] Local ICE candidate (mline={mlineIndex}): {candidate}");
            var json = JsonSerializer.Serialize(new { type = "ice", candidate, sdpMLineIndex = mlineIndex });
            _ = SafeSendAsync(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC][FATAL in OnIceCandidate] {ex}");
        }
    }

    /// <summary>
    /// SendAsync を fire-and-forget で呼ぶと、内部で発生した例外が
    /// 未観測タスク例外(unobserved task exception)になり、
    /// 呼び出し元がネイティブコールバックの場合はプロセスクラッシュに
    /// つながりうるため、ここで確実に捕捉してログに残す。
    /// </summary>
    private async Task SafeSendAsync(string json)
    {
        try
        {
            await _client.SendAsync(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling][SendAsync error] {ex}");
        }
    }

    private void OnVideoCapsNotify(IntPtr padHandle, IntPtr pspec, IntPtr userData)
    {
        try
        {
            if (GstNativeInterop.TryGetPadCurrentSize(padHandle, out var width, out var height))
            {
                _captureWidth = width;
                _captureHeight = height;
                var cropRight = width % 2 != 0 ? 1 : 0;

                var heightAfterTopCrop = height - _videoCropTopPixels;
                var cropBottom = heightAfterTopCrop % 2 != 0 ? 1 : 0;

                if (_videoCropHandle != IntPtr.Zero)
                {
                    GstNativeInterop.SetInt(_videoCropHandle, "right", cropRight);
                    GstNativeInterop.SetInt(_videoCropHandle, "bottom", cropBottom);
                    if (cropRight != 0 || cropBottom != 0)
                    {
                        Console.WriteLine($"[Config] Odd capture size {width}x{height} (top-crop={_videoCropTopPixels}) detected, cropping to even size (right={cropRight}, bottom={cropBottom})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config][FATAL in OnVideoCapsNotify] {ex}");
        }
    }

    private void OnBusMessage(object o, GLib.SignalArgs args)
    {
        try
        {
            var msg = (Message)args.Args[0];
            switch (msg.Type)
            {
                case MessageType.Error:
                    msg.ParseError(out var err, out var debug);
                    var srcName = (msg.Src as Element)?.Name ?? "(unknown)";
                    Console.WriteLine($"[Pipeline] ERROR from '{srcName}': {err.Message} ({debug})");

                    if (srcName is "capturesrc" or "vqueue1" or "venc" or "vpay" or "vqueue2")
                    {
                        RecoverVideoBranch();
                    }
                    break;
                case MessageType.Eos:
                    Console.WriteLine("[Pipeline] End of stream");
                    break;
                case MessageType.StateChanged when msg.Src == _pipeline:
                    msg.ParseStateChanged(out _, out var newState, out _);
                    Console.WriteLine($"[Pipeline] State -> {newState}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pipeline][FATAL in OnBusMessage] {ex}");
        }
    }

    private void OnSignalingMessage(string json)
    {
        try
        {
            SignalMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<SignalMessage>(json);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[Signaling] Invalid JSON: {ex.Message}");
                return;
            }

            if (msg is null) return;

            switch (msg.Type)
            {
                case "answer" when msg.Sdp is not null:
                    HandleAnswer(msg.Sdp);
                    break;
                case "ice" when msg.Candidate is not null && msg.SdpMLineIndex is not null:
                    Console.WriteLine($"[Signaling] Remote ICE candidate (mline={msg.SdpMLineIndex}): {msg.Candidate}");
                    GstNativeInterop.EmitAddIceCandidate(_webrtcHandle, msg.SdpMLineIndex.Value, msg.Candidate);
                    break;
                default:
                    Console.WriteLine($"[Signaling] Unhandled message type: {msg.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling][FATAL in OnSignalingMessage] {ex}");
        }
    }

    private void HandleAnswer(string sdpText)
    {
        if (_config.EnablePlayoutDelay && _offeredPlayoutExtId > 0)
        {
            sdpText = PlayoutDelayExtension.StripExtmapFromAnswer(sdpText, out var answeredId);
            if (answeredId > 0 && _videoPayloaderElement is not null)
            {
                _playoutDelay = new PlayoutDelayExtension();
                _playoutDelay.Attach(_videoPayloaderElement.Handle, answeredId);
            }
            else
            {
                Console.WriteLine("[PlayoutDelay] ブラウザが playout-delay 拡張を受け入れませんでした(未ネゴシエーション)。通常動作します");
            }
        }

        var sdpMessagePtr = GstNativeInterop.ParseSdp(sdpText);
        var answerDescPtr = GstNativeInterop.WebRtcSessionDescriptionNew(
            GstNativeInterop.WebRTCSDPType.Answer, sdpMessagePtr);

        var promise = GstNativeInterop.PromiseNew();
        GstNativeInterop.EmitSetRemoteDescription(_webrtcHandle, answerDescPtr, promise);
        GstNativeInterop.gst_promise_interrupt(promise);
        GstNativeInterop.gst_promise_unref(promise);
        GstNativeInterop.gst_webrtc_session_description_free(answerDescPtr);

        Console.WriteLine("[WebRTC] Remote description (answer) set");
    }

    private int _disposed;

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _resizeWatchTimer?.Dispose();
        _controlWork.CompleteAdding();
        lock (_windowLock)
        {
            _windowStateDisposed = true;
            RestoreOriginalWindowRectIfNeeded();
        }
        _playoutDelay?.Detach();
        _client.MessageReceived -= OnSignalingMessage;
        if (_pipeline is not null)
        {
            _pipeline.SetState(State.Null);
            _pipeline.Dispose();
            _pipeline = null;
        }
    }
}

internal sealed class HostConfig
{
    public string StunServer { get; set; } = "stun://stun.l.google.com:19302";

    /// <summary>true の場合、videotestsrc/audiotestsrc を使い d3d11/wasapi を経由しない
    /// 切り分けテスト用パイプラインになる。</summary>
    public bool UseTestSource { get; set; } = false;

    /// <summary>true の場合、RTP の playout-delay 拡張(min=max=0)で受信側に低遅延再生を指示する(接続クエリ ?playout=0 で無効化)。</summary>
    public bool EnablePlayoutDelay { get; set; } = true;

    /// <summary>映像の初期ビットレート(kbps)。接続クエリ ?bitrate=kbps で上書き可能。</summary>
    public uint BitrateKbps { get; set; } = 6000;

    /// <summary>接続直後にこのミリ秒だけ動きのあるテスト映像を流す実験機能(0で無効。接続クエリ ?burst=ms)。</summary>
    public int StartupBurstMs { get; set; } = 0;

    /// <summary>キャプチャフレームレート(接続時にクエリパラメータ ?fps=60 で上書き可能)。</summary>
    public int Framerate { get; set; } = 30;

    /// <summary>映像コーデック(接続時にクエリパラメータ ?codec=hevc で上書き可能)。</summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>
    /// ウィンドウ配信時に対象ウィンドウを移動する先のモニター番号(固定)。
    /// DPI拡大率100%・十分なサイズがあるモニターを想定している。
    /// </summary>
    public int WindowTargetMonitorIndex { get; set; } = 1;

    /// <summary>
    /// ウィンドウ配信先モニターの名前(部分一致、"名前#2" で同名の2番目)。指定した場合は
    /// WindowTargetMonitorIndex より優先し、見つからなければ WindowTargetMonitorIndex を使う。
    /// </summary>
    public string? WindowTargetMonitorName { get; set; }
}

internal enum VideoCodec
{
    H264,
    Hevc,
}
