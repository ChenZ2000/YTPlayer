using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog
    {
        private void ResetTreeState()
        {
            _isResettingTree = true;
            try
            {
                ClearTreeNodes();
                _nodeById.Clear();
                _topCommentIds.Clear();
                _floorParentByCommentId.Clear();
                _topPagesLoaded.Clear();
                _floorPagesLoaded.Clear();
                _loadingTopPages.Clear();
                _loadingFloors.Clear();
                _pendingFloorByParent.Clear();
                _floorAutoRetryStates.Clear();
                _hasMoreTop = false;
                _nextTopPage = 1;
                _minTopPageLoaded = 1;
                _maxTopPageLoaded = 0;
                _lastTreeNavDirection = TreeNavDirection.None;
                _pendingFloorSelectionParentId = null;
                _pendingFloorSelectionPage = 0;
                _pendingFocusCommentId = null;
                _pendingFocusParentId = null;
                _pendingFocusTopAfterRefresh = false;
                _lastSelectedLevel = null;
                _levelAnnouncedBeforeSelect = false;
                _pendingAutoExpandParents.Clear();
                _floorLoadMoreAtTop.Clear();
            }
            finally
            {
                _isResettingTree = false;
            }
        }

        private void ClearTreeSelectionAfterRefresh()
        {
            if (IsDisposed)
            {
                return;
            }

            int nodeCount = _commentTree.Nodes.Count;

            bool previousResetting = _isResettingTree;
            _isResettingTree = true;
            try
            {
                if (nodeCount > 0)
                {
                    _commentTree.TopNode = _commentTree.Nodes[0];
                }

                if (_commentTree.SelectedNode != null)
                {
                    _commentTree.SelectedNode = null;
                }
            }
            finally
            {
                _isResettingTree = previousResetting;
            }
        }

        private void ClearTreeNodes()
        {
            if (_commentTree.Nodes.Count == 0)
            {
                return;
            }

            _commentTree.BeginUpdate();
            try
            {
                if (_isResettingTree && _commentTree.SelectedNode != null)
                {
                    LogNodeSnapshot("ClearTreeNodes clear selection", _commentTree.SelectedNode);
                    _commentTree.SelectedNode = null;
                }

                for (int i = _commentTree.Nodes.Count - 1; i >= 0; i--)
                {
                    RemoveNodeSafe(_commentTree.Nodes[i]);
                }
            }
            finally
            {
                _commentTree.EndUpdate();
            }
        }

        private static CommentNodeTag? GetNodeTag(TreeNode? node)
        {
            return node?.Tag as CommentNodeTag;
        }

        private static bool IsPlaceholderNode(TreeNode node)
        {
            return node.Tag is CommentNodeTag tag && tag.IsPlaceholder;
        }

        private static bool IsLoadMoreNode(TreeNode node)
        {
            return node.Tag is CommentNodeTag tag && tag.IsLoadMoreNode;
        }

        private static bool IsVirtualFloorNode(TreeNode node)
        {
            return node.Tag is CommentNodeTag tag && tag.IsVirtualFloor;
        }

        private bool IsTopLevelCommentId(string commentId)
        {
            if (string.IsNullOrWhiteSpace(commentId))
            {
                return false;
            }

            foreach (TreeNode node in _commentTree.Nodes)
            {
                if (node.Tag is CommentNodeTag tag
                    && tag.IsTopLevel
                    && !string.IsNullOrWhiteSpace(tag.CommentId)
                    && string.Equals(tag.CommentId, commentId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildFloorKey(string parentId, string commentId)
        {
            parentId = parentId?.Trim() ?? string.Empty;
            commentId = commentId?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(commentId))
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(parentId))
            {
                return $"floor:unknown:{commentId}";
            }

            return $"floor:{parentId}:{commentId}";
        }

        private (string? selectedId, string? topId, bool hadFocus) CaptureAnchors()
        {
            string? selectedId = ResolveAnchorId(_commentTree.SelectedNode);
            string? topId = ResolveAnchorId(_commentTree.TopNode);
            bool hadFocus = _commentTree.Focused;
            return (selectedId, topId, hadFocus);
        }

        private static string? ResolveAnchorId(TreeNode? node)
        {
            if (node == null)
            {
                return null;
            }

            var tag = GetNodeTag(node);
            if (tag == null)
            {
                return null;
            }

            if (tag.IsPlaceholder || tag.IsLoadMoreNode)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(tag.ParentCommentId) && !tag.IsTopLevel)
            {
                if (string.IsNullOrWhiteSpace(tag.CommentId))
                {
                    return null;
                }

                return BuildFloorKey(tag.ParentCommentId, tag.CommentId);
            }

            return tag.CommentId;
        }

        private bool TryGetNodeByAnchor(string anchorId, out TreeNode node)
        {
            if (string.IsNullOrWhiteSpace(anchorId))
            {
                node = null!;
                return false;
            }

            return _nodeById.TryGetValue(anchorId, out node!);
        }

        private void RestoreAnchors(string? selectedId, string? topId, bool hadFocus)
        {
            if (topId != null && TryGetNodeByAnchor(topId, out var topNode))
            {
                _commentTree.TopNode = topNode;
                LogNodeSnapshot("RestoreTop", topNode);
            }

            if (selectedId != null && TryGetNodeByAnchor(selectedId, out var selectedNode))
            {
                if (!ReferenceEquals(_commentTree.SelectedNode, selectedNode))
                {
                    _commentTree.SelectedNode = selectedNode;
                    LogNodeSnapshot("RestoreSelect", selectedNode);
                }

                if (hadFocus && !_commentTree.Focused)
                {
                    _commentTree.Focus();
                }
            }
        }

        private bool TryFocusPendingCommentAfterRefresh()
        {
            if (string.IsNullOrWhiteSpace(_pendingFocusCommentId))
            {
                return false;
            }

            string? commentId = _pendingFocusCommentId;
            string? parentId = _pendingFocusParentId;
            _pendingFocusCommentId = null;
            _pendingFocusParentId = null;

            if (string.IsNullOrWhiteSpace(commentId))
            {
                return false;
            }

            return TrySelectCommentNode(commentId, ensureFocus: true, "RefreshFocus", parentId);
        }

        private bool TryFocusPendingTopAfterRefresh()
        {
            if (!_pendingFocusTopAfterRefresh)
            {
                return false;
            }

            _pendingFocusTopAfterRefresh = false;
            var firstNode = _commentTree.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node =>
                    node.Tag is CommentNodeTag tag &&
                    tag.Comment != null &&
                    !tag.IsPlaceholder &&
                    !tag.IsLoadMoreNode &&
                    tag.IsTopLevel);

            if (firstNode == null)
            {
                return false;
            }

            _commentTree.SelectedNode = firstNode;
            firstNode.EnsureVisible();
            if (!_commentTree.Focused)
            {
                _commentTree.Focus();
            }

            _commentTree.NotifyAccessibilityItemNameChange(firstNode);
            LogNodeSnapshot("SelectComment RefreshTop", firstNode);
            return true;
        }

        private bool TryFocusFirstTopAfterRefresh(bool ensureFocus)
        {
            var selectedTag = _commentTree.SelectedNode?.Tag as CommentNodeTag;
            bool canOverride = _commentTree.SelectedNode == null
                || (selectedTag != null && (selectedTag.IsPlaceholder || selectedTag.IsLoadMoreNode));
            if (!canOverride)
            {
                return false;
            }

            var firstNode = _commentTree.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node =>
                    node.Tag is CommentNodeTag tag &&
                    tag.Comment != null &&
                    !tag.IsPlaceholder &&
                    !tag.IsLoadMoreNode &&
                    tag.IsTopLevel);

            if (firstNode == null)
            {
                return false;
            }

            _commentTree.SelectedNode = firstNode;
            firstNode.EnsureVisible();
            if (ensureFocus && !_commentTree.Focused)
            {
                _commentTree.Focus();
            }

            _commentTree.NotifyAccessibilityItemNameChange(firstNode);
            LogNodeSnapshot("SelectComment RefreshFirst", firstNode);
            return true;
        }

        private bool HasRealTopNodes()
        {
            foreach (TreeNode node in _commentTree.Nodes)
            {
                if (node.Tag is CommentNodeTag tag
                    && tag.Comment != null
                    && !tag.IsPlaceholder
                    && !tag.IsLoadMoreNode
                    && tag.IsTopLevel)
                {
                    return true;
                }
            }

            return false;
        }

        private bool EnsureTreeIntegrity(string reason)
        {
            bool changed = false;
            var rootIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TreeNode root in _commentTree.Nodes)
            {
                if (root.Tag is CommentNodeTag rootTag && !string.IsNullOrWhiteSpace(rootTag.CommentId))
                {
                    rootIds.Add(rootTag.CommentId);
                }
            }

            foreach (TreeNode root in _commentTree.Nodes)
            {
                if (root.Tag is not CommentNodeTag rootTag || string.IsNullOrWhiteSpace(rootTag.CommentId))
                {
                    continue;
                }

                string rootId = rootTag.CommentId;
                for (int i = root.Nodes.Count - 1; i >= 0; i--)
                {
                    var child = root.Nodes[i];
                    var tag = child.Tag as CommentNodeTag;
                    if (tag == null || string.IsNullOrWhiteSpace(tag.CommentId))
                    {
                        continue;
                    }

                    bool duplicateRoot = rootIds.Contains(tag.CommentId);
                    bool isTopLevel = tag.IsTopLevel;
                    bool parentMismatch = !string.IsNullOrWhiteSpace(tag.ParentCommentId)
                        && !string.Equals(tag.ParentCommentId, rootId, StringComparison.OrdinalIgnoreCase);

                    if (!duplicateRoot && !isTopLevel && !parentMismatch)
                    {
                        continue;
                    }

                    if (duplicateRoot)
                    {
                        RemoveNodeSafe(child);
                        changed = true;
                        continue;
                    }

                    if (parentMismatch && !string.IsNullOrWhiteSpace(tag.ParentCommentId)
                        && _nodeById.TryGetValue(tag.ParentCommentId, out var correctParent)
                        && ReferenceEquals(correctParent.TreeView, _commentTree)
                        && correctParent.Parent == null)
                    {
                        child.Remove();
                        AttachChildNode(correctParent, child, -1, $"integrity_move_parent_{reason}");
                        changed = true;
                        continue;
                    }

                    if (isTopLevel)
                    {
                        child.Remove();
                        AttachRootNode(child, $"integrity_move_root_{reason}");
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private void TrackFloorParent(string commentId, string parentId)
        {
            if (string.IsNullOrWhiteSpace(commentId) || string.IsNullOrWhiteSpace(parentId))
            {
                return;
            }

            _floorParentByCommentId[commentId] = parentId;
        }

        private void RemoveFloorParent(string commentId)
        {
            if (string.IsNullOrWhiteSpace(commentId))
            {
                return;
            }

            _floorParentByCommentId.Remove(commentId);
        }

        private int PruneTopLevelCollisions(HashSet<string> topIds, string reason)
        {
            if (topIds == null || topIds.Count == 0)
            {
                return 0;
            }

            int removed = 0;
            foreach (TreeNode root in _commentTree.Nodes)
            {
                var rootTag = root.Tag as CommentNodeTag;
                if (rootTag == null || string.IsNullOrWhiteSpace(rootTag.CommentId))
                {
                    continue;
                }

                for (int i = root.Nodes.Count - 1; i >= 0; i--)
                {
                    var child = root.Nodes[i];
                    var tag = child.Tag as CommentNodeTag;
                    if (tag == null || string.IsNullOrWhiteSpace(tag.CommentId))
                    {
                        continue;
                    }

                    if (!topIds.Contains(tag.CommentId))
                    {
                        continue;
                    }

                    RemoveNodeSafe(child);
                    RemoveFloorParent(tag.CommentId ?? string.Empty);
                    removed++;
                }
            }

            return removed;
        }

        
        

        private bool TrySelectCommentNode(string commentId, bool ensureFocus, string reason, string? parentId = null)
        {
            if (string.IsNullOrWhiteSpace(commentId))
            {
                return false;
            }

            string key = commentId;
            if (commentId.StartsWith("floor:", StringComparison.OrdinalIgnoreCase))
            {
                key = commentId;
            }
            else if (!string.IsNullOrWhiteSpace(parentId))
            {
                key = BuildFloorKey(parentId!, commentId);
                if (!_nodeById.ContainsKey(key))
                {
                    key = commentId;
                }
            }

            if (!_nodeById.TryGetValue(key, out var node))
            {
                return false;
            }

            if (!ReferenceEquals(_commentTree.SelectedNode, node))
            {
                _commentTree.SelectedNode = node;
            }

            node.EnsureVisible();
            if (ensureFocus && !_commentTree.Focused)
            {
                _commentTree.Focus();
            }

            _commentTree.NotifyAccessibilityItemNameChange(node);
            LogNodeSnapshot($"SelectComment {reason}", node);
            return true;
        }

        private void AttachRootNode(TreeNode node, string reason)
        {
            if (node == null)
            {
                return;
            }

            _commentTree.Nodes.Add(node);
            if (_commentTree.IsHandleCreated)
            {
                _commentTree.ResetAccessibilityChildCache($"attach_root_{reason}");
            }
        }

        private void AttachChildNode(TreeNode parentNode, TreeNode node, int index, string reason)
        {
            if (parentNode == null || node == null)
            {
                return;
            }

            if (index < 0)
            {
                parentNode.Nodes.Add(node);
            }
            else
            {
                parentNode.Nodes.Insert(index, node);
            }
            if (_commentTree.IsHandleCreated)
            {
                _commentTree.ResetAccessibilityChildCache($"attach_child_{reason}");
            }
        }

        private void QueueAutoExpandParent(string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
            {
                return;
            }

            _pendingAutoExpandParents.Add(parentId);
        }

        private void TryAutoExpandParent(string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId) || !_pendingAutoExpandParents.Remove(parentId))
            {
                return;
            }

            if (IsDisposed)
            {
                return;
            }

            if (!_nodeById.TryGetValue(parentId, out var parentNode))
            {
                return;
            }

            if (!ReferenceEquals(_commentTree.SelectedNode, parentNode))
            {
                return;
            }

            if (!parentNode.IsExpanded)
            {
                parentNode.Expand();
            }
        }

        private void ApplyTopLevelResult(CommentResult result, int pageNo,
            string? selectedAnchorId = null, string? topAnchorId = null, bool hadFocus = false, bool restoreAnchors = true)
        {
            if (result == null)
            {
                return;
            }

            bool suppressAnnouncement = _suppressLevelAnnouncement;
            _suppressLevelAnnouncement = true;
            try
            {
                var comments = DeduplicateComments(result.Comments);
                if (comments.Count == 0)
                {
                    int maxBeforeEmpty = _maxTopPageLoaded;
                    _topPagesLoaded.Add(pageNo);
                    if (_topPagesLoaded.Count == 1)
                    {
                        _minTopPageLoaded = pageNo;
                        _maxTopPageLoaded = pageNo;
                    }
                    else
                    {
                        if (pageNo < _minTopPageLoaded)
                        {
                            _minTopPageLoaded = pageNo;
                        }
                        if (pageNo > _maxTopPageLoaded)
                        {
                            _maxTopPageLoaded = pageNo;
                        }
                    }

                    if (pageNo >= maxBeforeEmpty)
                    {
                        _hasMoreTop = result.HasMore;
                        _nextTopPage = pageNo + 1;
                    }
                    return;
                }

                RemoveTopLoadingPlaceholder("top_result");

                var anchors = (selectedId: (string?)null, topId: (string?)null, hadFocus: false);
                if (restoreAnchors)
                {
                    anchors = selectedAnchorId == null && topAnchorId == null
                        ? CaptureAnchors()
                        : (selectedId: selectedAnchorId, topId: topAnchorId, hadFocus: hadFocus);
                }

                _commentTree.BeginUpdate();
                try
                {
                    int topCount = 0;
                    int floorCount = 0;
                    var newTopIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pendingFloorReplies = new List<CommentInfo>();
                    foreach (var comment in comments)
                    {
                        if (string.IsNullOrWhiteSpace(comment.CommentId))
                        {
                            continue;
                        }

                        if (IsFloorReply(comment))
                        {
                            floorCount++;
                            pendingFloorReplies.Add(comment);
                            continue;
                        }

                        topCount++;
                        _topCommentIds.Add(comment.CommentId);
                        newTopIds.Add(comment.CommentId);
                        var node = UpsertTopLevelComment(comment);
                        ApplyInitialReplies(node, comment);
                        EnsureExpandableNode(node, comment);
                        ApplyPendingFloorReplies(node, comment.CommentId);
                    }

                    if (pendingFloorReplies.Count > 0)
                    {
                        foreach (var reply in pendingFloorReplies)
                        {
                            QueuePendingFloorReply(reply);
                        }
                    }

                    if (_pendingFloorByParent.Count > 0)
                    {
                    }

                    ApplyPendingFloorReplies();
                    PruneTopLevelCollisions(newTopIds, $"top_page_{pageNo}");
                    if (EnsureTreeIntegrity($"top_page_{pageNo}"))
                    {
                    }
                    RecalculateTopLevelSequences();
                }
                finally
                {
                    _commentTree.EndUpdate();
                    if (_commentTree.IsHandleCreated)
                    {
                        _commentTree.ResetAccessibilityChildCache($"top_page_{pageNo}");
                    }
                    if (restoreAnchors)
                    {
                        RestoreAnchors(anchors.selectedId, anchors.topId, anchors.hadFocus);
                    }
                }

                int maxBeforeUpdate = _maxTopPageLoaded;
                _topPagesLoaded.Add(pageNo);
                if (_topPagesLoaded.Count == 1)
                {
                    _minTopPageLoaded = pageNo;
                    _maxTopPageLoaded = pageNo;
                }
                else
                {
                    if (pageNo < _minTopPageLoaded)
                    {
                        _minTopPageLoaded = pageNo;
                    }
                    if (pageNo > _maxTopPageLoaded)
                    {
                        _maxTopPageLoaded = pageNo;
                    }
                }

                if (pageNo >= maxBeforeUpdate)
                {
                    _hasMoreTop = result.HasMore;
                    _nextTopPage = pageNo + 1;
                }
            }
            finally
            {
                _suppressLevelAnnouncement = suppressAnnouncement;
            }
        }

        private void ApplyFloorResult(CommentFloorResult result, int pageNo)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.ParentCommentId))
            {
                return;
            }

            if (!_nodeById.TryGetValue(result.ParentCommentId, out var parentNode))
            {
                return;
            }

            var parentTag = parentNode.Tag as CommentNodeTag;
            if (parentTag?.CommentId == null || !string.Equals(parentTag.CommentId, result.ParentCommentId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool suppressAnnouncement = _suppressLevelAnnouncement;
            _suppressLevelAnnouncement = true;
            var anchors = CaptureAnchors();
            int interactionVersion = _treeInteractionVersion;
            bool preferNewSelection = string.Equals(_pendingFloorSelectionParentId, result.ParentCommentId, StringComparison.OrdinalIgnoreCase)
                && _pendingFloorSelectionPage == pageNo;
            bool preferFocusComment = !string.IsNullOrWhiteSpace(_pendingFocusCommentId)
                && string.Equals(_pendingFocusParentId, result.ParentCommentId, StringComparison.OrdinalIgnoreCase);
            TreeNode? firstNewNode = null;
            HashSet<string>? existingIds = null;
            if (preferNewSelection)
            {
                existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TreeNode child in parentNode.Nodes)
                {
                    var tag = child.Tag as CommentNodeTag;
                    if (tag?.CommentId != null && tag.Comment != null && !tag.IsPlaceholder && !tag.IsLoadMoreNode)
                    {
                        existingIds.Add(tag.CommentId);
                    }
                }
            }

            bool loadMoreAtTop = IsFloorLoadMoreAtTop(result.ParentCommentId);
            List<TreeNode>? placeholders = null;
            int placeholderIndex = 0;
            bool hasPlaceholders = false;

            _commentTree.BeginUpdate();
            try
            {
                var replies = DeduplicateComments(result.Comments);
                TreeNode? lastInsertedNode = null;
                IEnumerable<CommentInfo> replySource = replies;
                if (loadMoreAtTop)
                {
                    replySource = replies.AsEnumerable().Reverse();
                }

                if (!loadMoreAtTop)
                {
                    placeholders = GetPlaceholderNodes(parentNode, pageNo);
                    hasPlaceholders = placeholders.Count > 0;
                }

                foreach (var reply in replySource)
                {
                    if (string.IsNullOrWhiteSpace(reply.CommentId))
                    {
                        continue;
                    }

                    if (_topCommentIds.Contains(reply.CommentId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(reply.ParentCommentId)
                        && !string.Equals(reply.ParentCommentId, result.ParentCommentId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(reply.ParentCommentId))
                    {
                        reply.ParentCommentId = result.ParentCommentId;
                    }

                    if (_nodeById.TryGetValue(reply.CommentId, out var topCandidate)
                        && topCandidate.Tag is CommentNodeTag topTag
                        && topTag.IsTopLevel)
                    {
                        continue;
                    }
                    if (IsTopLevelCommentId(reply.CommentId))
                    {
                        continue;
                    }

                    TreeNode node;
                    if (!loadMoreAtTop && placeholders != null && placeholderIndex < placeholders.Count)
                    {
                        node = placeholders[placeholderIndex++];
                        var oldTag = node.Tag as CommentNodeTag;
                        RemoveNodeMapping(oldTag);

                        UpdateCommentNode(node, reply, isTopLevel: false, sequenceNumber: oldTag?.SequenceNumber ?? 0);
                        BindNodeToCommentId(node, result.ParentCommentId, reply.CommentId);
                        TrackFloorParent(reply.CommentId, result.ParentCommentId);
                    }
                    else
                    {
                        node = UpsertFloorComment(parentNode, reply);
                        TrackFloorParent(reply.CommentId, result.ParentCommentId);
                    }

                    if (preferNewSelection && existingIds != null && !existingIds.Contains(reply.CommentId))
                    {
                        if (!loadMoreAtTop && firstNewNode == null)
                        {
                            firstNewNode = node;
                        }
                        else if (loadMoreAtTop)
                        {
                            lastInsertedNode = node;
                        }
                    }
                }

                if (!loadMoreAtTop && placeholders != null && placeholderIndex < placeholders.Count)
                {
                    for (int i = placeholders.Count - 1; i >= placeholderIndex; i--)
                    {
                        RemoveNodeSafe(placeholders[i]);
                    }
                }

                if (preferNewSelection && loadMoreAtTop && firstNewNode == null && lastInsertedNode != null)
                {
                    firstNewNode = lastInsertedNode;
                }

                var virtualNode = FindVirtualFloorNode(parentNode);
                if (virtualNode != null)
                {
                    var virtualTag = virtualNode.Tag as CommentNodeTag;
                    bool removeVirtual = false;
                    if (virtualTag != null)
                    {
                        if (!string.IsNullOrWhiteSpace(virtualTag.CommentId))
                        {
                            foreach (var reply in replies)
                            {
                                if (string.Equals(reply.CommentId, virtualTag.CommentId, StringComparison.OrdinalIgnoreCase))
                                {
                                    removeVirtual = true;
                                    break;
                                }
                            }
                        }

                        if (!removeVirtual
                            && virtualTag.FixedSequenceNumber > 0
                            && result.TotalCount > 0
                            && !result.HasMore
                            && result.TotalCount >= virtualTag.FixedSequenceNumber)
                        {
                            removeVirtual = true;
                        }
                    }

                    if (removeVirtual)
                    {
                        RemoveNodeSafe(virtualNode);
                    }
                }

                int expectedTotal = parentTag.Comment?.ReplyCount ?? 0;
                int actualCount = CountFloorReplies(parentNode);
                bool hasMore = result.HasMore;
                int nextPage = pageNo + 1;
                if (loadMoreAtTop)
                {
                    int prevPage = GetPrevFloorPageNumber(result.ParentCommentId, pageNo);
                    hasMore = prevPage >= 1;
                    nextPage = prevPage;
                    if (nextPage <= 0)
                    {
                        _floorLoadMoreAtTop.Remove(result.ParentCommentId);
                        loadMoreAtTop = false;
                        nextPage = pageNo + 1;
                    }
                }

                if (!hasMore && expectedTotal > 0 && actualCount < expectedTotal)
                {
                    hasMore = true;
                    if (nextPage <= 0)
                    {
                        nextPage = pageNo + 1;
                    }
                }

                EnsureLoadMoreNode(parentNode, result.ParentCommentId, hasMore, nextPage, loadMoreAtTop);
                EnsureExpandableNode(parentNode, parentNode.Tag as CommentNodeTag);

                RecalculateFloorSequences(parentNode);
                if (EnsureTreeIntegrity($"floor_{result.ParentCommentId}_{pageNo}"))
                {
                    RecalculateTopLevelSequences();
                }
            }
            finally
            {
                _commentTree.EndUpdate();
                if (_commentTree.IsHandleCreated)
                {
                    _commentTree.ResetAccessibilityChildCache($"floor_{result.ParentCommentId}_{pageNo}");
                }
                bool handledSelection = false;
                if (preferFocusComment)
                {
                    string? focusId = _pendingFocusCommentId;
                    _pendingFocusCommentId = null;
                    _pendingFocusParentId = null;
                    if (!string.IsNullOrWhiteSpace(focusId))
                    {
                        handledSelection = TrySelectCommentNode(focusId, ensureFocus: true, "ReplyFocus", result.ParentCommentId);
                    }
                }

                if (!handledSelection && preferNewSelection && !(hasPlaceholders && !loadMoreAtTop))
                {
                    if (firstNewNode != null)
                    {
                        _commentTree.SelectedNode = firstNewNode;
                        LogNodeSnapshot("FloorSelectNew", firstNewNode);
                    }
                    else
                    {
                        var loadMoreNode = FindLoadMoreNode(parentNode);
                        if (loadMoreNode != null)
                        {
                            _commentTree.SelectedNode = loadMoreNode;
                            LogNodeSnapshot("FloorSelectLoadMore", loadMoreNode);
                        }
                        else if (parentNode.Nodes.Count > 0)
                        {
                            var fallback = parentNode.Nodes[parentNode.Nodes.Count - 1];
                            _commentTree.SelectedNode = fallback;
                            LogNodeSnapshot("FloorSelectFallback", fallback);
                        }
                    }

                    var selected = _commentTree.SelectedNode;
                    if (selected != null)
                    {
                        _commentTree.NotifyAccessibilityItemNameChange(selected);
                    }
                    handledSelection = true;
                }

                if (!handledSelection)
                {
                    bool selectionInParent = IsNodeInParent(_commentTree.SelectedNode, parentNode);
                    bool allowRestore = (selectionInParent || _commentTree.SelectedNode == null)
                        && interactionVersion == _treeInteractionVersion;
                    if (allowRestore)
                    {
                        RestoreAnchors(anchors.selectedId, anchors.topId, anchors.hadFocus);
                    }
                    else
                    {
                    }

                    bool selectedIsPlaceholder = _commentTree.SelectedNode?.Tag is CommentNodeTag selectedTag && selectedTag.IsPlaceholder;
                    if (!selectedIsPlaceholder && allowRestore)
                    {
                        _commentTree.ResetAccessibilityChildCache("comments_patch");
                    }
                }
                _suppressLevelAnnouncement = suppressAnnouncement;
            }

            if (preferNewSelection)
            {
                _pendingFloorSelectionParentId = null;
                _pendingFloorSelectionPage = 0;
            }

            if (!_floorPagesLoaded.TryGetValue(result.ParentCommentId, out var pageSet))
            {
                pageSet = new HashSet<int>();
                _floorPagesLoaded[result.ParentCommentId] = pageSet;
            }

            pageSet.Add(pageNo);
        }

        private static bool IsNodeInParent(TreeNode? node, TreeNode parentNode)
        {
            if (node == null || parentNode == null)
            {
                return false;
            }

            var current = node;
            while (current != null)
            {
                if (ReferenceEquals(current, parentNode))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private TreeNode UpsertTopLevelComment(CommentInfo comment)
        {
            var node = GetOrCreateTopNode(comment.CommentId);
            int sequenceNumber = (node.Tag as CommentNodeTag)?.SequenceNumber ?? 0;
            UpdateCommentNode(node, comment, isTopLevel: true, sequenceNumber);

            if (!ReferenceEquals(node.TreeView, _commentTree))
            {
                node.Remove();
                AttachRootNode(node, "upsert_top_add");
            }
            else if (node.Parent != null)
            {
                node.Remove();
                AttachRootNode(node, "upsert_top_move");
            }

            return node;
        }

        private TreeNode UpsertFloorComment(TreeNode parentNode, CommentInfo reply)
        {
            string parentId = (parentNode.Tag as CommentNodeTag)?.CommentId ?? reply.ParentCommentId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(reply.ParentCommentId)
                && !string.Equals(reply.ParentCommentId, parentId, StringComparison.OrdinalIgnoreCase))
            {
                reply.ParentCommentId = parentId;
            }
            else if (string.IsNullOrWhiteSpace(reply.ParentCommentId))
            {
                reply.ParentCommentId = parentId;
            }

            string floorKey = BuildFloorKey(parentId, reply.CommentId);
            var node = GetOrCreateFloorNode(parentId, reply.CommentId);
            if (node.Parent == null
                && ReferenceEquals(node.TreeView, _commentTree)
                && _commentTree.Nodes.Contains(node))
            {
                var replacement = new TreeNode();
                if (!string.IsNullOrWhiteSpace(floorKey))
                {
                    _nodeById[floorKey] = replacement;
                }
                node = replacement;
            }

            int sequenceNumber = (node.Tag as CommentNodeTag)?.SequenceNumber ?? 0;
            UpdateCommentNode(node, reply, isTopLevel: false, sequenceNumber);

            if (!ReferenceEquals(node.Parent, parentNode))
            {
                node.Remove();
                int insertIndex = parentNode.Nodes.Count;
                var loadMoreNode = FindLoadMoreNode(parentNode);
                if (loadMoreNode != null)
                {
                    insertIndex = parentNode.Nodes.IndexOf(loadMoreNode);
                    if (insertIndex < 0)
                    {
                        insertIndex = parentNode.Nodes.Count;
                    }
                    else if (loadMoreNode.Index == 0)
                    {
                        insertIndex = Math.Min(1, parentNode.Nodes.Count);
                    }
                }

                AttachChildNode(parentNode, node, insertIndex, "upsert_floor");
            }

            return node;
        }

        private void ApplyInitialReplies(TreeNode parentNode, CommentInfo comment)
        {
            if (comment.Replies.Count == 0)
            {
                return;
            }

            if (_floorPagesLoaded.TryGetValue(comment.CommentId, out var existingPageSet) && existingPageSet.Contains(1))
            {
                return;
            }

            foreach (var reply in comment.Replies)
            {
                if (string.IsNullOrWhiteSpace(reply.CommentId))
                {
                    continue;
                }

                if (_topCommentIds.Contains(reply.CommentId))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(reply.ParentCommentId))
                {
                    reply.ParentCommentId = comment.CommentId;
                }

                UpsertFloorComment(parentNode, reply);
                TrackFloorParent(reply.CommentId, comment.CommentId);
            }

            if (!_floorPagesLoaded.TryGetValue(comment.CommentId, out var pageSet))
            {
                pageSet = new HashSet<int>();
                _floorPagesLoaded[comment.CommentId] = pageSet;
            }

            pageSet.Add(1);

            bool hasMore = comment.ReplyCount > comment.Replies.Count;
            EnsureLoadMoreNode(parentNode, comment.CommentId, hasMore, 2, placeAtTop: false);
        }

        private void EnsureExpandableNode(TreeNode parentNode, CommentInfo comment)
        {
            EnsureExpandableNode(parentNode, comment.ReplyCount > 0);
        }

        private void EnsureExpandableNode(TreeNode parentNode, CommentNodeTag? tag)
        {
            bool hasChildren = tag != null && tag.Comment != null && tag.Comment.ReplyCount > 0;
            EnsureExpandableNode(parentNode, hasChildren);
        }

        private void EnsureExpandableNode(TreeNode parentNode, bool hasChildren)
        {
            _commentTree.SetNodeHasChildren(parentNode, hasChildren);
        }

        private void EnsureLoadMoreNode(TreeNode parentNode, string parentId, bool hasMore, int nextPage, bool placeAtTop)
        {
            var loadMoreNode = FindLoadMoreNode(parentNode);
            if (hasMore && nextPage > 0)
            {
                if (loadMoreNode == null)
                {
                    var newNode = CreateLoadMoreNode(parentId, nextPage);
                    if (placeAtTop)
                    {
                        AttachChildNode(parentNode, newNode, 0, "load_more_add_top");
                    }
                    else
                    {
                        AttachChildNode(parentNode, newNode, -1, "load_more_add_tail");
                    }
                }
                else
                {
                    var tag = (CommentNodeTag)loadMoreNode.Tag!;
                    tag.PageNumber = nextPage;
                    tag.IsLoading = false;
                    tag.LoadFailed = false;
                    loadMoreNode.Tag = tag;
                    UpdateLoadMoreNodeText(loadMoreNode, tag);

                    if (placeAtTop && loadMoreNode.Index != 0)
                    {
                        loadMoreNode.Remove();
                        AttachChildNode(parentNode, loadMoreNode, 0, "load_more_move_top");
                    }
                    else if (!placeAtTop && loadMoreNode.Index == 0 && parentNode.Nodes.Count > 1)
                    {
                        loadMoreNode.Remove();
                        AttachChildNode(parentNode, loadMoreNode, -1, "load_more_move_tail");
                    }
                }
            }
            else if (loadMoreNode != null)
            {
                RemoveNodeSafe(loadMoreNode);
            }

            if (!hasMore)
            {
                _floorLoadMoreAtTop.Remove(parentId);
            }
        }

        private static TreeNode? FindLoadMoreNode(TreeNode parentNode)
        {
            return parentNode.Nodes.Cast<TreeNode>().FirstOrDefault(IsLoadMoreNode);
        }

        private TreeNode CreateLoadMoreNode(string parentId, int nextPage)
        {
            var tag = new CommentNodeTag
            {
                ParentCommentId = parentId,
                IsLoadMoreNode = true,
                IsTopLevel = false,
                PageNumber = nextPage,
                IsLoading = false,
                LoadFailed = false,
                AutoLoadTriggered = false
            };
            return new TreeNode(GetLoadMoreText(tag))
            {
                Tag = tag
            };
        }

        private TreeNode CreatePlaceholderNode(string parentId, int pageNo, bool isLoading, int sequenceNumber)
        {
            var tag = new CommentNodeTag
            {
                ParentCommentId = parentId,
                IsPlaceholder = true,
                IsTopLevel = false,
                PageNumber = pageNo,
                SequenceNumber = sequenceNumber,
                IsLoading = isLoading,
                LoadFailed = false
            };

            return new TreeNode(BuildPlaceholderText(tag, sequenceNumber, includeSequence: true))
            {
                Tag = tag
            };
        }

        private static string GetLoadMoreText(CommentNodeTag tag)
        {
            if (tag.LoadFailed)
            {
                return "加载失败，按 Enter 重试";
            }

            if (tag.IsLoading)
            {
                return "正在加载...";
            }

            return "加载更多...";
        }

        private void UpdateLoadMoreNodeText(TreeNode node, CommentNodeTag tag)
        {
            string text = GetLoadMoreText(tag);
            ApplyNodeText(node, text, "LoadMoreText");
        }

        private string BuildPlaceholderText(CommentNodeTag tag, int sequenceNumber, bool includeSequence)
        {
            if (tag.IsTopLevel && tag.IsPlaceholder)
            {
                return tag.LoadFailed ? "加载失败，按 Enter 重试" : "正在加载...";
            }

            string prefix = includeSequence ? $"{sequenceNumber} 楼 " : string.Empty;
            string body = tag.LoadFailed ? "加载失败，按 Enter 重试" : "加载中...";
            return $"{prefix}{body}".Trim();
        }

        private void UpdatePlaceholderNodeText(TreeNode node, CommentNodeTag tag)
        {
            int sequenceNumber = tag.SequenceNumber > 0 ? tag.SequenceNumber : 0;
            string text = BuildPlaceholderText(tag, sequenceNumber, includeSequence: true);
            ApplyNodeText(node, text, "PlaceholderText");
        }

        private List<TreeNode> GetPlaceholderNodes(TreeNode parentNode, int pageNo)
        {
            return parentNode.Nodes.Cast<TreeNode>()
                .Where(node => node.Tag is CommentNodeTag tag && tag.IsPlaceholder && tag.PageNumber == pageNo)
                .ToList();
        }

        private static bool IsTopLoadingPlaceholder(TreeNode node)
        {
            if (node.Tag is not CommentNodeTag tag)
            {
                return false;
            }

            return tag.IsPlaceholder && tag.IsTopLevel && tag.Comment == null;
        }

        private void EnsureTopLoadingPlaceholder()
        {
            if (_commentTree.Nodes.Count > 0)
            {
                return;
            }

            var tag = new CommentNodeTag
            {
                IsPlaceholder = true,
                IsTopLevel = true,
                IsLoading = true,
                LoadFailed = false,
                SequenceNumber = 0,
                PageNumber = 1
            };

            var node = new TreeNode(BuildPlaceholderText(tag, 0, includeSequence: true))
            {
                Tag = tag
            };

            _commentTree.Nodes.Add(node);
            if (_commentTree.IsHandleCreated)
            {
                _commentTree.ResetAccessibilityChildCache("top_placeholder_add");
            }
        }

        private void RemoveTopLoadingPlaceholder(string reason)
        {
            if (_commentTree.Nodes.Count == 0)
            {
                return;
            }

            bool removed = false;
            for (int i = _commentTree.Nodes.Count - 1; i >= 0; i--)
            {
                var node = _commentTree.Nodes[i];
                if (!IsTopLoadingPlaceholder(node))
                {
                    continue;
                }

                RemoveNodeSafe(node);
                removed = true;
            }

            if (removed)
            {
                if (_commentTree.IsHandleCreated)
                {
                    _commentTree.ResetAccessibilityChildCache($"top_placeholder_remove_{reason}");
                }
            }
        }

        private int GetMaxFloorSequence(TreeNode parentNode)
        {
            int max = 0;
            foreach (TreeNode node in parentNode.Nodes)
            {
                var tag = node.Tag as CommentNodeTag;
                if (tag == null || tag.IsLoadMoreNode)
                {
                    continue;
                }

                if (tag.SequenceNumber > max)
                {
                    max = tag.SequenceNumber;
                }
            }

            return max;
        }

        private void EnsurePlaceholderNodes(TreeNode parentNode, string parentId, int pageNo, int count, bool isLoading)
        {
            if (count <= 0)
            {
                return;
            }

            var placeholders = GetPlaceholderNodes(parentNode, pageNo);
            int missing = count - placeholders.Count;
            if (missing <= 0)
            {
                return;
            }

            int insertIndex = parentNode.Nodes.Count;
            var loadMoreNode = FindLoadMoreNode(parentNode);
            if (loadMoreNode != null)
            {
                int index = parentNode.Nodes.IndexOf(loadMoreNode);
                if (index >= 0)
                {
                    insertIndex = index;
                }
            }

            int sequenceNumber = GetMaxFloorSequence(parentNode);
            for (int i = 0; i < missing; i++)
            {
                sequenceNumber++;
                var placeholder = CreatePlaceholderNode(parentId, pageNo, isLoading, sequenceNumber);
                AttachChildNode(parentNode, placeholder, insertIndex + i, "placeholder_add");
            }
        }

        private bool UpdatePlaceholderState(TreeNode parentNode, int pageNo, bool isLoading, bool loadFailed)
        {
            bool changed = false;
            foreach (var placeholder in GetPlaceholderNodes(parentNode, pageNo))
            {
                var tag = placeholder.Tag as CommentNodeTag;
                if (tag == null)
                {
                    continue;
                }

                if (tag.IsLoading == isLoading && tag.LoadFailed == loadFailed)
                {
                    continue;
                }

                tag.IsLoading = isLoading;
                tag.LoadFailed = loadFailed;
                placeholder.Tag = tag;
                UpdatePlaceholderNodeText(placeholder, tag);
                changed = true;
            }

            return changed;
        }

        private void BindNodeToCommentId(TreeNode node, string? parentId, string commentId)
        {
            if (string.IsNullOrWhiteSpace(commentId))
            {
                return;
            }

            string key = string.IsNullOrWhiteSpace(parentId) ? commentId : BuildFloorKey(parentId, commentId);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _nodeById[key] = node;
        }

        private void MarkLoadMoreFailed(string parentId, int pageNo, string reason)
        {
            if (string.IsNullOrWhiteSpace(parentId))
            {
                return;
            }

            if (!_nodeById.TryGetValue(parentId, out var parentNode))
            {
                LogComments($"LoadMore failed parent={parentId} page={pageNo} reason={reason} (parent missing)");
                return;
            }

            var loadMoreNode = FindLoadMoreNode(parentNode);
            if (loadMoreNode == null)
            {
                LogComments($"LoadMore failed parent={parentId} page={pageNo} reason={reason} (node missing)");
                return;
            }

            var tag = loadMoreNode.Tag as CommentNodeTag;
            if (tag == null || !tag.IsLoadMoreNode)
            {
                return;
            }

            if (tag.PageNumber != pageNo)
            {
                LogComments($"LoadMore failed parent={parentId} page={pageNo} reason={reason} (page mismatch current={tag.PageNumber})");
                return;
            }

            tag.IsLoading = false;
            tag.LoadFailed = true;
            loadMoreNode.Tag = tag;
            UpdateLoadMoreNodeText(loadMoreNode, tag);
            UpdatePlaceholderState(parentNode, pageNo, isLoading: false, loadFailed: true);
            RecalculateFloorSequences(parentNode);
            LogComments($"LoadMore failed parent={parentId} page={pageNo} reason={reason}");
        }

        private TreeNode GetOrCreateTopNode(string commentId)
        {
            return GetOrCreateNodeByKey(commentId);
        }

        private TreeNode GetOrCreateFloorNode(string parentId, string commentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
            {
            }

            string key = BuildFloorKey(parentId, commentId);
            if (_nodeById.TryGetValue(key, out var existing))
            {
                var existingTag = existing.Tag as CommentNodeTag;
                bool isRootNode = existing.Parent == null
                    && ReferenceEquals(existing.TreeView, _commentTree)
                    && _commentTree.Nodes.Contains(existing);
                if (existingTag != null && existingTag.IsTopLevel || isRootNode)
                {
                    string topId = existingTag?.CommentId ?? "null";
                    var replacement = new TreeNode();
                    _nodeById[key] = replacement;
                    return replacement;
                }

                return existing;
            }

            return GetOrCreateNodeByKey(key);
        }

        private TreeNode GetOrCreateNodeByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return new TreeNode();
            }

            if (_nodeById.TryGetValue(key, out var node))
            {
                return node;
            }

            node = new TreeNode();
            _nodeById[key] = node;
            return node;
        }

        private void RecalculateTopLevelSequences()
        {
            int sequenceNumber = 0;
            foreach (TreeNode node in _commentTree.Nodes)
            {
                var tag = node.Tag as CommentNodeTag;
                if (tag?.Comment == null || tag.IsPlaceholder || tag.IsLoadMoreNode)
                {
                    continue;
                }
                if (!tag.IsTopLevel)
                {
                    continue;
                }

                sequenceNumber++;
                tag.SequenceNumber = sequenceNumber;
                tag.IsTopLevel = true;
                node.Tag = tag;
                string text = BuildNodeText(tag.Comment, sequenceNumber, isTopLevel: true, includeSequence: true);
                ApplyNodeText(node, text, "RecalcTop");

                RecalculateFloorSequences(node);
            }
        }

        private void RecalculateFloorSequences(TreeNode parentNode)
        {
            int sequenceNumber = 0;
            foreach (TreeNode node in parentNode.Nodes)
            {
                var tag = node.Tag as CommentNodeTag;
                if (tag == null || tag.IsLoadMoreNode)
                {
                    continue;
                }

                if (tag.IsVirtualFloor)
                {
                    int fixedNumber = tag.FixedSequenceNumber > 0 ? tag.FixedSequenceNumber : tag.SequenceNumber;
                    tag.SequenceNumber = fixedNumber;
                    tag.IsTopLevel = false;
                    node.Tag = tag;
                    if (tag.Comment != null)
                    {
                        string virtualText = BuildNodeText(tag.Comment, fixedNumber, isTopLevel: false, includeSequence: true);
                        ApplyNodeText(node, virtualText, "RecalcFloorVirtual");
                    }
                    if (fixedNumber > sequenceNumber)
                    {
                        sequenceNumber = fixedNumber;
                    }
                    continue;
                }

                sequenceNumber++;
                tag.SequenceNumber = sequenceNumber;
                tag.IsTopLevel = false;
                node.Tag = tag;
                if (tag.IsPlaceholder)
                {
                    string placeholderText = BuildPlaceholderText(tag, sequenceNumber, includeSequence: true);
                    ApplyNodeText(node, placeholderText, "RecalcFloorPlaceholder");
                    continue;
                }

                if (tag.Comment == null)
                {
                    continue;
                }

                string text = BuildNodeText(tag.Comment, sequenceNumber, isTopLevel: false, includeSequence: true);
                ApplyNodeText(node, text, "RecalcFloor");
            }
        }

        private void ApplyNodeText(TreeNode node, string text, string reason)
        {
            if (node == null)
            {
                return;
            }

            if (node.Text == text)
            {
                return;
            }

            node.Text = text;
            _commentTree.NotifyAccessibilityItemNameChange(node);
        }

        private void UpdateCommentNode(TreeNode node, CommentInfo comment, bool isTopLevel, int sequenceNumber)
        {
            var tag = node.Tag as CommentNodeTag ?? new CommentNodeTag();
            tag.Comment = comment;
            tag.CommentId = comment.CommentId;
            tag.ParentCommentId = comment.ParentCommentId;
            tag.IsPlaceholder = false;
            tag.IsLoadMoreNode = false;
            tag.IsLoading = false;
            tag.LoadFailed = false;
            tag.IsTopLevel = isTopLevel;
            tag.SequenceNumber = sequenceNumber;
            tag.IsVirtualFloor = false;
            tag.FixedSequenceNumber = 0;
            tag.AutoLoadTriggered = false;
            node.Tag = tag;

            string text = BuildNodeText(comment, sequenceNumber, isTopLevel, includeSequence: true);
            ApplyNodeText(node, text, "UpdateCommentNode");
        }

        private string BuildNodeText(CommentInfo comment, int sequenceNumber, bool isTopLevel, bool includeSequence)
        {
            string prefix;
            if (!includeSequence)
            {
                prefix = string.Empty;
            }
            else
            {
                prefix = isTopLevel ? $"{sequenceNumber}. " : $"{sequenceNumber} 楼 ";
            }
            string userName = string.IsNullOrWhiteSpace(comment.UserName) ? "用户" : comment.UserName;
            string replyTarget = string.IsNullOrWhiteSpace(comment.BeRepliedUserName)
                ? string.Empty
                : $" 回复 {comment.BeRepliedUserName}";
            string content = comment.Content ?? string.Empty;
            string timeText = comment.Time != default
                ? comment.Time.ToString("yyyy-MM-dd HH:mm")
                : string.Empty;
            string replyCountText = isTopLevel && comment.ReplyCount > 0
                ? $"  回复: {comment.ReplyCount}"
                : string.Empty;

            return $"{prefix}{userName}{replyTarget}{content}{replyCountText}  时间: {timeText}".Trim();
        }

        private List<CommentInfo> DeduplicateComments(IReadOnlyList<CommentInfo> comments)
        {
            var result = new List<CommentInfo>();
            if (comments == null || comments.Count == 0)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var comment in comments)
            {
                if (comment == null || string.IsNullOrWhiteSpace(comment.CommentId))
                {
                    continue;
                }

                if (seen.Add(comment.CommentId))
                {
                    result.Add(comment);
                }
            }

            return result;
        }

        private void RemoveNodeSafe(TreeNode node)
        {
            if (!_isResettingTree && ReferenceEquals(_commentTree.SelectedNode, node))
            {
                var fallback = node.NextVisibleNode ?? node.PrevVisibleNode ?? node.Parent;
                if (fallback != null)
                {
                    LogNodeSnapshot("RemoveSelectCurrent", node);
                    LogNodeSnapshot("RemoveSelectFallback", fallback);
                    _commentTree.SelectedNode = fallback;
                }
                else
                {
                    LogNodeSnapshot("RemoveSelectCurrent", node);
                }
            }
            else if (_isResettingTree && ReferenceEquals(_commentTree.SelectedNode, node))
            {
                LogNodeSnapshot("RemoveSelectCurrent (reset)", node);
            }

            var tag = GetNodeTag(node);
            RemoveNodeMapping(tag);

            LogNodeSnapshot("RemoveNode", node);
            node.Remove();
            if (_commentTree.IsHandleCreated)
            {
                _commentTree.ResetAccessibilityChildCache("remove_node");
            }
        }

        private void RemoveNodeMapping(CommentNodeTag? tag)
        {
            if (tag == null || string.IsNullOrWhiteSpace(tag.CommentId))
            {
                return;
            }

            string key = tag.IsTopLevel
                ? tag.CommentId
                : BuildFloorKey(tag.ParentCommentId ?? string.Empty, tag.CommentId);

            if (!string.IsNullOrWhiteSpace(key))
            {
                _nodeById.Remove(key);
            }

            RemoveFloorParent(tag.CommentId);
        }

        private TreeNode? FindVirtualFloorNode(TreeNode parentNode)
        {
            return parentNode.Nodes.Cast<TreeNode>().FirstOrDefault(IsVirtualFloorNode);
        }

        private void InsertVirtualFloorNode(TreeNode parentNode, TreeNode virtualNode)
        {
            if (parentNode == null || virtualNode == null)
            {
                return;
            }

            var existing = FindVirtualFloorNode(parentNode);
            if (existing != null && !ReferenceEquals(existing, virtualNode))
            {
                RemoveNodeSafe(existing);
            }

            if (ReferenceEquals(virtualNode.Parent, parentNode))
            {
                return;
            }

            virtualNode.Remove();
            var loadMoreNode = FindLoadMoreNode(parentNode);
            if (loadMoreNode != null)
            {
                int index = parentNode.Nodes.IndexOf(loadMoreNode);
                if (index < 0)
                {
                    AttachChildNode(parentNode, virtualNode, -1, "virtual_insert_fallback");
                }
                else
                {
                    AttachChildNode(parentNode, virtualNode, index + 1, "virtual_insert_after_loadmore");
                }
            }
            else
            {
                AttachChildNode(parentNode, virtualNode, -1, "virtual_insert_tail");
            }
        }

        private void UpdateAllNodeTexts()
        {
            if (_commentTree.Nodes.Count == 0)
            {
                return;
            }

            _commentTree.BeginUpdate();
            try
            {
                foreach (TreeNode node in _commentTree.Nodes)
                {
                    UpdateNodeTextRecursive(node);
                }
            }
            finally
            {
                _commentTree.EndUpdate();
            }

            _commentTree.ResetAccessibilityChildCache("comments_sequence_toggle");
            var selected = _commentTree.SelectedNode;
            if (selected != null)
            {
                _commentTree.NotifyAccessibilityItemNameChange(selected);
            }
            UpdateSelectedNodeAccessibilityName();
        }

        private void UpdateNodeTextRecursive(TreeNode node)
        {
            if (node == null)
            {
                return;
            }

            string text = GetNodeDisplayText(node);
            if (!string.IsNullOrEmpty(text) && node.Text != text)
            {
                node.Text = text;
            }

            if (node.Nodes.Count == 0)
            {
                return;
            }

            foreach (TreeNode child in node.Nodes)
            {
                UpdateNodeTextRecursive(child);
            }
        }

        private void UpdateSelectedNodeAccessibilityName()
        {
            if (!_commentTree.IsHandleCreated)
            {
                return;
            }

            var node = _commentTree.SelectedNode;
            if (node == null || node.Handle == IntPtr.Zero)
            {
                return;
            }

            string name = GetNodeDisplayText(node);
            AccessibilityPropertyService.TrySetTreeItemName(_commentTree.Handle, node.Handle, name);
            int accId = _commentTree.TryGetAccIdForNode(node);
            if (accId > 0)
            {
                AccessibilityPropertyService.TrySetTreeItemNameByChildId(_commentTree.Handle, accId, name);
            }

            if (_hideSequenceNumbers && node.NextVisibleNode == null)
            {
                string nodeText = node.Text ?? string.Empty;
                string namePreview = name.Length > 60 ? name.Substring(0, 60) : name;
                string textPreview = nodeText.Length > 60 ? nodeText.Substring(0, 60) : nodeText;
                LogComments($"SeqHideEndNode setName accId={accId} handle=0x{node.Handle.ToInt64():X} name='{namePreview}' text='{textPreview}'");
            }
        }

        private string GetNodeDisplayText(TreeNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            if (node.Tag is not CommentNodeTag tag)
            {
                return node.Text ?? string.Empty;
            }

            if (tag.IsLoadMoreNode)
            {
                return GetLoadMoreText(tag);
            }

            if (tag.IsPlaceholder)
            {
                int seq = tag.SequenceNumber > 0 ? tag.SequenceNumber : 0;
                return BuildPlaceholderText(tag, seq, includeSequence: !_hideSequenceNumbers);
            }

            if (tag.Comment != null)
            {
                return BuildNodeText(tag.Comment, tag.SequenceNumber, tag.IsTopLevel, includeSequence: !_hideSequenceNumbers);
            }

            return node.Text ?? string.Empty;
        }


        private static bool IsFloorReply(CommentInfo comment)
        {
            return !string.IsNullOrWhiteSpace(comment.ParentCommentId);
        }

        private void QueuePendingFloorReply(CommentInfo reply)
        {
            if (string.IsNullOrWhiteSpace(reply.ParentCommentId))
            {
                return;
            }

            if (!_pendingFloorByParent.TryGetValue(reply.ParentCommentId, out var list))
            {
                list = new List<CommentInfo>();
                _pendingFloorByParent[reply.ParentCommentId] = list;
            }

            list.Add(reply);
        }

        private void ApplyPendingFloorReplies()
        {
            if (_pendingFloorByParent.Count == 0)
            {
                return;
            }

            var resolvedParents = new List<string>();
            foreach (var pair in _pendingFloorByParent)
            {
                if (!_nodeById.TryGetValue(pair.Key, out var parentNode))
                {
                    continue;
                }

                ApplyPendingFloorReplies(parentNode, pair.Key, pair.Value);
                resolvedParents.Add(pair.Key);
            }

            foreach (var parentId in resolvedParents)
            {
                _pendingFloorByParent.Remove(parentId);
            }
        }

        private void ApplyPendingFloorReplies(TreeNode parentNode, string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
            {
                return;
            }

            if (_pendingFloorByParent.TryGetValue(parentId, out var replies))
            {
                ApplyPendingFloorReplies(parentNode, parentId, replies);
                _pendingFloorByParent.Remove(parentId);
            }
        }

        private void ApplyPendingFloorReplies(TreeNode parentNode, string parentId, List<CommentInfo> replies)
        {
            if (replies == null || replies.Count == 0)
            {
                return;
            }

            foreach (var reply in replies)
            {
                if (string.IsNullOrWhiteSpace(reply.CommentId))
                {
                    continue;
                }

                if (_topCommentIds.Contains(reply.CommentId))
                {
                    continue;
                }

                UpsertFloorComment(parentNode, reply);
                TrackFloorParent(reply.CommentId, parentId);
            }

            MarkFloorPageLoaded(parentId, 1);

            var tag = parentNode.Tag as CommentNodeTag;
            int expectedCount = tag?.Comment?.ReplyCount ?? 0;
            int actualCount = CountFloorReplies(parentNode);
            bool hasMore = expectedCount > actualCount;
            int nextPage = GetNextFloorPageNumber(parentId);
            bool loadMoreAtTop = IsFloorLoadMoreAtTop(parentId);
            EnsureLoadMoreNode(parentNode, parentId, hasMore, nextPage, loadMoreAtTop);
            RecalculateFloorSequences(parentNode);
        }

        private int CountFloorReplies(TreeNode parentNode)
        {
            int count = 0;
            foreach (TreeNode node in parentNode.Nodes)
            {
                var tag = node.Tag as CommentNodeTag;
                if (tag?.Comment == null || tag.IsPlaceholder || tag.IsLoadMoreNode)
                {
                    continue;
                }
                count++;
            }
            return count;
        }

        private void ResetFloorReplies(TreeNode parentNode, string parentId, int keepPage)
        {
            if (parentNode == null)
            {
                return;
            }

            _commentTree.BeginUpdate();
            try
            {
                for (int i = parentNode.Nodes.Count - 1; i >= 0; i--)
                {
                    RemoveNodeSafe(parentNode.Nodes[i]);
                }
            }
            finally
            {
                _commentTree.EndUpdate();
            }

            if (string.IsNullOrWhiteSpace(parentId))
            {
                return;
            }

            if (!_floorPagesLoaded.TryGetValue(parentId, out var pageSet))
            {
                pageSet = new HashSet<int>();
                _floorPagesLoaded[parentId] = pageSet;
            }
            else
            {
                pageSet.Clear();
            }

            if (keepPage > 0)
            {
                pageSet.Add(keepPage);
            }

            if (string.Equals(_pendingFloorSelectionParentId, parentId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingFloorSelectionParentId = null;
                _pendingFloorSelectionPage = 0;
            }
        }

        private void MarkFloorPageLoaded(string parentId, int pageNo)
        {
            if (string.IsNullOrWhiteSpace(parentId) || pageNo <= 0)
            {
                return;
            }

            if (!_floorPagesLoaded.TryGetValue(parentId, out var pageSet))
            {
                pageSet = new HashSet<int>();
                _floorPagesLoaded[parentId] = pageSet;
            }

            pageSet.Add(pageNo);
        }

        private int GetNextFloorPageNumber(string parentId)
        {
            if (_floorPagesLoaded.TryGetValue(parentId, out var pageSet) && pageSet.Count > 0)
            {
                if (IsFloorLoadMoreAtTop(parentId))
                {
                    return pageSet.Min() - 1;
                }

                return pageSet.Max() + 1;
            }

            return IsFloorLoadMoreAtTop(parentId) ? 0 : 2;
        }

        private int GetPrevFloorPageNumber(string parentId, int currentPage)
        {
            int minLoaded = currentPage;
            if (_floorPagesLoaded.TryGetValue(parentId, out var pageSet) && pageSet.Count > 0)
            {
                minLoaded = Math.Min(minLoaded, pageSet.Min());
            }

            return minLoaded - 1;
        }

        private bool IsFloorLoadMoreAtTop(string parentId)
        {
            return !string.IsNullOrWhiteSpace(parentId) && _floorLoadMoreAtTop.Contains(parentId);
        }
    }
}
