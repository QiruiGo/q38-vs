using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Q38Vs
{
    /// <summary>
    /// 工具窗内容：WebView2 承载自写聊天页。页面经虚拟主机名加载，
    /// 与宿主只用 postMessage 双向通信（协议见 ChatBridge）。
    /// </summary>
    public sealed class Q38WebViewHost : UserControl
    {
        public const string VirtualHost = "q38vs.local";

        private readonly WebView2 _web = new WebView2();
        private ChatBridge? _bridge;
        private bool _initializing;

        public Q38WebViewHost()
        {
            Content = _web;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initializing || _web.CoreWebView2 != null) return;
            _initializing = true;
            try
            {
                var userData = Path.Combine(CacheRoot(), "webview2");
                Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _web.EnsureCoreWebView2Async(env);

                var core = _web.CoreWebView2;
                var chatDir = Path.Combine(Path.GetDirectoryName(typeof(Q38WebViewHost).Assembly.Location) ?? ".", "ChatPage");
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, chatDir, CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += OnWebMessageReceived;
                core.Navigate($"https://{VirtualHost}/chat.html");
            }
            catch (Exception ex)
            {
                _initializing = false;
                Content = new TextBlock
                {
                    Text = "WebView2 初始化失败：" + ex.Message +
                           "\n请确认已安装 WebView2 Evergreen Runtime。",
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                };
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            _bridge ??= new ChatBridge(_web);
                // 页面 post 的都是 JS 对象；TryGetWebMessageAsString 对对象消息会抛错，
                // WebMessageAsJson 对字符串/对象都返回可解析的 JSON 文本。
                _bridge.HandleFromPage(e.WebMessageAsJson);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _bridge?.Dispose();
        }

        /// <summary>WebView2 用户数据目录：Q38_VS_CACHE 环境变量优先，默认落 %LOCALAPPDATA%\Q38Vs。</summary>
        internal static string CacheRoot()
        {
            var env = Environment.GetEnvironmentVariable("Q38_VS_CACHE");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Q38Vs");
        }
    }
}
