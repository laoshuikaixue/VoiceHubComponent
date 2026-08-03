using System;
using System.Collections.Generic;
using System.Linq;
using VoiceHubComponent.Models;

namespace VoiceHubComponent.Cache
{
    /// <summary>
    /// 当日歌词缓存文件结构
    /// </summary>
    public sealed class DailyLyricCache
    {
        public int Version { get; set; }
        public string Date { get; set; } = string.Empty;
        public string ScheduleSignature { get; set; } = string.Empty;
        public bool HasNeteaseCookie { get; set; }
        public string NeteaseCookieFingerprint { get; set; } = string.Empty;
        public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 单首歌曲的缓存条目
    /// </summary>
    public sealed class CacheEntry
    {
        public string CacheKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public double DurationMs { get; set; }
        public string DurationSource { get; set; } = "fallback";
        public string LyricStatus { get; set; } = "no-lyrics";

        /// <summary>
        /// 歌词格式：ttml / qrc / yrc / lrc / none
        /// </summary>
        public string LyricFormat { get; set; } = "none";

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<CachedLyricLine> Lyrics { get; set; } = new();

        public bool IsUsable()
        {
            return DurationMs > 0 && Lyrics.Count > 0 && !string.Equals(LyricStatus, "no-lyrics", StringComparison.OrdinalIgnoreCase);
        }

        public static CacheEntry FromPlayback(string cacheKey, ScheduledSongPlayback playback)
        {
            return new CacheEntry
            {
                CacheKey = cacheKey,
                Title = playback.Item.Song.Title,
                Artist = playback.Item.Song.Artist,
                DurationMs = playback.Duration.TotalMilliseconds,
                DurationSource = playback.DurationSource,
                LyricStatus = playback.LyricStatus,
                LyricFormat = playback.LyricFormat,
                UpdatedAt = DateTime.Now,
                Lyrics = playback.Lyrics.Select(CachedLyricLine.FromLyricLineItem).ToList()
            };
        }
    }

    /// <summary>
    /// 缓存用歌词行
    /// </summary>
    public sealed class CachedLyricLine
    {
        public double StartMs { get; set; }
        public double EndMs { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? Translation { get; set; }
        public string? Romanization { get; set; }
        public bool IsBG { get; set; }
        public bool IsDuet { get; set; }
        public List<CachedLyricWord> Words { get; set; } = new();

        public static CachedLyricLine FromLyricLineItem(LyricLineItem line)
        {
            return new CachedLyricLine
            {
                StartMs = line.Start.TotalMilliseconds,
                EndMs = line.End.TotalMilliseconds,
                Text = line.Text,
                Translation = line.Translation,
                Romanization = line.Romanization,
                IsBG = line.IsBG,
                IsDuet = line.IsDuet,
                Words = line.Words.Select(word => new CachedLyricWord
                {
                    StartMs = word.Start.TotalMilliseconds,
                    EndMs = word.End.TotalMilliseconds,
                    Text = word.Text
                }).ToList()
            };
        }

        public LyricLineItem ToLyricLineItem()
        {
            return new LyricLineItem
            {
                Start = TimeSpan.FromMilliseconds(StartMs),
                End = TimeSpan.FromMilliseconds(EndMs),
                Text = Text,
                Translation = Translation,
                Romanization = Romanization,
                IsBG = IsBG,
                IsDuet = IsDuet,
                Words = (Words ?? new List<CachedLyricWord>()).Select(word => new LyricWordItem
                {
                    Start = TimeSpan.FromMilliseconds(word.StartMs),
                    End = TimeSpan.FromMilliseconds(word.EndMs),
                    Text = word.Text
                }).ToList()
            };
        }
    }

    /// <summary>
    /// 缓存用逐字单词项
    /// </summary>
    public sealed class CachedLyricWord
    {
        public double StartMs { get; set; }
        public double EndMs { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// 排期内单首歌曲的播放计划（运行时结构，不参与序列化）
    /// </summary>
    public sealed class ScheduledSongPlayback
    {
        public ScheduledSongPlayback(SongItem item, TimeSpan duration)
        {
            Item = item;
            Duration = duration;
        }

        public SongItem Item { get; }
        public TimeSpan Duration { get; set; }
        public List<LyricLineItem> Lyrics { get; set; } = new();
        public string DurationSource { get; set; } = "fallback";
        public string LyricStatus { get; set; } = "loading";

        /// <summary>
        /// 歌词格式：ttml / qrc / yrc / lrc / none
        /// </summary>
        public string LyricFormat { get; set; } = "none";

        public bool HasReliableDuration => Duration > TimeSpan.Zero && DurationSource != "unresolved";
    }
}
