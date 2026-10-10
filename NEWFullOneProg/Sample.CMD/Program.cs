using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Windows.Media.Control;
using WindowsMediaController;
using Sample.CMD.Lyrics;
using Sample.CMD.Vfd;

namespace Sample.CMD
{
    internal static class Program
    {
        private static readonly ManualResetEventSlim ShutdownSignal = new ManualResetEventSlim(false);
        private static PipeBroadcaster _broadcaster;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            var options = CommandLineOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            ConsoleLog.Banner(new[]
            {
                "",
                "========================================================",
                "  VFDLyric  -  Windows SMTC -> VFD 歌词显示 (CU20029-TE200K)",
                "========================================================"
            });

            // ---- 配置 ----
            var configPath = options.ConfigPath ?? AppConfig.DefaultPath;
            var config = AppConfig.LoadOrCreateInteractive(configPath, ConsoleLog.Info);
            ConsoleLog.VerboseEnabled = options.Verbose;

            // ---- 串口 ----
            VfdDisplay display;
            try
            {
                display = new VfdDisplay(config.SerialPort, config.BaudRate);
            }
            catch (Exception ex)
            {
                ConsoleLog.Error(ex.Message);
                ConsoleLog.Error("请确认 VFD 已上电、驱动已安装，以及 " + configPath + " 中的串口配置是否正确。");
                ConsoleLog.Banner(new[] { "按 Enter 键退出..." });
                Console.ReadLine();
                return 1;
            }

            var lyricService = new LyricService(config.HttpTimeoutSeconds, config.SearchLimit, config.DurationToleranceSeconds);
            var controller = new VfdLyricController(display, lyricService, config);

            var statusBar = new StatusBar();
            HotkeyHandler hotkeys = null;
            var hotkeysStarted = false;

            var exitCode = 0;

            try
            {
                display.Initialize(config.Brightness, false, false);

                if (config.EnablePipe || options.EnablePipe)
                {
                    var pipeName = string.IsNullOrEmpty(config.PipeName) ? "MusicInfoPipe" : config.PipeName;
                    _broadcaster = new PipeBroadcaster(pipeName);
                    ConsoleLog.Info("命名管道广播已启用: \\\\.\\pipe\\" + pipeName);
                }

                ConsoleCancelEventHandler cancelHandler = (sender, e) =>
                {
                    e.Cancel = true;
                    ShutdownSignal.Set();
                };
                Console.CancelKeyPress += cancelHandler;

                using (var mediaManager = new MediaManager { Logger = BuildLogger() })
                {
                    mediaManager.OnAnySessionOpened += session =>
                        Log("会话打开: " + session.Id);

                    mediaManager.OnAnySessionClosed += session =>
                    {
                        controller.OnSessionClosed(session.Id);
                        Log("会话关闭: " + session.Id);
                    };

                    mediaManager.OnFocusedSessionChanged += session =>
                    {
                        if (session != null)
                        {
                            Log("焦点会话: " + session.Id);
                        }
                    };

                    mediaManager.OnAnyPlaybackStateChanged += (session, playbackInfo) =>
                        HandlePlaybackState(controller, session, playbackInfo);

                    mediaManager.OnAnyMediaPropertyChanged += (session, properties) =>
                        HandleMediaProperties(controller, session, properties);

                    mediaManager.OnAnyTimelinePropertyChanged += (session, timeline) =>
                        HandleTimeline(controller, session, timeline);

                    mediaManager.Start();

                    // 立即复查一次：程序启动时播放器可能已经在播放，
                    // 而 SMTC 不会补发历史播放事件。
                    PollSessions(controller, mediaManager);

                    controller.Start();

                    if (config.SessionPollSeconds > 0)
                    {
                        var poller = new Thread(() => PollLoop(controller, mediaManager, config.SessionPollSeconds))
                        {
                            IsBackground = true,
                            Name = "SessionPoller"
                        };
                        poller.Start();
                    }

                    ConsoleLog.Banner(new[]
                    {
                        "",
                        "已启动，正在监听系统媒体会话（foobar2000 / Spotify / 浏览器 ...）",
                        "按 Ctrl+C 退出" + (HotkeyHandler.IsSupported ? "（或按 Q / Esc）" : "")
                    });

                    ConsoleLog.Banner(HotkeyHandler.HelpLines());

                    if (HotkeyHandler.IsSupported)
                    {
                        hotkeys = new HotkeyHandler(
                            controller,
                            () => ShutdownSignal.Set(),
                            level => PersistBrightness(config, configPath, level));
                        hotkeys.Start();
                        hotkeysStarted = true;
                        ConsoleLog.Info("热键已启用（↑↓ 亮度 / P 屏幕 / C 光标 / R 反色 / H 帮助 / Q 退出）");
                    }
                    else
                    {
                        ConsoleLog.Warn("标准输入被重定向，热键不可用；将使用 Enter 键退出。");
                    }

                    if (statusBar.IsEnabled)
                    {
                        var bar = statusBar;
                        var statusThread = new Thread(() =>
                        {
                            while (!ShutdownSignal.IsSet)
                            {
                                try
                                {
                                    bar.SetText(controller.GetStatusText());
                                    bar.Tick();
                                    ShutdownSignal.Wait(250);
                                }
                                catch
                                {
                                    ShutdownSignal.Wait(1000);
                                }
                            }

                            bar.Dispose();
                        })
                        {
                            IsBackground = true,
                            Name = "StatusBar"
                        };
                        statusThread.Start();
                    }

                    // 关键：Console.ReadKey 与 Console.ReadLine 抢同一个标准输入流，
                    // 同时开会互相吞按键（ReadLine 会把按键吃掉然后等回车，
                    // 导致 ReadKey 永远收不到），热键就会整个失灵。
                    // 所以只有热键线程起不来时，才退回到"回车退出"这种单线程方案。
                    if (!hotkeysStarted && !Console.IsInputRedirected)
                    {
                        var enterWatcher = new Thread(() =>
                        {
                            try
                            {
                                Console.ReadLine();
                                ShutdownSignal.Set();
                            }
                            catch
                            {
                                // 控制台不可用时忽略
                            }
                        })
                        {
                            IsBackground = true,
                            Name = "EnterWatcher"
                        };
                        enterWatcher.Start();
                    }

                    ShutdownSignal.Wait();
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Error("运行失败: " + ex);
                exitCode = 1;
            }
            finally
            {
                controller.Dispose();
                display.Dispose();

                if (hotkeys != null)
                {
                    hotkeys.Dispose();
                }

                statusBar.Dispose();

                if (_broadcaster != null)
                {
                    _broadcaster.Dispose();
                    _broadcaster = null;
                }

                ConsoleLog.Info("已退出。");
            }

            return exitCode;
        }

