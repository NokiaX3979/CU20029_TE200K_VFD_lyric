using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sample.CMD.Lyrics
{
    /// <summary>
    /// 歌词获取服务：搜索网易云 -> 选出最匹配的一首 -> 拉取原文/翻译歌词。
    /// 所有网络操作都在后台线程执行，通过请求代次（generation）丢弃过期结果。
    /// </summary>
    internal sealed class LyricService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly NeteaseClient _client;
        private readonly int _searchLimit;
        private readonly double _durationToleranceSeconds;

        private CancellationTokenSource _cancellation;
        private Task _worker;
        private int _generation;

        public LyricService(int timeoutSeconds, int searchLimit, double durationToleranceSeconds)
        {
            _client = new NeteaseClient(timeoutSeconds);
            _searchLimit = Math.Max(1, searchLimit);
            _durationToleranceSeconds = Math.Max(0, durationToleranceSeconds);
        }

        /// <summary>已完成的歌词（按时间升序）。</summary>
        public LyricSnapshot Current { get; private set; }

        /// <summary>
        /// 请求为指定歌曲加载歌词。会立即取消上一次未完成的请求。
        /// </summary>
        public void RequestAsync(string title, string artist, string album, double? durationSeconds)
        {
            lock (_sync)
            {
                if (_cancellation != null)
                {
                    try { _cancellation.Cancel(); } catch { }
                    try { _cancellation.Dispose(); } catch { }
                }

                _generation++;
                var generation = _generation;

                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;

                _worker = Task.Run(async () =>
                {
                    await FetchAsync(generation, title, artist, album, durationSeconds, token).ConfigureAwait(false);
                }, token);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                Current = null;
            }
        }

        private async Task FetchAsync(int generation, string title, string artist, string album,
            double? durationSeconds, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(title))
                {
                    return;
                }

                ConsoleLog.Lyric("搜索中: " + title + (string.IsNullOrEmpty(artist) ? "" : " - " + artist));

                var candidates = await _client.SearchAsync(BuildQuery(title, artist, album), _searchLimit, token)
                    .ConfigureAwait(false);

                if (candidates.Count == 0)
                {
                    ConsoleLog.Warn("网易云未搜索到匹配歌曲: " + title);
                    return;
                }

                var best = ChooseBestSong(candidates, title, artist, album, durationSeconds);
                if (best == null)
                {
                    ConsoleLog.Warn("未能从候选中选出最佳匹配: " + title);
                    return;
                }

                ConsoleLog.Lyric("选定 SongId=" + best.Id + " 《" + best.Name + "》 " + best.Artists);

                var lyric = await _client.GetLyricAsync(best.Id, token).ConfigureAwait(false);
                if (lyric == null)
                {
                    ConsoleLog.Warn("网易云未返回歌词: " + best.Name);
                    return;
                }

                var lrcText = lyric.Lrc ?? string.Empty;
                var tlyricText = lyric.TranslatedLrc ?? string.Empty;

                // 只有翻译、没有原文时，直接把译文当原文，按"无翻译"处理
                var hasTranslation = !string.IsNullOrEmpty(tlyricText) && tlyricText.Trim().Length > 0;

                if (string.IsNullOrEmpty(lrcText) || lrcText.Trim().Length == 0)
                {
                    if (!hasTranslation)
                    {
                        ConsoleLog.Warn("该歌曲没有歌词: " + best.Name);
                        return;
                    }

                    lrcText = tlyricText;
                    hasTranslation = false;
                }

                bool parsedHasTranslation;
                var entries = LyricParser.BuildGrouped(
                    lrcText, hasTranslation ? tlyricText : null, out parsedHasTranslation);

                if (entries.Count == 0)
                {
                    ConsoleLog.Warn("歌词解析后为空: " + best.Name);
                    return;
                }

                lock (_sync)
                {
                    if (generation != _generation)
                    {
                        ConsoleLog.Lyric("丢弃过期歌词结果: " + best.Name);
                        return;
                    }

                    Current = new LyricSnapshot(best.Id, best.Name, entries, parsedHasTranslation);
                }

                ConsoleLog.Lyric("歌词就绪: " + entries.Count + " 行, 翻译=" + (parsedHasTranslation ? "有" : "无"));
            }
            catch (OperationCanceledException)
            {
                // 切歌导致的中断，属正常情况
            }
            catch (Exception ex)
            {
                ConsoleLog.Error("歌词获取失败: " + ex.Message);
            }
        }

        private static string BuildQuery(string title, string artist, string album)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(title)) parts.Add(title.Trim());
            if (!string.IsNullOrEmpty(artist)) parts.Add(artist.Trim());
            if (!string.IsNullOrEmpty(album)) parts.Add(album.Trim());

            var query = string.Join(" ", parts.ToArray()).Trim();
            return query.Length == 0 ? null : query;
        }

        /// <summary>
        /// 候选打分：0.6*歌名 + 0.25*歌手 + 0.1*专辑，再乘以时长吻合度。
        /// </summary>
        private NeteaseSong ChooseBestSong(List<NeteaseSong> candidates, string title, string artist,
            string album, double? durationSeconds)
        {
            NeteaseSong best = null;
            var bestScore = -1.0;

            foreach (var candidate in candidates)
            {
                var scoreTitle = StringSimilarity.Ratio(title, candidate.Name);
                var scoreArtist = StringSimilarity.Ratio(artist, candidate.Artists);
                var scoreAlbum = StringSimilarity.Ratio(album, candidate.Album);

                var durationScore = 1.0;
                if (durationSeconds.HasValue && candidate.DurationSeconds.HasValue)
                {
                    var localDuration = durationSeconds.Value;
                    var candidateDuration = candidate.DurationSeconds.Value;
                    var diff = Math.Abs(localDuration - candidateDuration);
                    var scale = Math.Max(localDuration, Math.Max(candidateDuration, 1.0));

                    durationScore = diff <= _durationToleranceSeconds
                        ? 1.0
                        : Math.Max(0.0, 1.0 - diff / scale);
                }

                var score = (0.6 * scoreTitle + 0.25 * scoreArtist + 0.1 * scoreAlbum) * durationScore;

                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(candidate.Artists) &&
                    candidate.Artists.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 0.05;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_cancellation != null)
                {
                    try { _cancellation.Cancel(); } catch { }
                    try { _cancellation.Dispose(); } catch { }
                    _cancellation = null;
                }
            }

            _client.Dispose();
        }
    }

    /// <summary>一次成功的歌词加载结果。</summary>
    internal sealed class LyricSnapshot
    {
        public LyricSnapshot(long songId, string songName, List<LyricEntry> entries, bool hasTranslation)
        {
            SongId = songId;
            SongName = songName;
            Entries = entries;
            HasTranslation = hasTranslation;
        }

        public long SongId { get; private set; }
        public string SongName { get; private set; }
        public List<LyricEntry> Entries { get; private set; }
        public bool HasTranslation { get; private set; }
    }
}