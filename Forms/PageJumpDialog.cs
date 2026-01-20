using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YTPlayer.Utils;
using MessageBox = YTPlayer.MessageBox;

namespace YTPlayer.Forms
{
    internal sealed class PageJumpDialog : Form
    {
        private const int EmSetCueBanner = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private readonly int _currentPage;
        private readonly int _maxPage;
        private readonly string _cueText;

        private TextBox _pageTextBox = null!;
        private Button _jumpButton = null!;
        private Button _firstButton = null!;
        private Button _lastButton = null!;
        private Button _cancelButton = null!;

        public int TargetPage { get; private set; } = -1;

        public PageJumpDialog(int currentPage, int maxPage)
        {
            _currentPage = Math.Max(1, currentPage);
            _maxPage = Math.Max(1, maxPage);
            _cueText = $"当前 {_currentPage}/{_maxPage} 页";

            InitializeComponent();
            ThemeManager.ApplyTheme(this);
        }

        private void InitializeComponent()
        {
            Text = "跳转";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ClientSize = new Size(380, 140);

            _pageTextBox = new TextBox
            {
                Location = new Point(15, 20),
                Size = new Size(340, 28),
                TabIndex = 0
            };
            _pageTextBox.AccessibleName = $"当前 {_currentPage}/{_maxPage} 页";
            _pageTextBox.KeyDown += OnPageTextBoxKeyDown;
            _pageTextBox.HandleCreated += (_, _) => ApplyCueBanner();

            _jumpButton = new Button
            {
                Text = "跳转(&J)",
                Location = new Point(85, 85),
                Size = new Size(80, 30),
                TabIndex = 1
            };
            _jumpButton.Click += (_, _) => ConfirmJump();

            _firstButton = new Button
            {
                Text = "第一页(&F)",
                Location = new Point(175, 85),
                Size = new Size(80, 30),
                TabIndex = 2
            };
            _firstButton.Click += (_, _) => SetTargetAndClose(1);

            _lastButton = new Button
            {
                Text = "最后一页(&L)",
                Location = new Point(265, 85),
                Size = new Size(80, 30),
                TabIndex = 3
            };
            _lastButton.Click += (_, _) => SetTargetAndClose(_maxPage);

            _cancelButton = new Button
            {
                Text = "取消(&C)",
                DialogResult = DialogResult.Cancel,
                Location = new Point(15, 85),
                Size = new Size(60, 30),
                TabIndex = 4
            };
            _cancelButton.Click += (_, _) => Close();

            Controls.Add(_pageTextBox);
            Controls.Add(_jumpButton);
            Controls.Add(_firstButton);
            Controls.Add(_lastButton);
            Controls.Add(_cancelButton);

            AcceptButton = _jumpButton;
            CancelButton = _cancelButton;

            Shown += (_, _) =>
            {
                _pageTextBox.Focus();
                _pageTextBox.SelectAll();
            };
        }

        private void ApplyCueBanner()
        {
            if (!_pageTextBox.IsHandleCreated)
            {
                return;
            }
            SendMessage(_pageTextBox.Handle, EmSetCueBanner, (IntPtr)1, _cueText);
        }

        private void OnPageTextBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                ConfirmJump();
            }
        }

        private void ConfirmJump()
        {
            string input = _pageTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("请输入要跳转的页码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                _pageTextBox.Focus();
                return;
            }

            int page = ParsePageNumber(input);
            if (page < 1 || page > _maxPage)
            {
                MessageBox.Show($"页码超出范围 (1-{_maxPage})", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                _pageTextBox.SelectAll();
                _pageTextBox.Focus();
                return;
            }

            SetTargetAndClose(page);
        }

        private static int ParsePageNumber(string input)
        {
            int slashIndex = input.IndexOf('/');
            if (slashIndex >= 0)
            {
                input = input.Substring(0, slashIndex);
            }
            input = input.Trim();
            return int.TryParse(input, out int page) ? page : -1;
        }

        private void SetTargetAndClose(int page)
        {
            TargetPage = page;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}


