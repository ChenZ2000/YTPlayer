using System;
using System.Windows.Forms;
using YTPlayer.Utils;

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
                UpdateNarratorTreeAccessibilityMode();
                ApplyPendingSortRefreshOnTreeFocus();
                AnnounceTreeIntroIfNeeded();
            };
            _commentTree.GotFocus += (_, _) =>
            {
                _commentTree.SuppressControlRole(false);
                UpdateNarratorTreeAccessibilityMode();
                ApplyPendingSortRefreshOnTreeFocus();
                AnnounceTreeIntroIfNeeded();
            };
            _commentTree.Leave += (_, _) =>
            {
                _commentTree.SuppressControlRole(false);
                CancelLevelAnnouncement();
            };

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
                return;
            }

            int? previousLevel = _commentTree.SelectedNode?.Level;

            _levelAnnouncedBeforeSelect = false;
            if (_suppressLevelAnnouncement || !_commentTree.ContainsFocus)
            {
                return;
            }
            int? stablePreviousLevel = _lastSelectedLevel ?? previousLevel;
            if (stablePreviousLevel.HasValue && stablePreviousLevel.Value != node.Level)
            {
                CancelLevelAnnouncement();
                AnnounceTreeLevelChange(node.Level);
                _levelAnnouncedBeforeSelect = true;
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

            MenuNavigationBoundaryHelper.Attach(_treeMenu);
            _commentTree.ContextMenuStrip = _treeMenu;
        }

        private async void OnDialogLoad(object? sender, EventArgs e)
        {
            UpdateNarratorTreeAccessibilityMode();
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
            if (!_sortComboBox.Focused && !_sortComboBox.DroppedDown)
            {
                return;
            }

            _pendingSortRefresh = true;
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
                await RefreshCommentsAsync(restoreAnchors: false, clearSelection: true);
            }
            else
            {
                _pendingSortRefreshOnTreeFocus = true;
            }
        }

        private async void ApplyPendingSortRefreshOnTreeFocus()
        {
            if (!_pendingSortRefreshOnTreeFocus)
            {
                return;
            }

            _pendingSortRefreshOnTreeFocus = false;
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
                    SetTreeNavDirection(TreeNavDirection.Up, $"KeyDown:{e.KeyCode}");
                    _treeInteractionVersion++;
                    break;
                case Keys.Down:
                case Keys.PageDown:
                case Keys.End:
                    SetTreeNavDirection(TreeNavDirection.Down, $"KeyDown:{e.KeyCode}");
                    _treeInteractionVersion++;
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
                SetTreeNavDirection(TreeNavDirection.Up, "MouseWheel");
            }
            else if (e.Delta < 0)
            {
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
                return;
            }

            int? previousLevel = _lastSelectedLevel;
            int? currentLevel = e.Node?.Level;
            _lastSelectedLevel = currentLevel;
            if (!_levelAnnouncedBeforeSelect
                && currentLevel.HasValue
                && previousLevel.HasValue
                && previousLevel.Value != currentLevel.Value
                && _commentTree.ContainsFocus)
            {
                ScheduleLevelAnnouncement(currentLevel.Value);
            }
            _levelAnnouncedBeforeSelect = false;

            if (e.Node != null)
            {
                _commentTree.NotifyAccessibilitySelection(e.Node);
            }
            UpdateSelectedNodeAccessibilityName();
            if (e.Node != null)
            {
                AnnounceSelectedNodeForNarrator(e.Node);
            }

            MaybeLoadPrevTopPage();
            MaybeLoadNextTopPage();
            TryAutoLoadVisibleFloorMore("after_select");
        }

        private void OnCommentTreeAfterExpand(object? sender, TreeViewEventArgs e)
        {
            LogNodeSnapshot("AfterExpand", e.Node);
            if (e.Node != null)
            {
                _commentTree.NotifyAccessibilityStateChange(e.Node);
            }
            MaybeLoadPrevTopPage();
            MaybeLoadNextTopPage();
            TryAutoLoadVisibleFloorMore("after_expand");
        }

        private void OnCommentTreeAfterCollapse(object? sender, TreeViewEventArgs e)
        {
            LogNodeSnapshot("AfterCollapse", e.Node);
            if (e.Node != null)
            {
                _commentTree.NotifyAccessibilityStateChange(e.Node);
            }
            TryAutoLoadVisibleFloorMore("after_collapse");
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
