using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using ZhConv.Net;

namespace Sample.CMD.Vfd
{
    /// 一行文本的编码档案：既包含发给屏幕的"切码指令"，也包含按硬件字库范围做校验的编码器。
    /// 为什么不能直接用 .NET 的代码页？
    /// .NET 的 936/950/949/932 分别是 GBK / CP950 / CP949 / CP932，都是各标准编码的**超集**。
    /// 而屏幕内置的字库只包含 GB2312-80 / Big-5 / KSC5601-87 / JIS X0208。
    /// 若直接用 .NET 编码，"體"、"説" 这类 GBK 独有字符会被编码成 GBK 扩展区的码位，
    /// 屏幕查不到对应字模，显示为乱码。因此这里按规格书给出的 2 字节字符有效范围逐字校验，
    /// 效果与 python 的 gb2312 / big5 / ksc5601 / shift_jis 协码一致。
    internal sealed class TextProfile
    {
        public byte[] Command { get; private set; }

        /// 屏幕名称，仅用于日志。
        public string Name { get; private set; }

        /// 是否使用 Big5（需要先做简繁转换，避免混入简体字导致乱码）。
        public bool RequiresTraditionalConversion { get; private set; }

        private readonly Encoding _codePage;
        private readonly Func<byte, bool> _isLeadByte;
        private readonly Func<byte, bool> _isTrailByte;
        private readonly Func<byte, bool> _isSingleByteHigh;

        public TextProfile(byte[] command, int codePage, string name,
            bool requiresTraditionalConversion,
            Func<byte, bool> isSingleByteHigh,
            Func<byte, bool> isLeadByte,
            Func<byte, bool> isTrailByte)
        {
            Command = command;
            Name = name;
            RequiresTraditionalConversion = requiresTraditionalConversion;
            _codePage = Encoding.GetEncoding(codePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _isSingleByteHigh = isSingleByteHigh;
            _isLeadByte = isLeadByte;
            _isTrailByte = isTrailByte;
        }

        /// 整行文本能否被该字库完整表示（用于语言探测）。
        public bool TryEncodeStrict(string text)
        {
            byte[] ignored;
            return TryEncode(text, out ignored);
        }

        /// 宽松编码：字库无法表示的字符退化为 '?'（与 python errors='replace' 一致）。
        public byte[] EncodeLenient(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new byte[0];
            }

            var buffer = new List<byte>(text.Length * 2);
            var index = 0;

            while (index < text.Length)
            {
                int length;
                var bytes = TryEncodeRune(text, index, out length);

                if (bytes == null)
                {
                    buffer.Add((byte)'?');
                    index += length > 0 ? length : 1;
                    continue;
                }

                if (IsSequenceValidForVfd(bytes))
                {
                    buffer.AddRange(bytes);
                }
                else
                {
                    // 每个字节都退化为 '?'，与 python 对多字节字符的 replace 行为一致
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        buffer.Add((byte)'?');
                    }
                }

                index += length;
            }

            return buffer.ToArray();
        }

        private bool TryEncode(string text, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            var buffer = new List<byte>(text.Length * 2);
            var index = 0;

            while (index < text.Length)
            {
                int length;
                var encoded = TryEncodeRune(text, index, out length);

                if (encoded == null || !IsSequenceValidForVfd(encoded))
                {
                    return false;
                }

                buffer.AddRange(encoded);
                index += length;
            }

            bytes = buffer.ToArray();
            return true;
        }

        /// 编码单个字符（还原代理对）。失败返回 null。
        private byte[] TryEncodeRune(string text, int index, out int consumed)
        {
            consumed = 1;

            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                consumed = 2;
            }

