using System;
using System.Threading;
using System.Threading.Tasks;
using Gst;
using Array = System.Array;

namespace RemoteDesktopHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // どこかで捕捉漏れの例外が起きた場合でも、無言で終了させず必ずログを残す。
        // (特にネイティブコールバックからの reverse P/Invoke 経由で例外が漏れると
        //  .NET はプロセスを無条件に終了させるため、通常のtry/catchだけでは足りない
        //  ケースがある。ここでは「見えるようにする」ことを優先する。)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine("[FATAL] AppDomain UnhandledException:");
            Console.WriteLine(e.ExceptionObject);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Console.WriteLine("[Process] ProcessExit event fired (process is shutting down)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine("[FATAL] UnobservedTaskException:");
            Console.WriteLine(e.Exception);
            e.SetObserved();
        };

        // 例: RemoteDesktopHost.exe --prefix http://+:8787/ --stun stun://stun.l.google.com:19302
        //     --test-source --fps 60 --codec hevc
        //     wss: --wss-port 8788 [--cert cert.pfx --cert-password xxx]
        var prefix = GetArg(args, "--prefix") ?? "http://+:8787/";
        // wss(TLS) 設定。--wss-port を指定すると ws と同時に wss も待ち受ける。
        //   --cert <pfx|pem> [--cert-password <pw>] [--cert-key <pem秘密鍵>]
        //   --cert 未指定なら自己署名証明書 (selfsigned.pfx) を自動生成して使う。
        var wssPort = int.TryParse(GetArg(args, "--wss-port"), out var wssPortArg) ? wssPortArg : 0;
        var wssOptions = wssPort > 0
            ? new WssOptions
            {
                Port = wssPort,
                CertPath = GetArg(args, "--cert"),
                CertPassword = GetArg(args, "--cert-password"),
                KeyPath = GetArg(args, "--cert-key"),
            }
            : null;
        var stun = GetArg(args, "--stun") ?? "stun://stun.l.google.com:19302";
        var useTestSource = Array.IndexOf(args, "--test-source") >= 0;
        var defaultFps = int.TryParse(GetArg(args, "--fps"), out var fpsArg) ? fpsArg : 30;
        var defaultCodec = string.Equals(GetArg(args, "--codec"), "hevc", StringComparison.OrdinalIgnoreCase)
            ? VideoCodec.Hevc
            : VideoCodec.H264;
        var windowMonitorIndex = int.TryParse(GetArg(args, "--window-monitor"), out var wmArg) ? wmArg : 1;

        Application.Init(); // Gst.Init 相当 (GstSharpBundle)
        Console.WriteLine($"[Gst] Version: {GstNativeInterop.GetVersionString()}");

        // タッチ注入 (CreateSyntheticPointerDevice) は、ここ(メインスレッド)ではなく
        // 実際に InjectSyntheticPointerInput を呼ぶスレッド(DataChannel受信コールバックが
        // 動く GStreamer 内部スレッド)側で初回呼び出し時に遅延作成する。
        // (注入デバイスハンドルは作成したスレッドに紐づくため、それ以外のスレッドから
        //  使うと失敗する。詳細は InputInjector.cs 参照)

        // GLib のメインループを別スレッドで回す。
        // GstPromise のコールバック自体は GStreamer 内部スレッドから呼ばれるが、
        // Bus のシグナルウォッチなど GLib のイベント配送には MainLoop が必要。
        var mainLoop = new GLib.MainLoop();
        var glibThread = new Thread(() => mainLoop.Run()) { IsBackground = true };
        glibThread.Start();

        var config = new HostConfig
        {
            StunServer = stun,
            UseTestSource = useTestSource,
            Framerate = defaultFps,
            Codec = defaultCodec,
            WindowTargetMonitorIndex = windowMonitorIndex,
        };
        if (useTestSource)
        {
            Console.WriteLine("[Config] --test-source: videotestsrc/audiotestsrc を使用します (d3d11/wasapi は経由しません)");
        }

        WebRtcSession? currentSession = null;
        WebRtcSession? pendingOldSession = null;
        var sessionLock = new object();
        var server = new SignalingServer(prefix, wssOptions);

        server.ClientConnected = client =>
        {
            // 新しい接続を優先する: 古いセッションが残っていれば破棄して引き継ぐ。
            // (無言で切れた古い接続が残って再接続が拒否される、という事態を避ける)
            lock (sessionLock)
            {
                if (currentSession is not null)
                {
                    Console.WriteLine("[Session] New client takes over the active session");
                    pendingOldSession = currentSession;
                    currentSession = null;
                }
            }
            if (pendingOldSession is not null)
            {
                var old = pendingOldSession;
                pendingOldSession = null;
                try { old.Dispose(); }
                catch (Exception ex) { Console.WriteLine($"[Session] Old session dispose failed: {ex.Message}"); }
            }

            // 接続URLのクエリパラメータで、接続ごとに fps/codec を上書きできるようにする。
            // 例: ws://host:8787/signaling?fps=60&codec=hevc
            var perConnectionConfig = new HostConfig
            {
                StunServer = config.StunServer,
                UseTestSource = config.UseTestSource,
                // ?audio=0 で音声パイプラインごと省く(映像ラグの原因切り分け用: A/V同期待ちの有無を確認できる)
                EnableAudio = !(client.QueryParams.TryGetValue("audio", out var audioStr) && audioStr == "0"),
                Framerate = client.QueryParams.TryGetValue("fps", out var fpsStr) && int.TryParse(fpsStr, out var fps)
                    ? Math.Clamp(fps, 1, 60)
                    : config.Framerate,
                Codec = client.QueryParams.TryGetValue("codec", out var codecStr) && string.Equals(codecStr, "hevc", StringComparison.OrdinalIgnoreCase)
                    ? VideoCodec.Hevc
                    : config.Codec,
                WindowTargetMonitorIndex = config.WindowTargetMonitorIndex,
            };
            Console.WriteLine($"[Config] fps={perConnectionConfig.Framerate}, codec={perConnectionConfig.Codec}");

            var session = new WebRtcSession(client, perConnectionConfig);
            lock (sessionLock) { currentSession = session; }

            client.Disconnected = () =>
            {
                Console.WriteLine("[Session] Client disconnected, tearing down pipeline");
                try { session.Dispose(); }
                catch (Exception ex) { Console.WriteLine($"[Session] Dispose failed: {ex.Message}"); }
                lock (sessionLock)
                {
                    // 引き継がれた後の古いセッションが、新しいセッションの参照を消さないようにする
                    if (ReferenceEquals(currentSession, session)) currentSession = null;
                }
            };

            try
            {
                session.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Session] Failed to start pipeline: {ex}");
                try { session.Dispose(); } catch { }
                lock (sessionLock)
                {
                    if (ReferenceEquals(currentSession, session)) currentSession = null;
                }
            }
        };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Shutting down...");
            server.Stop();
            mainLoop.Quit();
            Environment.Exit(0);
        };

        Console.WriteLine("Ctrl+C で終了します。");
        await server.RunAsync();
        return 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }
}
