using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Q38Vs
{
    /// <summary>
    /// q38 web 进程管理：先试附着 3848 上已跑的实例，否则 spawn 专用端口。
    /// 自己拉起的进程随 VS 退出回收；附着的不动。
    /// </summary>
    public sealed class Q38ProcessManager : IDisposable
    {
        public const int DefaultPort = 3848;
        private const int PickFreePortStart = 3849;  // 空闲端口探测的起始端口（= DefaultPort + 1，避开默认端口 3848）
        private const int PickFreePortUpper = 3960;   // 空闲端口探测的上限（不含），实际最大候选为 3959

        public int Port { get; private set; }
        public bool Attached { get; private set; }
        public string? Q38Exe { get; private set; }
        private Process? _proc;

        /// <summary>返回 null 表示找不到 q38 可执行文件。</summary>
        public static string? FindQ38()
        {
            var env = Environment.GetEnvironmentVariable("Q38_BIN");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            foreach (var name in new[] { "q38.exe", "q38" })
            {
                try
                {
                    var psi = new ProcessStartInfo("where.exe", name)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    var line = p?.StandardOutput.ReadLine();
                    p?.WaitForExit(2000);
                    if (!string.IsNullOrWhiteSpace(line) && File.Exists(line)) return line;
                }
                catch { /* where 不存在等，继续 */ }
            }

            var cargo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cargo", "bin", "q38.exe");
            return File.Exists(cargo) ? cargo : null;
        }

        private static async Task<bool> ProbeAsync(int port, CancellationToken ct)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
                using var resp = await http.GetAsync($"http://127.0.0.1:{port}/api/state", ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static int PickFreePort()
        {
            for (var port = PickFreePortStart; port < PickFreePortUpper; port++)
            {
                try
                {
                    var listener = TcpListener.Create(port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch (SocketException) { /* 占用，下一个 */ }
            }
            return 0;
        }

        /// <summary>附着或拉起 q38 web；成功后 Port/Attached 可用。失败抛异常（消息给人看）。</summary>
        public async Task StartAsync(IProgress<string> log, CancellationToken ct = default)
        {
            if (await ProbeAsync(DefaultPort, ct).ConfigureAwait(false))
            {
                Port = DefaultPort;
                Attached = true;
                log.Report($"已附着本机在跑的 q38 web（127.0.0.1:{DefaultPort}）");
                return;
            }

            var exe = FindQ38();
            if (exe == null)
                throw new InvalidOperationException(
                    "找不到 q38 可执行文件。请先 cargo install --path crates/q38-cli（上游仓库），" +
                    "或把 q38.exe 放进 PATH，或设环境变量 Q38_BIN 指向它。");

            var port = PickFreePort();
            if (port == 0) throw new InvalidOperationException("3849–3959 没有空闲端口。");

            var psi = new ProcessStartInfo(exe, $"web --no-open --bind 127.0.0.1:{port}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("q38 进程启动失败。");
            Q38Exe = exe;

            for (var i = 0; i < 80; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProbeAsync(port, ct).ConfigureAwait(false))
                {
                    Port = port;
                    Attached = false;
                    log.Report($"已拉起 q38 web（127.0.0.1:{port}）");
                    return;
                }
                if (_proc.HasExited)
                    throw new InvalidOperationException($"q38 web 启动后退出（code {_proc.ExitCode}）。");
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("q38 web 在 24 秒内没有就绪（GET /api/state）。");
        }

        public void Dispose()
        {
            if (Attached) return;
            try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { /* 收尾尽力 */ }
            _proc?.Dispose();
            _proc = null;
        }
    }
}
