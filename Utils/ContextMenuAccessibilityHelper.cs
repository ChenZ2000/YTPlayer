using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal static class ContextMenuAccessibilityHelper
    {
        private const int FocusWarmupDelayMs = 32;
        private const int FocusStabilizeAttempts = 8;
        private const int FocusStabilizeDelayMs = 20;
        private const int MinimumFocusPushAttempts = 2;

        public static ToolStripItem? FindFirstNavigableItem(ToolStripItemCollection? items)
        {
            if (items == null || items.Count == 0)
            {
                return null;
            }

            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripSeparator)
                {
                    continue;
                }

                if (!item.Available || !item.Visible || !item.Enabled)
                {
                    continue;
                }

                return item;
            }

            return null;
        }

        public static void EnsureFirstItemFocusedOnOpen(Control owner, ContextMenuStrip menu, string menuName, Action<string>? log = null)
        {
            if (owner == null || menu == null || owner.IsDisposed || menu.IsDisposed)
            {
                return;
            }

            _ = EnsureFirstItemFocusedOnOpenAsync(owner, menu, menuName, log);
        }

        public static void PrimeForAccessibility(ContextMenuStrip? menu)
        {
            if (menu == null || menu.IsDisposed)
            {
                return;
            }

            try
            {
                if (!menu.IsHandleCreated)
                {
                    _ = menu.Handle;
                }
            }
            catch
            {
                // 某些宿主阶段可能尚不允许创建句柄，忽略即可。
            }

            PrimeItemsForAccessibility(menu.Items);
        }

        public static bool TrySelectFirstNavigableItem(ContextMenuStrip? menu, out ToolStripItem? selectedItem)
        {
            selectedItem = null;

            if (menu == null || menu.IsDisposed || menu.Items.Count == 0)
            {
                return false;
            }

            if (menu.CanFocus)
            {
                menu.Focus();
            }

            menu.Select();
            selectedItem = FindFirstNavigableItem(menu.Items);

            if (selectedItem == null)
            {
                return false;
            }

            selectedItem.Select();
            return true;
        }

        private static async Task EnsureFirstItemFocusedOnOpenAsync(Control owner, ContextMenuStrip menu, string menuName, Action<string>? log)
        {
            if (FocusWarmupDelayMs > 0)
            {
                await Task.Delay(FocusWarmupDelayMs).ConfigureAwait(continueOnCapturedContext: true);
            }

            ToolStripItem? lastSelectedItem = null;

            for (int attempt = 1; attempt <= FocusStabilizeAttempts; attempt++)
            {
                if (owner.IsDisposed || menu.IsDisposed || !menu.Visible)
                {
                    return;
                }

                try
                {
                    if (TrySelectFirstNavigableItem(menu, out ToolStripItem? selectedItem) && selectedItem != null)
                    {
                        if (attempt >= MinimumFocusPushAttempts)
                        {
                            // 第二轮开始再强制推送无障碍焦点，避开菜单首次进入时的系统初始化竞争。
                            TrySelectItemAccessibilityFocus(selectedItem);
                        }

                        if (attempt >= MinimumFocusPushAttempts && selectedItem.Selected)
                        {
                            if (attempt > MinimumFocusPushAttempts)
                            {
                                log?.Invoke($"[{menuName}] 首项焦点在第 {attempt} 次尝试后稳定。");
                            }

                            return;
                        }

                        lastSelectedItem = selectedItem;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[{menuName}] 尝试设置首项焦点失败: {ex.Message}");
                }

                if (attempt < FocusStabilizeAttempts)
                {
                    await Task.Delay(FocusStabilizeDelayMs).ConfigureAwait(continueOnCapturedContext: true);
                }
            }

            if (lastSelectedItem != null)
            {
                TrySelectItemAccessibilityFocus(lastSelectedItem);
            }
        }

        private static void PrimeItemsForAccessibility(ToolStripItemCollection? items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            foreach (ToolStripItem item in items)
            {
                if (item == null)
                {
                    continue;
                }

                try
                {
                    _ = item.AccessibilityObject;
                }
                catch
                {
                    // 忽略单个项的预热异常，避免影响菜单整体逻辑。
                }

                if (item is ToolStripDropDownItem dropDownItem && dropDownItem.HasDropDownItems)
                {
                    PrimeItemsForAccessibility(dropDownItem.DropDownItems);
                }
            }
        }

        private static void TrySelectItemAccessibilityFocus(ToolStripItem item)
        {
            if (item == null)
            {
                return;
            }

            try
            {
                item.AccessibilityObject?.Select(AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection);
            }
            catch
            {
                // 某些自定义菜单项可能不支持该调用，忽略即可。
            }
        }
    }
}
