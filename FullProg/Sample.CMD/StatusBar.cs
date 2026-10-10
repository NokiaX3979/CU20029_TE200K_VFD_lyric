using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Sample.CMD
{
    /// <summary>
    /// 控制台底部常驻状态栏。
    ///
    /// 直接在最后一行原地重画，不产生滚动；内容没变化时不重绘，避免闪烁。
    /// 输出被重定向（写文件/管道）或控制台不可用时自动退化成空操作，
    /// 这样重定向到日志文件时不会混入光标定位转义字符。
    /// </summary>
    internal sealed class StatusBar : IDisposable
    {
        private readonly object _sync = new object();
        private readonly object _enableLock = new object();
        private bool _enabled;
        private string _text = string.Empty;
        private bool _dirty = true;

        public StatusBar()
        {
            _enabled = TryEnable();
        }

        public bool IsEnabled
        {
            get { lock (_enableLock) { return _enabled; } }
        }

        private static bool TryEnable()
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    return false;
                }

                return Console.BufferWidth > 20 && Console.BufferHeight > 2;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>设置状态栏内容（内容不变时不会触发重绘）。</summary>
        public void SetText(string text)
        {
            if (!IsEnabled)
            {
                return;
            }

            lock (_sync)
            {
                if (string.Equals(_text, text, StringComparison.Ordinal))
                {
                    return;
                }

                _text = text ?? string.Empty;
                _dirty = true;
            }
        }

        /// <summary>由调用方按固定节拍调用，内部会自动限流。</summary>
        public void Tick()
        {
            if (!IsEnabled)
            {
                return;
            }

            string snapshot;
            lock (_sync)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                snapshot = _text;
            }

            Paint(snapshot);
        }

        private void Paint(string text)
        {
            var failed = false;

            lock (_enableLock)
            {
                if (!_enabled)
                {
                    return;
                }
            }

            try
            {
                var width = Console.BufferWidth;
                var row = Console.BufferHeight - 1;

                var savedTop = Console.CursorTop;
                var savedLeft = Console.CursorLeft;

                // 留最后一列，避免写到行尾触发自动换行把整块屏幕顶上去
                var usable = width - 1;
                var builder = new StringBuilder();
                builder.Append(' ').Append(text);
                var line = builder.ToString();

                if (line.Length > usable)
                {
                    line = line.Substring(0, usable);
                }
                else
                {
                    line = line.PadRight(usable);
                }

                Console.SetCursorPosition(0, row);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(line);
                Console.ResetColor();

                // 光标位置可能因其它线程输出而失效，越界时回退到最后一行
                if (savedTop < Console.BufferHeight && savedLeft < width)
                {
                    Console.SetCursorPosition(savedLeft, savedTop);
                }
                else
                {
                    Console.SetCursorPosition(0, row);
                }
            }
            catch (IOException)
            {
                // 控制台被关闭或重定向，忽略
                failed = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // 光标定位越界（例如窗口被缩小），忽略
                failed = true;
            }
            catch (Exception)
            {
                failed = true;
            }

            if (failed)
            {
                // 一旦光标定位失败就别再尝试了：反复抛异常既刷屏，
                // 也可能把控制台状态越搞越糟（连带影响 ReadKey 读按键）。
                lock (_enableLock)
                {
                    _enabled = false;
                }

                ConsoleLog.Debug("状态栏已停用：控制台光标定位失败。");
            }
        }

        public void Dispose()
        {
            if (!IsEnabled)
            {
                return;
            }

            lock (_sync)
            {
                if (_text.Length == 0)
                {
                    return;
                }

                try
                {
                    var row = Console.BufferHeight - 1;
                    Console.SetCursorPosition(0, row);
                    Console.ResetColor();
                    Console.Write(new string(' ', Math.Max(1, Console.BufferWidth - 1)));
                    Console.SetCursorPosition(0, row);
                }
                catch
                {
                }

                _text = string.Empty;
            }
        }
    }
}