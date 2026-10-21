using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesktopHost;

/// <summary>
/// webrtcbin はプラグイン要素であり GObject-Introspection の .gir を持たないため、
/// GstSharp では型付きのシグナル/アクションシグナルが自動生成されない。
/// そのため GStreamer 公式サンプル "webrtc-sendrecv.c" と同じ手順を、
/// libgobject-2.0 / libgstreamer-1.0 / libgstwebrtc-1.0 / libgstsdp-1.0 の
/// C API を直接 P/Invoke することで再現する。
///
/// 注意: DLL 名は GStreamer の Windows MSVC 版インストーラの命名 (プレフィックス無し) を
/// 前提にしている。MinGW 版を導入している場合や、GstSharpBundle が同梱するネイティブ
/// バイナリの名前が異なる場合は、下記の DllImport の文字列を実際のファイル名に
/// 合わせて変更すること (例: "libgstreamer-1.0-0.dll")。
/// </summary>
internal static class GstNativeInterop
{
    private const string GLib = "gobject-2.0-0.dll";
    private const string GstCore = "gstreamer-1.0-0.dll";
    private const string GstWebRtc = "gstwebrtc-1.0-0.dll";
    private const string GstSdp = "gstsdp-1.0-0.dll";
    private const string Glib2 = "glib-2.0-0.dll";

    public enum WebRTCSDPType
    {
        Offer = 1,
        Pranswer = 2,
        Answer = 3,
        Rollback = 4,
    }

    // ---- delegate 型 (シグナルコールバック) ----------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NegotiationNeededCallback(IntPtr webrtcbin, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void IceCandidateCallback(IntPtr webrtcbin, uint mlineIndex, IntPtr candidate, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PromiseChangeFunc(IntPtr promise, IntPtr userData);

    // ---- g_signal_connect_data / g_signal_emit_by_name ----------------------

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_signal_connect_data")]
    private static extern ulong g_signal_connect_data(
        IntPtr instance, string detailedSignal, IntPtr cHandler,
        IntPtr data, IntPtr destroyData, int connectFlags);

    public static ulong ConnectNegotiationNeeded(IntPtr webrtcbin, NegotiationNeededCallback cb)
        => g_signal_connect_data(webrtcbin, "on-negotiation-needed",
            Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero, 0);

    public static ulong ConnectIceCandidate(IntPtr webrtcbin, IceCandidateCallback cb)
        => g_signal_connect_data(webrtcbin, "on-ice-candidate",
            Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero, 0);

    // create-offer / create-answer / set-local-description / set-remote-description は
    // いずれも (instance, name, gpointer, gpointer) の形で emit できる。
    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_signal_emit_by_name")]
    private static extern void g_signal_emit_by_name_2ptr(IntPtr instance, string detailedSignal, IntPtr arg1, IntPtr arg2);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_signal_emit_by_name")]
    private static extern void g_signal_emit_by_name_ice(IntPtr instance, string detailedSignal, uint mlineIndex, string candidate);

    public static void EmitCreateOffer(IntPtr webrtcbin, IntPtr promise)
        => g_signal_emit_by_name_2ptr(webrtcbin, "create-offer", IntPtr.Zero, promise);

    public static void EmitCreateAnswer(IntPtr webrtcbin, IntPtr promise)
        => g_signal_emit_by_name_2ptr(webrtcbin, "create-answer", IntPtr.Zero, promise);

    public static void EmitSetLocalDescription(IntPtr webrtcbin, IntPtr desc, IntPtr promise)
        => g_signal_emit_by_name_2ptr(webrtcbin, "set-local-description", desc, promise);

    public static void EmitSetRemoteDescription(IntPtr webrtcbin, IntPtr desc, IntPtr promise)
        => g_signal_emit_by_name_2ptr(webrtcbin, "set-remote-description", desc, promise);

    public static void EmitAddIceCandidate(IntPtr webrtcbin, uint mlineIndex, string candidate)
        => g_signal_emit_by_name_ice(webrtcbin, "add-ice-candidate", mlineIndex, candidate);

    // ---- DataChannel ------------------------------------------------------------
    //
    // マウス/キーボード/タッチなどの制御入力を送るための WebRTC DataChannel。
    // webrtcbin の "create-data-channel" アクションシグナルは
    // GstWebRTCDataChannel* (GObject) を戻り値として返すため、
    // g_signal_emit_by_name の可変引数の最後に「戻り値の格納先」を渡す形で呼び出す。

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_signal_emit_by_name")]
    private static extern void g_signal_emit_by_name_create_channel(
        IntPtr instance, string detailedSignal, string label, IntPtr options, out IntPtr channel);

