using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteDesktopHost;

/// <summary>
/// DataChannel経由で受け取った制御メッセージを、Win32の SendInput / SetCursorPos で
/// 実際のマウス・キーボード操作として注入する。
///
/// 注意(既知の制約):
/// - このプロセスと同じデスクトップセッション(対話ログオンセッション)でしか動作しない。
///   サービスとして実行したり、別セッション(RDP等)から起動した場合は入力が届かない。
/// - UIPI (User Interface Privilege Isolation) の制約により、管理者権限で実行されている
///   ウィンドウに対して、非管理者権限のこのプロセスから入力を送ることはできない。
///   ホストアプリを管理者として実行するか、対象アプリを昇格させない運用にすること。
/// - 画面ロック中は入力が届かない。
/// </summary>
internal static class InputInjector
{
    // ---- Win32 構造体定義 -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // ---- タッチ (CreateSyntheticPointerDevice / InjectSyntheticPointerInput) ----------
    //
    // 経緯: 当初は Windows 8 世代の InitializeTouchInjection / InjectTouchInput で実装したが、
    // 構造体レイアウト・フラグの組み合わせ・座標範囲・タッチデジタイザの有無・DPI設定を
    // 一通り検証しても down/move/up すべてで ERROR_INVALID_PARAMETER (87) が解消しなかった。
    // そのため、Windows 10 1809 以降で使える新しい注入API
    // (CreateSyntheticPointerDevice / InjectSyntheticPointerInput) に切り替える。
    //
    // 旧APIとの主な違い:
    // - プロセス全体で一度だけ初期化する(InitializeTouchInjection)のではなく、
    //   明示的な「注入デバイスハンドル」(HSYNTHETICPOINTERDEVICE)を CreateSyntheticPointerDevice
    //   で作成し、そのハンドルを InjectSyntheticPointerInput に渡す。
    // - このハンドルは「作成したスレッドに紐づく」ため、旧APIと同様に
    //   InjectSyntheticPointerInput を呼ぶスレッドごとに、そのスレッドでハンドルを
    //   作成しておく必要がある。
    // - 注入する構造体は POINTER_TOUCH_INFO 自体は変わらず、それを POINTER_TYPE_INFO で
    //   ラップして渡す形になる。
    //
    // まずはリスクを減らすためシングルタッチ(同時に1点のみ)に限定して実装する。

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOUCH_RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public int pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public TOUCH_RECT rcContact;
        public TOUCH_RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    // POINTER_TYPE_INFO は本来 { type; union { POINTER_INFO; POINTER_TOUCH_INFO; POINTER_PEN_INFO; } } という
    // 共用体だが、この実装ではタッチしか使わないため touchInfo だけを明示的にオフセット8(type分4byte+パディング4byte)
    // に配置する形で必要十分な部分だけを再現する。count=1でしか使わない(配列化しない)ため、
    // 共用体の最大サイズ(pen込みで152byte)に厳密に合わせる必要はない。
    [StructLayout(LayoutKind.Explicit)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public POINTER_TOUCH_INFO touchInfo;
    }

    private const int PT_TOUCH = 0x00000002;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_MASK_PRESSURE = 0x00000004;

    // pressure は 0〜1024 の範囲で、1024 が最大圧力を意味する。
    private const uint TOUCH_PRESSURE_MAX = 1024;

    // POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_DEFAULT = 1 (旧APIのTOUCH_FEEDBACK_DEFAULTと同値)
    private const int POINTER_FEEDBACK_DEFAULT = 1;

    // 同時に扱えるタッチ数。CreateSyntheticPointerDevice の maxCount にもそのまま使う。
    private const uint MaxTouchContacts = 10;

    private sealed class TouchState
    {
        public uint PointerId;
        // 絶対デスクトップ座標(仮想画面相対への変換は注入直前に行う)。
        public int X;
        public int Y;
    }

    // JS の Touch.identifier (ブラウザによっては大きい値/負の値になり得る) を、
    // Win32 の pointerId 用に 0〜MaxTouchContacts-1 の小さい値へマッピングして管理する。
    private static readonly Dictionary<int, TouchState> ActiveTouches = new();
    private static readonly Queue<uint> FreeTouchSlots = BuildInitialTouchSlots();

    private static Queue<uint> BuildInitialTouchSlots()
    {
        var queue = new Queue<uint>();
        for (uint i = 0; i < MaxTouchContacts; i++) queue.Enqueue(i);
        return queue;
    }

    private static readonly object TouchLock = new();
    private static uint _touchFrameId;

