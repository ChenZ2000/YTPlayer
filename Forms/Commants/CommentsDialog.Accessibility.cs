using System;
using System.Drawing;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Forms;
using System.Windows.Forms.Automation;
using YTPlayer.Core;

namespace YTPlayer.Forms
{
    internal sealed partial class CommentsDialog
    {
        private Label? _accessibilityAnnouncementLabel;
        private string _lastAnnouncementText = string.Empty;
        private DateTime _lastAnnouncementAt = DateTime.MinValue;
        private bool _suppressLevelAnnouncement;
        private bool _treeIntroAnnounced;
        private Timer? _levelAnnouncementTimer;
        private int? _pendingLevelAnnouncement;
        private int? _lastSelectedLevel;
        private bool _levelAnnouncedBeforeSelect;
        private DateTime _lastNarratorCheckAt = DateTime.MinValue;
        private bool _isNarratorRunningCached;
        private const int NarratorCheckIntervalMs = 1000;
        private const int LevelAnnouncementDelayMs = 140;

        private void InitializeAccessibilityAnnouncements()
        {
            EnsureAccessibilityAnnouncementLabel();
        }

        private void AnnounceTreeLevelChange(int level)
        {
            string text = $"{level} 级";
            RaiseAccessibilityAnnouncement(text,
                AutomationNotificationProcessing.All,
                notifyMsaa: false,
                updateLabelText: false);
        }

        private void AnnounceSortChange(CommentSortType sortType)
        {
            string name = GetSortDisplayName(sortType);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            RaiseAccessibilityAnnouncement($"按{name}排序");
        }

        private void AnnounceTreeIntroIfNeeded()
        {
            if (_treeIntroAnnounced || IsDisposed)
            {
                return;
            }

            if (!_commentTree.ContainsFocus)
            {
                return;
            }

            var selected = _commentTree.SelectedNode;
            if (selected != null)
            {
                var tag = selected.Tag as CommentNodeTag;
                if (tag != null && tag.Comment != null && !tag.IsPlaceholder)
                {
                    return;
                }
            }

            _treeIntroAnnounced = true;
            RaiseAccessibilityAnnouncement(
                "使用方向键浏览评论，右箭头展开楼层回复。",
                AutomationNotificationProcessing.All,
                notifyMsaa: false,
                updateLabelText: false);
        }

