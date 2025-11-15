namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 缓冲状态枚举，兼容旧版事件。
    /// </summary>
    public enum BufferingState
    {
        Idle,
        Buffering,
        Ready,
        Playing,
        LowBuffer
    }
}
