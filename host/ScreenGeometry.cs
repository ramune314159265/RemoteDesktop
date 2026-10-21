using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace RemoteDesktopHost;

/// <summary>
/// モニターやウィンドウの画面上の矩形(オフセット+サイズ)を取得する。
/// マウスの絶対座標(ダイレクトカーソル)モードで、正規化座標(0.0〜1.0)を
/// 実際のスクリーン座標へ変換するために使う。
/// </summary>
internal static class ScreenGeometry
{
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public static readonly Rect PrimaryMonitorFallback =
        new(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>
    /// モニターの矩形一覧を取得する。
    /// 注意: 並び順は Win32 の列挙順であり、d3d11screencapturesrc の monitor-index
    /// (DXGI の列挙順)と厳密に一致する保証はない(特に複数GPU構成の場合)。
    /// ズレる場合は実機で対応関係を確認して調整すること。
    /// </summary>
    public static List<Rect> GetMonitorRects()
    {
        var result = new List<Rect>();

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr dwData)
        {
            result.Add(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return result;
    }

    public static Rect? GetMonitorRect(int index)
    {
        var rects = GetMonitorRects();
        return index >= 0 && index < rects.Count ? rects[index] : null;
    }

    public static int GetMonitorCount() => GetMonitorRects().Count;

    // ---- モニターの名称取得 (GetMonitorInfo + EnumDisplayDevices) ----------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    // ---- モニターの正確な製品名取得 (DisplayConfig API) --------------------------------
    //
    // EnumDisplayDevices の DeviceString は、モニタ側の ICM/INF が無い環境では
    // 「Generic PnP Monitor」のような汎用名になることが多い。Windows の「設定」アプリが
    // 表示しているような実際の製品名(EDID由来のフレンドリ名)を取得するには、代わりに
    // QueryDisplayConfig / DisplayConfigGetDeviceInfo (DISPLAYCONFIG_TARGET_DEVICE_NAME)
    // を使う必要がある。
    //
    // 手順:
    //   1. QueryDisplayConfig で現在アクティブな各パス(GPU出力=ソース -> モニタ=ターゲット)を列挙する
    //   2. 各パスのソース情報から GDI デバイス名 (例: "\\.\DISPLAY1") を取得し、
    //      EnumDisplayMonitors/GetMonitorInfo で得た szDevice と突き合わせる
    //   3. 一致したパスのターゲット情報 (adapterId + id) を使って、
    //      DISPLAYCONFIG_TARGET_DEVICE_NAME から EDID 由来のフレンドリ名を取得する
    //
    // 取得できない(仮想ディスプレイ、リモートデスクトップのセッション等)場合は、
    // 呼び出し側で従来の EnumDisplayDevices ベースの名前にフォールバックする。

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering;
        // ネイティブ側は BOOL。.NET の bool を素朴に使うと既定で VARIANT_BOOL (2byte) として
        // マーシャリングされ構造体全体のサイズがズレるため、明示的に int (4byte) で受ける。
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    // ネイティブの DISPLAYCONFIG_MODE_INFO は共用体を含む 64 バイト固定長の構造体。
    // 中身(モード情報)は使わず、QueryDisplayConfig に渡す配列の要素サイズを
    // 合わせるためだけに用意する。
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO_PLACEHOLDER
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    // DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS.friendlyNameFromEdid ビット。
    // これが立っていない場合、monitorFriendlyDeviceName は信頼できない
    // (空文字や内部的なプレースホルダになっていることがある)。
    private const uint DISPLAYCONFIG_TARGET_FLAG_FRIENDLY_NAME_FROM_EDID = 0x1;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO_PLACEHOLDER[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>
    /// GDIデバイス名 (MONITORINFOEX.szDevice、例: "\\.\DISPLAY1") から、
    /// DisplayConfigGetDeviceInfo でターゲット名を引くための (adapterId, targetId) への
    /// マップを構築する。取得に失敗した場合は空の辞書を返す
    /// (呼び出し側が従来のフォールバック名を使う)。
    /// </summary>
    private static Dictionary<string, (LUID AdapterId, uint TargetId)> GetGdiDeviceNameToTargetMap()
    {
        var result = new Dictionary<string, (LUID, uint)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            int err;
            uint pathCount, modeCount;
            DISPLAYCONFIG_PATH_INFO[] paths;
            DISPLAYCONFIG_MODE_INFO_PLACEHOLDER[] modes;

            do
            {
                err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                if (err != ERROR_SUCCESS) return result;

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO_PLACEHOLDER[modeCount];
                err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                // 2回の呼び出しの間にディスプレイ構成が変わった場合、バッファが足りなくなることがあるためリトライする。
            } while (err == ERROR_INSUFFICIENT_BUFFER);

            if (err != ERROR_SUCCESS) return result;

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        Type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        AdapterId = path.SourceInfo.AdapterId,
                        Id = path.SourceInfo.Id,
                    },
                };

                if (DisplayConfigGetDeviceInfo(ref sourceName) != ERROR_SUCCESS) continue;
                if (string.IsNullOrEmpty(sourceName.ViewGdiDeviceName)) continue;

                result[sourceName.ViewGdiDeviceName] = (path.TargetInfo.AdapterId, path.TargetInfo.Id);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] DisplayConfig によるモニター名解決に失敗しました: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 指定ターゲット(モニタ)の EDID 由来のフレンドリ名(実際の製品名)を取得する。
    /// 取得できない、または EDID 由来でない場合は null を返す。
    /// </summary>
    private static string? TryGetMonitorFriendlyName(LUID adapterId, uint targetId)
    {
        try
        {
            var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    Type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    AdapterId = adapterId,
                    Id = targetId,
                },
            };

            if (DisplayConfigGetDeviceInfo(ref targetName) != ERROR_SUCCESS) return null;
            if ((targetName.Flags & DISPLAYCONFIG_TARGET_FLAG_FRIENDLY_NAME_FROM_EDID) == 0) return null;

            return string.IsNullOrWhiteSpace(targetName.MonitorFriendlyDeviceName) ? null : targetName.MonitorFriendlyDeviceName;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] DisplayConfigGetDeviceInfo (target name) に失敗しました: {ex.Message}");
            return null;
        }
    }

    public sealed class MonitorInfo
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("isPrimary")]
        public bool IsPrimary { get; set; }
    }

    /// <summary>
    /// モニターの一覧を、可能であれば実際の製品名(例: "Dell U2720Q")付きで取得する。
    ///
    /// 名前解決の優先順位:
    ///   1. DisplayConfig API (QueryDisplayConfig + DisplayConfigGetDeviceInfo) による
    ///      EDID由来のフレンドリ名。Windows の「設定」アプリと同じ実際の製品名が得られる。
    ///   2. EnumDisplayDevices の DeviceString。ドライバ/INF が無い環境では
    ///      "Generic PnP Monitor" 等の汎用名になりがちだが、1が失敗した場合のフォールバック。
    ///   3. GDI デバイス名 (例: "\\.\DISPLAY1")。
    ///
    /// 注意: 並び順は Win32 の列挙順であり、d3d11screencapturesrc の monitor-index
    /// (DXGI の列挙順)と厳密に一致する保証はない点は GetMonitorRects と同じ。
    /// </summary>
    public static List<MonitorInfo> GetMonitorInfos()
    {
        var handles = new List<IntPtr>();

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr dwData)
        {
            handles.Add(hMonitor);
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // GDIデバイス名 (\\.\DISPLAY1 等) -> (アダプタLUID, ターゲットID) のマップ。
        // 一度だけまとめて取得しておき、モニターごとの突き合わせに使う。
        var gdiNameToTarget = GetGdiDeviceNameToTargetMap();

        var result = new List<MonitorInfo>();
        for (var i = 0; i < handles.Count; i++)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            var width = 0;
            var height = 0;
            var isPrimary = false;
            var name = $"モニター {i}";

            if (GetMonitorInfo(handles[i], ref mi))
            {
                width = mi.rcMonitor.Right - mi.rcMonitor.Left;
                height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;

                string? resolvedName = null;
                if (!string.IsNullOrEmpty(mi.szDevice) && gdiNameToTarget.TryGetValue(mi.szDevice, out var target))
                {
                    resolvedName = TryGetMonitorFriendlyName(target.AdapterId, target.TargetId);
                }

                if (!string.IsNullOrWhiteSpace(resolvedName))
                {
                    name = resolvedName!;
                }
                else
                {
                    // フォールバック: 従来の EnumDisplayDevices ベースの名前解決。
                    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (EnumDisplayDevices(mi.szDevice, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString))
                    {
                        name = dd.DeviceString;
                    }
                    else if (!string.IsNullOrWhiteSpace(mi.szDevice))
                    {
                        name = mi.szDevice;
                    }
                }
            }

            result.Add(new MonitorInfo
            {
                Index = i,
                Name = name,
                Width = width,
                Height = height,
                IsPrimary = isPrimary,
            });
        }

        return result;
    }

    /// <summary>
    /// ウィンドウ全体(タイトルバー等を含む)の画面上矩形を取得する。
    ///
    /// GetWindowRect は Windows 10/11 で追加された「見た目には存在しない
    /// 透明なリサイズ用パディング」を含んでしまい、実際にWGCがキャプチャする
    /// 見た目の範囲より一回り大きい値を返す。この誤差は正方形〜縦長のウィンドウで
    /// 相対的に大きくなり、マウス座標のズレとして顕在化するため、
    /// DWM の拡張フレーム境界(見た目通りの範囲)を優先して使用する。
    /// </summary>
    public static Rect? GetWindowRectSafe(IntPtr hWnd)
    {
        if (!IsWindow(hWnd)) return null;

        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwmRect, Marshal.SizeOf<RECT>()) == 0)
        {
            return new Rect(dwmRect.Left, dwmRect.Top, dwmRect.Right - dwmRect.Left, dwmRect.Bottom - dwmRect.Top);
        }

        // DWM が使えない環境向けのフォールバック。
        if (!GetWindowRect(hWnd, out var r)) return null;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// ウィンドウを指定した位置・サイズに移動/リサイズする。フォーカスは奪わない。
    /// </summary>
    public static bool MoveResizeWindow(IntPtr hWnd, int x, int y, int width, int height)
    {
        if (!IsWindow(hWnd)) return false;
        return SetWindowPos(hWnd, IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height), SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ---- ウィンドウスタイル (タイトルバー等の着脱) ------------------------------------
    //
    // WS_OVERLAPPEDWINDOW (タイトルバー・システムメニュー・サイズ変更枠・最小化/最大化
    // ボタン一式) を取り除くと、GetWindowRect(見た目の範囲)とクライアント領域
    // (実際にキャプチャされる範囲)の差が無くなり、タイトルバー分のクロップ計算や
    // それに伴うズレが原理的に不要になる。
    //
    // GetWindowLong/SetWindowLong は正式名称に A/W サフィックスが必要だが、
    // .NET の P/Invoke は既定で見つからない場合に自動的に "A" サフィックス付きの
    // エクスポート (SetWindowLongA 等、64bit Windows でも実在する) を試すため、
    // このままの宣言で動作する。

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_STYLE = -16;
    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>WS_OVERLAPPEDWINDOW のビットが現在立っているかどうか。</summary>
    public static bool HasOverlappedWindowStyle(IntPtr hWnd) => (GetWindowLong(hWnd, GWL_STYLE) & WS_OVERLAPPEDWINDOW) != 0;

    /// <summary>
    /// WS_OVERLAPPEDWINDOW を取り除く(既に無ければ何もしない)。
    /// 現在のスタイル値から該当ビットだけを外す(スナップショットの丸ごと上書きはしない)。
    /// スタイル変更を見た目に反映させるため SWP_FRAMECHANGED 付きで SetWindowPos を呼ぶ
    /// (MSDN 推奨の手順。位置・サイズ自体は変えない)。
    /// </summary>
    public static bool RemoveOverlappedWindowStyle(IntPtr hWnd)
    {
        if (!IsWindow(hWnd)) return false;

        var style = GetWindowLong(hWnd, GWL_STYLE);
        if ((style & WS_OVERLAPPEDWINDOW) == 0) return false;

        SetWindowLong(hWnd, GWL_STYLE, style & ~WS_OVERLAPPEDWINDOW);
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return true;
    }

    /// <summary>
    /// WS_OVERLAPPEDWINDOW を付け直す(既にあれば何もしない)。
    ///
    /// 配信開始前のスタイル値をそのままスナップショットとして丸ごと上書き復元するのではなく、
    /// 「その時点の現在のスタイル」に対して該当ビットだけを OR で付け足す方式にしている。
    /// 配信中にアプリ自身が(タイトルバーと無関係な)別のスタイルビットを変更している
    /// 可能性があり、丸ごと上書きするとそれらを意図せず巻き戻してしまう上、
    /// 何らかの理由で上書きが反映されないケースがあったため、より安全な方式にした。
    /// </summary>
    public static bool AddOverlappedWindowStyle(IntPtr hWnd)
    {
        if (!IsWindow(hWnd)) return false;

        var style = GetWindowLong(hWnd, GWL_STYLE);
        if ((style & WS_OVERLAPPEDWINDOW) == WS_OVERLAPPEDWINDOW) return false;

        SetWindowLong(hWnd, GWL_STYLE, style | WS_OVERLAPPEDWINDOW);
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return true;
    }

    /// <summary>
    /// ウィンドウの「クライアント領域」(タイトルバー・メニューバー・枠を除いた、
    /// アプリの実際の描画内容のみの範囲)を、スクリーン座標系で取得する。
    /// ウィンドウキャプチャからタイトルバー分を除外してマウス座標を合わせる際に使う。
    /// </summary>
    public static Rect? GetClientRectOnScreen(IntPtr hWnd)
    {
        if (!IsWindow(hWnd)) return null;
        if (!GetClientRect(hWnd, out var client)) return null;

        var topLeft = new POINT { X = client.Left, Y = client.Top };
        var bottomRight = new POINT { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(hWnd, ref topLeft)) return null;
        if (!ClientToScreen(hWnd, ref bottomRight)) return null;

        return new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }
}
