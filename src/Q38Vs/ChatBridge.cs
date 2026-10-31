using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Wpf;

namespace Q38Vs
{
    /// <summary>
    /// 聊天页 ↔ q38 的翻译层。页面只认这些消息（均为 JSON 文本 postMessage）：
    ///  页 → 宿主：ready / {type:"send",text,content_parts?} / {type:"abort"} / {type:"permitDecision",id,decision} / {type:"getContext"} / {type:"openFile",path,line}
    ///  宿主 → 页：status / state / hello / event / history / permit / context / toast / error
    /// 不做任何 agent 逻辑，只是壳 + 翻译器。
    /// </summary>
    public sealed class ChatBridge : IDisposable
    {
        private readonly WebView2 _web;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly Q38ProcessManager _runtime = new();
        private readonly CancellationTokenSource _cts = new();
        private Q38Client? _client;
        private bool _busy;
        private bool _booted;

        public ChatBridge(WebView2 web)
        {
            _web = web;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        }

        // ---------- 页 → 宿主 ----------

        public void HandleFromPage(string? json)
        {
            if (string.IsNullOrEmpty(json)) return;
            JsonNode? node;
            try { node = JsonNode.Parse(json); } catch { return; }
            var type = node?["type"]?.GetValue<string>();
            if (type == null) return;

            switch (type)
            {
                case "ready":
                    if (!_booted) { _booted = true; Task.Run(() => SafeAsync(BootAsync)); }
                    break;
                case "send":
                    var text = node["text"]?.GetValue<string>() ?? "";
                    var parts = node["content_parts"] as JsonArray;
                    if (text.Length > 0) Task.Run(() => SafeAsync(() => SendAsync(text, parts)));
                    break;
                case "abort":
                    Task.Run(() => SafeAsync(() => _client?.RpcAsync("turn.abort", null, 5000)));
                    break;
                case "permitDecision":
                    var id = node["id"]?.GetValue<long>() ?? -1;
                    var decision = node["decision"]?.GetValue<string>() ?? "deny";
                    Task.Run(() => SafeAsync(() => _client?.PermitAsync(id, decision)));
                    break;
                case "getContext":
                    var ctx = _dispatcher.Invoke(() => VsContext.ActiveContext());
                    Post(new JsonObject { ["type"] = "context", ["context"] = ctx });
                    break;
                case "openFile":
                    var path = node["path"]?.GetValue<string>() ?? "";
                    var line = (int)(node["line"]?.GetValue<long>() ?? 0);
                    if (path.Length > 0) Task.Run(() => OpenFileInVs(path, line));
                    break;
            }
        }

        // ---------- 启动 ----------

        private async Task BootAsync()
        {
            PostStatus("connecting", "正在连接 q38 …");
            var log = new Progress<string>(s => PostStatus("log", s));
            await _runtime.StartAsync(log).ConfigureAwait(false);
            _client = new Q38Client(_runtime.Port);

            await EnsureWorkspaceAsync().ConfigureAwait(false);
            await _client.RunEventsAsync(
                (method, parameters) => _dispatcher.BeginInvoke(() => HandleServerMessage(method, parameters)),
                up => _dispatcher.BeginInvoke(() => PostStatus(up ? "up" : "down", up ? null : "连接断开，1.5 秒后重连…")),
                _cts.Token).ConfigureAwait(false);
        }

