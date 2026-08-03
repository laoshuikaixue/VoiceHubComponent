using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceHubComponent.Services
{
    /// <summary>
    /// 歌词升级的候选元数据（当前歌曲）
    /// </summary>
    public sealed class LyricUpgradeMeta
    {
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }

        /// <summary>
        /// 毫秒，用于跨平台匹配时过滤时长差异过大的候选
        /// </summary>
        public double DurationMs { get; init; }
    }

    /// <summary>
    /// 歌词升级的搜索结果候选
    /// </summary>
    public sealed class LyricSearchCandidate
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;

        /// <summary>
        /// 毫秒
        /// </summary>
        public double DurationMs { get; set; }

        public string Platform { get; set; } = string.Empty;
        public string MusicId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 歌词候选匹配打分，对照主项目 useMusicSources.ts 的 pickBestLyricCandidate 实现。
    /// </summary>
    public static class LyricMatcher
    {
        /// <summary>
        /// 子串占长串的最低比例，过低视为巧合
        /// </summary>
        private const double NameContainMinRatio = 0.34;

        private const int MinScore = 4;

        private static readonly Regex FeatRegex = new(@"\b(?:feat|ft)\.?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PunctuationRegex = new(@"[、;，,/|()（）·・\s\-_'" + "\"`~!?？！.。《》【】\\[\\]{}^*@#$%+=\\\\]+", RegexOptions.Compiled);
        private static readonly Regex ArtistSplitRegex = new(@"[、&＆;，,/|·・]+", RegexOptions.Compiled);

        private static readonly (string Key, Regex Pattern)[] VersionMarkers =
        {
            ("live", new Regex(@"\blive\b|现场", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("remix", new Regex(@"\bremix\b|混音", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("instrumental", new Regex(@"\binstrumental\b|伴奏", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("acoustic", new Regex(@"\bacoustic\b|不插电", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("cover", new Regex(@"\bcover\b|翻唱", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("demo", new Regex(@"\bdemo\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("remaster", new Regex(@"\bremaster(?:ed)?\b|重制", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("edit", new Regex(@"\bedit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("new", new Regex(@"新版", RegexOptions.Compiled)),
            ("old", new Regex(@"旧版", RegexOptions.Compiled))
        };

        /// <summary>
        /// 歌词候选匹配文本归一化：小写，去 feat./标点/空白，&/＆ → and
        /// </summary>
        public static string NormalizeLyricMatchText(string? value)
        {
            var text = FeatRegex.Replace(value ?? string.Empty, string.Empty);
            text = text.Replace('&', '＆').Replace("＆", "and").Replace("&", "and");
            text = PunctuationRegex.Replace(text, string.Empty);
            return text.ToLowerInvariant();
        }

        /// <summary>
        /// 拆分多歌手文本，避免同名歌曲只比较整串导致误判
        /// </summary>
        public static List<string> SplitLyricMatchArtists(string? value)
        {
            return ArtistSplitRegex.Split(value ?? string.Empty)
                .Select(artist => NormalizeLyricMatchText(artist))
                .Where(artist => artist.Length > 0)
                .ToList();
        }

        /// <summary>
        /// 构建升级搜索查询串，按优先级排序
        /// </summary>
        public static List<string> BuildLyricUpgradeQueries(LyricUpgradeMeta meta)
        {
            var title = meta.Title?.Trim() ?? string.Empty;
            var artist = meta.Artist?.Trim() ?? string.Empty;
            var album = meta.Album?.Trim() ?? string.Empty;

            var queries = new List<string>();
            if (title.Length > 0 && artist.Length > 0)
            {
                queries.Add($"{title} {artist}");
                queries.Add($"{artist} {title}");
            }

            if (title.Length > 0) queries.Add(title);
            if (title.Length > 0 && album.Length > 0) queries.Add($"{title} {album}");
            return queries;
        }

        private static bool BothContains(string a, string b) =>
            a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));

        private static bool IsDurationClose(double? left, double? right, double tolerance = 5000) =>
            left.HasValue && right.HasValue && Math.Abs(left.Value - right.Value) <= tolerance;

        private static List<string> GetVersionMarkers(string value) =>
            VersionMarkers
                .Where(marker => marker.Pattern.IsMatch(value))
                .Select(marker => marker.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

        private static string StripVersionMarkers(string value)
        {
            var stripped = VersionMarkers.Aggregate(value, (result, marker) => marker.Pattern.Replace(result, string.Empty)).Trim();
            return stripped.Length > 0 ? stripped : value;
        }

        private static bool IsSameVersion(string left, string right)
        {
            var leftMarkers = GetVersionMarkers(left);
            var rightMarkers = GetVersionMarkers(right);
            if (leftMarkers.Count == 0 && rightMarkers.Count == 0) return true;
            return string.Join("|", leftMarkers) == string.Join("|", rightMarkers);
        }

        private static (bool Exact, bool Contains) MatchLyricArtists(string? candidateArtist, List<string> trackArtists)
        {
            if (trackArtists.Count == 0) return (false, false);

            var candFull = NormalizeLyricMatchText(candidateArtist);
            var candParts = SplitLyricMatchArtists(candidateArtist);
            if (candFull.Length == 0) return (false, false);

            var exact = trackArtists.Any(artist =>
                candFull == artist || candParts.Any(part => part == artist));
            if (exact) return (true, false);

            var contains = trackArtists.Any(artist =>
                artist.Length >= 2 &&
                (BothContains(candFull, artist) || candParts.Any(part => BothContains(part, artist))));

            return (false, contains);
        }

        /// <summary>
        /// 从候选列表中挑出最匹配的结果。
        /// 硬性条件：标题匹配、歌手命中、时长容差、版本标记一致；打分 ≥ MinScore。
        /// </summary>
        public static LyricSearchCandidate? PickBestLyricCandidate(IEnumerable<LyricSearchCandidate> candidates, LyricUpgradeMeta track)
        {
            var trackTitle = NormalizeLyricMatchText(StripVersionMarkers(track.Title ?? string.Empty));
            var trackArtists = SplitLyricMatchArtists(track.Artist);
            var trackAlbum = NormalizeLyricMatchText(track.Album ?? string.Empty);
            double? trackDuration = track.DurationMs > 0 ? track.DurationMs : null;

            LyricSearchCandidate? best = null;
            var bestScore = MinScore - 1;

            foreach (var candidate in candidates)
            {
                var candTitle = NormalizeLyricMatchText(StripVersionMarkers(candidate.Title));
                var candAlbum = NormalizeLyricMatchText(candidate.Album);

                if (!IsSameVersion(candidate.Title, track.Title ?? string.Empty)) continue;

                // 硬性：title 必须匹配
                var titleExact = candTitle.Length > 0 && candTitle == trackTitle;
                if (!titleExact)
                {
                    if (!BothContains(candTitle, trackTitle)) continue;
                    var longer = Math.Max(candTitle.Length, trackTitle.Length);
                    var shorter = Math.Min(candTitle.Length, trackTitle.Length);
                    if ((double)shorter / longer < NameContainMinRatio) continue;
                }

                double? candidateDuration = candidate.DurationMs > 0 ? candidate.DurationMs : null;

                // 硬性：时长差距超过动态容差直接跳过
                if (trackDuration.HasValue && candidateDuration.HasValue &&
                    Math.Abs(trackDuration.Value - candidateDuration.Value) > Math.Max(8000, trackDuration.Value * 0.04))
                {
                    continue;
                }

                var artist = MatchLyricArtists(candidate.Artist, trackArtists);

                // 硬性：原歌曲有歌手时，候选也必须命中歌手
                if (trackArtists.Count > 0 && !artist.Exact && !artist.Contains) continue;

                // 硬性：title 仅子串命中时，必须有歌手或时长佐证
                if (!titleExact && !artist.Exact && !artist.Contains &&
                    !IsDurationClose(candidateDuration, trackDuration))
                {
                    continue;
                }

                var score = titleExact ? 10 : 4;
                if (artist.Exact) score += 5;
                else if (artist.Contains) score += 2;
                if (trackAlbum.Length > 0 && candAlbum == trackAlbum) score += 2;
                if (IsDurationClose(candidateDuration, trackDuration)) score += 3;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }
    }
}
