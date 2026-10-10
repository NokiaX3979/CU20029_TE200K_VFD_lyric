using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sample.CMD
{
    /// <summary>
    /// 极简控制台日志。多线程环境下所有输出都会串行化，避免行内容交错。
    /// </summary>
    internal static class ConsoleLog
    {
        private static readonly object WriteLock = new object();

        public static bool VerboseEnabled { get; set; }

        public static void Info(string message)
        {
            Write("INFO ", message, ConsoleColor.Gray);
        }

        public static void Song(string message)
        {
            Write("SONG ", message, ConsoleColor.Cyan);
        }

        public static void Lyric(string message)
        {
            Write("LYR  ", message, ConsoleColor.Magenta);
        }

        public static void Vfd(string message)
        {
            Write("VFD  ", message, ConsoleColor.DarkGreen);
        }

        public static void Warn(string message)
        {
            Write("WARN ", message, ConsoleColor.Yellow);
        }

        public static void Error(string message)
        {
            Write("ERROR", message, ConsoleColor.Red);
        }

        public static void Debug(string message)
        {
            if (VerboseEnabled)
            {
                Write("DEBUG", message, ConsoleColor.DarkGray);
            }
        }

        /// <summary>直接输出一行原始文本（不加前缀），用于滚屏切片这类高频输出。</summary>
        public static void Raw(string message, ConsoleColor color = ConsoleColor.DarkGray)
        {
            lock (WriteLock)
            {
                try
                {
                    var previous = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                    Console.WriteLine("      " + message);
                    Console.ForegroundColor = previous;
                }
                catch
                {
                    // 控制台不可用时静默忽略
                }
            }
        }

        private static void Write(string level, string message, ConsoleColor color)
        {
            lock (WriteLock)
            {
                try
                {
                    var builder = new StringBuilder();
                    builder.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    builder.Append(' ');
                    builder.Append(level);
                    builder.Append(' ');
                    builder.Append(message);

                    var previous = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                    Console.WriteLine(builder.ToString());
                    Console.ForegroundColor = previous;
                }
                catch
                {
                    // 控制台不可用时静默忽略
                }
            }
        }

        public static void Banner(IEnumerable<string> lines)
        {
            lock (WriteLock)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    foreach (var line in lines)
                    {
                        Console.WriteLine(line);
                    }
                }
                finally
                {
                    try { Console.ResetColor(); } catch { }
                }
            }
        }
    }
}