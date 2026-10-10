using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sample.CMD.Lyrics
{
    /// <summary>
    /// 一个时间点上的歌词内容。<see cref="Parts"/> 最多两项：
    /// [0] 为原文（lrc），[1] 为翻译（tlyric）。
    /// </summary>
    internal sealed class LyricEntry
    {
        public LyricEntry(double time, List<KeyValuePair<string, string>> parts)
        {
            Time = time;
            Parts = parts;
        }

        /// <summary>该句歌词的时间点（秒）</summary>
        public double Time { get; private set; }

        public List<KeyValuePair<string, string>> Parts { get; private set; }

        public string Primary
        {
            get { return Parts.Count > 0 ? Parts[0].Value : string.Empty; }
        }

        public string Secondary
        {
            get { return Parts.Count > 1 ? Parts[1].Value : string.Empty; }
        }
    }

    internal static class LyricParser
    {
        private static readonly Regex TimeTag =
            new Regex(@"\[(\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)\]", RegexOptions.Compiled);

        /// <summary>mm:ss / hh:mm:ss / mm:ss.fff -> 秒</summary>
        public static bool TryParseTimeToSeconds(string text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var parts = text.Split(':');
            if (parts.Length == 3)
            {
                int hours;
                int minutes;
                double secs;
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secs))
                {
                    seconds = hours * 3600 + minutes * 60 + secs;
                    return true;
                }
            }
            else if (parts.Length == 2)
            {
                int minutes;
                double secs;
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out secs))
                {
                    seconds = minutes * 60 + secs;
                    return true;
                }
            }

            return false;
        }

        /// <summary>解析原始 LRC，返回 (时间, 文本) 列表并按时间升序。</summary>
        public static List<KeyValuePair<double, string>> Parse(string lrcText)
        {
            var result = new List<KeyValuePair<double, string>>();
            if (string.IsNullOrEmpty(lrcText))
            {
                return result;
            }

            var lines = lrcText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (var rawLine in lines)
            {
                var raw = rawLine.Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                var matches = TimeTag.Matches(raw);
                var text = TimeTag.Replace(raw, string.Empty).Trim();

                foreach (Match match in matches)
                {
                    double seconds;
                    if (TryParseTimeToSeconds(match.Groups[1].Value, out seconds))
                    {
                        result.Add(new KeyValuePair<double, string>(seconds, text));
                    }
                }
            }

            result.Sort((x, y) => x.Key.CompareTo(y.Key));
            return result;
        }

        /// <summary>
        /// 合并原文与翻译。hasTranslation 表示整曲是否带翻译，
        /// 决定 VFD 走"单行全屏直推"还是"双行滚屏"。
        /// </summary>
        public static List<LyricEntry> BuildGrouped(string lrcText, string translatedLrcText, out bool hasTranslation)
        {
            var parsedMain = Parse(lrcText);
            var parsedTranslation = Parse(translatedLrcText);

            var order = new List<double>();
            var grouped = new Dictionary<double, List<KeyValuePair<string, string>>>();
            var originalTimes = new Dictionary<double, double>();

            hasTranslation = !string.IsNullOrEmpty(translatedLrcText) && translatedLrcText.Trim().Length > 0;

            Action<double, string, string> addEntry = (time, text, source) =>
            {
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
                {
                    return;
                }

                var trimmed = text.Trim();
                var key = Math.Round(time, 3);

                List<KeyValuePair<string, string>> bucket;
                if (!grouped.TryGetValue(key, out bucket))
                {
                    bucket = new List<KeyValuePair<string, string>>();
                    grouped[key] = bucket;
                    originalTimes[key] = time;
                    order.Add(key);
                }

                bucket.Add(new KeyValuePair<string, string>(source, trimmed));
            };

            foreach (var pair in parsedMain)
            {
                addEntry(pair.Key, pair.Value, "lrc");
            }

            foreach (var pair in parsedTranslation)
            {
                addEntry(pair.Key, pair.Value, "tlyric");
            }

            order.Sort();
            var result = new List<LyricEntry>(order.Count);
            foreach (var key in order)
            {
                result.Add(new LyricEntry(originalTimes[key], grouped[key]));
            }

            return result;
        }
    }
}