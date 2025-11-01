using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VoiceHubComponent.Models;
using MaterialDesignThemes.Wpf;
using ClassIsland.Core.Abstractions.Controls;

namespace VoiceHubComponent.Views
{
    public partial class VoiceHubSettingsView : ComponentBase<VoiceHubSettings>
    {
        private readonly VoiceHubSettings _settings;
        private readonly HttpClient _httpClient;

        public VoiceHubSettingsView(VoiceHubSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            _httpClient = new HttpClient();
            DataContext = _settings;
        }

        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            var originalContent = button.Content;
            button.Content = "测试中...";
            button.IsEnabled = false;

            try
            {
                var apiUrl = ApiUrlTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(apiUrl))
                {
                    ShowMessage("请输入API地址", false);
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
                        ShowMessage("连接成功！API响应正常", true);
                    }
                    else
                    {
                        ShowMessage("连接成功，但API返回空数据", false);
                    }
                }
                else
                {
                    ShowMessage($"连接失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}", false);
                }
            }
            catch (TaskCanceledException)
            {
                ShowMessage("连接超时，请检查网络或API地址", false);
            }
            catch (HttpRequestException ex)
            {
                ShowMessage($"网络错误：{ex.Message}", false);
            }
            catch (Exception ex)
            {
                ShowMessage($"测试失败：{ex.Message}", false);
            }
            finally
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.ApiUrl = "https://voicehub.lao-shui.top/api/songs/public";
            ShowMessage("已重置为默认API地址", true);
        }

        private void ShowMessage(string message, bool isSuccess)
        {
            var snackbar = new Snackbar();
            var messageQueue = new SnackbarMessageQueue(TimeSpan.FromSeconds(3));
            snackbar.MessageQueue = messageQueue;
            
            // 尝试找到主窗口中的Snackbar，如果找不到就在当前控件中显示
            var mainWindow = Window.GetWindow(this);
            var existingSnackbar = mainWindow?.FindName("MainSnackbar") as Snackbar;
            
            if (existingSnackbar != null)
            {
                existingSnackbar.MessageQueue?.Enqueue(message);
            }
            else
            {
                // 如果找不到主Snackbar，就显示MessageBox
                MessageBox.Show(message, isSuccess ? "成功" : "错误", 
                    MessageBoxButton.OK, 
                    isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
    }
}