        private static string GetSortDisplayName(CommentSortType sortType)
        {
            return sortType switch
            {
                CommentSortType.Recommend => "推荐",
                CommentSortType.Hot => "热度",
                CommentSortType.Time => "时间",
                _ => string.Empty
            };
        }
        private void RaiseAccessibilityAnnouncement(
            string text,
            AutomationNotificationProcessing processing = AutomationNotificationProcessing.ImportantMostRecent,
            bool notifyMsaa = true,
            bool updateLabelText = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EnsureAccessibilityAnnouncementLabel();
            if (_accessibilityAnnouncementLabel == null || _accessibilityAnnouncementLabel.IsDisposed)
            {
                return;
            }

            string trimmed = text.Trim();
            DateTime now = DateTime.UtcNow;
            if (string.Equals(_lastAnnouncementText, trimmed, StringComparison.Ordinal)
                && now - _lastAnnouncementAt < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            _lastAnnouncementText = trimmed;
            _lastAnnouncementAt = now;

            if (updateLabelText)
            {
                _accessibilityAnnouncementLabel.Text = trimmed;
                _accessibilityAnnouncementLabel.AccessibleName = trimmed;
                _accessibilityAnnouncementLabel.AccessibleDescription = trimmed;
            }
            try
            {
                _accessibilityAnnouncementLabel.AccessibilityObject.RaiseAutomationNotification(
                    AutomationNotificationKind.Other,
                    processing,
                    trimmed);
            }
            catch
            {
            }

            if (notifyMsaa)
            {
                try
                {
                    NotifyAccessibilityClients(_accessibilityAnnouncementLabel, AccessibleEvents.NameChange, -1);
                    NotifyAccessibilityClients(_accessibilityAnnouncementLabel, AccessibleEvents.ValueChange, -1);
                }
                catch
                {
                }
            }
        }

        private void AnnounceSelectedNodeForNarrator(TreeNode node)
        {
            if (node == null || _commentTree == null || !_commentTree.ContainsFocus)
            {
                return;
            }

            if (!IsNarratorRunningCached())
            {
                return;
            }
            UpdateNarratorTreeAccessibilityMode();

            if (_commentTree.IsHandleCreated)
            {
                _commentTree.ResetAccessibilityChildCache("narrator_select");
                _commentTree.NotifyAccessibilityItemNameChange(node);
            }
        }

        private void UpdateNarratorTreeAccessibilityMode()
        {
            if (_commentTree == null)
            {
                return;
            }

            bool preferIndex = IsNarratorRunningCached();
            if (_commentTree.PreferVisibleIndexMapping != preferIndex)
            {
                _commentTree.PreferVisibleIndexMapping = preferIndex;
                _commentTree.ResetAccessibilityChildCache("narrator_mode");
            }
        }

        private void EnsureAccessibilityAnnouncementLabel()
        {
            if (_accessibilityAnnouncementLabel != null || IsDisposed)
            {
                return;
            }

            _accessibilityAnnouncementLabel = new Label
            {
                Name = "commentsAccessibilityAnnouncementLabel",
                TabStop = false,
                AutoSize = false,
                Size = new Size(1, 1),
                Location = new Point(-2000, -2000),
                Text = string.Empty,
                AccessibleName = string.Empty,
                AccessibleRole = AccessibleRole.StaticText
            };

            Controls.Add(_accessibilityAnnouncementLabel);
        }

        private void ScheduleLevelAnnouncement(int level)
        {
            if (IsDisposed || _suppressLevelAnnouncement)
            {
                return;
            }

            EnsureLevelAnnouncementTimer();
            _pendingLevelAnnouncement = level;
            _levelAnnouncementTimer!.Stop();
            _levelAnnouncementTimer.Start();
        }

        private void CancelLevelAnnouncement()
        {
            if (_levelAnnouncementTimer == null)
            {
                _pendingLevelAnnouncement = null;
                return;
            }

            _levelAnnouncementTimer.Stop();
            _pendingLevelAnnouncement = null;
        }

        private void EnsureLevelAnnouncementTimer()
        {
            if (_levelAnnouncementTimer != null)
            {
                return;
            }

            _levelAnnouncementTimer = new Timer
            {
                Interval = LevelAnnouncementDelayMs
            };
            _levelAnnouncementTimer.Tick += (_, _) =>
            {
                _levelAnnouncementTimer.Stop();
                if (IsDisposed)
                {
                    return;
                }

                int? level = _pendingLevelAnnouncement;
                _pendingLevelAnnouncement = null;
                if (!level.HasValue)
                {
                    return;
                }

                if (!_commentTree.ContainsFocus)
                {
                    return;
                }

                var selected = _commentTree.SelectedNode;
                if (selected == null || selected.Level != level.Value)
                {
                    return;
                }

                AnnounceTreeLevelChange(level.Value);
            };
        }

        private void DisposeLevelAnnouncementTimer()
        {
            if (_levelAnnouncementTimer == null)
            {
                return;
            }

            _levelAnnouncementTimer.Stop();
            _levelAnnouncementTimer.Dispose();
            _levelAnnouncementTimer = null;
            _pendingLevelAnnouncement = null;
        }

        private void NotifyAccessibilityClients(Control control, AccessibleEvents accEvent, int childId)
        {
            if (control == null)
            {
                return;
            }

            try
            {
                MethodInfo? method = typeof(Control).GetMethod(
                    "AccessibilityNotifyClients",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(AccessibleEvents), typeof(int) },
                    null);
                method?.Invoke(control, new object[] { accEvent, childId });
            }
            catch
            {
            }
        }

        private bool IsNarratorRunningCached()
        {
            DateTime utcNow = DateTime.UtcNow;
            if ((utcNow - _lastNarratorCheckAt).TotalMilliseconds >= NarratorCheckIntervalMs)
            {
                _isNarratorRunningCached = IsNarratorRunning();
                _lastNarratorCheckAt = utcNow;
            }
            return _isNarratorRunningCached;
        }

        private static bool IsNarratorRunning()
        {
            try
            {
                return Process.GetProcessesByName("Narrator").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
