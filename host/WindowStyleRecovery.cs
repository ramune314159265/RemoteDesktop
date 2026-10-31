using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RemoteDesktopHost;

/// <summary>
/// ウィンドウ配信のためにタイトルバー等を外した/移動したウィンドウの「元の状態」を
/// ファイルへ記録しておき、ホストが異常終了(クラッシュ・強制終了・停電等)して
/// 通常の復元処理が走らなかった場合でも、次回起動時に元へ戻せるようにする。
/// ウィンドウハンドルは再利用されうるため、プロセスIDとウィンドウクラス名が一致する
/// 場合にだけ復元する。
/// </summary>
internal static class WindowStyleRecovery
{
    private sealed class Record
    {
        public long Hwnd { get; set; }
        public int StyleBits { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public uint ProcessId { get; set; }
        public string ClassName { get; set; } = "";
    }

    private static readonly object FileLock = new();
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "window_restore.json");

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int maxCount);

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>配信対象にしたウィンドウの元の状態を記録する(同じウィンドウは上書き)。</summary>
    public static void Save(IntPtr hwnd, int styleBits, ScreenGeometry.Rect? rect)
    {
        try
        {
            lock (FileLock)
            {
                var list = Load();
                list.RemoveAll(r => r.Hwnd == hwnd.ToInt64());
                GetWindowThreadProcessId(hwnd, out var pid);
                list.Add(new Record
                {
                    Hwnd = hwnd.ToInt64(),
                    StyleBits = styleBits,
                    X = rect?.X ?? 0,
                    Y = rect?.Y ?? 0,
                    Width = rect?.Width ?? 0,
                    Height = rect?.Height ?? 0,
                    ProcessId = pid,
                    ClassName = GetClass(hwnd),
                });
                Write(list);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Window] 復元用の記録の保存に失敗: {ex.Message}");
        }
    }

    /// <summary>正常に元へ戻せたウィンドウの記録を消す。</summary>
    public static void Remove(IntPtr hwnd)
    {
        try
        {
            lock (FileLock)
            {
                var list = Load();
                if (list.RemoveAll(r => r.Hwnd == hwnd.ToInt64()) > 0) Write(list);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Window] 復元用の記録の削除に失敗: {ex.Message}");
        }
    }

    /// <summary>起動時に呼ぶ。前回の異常終了で元に戻せなかったウィンドウがあれば復元する。</summary>
    public static void RecoverFromPreviousRun()
    {
        try
        {
            List<Record> list;
            lock (FileLock) list = Load();
            if (list.Count == 0) return;

            foreach (var r in list)
            {
                var hwnd = new IntPtr(r.Hwnd);
                if (!ScreenGeometry.IsWindow(hwnd)) continue;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != r.ProcessId || !string.Equals(GetClass(hwnd), r.ClassName, StringComparison.Ordinal)) continue;

                Console.WriteLine($"[Window] 前回の異常終了で元に戻せなかったウィンドウを復元します (hwnd={r.Hwnd})");
                ScreenGeometry.RestoreOverlappedStyleBits(hwnd, r.StyleBits);
                if (r.Width > 0 && r.Height > 0) ScreenGeometry.MoveResizeWindow(hwnd, r.X, r.Y, r.Width, r.Height);
            }

            lock (FileLock) Write(new List<Record>());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Window] 前回分の復元処理に失敗: {ex.Message}");
        }
    }

    private static List<Record> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Record>();
            return JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(FilePath)) ?? new List<Record>();
        }
        catch
        {
            return new List<Record>();
        }
    }

    private static void Write(List<Record> list)
    {
        if (list.Count == 0)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }
}
