using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteDesktopHost;

/// <summary>
/// RTP ヘッダー拡張 "playout-delay" (min=0, max=0) を映像 RTP パケットに付与する。
/// 受信側ブラウザ(特に Safari)が独自に 250〜500ms 程度の再生待ちバッファを持つのを抑え、
/// 低遅延再生にさせるための指示。GStreamer 1.24 にはこの拡張を付ける要素が無いため、
///   1) オファー SDP のテキストに extmap 行を追加してブラウザへ送る
///   2) アンサーに同じ extmap が含まれていたら採用(ID を取得)し、webrtcbin には渡さない
///   3) ペイロードの src パッドに buffer probe を付け、各 RTP パケットへ拡張を書き込む
/// の3段階で実現している。webrtcbin 自体は拡張の存在を知らない(SDP 上のみ)。
/// </summary>
internal sealed class PlayoutDelayExtension
{
    public const string Uri = "http://www.webrtc.org/experiments/rtp-hdrext/playout-delay";

    // ---- SDP 操作 ------------------------------------------------------------------

    /// <summary>
    /// オファー SDP の映像 m セクションに playout-delay の extmap を追加する。
    /// 追加した ID を返す。映像セクションが見つからない/空き ID が無い場合は null(SDP は無変更)。
    /// </summary>
    public static string? AddExtmapToOffer(string sdp, out int extId)
    {
        extId = 0;
        var lines = new List<string>(sdp.Replace("\r\n", "\n").Split('\n'));
        // 末尾の空要素を除く
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var used = new HashSet<int>();
        foreach (var l in lines)
        {
            var m = Regex.Match(l, @"^a=extmap:(\d+)");
            if (m.Success) used.Add(int.Parse(m.Groups[1].Value));
        }

        var videoStart = lines.FindIndex(l => l.StartsWith("m=video", StringComparison.Ordinal));
        if (videoStart < 0) return null;
        var videoEnd = lines.FindIndex(videoStart + 1, l => l.StartsWith("m=", StringComparison.Ordinal));
        if (videoEnd < 0) videoEnd = lines.Count;

        // 既に同じ拡張があればそのまま使う
        for (var i = videoStart; i < videoEnd; i++)
        {
            var m = Regex.Match(lines[i], @"^a=extmap:(\d+)\S*\s+" + Regex.Escape(Uri));
            if (m.Success)
            {
                extId = int.Parse(m.Groups[1].Value);
                return sdp;
            }
        }

        // one-byte ヘッダー拡張で使える ID は 1〜14
        var id = 0;
        for (var c = 1; c <= 14; c++)
        {
            if (!used.Contains(c)) { id = c; break; }
        }
        if (id == 0) return null;

        lines.Insert(videoEnd, $"a=extmap:{id} {Uri}");
        extId = id;
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// アンサー SDP から playout-delay の extmap 行を探し、ID を返す(無ければ 0)。
    /// webrtcbin は未知の拡張を扱えないため、見つけた行はテキストから取り除いて返す。
    /// </summary>
    public static string StripExtmapFromAnswer(string sdp, out int extId)
    {
        extId = 0;
        var lines = new List<string>(sdp.Replace("\r\n", "\n").Split('\n'));
        var pattern = new Regex(@"^a=extmap:(\d+)\S*\s+" + Regex.Escape(Uri));
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var m = pattern.Match(lines[i]);
            if (!m.Success) continue;
            extId = int.Parse(m.Groups[1].Value);
            lines.RemoveAt(i);
        }
        return extId == 0 ? sdp : string.Join("\r\n", lines);
    }

    // ---- buffer probe --------------------------------------------------------------

    private const string GstCore = "gstreamer-1.0-0.dll";
    private const string GstRtp = "gstrtp-1.0-0.dll";

    private const uint GST_PAD_PROBE_TYPE_BUFFER = 1 << 4;
    private const uint GST_PAD_PROBE_TYPE_BUFFER_LIST = 1 << 5;
    private const int GST_PAD_PROBE_OK = 1;
    private const int GST_MAP_READWRITE = 3; // GST_MAP_READ | GST_MAP_WRITE

    // GstRTPBuffer は GstMapInfo(約104B) x4 を含む(合計約528B)。余裕を持って確保する。
    private const int RtpBufferStructSize = 1024;

    private delegate int PadProbeCallback(IntPtr pad, IntPtr info, IntPtr userData);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_element_get_static_pad(IntPtr element, string name);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint gst_pad_add_probe(IntPtr pad, uint mask, PadProbeCallback callback, IntPtr userData, IntPtr destroyNotify);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gst_pad_remove_probe(IntPtr pad, uint id);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gst_object_unref(IntPtr obj);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_mini_object_make_writable(IntPtr mini);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint gst_buffer_list_length(IntPtr list);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_buffer_list_get_writable(IntPtr list, uint idx);

