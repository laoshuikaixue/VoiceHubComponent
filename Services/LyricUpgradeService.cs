using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VoiceHubComponent.Models;

namespace VoiceHubComponent.Services
{
    /// <summary>
    /// 歌词升级结果
    /// </summary>
    public sealed record LyricUpgradeResult(LyricPayload Payload, List<LyricLineItem> Lines, string Format);

    /// <summary>
    /// 跨平台歌词升级服务：当本侧歌词不是最高阶格式时，
    /// 搜索对侧平台（netease ↔ tencent，migu → netease）尝试获取 TTML/逐字歌词。
    /// 对照主项目 useMusicSources.ts 的 tryUpgradeLyric 实现。
    /// </summary>
    public sealed class LyricUpgradeService
    {
        private readonly HttpClient _httpClient;
        private readonly Func<Uri?> _originProvider;
        private readonly Func<Uri, CancellationToken, Task<string>> _voiceHubFetcher;
        private readonly Func<string, string, CancellationToken, Task<LyricPayload>> _fetchCandidateLyrics;
        private readonly ILogger? _logger;

        public LyricUpgradeService(
            HttpClient httpClient,
            Func<Uri?> originProvider,
            Func<Uri, CancellationToken, Task<string>> voiceHubFetcher,
            Func<string, string, CancellationToken, Task<LyricPayload>> fetchCandidateLyrics,
            ILogger? logger)
        {
            _httpClient = httpClient;
            _originProvider = originProvider;
            _voiceHubFetcher = voiceHubFetcher;
            _fetchCandidateLyrics = fetchCandidateLyrics;
            _logger = logger;
        }

        /// <summary>
        /// 尝试升级歌词。返回 null 表示未升级（保持原歌词）。
        /// </summary>
        public async Task<LyricUpgradeResult?> TryUpgradeAsync(
            string platform,
            LyricPayload current,
            List<LyricLineItem> currentLines,
            LyricUpgradeMeta meta,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(meta.Title) || string.IsNullOrWhiteSpace(meta.Artist)) return null;
            if (!string.IsNullOrWhiteSpace(current.Ttml)) return null;

            var currentRank = LyricParser.GetPayloadRank(current);
            var targetPlatform = platform switch
            {
                "netease" => "tencent",
                "tencent" or "qq" => "netease",
                "migu" => "netease",
                _ => null
            };
            if (targetPlatform == null) return null;

            var queries = LyricMatcher.BuildLyricUpgradeQueries(meta);
            if (queries.Count == 0) return null;

            var matchedCandidates = new List<LyricSearchCandidate>();
            foreach (var keywords in queries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var searchResult = await SearchAsync(targetPlatform, keywords, token);
                    if (searchResult.Count == 0)
                    {
                        continue;
                    }

                    var best = LyricMatcher.PickBestLyricCandidate(searchResult, meta);
                    if (best != null)
                    {
                        matchedCandidates = searchResult;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "VoiceHub 歌词升级跨平台搜索失败：platform={Platform}, keywords={Keywords}",
                        targetPlatform, keywords);
                }
            }

            if (matchedCandidates.Count == 0) return null;

            var rejectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var best = LyricMatcher.PickBestLyricCandidate(
                    matchedCandidates.Where(candidate =>
                        !string.IsNullOrEmpty(candidate.MusicId) && !rejectedIds.Contains(candidate.MusicId)),
                    meta);
                if (best == null || string.IsNullOrEmpty(best.MusicId)) return null;
                rejectedIds.Add(best.MusicId);

                LyricPayload upgraded;
                try
                {
                    upgraded = await _fetchCandidateLyrics(best.Platform, best.MusicId, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "VoiceHub 歌词升级候选获取失败：{Platform}:{MusicId}", best.Platform, best.MusicId);
                    continue;
                }

                var hasBaseline = !string.IsNullOrWhiteSpace(current.Lrc) ||
                                  !string.IsNullOrWhiteSpace(current.Yrc) ||
                                  !string.IsNullOrWhiteSpace(current.Qrc);

                // 优先接受 TTML
                if (!string.IsNullOrWhiteSpace(upgraded.Ttml) &&
                    LyricParser.RankTtml < currentRank &&
                    TryAccept(current, upgraded, meta, best, hasBaseline, "ttml"))
                {
                    return BuildTtmlResult(current, upgraded);
                }

