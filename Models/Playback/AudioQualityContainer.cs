using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using YTPlayer.Core.Reactive;

namespace YTPlayer.Models.Playback
{
    /// <summary>
    /// 音质容器 - 管理特定音质的所有音频数据块
    /// 采用行列结构设计，不同音质独立管理各自的块数（动态计算）
    /// </summary>
    public sealed class AudioQualityContainer
    {
        /// <summary>
        /// 数据块大小（256KB）
        /// </summary>
        public const int CHUNK_SIZE = 256 * 1024;

        /// <summary>
        /// 音质标识（如 "standard", "exhigh", "lossless" 等）
        /// </summary>
        public string Quality { get; }

        /// <summary>
        /// 下载 URL（响应式字段）
        /// </summary>
        public ObservableField<string> Url { get; }

        /// <summary>
        /// 总字节数（响应式字段）
        /// </summary>
        public ObservableField<long> TotalSize { get; }

        /// <summary>
        /// 总块数（动态计算，响应式字段）
        /// </summary>
        public ObservableField<int> TotalChunks { get; }

        /// <summary>
        /// URL 是否已解析（响应式字段）
        /// </summary>
        public ObservableField<bool> IsUrlResolved { get; }

        /// <summary>
        /// Chunks 容器（ConcurrentDictionary，支持并发读写）
        /// Key: chunk index, Value: ChunkSlot
        /// </summary>
        private readonly ConcurrentDictionary<int, ChunkSlot> _chunks;

        /// <summary>
        /// Chunk 就绪订阅者字典
        /// Key: chunk index, Value: 订阅者回调列表
        /// </summary>
        private readonly ConcurrentDictionary<int, List<Action<byte[]>>> _chunkReadySubscribers;

        /// <summary>
        /// 创建音质容器
        /// </summary>
        /// <param name="quality">音质标识</param>
        public AudioQualityContainer(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
                throw new ArgumentException("Quality cannot be null or empty", nameof(quality));

            Quality = quality;
            Url = new ObservableField<string>(null);
            TotalSize = new ObservableField<long>(0);
            TotalChunks = new ObservableField<int>(0);
            IsUrlResolved = new ObservableField<bool>(false);

            _chunks = new ConcurrentDictionary<int, ChunkSlot>();
            _chunkReadySubscribers = new ConcurrentDictionary<int, List<Action<byte[]>>>();

            // 订阅 TotalSize 变化，自动重新计算 TotalChunks
            TotalSize.Subscribe(size =>
            {
                if (size > 0)
                {
                    int calculatedChunks = (int)Math.Ceiling((double)size / CHUNK_SIZE);
                    TotalChunks.Value = calculatedChunks;
                }
                else
                {
                    TotalChunks.Value = 0;
                }
            });
        }

        /// <summary>
        /// 获取或创建数据块槽位
        /// </summary>
        /// <param name="index">块索引</param>
        /// <returns>数据块槽位</returns>
        public ChunkSlot GetOrCreateChunk(int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Chunk index cannot be negative");

            return _chunks.GetOrAdd(index, i =>
            {
                var slot = new ChunkSlot(i);

                // 订阅数据变化，自动通知订阅者
                slot.Data.Subscribe(data =>
                {
                    if (data != null && _chunkReadySubscribers.TryGetValue(i, out var subscribers))
                    {
                        foreach (var subscriber in subscribers.ToArray())
                        {
                            try
                            {
                                subscriber?.Invoke(data);
                            }
                            catch (Exception ex)
                            {
                                Utils.DebugLogger.Log(Utils.LogLevel.Error, "AudioQualityContainer",
                                    $"Chunk ready subscriber exception: {ex.Message}");
                            }
                        }
                    }
                });

                return slot;
            });
        }

        /// <summary>
        /// 尝试获取数据块槽位（不创建新槽位）
        /// </summary>
        public bool TryGetChunk(int index, out ChunkSlot chunk)
        {
            return _chunks.TryGetValue(index, out chunk);
        }

        /// <summary>
        /// 尝试获取数据块数据（快速读取）
        /// </summary>
        /// <param name="index">块索引</param>
        /// <param name="data">数据输出</param>
        /// <returns>是否成功获取</returns>
        public bool TryGetChunkData(int index, out byte[] data)
        {
            if (_chunks.TryGetValue(index, out var chunk) && chunk.IsReady)
            {
                data = chunk.Data.Value;
                chunk.RecordAccess(); // 记录访问
                return true;
            }

            data = null;
            return false;
        }

