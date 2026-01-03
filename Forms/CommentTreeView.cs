using System;
using System.Windows.Forms;

namespace YTPlayer.Forms
{
    internal sealed class CommentTreeView : TreeView
    {
        public void NotifyAccessibilityReorder(string reason)
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
    }
}
