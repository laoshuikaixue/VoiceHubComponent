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