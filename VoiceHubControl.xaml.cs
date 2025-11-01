using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
        private readonly VoiceHubSettings _settings;
        private CancellationTokenSource? _cancellationTokenSource;
        private ComponentState _currentState = ComponentState.Loading;

        public VoiceHubControl(VoiceHubSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            
            // 设置HTTP客户端超时
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            // 显示加载状态
            SetState(ComponentState.Loading);
            
            // 使用Task.Run确保异步加载在后台线程执行，完全不阻塞UI线程
            Task.Run(async () =>
            {
                try
                {
                    // 添加小延迟确保UI完全初始化
                    await Task.Delay(100);
                    await LoadVoiceHubDataAsync();
                }
                catch (Exception)
                {
                    // 确保异常不会导致应用崩溃
                    Dispatcher.Invoke(() => SetState(ComponentState.NetworkError, "广播站排期获取失败"));
                }
            });
        }

        private async Task LoadVoiceHubDataAsync()
        {
            // 取消之前的请求
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                // 设置加载状态
                SetState(ComponentState.Loading);
                
                // 使用配置的API地址
                var apiUrl = !string.IsNullOrEmpty(_settings.ApiUrl) 
                    ? _settings.ApiUrl 
                    : "https://voicehub.lao-shui.top/api/songs/public";
                
                var jsonResponse = await _httpClient.GetStringAsync(apiUrl, _cancellationTokenSource.Token);
                var songItems = JsonSerializer.Deserialize<List<SongItem>>(jsonResponse);

                if (songItems == null || !songItems.Any())
                {
                    SetState(ComponentState.NoSchedule, "暂无排期数据");
                    return;
                }

                // 过滤掉无效日期的项目
                var validItems = songItems.Where(s => s.GetPlayDate() != DateTime.MinValue).ToList();
                
                if (!validItems.Any())
                {
                    SetState(ComponentState.NoSchedule, "暂无有效排期数据");
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
                        SetState(ComponentState.NoSchedule, "暂无排期数据");
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
                    SetState(ComponentState.NoSchedule, "排期数据日期不一致");
                    return;
                }

                // 格式化显示内容，使用实际的内容日期
                var displayText = FormatScheduleDisplay(displayItems, actualDate.ToString("yyyy/MM/dd"));
                SetState(ComponentState.Normal, displayText);
            }
            catch (TaskCanceledException)
            {
                SetState(ComponentState.NetworkError, "广播站排期获取失败");
            }
            catch (OperationCanceledException)
            {
                // 请求被取消，不需要处理
            }
            catch (HttpRequestException)
            {
                SetState(ComponentState.NetworkError, "广播站排期获取失败");
            }
            catch (JsonException)
            {
                SetState(ComponentState.NetworkError, "广播站排期获取失败");
            }
            catch (Exception)
            {
                SetState(ComponentState.NetworkError, "广播站排期获取失败");
            }
        }

        private void SetState(ComponentState state, string? message = null)
        {
            _currentState = state;
            
            // 确保在UI线程上更新界面
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetState(state, message));
                return;
            }

            // 隐藏所有面板
            LoadingPanel.Visibility = Visibility.Collapsed;
            VoiceHubText.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;

            switch (state)
            {
                case ComponentState.Loading:
                    LoadingPanel.Visibility = Visibility.Visible;
                    break;
                    
                case ComponentState.Normal:
                    VoiceHubText.Text = message ?? "";
                    VoiceHubText.Visibility = Visibility.Visible;
                    break;
                    
                case ComponentState.NetworkError:
                    ErrorText.Text = message ?? "网络错误";
                    ErrorPanel.Visibility = Visibility.Visible;
                    break;
                    
                case ComponentState.NoSchedule:
                    ErrorText.Text = message ?? "暂无排期";
                    ErrorPanel.Visibility = Visibility.Visible;
                    break;
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

        /// <summary>
        /// 刷新数据
        /// </summary>
        public async Task RefreshAsync()
        {
            await LoadVoiceHubDataAsync();
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            // 取消正在进行的请求
            _cancellationTokenSource?.Cancel();
            _httpClient?.Dispose();
        }
    }
}