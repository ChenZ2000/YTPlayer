using System;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog : Form
    {
        private readonly CommentTreeView _commentTree;
        private readonly Button _refreshButton;
        private readonly ComboBox _sortComboBox;
        private readonly Label _replyHintLabel;
        private readonly TextBox _inputBox;
        private readonly Button _sendButton;
        private readonly Button _closeButton;

        public CommentsDialog(NeteaseApiClient apiClient, CommentTarget target, string? currentUserId, bool isLoggedIn)
        {
            _ = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _ = target ?? throw new ArgumentNullException(nameof(target));
            _apiClient = apiClient;
            _target = target;
            _currentUserId = currentUserId;
            _isLoggedIn = isLoggedIn;

            string titleName = string.IsNullOrWhiteSpace(target.DisplayName) ? "评论" : target.DisplayName;
            Text = $"评论 - {titleName}";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            Width = 720;
            Height = 520;
            MinimumSize = new Size(560, 380);
            KeyPreview = true;

            _refreshButton = new Button
            {
                Text = "刷新(F5)",
                AutoSize = true
            };

            var sortLabel = new Label
            {
                Text = "排序:",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(12, 6, 4, 0)
            };

            _sortComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120
            };
            _sortComboBox.Items.AddRange(new object[]
            {
                "推荐",
                "热度",
                "时间"
            });
            _sortComboBox.SelectedIndex = 0;

            var toolPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(8)
            };
            toolPanel.Controls.Add(_refreshButton);
            toolPanel.Controls.Add(sortLabel);
            toolPanel.Controls.Add(_sortComboBox);

            using (new AccessibilitySwitchScope(this, enableLegacy: false))
            {
                _commentTree = new CommentTreeView
                {
                    Dock = DockStyle.Fill,
                    HideSelection = false,
                    ShowLines = true,
                    ShowPlusMinus = true,
                    ShowRootLines = true,
                    AccessibleRole = AccessibleRole.Outline,
                    AccessibleName = "评论",
                    AccessibleDescription = "使用方向键浏览评论，右箭头展开楼层回复。",
                    TabIndex = 0
                };
                _ = _commentTree.AccessibilityObject;
            }

            _replyHintLabel = new Label
            {
                AutoSize = true,
                Text = " ",
                Visible = false,
                Margin = new Padding(0, 0, 0, 6)
            };

            _inputBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                AccessibleName = "评论内容",
                AccessibleDescription = "输入评论内容，Enter 换行，Shift+Enter 发送。",
                TabIndex = 1
            };

            _sendButton = new Button
            {
                Text = "发送",
                AutoSize = true,
                TabIndex = 2,
                Enabled = isLoggedIn
            };

            _closeButton = new Button
            {
                Text = "关闭",
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                TabIndex = 3
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(8, 0, 0, 0)
            };
            buttonPanel.Controls.Add(_sendButton);
            buttonPanel.Controls.Add(_closeButton);

            var inputLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2
            };
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            inputLayout.Controls.Add(_replyHintLabel, 0, 0);
            inputLayout.SetColumnSpan(_replyHintLabel, 2);
            inputLayout.Controls.Add(_inputBox, 0, 1);
            inputLayout.Controls.Add(buttonPanel, 1, 1);

            var inputPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 140,
                Padding = new Padding(8)
            };
            inputPanel.Controls.Add(inputLayout);

            Controls.Add(_commentTree);
            Controls.Add(inputPanel);
            Controls.Add(toolPanel);

            CancelButton = _closeButton;

            InitializeState();
            InitializeAccessibilityAnnouncements();
            InitializeEvents();
        }

        private sealed class AccessibilitySwitchScope : IDisposable
        {
            private readonly CommentsDialog _owner;
            private readonly bool _prev1;
            private readonly bool _prev2;
            private readonly bool _prev3;

            public AccessibilitySwitchScope(CommentsDialog owner, bool enableLegacy)
            {
                _owner = owner;
                AppContext.TryGetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures", out _prev1);
                AppContext.TryGetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.2", out _prev2);
                AppContext.TryGetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.3", out _prev3);

                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures", enableLegacy);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.2", enableLegacy);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.3", enableLegacy);
                _owner.LogComments($"AccSwitch override legacy={enableLegacy} prev={_prev1},{_prev2},{_prev3}");
            }

            public void Dispose()
            {
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures", _prev1);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.2", _prev2);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.3", _prev3);
                _owner.LogComments($"AccSwitch restore legacy={_prev1},{_prev2},{_prev3}");
            }
        }
    }
}
