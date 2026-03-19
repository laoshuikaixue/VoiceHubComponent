using System;
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

            Settings.ApiUrl = apiUrl;
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

                Settings.ApiUrl = apiUrl;
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
            ApiUrlTextBox.Text = DefaultApiUrl;
            this.ShowSuccessToast("已重置为默认API地址");
        }
    }
}
