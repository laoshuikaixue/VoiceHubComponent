using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using VoiceHubComponent.Models;
using MaterialDesignThemes.Wpf;

namespace VoiceHubComponent
{
    [ComponentInfo(
        "A1B2C3D4-E5F6-7890-ABCD-EF1234567890",
        "VoiceHub广播站排期",
        PackIconKind.Radio,
        "展示VoiceHub广播站当日排期歌曲，按播放顺序显示歌曲信息。"
    )]
    public partial class VoiceHubControl : ComponentBase
    {
        private readonly HttpClient _httpClient = new HttpClient();

        public VoiceHubControl()
        {
            InitializeComponent();
            LoadVoiceHubDataAsync();
        }

        private async void LoadVoiceHubDataAsync()
        {
            try
            {
                VoiceHubText.Text = "正在加载...";
                
                var jsonResponse = await _httpClient.GetStringAsync("https://voicehub.lao-shui.top/api/songs/public");
                var songItems = JsonSerializer.Deserialize<List<SongItem>>(jsonResponse);

                if (songItems == null || !songItems.Any())
                {
                    Dispatcher.Invoke(() => VoiceHubText.Text = "暂无排期数据");
                    return;
                }

                // 过滤掉无效日期的项目
                var validItems = songItems.Where(s => s.GetPlayDate() != DateTime.MinValue).ToList();
                
                if (!validItems.Any())
                {
                    Dispatcher.Invoke(() => VoiceHubText.Text = "暂无有效排期数据");
                    return;
                }

                // 找到今天或最近未来的排期
                var today = DateTime.Today;
                var todaySchedule = validItems.Where(s => s.GetPlayDate() == today).OrderBy(s => s.Sequence).ToList();
                
                List<SongItem> displayItems;
                DateTime actualDate;
                
                if (todaySchedule.Any())
                {
                    displayItems = todaySchedule;
                    actualDate = today;
                }
                else
                {
                    // 找最近的未来排期
                    var futureSchedule = validItems
                        .Where(s => s.GetPlayDate() > today)
                        .GroupBy(s => s.GetPlayDate())
                        .OrderBy(g => g.Key)
                        .FirstOrDefault();
                    
                    if (futureSchedule != null)
                    {
                        displayItems = futureSchedule.OrderBy(s => s.Sequence).ToList();
                        actualDate = futureSchedule.Key;
                    }
                    else
                    {
                        Dispatcher.Invoke(() => VoiceHubText.Text = "暂无排期数据");
                        return;
                    }
                }

                // 验证显示项目的日期一致性
                var inconsistentItems = displayItems.Where(item => item.GetPlayDate() != actualDate).ToList();
                if (inconsistentItems.Any())
                {
                    // 记录日期不一致的问题，但继续显示一致的项目
                    displayItems = displayItems.Where(item => item.GetPlayDate() == actualDate).ToList();
                }

                if (!displayItems.Any())
                {
                    Dispatcher.Invoke(() => VoiceHubText.Text = "排期数据日期不一致");
                    return;
                }

                // 格式化显示内容，使用实际的内容日期
                var displayText = FormatScheduleDisplay(displayItems, actualDate.ToString("yyyy/MM/dd"));
                Dispatcher.Invoke(() => VoiceHubText.Text = displayText);
            }
            catch (HttpRequestException)
            {
                Dispatcher.Invoke(() => VoiceHubText.Text = "网络连接失败");
            }
            catch (JsonException)
            {
                Dispatcher.Invoke(() => VoiceHubText.Text = "数据解析失败");
            }
            catch (Exception)
            {
                Dispatcher.Invoke(() => VoiceHubText.Text = "加载失败");
            }
        }

        private string FormatScheduleDisplay(List<SongItem> items, string dateInfo)
        {
            var sb = new StringBuilder();
            sb.Append($"广播站排期 | {dateInfo}: ");

            var songInfos = new List<string>();
            foreach (var item in items) // 显示所有歌曲
            {
                var song = item.Song;
                songInfos.Add($"#{item.Sequence:D2} {song.Artist} - {song.Title} - {song.Requester}");
            }

            sb.Append(string.Join(" | ", songInfos));

            return sb.ToString();
        }
    }
}