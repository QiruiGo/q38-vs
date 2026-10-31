using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Q38Vs
{
    public static class PackageGuids
    {
        public const string PackageString = "3f9c2d64-8a1e-4b7f-9d05-62c8e4a1b7f9";
        public static readonly Guid Package = new Guid(PackageString);
        public const string ToolWindowString = "c4a81f7e-2d3b-4e96-b1a4-7f2e9d5c8b06";
        public static readonly Guid ToolWindow = new Guid(ToolWindowString);
    }

    /// <summary>
    /// q38-vs 入口包。免 VSCT 路线：不注册菜单命令，包随解决方案上下文后台自动加载，
    /// 加载后直接把工具窗弹出来（demo 定位：零点击可见）。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuids.PackageString)]
    [ProvideToolWindow(typeof(Q38ToolWindow),
        Style = VsDockStyle.Tabbed,
        Window = ToolWindowGuids80.SolutionExplorer,
        Orientation = ToolWindowOrientation.Bottom,
        PositionX = 0, PositionY = 0, Width = 520, Height = 760)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class Q38Package : AsyncPackage
    {
        protected override Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // 2026-10-31 deadlock lessons (two rounds, three frozen devenv instances each):
            // During background autoload the shell synchronously waits for package init at
            // startup barriers, so ANY wait inside InitializeAsync (main-thread switch or
            // shell-init poll) forms a cycle with the UI thread. InitializeAsync must
            // return immediately; the auto-show runs detached and only hops to the UI
            // thread after the shell reports VSSPROPID_ShellInitialized.
            JoinableTaskFactory.RunAsync(() => ShowWhenShellReadyAsync(cancellationToken)).Task.Forget();
            return Task.CompletedTask;
        }

        private async Task ShowWhenShellReadyAsync(CancellationToken ct)
        {
            void Log(string s)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "q38vs-debug.log"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n"); } catch { }
            }
            try
            {
                Log("task start");
                IVsShell shell = null;
                for (var i = 0; i < 20 && shell == null; i++)
                {
                    shell = await GetServiceAsync(typeof(SVsShell)) as IVsShell;
                    if (shell == null) { Log("GetServiceAsync(SVsShell)=null, retry"); await Task.Delay(500, ct); }
                }
                Log(shell == null ? "shell STILL null" : "shell ok");

                // Poll VSSPROPID_ShellInitialized; after 60s proceed anyway (never block
                // the window forever just because a property read misbehaves).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var initialized = false;
                while (!ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < 60)
                {
                    object init = null;
                    var hr = shell != null
                        ? shell.GetProperty((int)__VSSPROPID4.VSSPROPID_ShellInitialized, out init)
                        : -2147467259;
                    if (hr == 0 && init is bool ready && ready) { initialized = true; break; }
                    await Task.Delay(250, ct);
                }
                Log("shellInitialized=" + initialized + " after " + sw.ElapsedMilliseconds + "ms");

                await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                var pane = FindToolWindow(typeof(Q38ToolWindow), 0, true);
                var frame = pane?.Frame as IVsWindowFrame;
                Log(frame == null ? "frame NULL (pane=" + (pane == null ? "null" : pane.GetType().Name) + ")" : "frame ok");
                frame?.Show();
                Log("Show() done");
            }
            catch (OperationCanceledException) { }
            catch (System.Exception ex)
            {
                Log("EX: " + ex);
            }
        }
    }
}
