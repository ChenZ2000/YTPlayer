using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback.Cache;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// BASS 流提供器 - 将 AudioCacheManager 适配到 BASS 的文件用户接口
    /// </summary>
    public class BassStreamProvider : IDisposable
    {
        #region BASS P/Invoke 定义

        [DllImport("bass.dll")]
        private static extern int BASS_StreamCreateFileUser(
            int system,
            uint flags,
            [In] ref BASS_FILEPROCS procs,
            IntPtr user);

        [StructLayout(LayoutKind.Sequential)]
        private struct BASS_FILEPROCS
        {
            public FILECLOSEPROC close;
            public FILELENPROC length;
            public FILEREADPROC read;
            public FILESEEKPROC seek;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void FILECLOSEPROC(IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate long FILELENPROC(IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int FILEREADPROC(IntPtr buffer, int length, IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool FILESEEKPROC(long offset, IntPtr user);

        // BASS 常量
        private const int STREAMFILE_NOBUFFER = 0;
        private const int BASS_STREAM_DECODE = 0x200000;
        private const int BASS_STREAM_BLOCK = 0x100000; // 允许阻塞读取，返回0不会被视为EOF

        #endregion

        #region 字段

        private readonly SmartCacheManager _cacheManager;
        private long _currentPosition = 0;
        private readonly object _positionLock = new object();

        // 回调委托（必须保持引用，防止GC回收）
        private BASS_FILEPROCS _fileProcs;
        private readonly FILECLOSEPROC _closeProc;
        private readonly FILELENPROC _lengthProc;
        private readonly FILEREADPROC _readProc;
        private readonly FILESEEKPROC _seekProc;

        // 读取缓冲区（避免频繁分配）
        private byte[]? _readBuffer;
        private const int READ_BUFFER_SIZE = 64 * 1024;  // 64KB
        private const int BaseReadTimeoutMs = 8000;       // 缓存等待基础超时
        private const int NearEofReadTimeoutMs = 3000;    // 接近 EOF 的较短等待
        private const int MaxStallWaitMs = 15000;         // 单次读取最大等待时间
        private const int MaxConsecutiveStalls = 2;
        private const int StallBackoffDelayMs = 200;

        // 统计信息
        private int _totalReads = 0;
        private int _totalSeeks = 0;
        private long _totalBytesRead = 0;
        private int _consecutiveStallCount = 0;

        private bool _disposed = false;
        private readonly CancellationTokenSource _abortReadCts = new CancellationTokenSource();

        #endregion

        #region 属性

        /// <summary>
        /// 当前读取位置
        /// </summary>
        public long CurrentPosition
        {
            get
            {
                lock (_positionLock)
                {
                    return _currentPosition;
                }
            }
        }

        /// <summary>
        /// 缓存管理器
        /// </summary>
        public SmartCacheManager CacheManager => _cacheManager;

        public event EventHandler<StreamReadFailureEventArgs>? ReadFailure;

        #endregion

        #region 构造函数

        public BassStreamProvider(SmartCacheManager cacheManager)
        {
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));

            // 创建回调委托（并保持引用）
            _closeProc = new FILECLOSEPROC(FileClose);
            _lengthProc = new FILELENPROC(FileLength);
            _readProc = new FILEREADPROC(FileRead);
            _seekProc = new FILESEEKPROC(FileSeek);

            // 构建 FILEPROCS 结构
            _fileProcs = new BASS_FILEPROCS
            {
                close = _closeProc,
                length = _lengthProc,
                read = _readProc,
                seek = _seekProc
            };

            Debug.WriteLine("[BassStreamProvider] Created");
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 创建 BASS 流句柄
        /// </summary>
        /// <param name="flags">BASS 标志</param>
        /// <returns>流句柄，失败返回0</returns>
        public int CreateStream(uint flags = 0)
        {
            Debug.WriteLine("[BassStreamProvider] Creating BASS stream with FileUser interface");

            int stream = BASS_StreamCreateFileUser(
                STREAMFILE_NOBUFFER,
                flags | BASS_STREAM_BLOCK,
                ref _fileProcs,
                IntPtr.Zero  // user data (不使用，因为C#闭包已经捕获了上下文)
            );

            // 注意：BASS句柄是DWORD（无符号），转为int后可能是负数，但仍有效
            // 只有0表示失败
            if (stream == 0)
            {
                Debug.WriteLine("[BassStreamProvider] Failed to create stream");
            }
            else
            {
                // 使用unchecked避免负数转uint时的OverflowException
                uint unsignedHandle = unchecked((uint)stream);
                Debug.WriteLine($"[BassStreamProvider] Stream created successfully: handle={stream} (unsigned: 0x{unsignedHandle:X8})");
            }

            return stream;
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public string GetStats()
        {
            return $"Reads: {_totalReads}, Seeks: {_totalSeeks}, " +
                   $"Bytes read: {_totalBytesRead:N0}, Position: {CurrentPosition:N0}";
        }

        #endregion

        public void AbortPendingReads()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _abortReadCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        #region BASS 回调实现

        /// <summary>
        /// 关闭回调
        /// </summary>
        private void FileClose(IntPtr user)
        {
            Debug.WriteLine("[BassStreamProvider] FileClose called");
            // 不在这里释放资源，由 Dispose 方法处理
        }

        /// <summary>
        /// 获取文件长度回调
        /// </summary>
        private long FileLength(IntPtr user)
        {
            // ⭐⭐⭐ 防止在释放后继续调用
            if (_disposed || _cacheManager.IsDisposed)
            {
                Debug.WriteLine($"[BassStreamProvider] FileLength called after disposal, returning 0");
                return 0;
            }

            long length = _cacheManager.TotalSize;
            Debug.WriteLine($"[BassStreamProvider] FileLength called: {length} bytes");
            return length;
        }

        /// <summary>
        /// 读取数据回调
        /// </summary>
        private int FileRead(IntPtr buffer, int length, IntPtr user)
        {
            try
            {
                if (_disposed || _cacheManager.IsDisposed || _abortReadCts.IsCancellationRequested)
                {
                    Debug.WriteLine("[BassStreamProvider] FileRead called after disposal, returning -1");
                    return -1;
                }

                Interlocked.Increment(ref _totalReads);

                if (_readBuffer == null || _readBuffer.Length < length)
                {
                    _readBuffer = new byte[Math.Max(length, READ_BUFFER_SIZE)];
                }

                long position;
                lock (_positionLock)
                {
                    position = _currentPosition;
                }

                int bytesRead = 0;
                int totalWaitMs = 0;

                while (true)
                {
                    if (_disposed || _cacheManager.IsDisposed || _abortReadCts.IsCancellationRequested)
                    {
                        return -1;
                    }

                    long remainingBytes = _cacheManager.TotalSize - position;
                    if (remainingBytes <= 0)
                    {
                        ResetStallTracking();
                        return 0;
                    }

                    bool nearEof = remainingBytes <= 512 * 1024;
                    int timeoutMs = nearEof ? NearEofReadTimeoutMs : BaseReadTimeoutMs;

                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cacheManager.LifecycleToken, _abortReadCts.Token))
                    {
                        try
                        {
                            bytesRead = _cacheManager.ReadAsync(position, _readBuffer, 0, length, linkedCts.Token, waitIfNotReady: true)
                                .GetAwaiter()
                                .GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            if (_abortReadCts.IsCancellationRequested || _disposed || _cacheManager.IsDisposed)
                            {
                                return -1;
                            }

                            bytesRead = 0;
                        }
                        catch (AggregateException aex)
                        {
                            Exception ex = aex.InnerException ?? aex;
                            if (ex is OperationCanceledException)
                            {
                                bytesRead = 0;
                            }
                            else
                            {
                                Debug.WriteLine($"[BassStreamProvider] FileRead exception at position {position}: {ex.Message}");
                                NotifyReadFailure(position, length, ex.Message);
                                return -1;
                            }
                        }
                    }

                    if (bytesRead > 0)
                    {
                        ResetStallTracking();
                        break;
                    }

                    totalWaitMs += timeoutMs;
                    if (totalWaitMs >= MaxStallWaitMs)
                    {
                        _consecutiveStallCount++;

                        Debug.WriteLine($"[BassStreamProvider] Stall detected (#{_consecutiveStallCount}) at {position}/{_cacheManager.TotalSize}");

                        if (_consecutiveStallCount >= MaxConsecutiveStalls)
                        {
                            string reason = $"Consecutive cache stalls exceeded threshold ({_consecutiveStallCount})";
                            NotifyReadFailure(position, length, reason);
                            return -1;
                        }

                        // In BLOCK mode returning 0 keeps the stream alive but yields control back to BASS.
                        return 0;
                    }

                    try
                    {
                        Task.Delay(StallBackoffDelayMs, _abortReadCts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return -1;
                    }
                }

                if (bytesRead > length)
                {
                    NotifyReadFailure(position, length, $"Invalid bytesRead={bytesRead} > requested={length}");
                    return -1;
                }

                Marshal.Copy(_readBuffer, 0, buffer, bytesRead);

                lock (_positionLock)
                {
                    _currentPosition += bytesRead;
                }

                Interlocked.Add(ref _totalBytesRead, bytesRead);

                if (_totalReads % 100 == 1)
                {
                    Debug.WriteLine($"[BassStreamProvider] FileRead #{_totalReads}: requested={length}, read={bytesRead}, pos={position}");
                }

                return bytesRead;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BassStreamProvider] FileRead error: {ex.Message}");
                NotifyReadFailure(CurrentPosition, length, ex.Message);
                return -1;
            }
        }

        private void ResetStallTracking()
        {
            _consecutiveStallCount = 0;
        }

        private void NotifyReadFailure(long position, int length, string reason)
        {
            try
            {
                ReadFailure?.Invoke(this, new StreamReadFailureEventArgs(position, length, reason));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BassStreamProvider] ReadFailure handler error: {ex.Message}");
            }
        }

        private bool FileSeek(long offset, IntPtr user)
        {
            try
            {
                // ⭐⭐⭐ 防止在释放后继续调用
                if (_disposed || _cacheManager.IsDisposed)
                {
                    Debug.WriteLine($"[BassStreamProvider] FileSeek called after disposal, returning false");
                    return false;
                }

                Interlocked.Increment(ref _totalSeeks);

                lock (_positionLock)
                {
                    // 边界检查
                    if (offset < 0 || offset > _cacheManager.TotalSize)
                    {
                        Debug.WriteLine($"[BassStreamProvider] FileSeek failed: offset {offset} out of range");
                        return false;
                    }

                    _currentPosition = offset;
                }

                ResetStallTracking();
                Debug.WriteLine($"[BassStreamProvider] FileSeek to {offset}");

                // 通知缓存管理器准备该位置（异步，不阻塞）
                _ = Task.Run(async () =>
                {
                    await _cacheManager.SeekAsync(offset, _cacheManager.LifecycleToken);
                });

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BassStreamProvider] FileSeek error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _abortReadCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _abortReadCts.Dispose();

            _readBuffer = null;

            Debug.WriteLine("[BassStreamProvider] Disposed");
        }

        #endregion
    }

    public sealed class StreamReadFailureEventArgs : EventArgs
    {
        public StreamReadFailureEventArgs(long position, int requestedBytes, string reason)
        {
            Position = position;
            RequestedBytes = requestedBytes;
            Reason = reason ?? string.Empty;
        }

        public long Position { get; }

        public int RequestedBytes { get; }

        public string Reason { get; }
    }
}
