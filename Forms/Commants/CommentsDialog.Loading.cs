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
            if (_commentTree.IsHandleCreated)
            {
                _commentTree.ResetAccessibilityChildCache("comments_sequence_sync");
            }
            _isFirstLoad = false;
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

                await LoadTopPageAsync(1, _cts?.Token ?? CancellationToken.None, selectedId, topId, hadFocus, restoreAnchorsForAttempt);

                if (HasRealTopNodes() || !_isFirstLoad)
                {
                    return;
                }

                if (i < order.Length - 1)
                {
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
                    return;
                }

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

        private async Task LoadFloorPageAsync(string parentCommentId, int pageNo, bool forceReload = false, bool autoTriggered = false)
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
                    }
                    ClearFloorAutoRetry(parentCommentId, pageNo);
                    return;
                }

                if (result == null)
                {
                    HandleFloorLoadFailure(parentCommentId, pageNo, "null_result", autoTriggered);
                    return;
                }

                ApplyFloorResult(result, pageNo);
                ClearFloorAutoRetry(parentCommentId, pageNo);
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
                HandleFloorLoadFailure(parentCommentId, pageNo, "exception", autoTriggered);
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

        private static string BuildFloorRetryKey(string parentCommentId, int pageNo)
        {
            return $"{parentCommentId}:{pageNo}";
        }

        private void ClearFloorAutoRetry(string parentCommentId, int pageNo)
        {
            if (string.IsNullOrWhiteSpace(parentCommentId) || pageNo <= 0)
            {
                return;
            }

            _floorAutoRetryStates.Remove(BuildFloorRetryKey(parentCommentId, pageNo));
        }

        private void HandleFloorLoadFailure(string parentCommentId, int pageNo, string reason, bool autoTriggered)
        {
            if (autoTriggered && TryScheduleFloorAutoRetry(parentCommentId, pageNo))
            {
                return;
            }

            MarkLoadMoreFailed(parentCommentId, pageNo, reason);
        }

        private bool TryScheduleFloorAutoRetry(string parentCommentId, int pageNo)
        {
            if (IsDisposed || string.IsNullOrWhiteSpace(parentCommentId) || pageNo <= 0)
            {
                return false;
            }

            if (_floorPagesLoaded.TryGetValue(parentCommentId, out var pages) && pages.Contains(pageNo))
            {
                ClearFloorAutoRetry(parentCommentId, pageNo);
                return false;
            }

            string key = BuildFloorRetryKey(parentCommentId, pageNo);
            AutoRetryState state = _floorAutoRetryStates.TryGetValue(key, out var existing) ? existing : default;
            if (state.Attempts >= FloorAutoRetryMaxAttempts)
            {
                _floorAutoRetryStates.Remove(key);
                return false;
            }

            state.Attempts++;
            _floorAutoRetryStates[key] = state;
            int delay = GetFloorAutoRetryDelayMs(state.Attempts);
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (IsDisposed || (_cts?.IsCancellationRequested ?? false))
                {
                    return;
                }

                BeginInvoke(new Action(() =>
                {
                    if (!IsFloorAutoRetryStillValid(parentCommentId, pageNo, key, state.Attempts))
                    {
                        return;
                    }

                    StartFloorAutoRetry(parentCommentId, pageNo);
                }));
            });

            return true;
        }

        private bool IsFloorAutoRetryStillValid(string parentCommentId, int pageNo, string key, int attempt)
        {
            if (IsDisposed || string.IsNullOrWhiteSpace(parentCommentId) || pageNo <= 0)
            {
                return false;
            }

            if (!_floorAutoRetryStates.TryGetValue(key, out var state) || state.Attempts != attempt)
            {
                return false;
            }

            if (_loadingFloors.Contains(parentCommentId))
            {
                return false;
            }

            if (_floorPagesLoaded.TryGetValue(parentCommentId, out var pages) && pages.Contains(pageNo))
            {
                _floorAutoRetryStates.Remove(key);
                return false;
            }

            return true;
        }

        private void StartFloorAutoRetry(string parentCommentId, int pageNo)
        {
            if (!_nodeById.TryGetValue(parentCommentId, out var parentNode))
            {
                ClearFloorAutoRetry(parentCommentId, pageNo);
                return;
            }

            TreeNode? loadNode = null;
            CommentNodeTag? loadTag = null;
            var loadMoreNode = FindLoadMoreNode(parentNode);
            if (loadMoreNode?.Tag is CommentNodeTag moreTag && moreTag.IsLoadMoreNode && moreTag.PageNumber == pageNo)
            {
                loadNode = loadMoreNode;
                loadTag = moreTag;
            }
            else
            {
                var placeholders = GetPlaceholderNodes(parentNode, pageNo);
                if (placeholders.Count > 0 && placeholders[0].Tag is CommentNodeTag placeholderTag)
                {
                    loadNode = placeholders[0];
                    loadTag = placeholderTag;
                }
            }

            if (loadNode == null || loadTag == null)
            {
                ClearFloorAutoRetry(parentCommentId, pageNo);
                return;
            }

            loadTag.IsLoading = false;
            loadTag.LoadFailed = false;
            loadTag.AutoLoadTriggered = true;
            loadNode.Tag = loadTag;

            BeginLoadMoreNode(loadNode, loadTag, preferSelection: false, isAuto: true, reason: "auto_retry");
        }

        private static int GetFloorAutoRetryDelayMs(int attempt)
        {
            return attempt switch
            {
                1 => 400,
                2 => 800,
                _ => 1500
            };
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

            if (!isAuto)
            {
                ClearFloorAutoRetry(tag.ParentCommentId, tag.PageNumber);
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

            if (preferSelection)
            {
                _pendingFloorSelectionParentId = tag.ParentCommentId;
                _pendingFloorSelectionPage = tag.PageNumber;
            }

            _ = LoadFloorPageAsync(tag.ParentCommentId, tag.PageNumber, autoTriggered: isAuto);
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
                return;
            }

            await LoadFloorPageAsync(tag.CommentId, 1, autoTriggered: false);
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
