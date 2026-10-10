using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Sample.CMD.Lyrics;
using Sample.CMD.Vfd;

namespace Sample.CMD
{
    internal enum PlaybackState
    {
        Unknown,
        Stopped,
        Paused,
        Playing
    }

    /// <summary>屏幕在"空闲"时的三种状态，逐级递进以防烧屏。</summary>
    internal enum IdleScreenState
    {
        /// <summary>正常亮度、正常供电。</summary>
        Normal,
        /// <summary>降到最低亮度（25%）。</summary>
        Dimmed,
        /// <summary>Display Power OFF，屏幕全黑。</summary>
        PoweredOff
    }

    /// <summary>当前系统媒体会话的快照。</summary>
    internal sealed class MediaSnapshot
    {
        public string SessionId;
        public string Title;
        public string Artist;
        public string Album;
        public double? DurationSeconds;
        public PlaybackState Playback = PlaybackState.Unknown;
    }

    /// <summary>
    /// 核心：把"当前播放位置 + 歌词"转换成 VFD 上的滚动显示。
    ///
    /// 时间轴用锚点法推算：收到 SMTC 的 position 时记录 (时间戳, 位置)，
    /// 之后按单调时钟外推，因此即使播放器不上报进度也能平滑滚动。
    /// </summary>
    internal sealed class VfdLyricController : IDisposable
    {
        /// <summary>自动变暗时使用的亮度档位（1 = 25%）。</summary>
        private const int DimBrightness = 1;

        private readonly object _sync = new object();
        private readonly VfdDisplay _display;
        private readonly LyricService _lyricService;
        private readonly AppConfig _config;

        private MediaSnapshot _media = new MediaSnapshot();
        private string _activeSessionId;

        private double? _anchorTimestamp;
        private double _anchorPosition;

        /// <summary>暂停时冻结的播放位置（暂停期间不外推，避免恢复播放后位置突跳）。</summary>
        private double _frozenPosition;

        /// <summary>SMTC 最近一次上报的原始位置值。用于识别"播放器不刷新进度"的陈旧数据。</summary>
        private double? _lastRawPosition;

        // ---- 自动息屏（防烧屏）----
        private double? _idleSince;
        private IdleScreenState _idleState = IdleScreenState.Normal;

        /// <summary>用户按 P 手动关/开屏后置位，挂起自动息屏，避免两种状态互相覆盖。</summary>
        private bool _manualOverride;

        /// <summary>用户设定的亮度档位（自动变暗前的原始值）。</summary>
        private int _userBrightness = 3;

        // 当前歌词句的渲染状态（仅渲染线程访问）
        private double? _activeBaseTime;
        private string _activeText1 = string.Empty;
        private string _activeText2 = string.Empty;
        private TextProfile _activeProfile1;
        private TextProfile _activeProfile2;
        private string _lastSlice1;
        private string _lastSlice2;

        /// <summary>下一次渲染必须无条件重写整帧（唤醒/关屏恢复后内容不可信）。</summary>
        private bool _forceRedraw;

        private Thread _renderThread;
        private volatile bool _running;

        public VfdLyricController(VfdDisplay display, LyricService lyricService, AppConfig config)
        {
            _display = display;
            _lyricService = lyricService;
            _config = config;
            _userBrightness = Math.Max(1, Math.Min(4, config.Brightness));
        }

        public string ActiveSessionId
        {
            get { lock (_sync) { return _activeSessionId; } }
        }

        // ---------------------------------------------------------------
        // 对外事件入口（由 SMTC 回调调用，可能是任意线程）
        // ---------------------------------------------------------------