    [DllImport(GstRtp, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gst_rtp_buffer_map(IntPtr buffer, int flags, IntPtr rtp);

    [DllImport(GstRtp, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gst_rtp_buffer_unmap(IntPtr rtp);

    [DllImport(GstRtp, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gst_rtp_buffer_add_extension_onebyte_header(IntPtr rtp, byte id, byte[] data, uint size);

    // GstPadProbeInfo: guint type @0, gulong id, gpointer data ...
    // Windows(LLP64)では gulong が 32bit のため id は @4〜7、data は @8 になる
    // (Linux/LP64 なら data @16)。本プロジェクトは win-x64 固定。
    private const int InfoTypeOffset = 0;
    private const int InfoDataOffset = 8;

    private readonly PadProbeCallback _probeCallback;
    private readonly IntPtr _rtpStruct = Marshal.AllocHGlobal(RtpBufferStructSize);
    private static readonly byte[] ExtPayload = { 0, 0, 0 }; // min=0, max=0 (単位 10ms, 各12bit)

    private IntPtr _pad;
    private uint _probeId;
    private int _extId;
    private long _packetsWritten;
    private long _failures;
    private bool _loggedFirstSuccess;

    public PlayoutDelayExtension()
    {
        _probeCallback = OnProbe;
    }

    /// <summary>payloader(rtph264pay 等)の src パッドへ probe を付ける。</summary>
    public void Attach(IntPtr payloaderElement, int extId)
    {
        if (_pad != IntPtr.Zero) return; // 二重 Attach 防止
        _extId = extId;
        var pad = gst_element_get_static_pad(payloaderElement, "src");
        if (pad == IntPtr.Zero)
        {
            Console.WriteLine("[PlayoutDelay] payloader の src パッドを取得できませんでした");
            return;
        }
        _probeId = gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER | GST_PAD_PROBE_TYPE_BUFFER_LIST, _probeCallback, IntPtr.Zero, IntPtr.Zero);
        _pad = pad; // 参照は Detach まで保持
        Console.WriteLine($"[PlayoutDelay] RTP ヘッダー拡張の書き込みを開始 (extmap id={extId})");
    }

    public void Detach()
    {
        _extId = 0; // 以降のコールバックは何もしない
        if (_pad != IntPtr.Zero)
        {
            try { gst_pad_remove_probe(_pad, _probeId); } catch { }
            try { gst_object_unref(_pad); } catch { }
            _pad = IntPtr.Zero;
        }
    }

    // GStreamer のストリーミングスレッドから呼ばれる。例外は絶対に外へ出さない。
    private int OnProbe(IntPtr pad, IntPtr info, IntPtr userData)
    {
        try
        {
            var extId = _extId;
            if (extId <= 0) return GST_PAD_PROBE_OK;

            var type = (uint)Marshal.ReadInt32(info, InfoTypeOffset);
            var data = Marshal.ReadIntPtr(info, InfoDataOffset);
            if (data == IntPtr.Zero) return GST_PAD_PROBE_OK;

            if ((type & GST_PAD_PROBE_TYPE_BUFFER_LIST) != 0)
            {
                var list = gst_mini_object_make_writable(data);
                Marshal.WriteIntPtr(info, InfoDataOffset, list);
                var n = gst_buffer_list_length(list);
                for (uint i = 0; i < n; i++)
                {
                    var buf = gst_buffer_list_get_writable(list, i);
                    if (buf != IntPtr.Zero) AddExtension(buf, extId);
                }
            }
            else if ((type & GST_PAD_PROBE_TYPE_BUFFER) != 0)
            {
                var buf = gst_mini_object_make_writable(data);
                Marshal.WriteIntPtr(info, InfoDataOffset, buf);
                AddExtension(buf, extId);
            }
        }
        catch (Exception ex)
        {
            if (System.Threading.Interlocked.Increment(ref _failures) <= 3)
                Console.WriteLine($"[PlayoutDelay] probe error: {ex.Message}");
        }
        return GST_PAD_PROBE_OK;
    }

    private void AddExtension(IntPtr buffer, int extId)
    {
        // 構造体はマップ関数が初期化するが、念のためゼロクリアしておく
        for (var o = 0; o < RtpBufferStructSize; o += 8) Marshal.WriteInt64(_rtpStruct, o, 0);

        if (gst_rtp_buffer_map(buffer, GST_MAP_READWRITE, _rtpStruct) == 0)
        {
            if (System.Threading.Interlocked.Increment(ref _failures) <= 3)
                Console.WriteLine("[PlayoutDelay] gst_rtp_buffer_map に失敗(パケットは無変更で送出)");
            return;
        }
        try
        {
            if (gst_rtp_buffer_add_extension_onebyte_header(_rtpStruct, (byte)extId, ExtPayload, (uint)ExtPayload.Length) != 0)
            {
                System.Threading.Interlocked.Increment(ref _packetsWritten);
                if (!_loggedFirstSuccess)
                {
                    _loggedFirstSuccess = true;
                    Console.WriteLine("[PlayoutDelay] 最初の RTP パケットへ拡張を書き込みました");
                }
            }
            else if (System.Threading.Interlocked.Increment(ref _failures) <= 3)
            {
                Console.WriteLine("[PlayoutDelay] add_extension_onebyte_header に失敗");
            }
        }
        finally
        {
            gst_rtp_buffer_unmap(_rtpStruct);
        }
    }
}
