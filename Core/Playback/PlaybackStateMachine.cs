using System;
using System.Collections.Generic;

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 播放状态机 - 管理播放状态转换，防止非法状态
    /// </summary>
    public class PlaybackStateMachine
    {
        private PlaybackState _currentState = PlaybackState.Idle;
        private readonly object _stateLock = new object();

        // 状态转换映射表
        private static readonly Dictionary<PlaybackState, HashSet<PlaybackState>> _validTransitions =
            new Dictionary<PlaybackState, HashSet<PlaybackState>>
            {
                [PlaybackState.Idle] = new HashSet<PlaybackState>
                {
                    PlaybackState.Loading
                },
                [PlaybackState.Loading] = new HashSet<PlaybackState>
                {
                    PlaybackState.Buffering,
                    PlaybackState.Playing,   // 🔧 缓存就绪后直接播放（WaitForCacheReadyAsync）
                    PlaybackState.Idle,      // 加载失败回到 Idle
                    PlaybackState.Stopped    // 用户取消加载
                },
                [PlaybackState.Buffering] = new HashSet<PlaybackState>
                {
                    PlaybackState.Playing,
                    PlaybackState.Idle,      // 缓冲失败
                    PlaybackState.Stopped,   // 用户取消
                    PlaybackState.Loading    // 🔧 用户切换歌曲（新增）
                },
                [PlaybackState.Playing] = new HashSet<PlaybackState>
                {
                    PlaybackState.Paused,
                    PlaybackState.Stopped,
                    PlaybackState.Buffering,  // 网络问题导致重新缓冲
                    PlaybackState.Idle,       // 播放完成
                    PlaybackState.Loading     // 🔧 用户切换歌曲（新增）
                },
                [PlaybackState.Paused] = new HashSet<PlaybackState>
                {
                    PlaybackState.Playing,
                    PlaybackState.Stopped,
                    PlaybackState.Idle,
                    PlaybackState.Loading     // 🔧 用户切换歌曲（新增）
                },
                [PlaybackState.Stopped] = new HashSet<PlaybackState>
                {
                    PlaybackState.Idle,
                    PlaybackState.Loading     // 停止后可以直接加载新歌曲
                }
            };

        public event EventHandler<StateTransitionEventArgs> StateChanged;

        /// <summary>
        /// 当前状态
        /// </summary>
        public PlaybackState CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentState;
                }
            }
        }

        /// <summary>
        /// 尝试转换到新状态
        /// </summary>
        /// <param name="newState">目标状态</param>
        /// <returns>是否转换成功</returns>
        public bool TransitionTo(PlaybackState newState)
        {
            lock (_stateLock)
            {
                // 相同状态，不需要转换
                if (_currentState == newState)
                {
                    return true;
                }

                // 验证状态转换是否合法
                if (!IsValidTransition(_currentState, newState))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[StateMachine] ❌ 非法状态转换: {_currentState} → {newState}");
                    return false;
                }

                var oldState = _currentState;
                _currentState = newState;

                System.Diagnostics.Debug.WriteLine(
                    $"[StateMachine] ✓ 状态转换: {oldState} → {newState}");

                // 触发状态变更事件（在锁外触发，避免死锁）
                var args = new StateTransitionEventArgs(oldState, newState);
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        StateChanged?.Invoke(this, args);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[StateMachine] 状态变更事件处理异常: {ex.Message}");
                    }
                });

                return true;
            }
        }

        /// <summary>
        /// 强制设置状态（跳过合法性检查，仅用于错误恢复）
        /// </summary>
        public void ForceSetState(PlaybackState newState)
        {
            lock (_stateLock)
            {
                var oldState = _currentState;
                _currentState = newState;

                System.Diagnostics.Debug.WriteLine(
                    $"[StateMachine] ⚠️ 强制设置状态: {oldState} → {newState}");

                var args = new StateTransitionEventArgs(oldState, newState);
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    StateChanged?.Invoke(this, args);
                });
            }
        }

        /// <summary>
        /// 检查状态转换是否合法
        /// </summary>
        private bool IsValidTransition(PlaybackState from, PlaybackState to)
        {
            if (!_validTransitions.TryGetValue(from, out var allowedStates))
            {
                // 未定义的状态，不允许转换
                return false;
            }

            return allowedStates.Contains(to);
        }

        /// <summary>
        /// 重置到初始状态
        /// </summary>
        public void Reset()
        {
            TransitionTo(PlaybackState.Idle);
        }

        public override string ToString()
        {
            return $"StateMachine[Current={CurrentState}]";
        }
    }

    /// <summary>
    /// 播放状态枚举
    /// </summary>
    public enum PlaybackState
    {
        /// <summary>
        /// 空闲状态（初始状态）
        /// </summary>
        Idle,

        /// <summary>
        /// 加载中（获取 URL、创建流）
        /// </summary>
        Loading,

        /// <summary>
        /// 缓冲中（下载音频数据）
        /// </summary>
        Buffering,

        /// <summary>
        /// 播放中
        /// </summary>
        Playing,

        /// <summary>
        /// 已暂停
        /// </summary>
        Paused,

        /// <summary>
        /// 已停止
        /// </summary>
        Stopped
    }

    /// <summary>
    /// 状态转换事件参数
    /// </summary>
    public class StateTransitionEventArgs : EventArgs
    {
        public PlaybackState OldState { get; }
        public PlaybackState NewState { get; }

        public StateTransitionEventArgs(PlaybackState oldState, PlaybackState newState)
        {
            OldState = oldState;
            NewState = newState;
        }

        public override string ToString()
        {
            return $"StateTransition[{OldState} → {NewState}]";
        }
    }
}
