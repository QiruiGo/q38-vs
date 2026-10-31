using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Q38Vs
{
    /// <summary>
    /// q38 web 的本机 API 客户端。shape 已按上游核实：
    ///   POST /api/rpc   body {"method","params"}；结果 JSON 直接返回，失败形如 {"ok":false,"error":"…"} 或 HTTP 非 200 {"error":"…"}
    ///   GET  /api/events WS 通知 {"jsonrpc","method","params"}：hello / event.append / history.replace / state / permit.ask
    ///   POST /api/permit body {"id":number,"decision":"allow|always|deny"}
    ///   POST /api/workspace body {"path":"…"}（回合进行中会被 409 拒绝）
    /// </summary>
    public sealed class Q38Client : IDisposable
    {
        private readonly HttpClient _http;
        private readonly int _port;

        public Q38Client(int port)
        {
            _port = port;
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        }

        public int Port => _port;

        /// <summary>调 /api/rpc。turn.start 会阻塞到整轮结束，请用 timeoutMs: -1 + 自行容错。</summary>
        public async Task<JsonNode?> RpcAsync(string method, object? parameters, int timeoutMs, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs > 0) cts.CancelAfter(timeoutMs);

            var body = JsonSerializer.Serialize(new RpcBody(method, parameters));
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/rpc", content, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseRpcResult(text);
        }

        public Task<JsonNode?> RpcAsync(string method, object? parameters, CancellationToken ct = default)
            => RpcAsync(method, parameters, 15000, ct);

        public static JsonNode? ParseRpcResult(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj && obj.TryGetPropertyValue("ok", out var ok) && ok is JsonValue v && v.TryGetValue<bool>(out var b) && !b)
            {
                var err = obj["error"]?.GetValue<string>() ?? "rpc failed";
                throw new Q38ApiException(err);
            }
            return node;
        }

        public async Task<JsonObject?> GetStateAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync("api/state", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonObject;
        }

        public async Task PermitAsync(long id, string decision, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new PermitBody(id, decision));
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/permit", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new Q38ApiException(text);
            }
        }

        public async Task SetWorkspaceAsync(string path, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new WorkspaceBody(path));
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/workspace", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new Q38ApiException(text);
            }
        }

        /// <summary>
        /// 长驻 WS 循环：断线自动重连（1.5s），连上即收到 hello 全量快照。
        /// onEvent 在 WS 线程回调，参数为 (method, params)。
        /// </summary>
        public async Task RunEventsAsync(Action<string, JsonElement?> onEvent, Action<bool> onStatus, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/api/events"), ct).ConfigureAwait(false);
                    onStatus(true);
                    var buffer = new byte[64 * 1024];
                    using var message = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        message.SetLength(0);
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, r.Count);
                        } while (!r.EndOfMessage);
                        if (r.MessageType == WebSocketMessageType.Close) break;

                        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray()));
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) continue;
                        JsonElement? parameters = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
                        onEvent(m.GetString()!, parameters);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 网络抖动落到重连 */ }
                finally { ws?.Dispose(); }

                if (ct.IsCancellationRequested) break;
                onStatus(false);
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        public void Dispose() => _http.Dispose();

        // net48 没有 IsExternalInit，不用 record struct；JsonPropertyName 对齐服务端小写字段
        private sealed class RpcBody
        {
            public RpcBody(string method, object? parameters) { Method = method; Params = parameters; }
            [JsonPropertyName("method")] public string Method { get; }
            [JsonPropertyName("params")] public object? Params { get; }
        }

        private sealed class PermitBody
        {
            public PermitBody(long id, string decision) { Id = id; Decision = decision; }
            [JsonPropertyName("id")] public long Id { get; }
            [JsonPropertyName("decision")] public string Decision { get; }
        }

        private sealed class WorkspaceBody
        {
            public WorkspaceBody(string path) { Path = path; }
            [JsonPropertyName("path")] public string Path { get; }
        }
    }

    public sealed class Q38ApiException : Exception
    {
        public Q38ApiException(string message) : base(message) { }
    }
}