        public void OnMediaProperties(string sessionId, string title, string artist, string album)
        {
            bool titleChanged;

            lock (_sync)
            {
                if (!AcceptSession(sessionId, false))
                {
                    ConsoleLog.Debug("忽略非活跃会话的曲目变更: " + sessionId);
                    return;
                }

                var newTitle = CleanText(title);

                titleChanged = !string.Equals(_media.Title, newTitle, StringComparison.Ordinal);

                // 空值不覆盖已有信息，避免 SMTC 上报的瞬时空值把曲目清掉
                if (newTitle != null)
                {
                    _media.Title = newTitle;
                }

                var newArtist = CleanText(artist);
                if (newArtist != null)
                {
                    _media.Artist = newArtist;
                }

                var newAlbum = CleanText(album);
                if (newAlbum != null)
                {
                    _media.Album = newAlbum;
                }

                if (titleChanged)
                {
                    _media.SessionId = _activeSessionId;
                    ResetActiveLine();
                    _lastRawPosition = null;
                    SetAnchorLocked(0.0);
                }
            }

            if (titleChanged)
            {
                var snapshot = GetMedia();
                ConsoleLog.Song("曲目变更: " + snapshot.Title +
                                (string.IsNullOrEmpty(snapshot.Artist) ? "" : " - " + snapshot.Artist));

                if (_config.FetchLyrics)
                {
                    _lyricService.RequestAsync(snapshot.Title, snapshot.Artist, snapshot.Album, snapshot.DurationSeconds);
                }
                else
                {
                    _lyricService.Clear();
                }
            }
        }

        public void OnPlaybackState(string sessionId, PlaybackState state)
        {
            bool sessionChanged;

            lock (_sync)
            {
                var previousSession = _activeSessionId;

                if (!AcceptSession(sessionId, state == PlaybackState.Playing))
                {
                    return;
                }

                sessionChanged = !string.Equals(previousSession, _activeSessionId, StringComparison.OrdinalIgnoreCase);

                if (sessionChanged)
                {
                    // 切换到另一个会话，清掉上一首的状态，随后由调用方补拉曲目信息
                    _media = new MediaSnapshot
                    {
                        SessionId = _activeSessionId,
                        Playback = state
                    };
                    _anchorTimestamp = null;
                    _lastRawPosition = null;
                    ResetActiveLine();
                }
                else
                {
                    _media.Playback = state;

                    if (state == PlaybackState.Playing)
                    {
                        // 恢复播放：从冻结的位置继续外推（若期间没有新的时间轴事件）
                        if (!_anchorTimestamp.HasValue)
                        {
                            SetAnchorLocked(_frozenPosition);
                        }

                        _manualOverride = false;
                        ResetIdleTimerLocked();
                    }
                    else
                    {
                        // 暂停：冻结当前进度，避免暂停期间位置一路外推
                        _frozenPosition = ComputePositionLocked();
                        _anchorTimestamp = null;
                    }
                }
            }

            ConsoleLog.Info("播放状态: " + state + " (" + sessionId + ")" +
                            (sessionChanged ? " [会话切换]" : ""));
        }

        public void OnTimeline(string sessionId, double positionSeconds, double? durationSeconds)
        {
            lock (_sync)
            {
                if (!AcceptSession(sessionId, false))
                {
                    return;
                }

                if (durationSeconds.HasValue && durationSeconds.Value > 0)
                {
                    _media.DurationSeconds = durationSeconds;
                }

                // 有些播放器（实测 foobar2000）只在真正发生跳播时才更新 SMTC 的 Position，
                // 平时一直返回同一个陈旧值。如果无条件拿它重新锚定，播放位置会不断倒退。
                // 因此这里做迟滞判断：
                //   1. 与外推位置接近            -> 忽略（外推更准）
                //   2. 明显靠前（快进/进度正常刷新）-> 接受
                //   3. 靠后但数值和上次上报不同    -> 认为是真实的向后跳播，接受
                //   4. 靠后且数值与上次完全相同    -> 陈旧数据，忽略
                var sameAsLastRaw = _lastRawPosition.HasValue &&
                                    Math.Abs(_lastRawPosition.Value - positionSeconds) < 0.05;

                if (_anchorTimestamp.HasValue)
                {
                    var extrapolated = ComputePositionLocked();
                    var drift = positionSeconds - extrapolated;

                    // 只有"比外推位置靠后"且"和上次上报一模一样"时才判定为陈旧数据
                    var looksStale = drift < -0.35 && sameAsLastRaw;

                    if (!looksStale)
                    {
                        SetAnchorLocked(positionSeconds);
                    }
                }
                else
                {
                    SetAnchorLocked(positionSeconds);
                }

                _lastRawPosition = positionSeconds;
            }
        }

