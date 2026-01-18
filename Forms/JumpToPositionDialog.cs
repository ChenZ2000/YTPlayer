using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 跳转到指定位置对话框
    /// 支持时间格式 (HH:MM:SS, MM:SS, SS) 和百分比格式 (XX%)
    /// </summary>
    public partial class JumpToPositionDialog : Form
    {
        private TextBox txtPosition = null!;
        private Button btnJump = null!;
        private Button btnCancel = null!;
        private Label lblInstruction = null!;
        private Label lblCurrentPosition = null!;

        private double _currentPosition;
        private double _duration;
        private double _targetPosition = -1;

        /// <summary>
        /// 获取目标位置（秒）
        /// </summary>
        public double TargetPosition => _targetPosition;

        public JumpToPositionDialog(double currentPosition, double duration)
        {
            _currentPosition = currentPosition;
            _duration = duration;

            InitializeComponent();
            InitializeCustomComponents();
        }

        private void InitializeComponent()
        {
            this.txtPosition = new TextBox();
            this.btnJump = new Button();
            this.btnCancel = new Button();
            this.lblInstruction = new Label();
            this.lblCurrentPosition = new Label();

            this.SuspendLayout();

            //
            // lblInstruction
            //
            this.lblInstruction.AutoSize = true;
            this.lblInstruction.Location = new System.Drawing.Point(12, 15);
            this.lblInstruction.Name = "lblInstruction";
            this.lblInstruction.Size = new System.Drawing.Size(300, 13);
            this.lblInstruction.TabIndex = 0;
            this.lblInstruction.Text = "输入跳转位置 (格式: HH:MM:SS, MM:SS, SS 或 XX%)";

            //
            // lblCurrentPosition
            //
            this.lblCurrentPosition.AutoSize = true;
            this.lblCurrentPosition.Location = new System.Drawing.Point(12, 40);
            this.lblCurrentPosition.Name = "lblCurrentPosition";
            this.lblCurrentPosition.Size = new System.Drawing.Size(200, 13);
            this.lblCurrentPosition.TabIndex = 1;
            this.lblCurrentPosition.Text = "当前位置: 00:00 / 00:00";

            //
            // txtPosition
            //
            this.txtPosition.Location = new System.Drawing.Point(12, 65);
            this.txtPosition.Name = "txtPosition";
            this.txtPosition.Size = new System.Drawing.Size(300, 22);
            this.txtPosition.TabIndex = 2;
            this.txtPosition.KeyPress += new KeyPressEventHandler(this.txtPosition_KeyPress);

            //
            // btnJump
            //
            this.btnJump.Location = new System.Drawing.Point(130, 100);
            this.btnJump.Name = "btnJump";
            this.btnJump.Size = new System.Drawing.Size(85, 30);
            this.btnJump.TabIndex = 3;
            this.btnJump.Text = "跳转(&J)";
            this.btnJump.UseVisualStyleBackColor = true;
            this.btnJump.Click += new EventHandler(this.btnJump_Click);

            //
            // btnCancel
            //
            this.btnCancel.DialogResult = DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(227, 100);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(85, 30);
            this.btnCancel.TabIndex = 4;
            this.btnCancel.Text = "取消(&C)";
            this.btnCancel.UseVisualStyleBackColor = true;

            //
            // JumpToPositionDialog
            //
            this.AcceptButton = this.btnJump;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(324, 145);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnJump);
            this.Controls.Add(this.txtPosition);
            this.Controls.Add(this.lblCurrentPosition);
            this.Controls.Add(this.lblInstruction);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "JumpToPositionDialog";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "跳转到位置";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void InitializeCustomComponents()
        {
            // 显示当前位置
            string currentTime = FormatTime(_currentPosition);
            string durationTime = FormatTime(_duration);
            lblCurrentPosition.Text = $"当前位置: {currentTime} / {durationTime}";

            // 预填充当前位置并选中
            txtPosition.Text = currentTime;
            txtPosition.SelectAll();
            txtPosition.Focus();
        }

        private void txtPosition_KeyPress(object? sender, KeyPressEventArgs e)
        {
            // 按回车键直接跳转
            if (e.KeyChar == (char)Keys.Return)
            {
                e.Handled = true;
                btnJump_Click(sender, EventArgs.Empty);
            }
        }

        private void btnJump_Click(object? sender, EventArgs e)
        {
            string input = txtPosition.Text.Trim();

            if (string.IsNullOrEmpty(input))
            {
                MessageBox.Show("请输入跳转位置", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPosition.Focus();
                return;
            }

            // 尝试解析输入
            double? targetSeconds = ParseInput(input);

            if (targetSeconds == null)
            {
                MessageBox.Show(
                    "无效的输入格式\n\n支持的格式:\n• 时间: HH:MM:SS, MM:SS 或 SS\n• 百分比: XX%",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                txtPosition.SelectAll();
                txtPosition.Focus();
                return;
            }

            // 验证范围
            if (targetSeconds.Value < 0 || targetSeconds.Value > _duration)
            {
                string msg = targetSeconds.Value < 0
                    ? "跳转位置不能为负数"
                    : $"跳转位置超出歌曲长度 ({FormatTime(_duration)})";

                MessageBox.Show(msg, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                txtPosition.SelectAll();
                txtPosition.Focus();
                return;
            }

            // 设置目标位置并关闭对话框
            _targetPosition = targetSeconds.Value;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        /// <summary>
        /// 解析输入（支持时间和百分比格式）
        /// 时间格式：单个数字=秒数，两个数字(1个冒号)=分:秒，三个数字(2个冒号)=小时:分:秒
        /// 每个组件允许任意位数和大小，自动处理溢出（如 120 秒 = 2 分钟）
        /// </summary>
        private double? ParseInput(string input)
        {
            input = input.Trim();

            // 百分比格式: XX%
            if (input.EndsWith("%"))
            {
                string percentStr = input.Substring(0, input.Length - 1).Trim();
                if (double.TryParse(percentStr, out double percent))
                {
                    if (percent >= 0 && percent <= 100)
                    {
                        return _duration * (percent / 100.0);
                    }
                }
                return null;
            }

            // 时间格式: 根据冒号数量判断格式
            string[] parts = input.Split(':');

            if (parts.Length == 1)
            {
                // 单个数字: 秒数（允许任意大小，如 120 = 2分钟）
                if (double.TryParse(parts[0], out double seconds))
                {
                    return seconds >= 0 ? seconds : null;
                }
            }
            else if (parts.Length == 2)
            {
                // 两个数字: 分:秒（允许溢出，如 120:120 = 122分钟）
                if (double.TryParse(parts[0], out double minutes) &&
                    double.TryParse(parts[1], out double seconds))
                {
                    double totalSeconds = minutes * 60 + seconds;
                    return totalSeconds >= 0 ? totalSeconds : null;
                }
            }
            else if (parts.Length == 3)
            {
                // 三个数字: 小时:分:秒（允许溢出）
                if (double.TryParse(parts[0], out double hours) &&
                    double.TryParse(parts[1], out double minutes) &&
                    double.TryParse(parts[2], out double seconds))
                {
                    double totalSeconds = hours * 3600 + minutes * 60 + seconds;
                    return totalSeconds >= 0 ? totalSeconds : null;
                }
            }

            return null;
        }

        /// <summary>
        /// 格式化时间为 HH:MM:SS 或 MM:SS
        /// </summary>
        private string FormatTime(double seconds)
        {
            if (seconds < 0)
                seconds = 0;

            TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);

            if (timeSpan.TotalHours >= 1)
            {
                return string.Format("{0:D2}:{1:D2}:{2:D2}",
                    (int)timeSpan.TotalHours,
                    timeSpan.Minutes,
                    timeSpan.Seconds);
            }
            else
            {
                return string.Format("{0:D2}:{1:D2}",
                    timeSpan.Minutes,
                    timeSpan.Seconds);
            }
        }
    }
}

