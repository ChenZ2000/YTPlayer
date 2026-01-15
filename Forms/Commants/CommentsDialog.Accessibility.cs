using System;
using System.Drawing;
using System.Reflection;
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
        private Timer? _levelAnnouncementTimer;
        private int? _pendingLevelAnnouncement;
        private const int LevelAnnouncementDelayMs = 140;

        private void InitializeAccessibilityAnnouncements()
        {
            EnsureAccessibilityAnnouncementLabel();
        }

        private void AnnounceTreeLevelChange(int level)
        {
            string text = $"{level} 级";
            LogComments($"AnnounceLevel level={level} focus={_commentTree.ContainsFocus}");
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
            LogComments($"AccAnnounce '{trimmed}' processing={processing} msaa={notifyMsaa} updateLabel={updateLabelText}");
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
    }
}