        public void OnSessionClosed(string sessionId)
        {
            lock (_sync)
            {
                if (string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _activeSessionId = null;
                    _media = new MediaSnapshot();
                    _anchorTimestamp = null;
                    _lastRawPosition = null;
                    ResetActiveLine();
                    _lyricService.Clear();
                    ConsoleLog.Info("当前会话已关闭: " + sessionId);
                }
            }
        }

        /// <summary>
        /// 周期性复查使用：只用来兜底捕获丢失的"暂停/停止"事件。
        ///
        /// 刻意**不**在这里刷新播放位置——部分播放器（foobar2000）的 SMTC Position
        /// 并不随播放前进，周期性重新锚定会让画面倒退。位置只由真实的时间轴事件更新。
        /// </summary>
        public void ApplyPolledState(string sessionId, PlaybackState state)
        {
            if (state == PlaybackState.Unknown)
            {
                return;
            }

            lock (_sync)
            {
                if (string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                    _media.Playback == state)
                {
                    return;
                }
            }

            OnPlaybackState(sessionId, state);
        }

        /// <summary>会话切换后需要重新拉取该会话的曲目/时间轴信息。</summary>
        public bool NeedsSessionRefresh(string sessionId)
        {
            lock (_sync)
            {
                return string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                       string.IsNullOrEmpty(_media.Title);
            }
        }

        public MediaSnapshot GetMedia()
        {
            lock (_sync)
            {
                return new MediaSnapshot
                {
                    SessionId = _media.SessionId,
                    Title = _media.Title,
                    Artist = _media.Artist,
                    Album = _media.Album,
                    DurationSeconds = _media.DurationSeconds,
                    Playback = _media.Playback
                };
            }
        }

        public double GetCurrentPosition()
        {
            lock (_sync)
            {
                return ComputePositionLocked();
            }
        }

        // ---------------------------------------------------------------
        // 交互式控制（热键调用）
        // ---------------------------------------------------------------

        /// <summary>当前生效的用户亮度档位（1~4）。</summary>
        public int GetBrightness()
        {
            lock (_sync) { return _userBrightness; }
        }

        /// <summary>调整亮度档位（循环 1~4），返回调整后的值。</summary>
        public int AdjustBrightness(int delta)
        {
            int target;
            lock (_sync)
            {
                target = WrapLevel(_userBrightness + delta, 1, 4);
                _manualOverride = false;
                ResetIdleTimerLocked();
                ApplyUserBrightnessLocked(target);
            }

            return target;
        }

        public bool GetCursorVisible()
        {
            return _display.IsCursorVisible;
        }

        public void ToggleCursorVisible()
        {
            lock (_sync) { WakeScreenLocked(); }
            _display.SetCursorVisible(!_display.IsCursorVisible);
            ConsoleLog.Vfd("光标显示: " + (_display.IsCursorVisible ? "开" : "关"));
        }

        public bool GetReverseDisplay()
        {
            return _display.IsReverseDisplay;
        }

        public void ToggleReverseDisplay()
        {
            lock (_sync) { WakeScreenLocked(); }
            _display.SetReverseDisplay(!_display.IsReverseDisplay);
            ConsoleLog.Vfd("反色显示: " + (_display.IsReverseDisplay ? "开" : "关"));
        }

        /// <summary>
        /// 手动开关屏幕电源。按下后会挂起自动息屏逻辑（_manualOverride），
        /// 直到恢复播放或按其它热键，避免手动状态和自动逻辑互相打架。
        /// </summary>
        public void ToggleDisplayPower()
        {
            bool turnOn;

            lock (_sync)
            {
                turnOn = !_display.IsDisplayPowerOn;
                _manualOverride = true;
                ResetIdleTimerLocked();

                if (turnOn)
                {
                    _idleState = IdleScreenState.Normal;
                    ApplyUserBrightnessLocked(_userBrightness);
                }
            }

            _display.SetDisplayPower(turnOn);

            if (turnOn)
            {
                lock (_sync) { ForceRedrawLocked(); }
            }

            ConsoleLog.Vfd("屏幕电源: " + (turnOn ? "开" : "关") + "（手动，自动息屏已挂起）");
        }