    public static IntPtr CreateDataChannel(IntPtr webrtcbin, string label)
    {
        g_signal_emit_by_name_create_channel(webrtcbin, "create-data-channel", label, IntPtr.Zero, out var channel);
        return channel;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DataChannelSimpleCallback(IntPtr channel, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DataChannelMessageStringCallback(IntPtr channel, IntPtr str, IntPtr userData);

    public static ulong ConnectDataChannelOpen(IntPtr channel, DataChannelSimpleCallback cb)
        => g_signal_connect_data(channel, "on-open", Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero, 0);

    public static ulong ConnectDataChannelClose(IntPtr channel, DataChannelSimpleCallback cb)
        => g_signal_connect_data(channel, "on-close", Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero, 0);

    public static ulong ConnectDataChannelMessageString(IntPtr channel, DataChannelMessageStringCallback cb)
        => g_signal_connect_data(channel, "on-message-string", Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero, 0);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_signal_emit_by_name")]
    private static extern void g_signal_emit_by_name_send_string(IntPtr instance, string detailedSignal, string data);

    public static void DataChannelSendString(IntPtr channel, string data)
        => g_signal_emit_by_name_send_string(channel, "send-string", data);

    // ---- g_object_set (プロパティの動的変更: ビットレート/ミュート/キャプチャ元切り替え等) ----
    //
    // g_object_set も可変引数のCの関数だが、直前の g_signal_emit_by_name と同じ考え方で、
    // 「特定の1プロパティを設定する」呼び出し1つ1つに対応する、型を固定した
    // P/Invoke 宣言を個別に用意する。

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_object_set")]
    private static extern void g_object_set_uint(IntPtr obj, string propertyName, uint value, IntPtr terminator);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_object_set")]
    private static extern void g_object_set_int(IntPtr obj, string propertyName, int value, IntPtr terminator);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_object_set")]
    private static extern void g_object_set_bool(IntPtr obj, string propertyName, int value, IntPtr terminator);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_object_set")]
    private static extern void g_object_set_double(IntPtr obj, string propertyName, double value, IntPtr terminator);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "g_object_set")]
    private static extern void g_object_set_uint64(IntPtr obj, string propertyName, ulong value, IntPtr terminator);

    public static void SetUInt(IntPtr obj, string propertyName, uint value)
        => g_object_set_uint(obj, propertyName, value, IntPtr.Zero);

    public static void SetInt(IntPtr obj, string propertyName, int value)
        => g_object_set_int(obj, propertyName, value, IntPtr.Zero);

    public static void SetBool(IntPtr obj, string propertyName, bool value)
        => g_object_set_bool(obj, propertyName, value ? 1 : 0, IntPtr.Zero);

    public static void SetDouble(IntPtr obj, string propertyName, double value)
        => g_object_set_double(obj, propertyName, value, IntPtr.Zero);

    public static void SetUInt64(IntPtr obj, string propertyName, ulong value)
        => g_object_set_uint64(obj, propertyName, value, IntPtr.Zero);

    // ---- Pad の caps 変更通知 (notify::caps) で動的に偶数サイズへ丸める ------------------
    //
    // x264enc は奇数の width/height を受け付けず初期化に失敗する。
    // ウィンドウキャプチャは任意のサイズ(奇数を含む)になり得るため、
    // videocrop の手前(上流側)のパッドで "notify::caps" シグナル(GObjectの標準的な
    // プロパティ変更通知)を監視し、caps が変わるたびに videocrop の right/bottom
    // プロパティを動的に設定して偶数サイズに丸める。
    //
    // 以前は GstPadProbe で DOWNSTREAM イベントを直接監視し、イベント種別を
    // GstEventType の数値で判定せずに gst_event_parse_caps を呼んでいたが、
    // CAPS 以外のイベント(SEGMENT等)に対して呼ぶと不正メモリアクセスで
    // クラッシュすることが実機で判明した。GstEventType の正確な数値を
    // 確実に判定する安全な方法がなかったため、より安全な notify::caps
    // シグナル(caps が実際に変わった時にだけ、確実に発火する)に変更した。

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NotifyCallback(IntPtr gobject, IntPtr pspec, IntPtr userData);

    public static ulong ConnectNotifyCaps(IntPtr pad, NotifyCallback cb)
        => g_signal_connect_data(pad, "notify::caps", Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero, 0);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_pad_get_current_caps(IntPtr pad);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gst_caps_unref(IntPtr caps);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gst_structure_get_int(IntPtr structure, string fieldname, out int value);