    // HandleTouch (呼び出し元はDataChannelのコールバックスレッド)で反映した状態変化のうち、
    // まだ実際に注入(Inject)していない分。専用スレッド側で読み取って消費する。
    private static readonly HashSet<int> PendingNewDownIds = new();
    private static readonly HashSet<int> PendingEndingIds = new();

    // 「触れているが動いていない」状態でもWindows側に継続的にタッチ信号を送り続けるための
    // 専用スレッド。これが無いと、ブラウザは touchmove を発火しない=何も送られない状態になり、
    // Windows の「長押しで右クリック」ジェスチャー判定が正しく働かない
    // (実タッチデジタイザは静止中も一定間隔で信号を送り続けているのが前提のため)。
    // 単指静止/単指移動後静止/複数指、のいずれについても、ここでは判定ロジックを持たず、
    // 単に「今の状態のまま」の信号を送り続けるだけにする。実際の長押し→右クリック昇格や
    // 複数指の除外判定は Windows 自身のジェスチャー認識エンジンに任せる。
    private const int TouchKeepAliveIntervalMs = 30;
    private static readonly AutoResetEvent TouchWakeEvent = new(false);
    private static Thread? _touchInjectorThread;
    private static readonly object TouchThreadStartLock = new();

    // CreateSyntheticPointerDevice が返すハンドルは「作成したスレッドに紐づく」ため、
    // InjectSyntheticPointerInput を呼ぶスレッドごとに(初回のみ)個別に作成する必要がある。
    [ThreadStatic]
    private static IntPtr _touchDeviceOnThisThread;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);

    // 診断用: 送信しようとしている座標が実際に「デスクトップの範囲内」に収まっているか
    // 確認するために、仮想デスクトップ(全モニターを包含する矩形)の範囲を取得する。
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    /// <summary>
    /// NormalizedToScreen が返す座標は「仮想デスクトップ上の絶対座標」
    /// (プライマリモニターより左/上にモニターがある場合は負値になり得る)。
    ///
    /// しかし InjectSyntheticPointerInput の ptPixelLocation は
    /// 「仮想画面の左上を基準にした相対座標」(=常に0以上)を要求する仕様のため、
    /// 注入直前にここで変換する。
    /// (旧APIの InjectTouchInput は絶対座標のままでよかったため、この変換は不要だった)
    ///
    /// マウス(SetCursorPos)は絶対座標のままで正しいため、この変換は
    /// タッチ注入専用であり、TouchState.X/Y (アクティブタッチの保持座標)自体は
    /// 絶対座標のまま保持する。
    /// </summary>
    private static (int X, int Y) ToVirtualScreenRelative(int absoluteX, int absoluteY)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return (absoluteX - vx, absoluteY - vy);
    }

    /// <summary>
    /// InjectSyntheticPointerInput を呼ぶ直前に、呼び出し元スレッドで一度だけ
    /// 注入デバイスハンドルを作成する。別スレッドから呼ばれた場合は、
    /// そのスレッドで改めて作成される(ハンドルはスレッドに紐づくため)。
    /// 失敗時は IntPtr.Zero を返す。
    /// </summary>
    private static IntPtr GetOrCreateTouchDeviceOnCurrentThread()
    {
        if (_touchDeviceOnThisThread != IntPtr.Zero) return _touchDeviceOnThisThread;

        var device = CreateSyntheticPointerDevice(PT_TOUCH, MaxTouchContacts, POINTER_FEEDBACK_DEFAULT);
        if (device == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[Input] CreateSyntheticPointerDevice failed on thread {Environment.CurrentManagedThreadId} (Win32 error {err})");
            return IntPtr.Zero;
        }

        Console.WriteLine($"[Input] CreateSyntheticPointerDevice succeeded on thread {Environment.CurrentManagedThreadId} (handle=0x{device:X})");
        _touchDeviceOnThisThread = device;
        return device;
    }

    /// <summary>
    /// タッチの down/move/up/cancel を受け取り、アクティブなタッチの状態を更新して
    /// 専用の注入スレッド(TouchInjectorThread)を起こす。実際の InjectSyntheticPointerInput
    /// 呼び出しはこのメソッドの中では行わない(常に同じスレッドから呼ぶ必要があるため)。
    /// </summary>
    public static void HandleTouch(IReadOnlyList<ControlMessage.TouchPointDto>? touches)
    {
        if (touches is null || touches.Count == 0) return;

        EnsureTouchInjectorThreadStarted();

        lock (TouchLock)
        {
            foreach (var t in touches)
            {
                switch (t.Phase)
                {
                    case "down":
                        if (!ActiveTouches.ContainsKey(t.Id))
                        {
                            if (FreeTouchSlots.Count == 0)
                            {
                                Console.WriteLine("[Input] Touch slot exhausted (too many simultaneous touches), ignoring new touch");
                                continue;
                            }
                            var (sx, sy) = NormalizedToScreen(t.X, t.Y);
                            ActiveTouches[t.Id] = new TouchState
                            {
                                PointerId = FreeTouchSlots.Dequeue(),
                                X = sx,
                                Y = sy,
                            };
                            PendingNewDownIds.Add(t.Id);
                        }
                        break;

                    case "move":
                        if (ActiveTouches.TryGetValue(t.Id, out var moveState))
                        {
                            var (sx, sy) = NormalizedToScreen(t.X, t.Y);
                            moveState.X = sx;
                            moveState.Y = sy;
                        }
                        break;

                    case "up":
                    case "cancel":
                        // 受信した座標は使わず、直前の move で保持している座標をそのまま使う。
                        if (ActiveTouches.ContainsKey(t.Id))
                        {
                            PendingEndingIds.Add(t.Id);
                        }
                        break;

                    default:
                        Console.WriteLine($"[Input] Unknown touch phase: {t.Phase}");
                        break;
                }
            }
        }

        // 実際の注入は専用スレッドに任せる。すぐに反映されるよう起こしておく。
        TouchWakeEvent.Set();
    }

    private static void EnsureTouchInjectorThreadStarted()
    {
        if (_touchInjectorThread is not null) return;

        lock (TouchThreadStartLock)
        {
            if (_touchInjectorThread is not null) return;

            var thread = new Thread(TouchInjectorThreadLoop)
            {
                IsBackground = true,
                Name = "TouchInjector",
            };
            _touchInjectorThread = thread;
            thread.Start();
        }
    }

    /// <summary>
    /// タッチ注入専用のバックグラウンドスレッド。CreateSyntheticPointerDevice は
    /// このスレッド上で一度だけ作成し、以後の InjectSyntheticPointerInput 呼び出しも
    /// (キープアライブも含めて)すべてこのスレッドから行う。
    ///
    /// TouchWakeEvent により以下の2パターンで起こされる:
    /// - HandleTouch でタッチ状態が変化した直後(即座に反映するため)
    /// - 何も変化がなくても TouchKeepAliveIntervalMs ごと(タイムアウトで自動的に起きる)
    ///
    /// 後者のキープアライブが無いと、指を動かさない間はブラウザから何もメッセージが
    /// 送られてこないため Windows 側のタッチ信号が途切れてしまい、長押し→右クリックの
    /// ジェスチャー判定が正しく働かない(実タッチデジタイザは静止中も一定間隔で
    /// 信号を送り続けているのが前提のため)。ここでは単に「今の状態のまま」の信号を
    /// 送り続けるだけにし、長押し昇格や複数指の除外判定自体は Windows 自身の
    /// ジェスチャー認識エンジンに任せる。
    /// </summary>
    private static void TouchInjectorThreadLoop()
    {
        var device = GetOrCreateTouchDeviceOnCurrentThread();
        if (device == IntPtr.Zero)
        {
            Console.WriteLine("[Input] Touch injector thread failed to create a synthetic pointer device; touch injection is disabled.");
            return;
        }

        while (true)
        {
            TouchWakeEvent.WaitOne(TouchKeepAliveIntervalMs);

            lock (TouchLock)
            {
                if (ActiveTouches.Count == 0) continue;

                _touchFrameId++;
                var wrapped = new POINTER_TYPE_INFO[ActiveTouches.Count];
                var sentDownIds = new List<int>();
                var sentUpIds = new List<int>();
                var i = 0;

                foreach (var (jsId, state) in ActiveTouches)
                {
                    uint flags;

                    // down->up が1サイクル内で両方保留になっているケース(非常に短いタップ等)では、
                    // 先に down を送ってから、次のサイクルで up を送る(同一フレームで
                    // 「無→UP」にはできないため)。
                    if (PendingNewDownIds.Contains(jsId))
                    {
                        flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                        sentDownIds.Add(jsId);
                    }
                    else if (PendingEndingIds.Contains(jsId))
                    {
                        flags = POINTER_FLAG_UP;
                        sentUpIds.Add(jsId);
                    }
                    else
                    {
                        // 実際に動いたかどうかに関わらず UPDATE として送り続ける
                        // (静止したままのキープアライブもこれで表現する)。
                        flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                    }

                    // ptPixelLocation は「仮想画面の左上基準の相対座標」を要求するため、
                    // ここで絶対座標から変換する(state.X/Y自体は絶対座標のまま)。
                    var (injectX, injectY) = ToVirtualScreenRelative(state.X, state.Y);
                    var contact = BuildTouchInfo(state.PointerId, injectX, injectY, flags, _touchFrameId);
                    wrapped[i++] = new POINTER_TYPE_INFO { type = (uint)PT_TOUCH, touchInfo = contact };
                }

                if (!InjectSyntheticPointerInput(device, wrapped, (uint)wrapped.Length))
                {
                    var err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[Input] InjectSyntheticPointerInput failed on touch injector thread (Win32 error {err})");
                }

                foreach (var id in sentDownIds) PendingNewDownIds.Remove(id);

                foreach (var id in sentUpIds)
                {
                    PendingEndingIds.Remove(id);
                    if (ActiveTouches.TryGetValue(id, out var st))
                    {
                        FreeTouchSlots.Enqueue(st.PointerId);
                    }
                    ActiveTouches.Remove(id);
                }
            }
        }
    }

    private static POINTER_TOUCH_INFO BuildTouchInfo(uint id, int x, int y, uint flags, uint frameId)
    {
        const int contactRadius = 5;
        var rect = new TOUCH_RECT { Left = x - contactRadius, Top = y - contactRadius, Right = x + contactRadius, Bottom = y + contactRadius };

        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = PT_TOUCH,
                pointerId = id,
                frameId = frameId,
                pointerFlags = flags,
                ptPixelLocation = new POINT { X = x, Y = y },
            },
            touchFlags = 0,
            touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE,
            rcContact = rect,
            rcContactRaw = rect,
            orientation = 0,
            pressure = TOUCH_PRESSURE_MAX,
        };
    }

    // 現在配信中の領域(オフセット+サイズ)。switchSource で切り替わるたびに更新される。
    // 既定値はプライマリモニタ全体。
    private static ScreenGeometry.Rect _captureRegion = ScreenGeometry.PrimaryMonitorFallback;

    /// <summary>
    /// 配信元(モニター/ウィンドウ)を切り替えた際に、マウス絶対座標のマッピング先を
    /// 更新するために呼ぶ。
    /// </summary>
    public static void SetCaptureRegion(ScreenGeometry.Rect region)
    {
        _captureRegion = region;
        Console.WriteLine($"[Input] Capture region updated: x={region.X}, y={region.Y}, w={region.Width}, h={region.Height}");
    }

    // ---- 公開API ----------------------------------------------------------------

    public static void Handle(ControlMessage msg)
    {
        try
        {
            switch (msg.Type)
            {
                case "mouseMove":
                    if (msg.Mode == "relative" && msg.Dx.HasValue && msg.Dy.HasValue)
                        MoveRelative(msg.Dx.Value, msg.Dy.Value);
                    else if (msg.Mode == "absolute" && msg.X.HasValue && msg.Y.HasValue)
                        MoveAbsoluteNormalized(msg.X.Value, msg.Y.Value);
                    break;

                case "mouseButton":
                    SendMouseButton(msg.Button, msg.Action);
                    break;

                case "mouseWheel":
                    if (msg.Dy.HasValue && msg.Dy.Value != 0) SendWheel(msg.Dy.Value, horizontal: false);
                    if (msg.Dx.HasValue && msg.Dx.Value != 0) SendWheel(msg.Dx.Value, horizontal: true);
                    break;

                case "keyDown":
                    if (msg.Code is not null) SendKey(msg.Code, down: true);
                    break;

                case "keyUp":
                    if (msg.Code is not null) SendKey(msg.Code, down: false);
                    break;

                case "touch":
                    HandleTouch(msg.Touches);
                    break;

                default:
                    Console.WriteLine($"[Input] Unhandled control message type: {msg.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Handle() は DataChannel の受信コールバック(リバース P/Invoke)から直接
            // 呼ばれるため、ここでも必ず例外を握りつぶしてログに残す。
            Console.WriteLine($"[Input][FATAL] {ex}");
        }
    }

    private static void MoveAbsoluteNormalized(double nx, double ny)
    {
        var (x, y) = NormalizedToScreen(nx, ny);
        SetCursorPos(x, y);
    }

    /// <summary>
    /// 現在配信中の領域(モニター全体 or 特定ウィンドウ)を基準に、正規化座標(0.0〜1.0)を
    /// 実際のスクリーンピクセル座標に変換する。マウスの絶対移動・タッチ注入の両方で使う。
    /// </summary>
    private static (int X, int Y) NormalizedToScreen(double nx, double ny)
    {
        var region = _captureRegion;
        var x = region.X + (int)Math.Round(Math.Clamp(nx, 0, 1) * Math.Max(region.Width - 1, 0));
        var y = region.Y + (int)Math.Round(Math.Clamp(ny, 0, 1) * Math.Max(region.Height - 1, 0));
        return (x, y);
    }

    private static void MoveRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
        };
        SendInput(1, new[] { input }, InputSize);
    }

    private static void SendMouseButton(string? button, string? action)
    {
        uint flags = (button, action) switch
        {
            ("left", "down") => MOUSEEVENTF_LEFTDOWN,
            ("left", "up") => MOUSEEVENTF_LEFTUP,
            ("right", "down") => MOUSEEVENTF_RIGHTDOWN,
            ("right", "up") => MOUSEEVENTF_RIGHTUP,
            ("middle", "down") => MOUSEEVENTF_MIDDLEDOWN,
            ("middle", "up") => MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };

        if (flags == 0)
        {
            Console.WriteLine($"[Input] Unknown mouse button/action: {button}/{action}");
            return;
        }

        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, new[] { input }, InputSize);
    }

    private static void SendWheel(int delta, bool horizontal)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                },
            },
        };
        SendInput(1, new[] { input }, InputSize);
    }

    private static void SendKey(string jsCode, bool down)
    {
        if (!KeyCodeMap.TryGetValue(jsCode, out var vk))
        {
            Console.WriteLine($"[Input] Unknown key code: {jsCode}");
            return;
        }

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
        };
        SendInput(1, new[] { input }, InputSize);
    }

    // ブラウザの KeyboardEvent.code (物理キー位置ベース、レイアウト非依存) から
    // Windows 仮想キーコード (VK_*) への対応表。
    private static readonly Dictionary<string, ushort> KeyCodeMap = BuildKeyCodeMap();

    private static Dictionary<string, ushort> BuildKeyCodeMap()
    {
        var map = new Dictionary<string, ushort>();

        for (var c = 'A'; c <= 'Z'; c++) map[$"Key{c}"] = c;
        for (var c = '0'; c <= '9'; c++) map[$"Digit{c}"] = c;
        for (var i = 1; i <= 12; i++) map[$"F{i}"] = (ushort)(0x70 + (i - 1));
        for (var i = 0; i <= 9; i++) map[$"Numpad{i}"] = (ushort)(0x60 + i);

        map["Escape"] = 0x1B;
        map["Enter"] = 0x0D;
        map["NumpadEnter"] = 0x0D;
        map["Tab"] = 0x09;
        map["Backspace"] = 0x08;
        map["Space"] = 0x20;
        map["CapsLock"] = 0x14;
        map["ShiftLeft"] = 0xA0;
        map["ShiftRight"] = 0xA1;
        map["ControlLeft"] = 0xA2;
        map["ControlRight"] = 0xA3;
        map["AltLeft"] = 0xA4;
        map["AltRight"] = 0xA5;
        map["MetaLeft"] = 0x5B;
        map["MetaRight"] = 0x5C;
        map["ArrowLeft"] = 0x25;
        map["ArrowUp"] = 0x26;
        map["ArrowRight"] = 0x27;
        map["ArrowDown"] = 0x28;
        map["Insert"] = 0x2D;
        map["Delete"] = 0x2E;
        map["Home"] = 0x24;
        map["End"] = 0x23;
        map["PageUp"] = 0x21;
        map["PageDown"] = 0x22;
        map["Minus"] = 0xBD;
        map["Equal"] = 0xBB;
        map["BracketLeft"] = 0xDB;
        map["BracketRight"] = 0xDD;
        map["Backslash"] = 0xDC;
        map["Semicolon"] = 0xBA;
        map["Quote"] = 0xDE;
        map["Comma"] = 0xBC;
        map["Period"] = 0xBE;
        map["Slash"] = 0xBF;
        map["Backquote"] = 0xC0;
        map["NumpadAdd"] = 0x6B;
        map["NumpadSubtract"] = 0x6D;
        map["NumpadMultiply"] = 0x6A;
        map["NumpadDivide"] = 0x6F;
        map["NumpadDecimal"] = 0x6E;

        return map;
    }
}