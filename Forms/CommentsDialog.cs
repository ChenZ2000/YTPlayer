using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
using System.Windows.Forms.Automation;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class CommentsDialog : Form
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly CommentTarget _target;
        private readonly string? _currentUserId;
        private readonly bool _isLoggedIn;

        private readonly CommentsTreeView _commentsTreeView;
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
        private string _statusLabelText = string.Empty;

        private readonly CancellationTokenSource _lifecycleCts = new CancellationTokenSource();
        private CancellationTokenSource? _loadCommentsCts;
        private CommentSortType _sortType = CommentSortType.Hot;
        private const int CommentsPageSize = 100;
        private const int AutoLoadThreshold = 10;
        private int _nextPageNumber = 1;
        private bool _hasMore;
        private bool _isLoading;
        private bool _suppressAutoLoad;
        private int _renderVersion;
        private int _activeRenderVersion;
        private readonly System.Windows.Forms.Timer _accessibilityReorderTimer;
        private bool _accessibilityReorderPending;
        private int _rootRenderStart = -1;
        private int _rootRenderEnd = -1;

        private int _rootTotalCount;
        private int _rootPageSize = CommentsPageSize;
        private readonly List<CommentInfo?> _rootCommentSlots = new List<CommentInfo?>();
        private readonly HashSet<int> _rootLoadedPages = new HashSet<int>();
        private readonly HashSet<int> _rootLoadingPages = new HashSet<int>();
        private readonly Dictionary<int, int> _rootRetryAttempts = new Dictionary<int, int>();
        private readonly HashSet<int> _rootRetryScheduled = new HashSet<int>();
        private readonly Dictionary<string, int> _rootIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const string RootPlaceholderText = "加载中 ...";
        private const string RootFailedText = "加载失败，按 Enter 重试";
        private const int RootPrefetchPadding = 12;
        private const int RootPageRetryDelayMs = 250;
        private const int RootPageMaxRetryAttempts = 5;
        private bool _hotFallbackActive;
        private int _hotFallbackStartPage;
        private int _timeFallbackNextPage = 1;
        private bool _timeFallbackHasMore = true;
        private int _hotFallbackTotalCount;
        private readonly SemaphoreSlim _timeFallbackGate = new SemaphoreSlim(1, 1);
        private const int ReplyPageSize = 20;
        private const string ReplyPlaceholderText = "加载中 ...";
        private const string ReplyFailedText = "加载失败，按 Enter 重试";
        private const int ReplyPrefetchPadding = 6;
        private const int ReplyEmptyRetryDelayMs = 200;
        private const int ReplyPageRetryDelayMs = 350;
        private const int ReplyPageMaxRetryAttempts = 4;

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

            _commentsTreeView = new CommentsTreeView()
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                Sorted = false
            };
            _commentsTreeView.TreeViewNodeSorter = null;
            _commentsTreeView.AccessibleName = _targetCommentsLabel;

            _accessibilityReorderTimer = new System.Windows.Forms.Timer
            {
                Interval = 80
            };
            _accessibilityReorderTimer.Tick += AccessibilityReorderTimer_Tick;

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

            _refreshButton.Click += async (_, __) =>
            {
                await RefreshCommentsAsync(resetPage: true, append: false);
            };
            _sortComboBox.SelectedIndexChanged += async (_, __) => await ChangeSortAsync();
            _sendButton.Click += async (_, __) => await SubmitNewCommentAsync();
            _commentInput.TextChanged += (_, __) => UpdateSendButtonState();
            _commentInput.KeyDown += CommentInput_KeyDown;

            _commentsTreeView.BeforeExpand += CommentsTreeView_BeforeExpand;
            _commentsTreeView.NodeMouseClick += CommentsTreeView_NodeMouseClick;
            _commentsTreeView.NodeMouseDoubleClick += CommentsTreeView_NodeMouseDoubleClick;
            _commentsTreeView.KeyDown += CommentsTreeView_KeyDown;        
            _commentsTreeView.AfterSelect += CommentsTreeView_AfterSelect;      
            _commentsTreeView.MouseWheel += CommentsTreeView_MouseWheel;        

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
                _accessibilityReorderTimer.Stop();
                _accessibilityReorderTimer.Tick -= AccessibilityReorderTimer_Tick;
                _accessibilityReorderTimer.Dispose();
                _loadCommentsCts?.Cancel();
                _loadCommentsCts?.Dispose();
                _lifecycleCts.Cancel();
                _lifecycleCts.Dispose();
                _nodeContextMenu.Dispose();
            }

            base.Dispose(disposing);
        }

        private void CommentsDialog_Shown(object? sender, EventArgs e)    
        {
            BeginInvoke(new Action(async () =>
            {
                if (IsDisposed)
                {
                    return;
                }
                await RefreshCommentsAsync(resetPage: true, append: false);
            }));
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
                return;
            }

            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _ = RefreshCommentsAsync(resetPage: true, append: false);
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
                _renderVersion++;
                _nextPageNumber = 1;
                _hasMore = false;
                ResetHotFallbackState();
                ResetRootSkeletonState();
                ClearTreeView();
            }

            int renderVersion = _renderVersion;
            SelectionSnapshot? snapshot = preserveSelection
                ? CaptureSelectionSnapshot(focusCommentId)
                : null;

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

                EnsureCommentsTreeViewHandle();
                EnsureRootPageSkeleton(requestPage, GetRootPageSize());
                ResetRootPageNodesToLoading(requestPage);

                CommentResult result;
                bool hasMore;
                int effectivePageSize;
                int attempts = 0;
                while (true)
                {
                    try
                    {
                        (result, hasMore, effectivePageSize) = await FetchRootCommentsPageAsync(requestPage, token)
                            .ConfigureAwait(true);
                        int pageSizeForRetry = effectivePageSize > 0 ? effectivePageSize : CommentsPageSize;
                        if (ShouldRetryEmptyRootPage(result, requestPage, pageSizeForRetry))
                        {
                            throw new InvalidOperationException("评论页返回空列表，触发重试。");
                        }
                        _rootRetryAttempts.Remove(requestPage);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        attempts++;
                        _rootRetryAttempts[requestPage] = attempts;
                        MarkRootPageFailed(requestPage);
                        DebugLogger.LogException("CommentsDialog", ex,
                            $"根评论页加载失败: page={requestPage}, attempt={attempts}, reason=Refresh");

                        if (attempts > RootPageMaxRetryAttempts)
                        {
                            throw;
                        }

                        int delayMs = RootPageRetryDelayMs * attempts;
                        await Task.Delay(delayMs, token).ConfigureAwait(true);
                        ResetRootPageNodesToLoading(requestPage);
                    }
                }

                if (renderVersion != _renderVersion)
                {
                    return;
                }

                _hasMore = hasMore;
                _nextPageNumber = _hasMore ? requestPage + 1 : requestPage;

                var ordered = result.Comments ?? new List<CommentInfo>();
                if (effectivePageSize <= 0)
                {
                    effectivePageSize = CommentsPageSize;
                }
                int expectedCount = ComputeExpectedPageCount(
                    result.TotalCount,
                    hasMore,
                    requestPage,
                    effectivePageSize,
                    ordered.Count);
                if (resetPage || _rootPageSize <= 0 || _rootPageSize != effectivePageSize)
                {
                    _rootPageSize = effectivePageSize;
                }
                string retryReason = append ? "Append" : "Refresh";
                if (resetPage || !append)
                {
                    RenderRootCommentsOnce(ordered, snapshot, focusCommentId, requestPage, expectedCount, retryReason);
                }
                else
                {
                    AppendRootComments(ordered, requestPage, snapshot, focusCommentId, expectedCount, retryReason);
                }

                if (!_hasMore)
                {
                    int pageSize = GetRootPageSize();
                    int startIndex = Math.Max(0, (requestPage - 1) * pageSize);
                    int newTotal = startIndex + ordered.Count;
                    if (newTotal < _rootTotalCount)
                    {
                        TrimRootSkeletonToCount(newTotal);
                    }
                }
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
        }

        private void ResetHotFallbackState()
        {
            _hotFallbackActive = false;
            _hotFallbackStartPage = 0;
            _timeFallbackNextPage = 1;
            _timeFallbackHasMore = true;
            _hotFallbackTotalCount = 0;
        }

        private bool ShouldTriggerHotFallback(CommentResult result, int pageNumber, int pageSize)
        {
            if (_sortType != CommentSortType.Hot || result == null)
            {
                return false;
            }

            if (result.HasMore)
            {
                return false;
            }

            int totalCount = result.TotalCount;
            if (totalCount <= 0)
            {
                return false;
            }

            int loadedCount = Math.Max(0, (pageNumber - 1) * pageSize) + (result.Comments?.Count ?? 0);
            return totalCount > loadedCount;
        }

        private void StartHotFallback(int pageNumber, int totalCount)
        {
            _hotFallbackActive = true;
            _hotFallbackStartPage = Math.Max(1, pageNumber);
            _timeFallbackNextPage = 1;
            _timeFallbackHasMore = true;
            if (totalCount > 0)
            {
                _hotFallbackTotalCount = totalCount;
            }
        }

        private bool ShouldRetryEmptyRootPage(CommentResult result, int pageNumber, int pageSize)
        {
            if (result == null || pageNumber <= 0 || pageSize <= 0)
            {
                return false;
            }

            if (result.Comments != null && result.Comments.Count > 0)
            {
                return false;
            }

            int totalCount = result.TotalCount;
            if (totalCount <= 0)
            {
                return false;
            }

            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            return startIndex < totalCount;
        }

        private static int ComputeExpectedPageCount(int totalCount, bool hasMore, int pageNumber, int pageSize, int actualCount)
        {
            if (pageNumber <= 0 || pageSize <= 0)
            {
                return actualCount;
            }

            if (totalCount > 0)
            {
                int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
                if (startIndex >= totalCount)
                {
                    return 0;
                }

                int remaining = totalCount - startIndex;
                return Math.Min(pageSize, remaining);
            }

            if (hasMore)
            {
                return pageSize;
            }

            return actualCount;
        }

        private async Task<(CommentResult Result, bool HasMore, int PageSize)> FetchRootCommentsPageAsync(int pageNumber, CancellationToken token)
        {
            if (_sortType != CommentSortType.Hot)
            {
                var result = await _apiClient.GetCommentsPageAsync(
                    _target.ResourceId,
                    _target.Type,
                    pageNumber,
                    CommentsPageSize,
                    _sortType,
                    token).ConfigureAwait(true);
                int pageSize = result.PageSize > 0 ? result.PageSize : CommentsPageSize;
                return (result, result.HasMore, pageSize);
            }

            if (!_hotFallbackActive || pageNumber <= _hotFallbackStartPage)     
            {
                var result = await _apiClient.GetCommentsPageAsync(
                    _target.ResourceId,
                    _target.Type,
                    pageNumber,
                    CommentsPageSize,
                    CommentSortType.Hot,
                    token).ConfigureAwait(true);
                int pageSize = result.PageSize > 0 ? result.PageSize : CommentsPageSize;
                if (ShouldTriggerHotFallback(result, pageNumber, pageSize))     
                {
                    StartHotFallback(pageNumber, result.TotalCount);
                    int hotCount = result.Comments?.Count ?? 0;
                    if (hotCount < pageSize)
                    {
                        int remaining = pageSize - hotCount;
                        var excludeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (result.Comments != null)
                        {
                            foreach (var comment in result.Comments)
                            {
                                if (comment != null && !string.IsNullOrWhiteSpace(comment.CommentId))
                                {
                                    excludeIds.Add(comment.CommentId);
                                }
                            }
                        }

                        var (timeResult, timeHasMore, _) = await FetchTimeFallbackPageAsync(
                                pageNumber,
                                remaining,
                                token,
                                excludeIds)
                            .ConfigureAwait(true);

                        if (result.TotalCount <= 0 && timeResult.TotalCount > 0)
                        {
                            result.TotalCount = timeResult.TotalCount;
                        }

                        var merged = new List<CommentInfo>(pageSize);
                        if (result.Comments != null && result.Comments.Count > 0)
                        {
                            merged.AddRange(result.Comments);
                        }

                        if (timeResult.Comments != null && timeResult.Comments.Count > 0)
                        {
                            foreach (var comment in timeResult.Comments)
                            {
                                if (comment == null)
                                {
                                    continue;
                                }

                                merged.Add(comment);
                                if (merged.Count >= pageSize)
                                {
                                    break;
                                }
                            }
                        }

                        result.Comments = merged;
                        result.HasMore = timeHasMore;
                        return (result, timeHasMore, pageSize);
                    }

                    result.HasMore = true;
                    return (result, true, pageSize);
                }
                return (result, result.HasMore, pageSize);
            }

            return await FetchTimeFallbackPageAsync(pageNumber, null, token).ConfigureAwait(true);
        }

        private async Task<(CommentResult Result, bool HasMore, int PageSize)> FetchTimeFallbackPageAsync(int displayPage, int? targetCount, CancellationToken token, ISet<string>? excludeIds = null)
        {
            await _timeFallbackGate.WaitAsync(token).ConfigureAwait(true);      
            try
            {
                int pageSize = _rootPageSize > 0 ? _rootPageSize : CommentsPageSize;
                if (pageSize <= 0)
                {
                    pageSize = CommentsPageSize;
                }

                int targetPageSize = pageSize;
                if (targetCount.HasValue)
                {
                    targetPageSize = Math.Max(0, Math.Min(targetCount.Value, pageSize));
                }

                int timePage = Math.Max(1, _timeFallbackNextPage);
                bool hasMore = _timeFallbackHasMore;
                int totalCount = _hotFallbackTotalCount;

                var aggregated = new List<CommentInfo>(targetPageSize);
                var localIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (excludeIds != null)
                {
                    foreach (var id in excludeIds)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            localIdSet.Add(id);
                        }
                    }
                }

                int guard = 0;
                while (aggregated.Count < targetPageSize && hasMore)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await _apiClient.GetCommentsPageAsync(
                        _target.ResourceId,
                        _target.Type,
                        timePage,
                        CommentsPageSize,
                        CommentSortType.Time,
                        token).ConfigureAwait(true);

                    if (result.TotalCount > 0)
                    {
                        totalCount = result.TotalCount;
                    }

                    hasMore = result.HasMore;
                    timePage++;

                    var comments = result.Comments ?? new List<CommentInfo>();
                    if (comments.Count == 0 && !hasMore)
                    {
                        break;
                    }

                    foreach (var comment in comments)
                    {
                        if (comment == null || string.IsNullOrWhiteSpace(comment.CommentId))
                        {
                            continue;
                        }
                        if (_rootIndexById.ContainsKey(comment.CommentId))      
                        {
                            continue;
                        }
                        if (!localIdSet.Add(comment.CommentId))
                        {
                            continue;
                        }
                        aggregated.Add(comment);
                        if (aggregated.Count >= targetPageSize)
                        {
                            break;
                        }
                    }

                    guard++;
                    if (guard >= 20)
                    {
                        break;
                    }
                }

                _timeFallbackNextPage = timePage;
                _timeFallbackHasMore = hasMore;

                var merged = new CommentResult
                {
                    Comments = aggregated,
                    HasMore = hasMore,
                    PageNumber = displayPage,
                    PageSize = pageSize,
                    SortType = CommentSortType.Time,
                    TotalCount = totalCount
                };

                return (merged, hasMore, pageSize);
            }
            finally
            {
                _timeFallbackGate.Release();
            }
        }

        private void RenderRootCommentsOnce(List<CommentInfo> ordered, SelectionSnapshot? snapshot, string? focusCommentId, int pageNumber, int expectedCount, string reason)
        {
            int previousActiveVersion = _activeRenderVersion;
            _activeRenderVersion = _renderVersion;

            EnsureCommentsTreeViewHandle();
            if (ordered == null || ordered.Count == 0)
            {
                _commentsTreeView.BeginUpdate();
                try
                {
                    _commentsTreeView.Nodes.Clear();
                    _rootCommentSlots.Clear();
                    _rootLoadedPages.Clear();
                    _rootLoadingPages.Clear();
                    _rootRetryAttempts.Clear();
                    _rootRetryScheduled.Clear();
                    _rootIndexById.Clear();
                    _rootTotalCount = 0;
                    _rootRenderStart = -1;
                    _rootRenderEnd = -1;
                }
                finally
                {
                    _commentsTreeView.EndUpdate();
                    _activeRenderVersion = previousActiveVersion;
                }
                return;
            }

            EnsureRootPageSkeleton(pageNumber, GetRootPageSize());
            bool updated = ApplyRootPage(pageNumber, ordered, expectedCount, out bool isComplete);
            if (updated)
            {
                ScheduleAccessibilityReorder();
            }
            if (!isComplete)
            {
                ScheduleRootPartialRetryAsync(pageNumber, expectedCount, ordered.Count, reason)
                    .SafeFireAndForget("Comments auto-retry");
            }
            var pending = TryResolvePendingSelection(focusCommentId, snapshot);
            if (pending != null)
            {
                SelectNodeWithMode(pending, announceSelection: true, ensureFocus: true);
            }
            _activeRenderVersion = previousActiveVersion;
        }

        private void AppendRootComments(List<CommentInfo> ordered, int pageNumber, SelectionSnapshot? snapshot, string? focusCommentId, int expectedCount, string reason)
        {
            int previousActiveVersion = _activeRenderVersion;
            _activeRenderVersion = _renderVersion;

            EnsureCommentsTreeViewHandle();
            EnsureRootPageSkeleton(pageNumber, GetRootPageSize());
            bool updated = ApplyRootPage(pageNumber, ordered, expectedCount, out bool isComplete);
            if (updated)
            {
                ScheduleAccessibilityReorder();
            }
            if (!isComplete)
            {
                ScheduleRootPartialRetryAsync(pageNumber, expectedCount, ordered.Count, reason)
                    .SafeFireAndForget("Comments auto-retry");
            }
            var pending = TryResolvePendingSelection(focusCommentId, snapshot);
            if (pending != null)
            {
                SelectNodeWithMode(pending, announceSelection: false, ensureFocus: false);
            }
            _activeRenderVersion = previousActiveVersion;
        }

        private void ResetRootSkeletonState()
        {
            _rootTotalCount = 0;
            _rootPageSize = CommentsPageSize;
            _rootCommentSlots.Clear();
            _rootLoadedPages.Clear();
            _rootLoadingPages.Clear();
            _rootRetryAttempts.Clear();
            _rootRetryScheduled.Clear();
            _rootIndexById.Clear();
            _rootRenderStart = -1;
            _rootRenderEnd = -1;
        }

        private void BuildRootSkeletonIfNeeded()
        {
            int nodeCount = _commentsTreeView.Nodes.Count;
            if (_rootCommentSlots.Count < nodeCount)
            {
                for (int i = _rootCommentSlots.Count; i < nodeCount; i++)
                {
                    _rootCommentSlots.Add(null);
                }
            }
            else if (_rootCommentSlots.Count > nodeCount)
            {
                _rootCommentSlots.RemoveRange(nodeCount, _rootCommentSlots.Count - nodeCount);
            }

            if (_rootTotalCount != nodeCount)
            {
                _rootTotalCount = nodeCount;
                _rootRenderStart = -1;
                _rootRenderEnd = -1;
            }
        }

        private void EnsureRootPageSkeleton(int pageNumber, int pageSize)
        {
            if (pageNumber <= 0)
            {
                return;
            }

            pageSize = pageSize > 0 ? pageSize : CommentsPageSize;
            int requiredCount = pageNumber * pageSize;
            if (requiredCount <= _commentsTreeView.Nodes.Count)
            {
                BuildRootSkeletonIfNeeded();
                return;
            }

            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = _commentsTreeView.Nodes.Count; i < requiredCount; i++)
                {
                    _commentsTreeView.Nodes.Add(CreateRootPlaceholderNode(i));
                    _rootCommentSlots.Add(null);
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            _rootTotalCount = _commentsTreeView.Nodes.Count;
        }

        private TreeNode CreateRootPlaceholderNode(int index)
        {
            var node = CreateNode(null, BuildRootPlaceholderText(index));       
            node.Tag = new RootPlaceholderTag(index);
            node.ForeColor = SystemColors.GrayText;
            return node;
        }

        private string BuildRootPlaceholderText(int index)
        {
            return $"{index + 1}. {RootPlaceholderText}";
        }

        private string BuildRootFailedText(int index)
        {
            return $"{index + 1}. {RootFailedText}";
        }

        private int GetRootPageSize()
        {
            return _rootPageSize > 0 ? _rootPageSize : CommentsPageSize;
        }

        private bool ApplyRootPage(int pageNumber, IReadOnlyList<CommentInfo> comments, int expectedCount, out bool isCompletePage)
        {
            int actualCount = comments?.Count ?? 0;
            expectedCount = Math.Max(0, expectedCount);
            isCompletePage = expectedCount == 0 || actualCount >= expectedCount;

            if (_rootTotalCount <= 0)
            {
                return false;
            }

            if (comments == null || comments.Count == 0)
            {
                if (isCompletePage)
                {
                    _rootLoadedPages.Add(pageNumber);
                    _rootRetryAttempts.Remove(pageNumber);
                }
                else
                {
                    _rootLoadedPages.Remove(pageNumber);
                }
                _rootLoadingPages.Remove(pageNumber);
                return false;
            }

            int pageSize = GetRootPageSize();
            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            int maxIndex = Math.Min(_rootTotalCount, startIndex + comments.Count);
            if (startIndex >= _rootTotalCount || startIndex >= maxIndex)
            {
                if (isCompletePage)
                {
                    _rootLoadedPages.Add(pageNumber);
                    _rootRetryAttempts.Remove(pageNumber);
                }
                else
                {
                    _rootLoadedPages.Remove(pageNumber);
                }
                _rootLoadingPages.Remove(pageNumber);
                return false;
            }

            bool updated = false;
            int? renderStart = null;
            int? renderEnd = null;
            if (TryGetRootRenderRange(out int activeStart, out int activeEnd))
            {
                renderStart = activeStart;
                renderEnd = activeEnd;
            }

            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = 0; i < comments.Count; i++)
                {
                    int targetIndex = startIndex + i;
                    if (targetIndex >= _rootTotalCount)
                    {
                        break;
                    }

                    var info = comments[i];
                    _rootCommentSlots[targetIndex] = info;
                    if (!string.IsNullOrWhiteSpace(info.CommentId))
                    {
                        _rootIndexById[info.CommentId] = targetIndex;
                    }

                    if (renderStart.HasValue &&
                        (targetIndex < renderStart.Value || targetIndex > renderEnd!.Value))
                    {
                        continue;
                    }

                    if (targetIndex >= 0 && targetIndex < _commentsTreeView.Nodes.Count)
                    {
                        var node = _commentsTreeView.Nodes[targetIndex];
                        if (node != null)
                        {
                            if (ApplyRootNodeFromSlot(node, info, targetIndex))
                            {
                                updated = true;
                            }
                        }
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            if (isCompletePage)
            {
                _rootLoadedPages.Add(pageNumber);
                _rootRetryAttempts.Remove(pageNumber);
            }
            else
            {
                _rootLoadedPages.Remove(pageNumber);
            }
            _rootLoadingPages.Remove(pageNumber);
            return updated;
        }

        private void TrimRootSkeletonToCount(int newTotal)
        {
            newTotal = Math.Max(0, newTotal);
            if (newTotal >= _rootTotalCount)
            {
                return;
            }

            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = _commentsTreeView.Nodes.Count - 1; i >= newTotal; i--)
                {
                    _commentsTreeView.Nodes.RemoveAt(i);
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            if (_rootCommentSlots.Count > newTotal)
            {
                _rootCommentSlots.RemoveRange(newTotal, _rootCommentSlots.Count - newTotal);
            }

            _rootIndexById.Clear();
            for (int i = 0; i < _rootCommentSlots.Count; i++)
            {
                var info = _rootCommentSlots[i];
                if (info != null && !string.IsNullOrWhiteSpace(info.CommentId))
                {
                    _rootIndexById[info.CommentId] = i;
                }
            }

            int pageSize = GetRootPageSize();
            _rootLoadedPages.RemoveWhere(page => (page - 1) * pageSize >= newTotal);
            _rootLoadingPages.RemoveWhere(page => (page - 1) * pageSize >= newTotal);
            _rootRetryScheduled.RemoveWhere(page => (page - 1) * pageSize >= newTotal);
            foreach (var page in _rootRetryAttempts.Keys.Where(page => (page - 1) * pageSize >= newTotal).ToList())
            {
                _rootRetryAttempts.Remove(page);
            }

            _rootTotalCount = newTotal;
            _rootRenderStart = -1;
            _rootRenderEnd = -1;
            _hasMore = false;
        }

        private bool MarkRootPageFailed(int pageNumber)
        {
            if (_rootTotalCount <= 0)
            {
                return false;
            }

            int pageSize = GetRootPageSize();
            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            int endIndex = Math.Min(_rootTotalCount - 1, startIndex + pageSize - 1);
            if (startIndex >= _rootTotalCount || endIndex < startIndex)
            {
                return false;
            }

            bool updated = false;
            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = startIndex; i <= endIndex && i < _commentsTreeView.Nodes.Count; i++)
                {
                    var node = _commentsTreeView.Nodes[i];
                    if (node == null)
                    {
                        continue;
                    }

                    if (node.Tag is RootPlaceholderTag)
                    {
                        string text = BuildRootFailedText(i);
                        if (!string.Equals(node.Text, text, StringComparison.Ordinal))
                        {
                            node.Text = text;
                            updated = true;
                        }

                        if (node.ForeColor != SystemColors.GrayText)
                        {
                            node.ForeColor = SystemColors.GrayText;
                            updated = true;
                        }
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            return updated;
        }

        private void ResetRootPageNodesToLoading(int pageNumber)
        {
            if (_rootTotalCount <= 0)
            {
                return;
            }

            int pageSize = GetRootPageSize();
            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            int endIndex = Math.Min(_rootTotalCount - 1, startIndex + pageSize - 1);
            if (startIndex >= _rootTotalCount || endIndex < startIndex)
            {
                return;
            }

            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = startIndex; i <= endIndex && i < _commentsTreeView.Nodes.Count; i++)
                {
                    var node = _commentsTreeView.Nodes[i];
                    if (node == null)
                    {
                        continue;
                    }

                    if (node.Tag is RootPlaceholderTag)
                    {
                        string text = BuildRootPlaceholderText(i);
                        if (!string.Equals(node.Text, text, StringComparison.Ordinal))
                        {
                            node.Text = text;
                        }

                        if (node.ForeColor != SystemColors.GrayText)
                        {
                            node.ForeColor = SystemColors.GrayText;
                        }
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }
        }

        private void UpdateRootNodeFromComment(TreeNode node, CommentInfo info, int index)
        {
            _ = ApplyRootNodeFromSlot(node, info, index);
        }

        private bool ApplyRootNodeFromSlot(TreeNode node, CommentInfo info, int index)
        {
            if (node == null || info == null)
            {
                return false;
            }

            bool updated = false;
            CommentTreeNodeState? state = node.Tag as CommentTreeNodeState;
            if (state == null || !string.Equals(state.Comment.CommentId, info.CommentId, StringComparison.OrdinalIgnoreCase))
            {
                state = new CommentTreeNodeState(info);
                node.Tag = state;
                updated = true;
            }

            string text = BuildNodeText(info, isRoot: true, rootIndex: index);
            if (!string.Equals(node.Text, text, StringComparison.Ordinal))
            {
                node.Text = text;
                updated = true;
            }

            if (node.ForeColor != SystemColors.WindowText)
            {
                node.ForeColor = SystemColors.WindowText;
                updated = true;
            }

            if (info.ReplyCount > 0)
            {
                if (!state.RepliesSkeletonBuilt && node.Nodes.Count == 0)
                {
                    EnsureReplyCollapsedPlaceholder(node);
                    updated = true;
                }
            }
            else
            {
                if (node.Nodes.Count > 0 || state.RepliesSkeletonBuilt)
                {
                    node.Nodes.Clear();
                    ResetReplySkeletonState(state);
                    updated = true;
                }
            }

            return updated;
        }

        private void EnsureReplySkeleton(TreeNode node, CommentTreeNodeState state, int? knownTotalCount)
        {
            if (node == null || state == null)
            {
                return;
            }

            int targetTotal = Math.Max(0, knownTotalCount ?? state.Comment.ReplyCount);
            BuildReplySkeletonIfNeeded(node, state, targetTotal);
        }

        private void EnsureReplyCollapsedPlaceholder(TreeNode node)
        {
            if (node == null)
            {
                return;
            }

            if (node.Nodes.Count == 0)
            {
                node.Nodes.Add(CreatePlaceholderNode(node, "展开以查看回复"));
            }
        }

        private void ResetReplySkeletonState(CommentTreeNodeState state)
        {
            if (state == null)
            {
                return;
            }

            state.ReplyTotalCount = 0;
            state.ReplyPageSize = ReplyPageSize;
            state.ReplySlots.Clear();
            state.ReplyLoadedPages.Clear();
            state.ReplyLoadingPages.Clear();
            state.RepliesSkeletonBuilt = false;
            state.RepliesLoaded = false;
            state.HasMoreReplies = state.Comment.ReplyCount > 0;
            state.ReplyNextPageNumber = 1;
            state.ReplyPendingTargetPage = 0;
            state.ReplyRetryScheduled = false;
            state.ReplyFailedPageAttempts.Clear();
            state.ReplyRenderStart = -1;
            state.ReplyRenderEnd = -1;
        }

        private void BuildReplySkeletonIfNeeded(TreeNode node, CommentTreeNodeState state, int totalCount)
        {
            if (node == null || state == null)
            {
                return;
            }

            totalCount = Math.Max(0, totalCount);
            if (totalCount <= 0)
            {
                node.Nodes.Clear();
                ResetReplySkeletonState(state);
                return;
            }

            if (state.RepliesSkeletonBuilt && state.ReplyTotalCount == totalCount && node.Nodes.Count == totalCount)
            {
                return;
            }

            state.ReplyTotalCount = totalCount;
            state.ReplyPageSize = ReplyPageSize;
            state.ReplySlots.Clear();
            for (int i = 0; i < totalCount; i++)
            {
                state.ReplySlots.Add(null);
            }
            state.ReplyLoadedPages.Clear();
            state.ReplyLoadingPages.Clear();
            state.RepliesSkeletonBuilt = true;
            state.RepliesLoaded = false;
            state.HasMoreReplies = totalCount > 0;
            state.ReplyNextPageNumber = 1;
            state.ReplyPendingTargetPage = 0;
            state.ReplyRetryScheduled = false;
            state.ReplyFailedPageAttempts.Clear();
            state.ReplyRenderStart = -1;
            state.ReplyRenderEnd = -1;

            _commentsTreeView.BeginUpdate();
            try
            {
                node.Nodes.Clear();
                var placeholders = new TreeNode[totalCount];
                for (int i = 0; i < totalCount; i++)
                {
                    placeholders[i] = CreateReplyPlaceholderNode(node, i);
                }
                node.Nodes.AddRange(placeholders);
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }
        }

        private async Task EnsureReplyVisibleRangeLoadedAsync(TreeNode parent, CommentTreeNodeState state, string reason)
        {
            if (parent == null || state == null)
            {
                return;
            }

            if (_suppressAutoLoad)
            {
                return;
            }

            int total = Math.Max(0, state.ReplyTotalCount);
            if (total <= 0)
            {
                return;
            }

            if (!state.RepliesSkeletonBuilt)
            {
                BuildReplySkeletonIfNeeded(parent, state, total);
            }

            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            if (!TryGetReplyVisibleRangeIndices(parent, state, out int startIndex, out int endIndex))
            {
                return;
            }

            endIndex = Math.Min(total - 1, endIndex + pageSize);

            int startPage = (startIndex / pageSize) + 1;
            int lastPage = (endIndex / pageSize) + 1;
            await EnsureReplyPagesLoadedRangeAsync(parent, state, startPage, lastPage, reason).ConfigureAwait(true);
            EnsureReplyVisibleRangeRendered(parent, state);
        }

        private async Task EnsureReplyPagesLoadedRangeAsync(TreeNode parent, CommentTreeNodeState state, int startPage, int endPage, string reason)
        {
            if (startPage <= 0 || endPage <= 0)
            {
                return;
            }

            if (endPage < startPage)
            {
                return;
            }

            for (int page = startPage; page <= endPage; page++)
            {
                await EnsureReplyPagesLoadedAsync(parent, state, page, reason).ConfigureAwait(true);
            }
        }

        private void EnsureReplyVisibleRangeRendered(TreeNode parent, CommentTreeNodeState state)
        {
            if (!TryGetReplyRenderRange(parent, state, out int startIndex, out int endIndex))
            {
                return;
            }

            if (startIndex == state.ReplyRenderStart && endIndex == state.ReplyRenderEnd)
            {
                return;
            }

            bool updated = ApplyReplySlotsInRange(parent, state, startIndex, endIndex);
            if (updated)
            {
                ScheduleAccessibilityReorder();
            }

            state.ReplyRenderStart = startIndex;
            state.ReplyRenderEnd = endIndex;
        }

        private bool TryGetReplyVisibleRangeIndices(TreeNode parent, CommentTreeNodeState state, out int startIndex, out int endIndex)
        {
            startIndex = 0;
            endIndex = -1;

            if (parent == null || state == null)
            {
                return false;
            }

            int total = Math.Max(0, state.ReplyTotalCount);
            if (total <= 0)
            {
                return false;
            }

            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            startIndex = 0;
            endIndex = Math.Min(total - 1, pageSize - 1 + ReplyPrefetchPadding);
            int? selectedIndex = null;
            var selectedNode = _commentsTreeView.SelectedNode;
            if (selectedNode?.Parent == parent)
            {
                selectedIndex = selectedNode.Index;
            }

            try
            {
                TreeNode? top = _commentsTreeView.TopNode;
                if (top != null)
                {
                    TreeNode? current = top;
                    while (current != null && current.Parent != parent)
                    {
                        current = current.NextVisibleNode;
                    }

                    if (current != null && current.Parent == parent)
                    {
                        startIndex = current.Index;
                        int visibleCount = _commentsTreeView.VisibleCount;
                        endIndex = Math.Min(total - 1, startIndex + Math.Max(1, visibleCount) + ReplyPrefetchPadding);
                    }
                }
            }
            catch
            {
                startIndex = 0;
                endIndex = total - 1;
            }

            if (selectedIndex.HasValue)
            {
                if (selectedIndex.Value < startIndex)
                {
                    startIndex = selectedIndex.Value;
                }
                endIndex = Math.Max(endIndex, selectedIndex.Value);
            }

            return endIndex >= startIndex;
        }

        private bool TryGetReplyRenderRange(TreeNode parent, CommentTreeNodeState state, out int startIndex, out int endIndex)
        {
            startIndex = 0;
            endIndex = -1;

            if (!TryGetReplyVisibleRangeIndices(parent, state, out int visibleStart, out int visibleEnd))
            {
                return false;
            }

            int total = Math.Max(0, state.ReplyTotalCount);
            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            startIndex = Math.Max(0, visibleStart - pageSize);
            endIndex = Math.Min(total - 1, visibleEnd + pageSize);
            return endIndex >= startIndex;
        }

        private bool ApplyReplySlotsInRange(TreeNode parent, CommentTreeNodeState state, int startIndex, int endIndex)
        {
            if (parent == null || state == null)
            {
                return false;
            }

            int total = Math.Max(0, state.ReplyTotalCount);
            if (total <= 0)
            {
                return false;
            }

            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(total - 1, endIndex);
            if (endIndex < startIndex)
            {
                return false;
            }

            bool updated = false;
            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = startIndex; i <= endIndex && i < parent.Nodes.Count; i++)
                {
                    var info = i >= 0 && i < state.ReplySlots.Count ? state.ReplySlots[i] : null;
                    if (info == null)
                    {
                        continue;
                    }

                    var node = parent.Nodes[i];
                    if (node == null)
                    {
                        continue;
                    }

                    if (ApplyReplyNodeFromSlot(node, info))
                    {
                        updated = true;
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            return updated;
        }

        private void TrimReplySkeletonToCount(TreeNode parent, CommentTreeNodeState state, int newTotal)
        {
            if (parent == null || state == null)
            {
                return;
            }

            newTotal = Math.Max(0, newTotal);
            if (newTotal >= state.ReplyTotalCount)
            {
                return;
            }

            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = parent.Nodes.Count - 1; i >= newTotal; i--)
                {
                    parent.Nodes.RemoveAt(i);
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            if (state.ReplySlots.Count > newTotal)
            {
                state.ReplySlots.RemoveRange(newTotal, state.ReplySlots.Count - newTotal);
            }

            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            state.ReplyLoadedPages.RemoveWhere(page => (page - 1) * pageSize >= newTotal);
            state.ReplyLoadingPages.RemoveWhere(page => (page - 1) * pageSize >= newTotal);

            state.ReplyTotalCount = newTotal;
            state.HasMoreReplies = false;
            state.ReplyRenderStart = -1;
            state.ReplyRenderEnd = -1;
        }

        private bool ApplyReplyNodeFromSlot(TreeNode node, CommentInfo info)
        {
            if (node == null || info == null)
            {
                return false;
            }

            bool updated = false;
            CommentTreeNodeState? existingState = node.Tag as CommentTreeNodeState;
            if (existingState == null || !string.Equals(existingState.Comment.CommentId, info.CommentId, StringComparison.OrdinalIgnoreCase))
            {
                existingState = new CommentTreeNodeState(info);
                node.Tag = existingState;
                updated = true;
            }

            string text = BuildNodeText(info, isRoot: false, rootIndex: null);
            if (!string.Equals(node.Text, text, StringComparison.Ordinal))
            {
                node.Text = text;
                updated = true;
            }

            if (node.ForeColor != SystemColors.WindowText)
            {
                node.ForeColor = SystemColors.WindowText;
                updated = true;
            }

            if (node.Nodes.Count > 0)
            {
                node.Nodes.Clear();
                updated = true;
            }

            return updated;
        }

        private async Task EnsureReplyPagesLoadedAsync(TreeNode parent, CommentTreeNodeState state, int targetPage, string reason)
        {
            if (parent == null || state == null || targetPage <= 0)
            {
                return;
            }

            if (state.ReplyTotalCount <= 0)
            {
                return;
            }

            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            if (!state.RepliesSkeletonBuilt)
            {
                BuildReplySkeletonIfNeeded(parent, state, state.ReplyTotalCount);
            }

            if (state.ReplyLoadedPages.Contains(targetPage) || state.ReplyLoadingPages.Contains(targetPage))
            {
                return;
            }

            state.ReplyLoadingPages.Add(targetPage);
            try
            {
                ResetReplyPageNodesToLoading(parent, state, targetPage);
                var token = _lifecycleCts.Token;
                var result = await _apiClient.GetCommentFloorPageAsync(
                    _target.ResourceId,
                    state.Comment.CommentId,
                    _target.Type,
                    targetPage,
                    pageSize,
                    token).ConfigureAwait(true);

                int totalCount = result.TotalCount > 0 ? result.TotalCount : state.Comment.ReplyCount;
                if (totalCount > 0 && totalCount != state.ReplyTotalCount)
                {
                    BuildReplySkeletonIfNeeded(parent, state, totalCount);
                }

                int expectedStartIndex = (targetPage - 1) * pageSize;
                bool expectedData = state.ReplyTotalCount > expectedStartIndex;
                IReadOnlyList<CommentInfo> comments = result.Comments ?? new List<CommentInfo>();
                bool hasComments = comments.Count > 0;
                int expectedCount = ComputeExpectedPageCount(
                    totalCount,
                    result.HasMore,
                    targetPage,
                    pageSize,
                    comments.Count);

                if (!hasComments && expectedData)
                {
                    state.ReplyPendingTargetPage = Math.Max(state.ReplyPendingTargetPage, targetPage);
                    ScheduleReplyAutoRetryAsync(parent, state, targetPage).SafeFireAndForget("Comments auto-retry");
                    return;
                }

                if (!hasComments)
                {
                    state.ReplyLoadedPages.Add(targetPage);
                    state.ReplyNextPageNumber = Math.Max(state.ReplyNextPageNumber, targetPage + 1);
                    state.HasMoreReplies = result.HasMore && result.NextTime.HasValue;
                    state.RepliesLoaded = state.ReplyLoadedPages.Count > 0;
                    state.ReplyFailedPageAttempts.Remove(targetPage);
                    return;
                }

                ApplyReplyPage(parent, state, targetPage, comments, expectedCount, out bool isComplete);
                state.HasMoreReplies = result.HasMore && result.NextTime.HasValue;
                state.RepliesLoaded = state.ReplyLoadedPages.Count > 0;
                state.ReplyNextPageNumber = Math.Max(state.ReplyNextPageNumber, targetPage + 1);
                if (!isComplete)
                {
                    ScheduleReplyPartialRetryAsync(parent, state, targetPage, expectedCount, comments.Count, reason)
                        .SafeFireAndForget("Comments auto-retry");
                }

                if (!result.HasMore)
                {
                    int startIndex = Math.Max(0, (targetPage - 1) * pageSize);
                    int newTotal = startIndex + comments.Count;
                    if (newTotal < state.ReplyTotalCount)
                    {
                        TrimReplySkeletonToCount(parent, state, newTotal);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                int attempts = state.ReplyFailedPageAttempts.TryGetValue(targetPage, out int current) ? current : 0;
                attempts++;
                state.ReplyFailedPageAttempts[targetPage] = attempts;

                MarkReplyPageFailed(parent, state, targetPage);
                DebugLogger.LogException("CommentsDialog", ex,
                    $"回复页加载失败: parent={state.Comment.CommentId}, page={targetPage}, attempt={attempts}, reason={reason}");

                if (attempts <= ReplyPageMaxRetryAttempts)
                {
                    int delayMs = ReplyPageRetryDelayMs * attempts;
                    try
                    {
                        await Task.Delay(delayMs, _lifecycleCts.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!IsDisposed && !_commentsTreeView.IsDisposed)
                    {
                        _ = EnsureReplyPagesLoadedAsync(parent, state, targetPage, "AutoRetry");
                    }
                }
            }
            finally
            {
                state.ReplyLoadingPages.Remove(targetPage);
            }
        }

        private void ApplyReplyPage(TreeNode parent, CommentTreeNodeState state, int pageNumber, IReadOnlyList<CommentInfo> comments, int expectedCount, out bool isCompletePage)
        {
            if (parent == null || state == null)
            {
                isCompletePage = expectedCount <= 0;
                return;
            }

            int actualCount = comments?.Count ?? 0;
            expectedCount = Math.Max(0, expectedCount);
            isCompletePage = expectedCount == 0 || actualCount >= expectedCount;

            int total = Math.Max(0, state.ReplyTotalCount);
            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            int maxIndex = Math.Min(total, startIndex + (comments?.Count ?? 0));

            if (comments == null || comments.Count == 0 || startIndex >= total || startIndex >= maxIndex)
            {
                if (isCompletePage)
                {
                    state.ReplyLoadedPages.Add(pageNumber);
                    state.ReplyFailedPageAttempts.Remove(pageNumber);
                }
                else
                {
                    state.ReplyLoadedPages.Remove(pageNumber);
                }
                return;
            }

            int? renderStart = null;
            int? renderEnd = null;
            if (TryGetReplyRenderRange(parent, state, out int activeStart, out int activeEnd))
            {
                renderStart = activeStart;
                renderEnd = activeEnd;
            }

            bool updated = false;
            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = 0; i < comments.Count; i++)
                {
                    int targetIndex = startIndex + i;
                    if (targetIndex >= total || targetIndex >= parent.Nodes.Count)
                    {
                        break;
                    }

                    var info = comments[i];
                    state.ReplySlots[targetIndex] = info;
                    var node = parent.Nodes[targetIndex];

                    if (renderStart.HasValue &&
                        (targetIndex < renderStart.Value || targetIndex > renderEnd!.Value))
                    {
                        continue;
                    }

                    if (ApplyReplyNodeFromSlot(node, info))
                    {
                        updated = true;
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            if (isCompletePage)
            {
                state.ReplyLoadedPages.Add(pageNumber);
                state.ReplyFailedPageAttempts.Remove(pageNumber);
            }
            else
            {
                state.ReplyLoadedPages.Remove(pageNumber);
            }
            if (renderStart.HasValue)
            {
                state.ReplyRenderStart = renderStart.Value;
                state.ReplyRenderEnd = renderEnd!.Value;
            }
            if (updated)
            {
                ScheduleAccessibilityReorder();
            }
        }

        private bool MarkReplyPageFailed(TreeNode parent, CommentTreeNodeState state, int pageNumber)
        {
            if (parent == null || state == null)
            {
                return false;
            }

            int total = Math.Max(0, state.ReplyTotalCount);
            if (total <= 0)
            {
                return false;
            }

            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            int endIndex = Math.Min(total - 1, startIndex + pageSize - 1);
            if (startIndex >= total || endIndex < startIndex)
            {
                return false;
            }

            bool updated = false;
            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = startIndex; i <= endIndex && i < parent.Nodes.Count; i++)
                {
                    var node = parent.Nodes[i];
                    if (node == null)
                    {
                        continue;
                    }

                    if (node.Tag is ReplyPlaceholderTag)
                    {
                        string text = BuildReplyFailedText(i);
                        if (!string.Equals(node.Text, text, StringComparison.Ordinal))
                        {
                            node.Text = text;
                            updated = true;
                        }

                        if (node.ForeColor != SystemColors.GrayText)
                        {
                            node.ForeColor = SystemColors.GrayText;
                            updated = true;
                        }
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            return updated;
        }

        private void ResetReplyPageNodesToLoading(TreeNode parent, CommentTreeNodeState state, int pageNumber)
        {
            if (parent == null || state == null)
            {
                return;
            }

            int total = Math.Max(0, state.ReplyTotalCount);
            if (total <= 0)
            {
                return;
            }

            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
            int endIndex = Math.Min(total - 1, startIndex + pageSize - 1);
            if (startIndex >= total || endIndex < startIndex)
            {
                return;
            }

            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = startIndex; i <= endIndex && i < parent.Nodes.Count; i++)
                {
                    var node = parent.Nodes[i];
                    if (node == null)
                    {
                        continue;
                    }

                    if (node.Tag is ReplyPlaceholderTag)
                    {
                        string text = BuildReplyPlaceholderText(i);
                        if (!string.Equals(node.Text, text, StringComparison.Ordinal))
                        {
                            node.Text = text;
                        }

                        if (node.ForeColor != SystemColors.GrayText)
                        {
                            node.ForeColor = SystemColors.GrayText;
                        }
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }
        }

        private TreeNode CreateReplyPlaceholderNode(TreeNode parent, int index)
        {
            var node = CreateNode(parent, BuildReplyPlaceholderText(index));    
            node.Tag = new ReplyPlaceholderTag(index);
            node.ForeColor = SystemColors.GrayText;
            return node;
        }

        private string BuildReplyPlaceholderText(int index)
        {
            return $"{index + 1}. {ReplyPlaceholderText}";
        }

        private string BuildReplyFailedText(int index)
        {
            return $"{index + 1}. {ReplyFailedText}";
        }

        private async Task EnsureRootPagesLoadedAsync(int targetPage, string reason)
        {
            if (targetPage <= 0)
            {
                return;
            }

            if (!_hasMore && _rootLoadedPages.Count > 0)
            {
                int pageSize = GetRootPageSize();
                int startIndex = Math.Max(0, (targetPage - 1) * pageSize);
                if (_rootTotalCount > 0 && startIndex >= _rootTotalCount)
                {
                    return;
                }
            }

            BuildRootSkeletonIfNeeded();

            await LoadRootPageAsync(targetPage, reason).ConfigureAwait(true);
        }

        private async Task LoadRootPageAsync(int pageNumber, string reason)
        {
            if (pageNumber <= 0)
            {
                return;
            }

            EnsureRootPageSkeleton(pageNumber, GetRootPageSize());

            if (_rootLoadedPages.Contains(pageNumber) || _rootLoadingPages.Contains(pageNumber))
            {
                return;
            }

            _rootLoadingPages.Add(pageNumber);
            ResetRootPageNodesToLoading(pageNumber);
            int renderVersion = _renderVersion;
            try
            {
                var token = _loadCommentsCts?.Token ?? _lifecycleCts.Token;
                var (result, hasMore, effectivePageSize) = await FetchRootCommentsPageAsync(pageNumber, token)
                    .ConfigureAwait(true);

                if (renderVersion != _renderVersion)
                {
                    return;
                }

                if (effectivePageSize <= 0)
                {
                    effectivePageSize = CommentsPageSize;
                }
                if (ShouldRetryEmptyRootPage(result, pageNumber, effectivePageSize))
                {
                    throw new InvalidOperationException("评论页返回空列表，触发重试。");
                }
                _hasMore = hasMore;
                if (effectivePageSize > 0 && effectivePageSize != _rootPageSize)
                {
                    _rootPageSize = effectivePageSize;
                }

                var ordered = result.Comments ?? new List<CommentInfo>();
                int expectedCount = ComputeExpectedPageCount(
                    result.TotalCount,
                    hasMore,
                    pageNumber,
                    effectivePageSize,
                    ordered.Count);
                if (ApplyRootPage(pageNumber, ordered, expectedCount, out bool isComplete))
                {
                    ScheduleAccessibilityReorder();
                }
                if (!isComplete)
                {
                    ScheduleRootPartialRetryAsync(pageNumber, expectedCount, ordered.Count, reason)
                        .SafeFireAndForget("Comments auto-retry");
                }

                _nextPageNumber = Math.Max(_nextPageNumber, pageNumber + 1);

                if (!_hasMore)
                {
                    int pageSize = GetRootPageSize();
                    int startIndex = Math.Max(0, (pageNumber - 1) * pageSize);
                    int newTotal = startIndex + ordered.Count;
                    if (newTotal < _rootTotalCount)
                    {
                        TrimRootSkeletonToCount(newTotal);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                int attempts = _rootRetryAttempts.TryGetValue(pageNumber, out int current) ? current : 0;
                attempts++;
                _rootRetryAttempts[pageNumber] = attempts;

                MarkRootPageFailed(pageNumber);
                DebugLogger.LogException("CommentsDialog", ex,
                    $"根评论页加载失败: page={pageNumber}, attempt={attempts}, reason={reason}");

                if (attempts <= RootPageMaxRetryAttempts)
                {
                    int delayMs = RootPageRetryDelayMs * attempts;
                    try
                    {
                        await Task.Delay(delayMs, _lifecycleCts.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (renderVersion == _renderVersion && !IsDisposed)
                    {
                        _ = LoadRootPageAsync(pageNumber, "AutoRetry");
                    }
                }
            }
            finally
            {
                _rootLoadingPages.Remove(pageNumber);
            }
        }

        private async Task ScheduleRootPartialRetryAsync(int pageNumber, int expectedCount, int actualCount, string reason)
        {
            if (pageNumber <= 0 || expectedCount <= 0 || actualCount >= expectedCount)
            {
                return;
            }

            if (IsDisposed || _rootRetryScheduled.Contains(pageNumber))
            {
                return;
            }

            _rootRetryScheduled.Add(pageNumber);

            int attempts = _rootRetryAttempts.TryGetValue(pageNumber, out int current) ? current : 0;
            attempts++;
            _rootRetryAttempts[pageNumber] = attempts;

            DebugLogger.Log(DebugLogger.LogLevel.Warning, "CommentsDialog",
                $"根评论页内容不足: page={pageNumber}, expected={expectedCount}, actual={actualCount}, attempt={attempts}, reason={reason}");

            if (attempts > RootPageMaxRetryAttempts)
            {
                _rootRetryScheduled.Remove(pageNumber);
                MarkRootPageFailed(pageNumber);
                return;
            }

            ResetRootPageNodesToLoading(pageNumber);
            int delayMs = RootPageRetryDelayMs * attempts;
            try
            {
                await Task.Delay(delayMs, _lifecycleCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _rootRetryScheduled.Remove(pageNumber);
            }

            if (IsDisposed || _rootLoadedPages.Contains(pageNumber))
            {
                return;
            }

            _ = LoadRootPageAsync(pageNumber, "PartialRetry");
        }

        private void ClearTreeView()
        {
            bool previousAutoLoadState = _suppressAutoLoad;
            _suppressAutoLoad = true;

            EnsureCommentsTreeViewHandle();
            _commentsTreeView.BeginUpdate();
            try
            {
                _commentsTreeView.Nodes.Clear();
                _commentsTreeView.SelectedNode = null;
            }
            finally
            {
                _commentsTreeView.EndUpdate();
                _suppressAutoLoad = previousAutoLoadState;
            }
        }

        private void EnsureCommentsTreeViewHandle()
        {
            if (!_commentsTreeView.IsHandleCreated)
            {
                _commentsTreeView.CreateControl();
            }
        }
        private void AccessibilityReorderTimer_Tick(object? sender, EventArgs e)
        {
            _accessibilityReorderTimer.Stop();
            _accessibilityReorderPending = false;
            NotifyAccessibilityReorder();
        }

        private void ScheduleAccessibilityReorder()
        {
            if (_accessibilityReorderPending || IsDisposed)
            {
                return;
            }

            _accessibilityReorderPending = true;
            _accessibilityReorderTimer.Stop();
            _accessibilityReorderTimer.Start();
        }

        private void NotifyAccessibilityReorder()
        {
            _commentsTreeView.NotifyAccessibilityReorder();
        }

        private async Task ChangeSortAsync()
        {
            if (_sortComboBox.SelectedItem is SortOption option)
            {
                _sortType = option.SortType;
                _nextPageNumber = 1;
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
            string targetText = _isLoading ? "正在加载 ..." : _targetCommentsLabel;
            if (!string.Equals(_statusLabelText, targetText, StringComparison.Ordinal))
            {
                _statusLabelText = targetText;
                _statusLabel.Text = targetText;
            }
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

        private TreeNode CreateNode(TreeNode? parent, string text)
        {
            return new TreeNode(text);
        }

        private TreeNode CreateCommentNode(CommentInfo info, TreeNode? parent, bool isReply = false, int? rootIndexOverride = null)
        {
            var state = new CommentTreeNodeState(info);
            bool isRoot = !isReply;
            var node = CreateNode(parent, string.Empty);
            node.Tag = state;

            if (isRoot && info.ReplyCount > 0)
            {
                EnsureReplyCollapsedPlaceholder(node);
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

        private TreeNode CreatePlaceholderNode(TreeNode parent, string text)
        {
            var node = CreateNode(parent, text);
            node.Tag = PlaceholderNodeTag;
            node.ForeColor = SystemColors.GrayText;
            return node;
        }

        private void CommentsTreeView_BeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            if (e.Node?.Tag is CommentTreeNodeState state)
            {
                if (state.Comment.ReplyCount > 0)
                {
                    EnsureReplySkeleton(e.Node, state, knownTotalCount: state.Comment.ReplyCount);
                    _ = EnsureReplyVisibleRangeLoadedAsync(e.Node, state, "BeforeExpand");
                    EnsureReplyVisibleRangeRendered(e.Node, state);
                }
            }
            else if (e.Node?.Tag == PlaceholderNodeTag || e.Node?.Tag is ReplyPlaceholderTag || e.Node?.Tag is RootPlaceholderTag)
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

            if (e.KeyCode == Keys.End || e.KeyCode == Keys.PageDown)
            {
                BeginInvoke(new Action(() =>
                {
                    if (_suppressAutoLoad || IsDisposed)
                    {
                        return;
                    }

                    var current = _commentsTreeView.SelectedNode;
                    if (current == null)
                    {
                        return;
                    }

                    TryTriggerAutoLoad(current);
                    EnsureRootVisibleRangeRendered();

                    if (current.Parent?.Tag is CommentTreeNodeState parentState && parentState.Comment.ReplyCount > 0)
                    {
                        EnsureReplySkeleton(current.Parent, parentState, knownTotalCount: parentState.Comment.ReplyCount);
                        _ = EnsureReplyVisibleRangeLoadedAsync(current.Parent, parentState, "KeyDown");
                        EnsureReplyVisibleRangeRendered(current.Parent, parentState);
                    }
                }));
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (selected.Tag is RootPlaceholderTag)
                {
                    e.Handled = true;
                    int targetIndex = selected.Index;
                    int pageSize = GetRootPageSize();
                    int pageNumber = Math.Max(1, (targetIndex / Math.Max(1, pageSize)) + 1);
                    _rootRetryAttempts.Remove(pageNumber);
                    _ = LoadRootPageAsync(pageNumber, "ManualRetry");
                }
                else if (selected.Tag is ReplyPlaceholderTag && selected.Parent?.Tag is CommentTreeNodeState parentState)
                {
                    e.Handled = true;
                    int pageSize = parentState.ReplyPageSize > 0 ? parentState.ReplyPageSize : ReplyPageSize;
                    int pageNumber = Math.Max(1, (selected.Index / Math.Max(1, pageSize)) + 1);
                    parentState.ReplyFailedPageAttempts.Remove(pageNumber);
                    _ = EnsureReplyPagesLoadedAsync(selected.Parent, parentState, pageNumber, "ManualRetry");
                }
                else if (selected.Tag is LoadMoreNodeTag loadMore && selected.Parent != null)
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

            EnsureRootVisibleRangeRendered();
            if (e.Node.Parent?.Tag is CommentTreeNodeState parentState && parentState.Comment.ReplyCount > 0)
            {
                if (!parentState.RepliesSkeletonBuilt)
                {
                    EnsureReplySkeleton(e.Node.Parent, parentState, knownTotalCount: parentState.Comment.ReplyCount);
                }
                EnsureReplyVisibleRangeRendered(e.Node.Parent, parentState);
            }
        }

        private void CommentsTreeView_MouseWheel(object? sender, MouseEventArgs e)
        {
            if (_suppressAutoLoad)
            {
                return;
            }

            BeginInvoke(new Action(() =>
            {
                if (!_suppressAutoLoad)
                {
                    EnsureRootVisibleRangeLoaded("MouseWheel");
                    EnsureRootVisibleRangeRendered();
                    var selected = _commentsTreeView.SelectedNode;
                    if (selected?.Parent?.Tag is CommentTreeNodeState parentState && parentState.Comment.ReplyCount > 0)
                    {
                        EnsureReplySkeleton(selected.Parent, parentState, knownTotalCount: parentState.Comment.ReplyCount);
                        _ = EnsureReplyVisibleRangeLoadedAsync(selected.Parent, parentState, "MouseWheel");
                        EnsureReplyVisibleRangeRendered(selected.Parent, parentState);
                    }
                }
            }));
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
            if (_suppressAutoLoad)
            {
                return;
            }

            var root = node;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            EnsureRootIndexLoaded(root.Index, "AfterSelect");
            EnsureRootVisibleRangeLoaded("AfterSelect");
        }

        private void EnsureRootIndexLoaded(int index, string reason)
        {
            if (index < 0)
            {
                return;
            }

            int loadedCount = _commentsTreeView.Nodes.Count;
            if (loadedCount <= 0)
            {
                return;
            }

            if (index >= loadedCount - AutoLoadThreshold)
            {
                int targetPage = Math.Max(1, _nextPageNumber);
                _ = EnsureRootPagesLoadedAsync(targetPage, reason);
            }
        }

        private void EnsureRootVisibleRangeLoaded(string reason)
        {
            int loadedCount = _commentsTreeView.Nodes.Count;
            if (loadedCount == 0)
            {
                return;
            }

            TreeNode? top = null;
            int visibleCount = 0;
            try
            {
                top = _commentsTreeView.TopNode;
                visibleCount = _commentsTreeView.VisibleCount;
            }
            catch
            {
                top = null;
            }

            if (top == null)
            {
                return;
            }

            TreeNode root = top;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            int startIndex = root.Index;
            int endIndex = startIndex + Math.Max(1, visibleCount) + RootPrefetchPadding;
            if (endIndex >= loadedCount - AutoLoadThreshold)
            {
                int targetPage = Math.Max(1, _nextPageNumber);
                _ = EnsureRootPagesLoadedAsync(targetPage, reason);
            }
        }

        private async Task EnsureRootPagesLoadedRangeAsync(int startPage, int endPage, string reason)
        {
            if (startPage <= 0 || endPage <= 0)
            {
                return;
            }

            if (endPage < startPage)
            {
                return;
            }

            for (int page = startPage; page <= endPage; page++)
            {
                await EnsureRootPagesLoadedAsync(page, reason).ConfigureAwait(true);
            }
        }

        private void EnsureRootVisibleRangeRendered()
        {
            if (!TryGetRootRenderRange(out int startIndex, out int endIndex))
            {
                return;
            }

            if (startIndex == _rootRenderStart && endIndex == _rootRenderEnd)
            {
                return;
            }

            bool updated = ApplyRootSlotsInRange(startIndex, endIndex);
            if (updated)
            {
                ScheduleAccessibilityReorder();
            }

            _rootRenderStart = startIndex;
            _rootRenderEnd = endIndex;
        }

        private bool TryGetRootRenderRange(out int startIndex, out int endIndex)
        {
            startIndex = 0;
            endIndex = -1;

            if (_rootTotalCount <= 0 || _commentsTreeView.Nodes.Count == 0)
            {
                return false;
            }

            TreeNode? top = null;
            int visibleCount = 0;
            try
            {
                top = _commentsTreeView.TopNode;
                visibleCount = _commentsTreeView.VisibleCount;
            }
            catch
            {
                top = null;
            }

            if (top == null)
            {
                return false;
            }

            TreeNode root = top;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            int pageSize = GetRootPageSize();
            int visibleStart = Math.Max(0, root.Index);
            int visibleEnd = Math.Min(_rootTotalCount - 1, visibleStart + Math.Max(1, visibleCount) - 1);

            startIndex = Math.Max(0, visibleStart - pageSize);
            endIndex = Math.Min(_rootTotalCount - 1, visibleEnd + pageSize);

            var selected = _commentsTreeView.SelectedNode;
            if (selected != null)
            {
                while (selected.Parent != null)
                {
                    selected = selected.Parent;
                }

                int selectedIndex = selected.Index;
                if (selectedIndex >= 0 && selectedIndex < _rootTotalCount)
                {
                    startIndex = Math.Min(startIndex, selectedIndex);
                    endIndex = Math.Max(endIndex, selectedIndex);
                }
            }

            return endIndex >= startIndex;
        }

        private bool ApplyRootSlotsInRange(int startIndex, int endIndex)
        {
            if (_rootTotalCount <= 0)
            {
                return false;
            }

            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(_rootTotalCount - 1, endIndex);
            if (endIndex < startIndex)
            {
                return false;
            }

            bool updated = false;
            _commentsTreeView.BeginUpdate();
            try
            {
                for (int i = startIndex; i <= endIndex && i < _commentsTreeView.Nodes.Count; i++)
                {
                    var info = i >= 0 && i < _rootCommentSlots.Count ? _rootCommentSlots[i] : null;
                    if (info == null)
                    {
                        continue;
                    }

                    var node = _commentsTreeView.Nodes[i];
                    if (node == null)
                    {
                        continue;
                    }

                    if (ApplyRootNodeFromSlot(node, info, i))
                    {
                        updated = true;
                    }
                }
            }
            finally
            {
                _commentsTreeView.EndUpdate();
            }

            return updated;
        }

        private void TryTriggerReplyAutoLoad(TreeNode node)
        {
            var parent = node.Parent;
            if (parent?.Tag is not CommentTreeNodeState parentState)
            {
                return;
            }

            if (_suppressAutoLoad)
            {
                return;
            }

            if (parentState.Comment.ReplyCount <= 0)
            {
                return;
            }

            EnsureReplySkeleton(parent, parentState, knownTotalCount: parentState.Comment.ReplyCount);
            _ = EnsureReplyVisibleRangeLoadedAsync(parent, parentState, "AfterSelect");
            EnsureReplyVisibleRangeRendered(parent, parentState);
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

        private sealed class CommentsTreeView : TreeView
        {
            public void NotifyAccessibilityReorder()
            {
                if (!IsHandleCreated)
                {
                    return;
                }

                try
                {
                    AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
                }
                catch
                {
                }
            }

            protected override AccessibleObject CreateAccessibilityInstance()
            {
                return new TreeViewAccessibilityProxy(this);
            }

            private sealed class TreeViewAccessibilityProxy : Control.ControlAccessibleObject
            {
                private static readonly Type? TreeViewAccessibleType = typeof(TreeView).GetNestedType("TreeViewAccessibleObject",
                    System.Reflection.BindingFlags.NonPublic);
                private static readonly System.Reflection.ConstructorInfo? TreeViewAccessibleCtor = TreeViewAccessibleType?.GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(TreeView) },
                    null);
                private static readonly Type? TreeNodeAccessibleType = typeof(TreeNode).GetNestedType("TreeNodeAccessibleObject",
                    System.Reflection.BindingFlags.NonPublic);
                private static readonly System.Reflection.ConstructorInfo? TreeNodeAccessibleCtor = TreeNodeAccessibleType?.GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(TreeNode), typeof(TreeView) },
                    null);
                private static readonly System.Reflection.FieldInfo? TreeNodeField = TreeNodeAccessibleType?.GetField("_owningTreeNode",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                private static readonly System.Reflection.FieldInfo? TreeViewField = TreeNodeAccessibleType?.GetField("_owningTreeView",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                private readonly CommentsTreeView _owner;
                private readonly AccessibleObject _inner;

                public TreeViewAccessibilityProxy(CommentsTreeView owner)
                    : base(owner)
                {
                    _owner = owner;
                    _inner = CreateInner(owner);
                }

                public override string? Name => _inner.Name;

                public override string? Value => _inner.Value;

                public override AccessibleRole Role => _inner.Role;

                public override AccessibleStates State => _inner.State;

                public override string? DefaultAction => _inner.DefaultAction;

                public override Rectangle Bounds => _inner.Bounds;

                public override AccessibleObject? Parent => base.Parent ?? _inner.Parent;

                public override void DoDefaultAction()
                {
                    _inner.DoDefaultAction();
                }

                public override int GetChildCount()
                {
                    try
                    {
                        return _owner.Nodes.Count;
                    }
                    catch
                    {
                        return 0;
                    }
                }

                public override AccessibleObject? GetChild(int index)
                {
                    if (index < 0)
                    {
                        return null;
                    }

                    if (index < _owner.Nodes.Count)
                    {
                        var node = _owner.Nodes[index];
                        var wrapped = WrapNode(node, _owner);
                        if (wrapped != null)
                        {
                            return wrapped;
                        }
                    }

                    AccessibleObject? child = null;
                    try
                    {
                        child = _inner.GetChild(index);
                    }
                    catch
                    {
                        child = null;
                    }

                    if (child == null)
                    {
                        return null;
                    }

                    return new TreeNodeAccessibilityProxy(this, _owner, child, null, null);
                }

                public override AccessibleObject? Navigate(AccessibleNavigation navdir)
                {
                    if (navdir == AccessibleNavigation.FirstChild)
                    {
                        return GetChild(0);
                    }

                    if (navdir == AccessibleNavigation.LastChild)
                    {
                        int last = _owner.Nodes.Count - 1;
                        return last >= 0 ? GetChild(last) : null;
                    }

                    return _inner.Navigate(navdir);
                }

                public override AccessibleObject? HitTest(int x, int y)
                {
                    return _inner.HitTest(x, y);
                }

                private static AccessibleObject CreateInner(TreeView owner)
                {
                    if (TreeViewAccessibleCtor != null)
                    {
                        try
                        {
                            return (AccessibleObject)TreeViewAccessibleCtor.Invoke(new object[] { owner });
                        }
                        catch
                        {
                        }
                    }

                    return new Control.ControlAccessibleObject(owner);
                }

                private static AccessibleObject? CreateTreeNodeAccessibleObject(TreeNode node, TreeView owner)
                {
                    if (TreeNodeAccessibleCtor != null)
                    {
                        try
                        {
                            return (AccessibleObject)TreeNodeAccessibleCtor.Invoke(new object[] { node, owner });
                        }
                        catch
                        {
                        }
                    }

                    return null;
                }

                internal AccessibleObject? WrapNode(TreeNode node, TreeView treeView)
                {
                    if (node == null)
                    {
                        return null;
                    }

                    var child = CreateTreeNodeAccessibleObject(node, treeView);
                    if (child == null)
                    {
                        return null;
                    }

                    return new TreeNodeAccessibilityProxy(this, _owner, child, node, treeView);
                }

                private sealed class TreeNodeAccessibilityProxy : AccessibleObject
                {
                    private readonly TreeViewAccessibilityProxy _rootProxy;
                    private readonly CommentsTreeView _owner;
                    private readonly AccessibleObject _inner;
                    private TreeNode? _cachedNode;
                    private TreeView? _cachedTreeView;

                    public TreeNodeAccessibilityProxy(TreeViewAccessibilityProxy rootProxy, CommentsTreeView owner, AccessibleObject inner, TreeNode? node, TreeView? treeView)
                    {
                        _rootProxy = rootProxy;
                        _owner = owner;
                        _inner = inner;
                        if (node != null)
                        {
                            _cachedNode = node;
                            _cachedTreeView = treeView ?? node.TreeView ?? owner;
                        }
                    }

                    public override string? Name
                    {
                        get
                        {
                            if (TryResolveNode(out var node, out _))
                            {
                                string text = GetAccessibleNodeText(node);
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    return text;
                                }
                            }

                            return _inner.Name;
                        }
                    }

                    public override string? Value
                    {
                        get
                        {
                            if (TryResolveNode(out var node, out _))
                            {
                                string text = GetAccessibleNodeText(node);
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    return text;
                                }
                            }

                            return _inner.Value;
                        }
                    }

                    public override AccessibleRole Role => _inner.Role;

                    public override AccessibleStates State => _inner.State;

                    public override string? DefaultAction => _inner.DefaultAction;

                    public override AccessibleObject? Parent
                    {
                        get
                        {
                            if (TryResolveNode(out var node, out var treeView))
                            {
                                if (node.Parent == null)
                                {
                                    return _rootProxy;
                                }

                                var wrappedParent = _rootProxy.WrapNode(node.Parent, treeView);
                                if (wrappedParent != null)
                                {
                                    return wrappedParent;
                                }
                            }

                            return _inner.Parent ?? _rootProxy;
                        }
                    }

                    public override void DoDefaultAction()
                    {
                        _inner.DoDefaultAction();
                    }

                    public override int GetChildCount()
                    {
                        if (TryResolveNode(out var node, out _))
                        {
                            return node.Nodes.Count;
                        }

                        try
                        {
                            return _inner.GetChildCount();
                        }
                        catch
                        {
                            return 0;
                        }
                    }

                    public override AccessibleObject? GetChild(int index)
                    {
                        if (index < 0)
                        {
                            return null;
                        }

                        if (TryResolveNode(out var node, out var treeView))
                        {
                            if (index >= node.Nodes.Count)
                            {
                                return null;
                            }

                            var wrapped = _rootProxy.WrapNode(node.Nodes[index], treeView);
                            if (wrapped != null)
                            {
                                return wrapped;
                            }
                        }

                        try
                        {
                            var child = _inner.GetChild(index);
                            return child == null ? null : new TreeNodeAccessibilityProxy(_rootProxy, _owner, child, null, null);
                        }
                        catch
                        {
                            return null;
                        }
                    }

                    public override AccessibleObject? Navigate(AccessibleNavigation navdir)
                    {
                        if (TryResolveNode(out var node, out var treeView))
                        {
                            switch (navdir)
                            {
                                case AccessibleNavigation.FirstChild:
                                    {
                                        var wrapped = node.FirstNode == null ? null : WrapNode(node.FirstNode, treeView);
                                        if (wrapped != null)
                                        {
                                            return wrapped;
                                        }
                                        break;
                                    }
                                case AccessibleNavigation.LastChild:
                                    {
                                        var wrapped = node.LastNode == null ? null : WrapNode(node.LastNode, treeView);
                                        if (wrapped != null)
                                        {
                                            return wrapped;
                                        }
                                        break;
                                    }
                                case AccessibleNavigation.Next:
                                    {
                                        var wrapped = node.NextNode == null ? null : WrapNode(node.NextNode, treeView);
                                        if (wrapped != null)
                                        {
                                            return wrapped;
                                        }
                                        break;
                                    }
                                case AccessibleNavigation.Previous:
                                    {
                                        var wrapped = node.PrevNode == null ? null : WrapNode(node.PrevNode, treeView);
                                        if (wrapped != null)
                                        {
                                            return wrapped;
                                        }
                                        break;
                                    }
                            }
                        }

                        return _inner.Navigate(navdir);
                    }

                    private AccessibleObject? WrapNode(TreeNode node, TreeView treeView)
                    {
                        return _rootProxy.WrapNode(node, treeView);
                    }

                    public override AccessibleObject? HitTest(int x, int y)
                    {
                        return _inner.HitTest(x, y);
                    }

                    public override Rectangle Bounds
                    {
                        get
                        {
                            Rectangle bounds = _inner.Bounds;
                            if (!ShouldOverrideBounds(bounds, _inner.State))
                            {
                                return bounds;
                            }

                            if (!TryResolveNode(out var node, out var treeView))
                            {
                                return bounds;
                            }

                            var resolvedTreeView = treeView as CommentsTreeView ?? _owner;
                            var virtualBounds = ComputeVirtualBounds(resolvedTreeView, node);
                            return virtualBounds.IsEmpty ? bounds : virtualBounds;
                        }
                    }

                    private bool TryResolveNode(out TreeNode node, out TreeView treeView)
                    {
                        if (_cachedNode != null && _cachedTreeView != null)
                        {
                            node = _cachedNode;
                            treeView = _cachedTreeView;
                            return true;
                        }

                        if (_cachedNode != null)
                        {
                            node = _cachedNode;
                            treeView = _cachedTreeView ?? _cachedNode.TreeView ?? _owner;
                            _cachedTreeView = treeView;
                            return true;
                        }

                        node = null!;
                        treeView = _owner;

                        if (TreeNodeField == null)
                        {
                            return false;
                        }

                        try
                        {
                            if (TreeNodeField.GetValue(_inner) is TreeNode owningNode)
                            {
                                node = owningNode;
                                _cachedNode = owningNode;
                                if (TreeViewField != null && TreeViewField.GetValue(_inner) is TreeView owningView)
                                {
                                    treeView = owningView;
                                    _cachedTreeView = owningView;
                                }
                                else
                                {
                                    treeView = owningNode.TreeView ?? _owner;
                                    _cachedTreeView = treeView;
                                }

                                return true;
                            }
                        }
                        catch
                        {
                        }

                        return false;
                    }

                    private static string GetAccessibleNodeText(TreeNode node)
                    {
                        if (node == null)
                        {
                            return string.Empty;
                        }

                        string text = node.Text ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }

                        if (node.Tag is CommentTreeNodeState state)
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

                            if (node.Level > 0)
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
                            builder.Append(")");

                            if (node.Level == 0 && info.ReplyCount > 0)
                            {
                                builder.Append(" · ");
                                builder.Append(info.ReplyCount);
                                builder.Append(" 回复");
                            }

                            return builder.ToString();
                        }

                        return string.Empty;
                    }

                    private static bool ShouldOverrideBounds(Rectangle bounds, AccessibleStates state)
                    {
                        if (bounds.Width <= 0 || bounds.Height <= 0)
                        {
                            return true;
                        }

                        if ((state & (AccessibleStates.Invisible | AccessibleStates.Offscreen)) != 0)
                        {
                            if (bounds.Y == 0 && bounds.X == 0)
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                }

                private static Rectangle ComputeVirtualBounds(CommentsTreeView view, TreeNode node)
                {
                    if (!view.IsHandleCreated)
                    {
                        return Rectangle.Empty;
                    }

                    int itemHeight = view.ItemHeight > 0 ? view.ItemHeight : 18;
                    int delta = GetVisibleDeltaFromTop(view, node);
                    if (delta == int.MinValue)
                    {
                        return Rectangle.Empty;
                    }

                    Point origin;
                    try
                    {
                        origin = view.PointToScreen(Point.Empty);
                    }
                    catch
                    {
                        origin = Point.Empty;
                    }

                    int indent = node.Level * view.Indent;
                    int x = origin.X + indent;
                    int y = origin.Y + (delta * itemHeight);
                    int width = Math.Max(1, view.ClientSize.Width - indent);
                    return new Rectangle(x, y, width, itemHeight);
                }

                private static int GetVisibleDeltaFromTop(CommentsTreeView view, TreeNode target)
                {
                    TreeNode? top = view.TopNode ?? (view.Nodes.Count > 0 ? view.Nodes[0] : null);
                    if (top == null)
                    {
                        return int.MinValue;
                    }

                    if (ReferenceEquals(top, target))
                    {
                        return 0;
                    }

                    int max = view.GetNodeCount(true) + 2;

                    int delta = 0;
                    TreeNode? current = top;
                    while (current != null && delta <= max)
                    {
                        if (ReferenceEquals(current, target))
                        {
                            return delta;
                        }

                        current = current.NextVisibleNode;
                        delta++;
                    }

                    delta = 0;
                    current = top;
                    while (current != null && -delta <= max)
                    {
                        if (ReferenceEquals(current, target))
                        {
                            return delta;
                        }

                        current = current.PrevVisibleNode;
                        delta--;
                    }

                    return int.MinValue;
                }
            }
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

            IEnumerable<TreeNode> targets = nodes ?? parent.Nodes.Cast<TreeNode>();
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
            string newText = BuildNodeText(state.Comment, isRoot, rootIndex);   
            node.Text = newText;
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

            if (_rootIndexById.TryGetValue(commentId, out int index))
            {
                if (index >= 0 && index < _commentsTreeView.Nodes.Count)
                {
                    return _commentsTreeView.Nodes[index];
                }
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
            if (node == null || state == null)
            {
                return;
            }

            _ = append;

            EnsureReplySkeleton(node, state, knownTotalCount: state.Comment.ReplyCount);
            int pageSize = state.ReplyPageSize > 0 ? state.ReplyPageSize : ReplyPageSize;
            int targetPage = Math.Max(1, state.ReplyNextPageNumber);
            int startIndex = Math.Max(0, (targetPage - 1) * pageSize);

            await EnsureReplyPagesLoadedAsync(node, state, targetPage, "ManualLoad").ConfigureAwait(true);

            if (focusNewReplies && startIndex < node.Nodes.Count)
            {
                var selectionAfterLoad = node.Nodes[startIndex];
                SelectNodeWithMode(selectionAfterLoad, announceSelection: true, ensureFocus: true);
            }
        }

        private async Task ScheduleReplyAutoRetryAsync(TreeNode parent, CommentTreeNodeState state, int pageNumber)
        {
            if (state.ReplyRetryScheduled || IsDisposed)
            {
                return;
            }

            state.ReplyRetryScheduled = true;
            try
            {
                await Task.Delay(ReplyEmptyRetryDelayMs * 3, _lifecycleCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                state.ReplyRetryScheduled = false;
            }

            if (IsDisposed || _commentsTreeView.IsDisposed)
            {
                return;
            }

            int target = Math.Max(pageNumber, state.ReplyPendingTargetPage);
            if (target <= 0)
            {
                return;
            }

            await EnsureReplyPagesLoadedAsync(parent, state, target, "AutoRetry").ConfigureAwait(true);
        }

        private async Task ScheduleReplyPartialRetryAsync(TreeNode parent, CommentTreeNodeState state, int pageNumber, int expectedCount, int actualCount, string reason)
        {
            if (parent == null || state == null || pageNumber <= 0)
            {
                return;
            }

            if (expectedCount <= 0 || actualCount >= expectedCount)
            {
                return;
            }

            if (state.ReplyRetryScheduled || IsDisposed)
            {
                return;
            }

            state.ReplyRetryScheduled = true;
            int attempts = state.ReplyFailedPageAttempts.TryGetValue(pageNumber, out int current) ? current : 0;
            attempts++;
            state.ReplyFailedPageAttempts[pageNumber] = attempts;

            DebugLogger.Log(DebugLogger.LogLevel.Warning, "CommentsDialog",
                $"回复页内容不足: parent={state.Comment.CommentId}, page={pageNumber}, expected={expectedCount}, actual={actualCount}, attempt={attempts}, reason={reason}");

            if (attempts > ReplyPageMaxRetryAttempts)
            {
                state.ReplyRetryScheduled = false;
                MarkReplyPageFailed(parent, state, pageNumber);
                return;
            }

            ResetReplyPageNodesToLoading(parent, state, pageNumber);
            int delayMs = ReplyPageRetryDelayMs * attempts;
            try
            {
                await Task.Delay(delayMs, _lifecycleCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                state.ReplyRetryScheduled = false;
            }

            if (IsDisposed || _commentsTreeView.IsDisposed)
            {
                return;
            }

            if (state.ReplyLoadedPages.Contains(pageNumber))
            {
                return;
            }

            await EnsureReplyPagesLoadedAsync(parent, state, pageNumber, "PartialRetry").ConfigureAwait(true);
        }

        private TreeNode CreateLoadMoreNode(TreeNode parent, CommentTreeNodeState state)
        {
            var node = CreateNode(parent, "点击加载更多回复…");
            node.Tag = new LoadMoreNodeTag(state);
            node.ForeColor = SystemColors.HotTrack;
            return node;
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

        private TreeNode[] BuildNodes(IEnumerable<CommentInfo> infos, TreeNode? parent, bool isReply, int startIndex = 0)
        {
            if (infos == null)
            {
                return Array.Empty<TreeNode>();
            }

            if (infos is not IList<CommentInfo> list)
            {
                list = infos.ToList();
            }

            if (list.Count == 0)
            {
                return Array.Empty<TreeNode>();
            }

            var nodes = new TreeNode[list.Count];

            if (isReply)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    nodes[i] = CreateCommentNode(list[i], parent, true);
                }

                return nodes;
            }

            int index = startIndex;
            for (int i = 0; i < list.Count; i++)
            {
                nodes[i] = CreateCommentNode(list[i], parent, false, index++);
            }

            return nodes;
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
                if (_commentsTreeView.IsDisposed || !_commentsTreeView.IsHandleCreated)
                {
                    return;
                }

                string text = node.Text;
                if (string.IsNullOrWhiteSpace(text) && node.Tag is CommentTreeNodeState state)
                {
                    text = BuildCommentDescriptor(node, state, limitSummaryLength: false, includeSeconds: false);
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                _commentsTreeView.AccessibilityObject?.RaiseAutomationNotification(
                    AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.ImportantMostRecent,
                    text);
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
                    await RefreshCommentsAsync(resetPage: true, append: false);
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
                ReplyTotalCount = Math.Max(0, comment.ReplyCount);
                ReplyPageSize = CommentsDialog.ReplyPageSize;
                ReplyNextPageNumber = 1;
            }

            public CommentInfo Comment { get; }
            public bool RepliesLoaded { get; set; }
            public bool HasMoreReplies { get; set; }
            public int ReplyTotalCount { get; set; }
            public int ReplyPageSize { get; set; }
            public List<CommentInfo?> ReplySlots { get; } = new List<CommentInfo?>();
            public HashSet<int> ReplyLoadedPages { get; } = new HashSet<int>();
            public HashSet<int> ReplyLoadingPages { get; } = new HashSet<int>();
            public bool RepliesSkeletonBuilt { get; set; }
            public int ReplyNextPageNumber { get; set; }
            public int ReplyPendingTargetPage { get; set; }
            public bool ReplyRetryScheduled { get; set; }
            public Dictionary<int, int> ReplyFailedPageAttempts { get; } = new Dictionary<int, int>();
            public int ReplyRenderStart { get; set; } = -1;
            public int ReplyRenderEnd { get; set; } = -1;
        }

        private sealed class LoadMoreNodeTag
        {
            public LoadMoreNodeTag(CommentTreeNodeState parentState)
            {
                ParentState = parentState;
            }

            public CommentTreeNodeState ParentState { get; }
        }

        private sealed class ReplyPlaceholderTag
        {
            public ReplyPlaceholderTag(int index)
            {
                Index = index;
            }

            public int Index { get; }
        }

        private sealed class RootPlaceholderTag
        {
            public RootPlaceholderTag(int index)
            {
                Index = index;
            }

            public int Index { get; }
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

