using System;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// 块数据结构 - 用于流式传输音频数据块
    /// </summary>
    public class ChunkData
    {
        /// <summary>
        /// 块索引（从0开始）
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 块数据（字节数组）
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// 块在文件中的起始位置（字节偏移）
        /// </summary>
        public long Position { get; set; }

        /// <summary>
        /// 块数据大小（字节）
        /// </summary>
        public int Size => Data?.Length ?? 0;

        /// <summary>
        /// 创建块数据
        /// </summary>
        public ChunkData(int index, byte[] data, long position)
        {
            Index = index;
            Data = data ?? Array.Empty<byte>();
            Position = position;
        }

        /// <summary>
        /// 默认构造函数
        /// </summary>
        public ChunkData()
        {
            Data = Array.Empty<byte>();
        }

        public override string ToString()
        {
            return $"Chunk#{Index} (Pos={Position}, Size={Size})";
        }
    }
}
