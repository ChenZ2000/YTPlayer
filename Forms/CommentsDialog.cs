using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class CommentsDialog : Form
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly CommentTarget _target;
        private readonly string? _currentUserId;
        private readonly bool _isLoggedIn;

        private readonly TreeView _commentsTreeView;
        private readonly ComboBox _sortComboBox;
        private readonly Button _refreshButton;
        private readonly TextBox _commentInput;
        private readonly Button _sendButton;
        private readonly Button _closeButton;
        private readonly Label _statusLabel;
        private readonly ContextMenuStrip _nodeContextMenu;
        private readonly ToolStripMenuItem _copyNodeMenuItem;
        private readonly ToolStripMenuItem _replyNodeMenuItem;
        private readonly ToolStripMenuItem _deleteNodeMenuItem;
        private readonly string _targetCommentsLabel;

        private readonly CancellationTokenSource _lifecycleCts = new CancellationTokenSource();
        private CancellationTokenSource? _loadCommentsCts;
        private CommentSortType _sortType = CommentSortType.Hot;
        private const int CommentsPageSize = 100;
        private const int AutoLoadThreshold = 10;
        private int _nextPageNumber = 1;
        private bool _hasMore;
        private bool _isLoading;
        private string? _currentCursor;
        private bool _suppressAutoLoad;

        private static readonly object PlaceholderNodeTag = new object();

        public CommentsDialog(NeteaseApiClient apiClient, CommentTarget target, string? currentUserId, bool isLoggedIn)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _currentUserId = currentUserId;
            _isLoggedIn = isLoggedIn;
            _targetCommentsLabel = string.IsNullOrWhiteSpace(_target.DisplayName)
                ? "评论"
                : $"{_target.DisplayName}的评论";

            Text = $"评论 - {target.DisplayName}";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            Width = 860;
            Height = 640;
            KeyPreview = true;

            _commentsTreeView = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                ShowNodeToolTips = false
            };

            _sortComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120
            };
            _sortComboBox.Items.Add(new SortOption("按热度", CommentSortType.Hot));
            _sortComboBox.Items.Add(new SortOption("按时间", CommentSortType.Time));
            _sortComboBox.SelectedIndex = 0;

            _refreshButton = new Button
            {
                Text = "刷新",
                AutoSize = true
            };

            _commentInput = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Height = 70
            };
            _commentInput.AccessibleName = "编辑评论";
            _commentInput.AccessibleDescription = "按 Shift + Enter 发表";

            _sendButton = new Button
            {
                Text = "发表评论",
                AutoSize = true,
                Enabled = false
            };

            _closeButton = new Button
            {
                Text = "关闭",
                AutoSize = true,
                DialogResult = DialogResult.Cancel
            };

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Text = _targetCommentsLabel
            };

            _nodeContextMenu = new ContextMenuStrip();
            _copyNodeMenuItem = new ToolStripMenuItem("复制 (&C)");
            _replyNodeMenuItem = new ToolStripMenuItem("回复 (&R)");
            _deleteNodeMenuItem = new ToolStripMenuItem("删除 (&D)");
            _nodeContextMenu.Items.AddRange(new ToolStripItem[]
            {
                _copyNodeMenuItem,
                _replyNodeMenuItem,
                _deleteNodeMenuItem
            });
            _commentsTreeView.ContextMenuStrip = _nodeContextMenu;

            var headerLayout = new TableLayoutPanel
            {
                ColumnCount = 3,
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            var targetLabel = new Label
            {
                Text = $"目标：{target.DisplayName}",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(0, 4, 0, 4)
            };

            var headerButtons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            headerButtons.Controls.Add(new Label { Text = "排序：", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 6, 4, 0) });
            headerButtons.Controls.Add(_sortComboBox);
            headerButtons.Controls.Add(_refreshButton);

            headerLayout.Controls.Add(targetLabel, 0, 0);
            headerLayout.Controls.Add(headerButtons, 1, 0);
            headerLayout.Controls.Add(_statusLabel, 2, 0);

            var inputLayout = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            buttonPanel.Controls.Add(_sendButton);
            buttonPanel.Controls.Add(_closeButton);

            inputLayout.Controls.Add(_commentInput, 0, 0);
            inputLayout.Controls.Add(buttonPanel, 1, 0);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.Controls.Add(headerLayout, 0, 0);
            mainLayout.Controls.Add(_commentsTreeView, 0, 1);
            mainLayout.Controls.Add(inputLayout, 0, 2);

            Controls.Add(mainLayout);
            RefreshStatusLabel();

            CancelButton = _closeButton;

            _refreshButton.Click += async (_, __) => await RefreshCommentsAsync(resetPage: true, append: false);
            _sortComboBox.SelectedIndexChanged += async (_, __) => await ChangeSortAsync();
            _sendButton.Click += async (_, __) => await SubmitNewCommentAsync();
            _commentInput.TextChanged += (_, __) => UpdateSendButtonState();
            _commentInput.KeyDown += CommentInput_KeyDown;

            _commentsTreeView.BeforeExpand += CommentsTreeView_BeforeExpand;
            _commentsTreeView.NodeMouseClick += CommentsTreeView_NodeMouseClick;
            _commentsTreeView.NodeMouseDoubleClick += CommentsTreeView_NodeMouseDoubleClick;
            _commentsTreeView.KeyDown += CommentsTreeView_KeyDown;
            _commentsTreeView.AfterSelect += CommentsTreeView_AfterSelect;

            _nodeContextMenu.Opening += NodeContextMenu_Opening;
            _copyNodeMenuItem.Click += (_, __) => CopySelectedComment();
            _replyNodeMenuItem.Click += async (_, __) => await ReplyToSelectedCommentAsync();
            _deleteNodeMenuItem.Click += async (_, __) => await DeleteSelectedCommentAsync();

            Shown += CommentsDialog_Shown;
            FormClosed += CommentsDialog_FormClosed;
            KeyDown += CommentsDialog_KeyDown;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _loadCommentsCts?.Cancel();
                _loadCommentsCts?.Dispose();
                _lifecycleCts.Cancel();
                _lifecycleCts.Dispose();
                _nodeContextMenu.Dispose();
            }

            base.Dispose(disposing);
        }

        private async void CommentsDialog_Shown(object? sender, EventArgs e)
        {
            await RefreshCommentsAsync(resetPage: true, append: false);
        }

        private void CommentsDialog_FormClosed(object? sender, FormClosedEventArgs e)
        {
            _lifecycleCts.Cancel();
        }

        private void CommentsDialog_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void CommentInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Shift && !e.Control && !e.Alt)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                _ = SubmitNewCommentAsync();
            }
        }

        private async Task RefreshCommentsAsync(bool resetPage, bool append, string? focusCommentId = null, bool preserveSelection = false)
        {
            if (_isLoading)
            {
                return;
            }

            if (resetPage)
            {
                _nextPageNumber = 1;
                _currentCursor = null;
                _hasMore = false;
                _suppressAutoLoad = true;
                _commentsTreeView.BeginUpdate();
                _commentsTreeView.Nodes.Clear();
                _commentsTreeView.SelectedNode = null;
                _commentsTreeView.EndUpdate();
                _suppressAutoLoad = false;
            }

            SelectionSnapshot? snapshot = preserveSelection
                ? CaptureSelectionSnapshot(focusCommentId)
                : null;

            TreeNode? pendingSelection = null;
            bool shouldSelectPending = false;
            bool previousAutoLoadState = _suppressAutoLoad;
            _suppressAutoLoad = true;

            try
            {
                _isLoading = true;
                RefreshStatusLabel();
                ToggleCommandButtons(false);

                _loadCommentsCts?.Cancel();
                _loadCommentsCts?.Dispose();
                _loadCommentsCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                var token = _loadCommentsCts.Token;

                int requestPage = Math.Max(1, _nextPageNumber);

                string? cursorParameter = null;
                if (_sortType == CommentSortType.Time && requestPage > 1)
                {
                    cursorParameter = _currentCursor;
                }

                var result = await _apiClient.GetCommentsAsync(
                    _target.ResourceId,
                    _target.Type,
                    requestPage,
                    CommentsPageSize,
                    _sortType,
                    cursorParameter,
                    token).ConfigureAwait(true);

                _hasMore = result.HasMore;
                _currentCursor = result.Cursor;
                _nextPageNumber = _hasMore ? requestPage + 1 : requestPage;

            int startIndex = _commentsTreeView.Nodes.Count;
            var nodesToAdd = await BuildNodesAsync(result.Comments, isReply: false, startIndex: startIndex).ConfigureAwait(true);

            _commentsTreeView.BeginUpdate();
            try
            {
                if (nodesToAdd.Length > 0)
                {
                    await AddNodesIncrementallyAsync(_commentsTreeView.Nodes, nodesToAdd).ConfigureAwait(true);
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

                pendingSelection = TryResolvePendingSelection(focusCommentId, snapshot);
                shouldSelectPending = pendingSelection != null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowError($"加载评论失败：{ex.Message}");
            }
            finally
            {
                _suppressAutoLoad = previousAutoLoadState;
                _isLoading = false;
                ToggleCommandButtons(true);
                RefreshStatusLabel();
            }

            if (shouldSelectPending && pendingSelection != null)
            {
                bool announce = !preserveSelection || !string.IsNullOrWhiteSpace(focusCommentId);
                var targetNode = pendingSelection;
                RunOnUiThreadAsync(() => SelectNodeWithMode(targetNode, announceSelection: announce, ensureFocus: announce));
            }
        }

        private async Task ChangeSortAsync()
        {
            if (_sortComboBox.SelectedItem is SortOption option)
            {
                _sortType = option.SortType;
                _nextPageNumber = 1;
                _currentCursor = null;
                await RefreshCommentsAsync(resetPage: true, append: false);
            }
        }

        private void ToggleCommandButtons(bool enabled)
        {
            _refreshButton.Enabled = enabled;
            _sortComboBox.Enabled = enabled;
            _sendButton.Enabled = enabled && HasPendingCommentText();
        }

        private void RefreshStatusLabel()
        {
            _statusLabel.Text = _isLoading ? "正在加载 ..." : _targetCommentsLabel;
        }

        private void ShowInfo(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            MessageBox.Show(this, message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void UpdateSendButtonState()
        {
            _sendButton.Enabled = !_isLoading && HasPendingCommentText();
        }

        private bool HasPendingCommentText()
        {
            return !string.IsNullOrWhiteSpace(_commentInput.Text);
        }

        private TreeNode CreateCommentNode(CommentInfo info, bool isReply = false, int? rootIndexOverride = null)
        {
            var state = new CommentTreeNodeState(info);
            bool isRoot = !isReply;
            var node = new TreeNode(BuildNodeText(info, isRoot, rootIndexOverride))
            {
                Tag = state
            };

            if (isRoot && info.ReplyCount > 0)
            {
                node.Nodes.Add(CreatePlaceholderNode("展开以查看回复"));
            }

            return node;
        }

        private string BuildNodeText(CommentInfo info, bool isRoot, int? rootIndex = null)
        {
            var displayName = string.IsNullOrWhiteSpace(info.UserName) ? "匿名用户" : info.UserName.Trim();
            var summary = string.IsNullOrWhiteSpace(info.Content)
                ? "(无内容)"
                : NormalizeSingleLine(info.Content);

            var builder = new System.Text.StringBuilder();

            if (isRoot && rootIndex.HasValue)
            {
                builder.Append(rootIndex.Value + 1);
                builder.Append(". ");
            }

            builder.Append(displayName);

            if (!isRoot)
            {
                var replyTarget = info.BeRepliedUserName?.Trim();
                if (!string.IsNullOrWhiteSpace(replyTarget))
                {
                    builder.Append(" 回复 ");
                    builder.Append(replyTarget);
                }
            }

            builder.Append("：");
            builder.Append(summary);
            builder.Append(" (");
            builder.Append(info.Time.ToString("yyyy-MM-dd HH:mm"));
            builder.Append(')');

            if (isRoot && info.ReplyCount > 0)
            {
                builder.Append(" · ");
                builder.Append(info.ReplyCount);
                builder.Append(" 回复");
            }

            return builder.ToString();
        }

        private TreeNode CreatePlaceholderNode(string text)
        {
            return new TreeNode(text)
            {
                Tag = PlaceholderNodeTag,
                ForeColor = SystemColors.GrayText
            };
        }

        private async void CommentsTreeView_BeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            if (e.Node?.Tag is CommentTreeNodeState state)
            {
                bool needsLoad = state.Comment.ReplyCount > 0 && !state.RepliesLoaded;
                if (needsLoad)
                {
                    e.Cancel = true;
                    await LoadRepliesForNodeAsync(e.Node, state, append: state.RepliesLoaded, focusNewReplies: false);
                    e.Node.Expand();
                }
            }
            else if (e.Node?.Tag == PlaceholderNodeTag)
            {
                e.Cancel = true;
            }
        }

        private void CommentsTreeView_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.Node != null)
            {
                _commentsTreeView.SelectedNode = e.Node;
            }
        }
        private async void CommentsTreeView_NodeMouseDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag is LoadMoreNodeTag loadMore && e.Node.Parent != null)
            {
                await LoadRepliesForNodeAsync(e.Node.Parent, loadMore.ParentState, append: true, focusNewReplies: true);
            }
        }

        private async void CommentsTreeView_KeyDown(object? sender, KeyEventArgs e)
        {
            var selected = _commentsTreeView.SelectedNode;
            if (selected == null)
            {
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (selected.Tag is LoadMoreNodeTag loadMore && selected.Parent != null)
                {
                    e.Handled = true;
                    await LoadRepliesForNodeAsync(selected.Parent, loadMore.ParentState, append: true, focusNewReplies: true);
                }
                else
                {
                    e.Handled = true;
                    await ReplyToSelectedCommentAsync();
                }
            }
            else if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                await DeleteSelectedCommentAsync();
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                e.Handled = true;
                CopySelectedComment();
            }
        }

        private void CommentsTreeView_AfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (e.Node == null)
            {
                return;
            }

            e.Node.EnsureVisible();

            if (!_suppressAutoLoad)
            {
                TryTriggerAutoLoad(e.Node);
            }
        }

        private void TryTriggerAutoLoad(TreeNode node)
        {
            if (node.Tag is LoadMoreNodeTag loadMore && node.Parent != null)
            {
                _ = LoadRepliesForNodeAsync(node.Parent, loadMore.ParentState, append: true, focusNewReplies: true);
                return;
            }

            if (node.Level == 0)
            {
                TryTriggerRootAutoLoad(node);
            }
            else
            {
                TryTriggerReplyAutoLoad(node);
            }
        }

        private void TryTriggerRootAutoLoad(TreeNode node)
        {
            if (_isLoading || !_hasMore || _suppressAutoLoad)
            {
                return;
            }

            var root = node;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            int remaining = _commentsTreeView.Nodes.Count - root.Index - 1;
            if (remaining <= AutoLoadThreshold)
            {
                _ = RefreshCommentsAsync(resetPage: false, append: true, preserveSelection: true);
            }
        }

        private void TryTriggerReplyAutoLoad(TreeNode node)
        {
            var parent = node.Parent;
            if (parent?.Tag is not CommentTreeNodeState parentState)
            {
                return;
            }

            if (!parentState.HasMoreReplies || parentState.IsLoading || _suppressAutoLoad)
            {
                return;
            }

            int remaining = parent.Nodes.Count - node.Index - 1;
            if (remaining <= AutoLoadThreshold)
            {
                _ = LoadRepliesForNodeAsync(parent, parentState, append: true, focusNewReplies: false);
            }
        }

        private string BuildCommentDescriptor(TreeNode node, CommentTreeNodeState state, bool limitSummaryLength, bool includeSeconds)
        {
            var info = state.Comment;
            var user = string.IsNullOrWhiteSpace(info.UserName) ? "匿名用户" : info.UserName.Trim();
            var summary = string.IsNullOrWhiteSpace(info.Content)
                ? "（无内容）"
                : NormalizeSingleLine(info.Content);

            var builder = new System.Text.StringBuilder();
            if (node.Level == 0)
            {
                builder.Append(node.Index + 1);
                builder.Append(". ");
            }

            builder.Append(user);

            var replyTarget = info.BeRepliedUserName?.Trim();
            if (node.Level > 0 && !string.IsNullOrWhiteSpace(replyTarget))
            {
                builder.Append(" 回复 ");
                builder.Append(replyTarget);
            }

            builder.Append("：");
            builder.Append(summary);
            builder.Append(" （");
            builder.Append(info.Time.ToString(includeSeconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm"));
            builder.Append("）");

            return builder.ToString();
        }

        private static string NormalizeSingleLine(string text)
        {
            return text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Trim();
        }

        private void SelectNodeWithMode(TreeNode node, bool announceSelection, bool ensureFocus)
        {
            if (node == null)
            {
                return;
            }

            bool previousAutoLoad = _suppressAutoLoad;
            _suppressAutoLoad = true;

            try
            {
                if (_commentsTreeView.SelectedNode != node)
                {
                    _commentsTreeView.SelectedNode = node;
                }
                else if (announceSelection)
                {
                    // 强制触发 ScreenReader 读取当前节点
                    _commentsTreeView.SelectedNode = node;
                }
            }
            finally
            {
                _suppressAutoLoad = previousAutoLoad;
            }

            node.EnsureVisible();
            if (announceSelection || ensureFocus)
            {
                EnsureTreeViewFocus();
            }

            if (announceSelection)
            {
                RaiseNodeAutomationFocus(node);
            }
        }

        private void EnsureTreeViewFocus()
        {
            if (!_commentsTreeView.Focused && !_commentsTreeView.IsDisposed)
            {
                _commentsTreeView.Focus();
            }
        }

        private void RenumberRootNodesFrom(int startIndex)
        {
            if (_commentsTreeView.Nodes.Count == 0)
            {
                return;
            }

            for (int i = Math.Max(0, startIndex); i < _commentsTreeView.Nodes.Count; i++)
            {
                UpdateNodeText(_commentsTreeView.Nodes[i], i);
            }
        }

        private void UpdateChildNodeTexts(TreeNode parent, IEnumerable<TreeNode>? nodes = null)
        {
            if (parent == null)
            {
                return;
            }

            var targets = nodes ?? parent.Nodes.Cast<TreeNode>();
            foreach (var child in targets)
            {
                UpdateNodeText(child);
            }
        }

        private void UpdateNodeText(TreeNode node, int? rootIndexOverride = null)
        {
            if (node?.Tag is not CommentTreeNodeState state)
            {
                return;
            }

            bool isRoot = node.Parent == null;
            int? rootIndex = isRoot ? (rootIndexOverride ?? node.Index) : null;
            node.Text = BuildNodeText(state.Comment, isRoot, rootIndex);
        }

        private TreeNode? TryResolvePendingSelection(string? focusCommentId, SelectionSnapshot? snapshot)
        {
            TreeNode? pending = null;

            if (!string.IsNullOrWhiteSpace(focusCommentId))
            {
                pending = FindNodeById(focusCommentId!);
                if (pending != null)
                {
                    return pending;
                }
            }

            if (snapshot != null && !snapshot.IsEmpty)
            {
                pending = ResolveSelectionTarget(snapshot);
                if (pending != null)
                {
                    return pending;
                }
            }

            if (_commentsTreeView.Nodes.Count > 0)
            {
                return _commentsTreeView.Nodes[0];
            }

            return null;
        }

        private TreeNode? FindNodeById(string commentId)
        {
            if (string.IsNullOrWhiteSpace(commentId))
            {
                return null;
            }

            var queue = new Queue<TreeNode>();
            foreach (TreeNode root in _commentsTreeView.Nodes)
            {
                queue.Enqueue(root);
            }

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node.Tag is CommentTreeNodeState state &&
                    string.Equals(state.Comment.CommentId, commentId, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                foreach (TreeNode child in node.Nodes)
                {
                    queue.Enqueue(child);
                }
            }

            return null;
        }

        private void NodeContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_commentsTreeView.SelectedNode?.Tag is CommentTreeNodeState state)
            {
                _deleteNodeMenuItem.Visible = IsCurrentUser(state.Comment.UserId);
            }
            else
            {
                e.Cancel = true;
            }
        }



        private async Task LoadRepliesForNodeAsync(TreeNode node, CommentTreeNodeState state, bool append, bool focusNewReplies)
        {
            if (state.IsLoading)
            {
                return;
            }

            state.IsLoading = true;

            if (_commentsTreeView.SelectedNode?.Tag is LoadMoreNodeTag loadMore && loadMore.ParentState == state)
            {
                SelectNodeWithMode(node, announceSelection: false, ensureFocus: false);
            }

            TreeNode? selectionAfterLoad = null;
            bool previousAutoLoadState = _suppressAutoLoad;
            _suppressAutoLoad = true;

            try
            {
                _commentsTreeView.BeginUpdate();
                try
                {
                    if (!append)
                    {
                        node.Nodes.Clear();
                    }
                    else
                    {
                        RemoveLoadMoreNode(node);
                    }

                    int previousChildCount = node.Nodes.Count;
                    var token = _lifecycleCts.Token;
                    var result = await _apiClient.GetCommentFloorAsync(
                        _target.ResourceId,
                        state.Comment.CommentId,
                        _target.Type,
                        state.NextFloorTime,
                        20,
                        token).ConfigureAwait(true);

                    var replyNodes = await BuildNodesAsync(result.Comments, isReply: true).ConfigureAwait(true);

                    if (replyNodes.Length > 0)
                    {
                        await AddNodesIncrementallyAsync(node.Nodes, replyNodes).ConfigureAwait(true);
                        UpdateChildNodeTexts(node, replyNodes);

                        if (append && focusNewReplies)
                        {
                            int firstNewIndex = previousChildCount;
                            if (firstNewIndex >= 0 && firstNewIndex < node.Nodes.Count)
                            {
                                selectionAfterLoad = node.Nodes[firstNewIndex];
                            }
                        }
                    }

                    state.RepliesLoaded = true;
                    state.NextFloorTime = result.NextTime;
                    state.HasMoreReplies = result.HasMore && result.NextTime.HasValue;

                    if (state.HasMoreReplies)
                    {
                        node.Nodes.Add(CreateLoadMoreNode(state));
                    }
                }
                finally
                {
                    _commentsTreeView.EndUpdate();
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                ShowError($"加载回复失败：{ex.Message}");
                if (node.Nodes.Count == 0)
                {
                    node.Nodes.Add(CreatePlaceholderNode("加载失败，重新展开重试"));
                }
            }
            finally
            {
                _suppressAutoLoad = previousAutoLoadState;
                state.IsLoading = false;
            }

            UpdateNodeText(node);

            if (selectionAfterLoad != null)
            {
                SelectNodeWithMode(selectionAfterLoad, announceSelection: focusNewReplies, ensureFocus: focusNewReplies);
            }
            else
            {
                bool announceParent = focusNewReplies && _commentsTreeView.SelectedNode != node;
                SelectNodeWithMode(node, announceSelection: announceParent, ensureFocus: announceParent);
            }
        }

        private TreeNode CreateLoadMoreNode(CommentTreeNodeState state)
        {
            return new TreeNode("点击加载更多回复…")
            {
                Tag = new LoadMoreNodeTag(state),
                ForeColor = SystemColors.HotTrack
            };
        }

        private static void RemoveLoadMoreNode(TreeNode node)
        {
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag is LoadMoreNodeTag)
                {
                    node.Nodes.Remove(child);
                    break;
                }
            }
        }
        private SelectionSnapshot CaptureSelectionSnapshot(string? forceCommentId = null)
        {
            if (!string.IsNullOrWhiteSpace(forceCommentId))
            {
                return SelectionSnapshot.ForComment(forceCommentId);
            }

            if (_commentsTreeView.SelectedNode?.Tag is CommentTreeNodeState state)
            {
                var snapshot = new SelectionSnapshot();
                var stack = new Stack<string>();
                var current = _commentsTreeView.SelectedNode;
                while (current != null)
                {
                    if (current.Tag is CommentTreeNodeState nodeState)
                    {
                        stack.Push(nodeState.Comment.CommentId);
                    }

                    current = current.Parent;
                }

                while (stack.Count > 0)
                {
                    snapshot.Path.Add(stack.Pop());
                }

                snapshot.RootIndex = _commentsTreeView.SelectedNode.Level == 0
                    ? _commentsTreeView.SelectedNode.Index
                    : _commentsTreeView.SelectedNode.Parent?.Index ?? 0;

                return snapshot;
            }

            return SelectionSnapshot.Empty;
        }

        private TreeNode? ResolveSelectionTarget(SelectionSnapshot snapshot)
        {
            if (snapshot.IsEmpty || _commentsTreeView.Nodes.Count == 0)
            {
                return null;
            }

            var node = FindNodeByPath(snapshot.Path);
            if (node != null)
            {
                return node;
            }

            if (snapshot.Path.Count > 1)
            {
                var parentPath = snapshot.Path.Take(snapshot.Path.Count - 1).ToList();
                var parentNode = FindNodeByPath(parentPath);
                if (parentNode != null)
                {
                    return parentNode;
                }
            }

            int fallbackIndex = Math.Min(snapshot.RootIndex ?? 0, _commentsTreeView.Nodes.Count - 1);
            if (fallbackIndex >= 0 && _commentsTreeView.Nodes.Count > fallbackIndex)
            {
                return _commentsTreeView.Nodes[fallbackIndex];
            }

            return null;
        }

        private TreeNode? FindNodeByPath(IReadOnlyList<string> path)
        {
            if (path.Count == 0)
            {
                return null;
            }

            TreeNodeCollection collection = _commentsTreeView.Nodes;
            TreeNode? current = null;

            foreach (var id in path)
            {
                current = null;
                foreach (TreeNode candidate in collection)
                {
                    if (candidate.Tag is CommentTreeNodeState state &&
                        string.Equals(state.Comment.CommentId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        current = candidate;
                        collection = candidate.Nodes;
                        break;
                    }
                }

                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        private TreeNode[] BuildNodes(IEnumerable<CommentInfo> infos, bool isReply, int startIndex = 0)
        {
            if (isReply)
            {
                return infos.Select(info => CreateCommentNode(info, true)).ToArray();
            }

            int index = startIndex;
            return infos.Select(info => CreateCommentNode(info, false, index++)).ToArray();
        }

        private Task<TreeNode[]> BuildNodesAsync(IEnumerable<CommentInfo> infos, bool isReply, int startIndex = 0)
        {
            return Task.Run(() => BuildNodes(infos, isReply, startIndex));
        }

        private async Task AddNodesIncrementallyAsync(TreeNodeCollection target, TreeNode[] nodes, int batchSize = 24)
        {
            if (target == null || nodes == null || nodes.Length == 0)
            {
                return;
            }

            int index = 0;
            while (index < nodes.Length)
            {
                int take = Math.Min(batchSize, nodes.Length - index);
                var batch = new TreeNode[take];
                Array.Copy(nodes, index, batch, 0, take);
                target.AddRange(batch);
                index += take;

                if (index < nodes.Length)
                {
                    await Task.Yield();
                }
            }
        }

        private void RunOnUiThreadAsync(Action action)
        {
            if (action == null || IsDisposed)
            {
                return;
            }

            if (IsHandleCreated)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch (ObjectDisposedException)
                {
                }
            }
            else
            {
                action();
            }
        }

        private void RaiseNodeAutomationFocus(TreeNode node)
        {
            try
            {
                if (node?.TreeView == null || node.TreeView.IsDisposed)
                {
                    return;
                }

                IntPtr hwnd = node.TreeView.Handle;
                IntPtr childHandle = node.Handle;
                if (hwnd == IntPtr.Zero || childHandle == IntPtr.Zero)
                {
                    return;
                }

                int childId = childHandle.ToInt32();
                NativeMethods.NotifyWinEvent(NativeMethods.EVENT_OBJECT_SELECTION, hwnd, NativeMethods.OBJID_CLIENT, childId);
                NativeMethods.NotifyWinEvent(NativeMethods.EVENT_OBJECT_FOCUS, hwnd, NativeMethods.OBJID_CLIENT, childId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommentsDialog] RaiseNodeAutomationFocus 失败: {ex.Message}");
            }
        }

        private async Task SubmitNewCommentAsync()
        {
            var content = _commentInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                ShowInfo("请输入评论内容。");
                return;
            }

            if (!_isLoggedIn)
            {
                ShowLoginRequired();
                return;
            }

            try
            {
                ToggleCommandButtons(false);
                var result = await _apiClient.AddCommentAsync(
                    _target.ResourceId,
                    content!,
                    _target.Type,
                    _lifecycleCts.Token).ConfigureAwait(true);

                if (result.Success)
                {
                    _commentInput.Clear();
                    await RefreshCommentsAsync(resetPage: true, append: false, focusCommentId: result.Comment?.CommentId);
                }
                else
                {
                    ShowError(result.Message ?? "发表评论失败。");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowError($"发表评论失败：{ex.Message}");
            }
            finally
            {
                ToggleCommandButtons(true);
            }
        }

        private async Task ReplyToSelectedCommentAsync()
        {
            if (_commentsTreeView.SelectedNode?.Tag is CommentTreeNodeState state)
            {
                await ReplyToCommentAsync(state);
            }
        }

        private async Task ReplyToCommentAsync(CommentTreeNodeState state)
        {
            if (!_isLoggedIn)
            {
                ShowLoginRequired();
                return;
            }

            using var replyDialog = new ReplyDialog(state.Comment.UserName);
            if (replyDialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var text = replyDialog.ReplyText?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                ToggleCommandButtons(false);
                var result = await _apiClient.ReplyCommentAsync(
                    _target.ResourceId,
                    state.Comment.CommentId,
                    text!,
                    _target.Type,
                    _lifecycleCts.Token).ConfigureAwait(true);

                if (result.Success)
                {
                    await RefreshCommentsAsync(resetPage: true, append: false, focusCommentId: result.Comment?.CommentId);
                }
                else
                {
                    ShowError(result.Message ?? "回复失败。");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowError($"回复失败：{ex.Message}");
            }
            finally
            {
                ToggleCommandButtons(true);
            }
        }

        private async Task DeleteSelectedCommentAsync()
        {
            var node = _commentsTreeView.SelectedNode;
            if (node?.Tag is not CommentTreeNodeState state)
            {
                return;
            }

            if (!IsCurrentUser(state.Comment.UserId))
            {
                ShowInfo("只能删除自己发表的评论。");
                return;
            }

            if (MessageBox.Show(this, "确定删除这条评论吗？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                ToggleCommandButtons(false);
                var result = await _apiClient.DeleteCommentAsync(
                    _target.ResourceId,
                    state.Comment.CommentId,
                    _target.Type,
                    _lifecycleCts.Token).ConfigureAwait(true);

                if (result.Success)
                {
                    int removedIndex = node.Index;
                    node.Remove();
                    if (node.Parent == null)
                    {
                        RenumberRootNodesFrom(removedIndex);
                    }
                }
                else
                {
                    ShowError(result.Message ?? "删除失败。");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowError($"删除失败：{ex.Message}");
            }
            finally
            {
                ToggleCommandButtons(true);
            }
        }

        private void CopySelectedComment()
        {
            var node = _commentsTreeView.SelectedNode;
            if (node?.Tag is CommentTreeNodeState state)
            {
                var descriptor = BuildCopyHeader(state, node.Level == 0);
                var builder = new System.Text.StringBuilder();
                builder.AppendLine(descriptor);
                if (!string.IsNullOrWhiteSpace(state.Comment.Content))
                {
                    builder.AppendLine(state.Comment.Content.Trim());
                }
                Clipboard.SetText(builder.ToString().Trim());
            }
        }

        private bool IsCurrentUser(string? commentUserId)
        {
            return !string.IsNullOrWhiteSpace(commentUserId)
                && !string.IsNullOrWhiteSpace(_currentUserId)
                && string.Equals(commentUserId, _currentUserId, StringComparison.OrdinalIgnoreCase);
        }

        private string BuildCopyHeader(CommentTreeNodeState state, bool isRoot)
        {
            var info = state.Comment;
            var builder = new System.Text.StringBuilder();
            var user = string.IsNullOrWhiteSpace(info.UserName) ? "匿名用户" : info.UserName.Trim();
            builder.Append(user);

            var replyTarget = info.BeRepliedUserName?.Trim();
            if (!isRoot && !string.IsNullOrWhiteSpace(replyTarget))
            {
                builder.Append(" 回复 ");
                builder.Append(replyTarget);
            }

            builder.Append(" （");
            builder.Append(info.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.Append("）：");
            return builder.ToString();
        }

        private void ShowLoginRequired()
        {
            MessageBox.Show(this, "该操作需要登录网易云账号。", "需要登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private sealed class CommentTreeNodeState
        {
            public CommentTreeNodeState(CommentInfo comment)
            {
                Comment = comment;
                RepliesLoaded = false;
                HasMoreReplies = comment.ReplyCount > 0;
            }

            public CommentInfo Comment { get; }
            public bool RepliesLoaded { get; set; }
            public bool HasMoreReplies { get; set; }
            public bool IsLoading { get; set; }
            public long? NextFloorTime { get; set; }
        }

        private sealed class LoadMoreNodeTag
        {
            public LoadMoreNodeTag(CommentTreeNodeState parentState)
            {
                ParentState = parentState;
            }

            public CommentTreeNodeState ParentState { get; }
        }

        private static class NativeMethods
        {
            public const uint EVENT_OBJECT_FOCUS = 0x8005;
            public const uint EVENT_OBJECT_SELECTION = 0x8006;
            public const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

            [DllImport("user32.dll")]
            public static extern void NotifyWinEvent(uint eventId, IntPtr hwnd, int idObject, int idChild);
        }

        private sealed class SelectionSnapshot
        {
            public static SelectionSnapshot Empty { get; } = new SelectionSnapshot();

            public List<string> Path { get; } = new List<string>();

            public int? RootIndex { get; set; }

            public bool IsEmpty => Path.Count == 0;

            public static SelectionSnapshot ForComment(string? commentId)
            {
                var snapshot = new SelectionSnapshot();
                if (!string.IsNullOrWhiteSpace(commentId))
                {
                    snapshot.Path.Add(commentId!);
                }

                return snapshot;
            }
        }

        private sealed class SortOption
        {
            public SortOption(string text, CommentSortType sortType)
            {
                Text = text;
                SortType = sortType;
            }

            public string Text { get; }

            public CommentSortType SortType { get; }

            public override string ToString() => Text;
        }

        private sealed class ReplyDialog : Form
        {
            private readonly TextBox _input;
            private readonly Button _sendButton;
            private readonly Button _cancelButton;

            public ReplyDialog(string? targetUser)
            {
                Text = string.IsNullOrWhiteSpace(targetUser) ? "回复评论" : $"回复 {targetUser}";
                StartPosition = FormStartPosition.CenterParent;
                AutoScaleMode = AutoScaleMode.Font;
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
                Width = 480;
                Height = 260;
                KeyPreview = true;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;

                _input = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    AcceptsReturn = true,
                    ScrollBars = ScrollBars.Vertical
                };
                _input.AccessibleName = "编辑回复";
                _input.AccessibleDescription = "按 Shift + Enter 发表";

                _sendButton = new Button
                {
                    Text = "发送",
                    AutoSize = true,
                    Enabled = false
                };

                _cancelButton = new Button
                {
                    Text = "取消",
                    AutoSize = true,
                    DialogResult = DialogResult.Cancel
                };

                var buttonPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    AutoSize = true
                };
                buttonPanel.Controls.Add(_cancelButton);
                buttonPanel.Controls.Add(_sendButton);

                var mainLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    RowCount = 2
                };
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                mainLayout.Controls.Add(_input, 0, 0);
                mainLayout.Controls.Add(buttonPanel, 0, 1);

                Controls.Add(mainLayout);

                AcceptButton = _sendButton;
                CancelButton = _cancelButton;

                _input.TextChanged += (_, __) => _sendButton.Enabled = !string.IsNullOrWhiteSpace(_input.Text);
                _input.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter && e.Shift && !e.Control && !e.Alt)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        if (_sendButton.Enabled)
                        {
                            _sendButton.PerformClick();
                        }
                    }
                };
                _sendButton.Click += (_, __) =>
                {
                    if (!string.IsNullOrWhiteSpace(_input.Text))
                    {
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                };

                KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Escape)
                    {
                        e.Handled = true;
                        Close();
                    }
                };
            }

            public string ReplyText => _input.Text;
        }
    }
}
