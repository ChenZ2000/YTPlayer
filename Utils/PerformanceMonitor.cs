using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 性能监控器 - 检测 UI 线程阻塞、长时间操作和死锁风险
    /// </summary>
    public class PerformanceMonitor : IDisposable
    {
        private readonly string _category;
        private Stopwatch _stopwatch;
        private string _operationName;
        private long _warningThresholdMs;
        private CancellationTokenSource _timeoutCts;
        private Task _timeoutTask;

        public PerformanceMonitor(string category)
        {
            _category = category;
        }

        /// <summary>
        /// 开始监控一个操作
        /// </summary>
        /// <param name="operationName">操作名称</param>
        /// <param name="warningThresholdMs">警告阈值（毫秒），默认1000ms</param>
        /// <param name="timeoutMs">超时时间（毫秒），0表示不超时</param>
        public void StartOperation(string operationName, long warningThresholdMs = 1000, long timeoutMs = 0)
        {
            _operationName = operationName;
            _warningThresholdMs = warningThresholdMs;

            // 检查是否在 UI 线程
            if (Control.CheckForIllegalCrossThreadCalls &&
                Application.OpenForms.Count > 0 &&
                Application.OpenForms[0].InvokeRequired == false)
            {
                DebugLogger.Log(DebugLogger.LogLevel.Warning, _category,
                    $"⚠️ 在 UI 线程上开始操作: {operationName} - 如果耗时过长会导致界面卡死");
            }

            _stopwatch = Stopwatch.StartNew();
            DebugLogger.Log(DebugLogger.LogLevel.Info, _category, $"▶️ 开始: {operationName}");

            // 设置超时监控
            if (timeoutMs > 0)
            {
                _timeoutCts = new CancellationTokenSource();
                _timeoutTask = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay((int)timeoutMs, _timeoutCts.Token).ConfigureAwait(false);
                        DebugLogger.Log(DebugLogger.LogLevel.Error, _category,
                            $"🔴 超时警告: {operationName} 执行超过 {timeoutMs}ms，可能发生死锁或卡死！");
                    }
                    catch (TaskCanceledException)
                    {
                        // 正常完成，取消了超时监控
                    }
                }, _timeoutCts.Token);
            }
        }

        /// <summary>
        /// 结束监控并记录结果
        /// </summary>
        public void EndOperation()
        {
            if (_stopwatch == null) return;

            _stopwatch.Stop();
            long elapsed = _stopwatch.ElapsedMilliseconds;

            // 取消超时监控
            _timeoutCts?.Cancel();
            _timeoutCts?.Dispose();
            _timeoutCts = null;

            // 记录结果
            if (elapsed > _warningThresholdMs)
            {
                DebugLogger.Log(DebugLogger.LogLevel.Performance, _category,
                    $"⏱️ 完成: {_operationName} - 耗时 {elapsed}ms ⚠️ 超过阈值 {_warningThresholdMs}ms");

                // 如果是在 UI 线程且超时严重，记录 UI 阻塞警告
                if (elapsed > 100 && IsUIThread())
                {
                    DebugLogger.LogUIThreadBlock(_category, _operationName, elapsed);
                }
            }
            else
            {
                DebugLogger.Log(DebugLogger.LogLevel.Info, _category,
                    $"✅ 完成: {_operationName} - 耗时 {elapsed}ms");
            }

            _stopwatch = null;
        }

        /// <summary>
        /// 监控一个异步操作
        /// </summary>
        public static async Task<T> MonitorAsync<T>(
            string category,
            string operationName,
            Func<Task<T>> operation,
            long warningThresholdMs = 1000,
            long timeoutMs = 30000)
        {
            using (var monitor = new PerformanceMonitor(category))
            {
                monitor.StartOperation(operationName, warningThresholdMs, timeoutMs);
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                finally
                {
                    monitor.EndOperation();
                }
            }
        }

        /// <summary>
        /// 监控一个同步操作
        /// </summary>
        public static T Monitor<T>(
            string category,
            string operationName,
            Func<T> operation,
            long warningThresholdMs = 1000)
        {
            using (var monitor = new PerformanceMonitor(category))
            {
                monitor.StartOperation(operationName, warningThresholdMs);
                try
                {
                    return operation();
                }
                finally
                {
                    monitor.EndOperation();
                }
            }
        }

        /// <summary>
        /// 检查是否在 UI 线程
        /// </summary>
        private static bool IsUIThread()
        {
            try
            {
                if (Application.OpenForms.Count == 0) return false;
                return !Application.OpenForms[0].InvokeRequired;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _timeoutCts?.Cancel();
            _timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// 死锁检测器 - 检测潜在的死锁情况
    /// </summary>
    public static class DeadlockDetector
    {
        /// <summary>
        /// 检测 Task.Wait() 或 .Result 在 UI 线程的使用（常见死锁原因）
        /// </summary>
        public static void CheckTaskWaitOnUIThread(string category, string operation)
        {
            if (IsUIThread())
            {
                DebugLogger.LogDeadlockRisk(category, operation,
                    "在 UI 线程上使用 Task.Wait() 或 .Result 可能导致死锁！建议使用 async/await");
            }
        }

        /// <summary>
        /// 检测 ConfigureAwait(true) 的使用
        /// </summary>
        public static void WarnConfigureAwaitTrue(string category, string operation)
        {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, category,
                $"⚠️ {operation} 使用了 ConfigureAwait(true)，可能导致 UI 线程阻塞");
        }

        /// <summary>
        /// 检测同步文件 I/O 在 UI 线程
        /// </summary>
        public static void CheckSyncIOOnUIThread(string category, string operation)
        {
            if (IsUIThread())
            {
                DebugLogger.LogDeadlockRisk(category, operation,
                    "在 UI 线程上进行同步文件 I/O 会阻塞界面！建议使用异步 I/O");
            }
        }

        /// <summary>
        /// 检测网络请求在 UI 线程
        /// </summary>
        public static void CheckNetworkOnUIThread(string category, string operation)
        {
            if (IsUIThread())
            {
                DebugLogger.LogUIThreadBlock(category, operation, -1);
                DebugLogger.Log(DebugLogger.LogLevel.Error, category,
                    $"🔴 在 UI 线程上进行网络请求: {operation} - 这会导致界面卡死！");
            }
        }

        private static bool IsUIThread()
        {
            try
            {
                if (Application.OpenForms.Count == 0) return false;
                return !Application.OpenForms[0].InvokeRequired;
            }
            catch
            {
                return false;
            }
        }
    }
}
