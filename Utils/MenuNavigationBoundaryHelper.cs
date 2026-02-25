using System;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 为菜单键盘导航提供边界保护：
    /// - 右方向键在不可展开项上不应跳出当前菜单上下文。
    /// - 左方向键在不可折叠菜单层级上不应切换到其他菜单栏入口。
    /// </summary>
    internal static class MenuNavigationBoundaryHelper
    {
        public static void Attach(ToolStrip? menu)
        {
            if (menu == null || menu.IsDisposed)
            {
                return;
            }

            menu.PreviewKeyDown -= OnMenuPreviewKeyDown;
            menu.PreviewKeyDown += OnMenuPreviewKeyDown;
            menu.KeyDown -= OnMenuKeyDown;
            menu.KeyDown += OnMenuKeyDown;
            menu.ItemAdded -= OnMenuItemAdded;
            menu.ItemAdded += OnMenuItemAdded;

            foreach (ToolStripItem item in menu.Items)
            {
                AttachItem(item);
            }
        }

        private static void OnMenuItemAdded(object? sender, ToolStripItemEventArgs e)
        {
            if (e?.Item == null)
            {
                return;
            }

            AttachItem(e.Item);
        }

        private static void AttachItem(ToolStripItem? item)
        {
            if (item is not ToolStripDropDownItem dropDownItem)
            {
                return;
            }

            dropDownItem.DropDownOpening -= OnDropDownOpening;
            dropDownItem.DropDownOpening += OnDropDownOpening;

            if (dropDownItem.DropDown != null)
            {
                Attach(dropDownItem.DropDown);
            }
        }

        private static void OnDropDownOpening(object? sender, EventArgs e)
        {
            if (sender is ToolStripDropDownItem dropDownItem && dropDownItem.DropDown != null)
            {
                Attach(dropDownItem.DropDown);
            }
        }

        private static void OnMenuPreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
        {
            if (sender is not ToolStrip menu || menu.IsDisposed)
            {
                return;
            }

            if (menu is MenuStrip)
            {
                // 顶层菜单栏保留 WinForms 默认左右切换行为（Alt 进入后在顶层项间移动）。
                return;
            }

            bool shouldSuppress = e.KeyCode switch
            {
                Keys.Right => ShouldSuppressRight(menu),
                Keys.Left => ShouldSuppressLeft(menu),
                _ => false
            };

            if (shouldSuppress)
            {
                // 仅在需要“边界拦截”时把左右键转为输入键；
                // 其余情况放行给 ToolStrip 默认导航（展开/折叠子菜单）。
                e.IsInputKey = true;
            }
        }

        private static void OnMenuKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not ToolStrip menu || e == null)
            {
                return;
            }

            switch (e.KeyCode)
            {
                case Keys.Right:
                    if (ShouldSuppressRight(menu))
                    {
                        SuppressAndKeepFocus(menu, e);
                    }
                    break;
                case Keys.Left:
                    if (ShouldSuppressLeft(menu))
                    {
                        SuppressAndKeepFocus(menu, e);
                    }
                    break;
            }
        }

        private static bool ShouldSuppressRight(ToolStrip menu)
        {
            if (menu is MenuStrip)
            {
                return false;
            }

            if (menu is ToolStripDropDown dropDown && !dropDown.Visible)
            {
                return false;
            }

            ToolStripItem? selectedItem = FindSelectedItem(menu);
            if (selectedItem == null)
            {
                return false;
            }

            return !CanExpand(selectedItem);
        }

        private static bool ShouldSuppressLeft(ToolStrip menu)
        {
            if (menu is MenuStrip)
            {
                return false;
            }

            if (menu is not ToolStripDropDown dropDown)
            {
                return false;
            }

            if (!dropDown.Visible)
            {
                return false;
            }

            if (IsNestedDropDown(dropDown))
            {
                // 子菜单层允许左方向键回到父菜单。
                return false;
            }

            return FindSelectedItem(menu) != null;
        }

        private static bool IsNestedDropDown(ToolStripDropDown dropDown)
        {
            ToolStripItem? ownerItem = dropDown.OwnerItem;
            if (ownerItem == null)
            {
                return false;
            }

            return ownerItem.Owner is ToolStripDropDown;
        }

        private static bool CanExpand(ToolStripItem item)
        {
            if (item is not ToolStripDropDownItem dropDownItem)
            {
                return false;
            }

            // 这里只判断是否存在子菜单项，不依赖 child.Available：
            // 未展开时子项常常不可用（Available=false），否则会误判“不可展开”。
            return dropDownItem.HasDropDownItems;
        }

        private static ToolStripItem? FindSelectedItem(ToolStrip menu)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item.Selected && item.Available && item.Visible)
                {
                    return item;
                }
            }

            return null;
        }

        private static void SuppressAndKeepFocus(ToolStrip menu, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            ToolStripItem? selectedItem = FindSelectedItem(menu);
            if (selectedItem == null)
            {
                return;
            }

            selectedItem.Select();
            try
            {
                selectedItem.AccessibilityObject?.Select(AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection);
            }
            catch
            {
                // 某些宿主不支持无障碍显式选中，忽略即可。
            }
        }
    }
}