        /// <summary>
        /// 任何"有活动"的信号（热键按键、恢复播放、换曲）都会唤醒屏幕并重置息屏计时。
        /// </summary>
        public void NotifyActivity()
        {
            lock (_sync)
            {
                _manualOverride = false;
                ResetIdleTimerLocked();
                WakeScreenLocked();
            }
        }

        /// <summary>底部状态栏文本。</summary>
        public string GetStatusText()
        {
            lock (_sync)
            {
                var position = ComputePositionLocked();
                var duration = _media.DurationSeconds;

                var time = FormatTime(position);
                if (duration.HasValue)
                {
                    time += " / " + FormatTime(duration.Value);
                }

                var song = string.IsNullOrEmpty(_media.Title) ? "(无)" : _media.Title;
                if (!string.IsNullOrEmpty(_media.Artist))
                {
                    song += " - " + _media.Artist;
                }

                string state;
                if (_media.Playback == PlaybackState.Playing)
                {
                    state = "播放";
                }
                else
                {
                    state = string.IsNullOrEmpty(_media.Title) ? "空闲" : "暂停";
                }

                return string.Format(
                    "{0} | {1} | {2} | 亮度 {3} | 光标{4} | 反色{5} | 屏幕{6}{7}",
                    state,
                    song,
                    time,
                    _userBrightness,
                    _display.IsCursorVisible ? "开" : "关",
                    _display.IsReverseDisplay ? "开" : "关",
                    _display.IsDisplayPowerOn ? "开" : "关",
                    _manualOverride ? "(手动)" : string.Empty);
            }
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0 || double.IsInfinity(seconds))
            {
                seconds = 0;
            }

            var total = (int)seconds;
            return string.Format("{0:D2}:{1:D2}", total / 60, total % 60);
        }

        private static int WrapLevel(int value, int lo, int hi)
        {
            var span = hi - lo + 1;
            while (value > hi) value -= span;
            while (value < lo) value += span;
            return value;
        }

