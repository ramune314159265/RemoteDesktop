using System;
using System.Threading;
using System.Threading.Tasks;
using Gst;
using Array = System.Array;

namespace RemoteDesktopHost;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static async Task<int> Main(string[] args)
    {
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

        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        var settings = HostSettings.Load(GetArg(args, "--settings"));

        var prefix = GetArg(args, "--prefix") ?? settings.Prefix ?? "http://+:8787/";
        var wssPort = int.TryParse(GetArg(args, "--wss-port"), out var wssPortArg) ? wssPortArg : settings.WssPort ?? 0;
        var wssOptions = wssPort > 0
            ? new WssOptions
            {
                Port = wssPort,
                CertPath = GetArg(args, "--cert") ?? settings.Cert,
                CertPassword = GetArg(args, "--cert-password") ?? settings.CertPassword,
                KeyPath = GetArg(args, "--cert-key") ?? settings.CertKey,
            }
            : null;
        var stun = GetArg(args, "--stun") ?? settings.Stun ?? "stun://stun.l.google.com:19302";
        var useTestSource = Array.IndexOf(args, "--test-source") >= 0 || settings.TestSource == true;
        var defaultFps = int.TryParse(GetArg(args, "--fps"), out var fpsArg) ? fpsArg : settings.Fps ?? 30;
        var defaultCodec = string.Equals(GetArg(args, "--codec") ?? settings.Codec, "hevc", StringComparison.OrdinalIgnoreCase)
            ? VideoCodec.Hevc
            : VideoCodec.H264;
        var defaultBitrate = uint.TryParse(GetArg(args, "--bitrate"), out var brArg) ? brArg : settings.Bitrate ?? 6000;

        var windowMonitorSpec = (GetArg(args, "--window-monitor") ?? settings.WindowMonitor?.ToString() ?? "1").Trim();
        var windowMonitorIndex = 1;
        string? windowMonitorName = null;
        if (int.TryParse(windowMonitorSpec, out var wmIndex)) windowMonitorIndex = wmIndex;
        else if (windowMonitorSpec.Length > 0) windowMonitorName = windowMonitorSpec;

        WindowStyleRecovery.RecoverFromPreviousRun();

        Application.Init();
        Console.WriteLine($"[Gst] Version: {GstNativeInterop.GetVersionString()}");

        try
        {
            Console.WriteLine("[Config] 検出されたモニター (windowMonitor には番号か名前を指定できます):");
            foreach (var m in ScreenGeometry.GetMonitorInfos())
            {
                Console.WriteLine($"    {m.Index}: {m.Name} ({m.Width}x{m.Height}){(m.IsPrimary ? " [プライマリ]" : "")}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] モニター一覧の取得に失敗: {ex.Message}");
        }

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
            WindowTargetMonitorName = windowMonitorName,
            BitrateKbps = defaultBitrate,
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

            var perConnectionConfig = new HostConfig
            {
                StunServer = config.StunServer,
                UseTestSource = config.UseTestSource,
                StartupBurstMs = client.QueryParams.TryGetValue("burst", out var burstStr) && int.TryParse(burstStr, out var burstMs)
                    ? Math.Clamp(burstMs, 0, 10000)
                    : 0,
                EnablePlayoutDelay = !(client.QueryParams.TryGetValue("playout", out var playoutStr) && playoutStr == "0"),
                Framerate = client.QueryParams.TryGetValue("fps", out var fpsStr) && int.TryParse(fpsStr, out var fps)
                    ? Math.Clamp(fps, 1, 60)
                    : config.Framerate,
                Codec = client.QueryParams.TryGetValue("codec", out var codecStr) && string.Equals(codecStr, "hevc", StringComparison.OrdinalIgnoreCase)
                    ? VideoCodec.Hevc
                    : config.Codec,
                WindowTargetMonitorIndex = config.WindowTargetMonitorIndex,
                WindowTargetMonitorName = config.WindowTargetMonitorName,
                BitrateKbps = client.QueryParams.TryGetValue("bitrate", out var brStr) && uint.TryParse(brStr, out var br)
                    ? Math.Clamp(br, 500u, 100000u)
                    : config.BitrateKbps,
            };
            Console.WriteLine($"[Config] fps={perConnectionConfig.Framerate}, codec={perConnectionConfig.Codec}, bitrate={perConnectionConfig.BitrateKbps}kbps");

            var session = new WebRtcSession(client, perConnectionConfig);
            lock (sessionLock) { currentSession = session; }

            client.Disconnected = () =>
            {
                Console.WriteLine("[Session] Client disconnected, tearing down pipeline");
                try { session.Dispose(); }
                catch (Exception ex) { Console.WriteLine($"[Session] Dispose failed: {ex.Message}"); }
                lock (sessionLock)
                {
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

        void DisposeActiveSession()
        {
            WebRtcSession? s;
            lock (sessionLock)
            {
                s = currentSession;
                currentSession = null;
            }
            try { s?.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[Session] Dispose on shutdown failed: {ex.Message}"); }
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeActiveSession();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Shutting down...");
            DisposeActiveSession();
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
