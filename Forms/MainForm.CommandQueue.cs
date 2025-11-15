using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Download;
using YTPlayer.Models;

namespace YTPlayer
{
    /// <summary>
    /// MainForm 的命令队列集成（部分类）
    /// </summary>
    public partial class MainForm
    {
        // 新架构组件（暂时未使用，保留以备将来扩展）
        // private PlaybackCommandQueue _commandQueue;

        /// <summary>
        /// 初始化命令队列系统（暂时未使用，保留以备将来扩展）
        /// </summary>
        private void InitializeCommandQueueSystem()
        {
            // 当前架构直接使用 BassAudioEngine，暂不需要命令队列层
            return;

            /*
            System.Diagnostics.Debug.WriteLine("[MainForm] 初始化命令队列系统...");

            // 创建命令队列（状态机已在 BassAudioEngine 中创建）
            _commandQueue = new PlaybackCommandQueue(_audioEngine, _playbackQueue);
            _commandQueue.CommandStateChanged += OnCommandStateChanged;

            // 设置下一曲/上一曲回调
            _commandQueue.OnExecuteNext = ExecuteNextCommandAsync;
            _commandQueue.OnExecutePrevious = ExecutePreviousCommandAsync;

            System.Diagnostics.Debug.WriteLine("[MainForm] ✅ 命令队列系统初始化完成");
            */
        }

        /// <summary>
        /// 释放命令队列系统资源（暂时未使用）
        /// </summary>
        private void DisposeCommandQueueSystem()
        {
            // _commandQueue?.Dispose();
        }

        /*
        #region 命令提交方法（供 UI 事件调用）- 暂时未使用

        /// <summary>
        /// 提交播放命令
        /// </summary>
        private async Task SubmitPlayCommandAsync(SongInfo song)
        {
            if (song == null) return;

            var command = new PlaybackCommand
            {
                Type = CommandType.Play,
                Payload = song,
                Priority = 100,
                CancellationToken = CancellationToken.None
            };

            await _commandQueue.EnqueueCommandAsync(command).ConfigureAwait(false);
        }

        /// <summary>
        /// 提交暂停命令
        /// </summary>
        private async Task SubmitPauseCommandAsync()
        {
            var command = new PlaybackCommand
            {
                Type = CommandType.Pause,
                Priority = 100,
                CancellationToken = CancellationToken.None
            };

            await _commandQueue.EnqueueCommandAsync(command).ConfigureAwait(false);
        }

        /// <summary>
        /// 提交恢复播放命令
        /// </summary>
        private async Task SubmitResumeCommandAsync()
        {
            var command = new PlaybackCommand
            {
                Type = CommandType.Resume,
                Priority = 100,
                CancellationToken = CancellationToken.None
            };

            await _commandQueue.EnqueueCommandAsync(command).ConfigureAwait(false);
        }

        /// <summary>
        /// 提交 Seek 命令
        /// </summary>
        private async Task SubmitSeekCommandAsync(double targetSeconds)
        {
            var command = new PlaybackCommand
            {
                Type = CommandType.Seek,
                Payload = targetSeconds,
                Priority = 100,
                CancellationToken = CancellationToken.None
            };

            await _commandQueue.EnqueueCommandAsync(command).ConfigureAwait(false);
        }

        /// <summary>
        /// 提交下一曲命令
        /// </summary>
        private async Task SubmitNextCommandAsync()
        {
            var command = new PlaybackCommand
            {
                Type = CommandType.Next,
                Priority = 100,
                CancellationToken = CancellationToken.None
            };

            await _commandQueue.EnqueueCommandAsync(command).ConfigureAwait(false);
        }

        /// <summary>
        /// 提交上一曲命令
        /// </summary>
        private async Task SubmitPreviousCommandAsync()
        {
            var command = new PlaybackCommand
            {
                Type = CommandType.Previous,
                Priority = 100,
                CancellationToken = CancellationToken.None
            };

            await _commandQueue.EnqueueCommandAsync(command).ConfigureAwait(false);
        }

        #endregion
        */

        #region 命令执行回调（由 CommandQueue 调用）

        /// <summary>
        /// 执行下一曲命令
        /// </summary>
        private async Task<CommandResult> ExecuteNextCommandAsync(PlaybackCommand command, CancellationToken ct)
        {
            try
            {
                // 调用原有的下一曲逻辑（需要在 UI 线程）
                bool success = false;

                await Task.Run(() =>
                {
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() =>
                        {
                            PlayNext(isManual: true);
                            success = true;
                        }));
                    }
                    else
                    {
                        PlayNext(isManual: true);
                        success = true;
                    }
                }, ct).ConfigureAwait(false);