    /// <summary>指定パッドの現在の negotiated caps から width/height を取り出す。</summary>
    public static bool TryGetPadCurrentSize(IntPtr pad, out int width, out int height)
    {
        width = 0;
        height = 0;

        var caps = gst_pad_get_current_caps(pad);
        if (caps == IntPtr.Zero) return false;

        try
        {
            var structure = gst_caps_get_structure(caps, 0);
            if (structure == IntPtr.Zero) return false;

            var widthOk = gst_structure_get_int(structure, "width", out width) != 0;
            var heightOk = gst_structure_get_int(structure, "height", out height) != 0;
            return widthOk && heightOk;
        }
        finally
        {
            gst_caps_unref(caps);
        }
    }

    // ---- GstPromise -----------------------------------------------------------

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_promise_new();

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_promise_new_with_change_func(IntPtr func, IntPtr userData, IntPtr notify);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_promise_interrupt(IntPtr promise);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    public static extern int gst_promise_wait(IntPtr promise);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_promise_unref(IntPtr promise);

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_promise_get_reply(IntPtr promise);

    public static IntPtr PromiseNew() => gst_promise_new();

    public static IntPtr PromiseNewWithChangeFunc(PromiseChangeFunc cb)
        => gst_promise_new_with_change_func(Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, IntPtr.Zero);

    public static IntPtr PromiseGetReply(IntPtr promise) => gst_promise_get_reply(promise);

    // ---- GstStructure から boxed 値 (GstWebRTCSessionDescription*) を取り出す ----

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_structure_get_value(IntPtr structure, string fieldName);

    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_value_dup_boxed(IntPtr value);

    /// <summary>
    /// GstStructure のフィールドから boxed 値の「複製(所有権あり)」を取り出す。
    /// g_value_get_boxed() は構造体内部が保持する借用ポインタを返すだけで、
    /// 呼び出し側が free してよいものではない。呼び出し側で解放する前提の値が
    /// 必要な場合は g_value_dup_boxed() で複製を取得すること
    /// (C の gst_structure_get(..., GST_TYPE_WEBRTC_SESSION_DESCRIPTION, &desc, NULL)
    ///  相当の動作)。
    /// </summary>
    public static IntPtr StructureGetBoxed(IntPtr structure, string fieldName)
    {
        var valuePtr = gst_structure_get_value(structure, fieldName);
        if (valuePtr == IntPtr.Zero) return IntPtr.Zero;
        return g_value_dup_boxed(valuePtr);
    }

    // ---- GstWebRTCSessionDescription --------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWebRTCSessionDescription
    {
        public int Type;
        public IntPtr Sdp; // GstSDPMessage*
    }

    [DllImport(GstWebRtc, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_webrtc_session_description_new(int type, IntPtr sdpMessage);

    [DllImport(GstWebRtc, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_webrtc_session_description_free(IntPtr desc);

    public static IntPtr WebRtcSessionDescriptionNew(WebRTCSDPType type, IntPtr sdpMessage)
        => gst_webrtc_session_description_new((int)type, sdpMessage);

    /// <summary>desc (GstWebRTCSessionDescription*) から SDP テキストを取り出す。</summary>
    public static string SessionDescriptionToSdpText(IntPtr descPtr)
    {
        var native = Marshal.PtrToStructure<NativeWebRTCSessionDescription>(descPtr);
        return SdpMessageAsText(native.Sdp);
    }

    // ---- GstSDPMessage ------------------------------------------------------------

    [DllImport(GstSdp, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gst_sdp_message_new(out IntPtr msg);

    [DllImport(GstSdp, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gst_sdp_message_parse_buffer(byte[] data, uint size, IntPtr msg);

    [DllImport(GstSdp, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gst_sdp_message_as_text(IntPtr msg);

    [DllImport(Glib2, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_free(IntPtr mem);

    public static string SdpMessageAsText(IntPtr sdpMessagePtr)
    {
        var textPtr = gst_sdp_message_as_text(sdpMessagePtr);
        if (textPtr == IntPtr.Zero) return string.Empty;
        var text = Marshal.PtrToStringUTF8(textPtr) ?? string.Empty;
        g_free(textPtr);
        return text;
    }

    /// <summary>ブラウザから受け取った SDP テキストから GstSDPMessage* を生成する。</summary>
    public static IntPtr ParseSdp(string sdpText)
    {
        gst_sdp_message_new(out var msg);
        var bytes = Encoding.UTF8.GetBytes(sdpText);
        gst_sdp_message_parse_buffer(bytes, (uint)bytes.Length, msg);
        return msg;
    }

    // ---- gst_version (GstSharpBundle の Version.String() が壊れているように見えるため、
    //      確実な情報取得のためネイティブ関数を直接使う) --------------------------------

    [DllImport(GstCore, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gst_version(out uint major, out uint minor, out uint micro, out uint nano);

    public static string GetVersionString()
    {
        gst_version(out var major, out var minor, out var micro, out var nano);
        return nano == 0 ? $"{major}.{minor}.{micro}" : $"{major}.{minor}.{micro} (nano {nano})";
    }
}