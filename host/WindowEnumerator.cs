using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace RemoteDesktopHost;

/// <summary>
/// 単一アプリ配信の対象を選ぶために、現在表示されている(タイトルのある)
/// トップレベルウィンドウの一覧を取得する。
/// </summary>
internal static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;

    /// <summary>
    /// タスクバーに実際に表示されるであろうウィンドウかどうかを判定する。
    /// - ツールウィンドウ(WS_EX_TOOLWINDOW)は除外
    /// - 所有者(オーナー)を持ち、かつ WS_EX_APPWINDOW が立っていないものは除外
    ///   (ダイアログ等、通常タスクバーに単独表示されないもの)
    /// - DWM上でCloakされている(別の仮想デスクトップ上・サスペンド中のUWP等)ものは除外
    /// </summary>
    private static bool IsTaskbarWindow(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;

        var exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        var owner = GetWindow(hWnd, GW_OWNER);
        if (owner != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0) return false;

        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        return true;
    }

    /// <summary>ウィンドウが最小化されている場合、配信できるように復元する。</summary>
    public static void RestoreIfMinimized(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
    }

    public sealed class WindowInfo
    {
        [JsonPropertyName("handle")]
        public ulong Handle { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
    }

    public static List<WindowInfo> ListVisibleWindows()
    {
        var result = new List<WindowInfo>();

        // EnumWindows はこのメソッド呼び出し中に同期的にコールバックを呼ぶため、
        // ローカル変数として保持しているだけでGCに回収される心配はない。
        bool Callback(IntPtr hWnd, IntPtr _)
        {
            if (!IsTaskbarWindow(hWnd)) return true;

            var length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (!string.IsNullOrWhiteSpace(title))
            {
                result.Add(new WindowInfo { Handle = (ulong)hWnd.ToInt64(), Title = title });
            }

            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);
        return result;
    }
}
