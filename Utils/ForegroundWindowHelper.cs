using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace YTPlayer.Utils
{
    internal static class ForegroundWindowHelper
    {
        internal const string ForegroundRequestEnvVar = "YTPLAYER_FORCE_FOREGROUND";
        private const int SW_RESTORE = 9;

        public static void MarkForegroundRequest(ProcessStartInfo startInfo)
        {
            if (startInfo == null)
            {
                throw new ArgumentNullException(nameof(startInfo));
            }

            if (!startInfo.UseShellExecute)
            {
                startInfo.EnvironmentVariables[ForegroundRequestEnvVar] = "1";
            }
        }

        public static bool ConsumeForegroundRequestFlag()
        {
            string? value = Environment.GetEnvironmentVariable(ForegroundRequestEnvVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Environment.SetEnvironmentVariable(ForegroundRequestEnvVar, null);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGrantForegroundPermission(Process? process, Action<string>? log = null)
        {
            if (process == null)
            {
                log?.Invoke("未提供进程实例，无法授予前台激活权限。");
                return false;
            }

            try
            {
                bool granted = AllowSetForegroundWindow(process.Id);
                log?.Invoke(granted
                    ? $"已调用 AllowSetForegroundWindow，目标 PID={process.Id}。"
                    : $"AllowSetForegroundWindow 返回 false，PID={process.Id}。");
                return granted;
            }
            catch (Exception ex)
            {
                log?.Invoke($"AllowSetForegroundWindow 调用失败：{ex.Message}");
                return false;
            }
        }

        public static bool TryBringProcessToForeground(Process? process, Action<string>? log = null, int timeoutMs = 10000)
        {
            if (process == null)
            {
                log?.Invoke("目标进程为空，无法前台激活。");
                return false;
            }

            TryGrantForegroundPermission(process, log);
            return TryActivateProcessMainWindow(process, log, timeoutMs);
        }

        public static bool TryActivateProcessMainWindow(Process process, Action<string>? log = null, int timeoutMs = 10000)
        {
            if (process == null)
            {
                throw new ArgumentNullException(nameof(process));
            }

            int safeTimeout = Math.Max(800, timeoutMs);
            try
            {
                if (!process.HasExited)
                {
                    int idleTimeout = Math.Min(3000, safeTimeout);
                    try
                    {
                        bool idleReady = process.WaitForInputIdle(idleTimeout);
                        log?.Invoke(idleReady
                            ? "目标进程进入消息循环空闲态。"
                            : "等待目标进程消息循环空闲超时，继续尝试句柄激活。");
                    }
                    catch (InvalidOperationException)
                    {
                        log?.Invoke("目标进程未提供图形消息循环，跳过 WaitForInputIdle。");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"WaitForInputIdle 阶段异常：{ex.Message}");
            }

            IntPtr windowHandle = IntPtr.Zero;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < safeTimeout)
            {
                try
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        log?.Invoke("目标进程已退出，无法前台激活。");
                        return false;
                    }

                    windowHandle = process.MainWindowHandle;
                    if (windowHandle != IntPtr.Zero)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"刷新目标进程窗口句柄失败：{ex.Message}");
                    return false;
                }

                Thread.Sleep(80);
            }

            if (windowHandle == IntPtr.Zero)
            {
                log?.Invoke("在超时范围内未获取到 MainWindowHandle。");
                return false;
            }

            return TryActivateWindow(windowHandle, log);
        }

        public static bool TryActivateWindow(IntPtr windowHandle, Action<string>? log = null, int attempts = 8, int delayMs = 70)
        {
            if (windowHandle == IntPtr.Zero)
            {
                log?.Invoke("窗口句柄为空，无法激活。");
                return false;
            }

            int totalAttempts = Math.Max(1, attempts);
            int sleepDelay = Math.Max(10, delayMs);
            for (int i = 0; i < totalAttempts; i++)
            {
                ShowWindowAsync(windowHandle, SW_RESTORE);
                if (SetForegroundWindow(windowHandle))
                {
                    log?.Invoke($"SetForegroundWindow 成功（第 {i + 1} 次）。");
                    return true;
                }

                Thread.Sleep(sleepDelay);
            }

            IntPtr foregroundWindow = GetForegroundWindow();
            uint foregroundThread = foregroundWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
            uint currentThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                    log?.Invoke(attached
                        ? "已附加输入队列，执行一次兜底激活。"
                        : "附加输入队列失败，执行常规兜底激活。");
                }

                ShowWindowAsync(windowHandle, SW_RESTORE);
                BringWindowToTop(windowHandle);
                if (SetForegroundWindow(windowHandle))
                {
                    log?.Invoke("兜底激活成功。");
                    return true;
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            log?.Invoke("所有前台激活尝试均失败。");
            return false;
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