                return success ? CommandResult.Success : CommandResult.Error("下一曲执行失败");
            }
            catch (Exception ex)
            {
                return CommandResult.Error(ex);
            }
        }

        /// <summary>
        /// 执行上一曲命令
        /// </summary>
        private async Task<CommandResult> ExecutePreviousCommandAsync(PlaybackCommand command, CancellationToken ct)
        {
            try
            {
                // 调用原有的上一曲逻辑（需要在 UI 线程）
                bool success = false;

                await Task.Run(() =>
                {
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() =>
                        {
                            PlayPrevious(isManual: true);
                            success = true;
                        }));
                    }
                    else
                    {
                        PlayPrevious(isManual: true);
                        success = true;
                    }
                }, ct).ConfigureAwait(false);

                return success ? CommandResult.Success : CommandResult.Error("上一曲执行失败");
            }
            catch (Exception ex)
            {
                return CommandResult.Error(ex);
            }
        }

        #endregion

        #region 事件处理

        /// <summary>
        /// 命令状态变更事件处理
        /// </summary>
        private void OnCommandStateChanged(object sender, CommandStateChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainForm] 命令状态变更: {e.Command.Type} - {e.State}");

            // 更新 UI（在 UI 线程）
            SafeInvoke(() =>
            {
                switch (e.State)
                {
                    case CommandState.Executing:
                        UpdateStatusBarForCommandExecuting(e.Command);
                        break;

                    case CommandState.Completed:
                        UpdateStatusBarForCommandCompleted(e.Command);
                        break;

                    case CommandState.Cancelled:
                        UpdateStatusBar("操作已取消");
                        break;

                    case CommandState.Failed:
                        UpdateStatusBar($"操作失败: {e.Message ?? "未知错误"}");
                        break;
                }
            });
        }

        /// <summary>
        /// 播放状态变更事件处理
        /// </summary>
        private void OnPlaybackStateChanged(object sender, StateTransitionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainForm] 播放状态变更: {e.OldState} → {e.NewState}");

            // 更新 UI（在 UI 线程）
            SafeInvoke(() =>
            {
                UpdateUIForPlaybackState(e.NewState);
                bool playbackActive = e.NewState == PlaybackState.Playing ||
                                      e.NewState == PlaybackState.Buffering ||
                                      e.NewState == PlaybackState.Loading;
                DownloadBandwidthCoordinator.Instance.NotifyPlaybackStateChanged(playbackActive);
            });
        }

        #endregion

        #region UI 更新方法

        /// <summary>
        /// 命令执行中的状态栏更新
        /// </summary>
        private void UpdateStatusBarForCommandExecuting(PlaybackCommand command)
        {
            switch (command.Type)
            {
                case CommandType.Play when command.Payload is SongInfo playSong:
                    UpdateStatusBar($"正在播放: {playSong.Name}");
                    break;

                case CommandType.Play:
                    UpdateStatusBar("正在播放...");
                    break;

                case CommandType.Pause:
                    UpdateStatusBar("暂停中...");
                    break;

                case CommandType.Resume:
                    UpdateStatusBar("恢复播放...");
                    break;

                case CommandType.Seek when command.Payload is double position:
                    UpdateStatusBar($"跳转到 {FormatTimeFromSeconds(position)}");
                    break;

                case CommandType.Seek:
                    UpdateStatusBar("跳转中...");
                    break;

                case CommandType.Next:
                    UpdateStatusBar("切换下一曲...");
                    break;

                case CommandType.Previous:
                    UpdateStatusBar("切换上一曲...");
                    break;
            }
        }

        /// <summary>
        /// 命令完成的状态栏更新
        /// </summary>
        private void UpdateStatusBarForCommandCompleted(PlaybackCommand command)
        {
            switch (command.Type)
            {
                case CommandType.Play when command.Payload is SongInfo playSong:
                    UpdateStatusBar($"正在播放: {playSong.Name} - {playSong.Artist}");
                    break;

                case CommandType.Play:
                    UpdateStatusBar("正在播放");
                    break;

                case CommandType.Pause:
                    UpdateStatusBar("已暂停");
                    break;

                case CommandType.Resume:
                    UpdateStatusBar("正在播放");
                    break;

                case CommandType.Seek:
                    UpdateStatusBar("跳转完成");
                    break;

                case CommandType.Next:
                case CommandType.Previous:
                    // 状态由 PlayNext/PlayPrevious 方法更新
                    break;
            }
        }

        /// <summary>
        /// 根据播放状态更新 UI
        /// </summary>
        private void UpdateUIForPlaybackState(PlaybackState state)
        {
            switch (state)
            {
                case PlaybackState.Idle:
                    playPauseButton.Text = "播放";
                    playPauseButton.Enabled = true;
                    break;

                case PlaybackState.Loading:
                    playPauseButton.Text = "加载中...";
                    playPauseButton.Enabled = false;
                    break;

                case PlaybackState.Buffering:
                    playPauseButton.Text = "缓冲中...";
                    playPauseButton.Enabled = false;
                    break;

                case PlaybackState.Playing:
                    playPauseButton.Text = "暂停";
                    playPauseButton.Enabled = true;
                    break;

                case PlaybackState.Paused:
                    playPauseButton.Text = "播放";
                    playPauseButton.Enabled = true;
                    break;

                case PlaybackState.Stopped:
                    playPauseButton.Text = "播放";
                    playPauseButton.Enabled = true;
                    break;
            }
        }

        /// <summary>
        /// 安全的 UI 调用（自动处理跨线程）
        /// </summary>
        private void SafeInvoke(Action action)
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        #endregion
    }
}
