using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace YTPlayer
{
    internal sealed class SafeListView : ListView
    {
        protected override void OnGotFocus(EventArgs e)
        {
            try
            {
                SafeClearSelectionIfEmpty();
                base.OnGotFocus(e);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine("[SafeListView] OnGotFocus suppressed: " + ex.Message);
                SafeClearSelection();
            }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            try
            {
                SafeClearSelectionIfEmpty();
                base.OnLostFocus(e);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine("[SafeListView] OnLostFocus suppressed: " + ex.Message);
                SafeClearSelection();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                SafeClearSelectionIfEmpty();
                base.OnHandleDestroyed(e);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine("[SafeListView] OnHandleDestroyed suppressed: " + ex.Message);
                SafeClearSelection();
            }
        }

        private void SafeClearSelectionIfEmpty()
        {
            if (VirtualMode)
            {
                if (VirtualListSize <= 0)
                {
                    SafeClearSelection();
                }
                return;
            }

            if (Items.Count == 0)
            {
                SafeClearSelection();
            }
        }

        private void SafeClearSelection()
        {
            try
            {
                SelectedIndices.Clear();
            }
            catch
            {
            }

            try
            {
                FocusedItem = null;
            }
            catch
            {
            }
        }
    }
}
