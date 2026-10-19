using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core.Recognition;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class SongRecognitionDialog : Form
    {
        private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "聆听设备" };
        private readonly ComboBox _duration = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "最长聆听时间" };
        private readonly TextBox _status = new() { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, AccessibleName = "识曲状态", Text = "选择麦克风收音，或选择系统内录识别电脑正在播放的音乐。识别成功后自动结束。" };
        private readonly Button _start = new() { Text = "开始(&S)", AutoSize = true, AccessibleName = "开始听歌识曲" };
        private readonly Button _cancel = new() { Text = "取消（Esc）(&C)", AutoSize = true, AccessibleName = "取消听歌识曲", DialogResult = DialogResult.Cancel };
        private readonly Func<AudioInputDeviceInfo, int, CancellationToken, IProgress<SongRecognitionProgress>, Task<SongRecognitionResult>> _recognize;
        private readonly CancellationToken _ownerToken;
        private CancellationTokenSource? _running;
        private bool _closeRequested;
        public AudioInputDeviceInfo? SelectedDevice => _device.SelectedItem as AudioInputDeviceInfo;
        public int SelectedDurationSeconds => new[] { 6, 15, 24 }[Math.Max(0, _duration.SelectedIndex)];
        public SongRecognitionResult? Result { get; private set; }

        public SongRecognitionDialog(IReadOnlyList<AudioInputDeviceInfo> devices, string? preferredDeviceId,
            Func<AudioInputDeviceInfo, int, CancellationToken, IProgress<SongRecognitionProgress>, Task<SongRecognitionResult>> recognize,
            CancellationToken ownerToken = default)
        {
            if (devices.Count == 0) throw new ArgumentException("设备列表不能为空。", nameof(devices));
            _recognize = recognize; _ownerToken = ownerToken;
            Text = "听歌识曲"; AccessibleName = Text;
            AccessibleDescription = "识别过程中按 Esc 可取消；首次未匹配时会继续聆听。";
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font; Font = new Font("Microsoft YaHei UI", 10F);
            ClientSize = new Size(500, 290); MinimumSize = new Size(480, 320);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
            layout.Controls.Add(new Label { Text = "聆听设备", AutoSize = true }, 0, 0);
            layout.Controls.Add(_device, 0, 1);
            layout.Controls.Add(new Label { Text = "最长聆听时间", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, 2);
            layout.Controls.Add(_duration, 0, 3);
            _status.Margin = new Padding(0, 12, 0, 12); layout.Controls.Add(_status, 0, 4);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(_cancel); buttons.Controls.Add(_start); layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout); AcceptButton = _start; CancelButton = _cancel;
            foreach (var d in devices.GroupBy(d => d.DeviceId).Select(g => g.First())) _device.Items.Add(d);
            int selected = devices.ToList().FindIndex(d => d.DeviceId == preferredDeviceId);
            if (selected < 0 && preferredDeviceId?.StartsWith("rec:", StringComparison.Ordinal) == true && int.TryParse(preferredDeviceId[4..], out int oldIndex))
                selected = devices.ToList().FindIndex(d => d.BassDeviceIndex == oldIndex);
            _device.SelectedIndex = Math.Max(0, selected);
            _duration.Items.AddRange(new object[] { "快速：最多 6 秒", "标准：最多 15 秒", "较难识别：最多 24 秒" });
            _duration.SelectedIndex = 1;
            _start.Click += async (_, _) => await RecognizeAsync();
            Shown += (_, _) => _device.Focus();
            ThemeManager.ApplyTheme(this);
        }

        private async Task RecognizeAsync()
        {
            if (_running != null || SelectedDevice == null) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_ownerToken);
            _running = cancellation; _closeRequested = false;
            _device.Enabled = _duration.Enabled = _start.Enabled = false;
            // Keep focus off frequently changing text while recording, so a screen
            // reader does not continuously feed progress speech back into the input.
            _cancel.Focus();
            var progress = new Progress<SongRecognitionProgress>(p =>
            {
                if (!IsDisposed && _running == cancellation && !cancellation.IsCancellationRequested)
                    _status.Text = p.Message + $"\r\n最长聆听 {p.MaximumSeconds} 秒；按 Esc 取消。";
            });
            bool success = false;
            try
            {
                Result = await _recognize(SelectedDevice, SelectedDurationSeconds, cancellation.Token, progress);
                cancellation.Token.ThrowIfCancellationRequested();
                if (Result.Matches.Count > 0) success = true;
                else _status.Text = "暂未识别到歌曲。可选择更长聆听时间、调整音量，或确认所选系统内录设备与播放设备一致后重试。";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _closeRequested = true; }
            catch (Exception ex) { _status.Text = "听歌识曲失败：" + ex.Message; }
            finally
            {
                _running = null;
                if (!IsDisposed)
                {
                    _device.Enabled = _duration.Enabled = _start.Enabled = _cancel.Enabled = true;
                    if (success || _closeRequested) { DialogResult = success ? DialogResult.OK : DialogResult.Cancel; Close(); }
                    else _status.Focus();
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running != null)
            {
                e.Cancel = true; _closeRequested = true;
                _running.Cancel(); _cancel.Enabled = false;
                _status.Text = "正在取消并释放音频设备…";
            }
            base.OnFormClosing(e);
        }
    }
}
