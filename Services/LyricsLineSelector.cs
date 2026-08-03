using System;
using System.Collections.Generic;
using System.Linq;
using VoiceHubComponent.Models;

namespace VoiceHubComponent.Services
{
    /// <summary>
    /// 当前应显示的歌词行选择结果
    /// </summary>
    public sealed record LyricsLineSelection(
        int LineIndex,
        LyricLineItem Line,
        bool IsDuetSide,
        LyricLineItem? BackgroundLine = null);

    /// <summary>
    /// 活跃歌词行选择器，支持 TTML 背景声行配对显示
    /// </summary>
    public static class LyricsLineSelector
    {
        private const int MaximumVisibleRows = 2;

        public static IReadOnlyList<LyricsLineSelection> SelectActive(
            IReadOnlyList<LyricLineItem> lines,
            string format,
            double positionMs)
        {
            var active = lines
                .Select((line, index) => new IndexedLine(index, line))
                .Where(item => IsActive(item.Line, positionMs))
                .ToArray();
            if (active.Length == 0)
            {
                var fallback = FindFallback(lines, positionMs);
                return fallback == null
                    ? Array.Empty<LyricsLineSelection>()
                    : new[] { new LyricsLineSelection(fallback.Index, fallback.Line, fallback.Line.IsDuet) };
            }

            if (!string.Equals(format, "ttml", StringComparison.OrdinalIgnoreCase))
            {
                return active
                    .Take(MaximumVisibleRows)
                    .Select(item => new LyricsLineSelection(item.Index, item.Line, item.Line.IsDuet))
                    .ToArray();
            }

            var foreground = active.Where(item => !item.Line.IsBG).ToArray();
            if (foreground.Length == 0)
            {
                return active
                    .Take(MaximumVisibleRows)
                    .Select(item => new LyricsLineSelection(item.Index, item.Line, item.Line.IsDuet))
                    .ToArray();
            }

            var backgrounds = active.Where(item => item.Line.IsBG).ToArray();
            var usedBackgrounds = new HashSet<int>();
            var result = new List<LyricsLineSelection>();
            foreach (var main in foreground.Take(MaximumVisibleRows))
            {
                var background = backgrounds
                    .Where(item => !usedBackgrounds.Contains(item.Index) && Overlaps(main.Line, item.Line))
                    .OrderByDescending(item => Overlap(main.Line, item.Line))
                    .ThenBy(item => item.Index)
                    .FirstOrDefault();
                if (background != null) usedBackgrounds.Add(background.Index);
                result.Add(new LyricsLineSelection(
                    main.Index,
                    main.Line,
                    main.Line.IsDuet,
                    background?.Line));
            }

            return result.OrderBy(item => item.LineIndex).ToArray();
        }

        private static IndexedLine? FindFallback(IReadOnlyList<LyricLineItem> lines, double positionMs)
        {
            IndexedLine? latest = null;
            IndexedLine? foreground = null;
            for (var index = 0; index < lines.Count; index++)
            {
                if (StartMs(lines[index]) > positionMs) break;
                latest = new IndexedLine(index, lines[index]);
                if (!lines[index].IsBG) foreground = latest;
            }

            return foreground ?? latest;
        }

        private static bool IsActive(LyricLineItem line, double positionMs)
        {
            var start = StartMs(line);
            var end = EndMs(line);
            return start <= positionMs && end > start && positionMs < end;
        }

        private static bool Overlaps(LyricLineItem first, LyricLineItem second) =>
            EndMs(first) > StartMs(second) && EndMs(second) > StartMs(first);

        private static double Overlap(LyricLineItem first, LyricLineItem second) =>
            Math.Max(0, Math.Min(EndMs(first), EndMs(second)) - Math.Max(StartMs(first), StartMs(second)));

        private static double StartMs(LyricLineItem line) => line.Start.TotalMilliseconds;
        private static double EndMs(LyricLineItem line) => line.End.TotalMilliseconds;

        private sealed record IndexedLine(int Index, LyricLineItem Line);
    }
}