        /// <summary>打开的 Solution 目录 → q38 workspace。回合进行中会被拒，静默忽略（保留原工作区）。</summary>
        private async Task EnsureWorkspaceAsync()
        {
            try
            {
                var state = await _client!.GetStateAsync().ConfigureAwait(false);
                var current = state?["workspace"]?.GetValue<string>();
                var target = _dispatcher.Invoke(() => VsContext.SolutionDirectory());
                // GetSolutionInfo 可能返回 \\?\ 前缀的扩展路径形态；q38 侧存的是普通路径，先规范化。
                if (target != null && target.StartsWith(@"\\?\")) target = target.Substring(4);
                if (string.IsNullOrEmpty(target) || current == target) return;
                await _client.SetWorkspaceAsync(target).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PostStatus("log", "工作区沿用 q38 配置：" + ex.Message);
            }
        }

        private async Task SendAsync(string text, JsonArray? contentParts)
        {
            if (_client == null) return;
            try
            {
                // turn.start 阻塞到整轮结束；期间流式增量全走 WS。忙时改排队。
                // content_parts（＠ 附加的上下文）turn.start / turn.queue 两条路都透传。
                var method = _busy ? "turn.queue" : "turn.start";
                object parameters = contentParts is { Count: > 0 }
                    ? new { text, content_parts = contentParts }
                    : new { text };
                await _client.RpcAsync(method, parameters, -1, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PostError(ex.Message);
            }
        }

        /// <summary>工具轨迹里的路径点击：UI 线程走 DTE 打开（相对路径以解决方案目录解析），
        /// 失败以 toast 回发页面，不抛异常打断。</summary>
        private void OpenFileInVs(string path, int line)
        {
            try
            {
                var error = _dispatcher.Invoke(() =>
                {
                    try { VsContext.OpenDocument(path, line); return null; }
                    catch (Exception ex) { return ex.Message; }
                });
                if (error != null) PostToast("无法打开 " + path + "：" + error);
            }
            catch (Exception ex)
            {
                PostToast("无法打开 " + path + "：" + ex.Message);
            }
        }

        // ---------- 服务端 → 页 ----------

        private void HandleServerMessage(string method, JsonElement? parameters)
        {
            switch (method)
            {
                case "hello":
                    {
                        var p = parameters ?? default;
                        var state = p.TryGetProperty("state", out var sm) && sm.ValueKind == JsonValueKind.Object
                            ? JsonNode.Parse(sm.GetRawText()) : null;
                        var events = p.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array
                            ? JsonNode.Parse(evs.GetRawText()) : new JsonArray();
                        var permit = p.TryGetProperty("permit", out var pm) && pm.ValueKind == JsonValueKind.Object
                            ? JsonNode.Parse(pm.GetRawText()) : null;
                        Post(new JsonObject { ["type"] = "hello", ["state"] = state, ["events"] = events, ["permit"] = permit });
                        break;
                    }
                case "event.append":
                    Post(new JsonObject { ["type"] = "event", ["event"] = JsonNode.Parse(parameters!.Value.GetRawText()) });
                    break;
                case "history.replace":
                    Post(new JsonObject
                    {
                        ["type"] = "history",
                        ["events"] = JsonNode.Parse(parameters!.Value.TryGetProperty("events", out var hevs) ? hevs.GetRawText() : "[]"),
                    });
                    break;
                case "state":
                    {
                        var st = JsonNode.Parse(parameters!.Value.GetRawText());
                        _busy = st?["busy"]?.GetValue<string>() != null || (st?["turn_in_flight"]?.GetValue<bool>() ?? false);
                        Post(new JsonObject { ["type"] = "state", ["state"] = st });
                        break;
                    }
                case "permit.ask":
                    Post(new JsonObject { ["type"] = "permit", ["permit"] = JsonNode.Parse(parameters!.Value.GetRawText()) });
                    break;
            }
        }

        // ---------- 小工具 ----------

        private async Task SafeAsync(Func<Task> body)
        {
            try { await body().ConfigureAwait(false); }
            catch (Exception ex) { PostError(ex.Message); }
        }

        private void PostStatus(string kind, string? detail)
        {
            var msg = new JsonObject { ["type"] = "status", ["kind"] = kind };
            if (detail != null) msg["detail"] = detail;
            Post(msg);
        }

        private void PostError(string message) => Post(new JsonObject { ["type"] = "error", ["message"] = message });

        private void PostToast(string message) => Post(new JsonObject { ["type"] = "toast", ["message"] = message });

        private void Post(JsonObject message)
        {
            // CoreWebView2 线程仿单：序列化在本线程做完，回 UI 线程再 Post。
            var json = message.ToJsonString();
            _dispatcher.BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsJson(json));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _runtime.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>VS 侧上下文：活动文档路径/选区、打开的 Solution 目录。必须在 UI 线程调用。</summary>
    internal static class VsContext
    {
        public static string SolutionDirectory()
        {
            try
            {
                var sol = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(
                    typeof(Microsoft.VisualStudio.Shell.Interop.SVsSolution)) as Microsoft.VisualStudio.Shell.Interop.IVsSolution;
                string dir = "";
                sol?.GetSolutionInfo(out dir, out _, out _);
                return dir;
            }
            catch { return ""; }
        }

        /// <summary>活动文档 + 选区的结构化上下文；无活动文档返回 null。必须在 UI 线程调用。</summary>
        public static JsonObject? ActiveContext()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(
                    typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var doc = dte?.ActiveDocument;
                if (doc == null || string.IsNullOrEmpty(doc.FullName)) return null;
                var selection = doc.Selection as EnvDTE.TextSelection;
                var text = selection?.Text ?? "";
                var hasSel = !string.IsNullOrEmpty(text);
                return new JsonObject
                {
                    ["path"] = doc.FullName,
                    ["name"] = Path.GetFileName(doc.FullName),
                    ["hasSelection"] = hasSel,
                    ["lineStart"] = hasSel ? selection!.TopPoint.Line : 0,
                    ["lineEnd"] = hasSel ? selection!.BottomPoint.Line : 0,
                    ["text"] = hasSel ? text : "",
                };
            }
            catch { return null; }
        }

        /// <summary>DTE 打开文件（相对路径以解决方案目录解析），line&gt;0 时跳到该行。
        /// EnvDTE.ItemOperations 没有 OpenDocumentViaPath 成员，打开走 OpenFile；
        /// 必须在 UI 线程调用；失败抛异常，由调用方转成 toast。</summary>
        public static void OpenDocument(string path, int line)
        {
            var dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(
                typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte?.ItemOperations == null) throw new InvalidOperationException("VS 编辑器服务不可用");
            if (!Path.IsPathRooted(path))
            {
                var dir = SolutionDirectory();
                if (!string.IsNullOrEmpty(dir)) path = Path.GetFullPath(Path.Combine(dir, path));
            }
            var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindCode);
            window?.Activate();
            if (line > 0)
            {
                var sel = window?.Document?.Selection as EnvDTE.TextSelection;
                sel?.GotoLine(line, true);
            }
        }
    }
}
