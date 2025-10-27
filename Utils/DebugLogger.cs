using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 详细的调试日志记录器 - 用于捕获卡顿、死锁和异常
    /// 日志文件位于：应用程序目录/Logs/
    /// </summary>
    public static class DebugLogger
    {
        private static readonly object _fileLock = new object();
        private static string _logDirectory;
        private static string _currentLogFile;
        private static bool _initialized = false;

        public enum LogLevel
        {
            Info,
            Warning,
            Error,
            Performance
        }

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                _logDirectory = Path.Combine(appDir, "Logs");

                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentLogFile = Path.Combine(_logDirectory, $"Debug_{timestamp}.log");

                _initialized = true;
                Log(LogLevel.Info, "Logger", "日志系统初始化完成");
                Log(LogLevel.Info, "Logger", $"日志文件: {_currentLogFile}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DebugLogger] 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录日志
        /// </summary>
        public static void Log(LogLevel level, string category, string message)
        {
            if (!_initialized) Initialize();

            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                int threadId = Thread.CurrentThread.ManagedThreadId;
                string threadName = Thread.CurrentThread.Name ?? "未命名";

                string logLine = $"[{timestamp}] [{level,-11}] [线程{threadId,3}:{threadName,-15}] [{category,-20}] {message}";

                // 输出到 Debug 控制台
                Debug.WriteLine(logLine);

                // 写入文件（线程安全）
                lock (_fileLock)
                {
                    File.AppendAllText(_currentLogFile, logLine + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DebugLogger] 记录日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录异常
        /// </summary>
        public static void LogException(string category, Exception ex, string additionalInfo = null)
        {
            if (!_initialized) Initialize();

            var sb = new StringBuilder();
            sb.AppendLine($"异常类型: {ex.GetType().Name}");
            sb.AppendLine($"异常消息: {ex.Message}");
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                sb.AppendLine($"附加信息: {additionalInfo}");
            }
            sb.AppendLine($"堆栈跟踪:\n{ex.StackTrace}");

            if (ex.InnerException != null)
            {
                sb.AppendLine($"内部异常: {ex.InnerException.GetType().Name}");
                sb.AppendLine($"内部异常消息: {ex.InnerException.Message}");
                sb.AppendLine($"内部异常堆栈:\n{ex.InnerException.StackTrace}");
            }

            Log(LogLevel.Error, category, sb.ToString());
        }

        /// <summary>
        /// 记录性能问题（执行时间过长）
        /// </summary>
        public static void LogPerformanceIssue(string category, string operation, long elapsedMs, long thresholdMs)
        {
            if (elapsedMs > thresholdMs)
            {
                Log(LogLevel.Performance, category,
                    $"⚠️ 性能警告: {operation} 耗时 {elapsedMs}ms (阈值: {thresholdMs}ms)");
            }
        }

        /// <summary>
        /// 开始性能计时
        /// </summary>
        public static Stopwatch StartTimer(string category, string operation)
        {
            Log(LogLevel.Info, category, $"▶️ 开始: {operation}");
            return Stopwatch.StartNew();
        }

        /// <summary>
        /// 结束性能计时并记录
        /// </summary>
        public static void EndTimer(Stopwatch timer, string category, string operation, long warningThresholdMs = 1000)
        {
            timer.Stop();
            long elapsed = timer.ElapsedMilliseconds;

            if (elapsed > warningThresholdMs)
            {
                Log(LogLevel.Performance, category,
                    $"⏱️ 完成: {operation} - 耗时 {elapsed}ms ⚠️ 超过阈值 {warningThresholdMs}ms");
            }
            else
            {
                Log(LogLevel.Info, category, $"✅ 完成: {operation} - 耗时 {elapsed}ms");
            }
        }

        /// <summary>
        /// 记录 UI 线程阻塞警告
        /// </summary>
        public static void LogUIThreadBlock(string category, string operation, long blockTimeMs)
        {
            Log(LogLevel.Error, category,
                $"🔴 UI线程阻塞: {operation} 阻塞了 {blockTimeMs}ms - 这会导致界面卡死！");
        }

        /// <summary>
        /// 记录死锁风险
        /// </summary>
        public static void LogDeadlockRisk(string category, string operation, string reason)
        {
            Log(LogLevel.Error, category,
                $"☠️ 死锁风险: {operation} - 原因: {reason}");
        }

        /// <summary>
        /// 清理旧日志文件（保留最近7天）
        /// </summary>
        public static void CleanOldLogs(int keepDays = 7)
        {
            try
            {
                if (!Directory.Exists(_logDirectory)) return;

                DateTime threshold = DateTime.Now.AddDays(-keepDays);
                var files = Directory.GetFiles(_logDirectory, "Debug_*.log");

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < threshold)
                    {
                        File.Delete(file);
                        Log(LogLevel.Info, "Logger", $"已删除过期日志: {Path.GetFileName(file)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DebugLogger] 清理旧日志失败: {ex.Message}");
            }
        }
    }
}
