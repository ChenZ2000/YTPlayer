using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Utils;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    internal sealed class OutputDeviceDialog : Form
    {
        private readonly List<AudioOutputDeviceInfo> _devices;
        private readonly string _activeDeviceId;
        private ComboBox _deviceComboBox = null!;
        private Button _confirmButton = null!;
        private Button _cancelButton = null!;

        public OutputDeviceDialog(IEnumerable<AudioOutputDeviceInfo> devices, string? activeDeviceId)
        {
            if (devices == null)
            {
                throw new ArgumentNullException(nameof(devices));
            }

            _devices = devices.Where(d => d != null).ToList();
            if (_devices.Count == 0)
            {
                throw new ArgumentException("设备列表不能为空", nameof(devices));
            }

            _activeDeviceId = string.IsNullOrWhiteSpace(activeDeviceId)
                ? AudioOutputDeviceInfo.WindowsDefaultId
                : activeDeviceId!;

            InitializeComponent();
            ThemeManager.ApplyTheme(this);
            PopulateDevices();
        }

        public AudioOutputDeviceInfo? SelectedDevice { get; private set; }

        private void InitializeComponent()
        {
            SuspendLayout();
            Text = "输出设备";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ClientSize = new Size(420, 150);

            var instructionLabel = new Label
            {
                Text = "选择要使用的声音输出设备：",
                AutoSize = true,
                Location = new Point(15, 15)
            };

            _deviceComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                Location = new Point(15, 45),
                Size = new Size(380, 30)
            };

            _deviceComboBox.KeyDown += DeviceComboBoxOnKeyDown;

            _confirmButton = new Button
            {
                Text = "确认(&O)",
                TabIndex = 0,
                DialogResult = DialogResult.OK,
                Location = new Point(230, 95),
                Size = new Size(80, 30)
            };
            _confirmButton.Click += ConfirmSelection;

            _cancelButton = new Button
            {
                Text = "取消(&C)",
                TabIndex = 1,
                DialogResult = DialogResult.Cancel,
                Location = new Point(320, 95),
                Size = new Size(80, 30)
            };
            _cancelButton.Click += (_, _) => Close();

            var buttons = DialogLayout.CreateButtonRow(_cancelButton, _confirmButton);
            buttons.TabIndex = 2;
            instructionLabel.TabIndex = 0;
            _deviceComboBox.TabIndex = 1;
            DialogLayout.CreateVertical(this, -1, instructionLabel, _deviceComboBox, buttons);

            AcceptButton = _confirmButton;
            CancelButton = _cancelButton;

            KeyDown += HandleDialogKeyDown;
            Shown += (_, _) => _deviceComboBox.Focus();
            ResumeLayout(true);
        }

        private void PopulateDevices()
        {
            _deviceComboBox.Items.Clear();
            foreach (var device in _devices)
            {
                _deviceComboBox.Items.Add(device);
            }

            int selectedIndex = _devices.FindIndex(d =>
                string.Equals(d.DeviceId, _activeDeviceId, StringComparison.OrdinalIgnoreCase));

            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            if (_deviceComboBox.Items.Count > 0)
            {
                _deviceComboBox.SelectedIndex = selectedIndex;
            }
        }

        private void ConfirmSelection(object? sender, EventArgs e)
        {
            if (_deviceComboBox.SelectedItem is AudioOutputDeviceInfo device)
            {
                SelectedDevice = device;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (_deviceComboBox.Items.Count > 0 && _deviceComboBox.Items[0] is AudioOutputDeviceInfo firstDevice)
            {
                SelectedDevice = firstDevice;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void HandleDialogKeyDown(object? sender, KeyEventArgs e)
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
                ConfirmSelection(sender, EventArgs.Empty);
            }
        }
    }
}


