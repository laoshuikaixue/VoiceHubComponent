using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VoiceHubComponent.Models
{
    /// <summary>
    /// 歌曲信息模型
    /// </summary>
    public class Song
    {
        /// <summary>
        /// 歌曲标题
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 艺术家/歌手
        /// </summary>
        [JsonPropertyName("artist")]
        public string Artist { get; set; } = string.Empty;

        /// <summary>
        /// 点歌人
        /// </summary>
        [JsonPropertyName("requester")]
        public string Requester { get; set; } = string.Empty;

        /// <summary>
        /// 投票数/热度
        /// </summary>
        [JsonPropertyName("voteCount")]
        public int VoteCount { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("musicPlatform")]
        public string? MusicPlatform { get; set; }

        [JsonPropertyName("musicId")]
        public string? MusicId { get; set; }

        [JsonPropertyName("playUrl")]
        public string? PlayUrl { get; set; }
    }

    /// <summary>
    /// 排期歌曲项目模型
    /// </summary>
    public class SongItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// 播放日期
        /// </summary>
        [JsonPropertyName("playDate")]
        public string PlayDate { get; set; } = string.Empty;

        /// <summary>
        /// 播放序号
        /// </summary>
        [JsonPropertyName("sequence")]
        public int Sequence { get; set; }

        /// <summary>
        /// 歌曲信息
        /// </summary>
        [JsonPropertyName("song")]
        public Song Song { get; set; } = new Song();

        [JsonPropertyName("playTime")]
        public PlayTimeInfo? PlayTime { get; set; }

        /// <summary>
        /// 获取播放日期的DateTime对象
        /// </summary>
        public DateTime GetPlayDateTime()
        {
            if (string.IsNullOrWhiteSpace(PlayDate))
            {
                return DateTime.MinValue;
            }

            // 日期格式固定为 yyyy-MM-dd，直接解析
            if (DateTime.TryParseExact(PlayDate, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime result))
            {
                return result;
            }

            return DateTime.MinValue;
        }

        /// <summary>
        /// 获取播放日期（仅日期部分）
        /// </summary>
        public DateTime GetPlayDate()
        {
            return GetPlayDateTime().Date;
        }
    }

    public class PlayTimeInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("startTime")]
        public string? StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public string? EndTime { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }

    public class LyricLineItem
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? Translation { get; set; }

        public LyricLineItem Clone()
        {
            return new LyricLineItem
            {
                Start = Start,
                End = End,
                Text = Text,
                Translation = Translation
            };
        }
    }

    /// <summary>
    /// 组件状态枚举
    /// </summary>
    public enum ComponentState
    {
        /// <summary>
        /// 加载中
        /// </summary>
        Loading,
        /// <summary>
        /// 正常显示
        /// </summary>
        Normal,
        /// <summary>
        /// 网络错误
        /// </summary>
        NetworkError,
        /// <summary>
        /// 暂无排期
        /// </summary>
        NoSchedule
    }

    /// <summary>
    /// 显示数据模型
    /// </summary>
    public class DisplayData
    {
        /// <summary>
        /// 组件状态
        /// </summary>
        public ComponentState State { get; set; }

        /// <summary>
        /// 歌曲列表
        /// </summary>
        public List<SongItem> Songs { get; set; } = new List<SongItem>();

        /// <summary>
        /// 显示日期
        /// </summary>
        public DateTime? DisplayDate { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
