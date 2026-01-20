using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Utils;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    internal sealed class SongRecognitionDialog : Form
    {
        private ComboBox _deviceComboBox = null!;
        private Button _startButton = null!;
        private Button _cancelButton = null!;

        public SongRecognitionDialog(
            IReadOnlyList<AudioInputDeviceInfo> devices,
            string? preferredDeviceId)
        {
            if (devices == null || devices.Count == 0)
            {
                throw new ArgumentException("设备列表不能为空", nameof(devices));
            }

            InitializeComponent();
            ThemeManager.ApplyTheme(this);
            PopulateDevices(devices, preferredDeviceId);
        }

        public AudioInputDeviceInfo? SelectedDevice { get; private set; }
        public int SelectedDurationSeconds => 6; // 固定 6 秒

        private void InitializeComponent()
        {
            Text = "听歌识曲";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ClientSize = new Size(460, 180);

            var deviceLabel = new Label
            {
                Text = "选择聆听设备：",
                AutoSize = true,
                Location = new Point(15, 20)
            };

            _deviceComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                Location = new Point(15, 45),
                Size = new Size(420, 28)
            };

            _deviceComboBox.KeyDown += DeviceComboBoxOnKeyDown;

            _startButton = new Button
            {
                Text = "开始(&S)",
                DialogResult = DialogResult.OK,
                Location = new Point(235, 125),
                Size = new Size(95, 32)
            };
            _startButton.Click += StartButtonOnClick;

            _cancelButton = new Button
            {
                Text = "取消(&C)",
                DialogResult = DialogResult.Cancel,
                Location = new Point(340, 125),
                Size = new Size(95, 32)
            };

            Controls.Add(deviceLabel);
            Controls.Add(_deviceComboBox);
            Controls.Add(_startButton);
            Controls.Add(_cancelButton);

            AcceptButton = _startButton;
            CancelButton = _cancelButton;

            KeyDown += OnDialogKeyDown;
            Shown += (_, _) => _deviceComboBox.Focus();
        }

        private void PopulateDevices(IReadOnlyList<AudioInputDeviceInfo> devices, string? preferredDeviceId)
        {
            _deviceComboBox.Items.Clear();

            var deduped = devices
                .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            foreach (var device in deduped)
            {
                _deviceComboBox.Items.Add(device);
            }

            int selectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(preferredDeviceId))
            {
                selectedIndex = deduped.FindIndex(d =>
                    string.Equals(d.DeviceId, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
                if (selectedIndex < 0)
                {
                    selectedIndex = 0;
                }
            }

            if (_deviceComboBox.Items.Count > 0)
            {
                _deviceComboBox.SelectedIndex = selectedIndex;
            }
        }

        private void StartButtonOnClick(object? sender, EventArgs e)
        {
            if (_deviceComboBox.SelectedItem is AudioInputDeviceInfo device)
            {
                SelectedDevice = device;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            MessageBox.Show(this, "请选择可用的录音设备", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnDialogKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private void DeviceComboBoxOnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                StartButtonOnClick(sender, EventArgs.Empty);
            }
        }
    }
}


