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

        // 统计信息
        private int _totalReads = 0;
        private int _totalSeeks = 0;
        private long _totalBytesRead = 0;

        private bool _disposed = false;

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
                flags,
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
            if (_disposed || _cacheManager == null)
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
                // ⭐⭐⭐ 防止在释放后继续调用
                if (_disposed || _cacheManager == null)
                {
                    Debug.WriteLine($"[BassStreamProvider] FileRead called after disposal, returning 0");
                    return 0;
                }

                Interlocked.Increment(ref _totalReads);

                // 分配缓冲区（如果还没有或大小不够）
                if (_readBuffer == null || _readBuffer.Length < length)
                {
                    _readBuffer = new byte[Math.Max(length, READ_BUFFER_SIZE)];
                }

                // 从缓存管理器读取数据（同步等待）
                long position;
                lock (_positionLock)
                {
                    position = _currentPosition;
                }

                // 🔧 关键修复：改为阻塞模式，等待缓存就绪后再返回数据给BASS
                // waitIfNotReady=true：等待缓存下载完成，避免传递损坏/不完整的数据给BASS
                // 使用超时机制（默认5秒，接近EOF时减少到500ms）防止无限阻塞
                int bytesRead = 0;

                // ⭐⭐⭐ 关键优化：如果请求位置接近文件末尾（> 98%），减少超时时间
                // 避免在歌曲结束时等待 5 秒
                double progressPercent = (double)position / _cacheManager.TotalSize;
                int timeoutMs = progressPercent >= 0.98 ? 500 : 5000;

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs)))
                {
                    try
                    {
                        // ⭐⭐⭐ 修复：正确处理Task.Run().Result可能抛出的AggregateException
                        bytesRead = Task.Run(async () =>
                        {
                            return await _cacheManager.ReadAsync(position, _readBuffer, 0, length, timeoutCts.Token, waitIfNotReady: true);
                        }, timeoutCts.Token).Result;
                    }
                    catch (AggregateException aex)
                    {
                        // .Result会将内部异常包装成AggregateException，需要解包
                        var innerEx = aex.InnerException ?? aex;
                        if (innerEx is OperationCanceledException)
                        {
                            if (progressPercent >= 0.98)
                            {
                                Debug.WriteLine($"[BassStreamProvider] ⏱ FileRead timeout ({timeoutMs}ms) at position {position} (EOF近端), 返回 EOF");
                            }
                            else
                            {
                                Debug.WriteLine($"[BassStreamProvider] ⚠️ FileRead timeout ({timeoutMs}ms) at position {position}, cache may be too slow!");
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[BassStreamProvider] ❌ FileRead exception at position {position}: {innerEx.Message}");
                        }
                        return 0;  // 返回0表示EOF或错误，BASS会触发 SYNC_END
                    }
                    catch (OperationCanceledException)
                    {
                        if (progressPercent >= 0.98)
                        {
                            Debug.WriteLine($"[BassStreamProvider] ⏱ FileRead timeout ({timeoutMs}ms) at position {position} (EOF近端), 返回 EOF");
                        }
                        else
                        {
                            Debug.WriteLine($"[BassStreamProvider] ⚠️ FileRead timeout ({timeoutMs}ms) at position {position}, cache may be too slow!");
                        }
                        return 0;  // 返回0表示EOF或错误，BASS会触发 SYNC_END
                    }
                }

                // 🔧 数据有效性检查
                if (bytesRead > 0)
                {
                    // 验证读取的数据大小是否合理
                    if (bytesRead > length)
                    {
                        Debug.WriteLine($"[BassStreamProvider] ❌ Invalid bytesRead={bytesRead} > requested={length}, data corruption detected!");
                        return -1;  // 返回-1表示错误
                    }

                    // 复制数据到 BASS 提供的缓冲区
                    Marshal.Copy(_readBuffer, 0, buffer, bytesRead);

                    // 更新位置
                    lock (_positionLock)
                    {
                        _currentPosition += bytesRead;
                    }

                    Interlocked.Add(ref _totalBytesRead, bytesRead);

                    // 每100次读取输出一次日志，避免刷屏
                    if (_totalReads % 100 == 1)
                    {
                        Debug.WriteLine($"[BassStreamProvider] ✓ FileRead #{_totalReads}: requested={length}, read={bytesRead}, pos={position}");
                    }
                }
                else if (bytesRead == 0 && position < _cacheManager.TotalSize)
                {
                    // 🔧 缓存未就绪或下载失败
                    Debug.WriteLine($"[BassStreamProvider] ⚠️ FileRead returned 0 at position {position}/{_cacheManager.TotalSize}, cache not ready!");
                }

                return bytesRead;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BassStreamProvider] FileRead error: {ex.Message}");
                return -1;  // 表示错误
            }
        }

        /// <summary>
        /// 定位回调
        /// </summary>
        private bool FileSeek(long offset, IntPtr user)
        {
            try
            {
                // ⭐⭐⭐ 防止在释放后继续调用
                if (_disposed || _cacheManager == null)
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

                Debug.WriteLine($"[BassStreamProvider] FileSeek to {offset}");

                // 通知缓存管理器准备该位置（异步，不阻塞）
                _ = Task.Run(async () =>
                {
                    await _cacheManager.SeekAsync(offset, CancellationToken.None);
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

            _readBuffer = null;

            Debug.WriteLine("[BassStreamProvider] Disposed");
        }

        #endregion
    }
}
