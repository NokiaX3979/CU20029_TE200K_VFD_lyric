using System;
using System.Collections.Generic;

namespace Sample.CMD.Lyrics
{
    /// <summary>
    /// difflib.SequenceMatcher(None, a, b).ratio() 的等价实现（纯 C#，无外部依赖）。
    /// 用于网易云搜索结果的"歌名 / 歌手 / 专辑"相似度打分。
    ///
    /// 算法与 Python difflib 一致：递归求最长匹配块 -> 累加匹配字符数 M，
    /// ratio = 2 * M / (len(a) + len(b))。
    ///
    /// 注：difflib 在参与比较的序列长度 > 200 时会启用 autojunk（忽略出现次数过多的元素），
    /// 本实现省略该启发式；本项目的输入（歌名/歌手/专辑）都很短，结果一致。
    /// </summary>
    internal static class StringSimilarity
    {
        /// <summary>difflib 的搜索区间 (alo, ahi, blo, bhi)，四个边界互相独立。</summary>
        private struct SearchRange
        {
            public int Alo;
            public int Ahi;
            public int Blo;
            public int Bhi;

            public SearchRange(int alo, int ahi, int blo, int bhi)
            {
                Alo = alo;
                Ahi = ahi;
                Blo = blo;
                Bhi = bhi;
            }
        }

        private struct MatchingBlock
        {
            public int A;
            public int B;
            public int Size;
        }

        public static double Ratio(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return 0.0;
            }

            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1.0;
            }

            var totalMatched = CountMatched(a, b);
            return 2.0 * totalMatched / (a.Length + (double)b.Length);
        }

        private static int CountMatched(string a, string b)
        {
            var b2j = BuildIndex(b);

            var queue = new Queue<SearchRange>();
            queue.Enqueue(new SearchRange(0, a.Length, 0, b.Length));

            var matched = 0;

            while (queue.Count > 0)
            {
                var range = queue.Dequeue();
                var best = FindLongestMatch(a, b, b2j, range);

                if (best.Size <= 0)
                {
                    continue;
                }

                matched += best.Size;

                if (range.Alo < best.A && range.Blo < best.B)
                {
                    queue.Enqueue(new SearchRange(range.Alo, best.A, range.Blo, best.B));
                }

                var rightA = best.A + best.Size;
                var rightB = best.B + best.Size;
                if (rightA < range.Ahi && rightB < range.Bhi)
                {
                    queue.Enqueue(new SearchRange(rightA, range.Ahi, rightB, range.Bhi));
                }
            }

            return matched;
        }

        private static Dictionary<char, List<int>> BuildIndex(string b)
        {
            var b2j = new Dictionary<char, List<int>>();
            for (var i = 0; i < b.Length; i++)
            {
                List<int> positions;
                if (!b2j.TryGetValue(b[i], out positions))
                {
                    positions = new List<int>();
                    b2j[b[i]] = positions;
                }

                positions.Add(i);
            }

            return b2j;
        }

        private static MatchingBlock FindLongestMatch(string a, string b,
            Dictionary<char, List<int>> b2j, SearchRange range)
        {
            var besti = range.Alo;
            var bestj = range.Blo;
            var bestsize = 0;

            var j2len = new Dictionary<int, int>();

            for (var i = range.Alo; i < range.Ahi; i++)
            {
                var newj2len = new Dictionary<int, int>();

                List<int> positions;
                if (b2j.TryGetValue(a[i], out positions))
                {
                    foreach (var j in positions)
                    {
                        if (j < range.Blo)
                        {
                            continue;
                        }

                        if (j >= range.Bhi)
                        {
                            break;
                        }

                        int previous;
                        var k = j2len.TryGetValue(j - 1, out previous) ? previous + 1 : 1;
                        newj2len[j] = k;

                        if (k > bestsize)
                        {
                            besti = i - k + 1;
                            bestj = j - k + 1;
                            bestsize = k;
                        }
                    }
                }

                j2len = newj2len;
            }

            // difflib 的边界收缩启发式
            while (besti > range.Alo && bestj > range.Blo && a[besti - 1] == b[bestj - 1])
            {
                besti--;
                bestj--;
                bestsize++;
            }

            while (besti + bestsize < range.Ahi && bestj + bestsize < range.Bhi &&
                   a[besti + bestsize] == b[bestj + bestsize])
            {
                bestsize++;
            }

            return new MatchingBlock { A = besti, B = bestj, Size = bestsize };
        }
    }
}