            try
            {
                var chars = new char[consumed];
                text.CopyTo(index, chars, 0, consumed);
                return _codePage.GetBytes(chars, 0, consumed);
            }
            catch (EncoderFallbackException)
            {
                return null;
            }
        }

        /// 按规格书的 2 字节字符有效范围校验一段编码结果。
        private bool IsSequenceValidForVfd(byte[] bytes)
        {
            var i = 0;
            while (i < bytes.Length)
            {
                var b = bytes[i];

                if (b < 0x80)
                {
                    // 1 字节（ASCII / 国际字库字符）
                    i++;
                    continue;
                }

                if (_isSingleByteHigh(b))
                {
                    // 1 字节高位字符（仅 Shift-JIS 的半角片假名 A1h-DFh）
                    i++;
                    continue;
                }

                if (_isLeadByte(b) && i + 1 < bytes.Length && _isTrailByte(bytes[i + 1]))
                {
                    i += 2;
                    continue;
                }

                return false;
            }

            return true;
        }
    }

    /// 
    /// 语言探测与硬件切码。完整对应 CU20029-TE200K 规格书的
    /// "2-byte character"(1Fh 28h 67h 02h n) 与 "2-byte character type"(1Fh 28h 67h 03h n)。
    ///
    /// type: 00=Shift-JIS(日) 01=KSC5601(韩) 02=GB2312(简) 03=Big5(繁)
    /// 字节有效范围同样取自规格书"Character display"一节。
    /// 
    internal static class LanguageDetector
    {
        // 只关闭 2-byte character（回到单字节 ASCII 显示）
        private static readonly byte[] CmdAscii = { 0x1F, 0x28, 0x67, 0x02, 0x00 };

        private static byte[] BuildTypeCommand(byte type)
        {
            return new byte[] { 0x1F, 0x28, 0x67, 0x02, 0x01, 0x1F, 0x28, 0x67, 0x03, type };
        }

        private static bool In(byte value, int lo, int hi)
        {
            return value >= lo && value <= hi;
        }

        // --- 各字库的字节范围（规格书 10.1 Character display / 2-byte character）---

        // Japanese: JIS X0208 (SHIFT-JIS)  c1: 81h-9Fh, E0h-EFh   c2: 40h-7Eh, 80h-FCh
        // Korean:   KSC5601-87           c1: A1h-FEh            c2: A1h-FEh
        // Simplified Chinese GB2312-80  c1: A1h-FEh            c2: A1h-FEh
        // Traditional Chinese Big-5    c1: A1h-FEh            c2: 40h-7Eh, A1h-FEh

        private static readonly TextProfile ProfileAscii =
            new TextProfile(CmdAscii, 20127, "ASCII", false,
                b => false, b => false, b => false);

        private static readonly TextProfile ProfileJapanese =
            new TextProfile(BuildTypeCommand(0x00), 932, "SHIFT_JIS", false,
                b => In(b, 0xA1, 0xDF),                            // 半角片假名按单字节处理
                b => In(b, 0x81, 0x9F) || In(b, 0xE0, 0xEF),
                b => In(b, 0x40, 0x7E) || In(b, 0x80, 0xFC));

        private static readonly TextProfile ProfileKorean =
            new TextProfile(BuildTypeCommand(0x01), 949, "KSC5601", false,
                b => false,
                b => In(b, 0xA1, 0xFE),
                b => In(b, 0xA1, 0xFE));

        private static readonly TextProfile ProfileSimplified =
            // 注意：必须用 20936 (GB2312-80)，不能用 936 (GBK)。
            // 936 是 GB2312 的超集，"體"、"後" 这类 GBK 独有字符会被编成屏幕查不到的码位而显示乱码。
            // 20936 的字表恰好是 GB2312-80 的 6763 个汉字，与 python 的 'gb2312' 协码一致。
            new TextProfile(BuildTypeCommand(0x02), 20936, "GB2312", false,
                b => false,
                b => In(b, 0xA1, 0xFE),
                b => In(b, 0xA1, 0xFE));

        private static readonly TextProfile ProfileTraditional =
            new TextProfile(BuildTypeCommand(0x03), 950, "BIG5", true,
                b => false,
                b => In(b, 0xA1, 0xFE),
                b => In(b, 0x40, 0x7E) || In(b, 0xA1, 0xFE));

        /// 
        /// 探测整行文本应该使用哪种语言/编码。判定顺序与 python 版本完全一致。
        /// 
        public static TextProfile Detect(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                return ProfileAscii;
            }

            text = text.Trim();

            int hiragana = 0, katakana = 0, hangul = 0, cjk = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var code = text[i];
                if (code >= 0x3040 && code <= 0x309F)
                {
                    hiragana++;
                }
                else if (code >= 0x30A0 && code <= 0x30FF)
                {
                    katakana++;
                }
                else if (code >= 0xAC00 && code <= 0xD7AF)
                {
                    hangul++;
                }
                else if ((code >= 0x4E00 && code <= 0x9FFF) ||
                         (code >= 0x3400 && code <= 0x4DBF) ||
                         (code >= 0xF900 && code <= 0xFAFF))
                {
                    cjk++;
                }
            }

            // 1. 带假名 -> 确定是日文
            if (hiragana + katakana > 0)
            {
                return ProfileJapanese;
            }

            // 2. 带谚文 -> 确定是韩文
            if (hangul > 0)
            {
                return ProfileKorean;
            }

            // 3. 纯汉字 -> 按字库优先级依次试探
            if (cjk > 0)
            {
                if (ProfileSimplified.TryEncodeStrict(text)) return ProfileSimplified;
                if (ProfileTraditional.TryEncodeStrict(text)) return ProfileTraditional;
                if (ProfileJapanese.TryEncodeStrict(text)) return ProfileJapanese;
                if (ProfileKorean.TryEncodeStrict(text)) return ProfileKorean;
            }

            // 4. 纯 ASCII
            if (ProfileAscii.TryEncodeStrict(text))
            {
                return ProfileAscii;
            }

            // 夹杂了 ASCII 无法表示的标点/符号：含简体字则走 Big5，否则走 GB2312
            if (ChineseConverter.ToSimplified(text) != text)
            {
                return ProfileTraditional;
            }

            return ProfileSimplified;
        }

        /// 
        /// 显示宽度内的字符替换（与 python replacements 表一致）。
        /// 
        private static readonly KeyValuePair<string, string>[] Replacements =
        {
            new KeyValuePair<string, string>("\u3000", " "), // 全角空格
            new KeyValuePair<string, string>("\t", " "),      // 制表符
            new KeyValuePair<string, string>("—", "-"),       // 破折号
            new KeyValuePair<string, string>("…", "..."),     // 省略号
            new KeyValuePair<string, string>("～", "~"),       // 全角波浪号
            new KeyValuePair<string, string>("\u00A0", ""),   // 不换行空格
            new KeyValuePair<string, string>("•", "●")      // 项目符号
        };

        /// 
        /// 清洗 -> (Big5 时)简繁转换 -> 截断/补齐到固定宽度 -> 按字库范围编码。
        /// 
        public static byte[] EncodeSlice(string sliceText, TextProfile profile, int displayWidth)
        {
            if (sliceText == null)
            {
                sliceText = string.Empty;
            }

            foreach (var replacement in Replacements)
            {
                sliceText = sliceText.Replace(replacement.Key, replacement.Value);
            }

            // Big5 模式下把整行统一转成繁体，避免网易云歌词里混入的简体字
            if (profile.RequiresTraditionalConversion)
            {
                sliceText = ChineseConverter.ToTraditional(sliceText);
            }

            if (sliceText.Length > displayWidth)
            {
                sliceText = sliceText.Substring(0, displayWidth);
            }
            else
            {
                sliceText = sliceText.PadRight(displayWidth);
            }

            return profile.EncodeLenient(sliceText);
        }
    }

    /// 
    /// VFD 串口封装（CU20029-TE200K，20x2 字符，RS-232 38400 8N1）。
    /// 
    internal sealed class VfdDisplay : IDisposable
    {
        private readonly SerialPort _port;
        private readonly object _writeLock = new object();
        private bool _verticalScrollMode;
        private bool _disposed;

        private bool _displayPowerOn = true;
        private bool _cursorVisible;
        private bool _reverseDisplay;

        /// 已写入的字节总数，便于排查。
        public long BytesWritten { get; private set; }

        public string PortName { get { return _port.PortName; } }
        public int BaudRate { get { return _port.BaudRate; } }
        public bool IsVerticalScrollMode { get { return _verticalScrollMode; } }

        public VfdDisplay(string portName, int baudRate)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false
            };

            try
            {
                _port.Open();
            }
            catch (Exception ex)
            {
                try { _port.Dispose(); } catch { }
                throw new InvalidOperationException(
                    "无法打开串口 " + portName + " (" + baudRate + "): " + ex.Message, ex);
            }
        }

        // ---------------- 基础指令 ----------------

        public void Write(params byte[][] chunks)
        {
            lock (_writeLock)
            {
                if (_disposed || !_port.IsOpen)
                {
                    return;
                }

                try
                {
                    foreach (var chunk in chunks)
                    {
                        if (chunk == null || chunk.Length == 0)
                        {
                            continue;
                        }

                        _port.Write(chunk, 0, chunk.Length);
                        BytesWritten += chunk.Length;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog.Error("串口写入失败: " + ex.Message);
                }
            }
        }

        /// 0Ch - Display Clear（清屏，光标回到第一行行首）
        public void Clear()
        {
            Write(new byte[] { 0x0C });
        }

        /// 18h - Line Clear（清除当前行）
        public void ClearCurrentLine()
        {
            Write(new byte[] { 0x18 });
        }

        /// 1Fh 58h n - Brightness level setting (1=25% 2=50% 3=75% 4=100%)
        public void SetBrightness(int level)
        {
            Write(new byte[] { 0x1F, 0x58, (byte)Math.Max(1, Math.Min(4, level)) });
        }

        /// 1Fh 02h - Vertical scroll mode
        public void SetVerticalScrollMode()
        {
            Write(new byte[] { 0x1F, 0x02 });
            _verticalScrollMode = true;
        }

        /// 1Fh 03h - Horizontal scroll mode
        public void SetHorizontalScrollMode()
        {
            Write(new byte[] { 0x1F, 0x03 });
            _verticalScrollMode = false;
        }

        /// 1Fh 24h x y - Cursor set。x: 1~20, y: 1~2
        public void SetCursor(int x, int y)
        {
            Write(new byte[] { 0x1F, 0x24, (byte)Math.Max(1, Math.Min(20, x)), (byte)Math.Max(1, Math.Min(2, y)) });
        }

        /// 1Fh 43h n - 光标显示（0=关，1=块状光标）
        public void SetCursorVisible(bool visible)
        {
            Write(new byte[] { 0x1F, 0x43, (byte)(visible ? 0x01 : 0x00) });
            _cursorVisible = visible;
        }

        /// 1Fh 72h n - 反色显示（0=关，1=开）
        public void SetReverseDisplay(bool enabled)
        {
            Write(new byte[] { 0x1F, 0x72, (byte)(enabled ? 0x01 : 0x00) });
            _reverseDisplay = enabled;
        }

        /// 
        /// 1Fh 28h 61h 40h n - Display power（0=省电关屏，1=开屏）。
        /// 关屏是防烧屏最彻底的手段，但内容是否还在刷新 RAM 里未经验证，
        /// 因此重新开屏后调用方需要强制重画一帧。
        /// 
        public void SetDisplayPower(bool on)
        {
            if (_displayPowerOn == on)
            {
                return;
            }

            Write(new byte[] { 0x1F, 0x28, 0x61, 0x40, (byte)(on ? 0x01 : 0x00) });
            _displayPowerOn = on;

            // 从关屏恢复时不确定 RAM 内容是否还在，先清屏让调用方整帧重画
            if (on)
            {
                Clear();
            }
        }

        public bool IsDisplayPowerOn { get { return _displayPowerOn; } }
        public bool IsCursorVisible { get { return _cursorVisible; } }
        public bool IsReverseDisplay { get { return _reverseDisplay; } }

        /// 启动序列：清屏 + 横向滚动模式 + 亮度 + 光标/反色设置。
        public void Initialize(int brightness, bool cursorVisible, bool reverseDisplay)
        {
            SetDisplayPower(true);
            Clear();
            SetHorizontalScrollMode();
            SetBrightness(brightness);
            SetCursorVisible(cursorVisible);
            SetReverseDisplay(reverseDisplay);
            ConsoleLog.Vfd("屏幕初始化完成 (端口 " + PortName + ", " + BaudRate + ", 亮度 " + brightness + ")");
        }

        /// 
        /// 单行全屏直推：清屏 + 切码指令 + 文本。由 VFD 硬件自行做竖向滚动。
        /// 
        public void SendFullLine(string text)
        {
            var profile = LanguageDetector.Detect(text);
            var payload = profile.EncodeLenient(text ?? string.Empty);
            Write(new byte[] { 0x0C }, profile.Command, payload);
            ConsoleLog.Vfd("单行 [" + profile.Name + "] " + text);
        }

        /// 
        /// 指定行写入一段定长文本：切码指令 + 光标定位 + 文本。
        /// 
        public void SendLine(int lineIndex, TextProfile profile, byte[] payload)
        {
            Write(profile.Command, new byte[] { 0x1F, 0x24, 0x01, (byte)lineIndex }, payload);
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    if (_port.IsOpen)
                    {
                        _port.Write(new byte[] { 0x0C }, 0, 1);
                        _port.Close();
                    }
                }
                catch
                {
                    // 关闭时的异常无需处理
                }

                _disposed = true;

                try
                {
                    _port.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}