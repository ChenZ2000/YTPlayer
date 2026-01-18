using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog
    {
        private void UpdateSortTypeFromUi()
        {
            _sortType = _sortComboBox.SelectedIndex switch
            {
                1 => CommentSortType.Hot,
                2 => CommentSortType.Time,
                _ => CommentSortType.Recommend
            };
        }

        private void CopySelectedNode()
        {
            var node = _commentTree.SelectedNode;
            if (node == null)
            {
                return;
            }

            try
            {
                Clipboard.SetText(node.Text ?? string.Empty);
            }
            catch
            {
            }
        }

        private async void ReplyToSelectedNode()
        {
            var tag = GetSelectedCommentTag();
            if (tag?.Comment == null)
            {
                return;
            }

            string? content = ShowReplyDialog(tag.Comment.UserName);
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            await SendReplyAsync(tag, content);
        }

        private bool CanDeleteComment(CommentNodeTag? tag)
        {
            if (!_isLoggedIn || tag?.Comment == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_currentUserId))
            {
                return false;
            }

            return string.Equals(tag.Comment.UserId, _currentUserId, StringComparison.OrdinalIgnoreCase);
        }

        private async Task DeleteSelectedCommentAsync()
        {
            var tag = GetSelectedCommentTag();
            if (tag?.Comment == null)
            {
                return;
            }

            if (!CanDeleteComment(tag))
            {
                return;
            }

            if (!ConfirmDeleteComment())
            {
                return;
            }

            var node = _commentTree.SelectedNode;
            if (node == null)
            {
                return;
            }

            var result = await _apiClient.DeleteCommentAsync(
                _target.ResourceId,
                tag.Comment.CommentId,
                _target.Type,
                _cts?.Token ?? default);

            if (!result.Success)
            {
                if (!TryShowRiskDialog(result))
                {
                    MessageBox.Show(this,
                        string.IsNullOrWhiteSpace(result.Message) ? "删除评论失败，请稍后重试。" : result.Message,
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            RemoveDeletedCommentNode(node, tag);
        }

        private bool ConfirmDeleteComment()
        {
            using var dialog = new Form
            {
                Text = "删除评论",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(320, 140),
                Font = Font
            };

            var messageLabel = new Label
            {
                AutoSize = false,
                Text = "确定要删除这条评论吗？",
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(16, 18),
                Size = new Size(288, 40)
            };

            var okButton = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.Yes,
                Size = new Size(88, 30),
                Location = new Point(128, 86)
            };

            var cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.No,
                Size = new Size(88, 30),
                Location = new Point(224, 86)
            };

            dialog.Controls.Add(messageLabel);
            dialog.Controls.Add(okButton);
            dialog.Controls.Add(cancelButton);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;

            return dialog.ShowDialog(this) == DialogResult.Yes;
        }

        private bool TryShowRiskDialog(CommentMutationResult result)
        {
            if (result == null)
            {
                return false;
            }

            if (result.Code != 250 && string.IsNullOrWhiteSpace(result.RiskUrl))
            {
                return false;
            }

            string title = string.IsNullOrWhiteSpace(result.RiskTitle) ? "风险提示" : result.RiskTitle;
            string message = string.IsNullOrWhiteSpace(result.RiskSubtitle)
                ? (string.IsNullOrWhiteSpace(result.Message) ? "抱歉，系统检测异常，暂时无法进行此操作。" : result.Message)
                : result.RiskSubtitle;
            if (result.Code > 0)
            {
                message = $"{message}\r\n错误码: {result.Code}";
            }
            string? url = string.IsNullOrWhiteSpace(result.RiskUrl) ? null : result.RiskUrl;

            using var dialog = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 170),
                Font = Font
            };

            var messageLabel = new Label
            {
                AutoSize = false,
                Text = message,
                Location = new Point(16, 16),
                Size = new Size(328, 80)
            };

            Button? detailButton = null;
            int buttonTop = 118;
            int right = 256;
            if (!string.IsNullOrWhiteSpace(url))
            {
                detailButton = new Button
                {
                    Text = "查看详情",
                    DialogResult = DialogResult.OK,
                    Size = new Size(88, 30),
                    Location = new Point(right - 96, buttonTop)
                };
                detailButton.Click += (_, _) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                    }
                };
            }

            var closeButton = new Button
            {
                Text = "关闭",
                DialogResult = DialogResult.Cancel,
                Size = new Size(88, 30),
                Location = new Point(right, buttonTop)
            };

            dialog.Controls.Add(messageLabel);
            if (detailButton != null)
            {
                dialog.Controls.Add(detailButton);
                dialog.AcceptButton = detailButton;
            }
            dialog.Controls.Add(closeButton);
            dialog.CancelButton = closeButton;

            dialog.ShowDialog(this);
            return true;
        }

        private async Task SendCommentAsync()
        {
            if (!_isLoggedIn)
            {
                MessageBox.Show(this, "请先登录后再发送评论。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string content = _inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            _sendButton.Enabled = false;
            bool inputHadFocus = _inputBox.Focused;
            bool focusNewComment = false;

            try
            {
                CommentMutationResult result;
                if (_replyTarget != null)
                {
                    result = await _apiClient.ReplyCommentAsync(
                        _target.ResourceId,
                        _replyTarget.CommentId,
                        content,
                        _target.Type,
                        cancellationToken: _cts?.Token ?? default);
                }
                else
                {
                    result = await _apiClient.AddCommentAsync(
                        _target.ResourceId,
                        content,
                        _target.Type,
                        _cts?.Token ?? default);
                }

                if (!result.Success)
                {
                    if (!TryShowRiskDialog(result))
                    {
                        MessageBox.Show(this,
                            string.IsNullOrWhiteSpace(result.Message) ? "发送评论失败。" : result.Message,
                            "提示",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    return;
                }

                _inputBox.Clear();
                ClearReplyTarget();

                string? newCommentId = result.Comment?.CommentId;
                if (string.IsNullOrWhiteSpace(newCommentId))
                {
                    newCommentId = result.CommentId;
                }

                if (_sortType != CommentSortType.Time)
                {
                    _sortType = CommentSortType.Time;
                    _sortComboBox.SelectedIndex = MapSortTypeToIndex(CommentSortType.Time);
                    AnnounceSortChange(CommentSortType.Time);
                }

                if (!string.IsNullOrWhiteSpace(newCommentId))
                {
                    _pendingFocusCommentId = newCommentId;
                    _pendingFocusParentId = null;
                    focusNewComment = true;
                }
                else
                {
                    _pendingFocusTopAfterRefresh = true;
                    focusNewComment = true;
                }
                await RefreshCommentsAsync(suppressTreeAccessibility: true, restoreAnchors: false, clearSelection: false);
            }
            finally
            {
                _sendButton.Enabled = _isLoggedIn;
                if (inputHadFocus && !focusNewComment)
                {
                    _inputBox.Focus();
                }
            }
        }

        private void ToggleSequenceNumbers()
        {
            _hideSequenceNumbers = !_hideSequenceNumbers;

            try
            {
                var config = ConfigManager.Instance.Load();
                if (config != null)
                {
                    config.CommentSequenceNumberHidden = _hideSequenceNumbers;
                    ConfigManager.Instance.Save(config);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Comments] 保存序号设置失败: {ex.Message}");
            }

            UpdateAllNodeTexts();
            RaiseAccessibilityAnnouncement(_hideSequenceNumbers ? "已隐藏序号" : "已显示序号");
        }

        private string? ShowReplyDialog(string? userName)
        {
            string name = string.IsNullOrWhiteSpace(userName) ? "用户" : userName!;
            using var dialog = new Form
            {
                Text = "回复评论",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 200),
                Font = Font
            };

            var hintLabel = new Label
            {
                AutoSize = true,
                Text = $"回复 {name}",
                Location = new Point(16, 14)
            };

            var inputBox = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Size = new Size(328, 90),
                Location = new Point(16, 40),
                AccessibleName = $"回复 {name}",
                AccessibleDescription = "输入评论内容，Enter 换行，Shift+Enter 发送。"
            };

            var sendButton = new Button
            {
                Text = "发送",
                DialogResult = DialogResult.OK,
                Size = new Size(88, 30),
                Location = new Point(168, 148)
            };

            var cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Size = new Size(88, 30),
                Location = new Point(256, 148)
            };

            sendButton.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(inputBox.Text))
                {
                    MessageBox.Show(dialog, "请输入回复内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    dialog.DialogResult = DialogResult.None;
                }
            };

            inputBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && e.Shift)
                {
                    e.SuppressKeyPress = true;
                    if (string.IsNullOrWhiteSpace(inputBox.Text))
                    {
                        MessageBox.Show(dialog, "请输入回复内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                }
            };

            dialog.Controls.Add(hintLabel);
            dialog.Controls.Add(inputBox);
            dialog.Controls.Add(sendButton);
            dialog.Controls.Add(cancelButton);
            dialog.AcceptButton = sendButton;
            dialog.CancelButton = cancelButton;

            return dialog.ShowDialog(this) == DialogResult.OK ? inputBox.Text.Trim() : null;
        }

        private async Task SendReplyAsync(CommentNodeTag tag, string content)
        {
            if (!_isLoggedIn)
            {
                MessageBox.Show(this, "请先登录后再发送评论。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string? parentCommentId = tag.IsTopLevel ? tag.Comment?.CommentId : tag.Comment?.ParentCommentId;
            TreeNode? parentNode = null;
            CommentNodeTag? parentTag = null;
            if (!string.IsNullOrWhiteSpace(parentCommentId) && _nodeById.TryGetValue(parentCommentId, out var resolvedParent))
            {
                parentNode = resolvedParent;
                parentTag = resolvedParent.Tag as CommentNodeTag;
            }

            CommentMutationResult result = await _apiClient.ReplyCommentAsync(
                _target.ResourceId,
                tag.Comment!.CommentId,
                content,
                _target.Type,
                _cts?.Token ?? default);

            if (!result.Success && !tag.IsTopLevel && !string.IsNullOrWhiteSpace(parentCommentId))
            {
                string fallbackContent = content;
                if (!fallbackContent.TrimStart().StartsWith("回复", StringComparison.Ordinal))
                {
                    string name = string.IsNullOrWhiteSpace(tag.Comment.UserName) ? "用户" : tag.Comment.UserName;
                    fallbackContent = $"回复 {name}：{content}";
                }

                result = await _apiClient.ReplyCommentAsync(
                    _target.ResourceId,
                    parentCommentId!,
                    fallbackContent,
                    _target.Type,
                    _cts?.Token ?? default);
            }

            if (!result.Success)
            {
                if (!TryShowRiskDialog(result))
                {
                    MessageBox.Show(this,
                        string.IsNullOrWhiteSpace(result.Message) ? "发送评论失败。" : result.Message,
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            string? newCommentId = result.Comment?.CommentId;
            if (string.IsNullOrWhiteSpace(newCommentId))
            {
                newCommentId = result.CommentId;
            }

            if (parentNode == null || parentTag?.Comment == null || string.IsNullOrWhiteSpace(parentCommentId))
            {
                if (!string.IsNullOrWhiteSpace(newCommentId))
                {
                    _pendingFocusCommentId = newCommentId;
                }
                _pendingFocusParentId = null;
                await RefreshCommentsAsync(suppressTreeAccessibility: true, restoreAnchors: false, clearSelection: false);
                if (_commentTree.Nodes.Count > 0)
                {
                    _commentTree.Focus();
                }
                return;
            }

            int currentReplyCount = parentTag.Comment.ReplyCount;
            int updatedReplyCount = Math.Max(currentReplyCount + 1, currentReplyCount);
            if (updatedReplyCount != parentTag.Comment.ReplyCount)
            {
                parentTag.Comment.ReplyCount = updatedReplyCount;
                UpdateCommentNode(parentNode, parentTag.Comment, isTopLevel: true, sequenceNumber: parentTag.SequenceNumber);
            }

            EnsureExpandableNode(parentNode, parentTag.Comment);
            _floorLoadMoreAtTop.Remove(parentCommentId);

            bool page1Loaded = _floorPagesLoaded.TryGetValue(parentCommentId, out var pageSet) && pageSet.Contains(1);
            if (!page1Loaded)
            {
                await LoadFloorPageAsync(parentCommentId, 1, forceReload: true, autoTriggered: false);
            }
            else
            {
                int actualCount = CountFloorReplies(parentNode);
                bool hasMore = parentTag.Comment.ReplyCount > actualCount;
                int nextPage = GetNextFloorPageNumber(parentCommentId);
                EnsureLoadMoreNode(parentNode, parentCommentId, hasMore, nextPage, placeAtTop: false);
            }

            var newComment = result.Comment ?? new CommentInfo
            {
                CommentId = newCommentId ?? string.Empty,
                UserId = _currentUserId ?? string.Empty,
                Content = content,
                Time = DateTime.Now
            };

            if (string.IsNullOrWhiteSpace(newComment.CommentId))
            {
                newComment.CommentId = newCommentId ?? $"virtual:{Guid.NewGuid():N}";
            }

            if (string.IsNullOrWhiteSpace(newComment.ParentCommentId))
            {
                newComment.ParentCommentId = parentCommentId;
            }

            if (!tag.IsTopLevel)
            {
                if (string.IsNullOrWhiteSpace(newComment.BeRepliedUserName))
                {
                    newComment.BeRepliedUserName = tag.Comment.UserName;
                }

                if (string.IsNullOrWhiteSpace(newComment.BeRepliedId))
                {
                    newComment.BeRepliedId = tag.Comment.CommentId;
                }
            }

            var virtualNode = GetOrCreateFloorNode(parentCommentId, newComment.CommentId);
            var virtualTag = virtualNode.Tag as CommentNodeTag ?? new CommentNodeTag();
            virtualTag.Comment = newComment;
            virtualTag.CommentId = newComment.CommentId;
            virtualTag.ParentCommentId = parentCommentId;
            virtualTag.IsPlaceholder = false;
            virtualTag.IsLoadMoreNode = false;
            virtualTag.IsTopLevel = false;
            virtualTag.IsVirtualFloor = true;
            virtualTag.FixedSequenceNumber = parentTag.Comment.ReplyCount;
            virtualNode.Tag = virtualTag;

            string text = BuildNodeText(newComment, virtualTag.FixedSequenceNumber, isTopLevel: false, includeSequence: true);
            ApplyNodeText(virtualNode, text, "VirtualReply");

            InsertVirtualFloorNode(parentNode, virtualNode);
            RecalculateFloorSequences(parentNode);

            _commentTree.SelectedNode = virtualNode;
            _commentTree.NotifyAccessibilityItemNameChange(virtualNode);

            if (!parentNode.IsExpanded)
            {
                parentNode.Expand();
            }

            parentNode.EnsureVisible();
            _commentTree.Focus();
        }

        private void UpdateReplyHint()
        {
            if (_replyTarget == null)
            {
                _replyHintLabel.Text = " ";
                _replyHintLabel.Visible = false;
            }
            else
            {
                string name = string.IsNullOrWhiteSpace(_replyTarget.UserName) ? "用户" : _replyTarget.UserName;
                _replyHintLabel.Text = $"正在回复 @{name}";
                _replyHintLabel.Visible = true;
            }
        }

        private void ClearReplyTarget()
        {
            _replyTarget = null;
            UpdateReplyHint();
        }

        private CommentNodeTag? GetSelectedCommentTag()
        {
            var tag = _commentTree.SelectedNode?.Tag as CommentNodeTag;
            if (tag == null || tag.IsPlaceholder || tag.IsLoadMoreNode)
            {
                return null;
            }

            return tag;
        }

        private void RemoveDeletedCommentNode(TreeNode node, CommentNodeTag tag)
        {
            if (tag.IsTopLevel)
            {
                RemoveNodeSafe(node);
                RecalculateTopLevelSequences();
                return;
            }

            var parentNode = node.Parent;
            RemoveNodeSafe(node);
            if (parentNode == null)
            {
                return;
            }

            var parentTag = parentNode.Tag as CommentNodeTag;
            if (parentTag?.Comment != null)
            {
                int actualCount = CountFloorReplies(parentNode);
                int newCount = Math.Max(actualCount, parentTag.Comment.ReplyCount - 1);
                if (newCount != parentTag.Comment.ReplyCount)
                {
                    parentTag.Comment.ReplyCount = newCount;
                    UpdateCommentNode(parentNode, parentTag.Comment, true, parentTag.SequenceNumber);
                }

                bool hasMore = parentTag.Comment.ReplyCount > actualCount;
                if (!string.IsNullOrWhiteSpace(parentTag.Comment.CommentId))
                {
                    int nextPage = GetNextFloorPageNumber(parentTag.Comment.CommentId);
                    bool loadMoreAtTop = IsFloorLoadMoreAtTop(parentTag.Comment.CommentId);
                    EnsureLoadMoreNode(parentNode, parentTag.Comment.CommentId, hasMore, nextPage, loadMoreAtTop);
                    EnsureExpandableNode(parentNode, parentTag);
                }
            }

            RecalculateFloorSequences(parentNode);
        }
    }
}