        // ---------------------------------------------------------------
        // SMTC 事件处理
        // ---------------------------------------------------------------

        private static void HandleMediaProperties(VfdLyricController controller,
            MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties properties)
        {
            if (session == null || properties == null)
            {
                return;
            }

            controller.OnMediaProperties(session.Id, properties.Title, properties.Artist, properties.AlbumTitle);
            Broadcast(session.Id + " is now playing " + properties.Title +
                      (string.IsNullOrEmpty(properties.Artist) ? "" : " by " + properties.Artist));
        }

        private static void HandlePlaybackState(VfdLyricController controller,
            MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
        {
            if (session == null || playbackInfo == null)
            {
                return;
            }

            var state = ToPlaybackState(playbackInfo.PlaybackStatus);

            controller.OnPlaybackState(session.Id, state);
            Broadcast(session.Id + " is now " + playbackInfo.PlaybackStatus);

            if (state == PlaybackState.Playing)
            {
                // 会话刚接管（或播放器漏报了曲目事件）时，主动补拉一次曲目与时间轴
                RefreshSessionAsync(controller, session);
            }
        }

        private static void HandleTimeline(VfdLyricController controller,
            MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
        {
            if (session == null || timeline == null)
            {
                return;
            }

            var position = timeline.Position.TotalSeconds;
            var end = timeline.EndTime.TotalSeconds;
            var duration = end > 0 ? (double?)end : null;

            controller.OnTimeline(session.Id, position, duration);
            Broadcast(session.Id + " timeline is now " + timeline.Position + "/" + timeline.EndTime);
        }

        private static void RefreshSessionAsync(VfdLyricController controller, MediaManager.MediaSession session)
        {
            var controlSession = session.ControlSession;
            if (controlSession == null)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var properties = await controlSession.TryGetMediaPropertiesAsync();
                    if (properties != null && controller.NeedsSessionRefresh(session.Id))
                    {
                        controller.OnMediaProperties(session.Id, properties.Title, properties.Artist, properties.AlbumTitle);
                    }
                }
                catch
                {
                    // WinRT 调用失败不影响主流程
                }

                try
                {
                    var timeline = controlSession.GetTimelineProperties();
                    if (timeline != null)
                    {
                        HandleTimeline(controller, session, timeline);
                    }
                }
                catch
                {
                }
            });
        }

