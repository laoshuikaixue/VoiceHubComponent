using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VoiceHubComponent.Models;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Helpers.UI;
using FluentAvalonia.UI.Controls;

namespace VoiceHubComponent.Views
{
    public partial class VoiceHubSettingsView : ComponentBase<VoiceHubSettings>
    {
        private const string DefaultApiUrl = "https://voicehub.lao-shui.top/api/songs/public";
        private readonly HttpClient _httpClient = new HttpClient();

        public VoiceHubSettingsView()
        {
            InitializeComponent();
        }

        public VoiceHubSettingsView(VoiceHubSettings settings)
        {
            InitializeComponent();
            DataContext = settings;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var apiUrl = ApiUrlTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(apiUrl))
            {
                this.ShowWarningToast("请输入API地址");
                return;
            }

            if (!TryReadLyricsSettings(out var startTime))
            {
                return;
            }

            Settings.ApiUrl = apiUrl;
            Settings.EnableLyrics = EnableLyricsCheckBox.IsChecked == true;
            Settings.BroadcastStartTime = startTime;
            Settings.NeteaseCookie = NeteaseCookieTextBox.Text?.Trim() ?? string.Empty;
            this.ShowSuccessToast("配置已保存");
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var originalContent = button.Content;
            button.Content = "刷新中...";
            button.IsEnabled = false;

            try
            {
                var apiUrl = ApiUrlTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(apiUrl))
                {
                    this.ShowWarningToast("请输入API地址");
                    return;
                }

                if (!TryReadLyricsSettings(out var startTime))
                {
                    return;
                }

                Settings.ApiUrl = apiUrl;
                Settings.EnableLyrics = EnableLyricsCheckBox.IsChecked == true;
                Settings.BroadcastStartTime = startTime;
                Settings.NeteaseCookie = NeteaseCookieTextBox.Text?.Trim() ?? string.Empty;
                await VoiceHubControl.RequestManualRefreshAsync();
                this.ShowSuccessToast("配置已生效，组件已刷新");
            }
            catch (Exception ex)
            {
                this.ShowErrorToast($"刷新失败：{ex.Message}");
            }
            finally
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }

        private async void ForceLyricsRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var originalContent = button.Content;
            button.Content = "重新获取中...";
            button.IsEnabled = false;

            try
            {
                var apiUrl = ApiUrlTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(apiUrl))
                {
                    this.ShowWarningToast("请输入API地址");
                    return;
                }

                if (!TryReadLyricsSettings(out var startTime))
                {
                    return;
                }

                Settings.ApiUrl = apiUrl;
                Settings.EnableLyrics = EnableLyricsCheckBox.IsChecked == true;
                Settings.BroadcastStartTime = startTime;
                Settings.NeteaseCookie = NeteaseCookieTextBox.Text?.Trim() ?? string.Empty;
                VoiceHubControl.ClearLyricCache();
                await VoiceHubControl.RequestManualRefreshAsync();
                this.ShowSuccessToast("歌词缓存已清除，正在重新获取");
            }
            catch (Exception ex)
            {
                this.ShowErrorToast($"重新获取失败：{ex.Message}");
            }
            finally
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }

        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var originalContent = button.Content;
            button.Content = "测试中...";
            button.IsEnabled = false;

            try
            {
                var apiUrl = Settings.ApiUrl?.Trim();
                if (string.IsNullOrEmpty(apiUrl))
                {
                    this.ShowWarningToast("请输入API地址");
                    return;
                }

                // 设置超时时间
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.GetAsync(apiUrl, cts.Token);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrEmpty(content))
                    {
                        this.ShowSuccessToast("连接成功！API响应正常");
                    }
                    else
                    {
                        this.ShowWarningToast("连接成功，但API返回空数据");
                    }
                }
                else
                {
                    this.ShowErrorToast($"连接失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (TaskCanceledException)
            {
                this.ShowErrorToast("连接超时，请检查网络或API地址");
            }
            catch (HttpRequestException ex)
            {
                this.ShowErrorToast($"网络错误：{ex.Message}");
            }
            catch (Exception ex)
            {
                this.ShowErrorToast($"测试失败：{ex.Message}");
            }
            finally
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.ApiUrl = DefaultApiUrl;
            Settings.EnableLyrics = false;
            Settings.BroadcastStartTime = "12:20:00";
            Settings.NeteaseCookie = string.Empty;
            ApiUrlTextBox.Text = DefaultApiUrl;
            EnableLyricsCheckBox.IsChecked = false;
            BroadcastStartTimeTextBox.Text = "12:20:00";
            NeteaseCookieTextBox.Text = string.Empty;
            this.ShowSuccessToast("已重置为默认API地址");
        }

        private bool TryReadLyricsSettings(out string startTime)
        {
            startTime = BroadcastStartTimeTextBox.Text?.Trim() ?? string.Empty;
            if (!TimeSpan.TryParseExact(
                    startTime,
                    new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" },
                    CultureInfo.InvariantCulture,
                    out _))
            {
                this.ShowWarningToast("固定开始时间格式应为 HH:mm 或 HH:mm:ss");
                return false;
            }

            return true;
        }
    }
}
