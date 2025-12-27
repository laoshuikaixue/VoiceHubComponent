using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using VoiceHubComponent.Models;

namespace VoiceHubComponent
{
    [ComponentInfo(
        "A1B2C3D4-E5F6-7890-ABCD-EF1234567890",
        "VoiceHub广播站排期",
        "\ue42b",
        "展示VoiceHub广播站当日排期歌曲，按播放顺序显示歌曲信息。"
    )]
    public partial class VoiceHubControl : ComponentBase<VoiceHubSettings>
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private CancellationTokenSource? _cancellationTokenSource;
        private ComponentState _currentState = ComponentState.Loading;
        private readonly DispatcherTimer _refreshTimer;
        
        // UI 状态属性
        public static readonly DirectProperty<VoiceHubControl, bool> IsLoadingProperty =
            AvaloniaProperty.RegisterDirect<VoiceHubControl, bool>(nameof(IsLoading), o => o.IsLoading);
        private bool _isLoading = true;
        public bool IsLoading { get => _isLoading; private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value); }

        public static readonly DirectProperty<VoiceHubControl, bool> IsDataLoadedProperty =
            AvaloniaProperty.RegisterDirect<VoiceHubControl, bool>(nameof(IsDataLoaded), o => o.IsDataLoaded);
        private bool _isDataLoaded = false;
        public bool IsDataLoaded { get => _isDataLoaded; private set => SetAndRaise(IsDataLoadedProperty, ref _isDataLoaded, value); }

        public static readonly DirectProperty<VoiceHubControl, bool> IsErrorProperty =
            AvaloniaProperty.RegisterDirect<VoiceHubControl, bool>(nameof(IsError), o => o.IsError);
        private bool _isError = false;
        public bool IsError { get => _isError; private set => SetAndRaise(IsErrorProperty, ref _isError, value); }

        public static readonly DirectProperty<VoiceHubControl, string> ContentTextProperty =
            AvaloniaProperty.RegisterDirect<VoiceHubControl, string>(nameof(ContentText), o => o.ContentText);
        private string _contentText = "";
        public string ContentText { get => _contentText; private set => SetAndRaise(ContentTextProperty, ref _contentText, value); }

        public static readonly DirectProperty<VoiceHubControl, string> MessageTextProperty =
            AvaloniaProperty.RegisterDirect<VoiceHubControl, string>(nameof(MessageText), o => o.MessageText);
        private string _messageText = "";
        public string MessageText { get => _messageText; private set => SetAndRaise(MessageTextProperty, ref _messageText, value); }

        // 重试逻辑相关字段
        private int _retryCount = 0;
        private const int MaxRetryCount = 3;
        private DateTime _lastFailureTime = DateTime.MinValue;
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(10);

        // 加载守护：超时自动重试
        private readonly DispatcherTimer _loadingGuardTimer;
        private readonly TimeSpan _loadingTimeout = TimeSpan.FromSeconds(15);

        public VoiceHubControl()
        {
            InitializeComponent();
            
            // 设置HTTP客户端超时
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            // 初始化定时器，正常情况下1小时刷新一次，失败后1分钟检查一次
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1) // 默认1小时刷新一次
            };
            _refreshTimer.Tick += async (sender, e) => await RefreshAsync();
            _refreshTimer.Start();
            
            // 初始化加载超时守护
            _loadingGuardTimer = new DispatcherTimer { Interval = _loadingTimeout };
            _loadingGuardTimer.Tick += async (s, e) =>
            {
                _loadingGuardTimer.Stop();
                SetState(ComponentState.NetworkError, "加载超时，正在重试...");
                await RefreshAsync();
            };

            // 使用Task.Run确保异步加载在后台线程执行，完全不阻塞UI线程
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    await LoadVoiceHubDataAsync();
                }
                catch (Exception)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        SetState(ComponentState.NetworkError, "广播站排期获取失败"));
                }
            });
        }

        private void UpdateTimerInterval()
        {
            // 如果有失败记录且还在冷却期内，设置为1分钟检查一次
            if (_lastFailureTime != DateTime.MinValue && 
                DateTime.Now - _lastFailureTime < _retryDelay)
            {
                _refreshTimer.Interval = TimeSpan.FromMinutes(1);
            }
            else
            {
                // 恢复正常的1小时间隔
                _refreshTimer.Interval = TimeSpan.FromHours(1);
            }
        }

        private async Task LoadVoiceHubDataAsync()
        {
            // 更新定时器间隔
            UpdateTimerInterval();
            
            // 检查是否需要等待（上次失败后的冷却时间）
            if (_lastFailureTime != DateTime.MinValue && 
                DateTime.Now - _lastFailureTime < _retryDelay)
            {
                var remainingTime = _retryDelay - (DateTime.Now - _lastFailureTime);
                await Dispatcher.UIThread.InvokeAsync(() => 
                    SetState(ComponentState.NetworkError, $"等待重试中... ({remainingTime.Minutes}分{remainingTime.Seconds}秒后重试)"));
                return;
            }

            // 尝试加载数据，带重试逻辑
            for (int attempt = 0; attempt <= MaxRetryCount; attempt++)
            {
                try
                {
                    await LoadVoiceHubDataCoreAsync();
                    
                    // 成功加载，重置重试计数和失败时间
                    _retryCount = 0;
                    _lastFailureTime = DateTime.MinValue;
                    
                    // 成功后恢复正常定时器间隔
                    await Dispatcher.UIThread.InvokeAsync(UpdateTimerInterval);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // 请求被取消：显式切换状态，防止残留“加载中”
                    await Dispatcher.UIThread.InvokeAsync(() => 
                    {
                        SetState(ComponentState.NetworkError, "请求已取消，稍后重试");
                        UpdateTimerInterval();
                    });
                    // 短暂等待后由定时器或守护触发重试
                    await Task.Delay(1000);
                    return;
                }
                catch (Exception ex)
                {
                    _retryCount = attempt + 1;
                    
                    // 如果还有重试机会
                    if (attempt < MaxRetryCount)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => 
                            SetState(ComponentState.NetworkError, $"获取失败，正在重试... ({_retryCount}/{MaxRetryCount})"));
                        
                        // 等待一段时间后重试（递增延迟：2秒、4秒、8秒）
                        var retryDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                        await Task.Delay(retryDelay);
                    }
                    else
                    {
                        // 所有重试都失败了，记录失败时间并设置错误状态
                        _lastFailureTime = DateTime.Now;
                        
                        string errorMessage = ex switch
                        {
                            HttpRequestException => "网络连接错误，10分钟后重试",
                            TaskCanceledException => "请求超时，10分钟后重试", 
                            JsonException => "数据格式错误，10分钟后重试",
                            _ => "获取失败，10分钟后重试"
                        };
                        
                        await Dispatcher.UIThread.InvokeAsync(() => 
                        {
                            SetState(ComponentState.NetworkError, errorMessage);
                            UpdateTimerInterval();
                        });
                    }
                }
            }
        }

        private async Task LoadVoiceHubDataCoreAsync()
        {
            // 取消之前的请求
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            // 设置加载状态
            await Dispatcher.UIThread.InvokeAsync(() => SetState(ComponentState.Loading));
            
            // 使用配置的API地址
            var apiUrl = !string.IsNullOrEmpty(Settings.ApiUrl) 
                ? Settings.ApiUrl 
                : "https://voicehub.lao-shui.top/api/songs/public";
            
            var jsonResponse = await _httpClient.GetStringAsync(apiUrl, _cancellationTokenSource.Token);
            var songItems = JsonSerializer.Deserialize<List<SongItem>>(jsonResponse);

            if (songItems == null || !songItems.Any())
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetState(ComponentState.NoSchedule, "暂无排期数据"));
                return;
            }

            // 过滤掉无效日期的项目
            var validItems = songItems.Where(s => s.GetPlayDate() != DateTime.MinValue).ToList();
            
            if (!validItems.Any())
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetState(ComponentState.NoSchedule, "暂无有效排期数据"));
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
                    await Dispatcher.UIThread.InvokeAsync(() => SetState(ComponentState.NoSchedule, "暂无排期数据"));
                    return;
                }
            }

            // 验证显示项目的日期一致性
            displayItems = displayItems.Where(item => item.GetPlayDate() == actualDate).ToList();

            if (!displayItems.Any())
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetState(ComponentState.NoSchedule, "排期数据日期不一致"));
                return;
            }

            // 格式化显示内容，与 v1 保持一致
            var sb = new StringBuilder();
            sb.Append($"广播站排期 | {actualDate:yyyy/MM/dd}: ");
            
            var songInfos = new List<string>();
            foreach (var item in displayItems)
            {
                var song = item.Song;
                songInfos.Add($"#{item.Sequence} {song.Artist} - {song.Title} - {song.Requester}");
            }
            sb.Append(string.Join(" | ", songInfos));

            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                SetState(ComponentState.Loaded);
                ContentText = sb.ToString();
            });
        }

        private void SetState(ComponentState state, string? message = null)
        {
            _currentState = state;
            
            // 更新守护定时器
            if (state == ComponentState.Loading)
                _loadingGuardTimer.Start();
            else
                _loadingGuardTimer.Stop();

            // 更新UI元素显示
            IsLoading = state == ComponentState.Loading;
            IsDataLoaded = state == ComponentState.Loaded;
            IsError = state == ComponentState.NetworkError || state == ComponentState.NoSchedule;

            if (message != null)
            {
                MessageText = message;
            }
        }

        public async Task RefreshAsync()
        {
            // 如果已经在加载中，不重复触发
            if (_currentState == ComponentState.Loading && _loadingGuardTimer.IsEnabled)
                return;

            await LoadVoiceHubDataAsync();
        }

        private enum ComponentState
        {
            Loading,
            Loaded,
            NoSchedule,
            NetworkError
        }
    }
}
