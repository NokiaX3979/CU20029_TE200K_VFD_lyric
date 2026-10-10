using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Sample.CMD
{
    /// 程序配置。首次运行时通过交互式向导生成，随后保存在 exe 同目录的 vfdlyric.json。
    /// 每次启动都会尝试读取；若文件缺失或字段非法，则重新进入向导。
    public sealed class AppConfig
    {
        public const string FileName = "vfdlyric.json";

        // ---- 串口 / 显示 ----
        public string SerialPort { get; set; }
        public int BaudRate { get; set; }
        /// 1=25% 2=50% 3=75% 4=100%
        public int Brightness { get; set; }
        /// 每行字符数（CU20029-TE200K 固定 20）
        public int DisplayWidth { get; set; }

        // ---- 歌词显示 ----
        /// 全局歌词偏移量（秒）：正数提前，负数延后
        public double LyricOffsetSeconds { get; set; }
        /// 滚屏首尾默认停顿时间（秒）
        public double ScrollPauseSeconds { get; set; }
        /// 渲染轮询间隔（毫秒），约 60ms ≈ 16 FPS
        public int RefreshIntervalMs { get; set; }
        /// 是否把每帧的歌词内容输出到控制台（调试用，默认关闭）
        public bool ShowFrameLog { get; set; }

        // ---- 自动息屏（防烧屏）----
        /// 暂停/无播放后多少秒把亮度降到 25%，0 = 关闭该级
        public int IdleDimSeconds { get; set; }
        /// 暂停/无播放后多少秒关闭屏幕（Display Power OFF），0 = 关闭该级
        public int IdleOffSeconds { get; set; }

        // ---- 系统媒体会话 ----
        /// 周期性复查播放状态的间隔（秒），0 表示关闭。用于兜底捕获丢失的暂停/停止事件。
        public int SessionPollSeconds { get; set; }

        // ---- 网易云歌词 ----
        public bool FetchLyrics { get; set; }
        public int SearchLimit { get; set; }
        public double DurationToleranceSeconds { get; set; }
        public int HttpTimeoutSeconds { get; set; }

        // ---- 命名管道广播（兼容旧的 python 脚本）----
        public bool EnablePipe { get; set; }
        public string PipeName { get; set; }

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        public static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                SerialPort = null,
                BaudRate = 38400,
                Brightness = 3,
                DisplayWidth = 20,
                LyricOffsetSeconds = 0.0,
                ScrollPauseSeconds = 0.6,
                RefreshIntervalMs = 60,
                ShowFrameLog = false,
                IdleDimSeconds = 5,
                IdleOffSeconds = 30,
                SessionPollSeconds = 5,
                FetchLyrics = true,
                SearchLimit = 5,
                DurationToleranceSeconds = 8.0,
                HttpTimeoutSeconds = 6,
                EnablePipe = false,
                PipeName = "MusicInfoPipe"
            };
        }

        /// 校验所有字段；返回 null 表示合法，否则返回错误描述。
        public string Validate()
        {
            var problems = new List<string>();

            if (string.IsNullOrEmpty(SerialPort) || SerialPort.Trim().Length == 0)
            {
                problems.Add("SerialPort 不能为空");
            }

            if (BaudRate <= 0)
            {
                problems.Add("BaudRate 必须为正数");
            }

            if (Brightness < 1 || Brightness > 4)
            {
                problems.Add("Brightness 必须在 1~4 之间");
            }

            if (DisplayWidth < 1 || DisplayWidth > 20)
            {
                problems.Add("DisplayWidth 必须在 1~20 之间");
            }

            if (RefreshIntervalMs < 10)
            {
                problems.Add("RefreshIntervalMs 不能小于 10");
            }

            if (IdleDimSeconds < 0)
            {
                problems.Add("IdleDimSeconds 不能为负数");
            }

            if (IdleOffSeconds < 0)
            {
                problems.Add("IdleOffSeconds 不能为负数");
            }

            if (IdleDimSeconds > 0 && IdleOffSeconds > 0 && IdleDimSeconds >= IdleOffSeconds)
            {
                problems.Add("IdleDimSeconds 应小于 IdleOffSeconds，否则变暗这一级永远不会生效");
            }

            if (SessionPollSeconds < 0)
            {
                problems.Add("SessionPollSeconds 不能为负数");
            }

            if (SearchLimit < 1)
            {
                problems.Add("SearchLimit 必须为正数");
            }

            if (HttpTimeoutSeconds < 1)
            {
                problems.Add("HttpTimeoutSeconds 必须为正数");
            }

            if (ScrollPauseSeconds < 0)
            {
                problems.Add("ScrollPauseSeconds 不能为负数");
            }

            if (string.IsNullOrEmpty(PipeName))
            {
                problems.Add("PipeName 不能为空");
            }

            return problems.Count == 0 ? null : string.Join("; ", problems.ToArray());
        }

        public void Save(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        /// 读取配置文件。失败时通过 error 返回原因。
        public static AppConfig Load(string path, out string error)
        {
            error = null;

            if (!File.Exists(path))
            {
                error = "配置文件不存在";
                return null;
            }

            AppConfig config;
            try
            {
                // 先铺一份默认值再覆盖，这样以后新增配置项时，
                // 老的 vfdlyric.json 也能正常读取而不会被判为非法。
                config = CreateDefault();
                var json = File.ReadAllText(path, Encoding.UTF8);
                JsonConvert.PopulateObject(json, config);
            }
            catch (Exception ex)
            {
                error = "配置文件解析失败: " + ex.Message;
                return null;
            }

            if (config == null)
            {
                error = "配置文件内容为空";
                return null;
            }

            var problem = config.Validate();
            if (problem != null)
            {
                error = "配置项非法: " + problem;
                return null;
            }

            return config;
        }

        /// 读取配置；不可用时进入交互式向导重新生成并保存。
        public static AppConfig LoadOrCreateInteractive(string path, Action<string> notify)
        {
            string error;
            var config = Load(path, out error);

            if (config != null)
            {
                notify?.Invoke("已读取配置: " + path);
                return config;
            }

            notify?.Invoke(error == null ? "无可用配置" : "无法使用配置 (" + error + ")，进入配置向导");

            ConsoleLog.Banner(new[]
            {
                "",
                "========================================================",
                "  VFDLyric 首次运行配置向导",
                "========================================================",
                ""
            });

            config = CreateDefault();

            // --- 串口 ---
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            ports = Array.FindAll(ports, p => !string.IsNullOrEmpty(p));
            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);

            if (ports.Length > 0)
            {
                ConsoleLog.Info("检测到 COM 端口: " + string.Join(", ", ports));
            }
            else
            {
                ConsoleLog.Warn("未检测到任何 COM 端口，请确认 VFD 已连接并完成驱动安装");
            }

            config.SerialPort = AskString("请输入要连接的 COM 端口", ports.Length > 0 ? ports[0] : "COM3", null);
            config.BaudRate = AskInt("波特率 (9600 / 19200 / 38400 / 115200)", config.BaudRate);
            config.Brightness = AskInt("亮度 1=25% 2=50% 3=75% 4=100%", config.Brightness);
            config.DisplayWidth = AskInt("每行字符数 (该屏固定 20)", config.DisplayWidth);

            // --- 歌词 ---
            config.LyricOffsetSeconds = AskDouble("歌词偏移秒数（正数提前，负数延后）", config.LyricOffsetSeconds);
            config.ScrollPauseSeconds = AskDouble("滚屏首尾停顿秒数", config.ScrollPauseSeconds);
            config.RefreshIntervalMs = AskInt("渲染刷新间隔（毫秒）", config.RefreshIntervalMs);
            config.ShowFrameLog = AskBool("是否把每帧歌词内容输出到控制台（调试用）", config.ShowFrameLog);

            // --- 自动息屏（防烧屏）---
            config.IdleDimSeconds = AskInt("暂停后多少秒把亮度降到 25%（0=不启用）", config.IdleDimSeconds);
            config.IdleOffSeconds = AskInt("暂停后多少秒关闭屏幕（0=不启用）", config.IdleOffSeconds);

            // --- 系统媒体会话 ---
            config.SessionPollSeconds = AskInt("播放状态复查间隔（秒，0=关闭）", config.SessionPollSeconds);

            // --- 联网 ---
            config.FetchLyrics = AskBool("是否联网搜索网易云歌词", config.FetchLyrics);

            // --- 管道 ---
            config.EnablePipe = AskBool("是否同时以命名管道广播播放信息（兼容旧脚本）", config.EnablePipe);

            ConsoleLog.Banner(new string[0]);

            var problem = config.Validate();
            if (problem != null)
            {
                // 用默认值兜底，保证程序仍可启动
                ConsoleLog.Warn("配置向导结果非法（" + problem + "），已回退到默认值");
                var fallback = CreateDefault();
                fallback.SerialPort = config.SerialPort;
                config = fallback;
            }

            try
            {
                config.Save(path);
                ConsoleLog.Info("配置已保存到: " + path);
            }
            catch (Exception ex)
            {
                ConsoleLog.Error("配置保存失败（本次运行仍使用内存中的配置）: " + ex.Message);
            }

            return config;
        }

        // ---------------- 交互辅助 ----------------

        private static string AskString(string prompt, string defaultValue, Func<string, bool> validator)
        {
            while (true)
            {
                Console.Write("  " + prompt + (string.IsNullOrEmpty(defaultValue) ? ": " : " [" + defaultValue + "]: "));
                Console.Out.Flush();
                var line = Console.ReadLine();
                var value = string.IsNullOrEmpty(line) ? defaultValue : line.Trim();

                if (validator == null || validator(value))
                {
                    return value;
                }

                ConsoleLog.Warn("输入无效，请重试");
            }
        }

        private static int AskInt(string prompt, int defaultValue)
        {
            double result;
            AskNumber(prompt, defaultValue, "请输入整数", v => Math.Abs(v - Math.Round(v)) < double.Epsilon, out result);
            return (int)Math.Round(result);
        }

        private static double AskDouble(string prompt, double defaultValue)
        {
            double result;
            AskNumber(prompt, defaultValue, "请输入数字", v => true, out result);
            return result;
        }

        private static void AskNumber(string prompt, double defaultValue, string message, Func<double, bool> isValid, out double result)
        {
            while (true)
            {
                Console.Write("  " + prompt + " [" + defaultValue.ToString(CultureInfo.InvariantCulture) + "]: ");
                Console.Out.Flush();
                var line = Console.ReadLine();

                if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
                {
                    result = defaultValue;
                    return;
                }

                double parsed;
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                    !double.IsNaN(parsed) &&
                    !double.IsInfinity(parsed) &&
                    isValid(parsed))
                {
                    result = parsed;
                    return;
                }

                ConsoleLog.Warn(message + "，请重试");
            }
        }

        private static bool AskBool(string prompt, bool defaultValue)
        {
            while (true)
            {
                var hint = defaultValue ? "Y/n" : "y/N";
                Console.Write("  " + prompt + " (" + hint + "): ");
                Console.Out.Flush();
                var line = Console.ReadLine();

                if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
                {
                    return defaultValue;
                }

                switch (line.Trim().ToLowerInvariant())
                {
                    case "y":
                    case "yes":
                    case "是":
                        return true;
                    case "n":
                    case "no":
                    case "否":
                        return false;
                }

                ConsoleLog.Warn("请输入 y 或 n");
            }
        }
    }
}