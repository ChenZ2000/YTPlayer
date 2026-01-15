using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog
    {
        private async Task RefreshCommentsAsync(bool suppressTreeAccessibility = false, bool restoreAnchors = true, bool clearSelection = false)
        {
            if (IsDisposed)
            {
                return;
            }

            bool suppressHeader = false;
            bool suppressRole = false;
            if (suppressTreeAccessibility && _commentTree.IsHandleCreated)
            {
                _commentTree.SuppressAccessibleText(true, "refresh");
                _commentTree.SuppressControlRole(true);
                suppressHeader = true;
                suppressRole = true;
            }

            try
            {
                var anchors = restoreAnchors
                    ? CaptureAnchors()
                    : (selectedId: (string?)null, topId: (string?)null, hadFocus: false);
                _loadVersion++;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                ResetTreeState();
                EnsureTopLoadingPlaceholder();
                UpdateSortTypeFromUi();
                LogComments($"Refresh start sort={_sortType} target={_target.Type}:{_target.ResourceId}");

                await LoadTopPageWithFallbackAsync(anchors, restoreAnchors);
                if (IsDisposed)
                {
                    return;
                }

                RecalculateTopLevelSequences();
                bool focusedPending = TryFocusPendingCommentAfterRefresh();
                if (!focusedPending)
                {
                    focusedPending = TryFocusPendingTopAfterRefresh();
                }

                bool focusedFirst = false;
                if (!focusedPending)
                {
                    bool ensureFocus = _commentTree.ContainsFocus || anchors.hadFocus || _isFirstLoad;
                    focusedFirst = TryFocusFirstTopAfterRefresh(ensureFocus);
                }

                if (clearSelection && !focusedPending && !focusedFirst)
                {
                    ClearTreeSelectionAfterRefresh();
                }

                RemoveTopLoadingPlaceholder("refresh_done");
                _commentTree.NotifyAccessibilityReorder("comments_sequence_sync");
                _isFirstLoad = false;
            }
            finally
            {
                if (suppressHeader)
                {
                    _commentTree.ScheduleRestoreAccessibleText("refresh");
                }
                if (suppressRole)
                {
                    _commentTree.ScheduleRestoreControlRole();
                }
            }
        }

        private async Task LoadTopPageWithFallbackAsync((string? selectedId, string? topId, bool hadFocus) anchors, bool restoreAnchors)
        {
            var order = GetSortFallbackOrder();
            for (int i = 0; i < order.Length; i++)
            {
                if (IsDisposed)
                {
                    return;
                }

                var sortType = order[i];
                bool sortChanged = _sortType != sortType;
                if (sortChanged)
                {
                    _sortType = sortType;
                    _sortComboBox.SelectedIndex = MapSortTypeToIndex(sortType);
                }

                if (i > 0 && sortChanged)
                {
                    AnnounceSortChange(sortType);
                }

                bool restoreAnchorsForAttempt = restoreAnchors && i == 0;
                string? selectedId = restoreAnchorsForAttempt ? anchors.selectedId : null;
                string? topId = restoreAnchorsForAttempt ? anchors.topId : null;
                bool hadFocus = restoreAnchorsForAttempt && anchors.hadFocus;

                LogComments($"Refresh load attempt={i + 1} sort={sortType} restoreAnchors={restoreAnchorsForAttempt}");
                await LoadTopPageAsync(1, _cts?.Token ?? CancellationToken.None, selectedId, topId, hadFocus, restoreAnchorsForAttempt);

                if (HasRealTopNodes() || !_isFirstLoad)
                {
                    return;
                }

                if (i < order.Length - 1)
                {
                    LogComments("Refresh fallback: no comments, try next sort");
                    ResetTreeState();
                }
            }
        }

        private CommentSortType[] GetSortFallbackOrder()
        {
            if (!_isFirstLoad)
            {
                return new[] { _sortType };
            }

            return _sortType switch
            {
                CommentSortType.Recommend => new[] { CommentSortType.Recommend, CommentSortType.Hot, CommentSortType.Time },
                CommentSortType.Hot => new[] { CommentSortType.Hot, CommentSortType.Time },
                CommentSortType.Time => new[] { CommentSortType.Time },
                _ => new[] { CommentSortType.Recommend, CommentSortType.Hot, CommentSortType.Time }
            };
        }

        private static int MapSortTypeToIndex(CommentSortType sortType)
        {
            return sortType switch
            {
                CommentSortType.Hot => 1,
                CommentSortType.Time => 2,
                _ => 0
            };
        }

        private async Task LoadTopPageAsync(int pageNo, CancellationToken token,
            string? selectedAnchorId = null, string? topAnchorId = null, bool hadFocus = false, bool restoreAnchors = true)
        {
            if (token.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            if (pageNo <= 0)
            {
                pageNo = 1;
            }

            if (_topPagesLoaded.Contains(pageNo) || _loadingTopPages.Contains(pageNo))
            {
                return;
            }

            _loadingTopPages.Add(pageNo);
            long version = _loadVersion;

            try
            {
                LogComments($"Load top page start page={pageNo} sort={_sortType}");
                var result = await RetryAsync(
                    () => _apiClient.GetCommentsNewPageAsync(
                        _target.ResourceId,
                        _target.Type,
                        pageNo,
                        TopPageSize,
                        _sortType,
                        token),
                    token: token);

                if (token.IsCancellationRequested || IsDisposed || version != _loadVersion)
                {
                    return;
                }

                if (result == null)
                {
                    LogComments($"Load top page null page={pageNo}");
                    return;
                }

                LogComments($"Load top page done page={pageNo} total={result.TotalCount} count={result.Comments.Count} hasMore={result.HasMore}");
                ApplyTopLevelResult(result, pageNo, selectedAnchorId, topAnchorId, hadFocus, restoreAnchors);
                MaybeLoadNextTopPage();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Comments] 顶层评论加载失败: {ex.Message}");
            }
            finally
            {
                _loadingTopPages.Remove(pageNo);
            }
        }

        private async Task LoadFloorPageAsync(string parentCommentId, int pageNo, bool forceReload = false)
        {
            if (string.IsNullOrWhiteSpace(parentCommentId) || IsDisposed)
            {
                return;
            }

            if (pageNo <= 0)
            {
                pageNo = 1;
            }

            if (_loadingFloors.Contains(parentCommentId))
            {
                return;
            }

            if (!forceReload && _floorPagesLoaded.TryGetValue(parentCommentId, out var pageSet) && pageSet.Contains(pageNo))
            {
                return;
            }

            _loadingFloors.Add(parentCommentId);
            long version = _loadVersion;
            var token = _cts?.Token ?? CancellationToken.None;

            try
            {
                LogComments($"Load floor page start parent={parentCommentId} page={pageNo}");
                var result = await RetryAsync(
                    () => _apiClient.GetCommentFloorPageAsync(
                        _target.ResourceId,
                        parentCommentId,
                        _target.Type,
                        pageNo,
                        FloorPageSize,
                        token),
                    token: token);

                if (token.IsCancellationRequested || IsDisposed || version != _loadVersion)
                {
                    if (string.Equals(_pendingFloorSelectionParentId, parentCommentId, StringComparison.OrdinalIgnoreCase)
                        && _pendingFloorSelectionPage == pageNo)
                    {
                        _pendingFloorSelectionParentId = null;
                        _pendingFloorSelectionPage = 0;
                        LogComments($"LoadFloor canceled clear pending parent={parentCommentId} page={pageNo}");
                    }
                    return;
                }

                if (result == null)
                {
                    LogComments($"Load floor page null parent={parentCommentId} page={pageNo}");
                    MarkLoadMoreFailed(parentCommentId, pageNo, "null_result");
                    return;
                }

                LogComments($"Load floor page done parent={parentCommentId} page={pageNo} count={result.Comments.Count} hasMore={result.HasMore}");
                ApplyFloorResult(result, pageNo);
                if (pageNo == 1)
                {
                    TryAutoExpandParent(parentCommentId);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Comments] 楼层评论加载失败: {ex.Message}");
                MarkLoadMoreFailed(parentCommentId, pageNo, "exception");
            }
            finally
            {
                _loadingFloors.Remove(parentCommentId);
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() => TryAutoLoadVisibleFloorMore("floor_loaded")));
                }
            }
        }

        private void BeginLoadMoreNode(TreeNode node, CommentNodeTag tag, bool preferSelection, bool isAuto, string reason)
        {
            if (node == null || tag == null || string.IsNullOrWhiteSpace(tag.ParentCommentId))
            {
                return;
            }

            if (tag.IsLoading)
            {
                return;
            }

            if (isAuto && tag.LoadFailed)
            {
                return;
            }

            var parentNode = node.Parent;
            if (parentNode == null)
            {
                return;
            }

            var parentTag = parentNode.Tag as CommentNodeTag;
            int expectedTotal = parentTag?.Comment?.ReplyCount ?? 0;
            int actualCount = CountFloorReplies(parentNode);
            int remaining = expectedTotal > 0 ? Math.Max(0, expectedTotal - actualCount) : FloorPageSize;
            int placeholderCount = Math.Min(FloorPageSize, remaining > 0 ? remaining : FloorPageSize);

            EnsurePlaceholderNodes(parentNode, tag.ParentCommentId, tag.PageNumber, placeholderCount, isLoading: true);
            bool placeholderChanged = UpdatePlaceholderState(parentNode, tag.PageNumber, isLoading: true, loadFailed: false);
            if (placeholderCount > 0 || placeholderChanged)
            {
                RecalculateFloorSequences(parentNode);
            }

            tag.IsLoading = true;
            tag.LoadFailed = false;
            if (isAuto)
            {
                tag.AutoLoadTriggered = true;
            }
            else
            {
                tag.AutoLoadTriggered = false;
            }
            node.Tag = tag;
            if (tag.IsLoadMoreNode)
            {
                UpdateLoadMoreNodeText(node, tag);
            }
            else if (tag.IsPlaceholder)
            {
                UpdatePlaceholderNodeText(node, tag);
                var loadMoreNode = FindLoadMoreNode(parentNode);
                if (loadMoreNode?.Tag is CommentNodeTag loadMoreTag && loadMoreTag.IsLoadMoreNode)
                {
                    loadMoreTag.IsLoading = true;
                    loadMoreTag.LoadFailed = false;
                    loadMoreNode.Tag = loadMoreTag;
                    UpdateLoadMoreNodeText(loadMoreNode, loadMoreTag);
                }
            }

            if (tag.IsLoadMoreNode && ReferenceEquals(_commentTree.SelectedNode, node))
            {
                var placeholders = GetPlaceholderNodes(parentNode, tag.PageNumber);
                if (placeholders.Count > 0)
                {
                    _commentTree.SelectedNode = placeholders[0];
                }
            }

            LogComments($"LoadMore start parent={tag.ParentCommentId} page={tag.PageNumber} auto={isAuto} reason={reason}");

            if (preferSelection)
            {
                _pendingFloorSelectionParentId = tag.ParentCommentId;
                _pendingFloorSelectionPage = tag.PageNumber;
            }

            _ = LoadFloorPageAsync(tag.ParentCommentId, tag.PageNumber);
        }

        private bool TryAutoLoadVisibleFloorMore(string reason)
        {
            if (IsDisposed)
            {
                return false;
            }

            bool hadVisibleLoadMore = false;

            foreach (var node in EnumerateLoadMoreNodes())
            {
                if (node.Tag is not CommentNodeTag tag || !tag.IsLoadMoreNode)
                {
                    continue;
                }

                if (!node.IsVisible)
                {
                    if (tag.AutoLoadTriggered)
                    {
                        tag.AutoLoadTriggered = false;
                        node.Tag = tag;
                    }
                    continue;
                }

                hadVisibleLoadMore = true;
                if (tag.IsLoading || tag.LoadFailed || tag.AutoLoadTriggered)
                {
                    continue;
                }

                BeginLoadMoreNode(node, tag, preferSelection: false, isAuto: true, reason: reason);
                return true;
            }

            if (hadVisibleLoadMore && (_loadingTopPages.Count > 0 || _loadingFloors.Count > 0))
            {
                return true;
            }

            return false;
        }

        private System.Collections.Generic.IEnumerable<TreeNode> EnumerateLoadMoreNodes()
        {
            foreach (TreeNode root in _commentTree.Nodes)
            {
                foreach (TreeNode child in root.Nodes)
                {
                    if (IsLoadMoreNode(child))
                    {
                        yield return child;
                    }
                }
            }
        }

        private void MaybeLoadNextTopPage()
        {
            if (!_hasMoreTop || IsDisposed)
            {
                return;
            }

            if (_lastTreeNavDirection != TreeNavDirection.Down)
            {
                return;
            }

            if (_nextTopPage <= 0 || _commentTree.Nodes.Count == 0)
            {
                return;
            }

            var lastTopNode = _commentTree.Nodes[_commentTree.Nodes.Count - 1];
            if (lastTopNode.IsVisible)
            {
                int nextPage = _nextTopPage;
                string selected = DescribeNodeShort(_commentTree.SelectedNode);
                string topNode = DescribeNodeShort(_commentTree.TopNode);
                string last = DescribeNodeShort(lastTopNode);
                LogComments($"Auto load next top page {nextPage} (last visible) selected={selected} topNode={topNode} lastTop={last}");
                LogTopChildAnomalies("auto_load_next");
                LogExpandedParentSummary("auto_load_next");
                if (_commentTree.ContainsFocus)
                {
                    _commentTree.SuppressNavigationA11y("auto_load", 420);
                }
                _ = LoadTopPageAsync(nextPage, _cts?.Token ?? CancellationToken.None, restoreAnchors: false);
            }
        }

        private void MaybeLoadPrevTopPage()
        {
            if (IsDisposed)
            {
                return;
            }

            if (_lastTreeNavDirection != TreeNavDirection.Up)
            {
                return;
            }

            if (_commentTree.Nodes.Count == 0)
            {
                return;
            }

            if (_minTopPageLoaded <= 1)
            {
                return;
            }

            int prevPage = _minTopPageLoaded - 1;
            if (_loadingTopPages.Contains(prevPage) || _topPagesLoaded.Contains(prevPage))
            {
                return;
            }

            var firstTopNode = _commentTree.Nodes[0];
            if (firstTopNode.IsVisible)
            {
                string selected = DescribeNodeShort(_commentTree.SelectedNode);
                string topNode = DescribeNodeShort(_commentTree.TopNode);
                string first = DescribeNodeShort(firstTopNode);
                LogComments($"Auto load prev top page {prevPage} (first visible) selected={selected} topNode={topNode} firstTop={first}");
                LogTopChildAnomalies("auto_load_prev");
                LogExpandedParentSummary("auto_load_prev");
                if (_commentTree.ContainsFocus)
                {
                    _commentTree.SuppressNavigationA11y("auto_load", 420);
                }
                _ = LoadTopPageAsync(prevPage, _cts?.Token ?? CancellationToken.None, restoreAnchors: false);
            }
        }

        private async void OnCommentTreeBeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node == null)
            {
                return;
            }

            var tag = node.Tag as CommentNodeTag;
            if (tag?.CommentId == null)
            {
                return;
            }

            if (tag.Comment == null || tag.Comment.ReplyCount <= 0)
            {
                return;
            }

            if (IsFloorLoadMoreAtTop(tag.CommentId)
                && _floorPagesLoaded.TryGetValue(tag.CommentId, out var loadedPages)
                && loadedPages.Count > 0)
            {
                return;
            }

            if (_floorPagesLoaded.TryGetValue(tag.CommentId, out var pageSet) && pageSet.Contains(1))
            {
                return;
            }

            e.Cancel = true;
            QueueAutoExpandParent(tag.CommentId);
            if (_loadingFloors.Contains(tag.CommentId))
            {
                LogComments($"Expand parent={tag.CommentId} wait loading");
                return;
            }

            LogComments($"Expand parent={tag.CommentId} load floor page=1 (defer)");
            await LoadFloorPageAsync(tag.CommentId, 1);
        }

        private bool TryLoadMoreFromSelectedNode()
        {
            var tag = _commentTree.SelectedNode?.Tag as CommentNodeTag;
            if (tag == null)
            {
                return false;
            }

            bool canTrigger = tag.IsLoadMoreNode || (tag.IsPlaceholder && tag.LoadFailed);
            if (!canTrigger)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(tag.ParentCommentId))
            {
                return true;
            }

            var node = _commentTree.SelectedNode;
            if (node == null)
            {
                return true;
            }

            BeginLoadMoreNode(node, tag, preferSelection: true, isAuto: false, reason: "manual_enter");
            return true;
        }

        private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int maxAttempts = 3, CancellationToken token = default)
        {
            int attempt = 0;
            Exception? last = null;
            while (attempt < maxAttempts)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return await action();
                }
                catch (Exception ex)
                {
                    last = ex;
                    attempt++;
                    if (attempt >= maxAttempts)
                    {
                        break;
                    }

                    int delay = attempt <= 2 ? 80 : 150;
                    await Task.Delay(delay, token);
                }
            }

            throw last ?? new InvalidOperationException("retry failed");
        }
    }
}
