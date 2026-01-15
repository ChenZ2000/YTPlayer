using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly CommentTarget _target;
        private readonly string? _currentUserId;
        private readonly bool _isLoggedIn;

        private CommentSortType _sortType = CommentSortType.Recommend;
        private bool _hideSequenceNumbers;
        private CommentReplyTarget? _replyTarget;
        private long _loadVersion;
        private CancellationTokenSource? _cts;
        private bool _isResettingTree;
        private TreeNavDirection _lastTreeNavDirection;
        private int _minTopPageLoaded = 1;
        private int _maxTopPageLoaded;
        private bool _pendingSortRefresh;
        private bool _pendingSortRefreshOnTreeFocus;
        private int _treeInteractionVersion;
        private string? _pendingFloorSelectionParentId;
        private int _pendingFloorSelectionPage;
        private string? _pendingFocusCommentId;
        private string? _pendingFocusParentId;
        private bool _pendingFocusTopAfterRefresh;
        private bool _isFirstLoad = true;
        private readonly HashSet<string> _pendingAutoExpandParents = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _floorLoadMoreAtTop = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, TreeNode> _nodeById = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _topCommentIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _floorParentByCommentId = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _topPagesLoaded = new();
        private readonly Dictionary<string, HashSet<int>> _floorPagesLoaded = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _loadingTopPages = new();
        private readonly HashSet<string> _loadingFloors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CommentInfo>> _pendingFloorByParent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AutoRetryState> _floorAutoRetryStates = new(StringComparer.OrdinalIgnoreCase);

        private const int TopPageSize = 100;
        private const int FloorPageSize = 20;
        private const int FloorAutoRetryMaxAttempts = 3;
        private bool _hasMoreTop;
        private int _nextTopPage = 1;

        private enum TreeNavDirection
        {
            None,
            Up,
            Down
        }

        private struct AutoRetryState
        {
            public int Attempts;
        }

        private void InitializeState()
        {
            var config = ConfigManager.Instance.Load();
            _hideSequenceNumbers = config?.CommentSequenceNumberHidden ?? false;
            _sortType = CommentSortType.Recommend;
        }

        [Conditional("DEBUG")]
        private void LogComments(string message)
        {
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Comments", message);
        }

        private void SetTreeNavDirection(TreeNavDirection direction, string reason)
        {
            if (_lastTreeNavDirection == direction)
            {
                return;
            }

            _lastTreeNavDirection = direction;
        }

        [Conditional("DEBUG")]
        private void LogNodeSnapshot(string prefix, TreeNode? node)
        {
            if (node == null)
            {
                LogComments($"{prefix} node=null");
                return;
            }

            var tag = node.Tag as CommentNodeTag;
            string id = tag?.CommentId ?? "null";
            string parentId = tag?.ParentCommentId ?? "null";
            string flags = $"top={tag?.IsTopLevel ?? false},placeholder={tag?.IsPlaceholder ?? false},loadMore={tag?.IsLoadMoreNode ?? false}";
            string handle = node.Handle == IntPtr.Zero ? "0" : $"0x{node.Handle.ToInt64():X}";
            LogComments($"{prefix} level={node.Level} id={id} parent={parentId} handle={handle} {flags} textLen={(node.Text?.Length ?? 0)}");
        }

    }
}
