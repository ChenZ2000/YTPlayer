using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog
    {
        private ContextMenuStrip? _treeMenu;

        private void InitializeEvents()
        {
            Load += OnDialogLoad;
            FormClosed += OnDialogClosed;
            KeyDown += OnDialogKeyDown;

            _refreshButton.Click += OnRefreshClicked;
            _sortComboBox.SelectedIndexChanged += OnSortChanged;
            _sortComboBox.Leave += OnSortComboLeave;
            _sendButton.Click += OnSendClicked;

            _inputBox.KeyDown += OnInputBoxKeyDown;

            _commentTree.BeforeExpand += OnCommentTreeBeforeExpand;
            _commentTree.BeforeSelect += OnCommentTreeBeforeSelect;
            _commentTree.AfterSelect += OnCommentTreeAfterSelect;
            _commentTree.AfterExpand += OnCommentTreeAfterExpand;
            _commentTree.AfterCollapse += OnCommentTreeAfterCollapse;
            _commentTree.MouseWheel += OnCommentTreeMouseWheel;
            _commentTree.KeyDown += OnCommentTreeKeyDown;
            _commentTree.NodeMouseClick += OnCommentTreeNodeMouseClick;
            _commentTree.Enter += (_, _) =>
            {
                _commentTree.SuppressControlRole(false);
                LogComments($"Tree Enter focus={_commentTree.ContainsFocus}");
                ApplyPendingSortRefreshOnTreeFocus();
            };
            _commentTree.GotFocus += (_, _) =>
            {
                _commentTree.SuppressControlRole(false);
                LogComments($"Tree GotFocus focus={_commentTree.ContainsFocus}");
                ApplyPendingSortRefreshOnTreeFocus();
            };
            _commentTree.Leave += (_, _) =>
            {
                _commentTree.SuppressControlRole(false);
                LogComments("Tree Leave");
                CancelLevelAnnouncement();
            };
            _commentTree.HandleCreated += (_, _) => LogComments("Tree HandleCreated");
            _commentTree.HandleDestroyed += (_, _) => LogComments("Tree HandleDestroyed");

            BuildTreeContextMenu();
        }

        private void OnCommentTreeBeforeSelect(object? sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node == null)
            {
                return;
            }

            if (_isResettingTree)
            {
                LogComments("BeforeSelect suppressed (resetting)");
                return;
            }

            int? previousLevel = _commentTree.SelectedNode?.Level;
            string preview = BuildNodePreview(node, 28);
            LogComments($"BeforeSelect level={node.Level} prevLevel={(previousLevel.HasValue ? previousLevel.Value.ToString() : "null")} focus={_commentTree.ContainsFocus} text='{preview}'");

            if (_suppressLevelAnnouncement || !_commentTree.ContainsFocus)
            {
                return;
            }
            if (previousLevel.HasValue && previousLevel.Value != node.Level)
            {
                ScheduleLevelAnnouncement(node.Level);
            }
        }

        private void BuildTreeContextMenu()
        {
            _treeMenu = new ContextMenuStrip();

            var copyItem = new ToolStripMenuItem("复制");
            copyItem.Click += (_, _) => CopySelectedNode();

            var replyItem = new ToolStripMenuItem("回复");
            replyItem.Click += (_, _) => ReplyToSelectedNode();

            var deleteItem = new ToolStripMenuItem("删除");
            deleteItem.Click += async (_, _) => await DeleteSelectedCommentAsync();

            var refreshItem = new ToolStripMenuItem("刷新");
            refreshItem.Click += async (_, _) => await RefreshCommentsAsync();

            _treeMenu.Items.AddRange(new ToolStripItem[]
            {
                copyItem,
                replyItem,
                deleteItem,
                new ToolStripSeparator(),
                refreshItem
            });

            _commentTree.ContextMenuStrip = _treeMenu;
        }

        private async void OnDialogLoad(object? sender, EventArgs e)
        {
            await RefreshCommentsAsync();
        }

        private void OnDialogClosed(object? sender, FormClosedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            DisposeLevelAnnouncementTimer();
        }

        private async void OnRefreshClicked(object? sender, EventArgs e)
        {
            await RefreshCommentsAsync();
        }

        private void OnSortChanged(object? sender, EventArgs e)
        {
            var active = ActiveControl;
            string activeName = active?.Name ?? active?.GetType().Name ?? "null";
            LogComments($"SortChanged active={activeName} treeFocused={_commentTree.Focused} treeContainsFocus={_commentTree.ContainsFocus}");
            if (!_sortComboBox.Focused && !_sortComboBox.DroppedDown)
            {
                LogComments("SortChanged ignored (not focused)");
                return;
            }

            _pendingSortRefresh = true;
            LogComments($"SortChanged pending index={_sortComboBox.SelectedIndex}");
        }

        private async void OnSortComboLeave(object? sender, EventArgs e)
        {
            if (!_pendingSortRefresh)
            {
                return;
            }

            _pendingSortRefresh = false;
            if (_commentTree.ContainsFocus)
            {
                LogComments("SortLeave apply pending (tree focused)");
                await RefreshCommentsAsync(restoreAnchors: false, clearSelection: true);
            }
            else
            {
                _pendingSortRefreshOnTreeFocus = true;
                LogComments("SortLeave pending until tree focus");
            }
        }

        private async void ApplyPendingSortRefreshOnTreeFocus()
        {
            if (!_pendingSortRefreshOnTreeFocus)
            {
                return;
            }

            _pendingSortRefreshOnTreeFocus = false;
            LogComments("SortPending apply on tree focus");
            await RefreshCommentsAsync(restoreAnchors: false, clearSelection: true);
        }

        private async void OnSendClicked(object? sender, EventArgs e)
        {
            await SendCommentAsync();
        }

        private void OnDialogKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                _ = RefreshCommentsAsync();
                return;
            }

            if (e.KeyCode == Keys.F8)
            {
                e.Handled = true;
                ToggleSequenceNumbers();
            }
        }

        private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Shift)
            {
                e.SuppressKeyPress = true;
                _ = SendCommentAsync();
            }
        }

        private void OnCommentTreeKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                case Keys.PageUp:
                case Keys.Home:
                    _commentTree.SuppressNavigationA11y("tree_nav", 260);
                    SetTreeNavDirection(TreeNavDirection.Up, $"KeyDown:{e.KeyCode}");
                    _treeInteractionVersion++;
                    break;
                case Keys.Down:
                case Keys.PageDown:
                case Keys.End:
                    int navDelay = e.KeyCode == Keys.End || e.KeyCode == Keys.PageDown ? 360 : 260;
                    _commentTree.SuppressNavigationA11y("tree_nav", navDelay);
                    SetTreeNavDirection(TreeNavDirection.Down, $"KeyDown:{e.KeyCode}");
                    _treeInteractionVersion++;
                    if (e.KeyCode == Keys.End || e.KeyCode == Keys.PageDown)
                    {
                        string selected = DescribeNodeShort(_commentTree.SelectedNode);
                        string topNode = DescribeNodeShort(_commentTree.TopNode);
                        string lastTop = _commentTree.Nodes.Count > 0
                            ? DescribeNodeShort(_commentTree.Nodes[_commentTree.Nodes.Count - 1])
                            : "null";
                        LogComments($"KeyDownScroll key={e.KeyCode} selected={selected} topNode={topNode} lastTop={lastTop} roots={_commentTree.Nodes.Count}");
                        LogTopChildAnomalies($"keydown_{e.KeyCode}");
                        LogExpandedParentSummary($"keydown_{e.KeyCode}");
                    }
                    break;
            }

            if (e.KeyCode == Keys.C && e.Control)
            {
                e.Handled = true;
                CopySelectedNode();
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                if (!TryLoadMoreFromSelectedNode())
                {
                    ReplyToSelectedNode();
                }
            }

            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                _ = DeleteSelectedCommentAsync();
            }
        }

        private void OnCommentTreeMouseWheel(object? sender, MouseEventArgs e)
        {
            if (e.Delta > 0)
            {
                _commentTree.SuppressNavigationA11y("tree_nav", 260);
                SetTreeNavDirection(TreeNavDirection.Up, "MouseWheel");
            }
            else if (e.Delta < 0)
            {
                _commentTree.SuppressNavigationA11y("tree_nav", 260);
                SetTreeNavDirection(TreeNavDirection.Down, "MouseWheel");
            }
            _treeInteractionVersion++;

            MaybeLoadPrevTopPage();
            MaybeLoadNextTopPage();
            TryAutoLoadVisibleFloorMore("mouse_wheel");
        }

        private void OnCommentTreeNodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _commentTree.SelectedNode = e.Node;
                UpdateContextMenuState();
            }
        }

        private void OnCommentTreeAfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (_isResettingTree)
            {
                LogComments("AfterSelect suppressed (resetting)");
                return;
            }

            if (e.Node != null)
            {
                LogNodeTextMismatchIfAny(e.Node, "AfterSelect");
            }

            if (_commentTree.ContainsFocus
                && (e.Action == TreeViewAction.ByKeyboard || e.Action == TreeViewAction.Unknown))
            {
                _commentTree.SuppressNavigationA11y("after_select", 240);
            }

            string preview = e.Node?.Text ?? string.Empty;
            if (preview.Length > 32)
            {
                preview = preview.Substring(0, 32);
            }
            LogNodeSnapshot("Select", e.Node);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                LogComments($"SelectText '{preview}'");
            }
            LogComments($"AfterSelect focus={_commentTree.ContainsFocus}");
            MaybeLoadPrevTopPage();
            _commentTree.ScheduleRestoreControlRole();
            MaybeLoadNextTopPage();
            TryAutoLoadVisibleFloorMore("after_select");
            LogTopChildAnomalies("after_select");
            LogExpandedParentSummary("after_select");
        }

        private void OnCommentTreeAfterExpand(object? sender, TreeViewEventArgs e)
        {
            LogNodeSnapshot("AfterExpand", e.Node);
            LogComments($"AfterExpand focus={_commentTree.ContainsFocus}");
            _commentTree.NotifyAccessibilityReorder("after_expand");
            MaybeLoadPrevTopPage();
            MaybeLoadNextTopPage();
            TryAutoLoadVisibleFloorMore("after_expand");
            LogTopChildAnomalies("after_expand");
            LogExpandedParentSummary("after_expand");
        }

        private void OnCommentTreeAfterCollapse(object? sender, TreeViewEventArgs e)
        {
            LogNodeSnapshot("AfterCollapse", e.Node);
            LogComments($"AfterCollapse focus={_commentTree.ContainsFocus}");
            _commentTree.NotifyAccessibilityReorder("after_collapse");
            TryAutoLoadVisibleFloorMore("after_collapse");
            LogTopChildAnomalies("after_collapse");
            LogExpandedParentSummary("after_collapse");
        }

        private static string BuildNodePreview(TreeNode? node, int maxLength)
        {
            if (node == null || string.IsNullOrEmpty(node.Text))
            {
                return string.Empty;
            }

            if (node.Text.Length <= maxLength)
            {
                return node.Text;
            }

            return node.Text.Substring(0, maxLength);
        }


        private void UpdateContextMenuState()
        {
            if (_treeMenu == null)
            {
                return;
            }

            var selectedTag = _commentTree.SelectedNode?.Tag as CommentNodeTag;
            bool hasComment = selectedTag?.Comment != null && !selectedTag.IsPlaceholder && !selectedTag.IsLoadMoreNode;
            bool canDelete = CanDeleteComment(selectedTag);

            foreach (ToolStripItem item in _treeMenu.Items)
            {
                if (item is ToolStripMenuItem menuItem)
                {
                    switch (menuItem.Text)
                    {
                        case "复制":
                            menuItem.Enabled = _commentTree.SelectedNode != null;
                            break;
                        case "回复":
                            menuItem.Enabled = hasComment;
                            break;
                        case "删除":
                            menuItem.Enabled = canDelete;
                            break;
                        case "刷新":
                            menuItem.Enabled = true;
                            break;
                    }
                }
            }
        }
    }
}