                // 其次接受逐字（QRC/YRC）
                var upgradedWord = !string.IsNullOrWhiteSpace(upgraded.Qrc) ? upgraded.Qrc : upgraded.Yrc;
                if (!string.IsNullOrWhiteSpace(upgradedWord) &&
                    LyricParser.RankWord < currentRank &&
                    TryAccept(current, upgraded, meta, best, hasBaseline, "yrc"))
                {
                    return BuildWordResult(current, upgraded);
                }
            }

            return null;
        }

        private bool TryAccept(
            LyricPayload current,
            LyricPayload candidate,
            LyricUpgradeMeta meta,
            LyricSearchCandidate best,
            bool hasBaseline,
            string format)
        {
            // 无基准歌词时无法做内容匹配，仅校验时长差异
            if (!hasBaseline)
            {
                if (meta.DurationMs > 0 && best.DurationMs > 0)
                {
                    var durationDiffMs = Math.Abs(meta.DurationMs - best.DurationMs);
                    if (durationDiffMs > Math.Max(8000, meta.DurationMs * 0.04))
                    {
                        _logger?.LogInformation(
                            "VoiceHub 歌词升级 {Platform}:{MusicId} {Format} rejected/duration_mismatch",
                            best.Platform, best.MusicId, format);
                        return false;
                    }
                }

                _logger?.LogInformation(
                    "VoiceHub 歌词升级 {Platform}:{MusicId} {Format} accepted/no_baseline",
                    best.Platform, best.MusicId, format);
                return true;
            }

            var referenceData = new LyricPayload(
                current.Lrc,
                null,
                string.IsNullOrWhiteSpace(current.Yrc) ? current.Qrc : current.Yrc,
                current.Ttml);
            var candidateData = new LyricPayload(
                candidate.Lrc,
                null,
                string.IsNullOrWhiteSpace(candidate.Yrc) ? candidate.Qrc : candidate.Yrc,
                candidate.Ttml);
            var decision = LyricMatchQuality.EvaluateLyricDataMatch(
                referenceData,
                candidateData,
                meta.DurationMs > 0 ? meta.DurationMs : null,
                best.DurationMs > 0 ? best.DurationMs : null);
            _logger?.LogInformation(
                "VoiceHub 歌词升级 {Platform}:{MusicId} {Format} {Status}/{Reason} {Metrics}",
                best.Platform, best.MusicId, format, decision.Status, decision.Reason, decision.Metrics);
            return decision.Status == "accepted";
        }

        private static LyricUpgradeResult BuildTtmlResult(LyricPayload current, LyricPayload upgraded)
        {
            var payload = new LyricPayload(
                current.Lrc,
                string.IsNullOrWhiteSpace(current.Translation) ? upgraded.Translation : current.Translation,
                current.Yrc,
                upgraded.Ttml,
                current.Qrc,
                current.Roma);
            var lines = LyricParser.ParseTtml(payload.Ttml);
            if (!string.IsNullOrWhiteSpace(payload.Translation))
            {
                LyricParser.AlignTranslations(lines, LyricParser.ParseSmartLrc(payload.Translation));
            }

            return new LyricUpgradeResult(payload, lines, "ttml");
        }

        private static LyricUpgradeResult BuildWordResult(LyricPayload current, LyricPayload upgraded)
        {
            var payload = new LyricPayload(
                current.Lrc,
                string.IsNullOrWhiteSpace(current.Translation) ? upgraded.Translation : current.Translation,
                upgraded.Yrc,
                current.Ttml,
                upgraded.Qrc,
                string.IsNullOrWhiteSpace(current.Roma) ? upgraded.Roma : current.Roma);
            var parsed = LyricParser.ParseBestLyrics(new LyricPayload(null, null, payload.Yrc, null, payload.Qrc));
            var lines = parsed.Lines;
            if (!string.IsNullOrWhiteSpace(payload.Translation))
            {
                LyricParser.AlignTranslations(lines, LyricParser.ParseSmartLrc(payload.Translation));
            }

            if (!string.IsNullOrWhiteSpace(payload.Roma))
            {
                LyricParser.AlignRomanizations(lines, LyricParser.ParseSmartLrc(payload.Roma));
            }

            return new LyricUpgradeResult(payload, lines, parsed.Format == "lrc" ? "qrc" : parsed.Format);
        }

        /// <summary>
        /// 通过 VoiceHub 服务端搜索接口检索对侧平台候选
        /// </summary>
        private async Task<List<LyricSearchCandidate>> SearchAsync(string targetPlatform, string keywords, CancellationToken token)
        {
            var origin = _originProvider();
            if (origin == null)
            {
                return new List<LyricSearchCandidate>();
            }

            var endpoint = targetPlatform == "tencent" ? "tx" : "wy";
            var url = new Uri(origin, $"/api/native-api/search/{endpoint}?str={Uri.EscapeDataString(keywords)}&limit=10");
            var json = await _voiceHubFetcher(url, token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
            {
                return new List<LyricSearchCandidate>();
            }

            var candidates = new List<LyricSearchCandidate>();
            foreach (var item in listElement.EnumerateArray())
            {
                var musicId = GetString(item, "songmid");
                if (string.IsNullOrWhiteSpace(musicId))
                {
                    musicId = GetString(item, "id");
                }

                if (string.IsNullOrWhiteSpace(musicId))
                {
                    continue;
                }

                var durationSeconds = GetNumber(item, "duration");
                candidates.Add(new LyricSearchCandidate
                {
                    Title = GetString(item, "name") ?? string.Empty,
                    Artist = GetString(item, "singer") ?? string.Empty,
                    Album = GetString(item, "albumName") ?? string.Empty,
                    DurationMs = durationSeconds > 0 ? durationSeconds * 1000 : 0,
                    Platform = targetPlatform,
                    MusicId = musicId
                });
            }

            return candidates;
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static double GetNumber(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return 0;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var number) => number,
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
                _ => 0
            };
        }
    }
}