        /// <summary>
        /// 订阅特定块的就绪事件
        /// </summary>
        /// <param name="chunkIndex">块索引</param>
        /// <param name="onReady">就绪回调</param>
        /// <returns>取消订阅令牌</returns>
        public IDisposable SubscribeChunkReady(int chunkIndex, Action<byte[]> onReady)
        {
            if (onReady == null)
                throw new ArgumentNullException(nameof(onReady));

            var subscribers = _chunkReadySubscribers.GetOrAdd(chunkIndex, _ => new List<Action<byte[]>>());

            lock (subscribers)
            {
                subscribers.Add(onReady);
            }

            // 如果该块已经就绪，立即通知
            if (TryGetChunkData(chunkIndex, out var existingData))
            {
                System.Threading.Tasks.Task.Run(() => onReady(existingData));
            }

            return new ChunkReadySubscription(this, chunkIndex, onReady);
        }

        /// <summary>
        /// 取消特定块的订阅
        /// </summary>
        private void UnsubscribeChunkReady(int chunkIndex, Action<byte[]> callback)
        {
            if (_chunkReadySubscribers.TryGetValue(chunkIndex, out var subscribers))
            {
                lock (subscribers)
                {
                    subscribers.Remove(callback);
                }
            }
        }

        /// <summary>
        /// 获取所有数据块（用于遍历）
        /// </summary>
        public IEnumerable<ChunkSlot> GetAllChunks()
        {
            return _chunks.Values;
        }

        /// <summary>
        /// 获取已加载的数据块数量
        /// </summary>
        public int GetLoadedChunkCount()
        {
            return _chunks.Values.Count(c => c.IsReady);
        }

        /// <summary>
        /// 计算当前内存占用（字节）
        /// </summary>
        public long CalculateMemoryUsage()
        {
            return _chunks.Values.Sum(c => c.MemoryUsage);
        }

        /// <summary>
        /// 清空所有非 Chunk 0 的数据（用于垃圾回收）
        /// </summary>
        public void ClearNonChunk0Data()
        {
            foreach (var chunk in _chunks.Values.Where(c => c.Index > 0))
            {
                chunk.Clear();
            }
        }

        /// <summary>
        /// 清空所有数据（包括 Chunk 0）
        /// </summary>
        public void ClearAllData()
        {
            foreach (var chunk in _chunks.Values)
            {
                chunk.Clear();
            }
        }

        /// <summary>
        /// 获取缓存进度百分比（0-100）
        /// </summary>
        public double GetCacheProgress()
        {
            int totalChunks = TotalChunks.Value;
            if (totalChunks == 0)
                return 0;

            int loadedChunks = GetLoadedChunkCount();
            return (double)loadedChunks / totalChunks * 100.0;
        }

        /// <summary>
        /// 检查是否包含 Chunk 0
        /// </summary>
        public bool HasChunk0 => TryGetChunk(0, out var chunk) && chunk.IsReady;

        public override string ToString()
        {
            return $"AudioQuality[{Quality}] URL={IsUrlResolved.Value}, " +
                   $"Size={TotalSize.Value / 1024.0 / 1024.0:F2}MB, " +
                   $"Chunks={GetLoadedChunkCount()}/{TotalChunks.Value}, " +
                   $"Memory={CalculateMemoryUsage() / 1024.0 / 1024.0:F2}MB";
        }

        /// <summary>
        /// Chunk 就绪订阅令牌
        /// </summary>
        private sealed class ChunkReadySubscription : IDisposable
        {
            private AudioQualityContainer _container;
            private int _chunkIndex;
            private Action<byte[]> _callback;

            public ChunkReadySubscription(AudioQualityContainer container, int chunkIndex, Action<byte[]> callback)
            {
                _container = container;
                _chunkIndex = chunkIndex;
                _callback = callback;
            }

            public void Dispose()
            {
                if (_container != null && _callback != null)
                {
                    _container.UnsubscribeChunkReady(_chunkIndex, _callback);
                    _container = null;
                    _callback = null;
                }
            }
        }
    }
}
