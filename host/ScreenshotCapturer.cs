using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RemoteDesktopHost;

/// <summary>
/// 配信範囲(モニター全体 / ウィンドウのクライアント領域)を撮影し、PNG として
/// Windows 標準のスクリーンショットフォルダ(通常 %USERPROFILE%\Pictures\Screenshots)へ保存する。
/// 追加パッケージなしで動かすため、GDI で取得して PNG は自前でエンコードする。
/// ファイル名: "スクリーンショット YYYY-MM-DD hhmmss.png"
/// </summary>
internal static class ScreenshotCapturer
{
    // ---- Win32 -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // 32bpp では未使用だが構造体の体裁として確保
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint PW_CLIENTONLY = 0x1;
    private const uint PW_RENDERFULLCONTENT = 0x2;
    private static readonly Guid FolderIdScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private static readonly Guid FolderIdPictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");

    // ---- 公開API ---------------------------------------------------------------

    /// <summary>
    /// monitorRect を撮影して保存する。hwnd が指定された場合は、そのウィンドウのクライアント領域だけを
    /// (他のウィンドウに隠れていても)撮影する。
    /// </summary>
    public static bool TryCapture(IntPtr? hwnd, ScreenGeometry.Rect monitorRect, out string result)
    {
        try
        {
            byte[]? bgra;
            int width, height;

            if (hwnd is { } h)
            {
                var client = ScreenGeometry.GetClientRectOnScreen(h);
                if (client is null)
                {
                    result = "対象ウィンドウが見つかりません。";
                    return false;
                }
                width = client.Value.Width;
                height = client.Value.Height;
                bgra = CaptureWindowClient(h, width, height);
            }
            else
            {
                width = monitorRect.Width;
                height = monitorRect.Height;
                bgra = CaptureScreenRect(monitorRect.X, monitorRect.Y, width, height);
            }

            if (bgra is null || width <= 0 || height <= 0)
            {
                result = "画面の取得に失敗しました。";
                return false;
            }

            var dir = GetScreenshotsFolder();
            Directory.CreateDirectory(dir);
            var fileName = $"スクリーンショット {DateTime.Now:yyyy-MM-dd HHmmss}.png";
            var path = Path.Combine(dir, fileName);
            // 同じ秒に2枚撮った場合は上書きしない
            for (var n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(dir, $"スクリーンショット {DateTime.Now:yyyy-MM-dd HHmmss} ({n}).png");
            }

            WritePng(path, bgra, width, height);
            result = path;
            return true;
        }
        catch (Exception ex)
        {
            result = ex.Message;
            return false;
        }
    }

    // ---- 取得 ------------------------------------------------------------------

    private static byte[]? CaptureScreenRect(int x, int y, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            return CaptureInto(screenDc, width, height, (memDc) =>
                BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[]? CaptureWindowClient(IntPtr hwnd, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            return CaptureInto(screenDc, width, height, (memDc) =>
                PrintWindow(hwnd, memDc, PW_CLIENTONLY | PW_RENDERFULLCONTENT));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>DIB セクションを作って draw を呼び、BGRA(上から下)のピクセル列を返す。</summary>
    private static byte[]? CaptureInto(IntPtr referenceDc, int width, int height, Func<IntPtr, bool> draw)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 負 = 上から下の並び
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        var memDc = CreateCompatibleDC(referenceDc);
        if (memDc == IntPtr.Zero) return null;
        var bitmap = CreateDIBSection(referenceDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memDc);
            return null;
        }

        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!draw(memDc)) return null;
            var data = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, data, 0, data.Length);
            return data;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
        }
    }

    // ---- 保存先 ----------------------------------------------------------------

    /// <summary>Windows 標準のスクリーンショットフォルダ。取得できなければ Pictures\Screenshots。</summary>
    private static string GetScreenshotsFolder()
    {
        var known = GetKnownFolder(FolderIdScreenshots);
        if (!string.IsNullOrEmpty(known)) return known;

        var pictures = GetKnownFolder(FolderIdPictures)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        return Path.Combine(pictures, "Screenshots");
    }

    private static string? GetKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { CoTaskMemFree(p); }
        }
        catch
        {
            return null;
        }
    }

    // ---- PNG エンコード(8bit RGB, zlib) ---------------------------------------

    private static void WritePng(string path, byte[] bgra, int width, int height)
    {
        // 各行の先頭にフィルタ種別(0=None)を付けた RGB 画素列
        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var dst = y * (stride + 1);
            raw[dst++] = 0;
            var src = y * width * 4;
            for (var x = 0; x < width; x++, src += 4)
            {
                raw[dst++] = bgra[src + 2];
                raw[dst++] = bgra[src + 1];
                raw[dst++] = bgra[src];
            }
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // color type: truecolor RGB
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", idat.ToArray());
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        var crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
