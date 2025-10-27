using System;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 播放命令队列 - 统一管理所有播放命令，确保串行执行和优雅取消
    /// </summary>
    public class PlaybackCommandQueue : IDisposable
    {
        private readonly PlaybackOrchestrator _orchestrator;
        private readonly PlaybackQueueManager _queueManager;
        private readonly SemaphoreSlim _executionSemaphore;
        private CancellationTokenSource _currentCts;
        private bool _disposed;

        // 命令执行回调（用于需要 MainForm 上下文的操作）
        public Func<PlaybackCommand, CancellationToken, Task<CommandResult>> OnExecuteNext { get; set; }
        public Func<PlaybackCommand, CancellationToken, Task<CommandResult>> OnExecutePrevious { get; set; }

        public event EventHandler<CommandStateChangedEventArgs> CommandStateChanged;

        public PlaybackCommandQueue(PlaybackOrchestrator orchestrator, PlaybackQueueManager queueManager)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _queueManager = queueManager ?? throw new ArgumentNullException(nameof(queueManager));
            _executionSemaphore = new SemaphoreSlim(1, 1);  // 确保命令串行执行
        }

        /// <summary>
        /// 提交新命令（会取消当前命令）
        /// </summary>
        public async Task<CommandResult> EnqueueCommandAsync(PlaybackCommand command)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlaybackCommandQueue));

            // 等待获取执行权（串行执行）
            await _executionSemaphore.WaitAsync(command.CancellationToken).ConfigureAwait(false);

            try
            {
                // 取消当前命令
                _currentCts?.Cancel();
                _currentCts?.Dispose();

                // 创建新的取消源（链接外部取消令牌）
                _currentCts = CancellationTokenSource.CreateLinkedTokenSource(command.CancellationToken);
                var ct = _currentCts.Token;

                // 触发命令开始事件
                OnCommandStateChanged(command, CommandState.Executing);

                // 执行命令
                var result = await ExecuteCommandInternalAsync(command, ct).ConfigureAwait(false);

                // 触发命令完成事件
                OnCommandStateChanged(command, result.IsSuccess ? CommandState.Completed : CommandState.Failed, result.ErrorMessage);

                return result;
            }
            catch (OperationCanceledException)
            {
                OnCommandStateChanged(command, CommandState.Cancelled);
                return CommandResult.Cancelled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommandQueue] 命令执行异常: {ex.Message}");
                OnCommandStateChanged(command, CommandState.Failed, ex.Message);
                return CommandResult.Error(ex);
            }
            finally
            {
                _executionSemaphore.Release();
            }
        }

        private async Task<CommandResult> ExecuteCommandInternalAsync(PlaybackCommand command, CancellationToken ct)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandQueue] 开始执行命令: {command.Type}");

            try
            {
                switch (command.Type)
                {
                    case CommandType.Play:
                        var song = (SongInfo)command.Payload;
                        bool playSuccess = await _orchestrator.PlayAsync(song, ct).ConfigureAwait(false);
                        return playSuccess ? CommandResult.Success : CommandResult.Error("播放失败");

                    case CommandType.Pause:
                        bool pauseSuccess = await _orchestrator.PauseAsync(15, ct).ConfigureAwait(false);
                        return pauseSuccess ? CommandResult.Success : CommandResult.Error("暂停失败");

                    case CommandType.Resume:
                        bool resumeSuccess = await _orchestrator.ResumeAsync(15, ct).ConfigureAwait(false);
                        return resumeSuccess ? CommandResult.Success : CommandResult.Error("恢复失败");

                    case CommandType.Seek:
                        double position = (double)command.Payload;
                        bool seekSuccess = await _orchestrator.SeekAsync(position, 15, ct).ConfigureAwait(false);
                        return seekSuccess ? CommandResult.Success : CommandResult.Error("跳转失败");

                    case CommandType.Next:
                        // 调用 MainForm 的下一曲逻辑
                        if (OnExecuteNext != null)
                        {
                            return await OnExecuteNext(command, ct).ConfigureAwait(false);
                        }
                        return CommandResult.Error("下一曲回调未设置");

                    case CommandType.Previous:
                        // 调用 MainForm 的上一曲逻辑
                        if (OnExecutePrevious != null)
                        {
                            return await OnExecutePrevious(command, ct).ConfigureAwait(false);
                        }
                        return CommandResult.Error("上一曲回调未设置");

                    case CommandType.Stop:
                        bool stopSuccess = await _orchestrator.StopAsync().ConfigureAwait(false);
                        return stopSuccess ? CommandResult.Success : CommandResult.Error("停止失败");

                    default:
                        return CommandResult.Error($"未知命令类型: {command.Type}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;  // 取消操作向上传递
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommandQueue] 命令执行内部异常: {ex.Message}");
                return CommandResult.Error(ex);
            }
        }

        private void OnCommandStateChanged(PlaybackCommand command, CommandState state, string message = null)
        {
            CommandStateChanged?.Invoke(this, new CommandStateChangedEventArgs(command, state, message));
        }

        /// <summary>
        /// 取消所有待处理的命令
        /// </summary>
        public void CancelAll()
        {
            _currentCts?.Cancel();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _executionSemaphore?.Dispose();
            _disposed = true;
        }
    }

    #region 命令模型和枚举

    /// <summary>
    /// 命令类型
    /// </summary>
    public enum CommandType
    {
        Play,        // 播放指定歌曲
        Pause,       // 暂停
        Resume,      // 恢复播放
        Stop,        // 停止
        Seek,        // 跳转
        Next,        // 下一曲
        Previous     // 上一曲
    }

    /// <summary>
    /// 命令状态
    /// </summary>
    public enum CommandState
    {
        Queued,      // 排队中
        Executing,   // 执行中
        Completed,   // 已完成
        Cancelled,   // 已取消
        Failed       // 失败
    }

    /// <summary>
    /// 播放命令
    /// </summary>
    public class PlaybackCommand
    {
        public CommandType Type { get; set; }
        public object Payload { get; set; }  // 例如: SongInfo, double(Seek位置)
        public int Priority { get; set; }     // 0 = 低, 100 = 高（预留，当前未使用）
        public CancellationToken CancellationToken { get; set; }

        public override string ToString()
        {
            return $"Command[Type={Type}, Payload={Payload}, Priority={Priority}]";
        }
    }

    /// <summary>
    /// 命令执行结果
    /// </summary>
    public class CommandResult
    {
        public bool IsSuccess { get; private set; }
        public string ErrorMessage { get; private set; }

        public static CommandResult Success { get; } = new CommandResult { IsSuccess = true };
        public static CommandResult Cancelled { get; } = new CommandResult { IsSuccess = false, ErrorMessage = "已取消" };

        public static CommandResult Error(string message)
        {
            return new CommandResult { IsSuccess = false, ErrorMessage = message };
        }

        public static CommandResult Error(Exception ex)
        {
            return new CommandResult { IsSuccess = false, ErrorMessage = ex.Message };
        }

        public override string ToString()
        {
            return IsSuccess ? "Success" : $"Failed: {ErrorMessage}";
        }
    }

    /// <summary>
    /// 命令状态变更事件参数
    /// </summary>
    public class CommandStateChangedEventArgs : EventArgs
    {
        public PlaybackCommand Command { get; }
        public CommandState State { get; }
        public string Message { get; }

        public CommandStateChangedEventArgs(PlaybackCommand command, CommandState state, string message = null)
        {
            Command = command;
            State = state;
            Message = message;
        }

        public override string ToString()
        {
            return $"CommandStateChanged[Command={Command.Type}, State={State}, Message={Message}]";
        }
    }

    #endregion
}