        private static void PollLoop(VfdLyricController controller, MediaManager mediaManager, int intervalSeconds)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));

            while (!ShutdownSignal.IsSet)
            {
                try
                {
                    ShutdownSignal.Wait(interval);
                    if (ShutdownSignal.IsSet)
                    {
                        return;
                    }

                    PollSessions(controller, mediaManager);
                }
                catch (Exception ex)
                {
                    ConsoleLog.Debug("状态复查异常: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 复查当前活跃会话的播放状态。只取状态，不取位置——
        /// foobar2000 等播放器的 SMTC Position 不会随播放前进，周期性重新锚定会让画面倒退。
        /// </summary>
        private static void PollSessions(VfdLyricController controller, MediaManager mediaManager)
        {
            var sessionId = controller.ActiveSessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            MediaManager.MediaSession session;
            if (!mediaManager.CurrentMediaSessions.TryGetValue(sessionId, out session) ||
                session == null || session.ControlSession == null)
            {
                return;
            }

            try
            {
                var info = session.ControlSession.GetPlaybackInfo();
                if (info != null)
                {
                    controller.ApplyPolledState(sessionId, ToPlaybackState(info.PlaybackStatus));
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Debug("读取播放状态失败: " + ex.Message);
            }
        }

        private static PlaybackState ToPlaybackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            switch (status)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    return PlaybackState.Playing;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    return PlaybackState.Paused;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                    return PlaybackState.Stopped;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed:
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                default:
                    return PlaybackState.Unknown;
            }
        }

        // ---------------------------------------------------------------
        // 杂项
        // ---------------------------------------------------------------

        private static void Broadcast(string line)
        {
            var broadcaster = _broadcaster;
            if (broadcaster == null)
            {
                return;
            }

            try
            {
                broadcaster.Broadcast("[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + line);
            }
            catch
            {
            }
        }

        private static void Log(string message)
        {
            ConsoleLog.Info(message);
            Broadcast(message);
        }

        /// 亮度被修改后保存（亮度是用户明确要求持久化的项）。
        private static void PersistBrightness(AppConfig config, string path, int level)
        {
            if (config.Brightness == level)
            {
                return;
            }

            config.Brightness = level;

            try
            {
                config.Save(path);
                ConsoleLog.Debug("亮度已写入配置: " + path);
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn("亮度写回配置失败: " + ex.Message);
            }
        }

        private static Microsoft.Extensions.Logging.ILogger BuildLogger()
        {
            return new LoggerFactory().AddSerilog(new LoggerConfiguration()
                    .MinimumLevel.Is(LogEventLevel.Warning)
                    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff}] [{Level:u4}] SMTC: {Message:lj}{NewLine}{Exception}")
                    .CreateLogger())
                .CreateLogger(string.Empty);
        }

        private static void PrintUsage()
        {
            ConsoleLog.Banner(new[]
            {
                "",
                "VFDLyric - 监听 Windows 系统媒体会话，把网易云歌词推送到 VFD 显示屏",
                "",
                "用法: Sample.CMD.exe [选项]",
                "",
                "选项:",
                "  --pipe              强制启用命名管道广播（\\\\.\\pipe\\MusicInfoPipe）",
                "  --config <文件>      指定配置文件路径（默认: exe 同目录 vfdlyric.json）",
                "  --verbose           额外的调试诊断（每秒播放位置、忽略的会话等）",
                "  -h, --help          显示本帮助",
                "",
                "首次运行会进入配置向导询问串口等参数，结果保存到 vfdlyric.json；",
                "之后每次启动直接读取该文件。",
                "",
                "逐帧歌词输出由配置项 ShowFrameLog 控制，不再需要命令行参数。",
                ""
            });

            foreach (var line in HotkeyHandler.HelpLines())
            {
                ConsoleLog.Banner(new[] { line });
            }
        }

        private sealed class CommandLineOptions
        {
            public bool ShowHelp { get; private set; }
            public bool EnablePipe { get; private set; }
            public bool Verbose { get; private set; }
            public string ConfigPath { get; private set; }

            public static CommandLineOptions Parse(string[] args)
            {
                var options = new CommandLineOptions();
                if (args == null)
                {
                    return options;
                }

                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    switch ((arg ?? string.Empty).ToLowerInvariant())
                    {
                        case "-h":
                        case "--help":
                        case "/?":
                            options.ShowHelp = true;
                            break;
                        case "--pipe":
                            options.EnablePipe = true;
                            break;
                        case "--verbose":
                        case "-v":
                            options.Verbose = true;
                            break;
                        case "--config":
                            if (i + 1 < args.Length)
                            {
                                options.ConfigPath = args[++i];
                            }
                            break;
                    }
                }

                return options;
            }
        }
    }
}