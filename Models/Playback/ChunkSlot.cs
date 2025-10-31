using System;
using YTPlayer.Core.Reactive;

namespace YTPlayer.Models.Playback
{
    /// <summary>
    /// 数据块状态枚举
    /// </summary>
    public enum ChunkState
    {
        /// <summary>
        /// 空状态（未开始下载）
        /// </summary>
        Empty,

        /// <summary>
        /// 正在加载中
        /// </summary>
        Loading,

        /// <summary>
        /// 已就绪（数据完整）
        /// </summary>
        Ready,

        /// <summary>
        /// 下载失败
        /// </summary>
        Failed
    }

    /// <summary>
    /// 数据块槽位
    /// 表示音频数据的一个分块（通常 256KB）
    /// </summary>
    public sealed class ChunkSlot
    {
        /// <summary>
        /// 数据块索引（从 0 开始）
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// 数据块状态（响应式字段）
        /// </summary>
        public ObservableField<ChunkState> State { get; }

        /// <summary>
        /// 实际数据（null 表示占位符，未加载）
        /// </summary>
        public ObservableField<byte[]> Data { get; }

        /// <summary>
        /// 缓存时间（用于 LRU 淘汰）
        /// </summary>
        public ObservableField<DateTime?> CachedTime { get; }

        /// <summary>
        /// 访问次数（用于统计和 LRU 优化）
        /// </summary>
        public ObservableField<int> AccessCount { get; }

        /// <summary>
        /// 创建数据块槽位
        /// </summary>
        /// <param name="index">数据块索引</param>
        public ChunkSlot(int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Chunk index cannot be negative");

            Index = index;
            State = new ObservableField<ChunkState>(ChunkState.Empty);
            Data = new ObservableField<byte[]>(null);
            CachedTime = new ObservableField<DateTime?>(null);
            AccessCount = new ObservableField<int>(0);
        }

        /// <summary>
        /// 设置数据块数据（标记为 Ready）
        /// </summary>
        /// <param name="data">数据内容</param>
        public void SetData(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("Chunk data cannot be null or empty", nameof(data));
            }

            Data.Value = data;
            State.Value = ChunkState.Ready;
            CachedTime.Value = DateTime.UtcNow;
        }

        /// <summary>
        /// 清空数据块（用于垃圾回收）
        /// </summary>
        public void Clear()
        {
            Data.Value = null;
            State.Value = ChunkState.Empty;
            CachedTime.Value = null;
            AccessCount.Value = 0;
        }

        /// <summary>
        /// 记录一次访问（用于 LRU 统计）
        /// </summary>
        public void RecordAccess()
        {
            AccessCount.Value++;
        }

        /// <summary>
        /// 检查数据块是否就绪
        /// </summary>
        public bool IsReady => State.Value == ChunkState.Ready && Data.Value != null;

        /// <summary>
        /// 检查数据块是否为空
        /// </summary>
        public bool IsEmpty => State.Value == ChunkState.Empty && Data.Value == null;

        /// <summary>
        /// 获取数据块大小（字节）
        /// </summary>
        public int Size => Data.Value?.Length ?? 0;

        /// <summary>
        /// 计算内存占用（字节）
        /// </summary>
        public long MemoryUsage => Data.Value?.Length ?? 0;

        public override string ToString()
        {
            return $"Chunk[{Index}] State={State.Value}, Size={Size} bytes, Accesses={AccessCount.Value}";
        }
    }
}
