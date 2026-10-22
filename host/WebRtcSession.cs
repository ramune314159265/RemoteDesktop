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

    // ネイティブコールバックは GC で回収されないよう参照を保持し続ける必要がある。
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
    private Element? _captureSourceElement;
    private Element? _videoQueue1Element;
    private Element? _videoEncoderElement;
    private Element? _videoPayloaderElement;
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

    private uint _currentBitrateKbps = 4000;

    // 直近に「切り替えを試みた」ソース設定。エラー発生時、これより前に有効だった
    // 設定へロールバックするのではなく、常に安全なプライマリモニタへ戻す簡易実装。
    private DateTime _lastVideoBranchRecovery = DateTime.MinValue;
    private static readonly TimeSpan VideoBranchRecoveryDebounce = TimeSpan.FromSeconds(2);

    // ウィンドウ配信中、対象ウィンドウがリサイズされた場合にマウス座標マッピングを
    // 追従させるためのポーリング。リアルタイムなイベント通知(WinEventHook等)は
    // 実装コストが高いため、軽量な debounce ポーリング(既定500ms間隔)で対応する。
    private Timer? _resizeWatchTimer;
    private IntPtr? _currentWindowHandle;
    private ScreenGeometry.Rect? _lastKnownWindowRect;
    private static readonly TimeSpan ResizeWatchInterval = TimeSpan.FromMilliseconds(500);

    // --- クライアント解像度への追従(ウィンドウ配信時) ---
    // アスペクト比の計算はクライアント側の責務とし、サーバーは指定された
    // 「クライアント領域(配信される映像そのもの)のサイズ」をそのまま適用するだけにする。
    private ScreenGeometry.Rect? _originalWindowRect; // 配信開始前の位置・サイズ(終了時に復元する)
    private (int Width, int Height)? _lastClientRequestedSize; // クライアントが最後に要求したクライアント領域サイズ
    private (int Width, int Height)? _lastRequestedWindowSize; // それを実現するために実際にリクエストした「ウィンドウ全体」のサイズ

    // --- タイトルバー等(WS_OVERLAPPEDWINDOW)の着脱 ---
    private bool? _originalHadOverlappedStyle; // 配信開始前にタイトルバー等があったか(終了時にその状態へ戻す)
    private int _styleStripAttempts;
    private const int MaxStyleStripAttempts = 2;

    public WebRtcSession(SignalingClient client, HostConfig config)
    {
        _client = client;
        _config = config;
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

        // videoconvert -> vcrop の間(vcropのsinkパッド)の caps 変更を "notify::caps"
        // シグナル(GObjectの標準的なプロパティ変更通知)で監視し、
        // 奇数サイズが来たら即座に vcrop の right/bottom を設定して偶数に丸める。
        // ウィンドウのリサイズで再ネゴシエーションが起きるたびに自動で追従する。
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

        // 既定はプライマリモニタ全体の配信なので、マウス座標マッピングもそれに合わせる。
        InputInjector.SetCaptureRegion(ScreenGeometry.GetMonitorRect(0) ?? ScreenGeometry.PrimaryMonitorFallback);

        // マウス/キーボード等の制御入力用 DataChannel。
        // webrtcbin がまだ NULL 状態のうちに create-data-channel を呼ぶと
        // SCTP トランスポートが正しく準備されないことがあるため、
        // パイプラインを PLAYING にしてから作成する。
        // (これにより初回オファーに含まれず、作成時点で on-negotiation-needed が
        //  再度発火して2回目のネゴシエーションが走る可能性があるが、
        //  ブラウザ側は setRemoteDescription を都度受け入れる作りなので問題ない)
        CreateControlDataChannel();

        // ウィンドウ配信中のリサイズにマウス座標マッピングを追従させるための
        // 軽量ポーリング(debounce)。_currentWindowHandle が null の間(モニター配信中)は
        // 何もしない。
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
            if (_currentWindowHandle is not { } hwnd) return;
            if (!ScreenGeometry.IsWindow(hwnd)) return; // ウィンドウが閉じられた等。エラー処理は別途 SwitchSource/RecoverVideoBranch 側で行う。

            // タイトルバーを剥がした後、何らかの理由で復活していないか確認する。
            // (アプリが自前で再度スタイルを設定し直す、テーマ変更等で起こり得る)
            // 連続して復活し続ける場合は無限ループを避けるため一定回数で諦める。
            if (_originalHadOverlappedStyle == true && ScreenGeometry.HasOverlappedWindowStyle(hwnd))
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

            // タイトルバーの高さ自体はほぼ変わらないが、毎回計算しても軽量なので
            // 移動やテーマ変更にも追従できるよう都度再計算する。
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
                    // 今回のサイズ変化が「こちらが直前にリクエストした通り」かどうかを確認する。
                    var windowRect = ScreenGeometry.GetWindowRectSafe(hwnd);
                    if (windowRect is not null)
                    {
                        var matchesRequested = _lastRequestedWindowSize is { } req
                            && Math.Abs(windowRect.Value.Width - req.Width) <= 2
                            && Math.Abs(windowRect.Value.Height - req.Height) <= 2;

                        if (matchesRequested)
                        {
                            // こちら(ApplyRequestedClientSize)がリクエストした変更が反映された。
                            // WGC の再初期化はそちらで既に同期的に行い済みなので、ここでは何もしない。
                        }
                        else
                        {
                            // アプリが自発的にサイズを変えた(こちらの要求とは無関係)。
                            // ここでは配信サイズ(WGC)はまだ変えない。まずクライアントに
                            // 新しいサイズを通知し、クライアントが希望サイズを返してきたら
                            // ApplyRequestedClientSize 側でウィンドウのリサイズと配信の
                            // 更新をまとめて行う。
                            _lastRequestedWindowSize = (windowRect.Value.Width, windowRect.Value.Height);
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
                    SwitchSource(msg);
                    break;

                case "listWindows":
                    SendWindowList();
                    break;

                case "listMonitors":
                    SendMonitorList();
                    break;

                case "setViewportSize":
                    HandleViewportSize(msg.ViewportWidth, msg.ViewportHeight);
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

        // nvh264enc の "bitrate" プロパティは kbit/s 単位(x264enc と同じ慣習)。
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

            // 最小化されていると WGC がキャプチャできないため、事前に復元する。
            WindowEnumerator.RestoreIfMinimized(hwnd);

            if (_currentWindowHandle != hwnd)
            {
                // 別のウィンドウへの切り替えなら、前のウィンドウを元の位置・サイズ・スタイルへ
                // 戻してから、新しいウィンドウの「切り替え前」の状態を記録して
                // 指定モニターへ移動する。
                RestoreOriginalWindowRectIfNeeded();
                _originalWindowRect = ScreenGeometry.GetWindowRectSafe(hwnd);
                _lastRequestedWindowSize = null;
                MoveWindowToTargetMonitor(hwnd);

                // タイトルバー等(WS_OVERLAPPEDWINDOW)を取り除く。これにより
                // GetWindowRect(見た目)とクライアント領域(キャプチャ範囲)の差が
                // 無くなり、タイトルバー分のクロップ/ズレが原理的に発生しなくなる。
                // 「配信開始前に持っていたかどうか」だけを記録し、終了時はその状態に
                // 戻す(元から無いウィンドウなら何もしない)。
                _originalHadOverlappedStyle = ScreenGeometry.HasOverlappedWindowStyle(hwnd);
                _styleStripAttempts = 0;
                if (_originalHadOverlappedStyle == true)
                {
                    ScreenGeometry.RemoveOverlappedWindowStyle(hwnd);
                }
            }

            ApplySourceSwitch(() =>
            {
                GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", msg.WindowHandle.Value);
            });

            var clientRect = ApplyWindowChromeCrop(hwnd);
            InputInjector.SetCaptureRegion(clientRect ?? ScreenGeometry.PrimaryMonitorFallback);
            _currentWindowHandle = hwnd;
            _lastKnownWindowRect = clientRect;

            // サイズの決定はここで即座に(以前のクライアント希望値を使って)行わず、
            // 「アプリが自発的にリサイズした」場合と同じ通知→応答フローに統一する。
            // 現在のクライアント領域サイズをまず通知し、クライアントが計算した
            // 希望サイズが返ってきたら ApplyRequestedClientSize 側で適用する。
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

            ApplySourceSwitch(() =>
            {
                // window-handle を 0 に戻してウィンドウキャプチャモードを解除する。
                GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", 0);
                GstNativeInterop.SetInt(_captureSourceHandle, "monitor-index", msg.MonitorIndex.Value);
            });

            var rect = ScreenGeometry.GetMonitorRect(msg.MonitorIndex.Value);
            InputInjector.SetCaptureRegion(rect ?? ScreenGeometry.PrimaryMonitorFallback);
            _currentWindowHandle = null;
            _lastKnownWindowRect = null;
            ResetWindowChromeCrop();
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
    private void MoveWindowToTargetMonitor(IntPtr hwnd)
    {
        var monitorRect = ScreenGeometry.GetMonitorRect(_config.WindowTargetMonitorIndex) ?? ScreenGeometry.PrimaryMonitorFallback;
        var currentRect = ScreenGeometry.GetWindowRectSafe(hwnd);
        if (currentRect is null) return;

        Console.WriteLine($"[Config] Moving window to target monitor {_config.WindowTargetMonitorIndex} at ({monitorRect.X},{monitorRect.Y})");
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
        var monitorRect = ScreenGeometry.GetMonitorRect(_config.WindowTargetMonitorIndex) ?? ScreenGeometry.PrimaryMonitorFallback;

        var windowWidth = clientWidth;
        var windowHeight = clientHeight + _videoCropTopPixels;

        var x = monitorRect.X + Math.Max(0, (monitorRect.Width - windowWidth) / 2);
        var y = monitorRect.Y + Math.Max(0, (monitorRect.Height - windowHeight) / 2);

        Console.WriteLine($"[Config] Resizing window: clientArea={clientWidth}x{clientHeight} -> window={windowWidth}x{windowHeight}");
        ScreenGeometry.MoveResizeWindow(hwnd, x, y, windowWidth, windowHeight);
        _lastRequestedWindowSize = (windowWidth, windowHeight);

        // ポーリング(最大500ms後)を待たず、この場で反映させる。
        var clientRect = ApplyWindowChromeCrop(hwnd);
        InputInjector.SetCaptureRegion(clientRect ?? ScreenGeometry.PrimaryMonitorFallback);
        _lastKnownWindowRect = clientRect;

        ApplySourceSwitch(() =>
        {
            GstNativeInterop.SetUInt64(_captureSourceHandle, "window-handle", (ulong)hwnd.ToInt64());
        });
    }

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
        if (_currentWindowHandle is { } hwnd && ScreenGeometry.IsWindow(hwnd))
        {
            if (_originalHadOverlappedStyle == true)
            {
                Console.WriteLine("[Config] Restoring window title bar");
                ScreenGeometry.AddOverlappedWindowStyle(hwnd);
            }

            if (_originalWindowRect is { } orig)
            {
                Console.WriteLine("[Config] Restoring window to its original position/size");
                ScreenGeometry.MoveResizeWindow(hwnd, orig.X, orig.Y, orig.Width, orig.Height);
            }
        }

        _originalWindowRect = null;
        _originalHadOverlappedStyle = null;
        _styleStripAttempts = 0;
        _lastRequestedWindowSize = null;
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
    /// x264enc は一度初期化に失敗すると、単純に NULL -> PLAYING と状態を
    /// 遷移させるだけでは内部状態が正しくリセットされず、同じ映像が来ても
    /// 再度失敗し続けることが実機で確認された。そのため x264enc だけは
    /// 要素そのものを破棄して新しいインスタンスを作り直す(パイプラインに
    /// 差し込み直す)方式にしている。capturesrc/queue/payloader は
    /// 通常の NULL -> PLAYING サイクルで十分なため、そちらはそのまま。
    /// webrtcbin には一切触れない。
    /// </summary>
    private void RecoverVideoBranch()
    {
        // 同一障害から立て続けに複数の ERROR メッセージが飛んでくることが多いため、
        // 短時間のうちに何度も復旧処理を走らせないようデバウンスする。
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

            // 新しい x264enc をリンクまでは行うが、まだ PLAYING にはしない。
            // 周囲の要素がまだ NULL のうちにエンコーダだけ先に PLAYING へ同期させると
            // 不安定になる(クラッシュする)ことが実機で確認されたため、
            // 「周辺を先に立ち上げてから、最後にエンコーダを立ち上げる」順序にしている。
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
    /// x264enc 要素を破棄し、同じプロパティで新しいインスタンスを作り直してパイプラインに
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

        Console.WriteLine("[Config] Recreating x264enc element (full reset) ...");

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
        // Bin に追加すると GstBin が自動的に親(パイプライン)の現在の状態へ
        // 同期させようとすることがあるため、ロックして防いでおく。
        // 明示的に PLAYING にするのは呼び出し元が周囲の要素を立ち上げた後。
        newVenc.SetLockedState(true);
        _pipeline.Add(newVenc);

        var newSink = newVenc.GetStaticPad("sink");
        var newSrc = newVenc.GetStaticPad("src");

        if (beforePad is not null && newSink is not null) beforePad.Link(newSink);
        if (afterPad is not null && newSrc is not null) newSrc.Link(afterPad);

        _videoEncoderElement = newVenc;
        // BinFromDescription はラップした Bin を返すため、bitrate 等のプロパティ操作は
        // 中の実体である x264enc 要素そのものに対して行う必要がある。
        _videoEncoderHandle = (newVenc as Bin)?.GetByName("venc")?.Handle ?? IntPtr.Zero;

        Console.WriteLine("[Config] x264enc recreated (linked, not yet playing)");
        return newVenc;
    }

    private void SendWindowList()
    {
        if (_controlChannelHandle == IntPtr.Zero || !_isControlChannelOpen) return;

        var windows = WindowEnumerator.ListVisibleWindows();
        var json = JsonSerializer.Serialize(new { type = "windowList", windows });
        GstNativeInterop.DataChannelSendString(_controlChannelHandle, json);
        Console.WriteLine($"[Config] Sent window list ({windows.Count} windows)");
    }

    private void SendMonitorList()
    {
        if (_controlChannelHandle == IntPtr.Zero || !_isControlChannelOpen) return;

        var monitors = ScreenGeometry.GetMonitorInfos();
        var json = JsonSerializer.Serialize(new { type = "monitorList", monitors });
        GstNativeInterop.DataChannelSendString(_controlChannelHandle, json);
        Console.WriteLine($"[Config] Sent monitor list ({monitors.Count} monitors)");
    }

    private static string BuildPipelineDescription(HostConfig config)
    {
        var videoSource = config.UseTestSource
            ? "videotestsrc name=capturesrc is-live=true pattern=ball !"
            // capture-api=1 (WGC/Windows Graphics Capture) に固定しておくことで、
            // モニター全体キャプチャ(monitor-index)とウィンドウキャプチャ(window-handle)を
            // 実行中にプロパティ変更だけで切り替えられるようにする。
            // (capture-api=0 の Desktop Duplication API はウィンドウ単位キャプチャ非対応)
            // ※ プロパティ名/値は環境のGStreamerバージョンで `gst-inspect-1.0 d3d11screencapturesrc`
            //    を実行し実際の名称と一致するか確認すること。
            : $"d3d11screencapturesrc name=capturesrc capture-api=1 monitor-index=0 show-cursor=true do-timestamp=true ! video/x-raw(memory:D3D11Memory),framerate={config.Framerate}/1 ! d3d11convert ! video/x-raw(memory:D3D11Memory),format=NV12 ! d3d11download !";

        var audioSource = config.UseTestSource
            ? "audiotestsrc name=audiosrc is-live=true wave=sine !"
            : "wasapisrc name=audiosrc loopback=true low-latency=true do-timestamp=true !";

        var videoCapsAndPay = config.Codec == VideoCodec.Hevc
            // H.265/HEVC: libx264/nvh264enc よりも効率的だが、WebRTCでのブラウザ対応は
            // 限定的(2026年時点でも Chrome は非対応環境が多く、Safari の一部バージョンのみ
            // 対応)。事前に受信側ブラウザの対応状況を確認してから使うこと。
            ? "rtph265pay name=vpay config-interval=-1 pt=96 aggregate-mode=zero-latency !"
            : "video/x-h264,profile=baseline ! rtph264pay name=vpay config-interval=-1 pt=96 aggregate-mode=zero-latency !";

        var parts = new List<string>
        {
            $"webrtcbin name=sendrecv bundle-policy=max-bundle stun-server={config.StunServer}",

            // 映像: プライマリモニタ(または切り替え後の対象)をキャプチャしてエンコードし RTP 化
            // 各要素に名前を付け、エラー発生時に映像経路だけをまとめて NULL -> PLAYING で
            // 再初期化できるようにしている。
            videoSource,
            "videoconvert name=vconv !",
            "videocrop name=vcrop !",
            "video/x-raw,format=I420 !",
            "queue name=vqueue1 max-size-buffers=1 leaky=downstream !",
            "videoconvert name=vconv2 !",
            BuildVideoEncoderDescription(config, 4000) + " !",
            videoCapsAndPay,
            // queue のデフォルトは最大1秒分(max-size-time=1s)まで溜め込めるため、下流(webrtcbin)が
            // 一時的に詰まった際に最大約1秒の遅延がそのまま残る恐れがある。上限を100msに絞る。
            // ここは RTP パケット(エンコード済み)なので leaky にはしない(パケットを落とすと
            // 映像が壊れる)。満杯になったら上流のエンコーダが待たされ、vqueue1(leaky)が
            // 未エンコードのフレームを間引くため、遅延は溜まらず映像も壊れない。
            // (max-size-buffers/bytes を 0 にして時間のみで制限。キーフレームは多数のパケットに
            //  分割されるため、バッファ数で制限するとキーフレーム送出が不必要に詰まる)
            "queue name=vqueue2 max-size-buffers=0 max-size-bytes=0 max-size-time=100000000 ! sendrecv.",

        };

        if (config.EnableAudio)
        {
            parts.AddRange(new[]
            {
            // 音声: 既定の再生デバイスをループバックキャプチャして Opus でエンコードし RTP 化
                // volume 要素でミュート/音量を制御できるようにする。
                audioSource,
                "audioconvert ! audioresample !",
                "volume name=vol !",
                "queue max-size-buffers=1 leaky=downstream !",
                "opusenc bitrate=64000 !",
                "rtpopuspay pt=97 !",
                // 音声も同様に上限を100msに絞る。音声パケットは1つ落ちても一瞬のノイズで済むため leaky にする。
                "queue max-size-buffers=0 max-size-bytes=0 max-size-time=100000000 leaky=downstream ! sendrecv.",
            });
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
        // NVIDIA GPU (NVENC) 前提。GPUが無い環境では x264enc 等への差し替えが必要
        // (ただし x264enc は失敗後の再初期化が不安定なことが実機で確認されている点に注意)。
        return config.Codec == VideoCodec.Hevc
            ? $"nvh265enc name=venc bitrate={bitrateKbps} zerolatency=true rc-mode=cbr preset=low-latency-hq"
            : $"nvh264enc name=venc bitrate={bitrateKbps} zerolatency=true rc-mode=cbr preset=low-latency-hq";
    }

    // ---- webrtcbin コールバック --------------------------------------------------

    // NOTE: これらのメソッドは全てネイティブコード(GLibシグナル/リバースP/Invoke)から
    // 直接呼び出される。ここで例外を外に漏らすと、.NETはマネージド例外をネイティブの
    // 呼び出しスタックへ安全に unwind できず、プロセスが「エラーメッセージなしに」
    // 即座に終了する。そのため全ての本体を try/catch で確実に囲むこと。

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

            // ここでの offerDescPtr は g_value_dup_boxed() で複製した「自分の所有物」なので、
            // 公式サンプルと同じく emit -> interrupt -> unref -> free の同期パターンでよい。
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
                var cropRight = width % 2 != 0 ? 1 : 0;

                // top を既にタイトルバー除去用にクロップしている場合、その分を差し引いた
                // 「最終的な出力の高さ」が偶数になるよう bottom を調整する必要がある。
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

                    // 映像経路(capturesrc/vqueue1/venc/vpay/vqueue2)由来のエラーのみ、
                    // 自動復旧の対象にする。webrtcbin 自体のエラー等はここでは扱わない。
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

    // ---- ブラウザからのシグナリングメッセージ処理 --------------------------------

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
        var sdpMessagePtr = GstNativeInterop.ParseSdp(sdpText);
        var answerDescPtr = GstNativeInterop.WebRtcSessionDescriptionNew(
            GstNativeInterop.WebRTCSDPType.Answer, sdpMessagePtr);

        // answerDescPtr はこちらで gst_webrtc_session_description_new() により新規生成した
        // 自分の所有物なので、公式サンプルと同じ同期パターンで解放してよい。
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
        // 引き継ぎ(takeover)と Disconnected の両方から呼ばれうるので、二重実行を防ぐ
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        RestoreOriginalWindowRectIfNeeded();
        _resizeWatchTimer?.Dispose();
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

    /// <summary>false の場合は音声パイプラインを構築しない(接続クエリ ?audio=0)。</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>キャプチャフレームレート(接続時にクエリパラメータ ?fps=60 で上書き可能)。</summary>
    public int Framerate { get; set; } = 30;

    /// <summary>映像コーデック(接続時にクエリパラメータ ?codec=hevc で上書き可能)。</summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>
    /// ウィンドウ配信時に対象ウィンドウを移動する先のモニター番号(固定)。
    /// DPI拡大率100%・十分なサイズがあるモニターを想定している。
    /// </summary>
    public int WindowTargetMonitorIndex { get; set; } = 1;
}

internal enum VideoCodec
{
    H264,
    Hevc,
}
