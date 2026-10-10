using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Sample.CMD.Lyrics
{
    internal sealed class NeteaseSong
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Artists { get; set; }
        public string Album { get; set; }
        public double? DurationSeconds { get; set; }
    }

    internal sealed class NeteaseLyric
    {
        public string Lrc { get; set; }
        public string TranslatedLrc { get; set; }
    }

    /// <summary>
    /// 网易云音乐公开接口客户端。
    ///
    /// 搜索:  https://music.163.com/api/search/get
    /// 歌词:  https://music.163.com/api/song/lyric
    ///
    /// 主接口失败时自动回退到 /web 与 /v1 变体。
    /// </summary>
    internal sealed class NeteaseClient : IDisposable
    {
        private static readonly string[] SearchEndpoints =
        {
            "https://music.163.com/api/search/get",
            "https://music.163.com/api/search/get/web"
        };

        private static readonly string[] LyricEndpoints =
        {
            "https://music.163.com/api/song/lyric",
            "https://music.163.com/api/song/lyric/v1"
        };

        private readonly HttpClient _http;
        private readonly int _timeoutSeconds;

        public NeteaseClient(int timeoutSeconds)
        {
            _timeoutSeconds = Math.Max(1, timeoutSeconds);

            var handler = new HttpClientHandler();
            try
            {
                // 部分网络环境下默认代理不可用，失败时回退系统代理
                handler.UseProxy = true;
                handler.Proxy = WebRequest.GetSystemWebProxy();
                if (handler.Proxy != null)
                {
                    handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }
            }
            catch
            {
                handler.UseProxy = true;
            }

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_timeoutSeconds) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        }

        public async Task<List<NeteaseSong>> SearchAsync(string query, int limit, CancellationToken token)
        {
            var parameters = "s=" + Uri.EscapeDataString(query) +
                             "&type=1&offset=0&limit=" + limit.ToString(CultureInfo.InvariantCulture);

            foreach (var endpoint in SearchEndpoints)
            {
                var json = await GetJsonAsync(endpoint + "?" + parameters, token).ConfigureAwait(false);
                var songs = ParseSearchResults(json);
                if (songs.Count > 0)
                {
                    return songs;
                }
            }

            return new List<NeteaseSong>();
        }

        public async Task<NeteaseLyric> GetLyricAsync(long songId, CancellationToken token)
        {
            var parameters = "os=pc&id=" + songId.ToString(CultureInfo.InvariantCulture) + "&lv=-1&tv=-1";

            foreach (var endpoint in LyricEndpoints)
            {
                var json = await GetJsonAsync(endpoint + "?" + parameters, token).ConfigureAwait(false);
                if (json == null)
                {
                    continue;
                }

                var lrc = ReadString(json, "lrc", "lyric");
                var tlyric = ReadString(json, "tlyric", "lyric");

                if (!string.IsNullOrEmpty(lrc) || !string.IsNullOrEmpty(tlyric))
                {
                    return new NeteaseLyric { Lrc = lrc, TranslatedLrc = tlyric };
                }
            }

            return null;
        }

        private async Task<string> GetJsonAsync(string url, CancellationToken token)
        {
            try
            {
                using (var response = await _http.GetAsync(url, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        ConsoleLog.Warn("网易云接口返回 " + (int)response.StatusCode + " (" + url.Split('?')[0] + ")");
                        return null;
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return string.IsNullOrEmpty(body) ? null : body;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn("请求网易云接口失败: " + ex.Message);
                return null;
            }
        }

        private static List<NeteaseSong> ParseSearchResults(string json)
        {
            var result = new List<NeteaseSong>();
            if (string.IsNullOrEmpty(json))
            {
                return result;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn("网易云搜索结果解析失败: " + ex.Message);
                return result;
            }

            var songs = root["result"]?["songs"] as JArray;
            if (songs == null)
            {
                return result;
            }

            foreach (var item in songs.OfType<JObject>())
            {
                var idToken = item["id"];
                if (idToken == null)
                {
                    continue;
                }

                long id;
                if (!long.TryParse(idToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    continue;
                }

                var artistNames = (item["artists"] as JArray)?.OfType<JObject>()
                    .Select(a => a.Value<string>("name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToArray();

                double? duration = null;
                var durationToken = item["duration"] ?? item["dt"];
                if (durationToken != null)
                {
                    long ms;
                    if (long.TryParse(durationToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
                    {
                        duration = ms / 1000.0;
                    }
                }

                result.Add(new NeteaseSong
                {
                    Id = id,
                    Name = item.Value<string>("name") ?? string.Empty,
                    Artists = artistNames != null ? string.Join(",", artistNames) : string.Empty,
                    Album = item["album"]?.Value<string>("name") ?? string.Empty,
                    DurationSeconds = duration
                });
            }

            return result;
        }

        private static string ReadString(string json, string container, string field)
        {
            if (string.IsNullOrEmpty(json))
            {
                return string.Empty;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch
            {
                return string.Empty;
            }

            var value = root[container]?[field];
            return value == null || value.Type == JTokenType.Null ? string.Empty : value.ToString();
        }

        public void Dispose()
        {
            try
            {
                _http.Dispose();
            }
            catch
            {
            }
        }
    }
}