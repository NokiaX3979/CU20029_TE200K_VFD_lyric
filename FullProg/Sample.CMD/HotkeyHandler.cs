using System;
using System.Collections.Generic;
using System.Threading;

namespace Sample.CMD
{
    /// <summary>
    /// 控制台单键热键（无需回车）。
    ///
    /// 用一个后台线程阻塞在 Console.ReadKey 上。stdin 被重定向时
    /// Console.ReadKey 会抛异常，因此这里先判断，退出自动降级为"只有 Ctrl+C 能退"。
    /// </summary>
    internal sealed class HotkeyHandler : IDisposable
    {
        private readonly VfdLyricController _controller;
        private readonly Action _requestShutdown;
        private readonly Action<int> _onBrightnessChanged;
        private readonly Thread _thread;
        private volatile bool _running = true;

        public HotkeyHandler(VfdLyricController controller, Action requestShutdown, Action<int> onBrightnessChanged)
        {
            _controller = controller;
            _requestShutdown = requestShutdown;
            _onBrightnessChanged = onBrightnessChanged;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Hotkeys" };
        }

        public static bool IsSupported
        {
            get
            {
                try
                {
                    return !Console.IsInputRedirected;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Start()
        {
            _thread.Start();
        }

        private void Loop()
        {
            try
            {
                // 诊断信息，帮助定位"热键没反应"的成因
                ConsoleLog.Debug("热键线程启动: IsInputRedirected=" + Console.IsInputRedirected +
                                  " TreatControlCAsInput=" + Console.TreatControlCAsInput +
                                  " WindowWidth=" + SafeWindowWidth());
            }
            catch (Exception ex)
            {
                ConsoleLog.Debug("读取控制台属性失败: " + ex.Message);
            }

            while (_running)
            {
                ConsoleKeyInfo key;
                try
                {
                    // intercept: true —— 键入的字符不会回显到屏幕上
                    key = Console.ReadKey(true);
                }
                catch (InvalidOperationException ex)
                {
                    ConsoleLog.Warn("热键不可用：当前没有可读的控制台输入（" + ex.Message + "）。");
                    ConsoleLog.Warn("常见原因：标准输入被重定向，或程序没有附加到交互式控制台。");
                    return;
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn("热键线程异常退出: " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                ConsoleLog.Debug("收到按键: " + Describe(key));

                try
                {
                    if (Handle(key))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog.Error("热键处理失败: " + ex.Message);
                }
            }
        }

        private static string SafeWindowWidth()
        {
            try
            {
                return Console.WindowWidth.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static string Describe(ConsoleKeyInfo key)
        {
            var ch = key.KeyChar == '\0' ? "(null)" : "U+" + ((int)key.KeyChar).ToString("X4");
            return "Key=" + key.Key + " Char=" + ch + " Modifiers=" + key.Modifiers;
        }

        /// <summary>返回 true 表示应结束热键线程。</summary>
        private bool Handle(ConsoleKeyInfo key)
        {
            // Ctrl+C 有两种到达方式（有 ReadKey 阻塞时通常走按键），
            // 两种都处理，且 CancelKeyPress 那边是幂等的。
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                (key.Key == ConsoleKey.C || key.Key == ConsoleKey.Q))
            {
                _requestShutdown();
                return true;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.OemPlus:
                case ConsoleKey.Add:
                    ChangeBrightness(+1);
                    return false;

                case ConsoleKey.DownArrow:
                case ConsoleKey.OemMinus:
                case ConsoleKey.Subtract:
                    ChangeBrightness(-1);
                    return false;

                case ConsoleKey.P:
                    // 任何按键都算"有活动"，先唤醒屏幕
                    _controller.NotifyActivity();
                    _controller.ToggleDisplayPower();
                    return false;

                case ConsoleKey.C:
                    _controller.ToggleCursorVisible();
                    return false;

                case ConsoleKey.R:
                    _controller.ToggleReverseDisplay();
                    return false;

                case ConsoleKey.H:
                    ShowHelp();
                    return false;

                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    _requestShutdown();
                    return true;

                default:
                    // 其它按键也算活动：把息屏的屏幕唤醒（跟手机"点亮屏幕"一个道理）
                    _controller.NotifyActivity();
                    return false;
            }
        }

        private void ChangeBrightness(int delta)
        {
            var level = _controller.AdjustBrightness(delta);
            ConsoleLog.Vfd("亮度: " + level + " (" + level * 25 + "%)");

            var callback = _onBrightnessChanged;
            if (callback != null)
            {
                try
                {
                    callback(level);
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn("亮度写回配置失败: " + ex.Message);
                }
            }
        }

        private static void ShowHelp()
        {
            ConsoleLog.Banner(HelpLines());
        }

        public static IEnumerable<string> HelpLines()
        {
            yield return "";
            yield return "------------------ 热键 ------------------";
            yield return "  ↑ / +      亮度 +1（25% -> 100% 循环）";
            yield return "  ↓ / -      亮度 -1";
            yield return "  P          屏幕电源 开/关（手动接管，自动息屏暂停）";
            yield return "  C          光标显示 开/关";
            yield return "  R          反色显示 开/关";
            yield return "  H / ?      显示本帮助";
            yield return "  Q / Esc    退出";
            yield return "";
            yield return "  其它任意键   唤醒屏幕（息屏后点亮）";
            yield return "------------------------------------------";
        }

        public void Dispose()
        {
            _running = false;

            // 线程阻塞在 ReadKey 上无法主动唤醒；它已标记为后台线程，
            // 主流程退出时进程结束即可，这里只做标记。
        }
    }
}