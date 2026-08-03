using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VoiceHubComponent.Models;

namespace VoiceHubComponent.Services
{
    /// <summary>
    /// 歌词内容匹配判定结果
    /// </summary>
    public sealed record LyricMatchDecision(string Status, string Reason, LyricMatchMetrics Metrics);

    /// <summary>
    /// 歌词内容匹配指标
    /// </summary>
    public sealed class LyricMatchMetrics
    {
        public double? DurationDiffMs { get; set; }
        public double? DurationLimitMs { get; set; }
        public double? ContentScore { get; set; }
        public int? AnchorCount { get; set; }
        public double? TimelineCoverage { get; set; }
        public double? WithinTimeRatio { get; set; }
        public double? P90DiffMs { get; set; }
        public double? TimelineDriftMs { get; set; }
        public double? TimelineRatio { get; set; }

        public override string ToString()
        {
            var parts = new List<string>();
            if (ContentScore.HasValue) parts.Add($"contentScore={ContentScore.Value:F3}");
            if (AnchorCount.HasValue) parts.Add($"anchors={AnchorCount.Value}");
            if (TimelineCoverage.HasValue) parts.Add($"coverage={TimelineCoverage.Value:F2}");
            if (WithinTimeRatio.HasValue) parts.Add($"withinRatio={WithinTimeRatio.Value:F2}");
            if (P90DiffMs.HasValue) parts.Add($"p90={P90DiffMs.Value:F0}ms");
            if (TimelineDriftMs.HasValue) parts.Add($"drift={TimelineDriftMs.Value:F0}ms");
            if (TimelineRatio.HasValue) parts.Add($"ratio={TimelineRatio.Value:F3}");
            if (DurationDiffMs.HasValue) parts.Add($"durationDiff={DurationDiffMs.Value:F0}ms");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// 高阶歌词与基准歌词的同版本一致性校验，
    /// 对照主项目 app/utils/lyric/lyricMatchQuality.ts 实现。
    /// </summary>
    public static class LyricMatchQuality
    {
        private const double MinContentScore = 0.82;
        private const double MaxAnchorDiffMs = 3000;
        private const double MaxP90DiffMs = 2500;
        private const double MaxTimelineDriftMs = 4000;
        private const double MinTimelineRatio = 0.985;
        private const double MaxTimelineRatio = 1.015;
        private const double MinLineSimilarity = 0.68;

        private static readonly Regex MetadataLineRegex = new(
            @"^(?:作词|作曲|编曲|制作人|混音|母带|录音|词|曲|composer|lyricist|arranger|producer|mixed by)\s*[:：]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StripSymbolsRegex = new(@"[\p{P}\p{S}\s]+", RegexOptions.Compiled);

        private sealed class ComparableLine
        {
            public string Text { get; init; } = string.Empty;
            public double StartTime { get; init; }
            public double EndTime { get; init; }
        }

        private sealed record MatchAnchor(double ReferenceStart, double CandidateStart);

        /// <summary>
        /// 检测高阶歌词是否与基准歌词属于同一录音版本
        /// </summary>
        public static LyricMatchDecision EvaluateLyricDataMatch(
            LyricPayload referenceData,
            LyricPayload candidateData,
            double? referenceDurationMs,
            double? candidateDurationMs)
        {
            var metrics = new LyricMatchMetrics();
            if (referenceDurationMs > 0 && candidateDurationMs > 0)
            {
                metrics.DurationDiffMs = Math.Abs(referenceDurationMs.Value - candidateDurationMs.Value);
                metrics.DurationLimitMs = Math.Max(8000, referenceDurationMs.Value * 0.04);
                if (metrics.DurationDiffMs > metrics.DurationLimitMs)
                {
                    return new LyricMatchDecision("rejected", "duration_mismatch", metrics);
                }
            }

            var reference = ParseComparableLyrics(referenceData);
            var candidate = ParseComparableLyrics(candidateData);
            if (reference.Count < 3 || candidate.Count < 3)
            {
                return new LyricMatchDecision("uncertain", "insufficient_lines", metrics);
            }

            var aligned = AlignLines(reference, candidate);
            var referenceChars = reference.Sum(line => line.Text.Length);
            var candidateChars = candidate.Sum(line => line.Text.Length);
            metrics.ContentScore = 2 * aligned.Score / (referenceChars + candidateChars);
            if (metrics.ContentScore < MinContentScore)
            {
                return new LyricMatchDecision("rejected", "content_mismatch", metrics);
            }

            var anchors = aligned.Anchors
                .Where(anchor => double.IsFinite(anchor.ReferenceStart) && double.IsFinite(anchor.CandidateStart))
                .ToList();
            metrics.AnchorCount = anchors.Count;
            var lastReference = reference[^1];
            var timelineDuration = referenceDurationMs > 0
                ? referenceDurationMs.Value
                : lastReference.EndTime > 0 ? lastReference.EndTime : lastReference.StartTime;
            var minAnchors = timelineDuration > 0 && timelineDuration < 60000 ? 3 : 6;
            if (anchors.Count < minAnchors)
            {
                return new LyricMatchDecision("uncertain", "insufficient_anchors", metrics);
            }

            var first = anchors[0];
            var last = anchors[^1];
            var referenceSpan = last.ReferenceStart - first.ReferenceStart;
            var candidateSpan = last.CandidateStart - first.CandidateStart;
            metrics.TimelineCoverage = timelineDuration > 0 ? referenceSpan / timelineDuration : 0;
            var minimumCoverage = timelineDuration > 0 && timelineDuration < 60000 ? 0.35 : 0.5;
            if (metrics.TimelineCoverage < minimumCoverage)
            {
                return new LyricMatchDecision("uncertain", "insufficient_timeline_coverage", metrics);
            }

            var differences = anchors
                .Select(anchor => Math.Abs(anchor.CandidateStart - anchor.ReferenceStart))
                .OrderBy(value => value)
                .ToList();
            metrics.WithinTimeRatio = (double)differences.Count(difference => difference <= MaxAnchorDiffMs) / differences.Count;
            metrics.P90DiffMs = differences[Math.Max(0, (int)Math.Ceiling(differences.Count * 0.9) - 1)];
            var firstOffset = first.CandidateStart - first.ReferenceStart;
            var lastOffset = last.CandidateStart - last.ReferenceStart;
            metrics.TimelineDriftMs = Math.Abs(lastOffset - firstOffset);
            metrics.TimelineRatio = referenceSpan > 0 ? candidateSpan / referenceSpan : 0;

            if (metrics.WithinTimeRatio < 0.8 ||
                metrics.P90DiffMs > MaxP90DiffMs ||
                metrics.TimelineDriftMs > MaxTimelineDriftMs ||
                metrics.TimelineRatio < MinTimelineRatio ||
                metrics.TimelineRatio > MaxTimelineRatio)
            {
                return new LyricMatchDecision("rejected", "timeline_mismatch", metrics);
            }

            return new LyricMatchDecision("accepted", "matched", metrics);
        }

        /// <summary>
        /// 归一化歌词正文，仅用于跨来源一致性比较
        /// </summary>
        private static string NormalizeText(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            return StripSymbolsRegex.Replace(normalized, string.Empty);
        }

        private static List<ComparableLine> ToComparableLines(IEnumerable<LyricLineItem> lines)
        {
            var result = new List<ComparableLine>();
            foreach (var line in lines)
            {
                if (line.IsBG) continue;
                var raw = (line.Words.Count > 0
                    ? string.Concat(line.Words.Select(word => word.Text))
                    : line.Text).Trim();
                if (raw.Length == 0 || MetadataLineRegex.IsMatch(raw)) continue;
                var text = NormalizeText(raw);
                if (text.Length < 2) continue;
                result.Add(new ComparableLine
                {
                    Text = text,
                    StartTime = line.Start.TotalMilliseconds,
                    EndTime = line.End.TotalMilliseconds
                });
            }

            return result;
        }

        /// <summary>
        /// 按格式优先级解析歌词正文为可比较行
        /// </summary>
        private static List<ComparableLine> ParseComparableLyrics(LyricPayload data)
        {
            if (!string.IsNullOrWhiteSpace(data.Ttml))
            {
                try
                {
                    var lines = ToComparableLines(LyricParser.ParseTtml(data.Ttml));
                    if (lines.Count > 0) return lines;
                }
                catch
                {
                    // 继续尝试逐字或普通歌词
                }
            }

            // QRC/YRC 逐字内容统一走 yrc 槽位比较
            var wordContent = data.Yrc ?? data.Qrc;
            if (!string.IsNullOrWhiteSpace(wordContent))
            {
                try
                {
                    var parsed = LyricParser.ParseBestLyrics(new LyricPayload(null, null, wordContent, null));
                    var lines = ToComparableLines(parsed.Lines);
                    if (lines.Count > 0) return lines;
                }
                catch
                {
                    // 继续回退到通用 LRC
                }
            }

            if (!string.IsNullOrWhiteSpace(data.Lrc))
            {
                try
                {
                    return ToComparableLines(LyricParser.ParseSmartLrc(data.Lrc));
                }
                catch
                {
                    return new List<ComparableLine>();
                }
            }

            return new List<ComparableLine>();
        }

        /// <summary>
        /// 归一化编辑相似度
        /// </summary>
        private static double TextSimilarity(string left, string right)
        {
            if (left == right) return 1;
            if (left.Length == 0 || right.Length == 0) return 0;
            if (left.Length < right.Length) return TextSimilarity(right, left);

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var j = 0; j <= right.Length; j++) previous[j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    current[j] = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                }

                Array.Copy(current, previous, current.Length);
            }

            return 1 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
        }

        private static int SpanLength(List<ComparableLine> lines, int start, int count) =>
            lines[start].Text.Length + (count == 2 ? lines[start + 1].Text.Length : 0);

        private static string JoinSpan(List<ComparableLine> lines, int start, int count) =>
            count == 1 ? lines[start].Text : lines[start].Text + lines[start + 1].Text;

        private sealed class AlignResult
        {
            public double Score { get; init; }
            public List<MatchAnchor> Anchors { get; init; } = new();
        }

        /// <summary>
        /// 有序对齐歌词行，允许相邻两行互相合并（DP）
        /// </summary>
        private static AlignResult AlignLines(List<ComparableLine> reference, List<ComparableLine> candidate)
        {
            var width = candidate.Count + 1;
            var size = (reference.Count + 1) * width;
            var scores = new double[size];
            var prevReference = new byte[size];
            var prevCandidate = new byte[size];

            void Update(int fromReference, int fromCandidate, int referenceCount, int candidateCount, double addedScore)
            {
                var nextReference = fromReference + referenceCount;
                var nextCandidate = fromCandidate + candidateCount;
                var from = fromReference * width + fromCandidate;
                var next = nextReference * width + nextCandidate;
                var score = scores[from] + addedScore;
                if (score <= scores[next]) return;
                scores[next] = score;
                prevReference[next] = (byte)referenceCount;
                prevCandidate[next] = (byte)candidateCount;
            }

            for (var i = 0; i <= reference.Count; i++)
            {
                for (var j = 0; j <= candidate.Count; j++)
                {
                    if (i < reference.Count) Update(i, j, 1, 0, 0);
                    if (j < candidate.Count) Update(i, j, 0, 1, 0);
                    for (var referenceCount = 1; referenceCount <= 2; referenceCount++)
                    {
                        if (i + referenceCount > reference.Count) break;
                        for (var candidateCount = 1; candidateCount <= 2; candidateCount++)
                        {
                            if (j + candidateCount > candidate.Count) break;
                            var referenceLength = SpanLength(reference, i, referenceCount);
                            var candidateLength = SpanLength(candidate, j, candidateCount);
                            if ((double)Math.Min(referenceLength, candidateLength) /
                                Math.Max(referenceLength, candidateLength) < MinLineSimilarity)
                            {
                                continue;
                            }

                            var referenceText = JoinSpan(reference, i, referenceCount);
                            var candidateText = JoinSpan(candidate, j, candidateCount);
                            var similarity = TextSimilarity(referenceText, candidateText);
                            if (similarity < MinLineSimilarity) continue;
                            Update(i, j, referenceCount, candidateCount,
                                Math.Min(referenceText.Length, candidateText.Length) * similarity);
                        }
                    }
                }
            }

            var anchors = new List<MatchAnchor>();
            var ii = reference.Count;
            var jj = candidate.Count;
            while (ii > 0 || jj > 0)
            {
                var index = ii * width + jj;
                var referenceCount = prevReference[index];
                var candidateCount = prevCandidate[index];
                if (referenceCount == 0 && candidateCount == 0) break;
                var previousI = ii - referenceCount;
                var previousJ = jj - candidateCount;
                if (referenceCount > 0 && candidateCount > 0)
                {
                    anchors.Add(new MatchAnchor(
                        reference[previousI].StartTime,
                        candidate[previousJ].StartTime));
                }

                ii = previousI;
                jj = previousJ;
            }

            anchors.Reverse();
            return new AlignResult { Score = scores[size - 1], Anchors = anchors };
        }
    }
}