        // ---------------------------------------------------------------
        // 渲染线程
        // ---------------------------------------------------------------

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "VfdLyricRenderer"
            };
            _renderThread.Start();

            ConsoleLog.Info("渲染线程已启动 (间隔 " + _config.RefreshIntervalMs + "ms)");
        }

        public void Stop()
        {
            _running = false;
            var thread = _renderThread;
            if (thread != null && thread.IsAlive)
            {
                thread.Join(1500);
            }
        }

        private void RenderLoop()
        {
            var interval = Math.Max(10, _config.RefreshIntervalMs);
            var nextPositionLog = NowSeconds() + 1.0;

            // 息屏状态的写入频率很低（只在跨过阈值的那一帧变化），单独低频轮询即可
            var nextIdleCheck = 0.0;

            while (_running)
            {
                try
                {
                    var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

                    if (now >= nextIdleCheck)
                    {
                        nextIdleCheck = now + 0.25;
                        UpdateIdleState();
                    }

                    RenderOnce();
                }
                catch (Exception ex)
                {
                    ConsoleLog.Error("渲染异常: " + ex.Message);
                }

                var now2 = NowSeconds();
                if (ConsoleLog.VerboseEnabled && now2 >= nextPositionLog)
                {
                    nextPositionLog = now2 + 1.0;
                    LogPosition();
                }

                if (_running)
                {
                    Thread.Sleep(interval);
                }
            }
        }

        /// <summary>
        /// 自动息屏状态机。
        ///
        /// 计时**从暂停那一刻开始**（不是从"最后一次屏幕写入"开始），符合用户预期：
        /// 暂停 -> IdleDimSeconds 后变暗 -> IdleOffSeconds 后关屏。
        /// 程序启动后没有音乐时同样视为空闲，从启动时刻开始计时。
        ///
        /// 恢复播放、或任意热键，都会唤醒并重置计时，逐级恢复亮度与供电。
        /// </summary>
        private void UpdateIdleState()
        {
            var changed = false;

            lock (_sync)
            {
                var now = NowSeconds();
                var isIdle = _media.Playback != PlaybackState.Playing;

                if (!isIdle)
                {
                    ResetIdleTimerLocked();
                    changed = WakeScreenLocked();
                }
                else
                {
                    if (!_idleSince.HasValue)
                    {
                        _idleSince = now;
                    }

                    var elapsed = now - _idleSince.Value;

                    var wantOff = _config.IdleOffSeconds > 0 && elapsed >= _config.IdleOffSeconds;
                    var wantDim = !wantOff && _config.IdleDimSeconds > 0 && elapsed >= _config.IdleDimSeconds;

                    var target = wantOff ? IdleScreenState.PoweredOff
                        : wantDim ? IdleScreenState.Dimmed
                        : IdleScreenState.Normal;

                    // 手动 P 键接管期间不自动干预
                    if (_manualOverride)
                    {
                        target = IdleScreenState.Normal;
                    }

                    if (target != _idleState)
                    {
                        _idleState = target;
                        changed = true;
                    }
                }

                if (changed)
                {
                    ApplyIdleStateLocked();
                }
            }

            if (changed)
            {
                ConsoleLog.Vfd("屏幕状态: " + DescribeIdleState(_idleState) +
                               "（暂停已 " + GetIdleElapsedText() + "）");
            }
        }

        private static string DescribeIdleState(IdleScreenState state)
        {
            switch (state)
            {
                case IdleScreenState.Dimmed: return "变暗 25%";
                case IdleScreenState.PoweredOff: return "已关屏";
                default: return "正常";
            }
        }

        private string GetIdleElapsedText()
        {
            lock (_sync)
            {
                if (!_idleSince.HasValue)
                {
                    return "0s";
                }

                return Math.Round(NowSeconds() - _idleSince.Value) + "s";
            }
        }

        /// <summary>按当前状态实际写屏。调用方必须持有 _sync。</summary>
        private void ApplyIdleStateLocked()
        {
            switch (_idleState)
            {
                case IdleScreenState.PoweredOff:
                    _display.SetDisplayPower(false);
                    break;

                case IdleScreenState.Dimmed:
                    _display.SetDisplayPower(true);
                    _display.SetBrightness(DimBrightness);
                    break;

                default:
                    _display.SetDisplayPower(true);
                    _display.SetBrightness(_userBrightness);
                    break;
            }

            ForceRedrawLocked();
        }

        /// <summary>恢复正常的亮度与供电。返回是否发生了变化。</summary>
        private bool WakeScreenLocked()
        {
            if (_idleState == IdleScreenState.Normal && _display.IsDisplayPowerOn)
            {
                return false;
            }

            _idleState = IdleScreenState.Normal;
            ApplyIdleStateLocked();
            return true;
        }

        private void ApplyUserBrightnessLocked(int level)
        {
            _userBrightness = level;

            // 变暗状态下不改真实亮度，等恢复时再应用，避免"变暗时调亮度"被立刻吃掉
            if (_idleState == IdleScreenState.Normal)
            {
                _display.SetBrightness(_userBrightness);
            }
        }

        /// <summary>让下一帧无条件重画（关屏恢复 / 清屏后内容不可信时使用）。</summary>
        private void ForceRedrawLocked()
        {
            _forceRedraw = true;
            _lastSlice1 = null;
            _lastSlice2 = null;
            _activeProfile1 = null;
            _activeProfile2 = null;
            _activeBaseTime = null;
        }

        private void ResetIdleTimerLocked()
        {
            _idleSince = null;
        }

        private static double NowSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private void LogPosition()
        {
            lock (_sync)
            {
                ConsoleLog.Debug("位置 " + ComputePositionLocked().ToString("F1") + "s / " +
                                 (_media.DurationSeconds.HasValue ? _media.DurationSeconds.Value.ToString("F0") : "?") +
                                 "s  状态=" + _media.Playback + "  基准时间=" +
                                 (_anchorTimestamp.HasValue ? "有" : "无") + "  基准位置=" + _anchorPosition.ToString("F1"));
            }
        }

        private void RenderOnce()
        {
            double position;
            LyricSnapshot lyrics;

            lock (_sync)
            {
                // 关屏状态下没有任何东西可见，也不必浪费串口带宽
                if (!_display.IsDisplayPowerOn)
                {
                    return;
                }

                var playing = _media.Playback == PlaybackState.Playing;

                // 暂停时只重画"冻结的那一帧"（关屏恢复 / 手动唤醒后需要把内容补回去），
                // 不会继续推进。
                if (!playing && !_forceRedraw)
                {
                    return;
                }

                if (string.IsNullOrEmpty(_media.Title))
                {
                    return;
                }

                position = ComputePositionLocked();
            }

            lyrics = _lyricService.Current;
            if (lyrics == null || lyrics.Entries.Count == 0)
            {
                return;
            }

            var targetTime = position + _config.LyricOffsetSeconds;

            var index = FindIndexAt(lyrics.Entries, targetTime);
            if (index < 0)
            {
                return;
            }

            var entry = lyrics.Entries[index];
            if (entry.Parts.Count == 0)
            {
                return;
            }

            if (!lyrics.HasTranslation)
            {
                RenderSingleLine(entry);
            }
            else
            {
                RenderDualLine(lyrics.Entries, index, entry, targetTime);
            }
        }

        /// <summary>整曲无翻译：清屏 + 切码 + 整句直推，交给 VFD 硬件做竖向滚动。</summary>
        private void RenderSingleLine(LyricEntry entry)
        {
            lock (_sync)
            {
                if (!_forceRedraw && _activeBaseTime.HasValue &&
                    Math.Abs(_activeBaseTime.Value - entry.Time) < 0.0005)
                {
                    return;
                }

                _activeBaseTime = entry.Time;
                _lastSlice1 = null;
                _lastSlice2 = null;
            }

            if (!_display.IsVerticalScrollMode)
            {
                _display.SetVerticalScrollMode();
            }

            _display.SendFullLine(entry.Primary);

            lock (_sync) { _forceRedraw = false; }
        }

        /// <summary>整曲有翻译：双行横向滚屏，逐帧比较切片，变化时才写串口。</summary>
        private void RenderDualLine(List<LyricEntry> entries, int index, LyricEntry entry, double targetTime)
        {
            string text1, text2, prevSlice1, prevSlice2;
            TextProfile profile1, profile2;
            double baseTime;

            if (_display.IsVerticalScrollMode)
            {
                _display.SetHorizontalScrollMode();
            }

            var duration = 5.0;
            if (index + 1 < entries.Count)
            {
                duration = Math.Max(0.5, entries[index + 1].Time - entry.Time);
            }

            lock (_sync)
            {
                if (!_activeBaseTime.HasValue || Math.Abs(_activeBaseTime.Value - entry.Time) >= 0.0005)
                {
                    _activeBaseTime = entry.Time;
                    _activeText1 = entry.Primary;
                    _activeText2 = entry.Secondary;
                    _activeProfile1 = LanguageDetector.Detect(_activeText1);
                    _activeProfile2 = LanguageDetector.Detect(_activeText2);
                    _lastSlice1 = null;
                    _lastSlice2 = null;
                }

                // 热键线程可能在此之后把状态清空（ForceRedraw），所以必须在锁内取快照，
                // 锁外只使用局部变量，避免与手动唤醒竞争。
                text1 = _activeText1;
                text2 = _activeText2;
                profile1 = _activeProfile1;
                profile2 = _activeProfile2;
                baseTime = _activeBaseTime ?? entry.Time;
                prevSlice1 = _lastSlice1;
                prevSlice2 = _lastSlice2;
            }

            if (profile1 == null || profile2 == null)
            {
                return;
            }

            var elapsed = Math.Max(0.0, targetTime - baseTime);
            var width = Math.Max(1, _config.DisplayWidth);

            var slice1 = SliceAt(text1, GetScrollOffset(text1, elapsed, duration), width);
            var slice2 = SliceAt(text2, GetScrollOffset(text2, elapsed, duration), width);

            if (!string.Equals(slice1, prevSlice1, StringComparison.Ordinal))
            {
                var payload = LanguageDetector.EncodeSlice(slice1, profile1, width);
                _display.SendLine(1, profile1, payload);

                if (_config.ShowFrameLog)
                {
                    ConsoleLog.Raw("line1 [" + profile1.Name + "] " + slice1);
                }
            }

            if (!string.Equals(slice2, prevSlice2, StringComparison.Ordinal))
            {
                var payload = LanguageDetector.EncodeSlice(slice2, profile2, width);
                _display.SendLine(2, profile2, payload);

                if (_config.ShowFrameLog)
                {
                    ConsoleLog.Raw("line2 [" + profile2.Name + "] " + slice2);
                }
            }

            lock (_sync)
            {
                _lastSlice1 = slice1;
                _lastSlice2 = slice2;
                _forceRedraw = false;
            }
        }

        /// <summary>
        /// 根据歌词可用时长动态计算滚屏偏移：首尾各停顿 <see cref="AppConfig.ScrollPauseSeconds"/> 秒，
        /// 中间线性推进；时长过短时按比例压缩。
        /// </summary>
        private int GetScrollOffset(string text, double elapsedTime, double duration)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var displayWidth = Math.Max(1, _config.DisplayWidth);
            var maxOffset = text.Length - displayWidth;
            if (maxOffset <= 0)
            {
                return 0;
            }

            var pauseStart = Math.Min(_config.ScrollPauseSeconds, duration * 0.2);
            var pauseEnd = Math.Min(_config.ScrollPauseSeconds, duration * 0.2);
            var activeTime = duration - pauseStart - pauseEnd;

            if (activeTime <= 0)
            {
                var fallbackProgress = duration > 0
                    ? Math.Min(1.0, Math.Max(0.0, elapsedTime / duration))
                    : 0;
                return (int)Math.Min(maxOffset, fallbackProgress * (maxOffset + 1));
            }

            if (elapsedTime < pauseStart)
            {
                return 0;
            }

            if (elapsedTime >= duration - pauseEnd)
            {
                return maxOffset;
            }

            var progress = Math.Min(1.0, Math.Max(0.0, (elapsedTime - pauseStart) / activeTime));
            return (int)Math.Min(maxOffset, progress * (maxOffset + 1));
        }

        private static string SliceAt(string text, int offset, int width)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            offset = Math.Max(0, Math.Min(offset, Math.Max(0, text.Length - width)));
            var length = Math.Min(width, text.Length - offset);
            return text.Substring(offset, length);
        }

        private static int FindIndexAt(List<LyricEntry> entries, double targetTime)
        {
            var index = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Time <= targetTime + 1e-6)
                {
                    index = i;
                }
                else
                {
                    break;
                }
            }

            return index;
        }

        // ---------------------------------------------------------------
        // 内部状态辅助
        // ---------------------------------------------------------------

        /// <summary>
        /// 会话过滤：优先跟随"正在播放"的会话；否则第一个上报的会话成为活跃会话。
        /// 其它会话（浏览器视频、其它播放器）的干扰事件会被丢弃。
        /// </summary>
        private bool AcceptSession(string sessionId, bool takeOver)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            if (string.IsNullOrEmpty(_activeSessionId))
            {
                _activeSessionId = sessionId;
                return true;
            }

            if (string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (takeOver)
            {
                _activeSessionId = sessionId;
                return true;
            }

            return false;
        }

        private double ComputePositionLocked()
        {
            if (!_anchorTimestamp.HasValue)
            {
                return _frozenPosition;
            }

            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            return _anchorPosition + Math.Max(0.0, now - _anchorTimestamp.Value);
        }

        private void SetAnchorLocked(double positionSeconds)
        {
            _anchorTimestamp = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            _anchorPosition = positionSeconds;
            _frozenPosition = positionSeconds;
        }

        private void ResetActiveLine()
        {
            _activeBaseTime = null;
            _activeText1 = string.Empty;
            _activeText2 = string.Empty;
            _activeProfile1 = null;
            _activeProfile2 = null;
            _lastSlice1 = null;
            _lastSlice2 = null;
        }

        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        public void Dispose()
        {
            Stop();
            _lyricService.Dispose();

            // 退出时不要把屏幕留在"关屏"或"变暗"状态
            try
            {
                lock (_sync)
                {
                    _idleState = IdleScreenState.Normal;
                    _display.SetDisplayPower(true);
                    _display.SetBrightness(_userBrightness);
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Debug("退出时恢复屏幕状态失败: " + ex.Message);
            }
        }
    }
}