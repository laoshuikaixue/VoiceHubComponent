using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;
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
        private static event Func<Task>? ManualRefreshRequested;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly ILogger<VoiceHubControl>? _logger = IAppHost.TryGetService<ILogger<VoiceHubControl>>();
        private ComponentState _currentState = ComponentState.Loading;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _lyricTimer;
        private CancellationTokenSource _lyricsCts = new();
        private List<ScheduledSongPlayback> _playbackPlan = new();
        private readonly object _playbackLock = new();
        private DateTime _playbackDate = DateTime.MinValue;
        private string _scheduleSummaryText = string.Empty;
        private static readonly Regex LrcTimeRegex = new(@"\[(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex EnhancedWordRegex = new(@"<(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?>([^<]*)", RegexOptions.Compiled);
        private static readonly Regex QrcLineRegex = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
        private static readonly Regex QrcWordRegex = new(@"([^(]*)\((\d+),(\d+)\)", RegexOptions.Compiled);
        private static readonly Regex TtmlLineRegex = new(@"<(?:p|span)[^>]*?begin=""([^""]+)""[^>]*?(?:end=""([^""]+)""|dur=""([^""]+)"")?[^>]*>(.*?)</(?:p|span)>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex LrcMetaRegex = new(@"^\[[a-z]+:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex XmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex TencentLegacyIdRegex = new(@"^\d+$", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private const int LyricCacheVersion = 6;
        private const int LyricFetchRetryCount = 3;
        private const int MinValidTencentAudioDurationSeconds = 10;
        private const string InvalidTencentAudioUrlSuffix = "/2149972737147268278.mp3";
        private static readonly TimeSpan MetadataLyricDurationTolerance = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan LastResortSongDuration = TimeSpan.FromMinutes(4);
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClassIsland", "Plugins", "VoiceHubComponent", "Cache");
        
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
        private readonly TimeSpan _loadingTimeout = TimeSpan.FromSeconds(60);

        public VoiceHubControl()
        {
            InitializeComponent();
            ManualRefreshRequested += HandleManualRefreshRequestedAsync;
            DetachedFromVisualTree += VoiceHubControl_DetachedFromVisualTree;
            
            // 设置HTTP客户端超时
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            // 初始化定时器，正常情况下1小时刷新一次，失败后1分钟检查一次
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1) // 默认1小时刷新一次
            };
            _refreshTimer.Tick += async (sender, e) => await RefreshAsync();
            _refreshTimer.Start();

            _lyricTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _lyricTimer.Tick += (_, _) => UpdateLyricDisplay();
            _lyricTimer.Start();
            
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

        public static async Task RequestManualRefreshAsync()
        {
            var handlers = ManualRefreshRequested;
            if (handlers == null)
                return;

            var tasks = handlers.GetInvocationList()
                .Cast<Func<Task>>()
                .Select(handler => handler());
            await Task.WhenAll(tasks);
        }

        public static void ClearLyricCache()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(CacheDirectory, "lyrics-cache-*.json"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略单个文件删除失败，继续清理其他缓存文件。
                    }
                }
            }
            catch
            {
                // 缓存清理失败时不阻断后续刷新。
            }
        }

        private async Task HandleManualRefreshRequestedAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(async () => await RefreshAsync());
        }

        private void VoiceHubControl_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            ManualRefreshRequested -= HandleManualRefreshRequestedAsync;
            DetachedFromVisualTree -= VoiceHubControl_DetachedFromVisualTree;
            _lyricsCts.Cancel();
            _refreshTimer.Stop();
            _lyricTimer.Stop();
            _loadingGuardTimer.Stop();
            _httpClient.Dispose();
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
            // 设置加载状态
            await Dispatcher.UIThread.InvokeAsync(() => SetState(ComponentState.Loading));
            
            // 使用配置的API地址
            var apiUrl = !string.IsNullOrEmpty(Settings.ApiUrl) 
                ? Settings.ApiUrl 
                : "https://voicehub.lao-shui.top/api/songs/public";
            
            var jsonResponse = await _httpClient.GetStringAsync(apiUrl);
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

            _scheduleSummaryText = BuildScheduleSummary(displayItems, actualDate);
            await BuildPlaybackPlanAsync(displayItems, actualDate);

            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                SetState(ComponentState.Loaded);
                ContentText = Settings.EnableLyrics ? GetLyricDisplayText(DateTime.Now) : _scheduleSummaryText;
            });
        }

        private string BuildScheduleSummary(IReadOnlyList<SongItem> displayItems, DateTime actualDate)
        {
            var sb = new StringBuilder();
            sb.Append($"广播站排期 | {actualDate:yyyy/MM/dd}: ");

            var songInfos = displayItems.Select(item =>
            {
                var song = item.Song;
                return $"#{item.Sequence} {song.Artist} - {song.Title} - {song.Requester}";
            });
            sb.Append(string.Join(" | ", songInfos));

            return sb.ToString();
        }

        private static string BuildScheduleSignature(IEnumerable<SongItem> items)
        {
            return string.Join("|", items.Select(item =>
            {
                var song = item.Song;
                var platform = NormalizePlatform(song.MusicPlatform);
                var musicId = string.IsNullOrWhiteSpace(song.MusicId) ? song.Id.ToString(CultureInfo.InvariantCulture) : song.MusicId.Trim();
                return $"{item.Id}:{song.Id}:{platform}:{musicId}:{item.Sequence}";
            }));
        }

        private static string GetSongCacheKey(Song song)
        {
            var platform = NormalizePlatform(song.MusicPlatform);
            var musicId = string.IsNullOrWhiteSpace(song.MusicId) ? song.Id.ToString(CultureInfo.InvariantCulture) : song.MusicId.Trim();
            return string.IsNullOrWhiteSpace(musicId) ? string.Empty : $"{platform}:{musicId}";
        }

        private static string NormalizePlatform(string? platform)
        {
            var value = platform?.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(value) ? "netease" : value;
        }

        private static string NormalizeTencentMusicId(string musicId)
        {
            return musicId.Trim().StartsWith("qqmid:", StringComparison.OrdinalIgnoreCase)
                ? musicId.Trim()[6..]
                : musicId.Trim();
        }

        private static bool IsTencentLegacyNumericId(string musicId)
        {
            return TencentLegacyIdRegex.IsMatch(NormalizeTencentMusicId(musicId));
        }

        private static (string Key, string Value) GetVkeysIdParam(string platform, string musicId)
        {
            if (platform == "tencent")
            {
                var normalized = NormalizeTencentMusicId(musicId);
                return IsTencentLegacyNumericId(normalized)
                    ? ("id", normalized)
                    : ("mid", normalized);
            }

            return ("id", musicId.Trim());
        }

        private static (string Key, string Value) GetTencentNativeLyricIdParam(string musicId)
        {
            var normalized = NormalizeTencentMusicId(musicId);
            return IsTencentLegacyNumericId(normalized)
                ? ("songid", normalized)
                : ("songmid", normalized);
        }

        private static DailyLyricCache LoadDailyCache(DateTime date, string scheduleSignature, string cookieFingerprint)
        {
            try
            {
                var path = GetCachePath(date);
                if (File.Exists(path))
                {
                    var cache = JsonSerializer.Deserialize<DailyLyricCache>(File.ReadAllText(path), JsonOptions);
                    if (cache != null)
                    {
                        if (cache.Version != LyricCacheVersion)
                        {
                            return CreateDailyCache(date, scheduleSignature, cookieFingerprint);
                        }

                        cache.Entries ??= new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
                        cache.Entries = new Dictionary<string, CacheEntry>(cache.Entries, StringComparer.OrdinalIgnoreCase);
                        if (cache.ScheduleSignature != scheduleSignature ||
                            cache.NeteaseCookieFingerprint != cookieFingerprint)
                        {
                            cache.Entries.Clear();
                        }

                        cache.Version = LyricCacheVersion;
                        cache.ScheduleSignature = scheduleSignature;
                        cache.HasNeteaseCookie = !string.IsNullOrWhiteSpace(cookieFingerprint);
                        cache.NeteaseCookieFingerprint = cookieFingerprint;
                        return cache;
                    }
                }
            }
            catch
            {
                // 缓存损坏时直接重建。
            }

            return CreateDailyCache(date, scheduleSignature, cookieFingerprint);
        }

        private static DailyLyricCache CreateDailyCache(DateTime date, string scheduleSignature, string cookieFingerprint)
        {
            return new DailyLyricCache
            {
                Version = LyricCacheVersion,
                Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ScheduleSignature = scheduleSignature,
                HasNeteaseCookie = !string.IsNullOrWhiteSpace(cookieFingerprint),
                NeteaseCookieFingerprint = cookieFingerprint,
                Entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void SaveDailyCache(DateTime date, DailyLyricCache cache)
        {
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                cache.Version = LyricCacheVersion;
                File.WriteAllText(GetCachePath(date), JsonSerializer.Serialize(cache, JsonOptions));
            }
            catch
            {
                // 缓存只是性能优化，保存失败不影响显示。
            }
        }

        private static void CleanupOldCacheFiles(DateTime currentDate)
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                {
                    return;
                }

                var currentFileName = Path.GetFileName(GetCachePath(currentDate));
                foreach (var file in Directory.EnumerateFiles(CacheDirectory, "lyrics-cache-*.json"))
                {
                    if (!string.Equals(Path.GetFileName(file), currentFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // 清理失败不影响主流程。
            }
        }

        private static string GetCachePath(DateTime date)
        {
            return Path.Combine(CacheDirectory, $"lyrics-cache-{date:yyyy-MM-dd}.json");
        }

        private static void ApplyCacheEntry(ScheduledSongPlayback playback, CacheEntry cacheEntry)
        {
            playback.DurationSource = string.IsNullOrWhiteSpace(cacheEntry.DurationSource)
                ? "fallback"
                : cacheEntry.DurationSource;
            playback.Duration = playback.DurationSource == "unresolved"
                ? TimeSpan.Zero
                : cacheEntry.DurationMs > 0
                    ? TimeSpan.FromMilliseconds(cacheEntry.DurationMs)
                    : LastResortSongDuration;
            playback.LyricStatus = string.IsNullOrWhiteSpace(cacheEntry.LyricStatus)
                ? (cacheEntry.Lyrics.Count > 0 ? "ready" : "no-lyrics")
                : cacheEntry.LyricStatus;
            playback.Lyrics = cacheEntry.Lyrics.Select(line => line.ToLyricLineItem()).ToList();
        }

        private async Task BuildPlaybackPlanAsync(IReadOnlyList<SongItem> displayItems, DateTime actualDate)
        {
            _lyricsCts.Cancel();
            _lyricsCts.Dispose();
            _lyricsCts = new CancellationTokenSource();
            var token = _lyricsCts.Token;

            var orderedItems = displayItems.OrderBy(item => item.Sequence).ToList();
            var scheduleSignature = BuildScheduleSignature(orderedItems);
            var cookieFingerprint = GetNeteaseCookieFingerprint(Settings.NeteaseCookie);
            var cache = LoadDailyCache(actualDate, scheduleSignature, cookieFingerprint);
            CleanupOldCacheFiles(actualDate);

            var currentKeys = orderedItems
                .Select(item => GetSongCacheKey(item.Song))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            cache.Entries = cache.Entries
                .Where(pair => currentKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            var plan = orderedItems
                .Select(item => new ScheduledSongPlayback(item, LastResortSongDuration))
                .ToList();

            if (Settings.EnableLyrics)
            {
                var metadataGroups = plan
                    .GroupBy(entry => GetSongCacheKey(entry.Item.Song), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var group in metadataGroups)
                {
                    token.ThrowIfCancellationRequested();
                    var cacheKey = group.Key;
                    var firstEntry = group.First();
                    if (!string.IsNullOrWhiteSpace(cacheKey) &&
                        cache.Entries.TryGetValue(cacheKey, out var cachedEntry) &&
                        cachedEntry.IsUsable())
                    {
                        _logger?.LogInformation(
                            "VoiceHub 歌词缓存命中：{CacheKey}, status={LyricStatus}, lines={LineCount}, durationSource={DurationSource}",
                            cacheKey,
                            cachedEntry.LyricStatus,
                            cachedEntry.Lyrics.Count,
                            cachedEntry.DurationSource);
                        foreach (var entry in group)
                        {
                            ApplyCacheEntry(entry, cachedEntry);
                        }
                        continue;
                    }

                    var resolved = await ResolveSongPlaybackAsync(firstEntry.Item.Song, token);
                    foreach (var entry in group)
                    {
                        entry.Duration = resolved.Duration;
                        entry.DurationSource = resolved.DurationSource;
                        entry.Lyrics = resolved.Lyrics.Select(line => line.Clone()).ToList();
                        entry.LyricStatus = resolved.LyricStatus;
                    }

                    if (!string.IsNullOrWhiteSpace(cacheKey))
                    {
                        cache.Entries[cacheKey] = CacheEntry.FromPlayback(cacheKey, firstEntry);
                    }

                    _logger?.LogInformation(
                        "VoiceHub 歌曲解析完成：{Platform}:{MusicId}, status={LyricStatus}, lines={LineCount}, duration={Duration}, durationSource={DurationSource}",
                        NormalizePlatform(firstEntry.Item.Song.MusicPlatform),
                        firstEntry.Item.Song.MusicId ?? firstEntry.Item.Song.Id.ToString(CultureInfo.InvariantCulture),
                        resolved.LyricStatus,
                        resolved.Lyrics.Count,
                        resolved.Duration,
                        resolved.DurationSource);
                }
            }

            cache.Date = actualDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            cache.ScheduleSignature = scheduleSignature;
            cache.HasNeteaseCookie = !string.IsNullOrWhiteSpace(cookieFingerprint);
            cache.NeteaseCookieFingerprint = cookieFingerprint;
            SaveDailyCache(actualDate, cache);

            lock (_playbackLock)
            {
                _playbackDate = actualDate.Date;
                _playbackPlan = plan;
            }
        }

        private async Task<ResolvedPlayback> ResolveSongPlaybackAsync(Song song, CancellationToken token)
        {
            var payload = await FetchLyricsAsync(song, token);
            var lyrics = ParseBestLyrics(payload);
            _logger?.LogInformation(
                "VoiceHub 歌词解析行数：{Platform}:{MusicId}, lines={LineCount}, hasTranslation={HasTranslation}",
                NormalizePlatform(song.MusicPlatform),
                song.MusicId ?? song.Id.ToString(CultureInfo.InvariantCulture),
                lyrics.Count,
                !string.IsNullOrWhiteSpace(payload.Translation));
            if (!string.IsNullOrWhiteSpace(payload.Translation))
            {
                AlignTranslations(lyrics, ParseSmartLrc(payload.Translation));
            }

            var lyricDuration = GetEstimatedDuration(lyrics, TimeSpan.Zero);
            var officialDuration = await FetchOfficialDurationAsync(song, token);
            if (officialDuration.Duration > TimeSpan.Zero)
            {
                if (IsDurationTrusted(officialDuration.Duration, lyricDuration))
                {
                    return new ResolvedPlayback(officialDuration.Duration, officialDuration.Source, lyrics, lyrics.Count > 0 ? "ready" : "no-lyrics");
                }

                var fallbackAudioDuration = await FetchFallbackAudioDurationAsync(song, lyricDuration, token);
                if (fallbackAudioDuration.Duration > TimeSpan.Zero)
                {
                    return new ResolvedPlayback(fallbackAudioDuration.Duration, fallbackAudioDuration.Source, lyrics, lyrics.Count > 0 ? "ready" : "no-lyrics");
                }

                return BuildEstimatedPlayback(lyricDuration, lyrics);
            }

            var audioDuration = await FetchFallbackAudioDurationAsync(song, lyricDuration, token);
            if (audioDuration.Duration > TimeSpan.Zero)
            {
                return new ResolvedPlayback(audioDuration.Duration, audioDuration.Source, lyrics, lyrics.Count > 0 ? "ready" : "no-lyrics");
            }

            return BuildEstimatedPlayback(lyricDuration, lyrics);
        }

        private static ResolvedPlayback BuildEstimatedPlayback(TimeSpan lyricDuration, List<LyricLineItem> lyrics)
        {
            var duration = lyricDuration > TimeSpan.Zero ? lyricDuration : LastResortSongDuration;
            return new ResolvedPlayback(duration, "fallback", lyrics, lyrics.Count > 0 ? "ready" : "no-lyrics");
        }

        private async Task<LyricPayload> FetchLyricsAsync(Song song, CancellationToken token)
        {
            var platform = song.MusicPlatform?.Trim().ToLowerInvariant();
            var musicId = string.IsNullOrWhiteSpace(song.MusicId) ? song.Id.ToString(CultureInfo.InvariantCulture) : song.MusicId.Trim();
            if (string.IsNullOrWhiteSpace(musicId))
            {
                return LyricPayload.Empty;
            }

            for (var attempt = 1; attempt <= LyricFetchRetryCount; attempt++)
            {
                try
                {
                    LyricPayload payload;
                    if (platform == "tencent" || platform == "qq")
                    {
                        try
                        {
                            payload = await FetchVoiceHubTencentLyricsAsync(musicId, token);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(
                                ex,
                                "VoiceHub QQ 原生歌词接口失败，将回退 VKeys：musicId={MusicId}",
                                musicId);
                            payload = LyricPayload.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(payload.Lrc) &&
                            string.IsNullOrWhiteSpace(payload.Yrc) &&
                            string.IsNullOrWhiteSpace(payload.Ttml))
                        {
                            payload = await FetchVkeysLyricsAsync("tencent", musicId, token);
                        }
                    }
                    else
                    {
                        try
                        {
                            payload = await FetchVoiceHubNeteaseLyricsAsync(musicId, token);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(
                                ex,
                                "VoiceHub 网易云歌词接口失败，将回退 VKeys：musicId={MusicId}",
                                musicId);
                            payload = LyricPayload.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(payload.Lrc) &&
                            string.IsNullOrWhiteSpace(payload.Yrc) &&
                            string.IsNullOrWhiteSpace(payload.Ttml))
                        {
                            payload = await FetchVkeysLyricsAsync("netease", musicId, token);
                        }
                    }

                    return payload;
                }
                catch (Exception ex)
                {
                    if (attempt == LyricFetchRetryCount)
                    {
                        _logger?.LogWarning(
                            ex,
                            "VoiceHub 歌词获取最终失败：platform={Platform}, musicId={MusicId}, attempt={Attempt}",
                            platform,
                            musicId,
                            attempt);
                        return LyricPayload.Empty;
                    }

                    _logger?.LogWarning(
                        ex,
                        "VoiceHub 歌词获取失败，准备重试：platform={Platform}, musicId={MusicId}, attempt={Attempt}",
                        platform,
                        musicId,
                        attempt);
                    // 临时 API 失败时稍后重试。
                }

                if (attempt < LyricFetchRetryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), token);
                }
            }

            return LyricPayload.Empty;
        }

        private async Task<LyricPayload> FetchVoiceHubNeteaseLyricsAsync(string musicId, CancellationToken token)
        {
            var origin = GetVoiceHubOrigin();
            if (origin == null)
            {
                return LyricPayload.Empty;
            }

            var lyricUrl = new Uri(origin, $"/api/api-enhanced/netease/lyric?id={Uri.EscapeDataString(musicId)}");
            var lyricNewUrl = new Uri(origin, $"/api/api-enhanced/netease/lyric/new?id={Uri.EscapeDataString(musicId)}");
            var lrcTask = GetVoiceHubStringAsync(lyricUrl, token);
            var yrcTask = GetVoiceHubStringAsync(lyricNewUrl, token);

            string? lrc = null;
            string? translation = null;
            string? yrc = null;
            var successfulResponses = 0;
            Exception? lastError = null;

            try
            {
                using var document = JsonDocument.Parse(await lrcTask);
                var root = document.RootElement;
                EnsureSuccessfulApiResponse(root, "网易云歌词");
                lrc = TryGetNestedString(root, "lrc", "lyric");
                translation = TryGetNestedString(root, "tlyric", "lyric");
                successfulResponses++;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger?.LogWarning(ex, "VoiceHub 网易云 LRC 歌词接口失败：musicId={MusicId}", musicId);
                // 歌词接口失败时允许后续备用源兜底。
            }

            try
            {
                using var document = JsonDocument.Parse(await yrcTask);
                var root = document.RootElement;
                EnsureSuccessfulApiResponse(root, "网易云逐字歌词");
                yrc = TryGetNestedString(root, "yrc", "lyric");
                successfulResponses++;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger?.LogWarning(ex, "VoiceHub 网易云 YRC 歌词接口失败：musicId={MusicId}", musicId);
                // yrc 不是必需数据。
            }

            if (successfulResponses == 0 && lastError != null)
            {
                throw new HttpRequestException("网易云歌词接口请求失败", lastError);
            }

            return new LyricPayload(lrc, translation, yrc, null);
        }

        private async Task<LyricPayload> FetchVoiceHubTencentLyricsAsync(string musicId, CancellationToken token)
        {
            var origin = GetVoiceHubOrigin();
            if (origin == null)
            {
                return LyricPayload.Empty;
            }

            var idParam = GetTencentNativeLyricIdParam(musicId);
            _logger?.LogInformation(
                "VoiceHub QQ 原生歌词请求：{IdKey}={IdValue}",
                idParam.Key,
                idParam.Value);
            var lyricUrl = new Uri(origin, $"/api/native-api/lyric/tx?{idParam.Key}={Uri.EscapeDataString(idParam.Value)}");
            var json = await GetVoiceHubStringAsync(lyricUrl, token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                throw new HttpRequestException("VoiceHub QQ 歌词接口返回失败");
            }

            if (!root.TryGetProperty("data", out var data))
            {
                throw new HttpRequestException("VoiceHub QQ 歌词接口返回无效数据");
            }

            var lrc = TryGetString(data, "lrc");
            var translation = TryGetString(data, "trans");
            _logger?.LogInformation(
                "VoiceHub QQ 原生歌词响应：{IdKey}={IdValue}, hasLrc={HasLrc}, hasTranslation={HasTranslation}",
                idParam.Key,
                idParam.Value,
                !string.IsNullOrWhiteSpace(lrc),
                !string.IsNullOrWhiteSpace(translation));
            return new LyricPayload(lrc, translation, null, null);
        }

        private async Task<LyricPayload> FetchVkeysLyricsAsync(string platform, string musicId, CancellationToken token)
        {
            var idParam = GetVkeysIdParam(platform, musicId);
            _logger?.LogInformation(
                "VoiceHub VKeys 歌词请求：platform={Platform}, {IdKey}={IdValue}",
                platform,
                idParam.Key,
                idParam.Value);
            var lyricUrl = $"https://api.vkeys.cn/v2/music/{platform}/lyric?{idParam.Key}={Uri.EscapeDataString(idParam.Value)}";
            var json = await _httpClient.GetStringAsync(lyricUrl, token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            EnsureSuccessfulApiResponse(root, "VKeys 歌词");

            if (!root.TryGetProperty("data", out var data))
            {
                throw new HttpRequestException("VKeys 歌词接口返回无效数据");
            }

            var lrc = TryGetString(data, "lrc");
            var translation = TryGetString(data, "trans");
            var yrc = TryGetString(data, "yrc");
            var ttml = TryGetString(data, "ttml");
            _logger?.LogInformation(
                "VoiceHub VKeys 歌词响应：platform={Platform}, {IdKey}={IdValue}, hasLrc={HasLrc}, hasTrans={HasTrans}, hasYrc={HasYrc}, hasTtml={HasTtml}",
                platform,
                idParam.Key,
                idParam.Value,
                !string.IsNullOrWhiteSpace(lrc),
                !string.IsNullOrWhiteSpace(translation),
                !string.IsNullOrWhiteSpace(yrc),
                !string.IsNullOrWhiteSpace(ttml));
            return new LyricPayload(lrc, translation, yrc, ttml);
        }

        private async Task<DurationProbe> FetchOfficialDurationAsync(Song song, CancellationToken token)
        {
            var platform = song.MusicPlatform?.Trim().ToLowerInvariant();
            var musicId = string.IsNullOrWhiteSpace(song.MusicId) ? song.Id.ToString(CultureInfo.InvariantCulture) : song.MusicId.Trim();
            if (string.IsNullOrWhiteSpace(musicId))
            {
                return DurationProbe.Empty;
            }

            try
            {
                var duration = platform == "tencent" || platform == "qq"
                    ? await FetchTencentDurationAsync(musicId, token)
                    : await FetchNeteaseDurationAsync(musicId, token);
                return duration;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "VoiceHub 官方时长获取失败：platform={Platform}, musicId={MusicId}",
                    platform,
                    musicId);
                return DurationProbe.Empty;
            }
        }

        private async Task<DurationProbe> FetchNeteaseDurationAsync(string musicId, CancellationToken token)
        {
            var origin = GetVoiceHubOrigin();
            if (origin == null)
            {
                return DurationProbe.Empty;
            }

            try
            {
                var urlDuration = await FetchNeteaseUrlDurationAsync(origin, musicId, token);
                if (urlDuration.Duration > TimeSpan.Zero)
                {
                    return urlDuration;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "VoiceHub 网易云 URL 时长接口失败，尝试详情接口：musicId={MusicId}", musicId);
                // URL 接口失败时继续尝试详情接口。
            }

            return await FetchNeteaseDetailDurationAsync(origin, musicId, token);
        }

        private async Task<DurationProbe> FetchNeteaseUrlDurationAsync(Uri origin, string musicId, CancellationToken token)
        {
            var cookie = Settings.NeteaseCookie?.Trim();
            var query = $"/api/api-enhanced/netease/song/url/v1?id={Uri.EscapeDataString(musicId)}&level=exhigh";
            query += string.IsNullOrWhiteSpace(cookie)
                ? "&unblock=true"
                : $"&unblock=false&cookie={Uri.EscapeDataString(cookie)}";
            var url = new Uri(origin, query);
            var json = await GetVoiceHubStringAsync(url, token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var dataElement) ||
                dataElement.ValueKind != JsonValueKind.Array ||
                dataElement.GetArrayLength() == 0)
            {
                return DurationProbe.Empty;
            }

            var firstSong = dataElement[0];
            var durationMs = TryGetNumber(firstSong, "time");
            if (durationMs > 0)
            {
                return new DurationProbe(TimeSpan.FromMilliseconds(durationMs), "metadata-url");
            }

            var estimatedDuration = await EstimateAudioDurationAsync(firstSong, token);
            return estimatedDuration > TimeSpan.Zero
                ? new DurationProbe(estimatedDuration, "audio-estimated")
                : DurationProbe.Empty;
        }

        private async Task<DurationProbe> FetchFallbackAudioDurationAsync(Song song, TimeSpan lyricDuration, CancellationToken token)
        {
            var platform = song.MusicPlatform?.Trim().ToLowerInvariant();
            var musicId = string.IsNullOrWhiteSpace(song.MusicId) ? song.Id.ToString(CultureInfo.InvariantCulture) : song.MusicId.Trim();
            if (string.IsNullOrWhiteSpace(musicId))
            {
                return DurationProbe.Empty;
            }

            if (platform is "tencent" or "qq")
            {
                return await FetchVoiceHubResolvedAudioDurationAsync(song, lyricDuration, token);
            }

            foreach (var fetcher in new Func<string, CancellationToken, Task<DurationProbe>>[]
                     {
                         FetchRrvennDurationAsync,
                         FetchVkeysNeteaseDurationAsync,
                         FetchMetingDurationAsync
                     })
            {
                try
                {
                    var duration = await fetcher(musicId, token);
                    if (IsDurationTrusted(duration.Duration, lyricDuration))
                    {
                        return duration;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "VoiceHub 备用音源时长获取失败：source={Source}, musicId={MusicId}",
                        fetcher.Method.Name,
                        musicId);
                    // 单个备用音源失败时继续尝试下一个。
                }
            }

            return DurationProbe.Empty;
        }

        private async Task<DurationProbe> FetchVoiceHubResolvedAudioDurationAsync(Song song, TimeSpan lyricDuration, CancellationToken token)
        {
            var origin = GetVoiceHubOrigin();
            if (origin == null)
            {
                return DurationProbe.Empty;
            }

            var musicId = string.IsNullOrWhiteSpace(song.MusicId)
                ? song.Id.ToString(CultureInfo.InvariantCulture)
                : song.MusicId.Trim();
            var payload = new
            {
                platform = "tencent",
                musicId,
                playUrl = string.IsNullOrWhiteSpace(song.PlayUrl) ? null : song.PlayUrl.Trim(),
                quality = 8
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/api/music/resolve-url"))
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "VoiceHub QQ resolve-url 返回非成功状态：status={StatusCode}, musicId={MusicId}",
                    response.StatusCode,
                    musicId);
                return DurationProbe.Empty;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: token);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True)
            {
                _logger?.LogWarning("VoiceHub QQ resolve-url 响应 success=false：musicId={MusicId}", musicId);
                return DurationProbe.Empty;
            }

            var audioUrl = TryGetString(root, "url");
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                _logger?.LogWarning("VoiceHub QQ resolve-url 未返回音频 URL：musicId={MusicId}", musicId);
                return DurationProbe.Empty;
            }

            if (IsKnownInvalidTencentAudioUrl(audioUrl))
            {
                _logger?.LogWarning("VoiceHub QQ 解析到已知无效音频链接：{AudioUrl}", audioUrl);
                return DurationProbe.Empty;
            }

            var estimatedDuration = await EstimateAudioDurationFromUrlAsync(audioUrl, 0, token);
            _logger?.LogInformation(
                "VoiceHub QQ resolve-url 音频时长估算完成：musicId={MusicId}, duration={Duration}",
                musicId,
                estimatedDuration);
            if (IsInvalidTencentAudioDuration(estimatedDuration, lyricDuration))
            {
                _logger?.LogWarning(
                    "VoiceHub QQ 音频时长过短，忽略该解析结果：duration={Duration}, lyricDuration={LyricDuration}, url={AudioUrl}",
                    estimatedDuration,
                    lyricDuration,
                    audioUrl);
                return DurationProbe.Empty;
            }

            return IsDurationTrusted(estimatedDuration, lyricDuration)
                ? new DurationProbe(estimatedDuration, "voicehub-audio")
                : DurationProbe.Empty;
        }

        private async Task<DurationProbe> FetchRrvennDurationAsync(string musicId, CancellationToken token)
        {
            var url = $"https://music.rrvenn.cn/api/song?url={Uri.EscapeDataString(musicId)}&level=exhigh";
            var json = await _httpClient.GetStringAsync(url, token);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data))
            {
                return await ResolveAudioDataDurationAsync(data, "rrvenn-audio", token);
            }

            return await ResolveAudioDataDurationAsync(document.RootElement, "rrvenn-audio", token);
        }

        private async Task<DurationProbe> FetchVkeysNeteaseDurationAsync(string musicId, CancellationToken token)
        {
            var url = $"https://api.vkeys.cn/v2/music/netease?id={Uri.EscapeDataString(musicId)}&quality=4";
            var json = await _httpClient.GetStringAsync(url, token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return DurationProbe.Empty;
            }

            return await ResolveAudioDataDurationAsync(data, "vkeys-audio", token);
        }

        private async Task<DurationProbe> FetchMetingDurationAsync(string musicId, CancellationToken token)
        {
            foreach (var baseUrl in new[] { "https://api.qijieya.cn/meting", "https://api.obdo.cc/meting" })
            {
                try
                {
                    var url = $"{baseUrl}/?server=netease&type=song&id={Uri.EscapeDataString(musicId)}";
                    var json = await _httpClient.GetStringAsync(url, token);
                    using var document = JsonDocument.Parse(json);
                    var data = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                        ? document.RootElement[0]
                        : document.RootElement;
                    var duration = await ResolveAudioDataDurationAsync(data, "meting-audio", token);
                    if (duration.Duration > TimeSpan.Zero)
                    {
                        return duration;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "VoiceHub Meting 时长源失败：baseUrl={BaseUrl}, musicId={MusicId}", baseUrl, musicId);
                    // 继续尝试下一个 Meting 源。
                }
            }

            return DurationProbe.Empty;
        }

        private async Task<DurationProbe> ResolveAudioDataDurationAsync(JsonElement data, string source, CancellationToken token)
        {
            var estimatedDuration = await EstimateAudioDurationAsync(data, token);
            if (estimatedDuration > TimeSpan.Zero)
            {
                return new DurationProbe(estimatedDuration, source);
            }

            var directDuration = TryReadDuration(data);
            return directDuration > TimeSpan.Zero
                ? new DurationProbe(directDuration, source)
                : DurationProbe.Empty;
        }

        private async Task<TimeSpan> EstimateAudioDurationAsync(JsonElement songUrlData, CancellationToken token)
        {
            var bitrate = TryGetNumber(songUrlData, "br");
            var size = TryGetNumber(songUrlData, "size");
            if (bitrate > 0 && size > 0)
            {
                return TimeSpan.FromSeconds(size * 8 / bitrate);
            }

            var audioUrl = TryGetString(songUrlData, "url");
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                return TimeSpan.Zero;
            }

            return await EstimateAudioDurationFromUrlAsync(audioUrl, bitrate, token);
        }

        private async Task<TimeSpan> EstimateAudioDurationFromUrlAsync(string audioUrl, double bitrate, CancellationToken token)
        {
            try
            {
                var contentLength = await TryGetAudioContentLengthAsync(audioUrl, token);
                if (contentLength <= 0)
                {
                    return TimeSpan.Zero;
                }

                var trustedBitrate = bitrate > 0 ? bitrate : await TryReadMp3BitrateAsync(audioUrl, token);
                return trustedBitrate > 0
                    ? TimeSpan.FromSeconds(contentLength * 8d / trustedBitrate)
                    : TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "VoiceHub 音频时长估算失败：url={AudioUrl}", audioUrl);
                return TimeSpan.Zero;
            }
        }

        private async Task<DurationProbe> FetchNeteaseDetailDurationAsync(Uri origin, string musicId, CancellationToken token)
        {
            var detailUrl = new Uri(origin, $"/api/api-enhanced/netease/song/detail?ids={Uri.EscapeDataString(musicId)}");
            var json = await GetVoiceHubStringAsync(detailUrl, token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("songs", out var songsElement) ||
                songsElement.ValueKind != JsonValueKind.Array ||
                songsElement.GetArrayLength() == 0)
            {
                return DurationProbe.Empty;
            }

            var firstSong = songsElement[0];
            var durationMs = TryGetNumber(firstSong, "dt");
            return durationMs > 0
                ? new DurationProbe(TimeSpan.FromMilliseconds(durationMs), "metadata")
                : DurationProbe.Empty;
        }

        private async Task<DurationProbe> FetchTencentDurationAsync(string musicId, CancellationToken token)
        {
            var idParam = GetVkeysIdParam("tencent", musicId);
            var detailUrl = $"https://api.vkeys.cn/v2/music/tencent?{idParam.Key}={Uri.EscapeDataString(idParam.Value)}";
            var json = await _httpClient.GetStringAsync(detailUrl, token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return DurationProbe.Empty;
            }

            var duration = TryGetNumber(data, "duration");
            if (duration <= 0)
            {
                duration = TryGetNumber(data, "interval");
            }

            if (duration <= 0)
            {
                return DurationProbe.Empty;
            }

            // Vkeys QQ 音乐通常返回秒；异常偏大的值按毫秒处理。
            var durationValue = duration > 10_000 ? TimeSpan.FromMilliseconds(duration) : TimeSpan.FromSeconds(duration);
            _logger?.LogInformation(
                "VoiceHub QQ VKeys 时长响应：{IdKey}={IdValue}, duration={Duration}",
                idParam.Key,
                idParam.Value,
                durationValue);
            return new DurationProbe(durationValue, "metadata");
        }

        private Uri? GetVoiceHubOrigin()
        {
            if (!Uri.TryCreate(Settings.ApiUrl, UriKind.Absolute, out var apiUri))
            {
                return null;
            }

            return new Uri(apiUri.GetLeftPart(UriPartial.Authority));
        }

        private async Task<string> GetVoiceHubStringAsync(Uri url, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddVoiceHubSameOriginHeaders(request, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var content = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "VoiceHub 内部接口返回非成功状态：status={StatusCode}, path={Path}, body={Body}",
                    response.StatusCode,
                    url.AbsolutePath,
                    TruncateForLog(content, 500));
                throw new HttpRequestException(
                    $"VoiceHub 内部接口返回非成功状态：{(int)response.StatusCode} {response.StatusCode}");
            }

            return content;
        }

        private static void AddVoiceHubSameOriginHeaders(HttpRequestMessage request, Uri url)
        {
            var origin = url.GetLeftPart(UriPartial.Authority);
            request.Headers.Referrer = new Uri(origin + "/");
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.TryAddWithoutValidation("X-Requested-From", "ClassIslandPlugin");
        }

        private static string TruncateForLog(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }

        private static string? TryGetNestedString(JsonElement root, string objectName, string propertyName)
        {
            return root.TryGetProperty(objectName, out var nested)
                ? TryGetString(nested, propertyName)
                : null;
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static double TryGetNumber(JsonElement element, string propertyName)
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

        private static void EnsureSuccessfulApiResponse(JsonElement root, string sourceName)
        {
            if (!root.TryGetProperty("code", out var code))
            {
                return;
            }

            var isSuccessful = code.ValueKind switch
            {
                JsonValueKind.Number when code.TryGetInt32(out var number) => number == 200,
                JsonValueKind.String => string.Equals(code.GetString(), "200", StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (!isSuccessful)
            {
                throw new HttpRequestException($"{sourceName}接口返回异常状态：{code}");
            }
        }

        private static TimeSpan TryReadDuration(JsonElement element)
        {
            var timeMs = TryGetNumber(element, "time");
            if (timeMs > 0)
            {
                return TimeSpan.FromMilliseconds(timeMs);
            }

            var duration = TryGetNumber(element, "duration");
            if (duration <= 0)
            {
                duration = TryGetNumber(element, "interval");
            }

            if (duration <= 0)
            {
                return TimeSpan.Zero;
            }

            return duration > 10_000 ? TimeSpan.FromMilliseconds(duration) : TimeSpan.FromSeconds(duration);
        }

        private static bool IsKnownInvalidTencentAudioUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var normalized = url.Trim().Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            var urlWithoutParams = normalized.Split('?', '#')[0];
            return urlWithoutParams.EndsWith(InvalidTencentAudioUrlSuffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInvalidTencentAudioDuration(TimeSpan duration, TimeSpan expectedDuration)
        {
            return expectedDuration >= TimeSpan.FromSeconds(MinValidTencentAudioDurationSeconds) &&
                   duration > TimeSpan.Zero &&
                   duration < TimeSpan.FromSeconds(MinValidTencentAudioDurationSeconds);
        }

        private static bool IsDurationTrusted(TimeSpan duration, TimeSpan lyricDuration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return false;
            }

            if (lyricDuration <= TimeSpan.Zero)
            {
                return true;
            }

            var tolerance = TimeSpan.FromMilliseconds(Math.Max(
                MetadataLyricDurationTolerance.TotalMilliseconds,
                lyricDuration.TotalMilliseconds * 0.15));
            return duration + tolerance >= lyricDuration;
        }

        private static string ComputeMd5Hex(string value)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string GetNeteaseCookieFingerprint(string? cookie)
        {
            return string.IsNullOrWhiteSpace(cookie) ? string.Empty : ComputeMd5Hex(cookie.Trim());
        }

        private async Task<long> TryGetAudioContentLengthAsync(string audioUrl, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, audioUrl);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.Content.Headers.ContentLength is long contentLength && contentLength > 0)
                {
                    return contentLength;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "VoiceHub 音频 HEAD 长度探测失败，尝试 Range：url={AudioUrl}", audioUrl);
                // 有些音源不支持 HEAD，继续尝试 Range 请求。
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, audioUrl);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                return response.Content.Headers.ContentRange?.Length
                       ?? response.Content.Headers.ContentLength
                       ?? 0;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "VoiceHub 音频 Range 长度探测失败：url={AudioUrl}", audioUrl);
                return 0;
            }
        }

        private async Task<double> TryReadMp3BitrateAsync(string audioUrl, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, audioUrl);
                request.Headers.Range = new RangeHeaderValue(0, 65535);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                return TryReadMp3Bitrate(bytes);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "VoiceHub MP3 比特率探测失败：url={AudioUrl}", audioUrl);
                return 0;
            }
        }

        private static double TryReadMp3Bitrate(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length - 3; i++)
            {
                if (bytes[i] != 0xFF || (bytes[i + 1] & 0xE0) != 0xE0)
                {
                    continue;
                }

                var versionBits = (bytes[i + 1] >> 3) & 0x03;
                var layerBits = (bytes[i + 1] >> 1) & 0x03;
                var bitrateIndex = (bytes[i + 2] >> 4) & 0x0F;
                if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15)
                {
                    continue;
                }

                var kbps = GetMp3BitrateKbps(versionBits, layerBits, bitrateIndex);
                if (kbps > 0)
                {
                    return kbps * 1000d;
                }
            }

            return 0;
        }

        private static int GetMp3BitrateKbps(int versionBits, int layerBits, int bitrateIndex)
        {
            int[] mpeg1Layer1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 };
            int[] mpeg1Layer2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };
            int[] mpeg1Layer3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            int[] mpeg2Layer1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 };
            int[] mpeg2Layer23 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

            return versionBits == 3
                ? layerBits switch
                {
                    3 => mpeg1Layer1[bitrateIndex],
                    2 => mpeg1Layer2[bitrateIndex],
                    1 => mpeg1Layer3[bitrateIndex],
                    _ => 0
                }
                : layerBits == 3
                    ? mpeg2Layer1[bitrateIndex]
                    : mpeg2Layer23[bitrateIndex];
        }

        private static List<LyricLineItem> ParseBestLyrics(LyricPayload payload)
        {
            if (!string.IsNullOrWhiteSpace(payload.Ttml))
            {
                var ttmlLines = ParseTtml(payload.Ttml);
                if (ttmlLines.Count > 0)
                {
                    return ttmlLines;
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Yrc))
            {
                var qrcLines = ParseQrc(payload.Yrc);
                if (qrcLines.Count > 0)
                {
                    return qrcLines;
                }

                var yrcLines = ParseSmartLrc(payload.Yrc);
                if (yrcLines.Count > 0)
                {
                    return yrcLines;
                }
            }

            return ParseSmartLrc(payload.Lrc);
        }

        private static List<LyricLineItem> ParseSmartLrc(string? lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc))
            {
                return new List<LyricLineItem>();
            }

            if (lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Any(line => EnhancedWordRegex.IsMatch(line)))
            {
                var enhancedLines = ParseEnhancedLrc(lrc);
                if (enhancedLines.Count > 0)
                {
                    return enhancedLines;
                }
            }

            if (lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Any(line => LrcTimeRegex.Matches(line).Count > 1))
            {
                var wordByWordLines = ParseWordByWordLrc(lrc);
                if (wordByWordLines.Count > 0)
                {
                    return wordByWordLines;
                }
            }

            return ParseLineLrc(lrc);
        }

        private static List<LyricLineItem> ParseLineLrc(string? lrc)
        {
            var result = new List<LyricLineItem>();
            if (string.IsNullOrWhiteSpace(lrc))
            {
                return result;
            }

            foreach (var rawLine in lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var matches = LrcTimeRegex.Matches(line);
                if (matches.Count == 0)
                {
                    continue;
                }

                var text = LrcTimeRegex.Replace(line, string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                foreach (Match match in matches)
                {
                    result.Add(new LyricLineItem
                    {
                        Start = ParseLrcTimestamp(match),
                        Text = text
                    });
                }
            }

            result = result.OrderBy(line => line.Start).ToList();
            for (var i = 0; i < result.Count; i++)
            {
                result[i].End = i + 1 < result.Count
                    ? result[i + 1].Start
                    : result[i].Start + TimeSpan.FromSeconds(5);
            }

            return result;
        }

        private static List<LyricLineItem> ParseWordByWordLrc(string lrc)
        {
            var result = new List<LyricLineItem>();
            foreach (var rawLine in lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var matches = LrcTimeRegex.Matches(line).Cast<Match>().ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                var segments = new List<(TimeSpan Start, string Text)>();
                for (var i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    var contentStart = match.Index + match.Length;
                    var contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
                    var text = line[contentStart..contentEnd];
                    if (!string.IsNullOrEmpty(text))
                    {
                        segments.Add((ParseLrcTimestamp(match), text));
                    }
                }

                if (segments.Count == 0)
                {
                    continue;
                }

                var lineText = string.Concat(segments.Select(segment => segment.Text)).Trim();
                if (string.IsNullOrEmpty(lineText))
                {
                    continue;
                }

                result.Add(new LyricLineItem
                {
                    Start = segments.Min(segment => segment.Start),
                    End = segments.Last().Start + TimeSpan.FromSeconds(1),
                    Text = lineText
                });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        private static List<LyricLineItem> ParseEnhancedLrc(string lrc)
        {
            var result = new List<LyricLineItem>();
            foreach (var rawLine in lrc.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var lineMatch = LrcTimeRegex.Match(line);
                if (!lineMatch.Success)
                {
                    continue;
                }

                var contentAfterLineTime = line[(lineMatch.Index + lineMatch.Length)..];
                var wordMatches = EnhancedWordRegex.Matches(contentAfterLineTime).Cast<Match>().ToList();
                var text = wordMatches.Count > 0
                    ? string.Concat(wordMatches.Select(match => match.Groups[4].Value)).Trim()
                    : EnhancedWordRegex.Replace(contentAfterLineTime, string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var start = ParseLrcTimestamp(lineMatch);
                var end = wordMatches.Count > 0
                    ? ParseEnhancedTimestamp(wordMatches.Last()) + TimeSpan.FromSeconds(1)
                    : start + TimeSpan.FromSeconds(1);

                result.Add(new LyricLineItem { Start = start, End = end, Text = text });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        private static List<LyricLineItem> ParseQrc(string? content)
        {
            var result = new List<LyricLineItem>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            var rawContent = ExtractQrcLyricContent(content) ?? content;
            foreach (var rawLine in rawContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || LrcMetaRegex.IsMatch(line))
                {
                    continue;
                }

                var lineMatch = QrcLineRegex.Match(line);
                if (!lineMatch.Success)
                {
                    continue;
                }

                var startMs = long.Parse(lineMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var durationMs = long.Parse(lineMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var lineContent = lineMatch.Groups[3].Value;
                var words = QrcWordRegex.Matches(lineContent)
                    .Cast<Match>()
                    .Select(match => match.Groups[1].Value)
                    .Where(word => !string.IsNullOrEmpty(word));
                var text = string.Concat(words).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                result.Add(new LyricLineItem
                {
                    Start = TimeSpan.FromMilliseconds(startMs),
                    End = TimeSpan.FromMilliseconds(startMs + durationMs),
                    Text = text
                });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        private static List<LyricLineItem> ParseTtml(string? content)
        {
            var result = new List<LyricLineItem>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            foreach (Match match in TtmlLineRegex.Matches(content))
            {
                var text = DecodeXmlText(XmlTagRegex.Replace(match.Groups[4].Value, string.Empty)).Trim();
                if (string.IsNullOrWhiteSpace(text) || !TryParseTtmlTime(match.Groups[1].Value, out var start))
                {
                    continue;
                }

                TimeSpan end;
                if (match.Groups[2].Success && TryParseTtmlTime(match.Groups[2].Value, out var explicitEnd))
                {
                    end = explicitEnd;
                }
                else if (match.Groups[3].Success && TryParseTtmlTime(match.Groups[3].Value, out var duration))
                {
                    end = start + duration;
                }
                else
                {
                    end = start + TimeSpan.FromSeconds(5);
                }

                result.Add(new LyricLineItem { Start = start, End = end, Text = text });
            }

            CompleteLineEndTimes(result);
            return result;
        }

        private static TimeSpan ParseLrcTimestamp(Match match)
        {
            var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var millisecondsText = match.Groups[3].Success ? match.Groups[3].Value : "0";
            var normalizedMilliseconds = millisecondsText.PadRight(3, '0')[..3];
            var milliseconds = int.Parse(normalizedMilliseconds, CultureInfo.InvariantCulture);
            return TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1_000 + milliseconds);
        }

        private static TimeSpan ParseEnhancedTimestamp(Match match)
        {
            var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var millisecondsText = match.Groups[3].Success ? match.Groups[3].Value : "0";
            var normalizedMilliseconds = millisecondsText.PadRight(3, '0')[..3];
            var milliseconds = int.Parse(normalizedMilliseconds, CultureInfo.InvariantCulture);
            return TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1_000 + milliseconds);
        }

        private static void CompleteLineEndTimes(List<LyricLineItem> lines)
        {
            lines.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var i = 0; i < lines.Count; i++)
            {
                var nextLine = i + 1 < lines.Count ? lines[i + 1] : null;
                if (nextLine != null && (lines[i].End <= lines[i].Start || lines[i].End > nextLine.Start))
                {
                    lines[i].End = nextLine.Start;
                }
                else if (lines[i].End <= lines[i].Start)
                {
                    lines[i].End = lines[i].Start + TimeSpan.FromSeconds(5);
                }
            }
        }

        private static string? ExtractQrcLyricContent(string rawContent)
        {
            var match = Regex.Match(rawContent, "LyricContent=\"([\\s\\S]*?)\"", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            return DecodeXmlText(match.Groups[1].Value)
                .Replace("\\n", "\n")
                .Replace("\\r", "\r");
        }

        private static bool TryParseTtmlTime(string value, out TimeSpan time)
        {
            value = value.Trim();
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time))
            {
                return true;
            }

            if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            {
                time = TimeSpan.FromMilliseconds(milliseconds);
                return true;
            }

            if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                time = TimeSpan.FromSeconds(seconds);
                return true;
            }

            time = TimeSpan.Zero;
            return false;
        }

        private static string DecodeXmlText(string value)
        {
            return value
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");
        }

        private static void AlignTranslations(List<LyricLineItem> lyrics, IReadOnlyList<LyricLineItem> translations)
        {
            var i = 0;
            var j = 0;
            var tolerance = TimeSpan.FromMilliseconds(300);

            while (i < lyrics.Count && j < translations.Count)
            {
                var diff = lyrics[i].Start - translations[j].Start;
                if (diff.Duration() <= tolerance)
                {
                    lyrics[i].Translation = translations[j].Text;
                    i++;
                    j++;
                }
                else if (diff < TimeSpan.Zero)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }
        }

        private static TimeSpan GetEstimatedDuration(IReadOnlyList<LyricLineItem> lyrics, TimeSpan fallback)
        {
            if (lyrics.Count == 0)
            {
                return fallback;
            }

            var lastLineEnd = lyrics.Max(line => line.End);
            return lastLineEnd > TimeSpan.FromSeconds(30) ? lastLineEnd + TimeSpan.FromSeconds(3) : fallback;
        }

        private void UpdateLyricDisplay()
        {
            if (_currentState != ComponentState.Loaded)
            {
                return;
            }

            ContentText = Settings.EnableLyrics ? GetLyricDisplayText(DateTime.Now) : _scheduleSummaryText;
        }

        private string GetLyricDisplayText(DateTime now)
        {
            List<ScheduledSongPlayback> plan;
            DateTime playbackDate;
            lock (_playbackLock)
            {
                plan = _playbackPlan.ToList();
                playbackDate = _playbackDate;
            }

            if (plan.Count == 0 || playbackDate == DateTime.MinValue)
            {
                return _scheduleSummaryText;
            }

            var startTime = ResolveBroadcastStartTime(plan);
            var firstSongStart = playbackDate.Date + startTime;
            if (now < firstSongStart)
            {
                return _scheduleSummaryText;
            }

            var elapsed = now - firstSongStart;
            var cursor = TimeSpan.Zero;

            foreach (var entry in plan)
            {
                if (!entry.HasReliableDuration)
                {
                    return _scheduleSummaryText;
                }

                var start = cursor;
                var end = cursor + entry.Duration;
                if (elapsed >= start && elapsed < end)
                {
                    return BuildCurrentLyricText(entry, elapsed - start);
                }

                cursor = end;
            }

            return _scheduleSummaryText;
        }

        private TimeSpan ResolveBroadcastStartTime(IReadOnlyList<ScheduledSongPlayback> plan)
        {
            if (TryParseClockTime(Settings.BroadcastStartTime, out var configuredStart))
            {
                return configuredStart;
            }

            var playTimeStart = plan.FirstOrDefault()?.Item.PlayTime?.StartTime;
            if (TryParseClockTime(playTimeStart, out var scheduleStart))
            {
                return scheduleStart;
            }

            return TimeSpan.Zero;
        }

        private static bool TryParseClockTime(string? value, out TimeSpan time)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                TimeSpan.TryParseExact(
                    value.Trim(),
                    new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" },
                    CultureInfo.InvariantCulture,
                    out time))
            {
                return true;
            }

            time = TimeSpan.Zero;
            return false;
        }

        private string BuildCurrentLyricText(ScheduledSongPlayback entry, TimeSpan songPosition)
        {
            var song = entry.Item.Song;
            var prefix = $"♪ #{entry.Item.Sequence} {song.Artist} - {song.Title}";

            if (entry.Lyrics.Count == 0)
            {
                return entry.LyricStatus == "loading"
                    ? $"{prefix} | 歌词加载中"
                    : $"{prefix} | 暂无歌词{BuildDurationStatusSuffix(entry)}";
            }

            var currentLine = entry.Lyrics.LastOrDefault(line => songPosition >= line.Start);
            if (currentLine == null)
            {
                return $"{prefix} | 前奏";
            }

            var lyricText = currentLine.Text;
            if (!string.IsNullOrWhiteSpace(currentLine.Translation))
            {
                lyricText += $" / {currentLine.Translation}";
            }

            return $"{prefix} | {lyricText}{BuildDurationStatusSuffix(entry)}";
        }

        private static string BuildDurationStatusSuffix(ScheduledSongPlayback entry)
        {
            return entry.DurationSource == "fallback" ? "（时长估算）" : string.Empty;
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
            if (!await _refreshLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                await LoadVoiceHubDataAsync();
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private sealed record LyricPayload(string? Lrc, string? Translation, string? Yrc, string? Ttml)
        {
            public static LyricPayload Empty { get; } = new(null, null, null, null);
        }

        private sealed record ResolvedPlayback(
            TimeSpan Duration,
            string DurationSource,
            List<LyricLineItem> Lyrics,
            string LyricStatus);

        private sealed record DurationProbe(TimeSpan Duration, string Source)
        {
            public static readonly DurationProbe Empty = new(TimeSpan.Zero, string.Empty);
        }

        private sealed class DailyLyricCache
        {
            public int Version { get; set; } = LyricCacheVersion;
            public string Date { get; set; } = string.Empty;
            public string ScheduleSignature { get; set; } = string.Empty;
            public bool HasNeteaseCookie { get; set; }
            public string NeteaseCookieFingerprint { get; set; } = string.Empty;
            public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CacheEntry
        {
            public string CacheKey { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public double DurationMs { get; set; }
            public string DurationSource { get; set; } = "fallback";
            public string LyricStatus { get; set; } = "no-lyrics";
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
                    UpdatedAt = DateTime.Now,
                    Lyrics = playback.Lyrics.Select(CachedLyricLine.FromLyricLineItem).ToList()
                };
            }
        }

        private sealed class CachedLyricLine
        {
            public double StartMs { get; set; }
            public double EndMs { get; set; }
            public string Text { get; set; } = string.Empty;
            public string? Translation { get; set; }

            public static CachedLyricLine FromLyricLineItem(LyricLineItem line)
            {
                return new CachedLyricLine
                {
                    StartMs = line.Start.TotalMilliseconds,
                    EndMs = line.End.TotalMilliseconds,
                    Text = line.Text,
                    Translation = line.Translation
                };
            }

            public LyricLineItem ToLyricLineItem()
            {
                return new LyricLineItem
                {
                    Start = TimeSpan.FromMilliseconds(StartMs),
                    End = TimeSpan.FromMilliseconds(EndMs),
                    Text = Text,
                    Translation = Translation
                };
            }
        }

        private sealed class ScheduledSongPlayback
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
            public bool HasReliableDuration => Duration > TimeSpan.Zero && DurationSource != "unresolved";
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
