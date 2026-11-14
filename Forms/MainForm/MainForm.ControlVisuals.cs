using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer
{
    public partial class MainForm
    {
        private VolumeSaveScheduler? _volumeSaveScheduler;
        private Panel? _playbackLayoutHost;
        private ToolTip? _controlTextToolTip;
        private bool _layingOutPlayerControls;
        private bool _dpiLayoutPending;
        private SongInfo? _progressGestureSong;
        private long _progressGestureRequestVersion;
        private long _progressGestureEpoch;
        private long _activeProgressGestureEpoch;
        private double _progressGestureDuration;
        private double _progressLastRequestedTarget = double.NaN;

        private void InitializeControlVisuals()
        {
            _volumeSaveScheduler = new VolumeSaveScheduler(() => SaveConfig());
            _controlTextToolTip = new ToolTip { AutoPopDelay = 20000, ShowAlways = true };
            progressTrackBar.PreviewText = (position, delta) => FormatTimeFromSeconds(position) +
                (delta.HasValue ? $"  ({(delta.Value >= 0 ? "+" : "−")}{FormatTimeFromSeconds(Math.Abs(delta.Value))})" : string.Empty);
            volumeTrackBar.PreviewText = (position, _) => $"{Math.Round(position)}%";
            progressTrackBar.GestureStarted += ProgressGestureStarted;
            progressTrackBar.GesturePreview += ProgressGesturePreview;
            progressTrackBar.GestureEnded += ProgressGestureEnded;
            volumeTrackBar.GestureStarted += (_, _) => _volumeSaveScheduler.BeginGesture();
            volumeTrackBar.GestureEnded += (_, _) => _volumeSaveScheduler.EndGesture();
            Deactivate += MainForm_Deactivate;
            controlPanel.VisibleChanged += (_, _) =>
            {
                if (!controlPanel.Visible) { progressTrackBar.EndGesture(true); volumeTrackBar.EndGesture(true); }
                LayoutPlayerControls();
            };

            // GroupBox itself cannot scroll. Keep it and its accessible grouping, and
            // move the same controls into one non-focusable, locally scrollable viewport.
            _playbackLayoutHost = new Panel { Name = "playbackViewport", AutoScroll = true, TabStop = false };
            Control[] playbackControls = controlPanel.Controls.Cast<Control>().ToArray();
            controlPanel.Controls.Clear();
            controlPanel.Padding = Padding.Empty;
            controlPanel.Controls.Add(_playbackLayoutHost);
            _playbackLayoutHost.Controls.AddRange(playbackControls);
            foreach (Control control in playbackControls) control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            foreach (Control control in searchPanel.Controls) control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            searchPanel.AutoScroll = true;
            searchPanel.Padding = Padding.Empty;
            currentSongLabel.AutoEllipsis = true;
            lyricsLabel.AutoEllipsis = true;
            currentSongLabel.TextChanged += (_, _) => UpdateFullControlText(currentSongLabel);
            lyricsLabel.TextChanged += (_, _) => UpdateFullControlText(lyricsLabel);
            UpdateFullControlText(currentSongLabel);
            UpdateFullControlText(lyricsLabel);
            ClientSizeChanged += (_, _) => LayoutPlayerControls();
            FontChanged += (_, _) => LayoutPlayerControls();
            foreach (Control control in playbackControls.Concat(searchPanel.Controls.Cast<Control>()))
                control.FontChanged += (_, _) => LayoutPlayerControls();
            DpiChanged += (_, _) =>
            {
                // WinForms still has to scale child fonts/bounds after this event.
                // Measure once that work has finished, against the destination screen.
                _dpiLayoutPending = true;
                BeginInvoke(new Action(() =>
                {
                    _dpiLayoutPending = false;
                    if (IsDisposed || Disposing) return;
                    ConstrainPlayerWindow(Bounds);
                    LayoutPlayerControls();
                }));
            };
            Shown += (_, _) => { ConstrainPlayerWindow(Bounds); LayoutPlayerControls(); };
            ConstrainPlayerWindow(Bounds);
            LayoutPlayerControls();
        }

        private void UpdateFullControlText(Label label)
        {
            label.AccessibleName = label.Text;
            _controlTextToolTip?.SetToolTip(label, label.Text);
        }

        private void ConstrainPlayerWindow(Rectangle bounds)
        {
            Rectangle workArea = Screen.FromRectangle(bounds).WorkingArea;
            float scale = DeviceDpi / 96f;
            MinimumSize = new Size(Math.Min(workArea.Width, (int)(520 * scale)), Math.Min(workArea.Height, (int)(400 * scale)));
            if (WindowState == FormWindowState.Normal) Bounds = EnsureWindowBoundsVisible(bounds);
        }

        private void ProgressGestureStarted(object? sender, SliderGestureEventArgs e)
        {
            _isUserDragging = true;
            _progressGestureSong = _audioEngine?.CurrentSong;
            _progressGestureRequestVersion = Interlocked.Read(ref _playRequestVersion);
            _activeProgressGestureEpoch = Interlocked.Read(ref _progressGestureEpoch);
            _progressGestureDuration = GetCachedDuration();
            _progressLastRequestedTarget = double.NaN;
            if (!IsProgressGestureCurrent()) progressTrackBar.EndGesture(true);
        }

        private bool IsProgressGestureCurrent() => _isUserDragging && _progressGestureDuration > 0 &&
            _progressGestureSong != null && ReferenceEquals(_progressGestureSong, _audioEngine?.CurrentSong) &&
            _progressGestureRequestVersion == Interlocked.Read(ref _playRequestVersion) &&
            _activeProgressGestureEpoch == Interlocked.Read(ref _progressGestureEpoch) && !IsSwitchingTrack;

        private void ProgressGesturePreview(object? sender, SliderGestureEventArgs e)
        {
            if (!IsProgressGestureCurrent()) { progressTrackBar.EndGesture(true); return; }
            // Visual-only movement must not raise any extra accessibility events.
            timeLabel.Text = FormatTimeFromSeconds(e.Position) + " / " + FormatTimeFromSeconds(_progressGestureDuration);
        }

        private void ProgressGestureEnded(object? sender, SliderGestureEventArgs e)
        {
            bool current = IsProgressGestureCurrent();
            _isUserDragging = false;
            _progressGestureSong = null;
            if (!current) return;
            if (!e.Canceled)
            {
                double target = Math.Max(0, Math.Min(_progressGestureDuration, e.Position));
                if (double.IsNaN(_progressLastRequestedTarget) || Math.Abs(target - _progressLastRequestedTarget) > 0.000001)
                    RequestSeekAndResetLyrics(target);
                UpdateProgressTrackBarDisplay(target, _progressGestureDuration, announceWhenFocused: true);
            }
            _seekManager?.FinishSeek();
        }

        private void CancelProgressGestureForTrackChange()
        {
            // Invalidate synchronously even if the playback request originated off the UI
            // thread. A queued mouse-up must never commit into a newer playback request.
            long canceledEpoch = Interlocked.Increment(ref _progressGestureEpoch);
            if (IsDisposed || Disposing) return;
            void EndOldGesture()
            {
                if (_isUserDragging && _activeProgressGestureEpoch < canceledEpoch)
                    progressTrackBar.EndGesture(true);
            }
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    try { BeginInvoke(new Action(EndOldGesture)); }
                    catch (InvalidOperationException) { /* The window closed while posting. */ }
                }
            }
            else EndOldGesture();
        }

        private void LayoutPlayerControls()
        {
            if (_layingOutPlayerControls || _dpiLayoutPending || _playbackLayoutHost == null || IsDisposed) return;
            _layingOutPlayerControls = true;
            searchPanel.SuspendLayout();
            _playbackLayoutHost.SuspendLayout();
            try
            {
                float scale = DeviceDpi / 96f;
                int S(int logical) => Math.Max(1, (int)Math.Round(logical * scale));
                int gap = S(8), margin = S(12);
                int TextWidth(Control c, string text) => TextRenderer.MeasureText(text, c.Font).Width;
                // Search has an independent second row when its measured contents do not fit.
                int rowHeight = Math.Max(S(36), Math.Max(searchTypeComboBox.PreferredHeight, searchButton.Font.Height + S(12)));
                int backWidth = Math.Max(S(36), TextWidth(backButton, backButton.Text) + S(8));
                int searchLabelWidth = TextWidth(searchLabel, searchLabel.Text);
                int buttonWidth = TextWidth(searchButton, searchButton.Text) + S(24);
                int typeLabelWidth = TextWidth(searchTypeLabel, searchTypeLabel.Text);
                int typeWidth = Math.Max(S(96), TextWidth(searchTypeComboBox, "播客") + S(36));
                searchPanel.AutoScrollPosition = Point.Empty;
                int searchWidth = Math.Max(S(360), searchPanel.Width);
                bool wrapSearch = searchWidth < margin * 2 + backWidth + searchLabelWidth + S(200) + typeLabelWidth + typeWidth + buttonWidth + gap * 6;
                searchPanel.Height = margin * 2 + rowHeight * (wrapSearch ? 2 : 1) + (wrapSearch ? gap : 0);
                searchPanel.AutoScrollMinSize = new Size(S(360), 0);
                backButton.SetBounds(margin, margin, backWidth, rowHeight);
                searchLabel.Location = new Point(margin + backWidth + gap, margin + (rowHeight - searchLabel.Height) / 2);
                int inputX = margin + backWidth + searchLabelWidth + gap * 2;
                int rightStart = wrapSearch ? margin : searchWidth - margin - typeLabelWidth - typeWidth - buttonWidth - gap * 2;
                searchTextBox.SetBounds(inputX, margin + (rowHeight - searchTextBox.PreferredHeight) / 2,
                    (wrapSearch ? searchWidth - margin : rightStart - gap) - inputX, searchTextBox.PreferredHeight);
                int rightY = wrapSearch ? margin + rowHeight + gap : margin;
                searchTypeLabel.Location = new Point(rightStart, rightY + (rowHeight - searchTypeLabel.Height) / 2);
                searchTypeComboBox.SetBounds(rightStart + typeLabelWidth + gap, rightY + (rowHeight - searchTypeComboBox.PreferredHeight) / 2, typeWidth, searchTypeComboBox.PreferredHeight);
                searchButton.SetBounds(searchWidth - margin - buttonWidth, rightY, buttonWidth, rowHeight);

                int playWidth = Math.Max(S(100), TextWidth(playPauseButton, "缓冲中...") + S(24));
                int playHeight = Math.Max(S(40), playPauseButton.Font.Height * 2 + S(8));
                int timeWidth = TextWidth(timeLabel, "888:88:88 / 888:88:88") + S(6);
                int volumeWidth = S(140), percentWidth = TextWidth(volumeLabel, "100%") + S(8);
                int sliderHeight = Math.Max(progressTrackBar.PreferredVisualHeight, volumeTrackBar.PreferredVisualHeight);
                int lineHeight = Math.Max(playHeight, sliderHeight);
                int titleHeight = currentSongLabel.Font.Height + S(4);
                int lyricHeight = Math.Max(S(40), lyricsLabel.Font.Height + S(16));
                int minimumWidth = Math.Max(S(360), timeWidth + S(160) + gap + 2 * margin);
                int available = Math.Max(minimumWidth, controlPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                bool wide = available >= 2 * margin + playWidth + S(260) + timeWidth + volumeWidth + percentWidth + 5 * gap;
                int y = margin + titleHeight + gap;
                int contentHeight = y + lineHeight * (wide ? 1 : 2) + (wide ? 0 : gap) + gap + lyricHeight + margin;
                int availableHeight = Math.Max(S(80), ClientSize.Height - menuStrip1.Height - statusStrip1.Height - searchPanel.Height - S(96));
                controlPanel.Height = Math.Min(contentHeight, availableHeight);
                Panel host = _playbackLayoutHost;
                host.Bounds = controlPanel.ClientRectangle;
                host.AutoScrollMinSize = new Size(minimumWidth, contentHeight);
                bool needsVerticalScroll = contentHeight > host.Height - (minimumWidth > host.Width ? SystemInformation.HorizontalScrollBarHeight : 0);
                int width = Math.Max(minimumWidth, host.Width - (needsVerticalScroll ? SystemInformation.VerticalScrollBarWidth : 0));
                Point scroll = host.AutoScrollPosition;
                void Place(Control c, int x, int top, int w, int h) => c.SetBounds(x + scroll.X, top + scroll.Y, Math.Max(1, w), h);
                Place(currentSongLabel, margin, margin, width - 2 * margin, titleHeight);
                int trackY = y + (lineHeight - sliderHeight) / 2;
                if (wide)
                {
                    Place(playPauseButton, margin, y + (lineHeight - playHeight) / 2, playWidth, playHeight);
                    int percentX = width - margin - percentWidth;
                    int volumeX = percentX - gap - volumeWidth;
                    int timeX = volumeX - gap - timeWidth;
                    int progressX = margin + playWidth + gap;
                    Place(progressTrackBar, progressX, trackY, timeX - gap - progressX, sliderHeight);
                    Place(timeLabel, timeX, trackY + sliderHeight - S(32), timeWidth, S(32));
                    Place(volumeTrackBar, volumeX, trackY, volumeWidth, sliderHeight);
                    Place(volumeLabel, percentX, trackY + sliderHeight - S(32), percentWidth, S(32));
                }
                else
                {
                    int timeX = width - margin - timeWidth;
                    Place(progressTrackBar, margin, trackY, timeX - gap - margin, sliderHeight);
                    Place(timeLabel, timeX, trackY + sliderHeight - S(32), timeWidth, S(32));
                    y += lineHeight + gap;
                    trackY = y + (lineHeight - sliderHeight) / 2;
                    Place(playPauseButton, margin, y + (lineHeight - playHeight) / 2, playWidth, playHeight);
                    int percentX = width - margin - percentWidth;
                    int volumeX = Math.Max(margin + playWidth + gap, percentX - gap - volumeWidth);
                    Place(volumeTrackBar, volumeX, trackY, percentX - gap - volumeX, sliderHeight);
                    Place(volumeLabel, percentX, trackY + sliderHeight - S(32), percentWidth, S(32));
                }
                Place(lyricsLabel, margin, y + lineHeight + gap, width - 2 * margin, lyricHeight);


            }
            finally
            {
                searchPanel.ResumeLayout(true);
                _playbackLayoutHost.ResumeLayout(true);
                _layingOutPlayerControls = false;
            }
        }
    }
}
