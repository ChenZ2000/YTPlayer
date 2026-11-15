using System;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal static class ListRemovalSelection
    {
        // Keep a surviving selection on the same item. If it was removed, use its
        // former position, clamped to the new last item. -1 means an empty list.
        internal static int ResolveIndex(int selectedIndex, int removedIndex, int remainingCount)
        {
            if (remainingCount <= 0) return -1;
            int index = selectedIndex < 0 ? removedIndex : selectedIndex;
            if (selectedIndex > removedIndex) index--;
            return Math.Clamp(index, 0, remainingCount - 1);
        }

        internal static void RestoreAfterRefresh(ListView list, ListViewItem? selectedItem, int previousIndex)
        {
            if (previousIndex < 0 || list.Items.Count == 0 || list.IsDisposed) return;
            // Incremental transfer refreshes keep surviving ListViewItem instances.
            int index = selectedItem?.ListView == list ? selectedItem.Index : Math.Min(previousIndex, list.Items.Count - 1);
            var target = list.Items[index];
            if (target.Selected && target.Focused) return;
            list.SelectedIndices.Clear();
            target.Selected = true;
            target.Focused = true;
            target.EnsureVisible();
            // Setting the item focus does not take keyboard focus from another control.
        }
    }
}
