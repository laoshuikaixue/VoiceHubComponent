using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClassIsland.Shared.Helpers;

namespace VoiceHubComponent.Models
{
    /// <summary>
    /// VoiceHub插件设置模型
    /// </summary>
    public class VoiceHubSettings : INotifyPropertyChanged
    {
        private string _apiUrl = "https://voicehub.lao-shui.top/api/songs/public";
        private bool _enableLyrics = false;
        private string _broadcastStartTime = "12:20:00";
        private string _neteaseCookie = string.Empty;
        private bool _useDebugScheduleDate = false;
        private DateTime _debugScheduleDate = DateTime.Today;
        private bool _showCover = true;
        private bool _showTranslation = true;
        private bool _showRomanization = false;
        private bool _wordByWord = true;
        private bool _enableLyricUpgrade = true;
        private bool _isLoaded = false;
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClassIsland", "Plugins", "VoiceHubComponent", "settings.json");

        /// <summary>
        /// API地址
        /// </summary>
        public string ApiUrl
        {
            get
            {
                EnsureLoaded();
                return _apiUrl;
            }
            set
            {
                EnsureLoaded();
                if (_apiUrl != value)
                {
                    _apiUrl = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否按排期时间显示歌词。
        /// </summary>
        public bool EnableLyrics
        {
            get
            {
                EnsureLoaded();
                return _enableLyrics;
            }
            set
            {
                EnsureLoaded();
                if (_enableLyrics != value)
                {
                    _enableLyrics = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 当天第一首歌的固定开始播放时间，格式 HH:mm 或 HH:mm:ss。
        /// </summary>
        public string BroadcastStartTime
        {
            get
            {
                EnsureLoaded();
                return _broadcastStartTime;
            }
            set
            {
                EnsureLoaded();
                var normalized = string.IsNullOrWhiteSpace(value) ? "12:20:00" : value.Trim();
                if (_broadcastStartTime != normalized)
                {
                    _broadcastStartTime = normalized;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 网易云音乐 Cookie，用于获取更完整的播放地址和时长信息。
        /// </summary>
        public string NeteaseCookie
        {
            get
            {
                EnsureLoaded();
                return _neteaseCookie;
            }
            set
            {
                EnsureLoaded();
                var normalized = value?.Trim() ?? string.Empty;
                if (_neteaseCookie != normalized)
                {
                    _neteaseCookie = normalized;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否使用调试排期日期。
        /// </summary>
        public bool UseDebugScheduleDate
        {
            get
            {
                EnsureLoaded();
                return _useDebugScheduleDate;
            }
            set
            {
                EnsureLoaded();
                if (_useDebugScheduleDate != value)
                {
                    _useDebugScheduleDate = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 调试排期日期。
        /// </summary>
        public DateTime DebugScheduleDate
        {
            get
            {
                EnsureLoaded();
                return _debugScheduleDate;
            }
            set
            {
                EnsureLoaded();
                var normalized = value.Date;
                if (_debugScheduleDate != normalized)
                {
                    _debugScheduleDate = normalized;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否显示歌曲封面。
        /// </summary>
        public bool ShowCover
        {
            get
            {
                EnsureLoaded();
                return _showCover;
            }
            set
            {
                EnsureLoaded();
                if (_showCover != value)
                {
                    _showCover = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否显示歌词翻译。
        /// </summary>
        public bool ShowTranslation
        {
            get
            {
                EnsureLoaded();
                return _showTranslation;
            }
            set
            {
                EnsureLoaded();
                if (_showTranslation != value)
                {
                    _showTranslation = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否显示歌词罗马音。
        /// </summary>
        public bool ShowRomanization
        {
            get
            {
                EnsureLoaded();
                return _showRomanization;
            }
            set
            {
                EnsureLoaded();
                if (_showRomanization != value)
                {
                    _showRomanization = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否启用逐字歌词高亮。关闭后不发送歌词升级请求。
        /// </summary>
        public bool WordByWord
        {
            get
            {
                EnsureLoaded();
                return _wordByWord;
            }
            set
            {
                EnsureLoaded();
                if (_wordByWord != value)
                {
                    _wordByWord = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// 是否启用跨平台歌词升级（TTML/逐字）。仅在逐字歌词开启时生效。
        /// </summary>
        public bool EnableLyricUpgrade
        {
            get
            {
                EnsureLoaded();
                return _enableLyricUpgrade;
            }
            set
            {
                EnsureLoaded();
                if (_enableLyricUpgrade != value)
                {
                    _enableLyricUpgrade = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public VoiceHubSettings()
        {
            // 移除构造函数中的LoadSettings调用，改为延迟加载
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void EnsureLoaded()
        {
            if (!_isLoaded)
            {
                LoadSettings();
                _isLoaded = true;
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var jsonString = File.ReadAllText(SettingsPath);
                    var jsonDocument = JsonDocument.Parse(jsonString);

                    if (jsonDocument.RootElement.TryGetProperty("ApiUrl", out var apiUrlElement))
                    {
                        var apiUrl = apiUrlElement.GetString();
                        if (!string.IsNullOrEmpty(apiUrl))
                        {
                            _apiUrl = apiUrl;
                        }
                    }

                    if (jsonDocument.RootElement.TryGetProperty("EnableLyrics", out var enableLyricsElement))
                    {
                        _enableLyrics = enableLyricsElement.GetBoolean();
                    }

                    if (jsonDocument.RootElement.TryGetProperty("BroadcastStartTime", out var startTimeElement))
                    {
                        var startTime = startTimeElement.GetString();
                        if (!string.IsNullOrWhiteSpace(startTime))
                        {
                            _broadcastStartTime = startTime.Trim();
                        }
                    }

                    if (jsonDocument.RootElement.TryGetProperty("NeteaseCookie", out var cookieElement))
                    {
                        _neteaseCookie = cookieElement.GetString()?.Trim() ?? string.Empty;
                    }

                    if (jsonDocument.RootElement.TryGetProperty("UseDebugScheduleDate", out var useDebugScheduleDateElement))
                    {
                        _useDebugScheduleDate = useDebugScheduleDateElement.GetBoolean();
                    }

                    if (jsonDocument.RootElement.TryGetProperty("DebugScheduleDate", out var debugScheduleDateElement))
                    {
                        if (debugScheduleDateElement.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(debugScheduleDateElement.GetString(), out var debugScheduleDate))
                        {
                            _debugScheduleDate = debugScheduleDate.Date;
                        }
                    }

                    if (jsonDocument.RootElement.TryGetProperty("ShowCover", out var showCoverElement))
                    {
                        _showCover = showCoverElement.GetBoolean();
                    }

                    if (jsonDocument.RootElement.TryGetProperty("ShowTranslation", out var showTranslationElement))
                    {
                        _showTranslation = showTranslationElement.GetBoolean();
                    }

                    if (jsonDocument.RootElement.TryGetProperty("ShowRomanization", out var showRomanizationElement))
                    {
                        _showRomanization = showRomanizationElement.GetBoolean();
                    }

                    if (jsonDocument.RootElement.TryGetProperty("WordByWord", out var wordByWordElement))
                    {
                        _wordByWord = wordByWordElement.GetBoolean();
                    }

                    if (jsonDocument.RootElement.TryGetProperty("EnableLyricUpgrade", out var enableLyricUpgradeElement))
                    {
                        _enableLyricUpgrade = enableLyricUpgradeElement.GetBoolean();
                    }
                }
            }
            catch
            {
                // 如果加载失败，使用默认值
            }
        }

        private void SaveSettings()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                ConfigureFileHelper.SaveConfig(SettingsPath, this);
            }
            catch
            {
                // 忽略保存错误
            }
        }
    }
}
