using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal static class ContextMenuAccessibilityHelper
    {
        private const int InitialSelectionDelayMs = 32;
        private const uint EventObjectFocus = 0x8005;
        private const int ObjIdClient = -4;
        private static readonly ConditionalWeakTable<ToolStripDropDown, OpeningState> States = new();

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern void NotifyWinEvent(uint eventId, IntPtr hwnd, int objectId, int childId);

        private static ToolStripItem? FindFirstNavigableItem(ToolStripItemCollection? items)
        {
            if (items == null) return null;
            foreach (ToolStripItem item in items)
            {
                if (IsNavigable(item)) return item;
            }
            return null;
        }

        private static bool IsNavigable(ToolStripItem item) => !item.IsDisposed && item is not ToolStripSeparator &&
            item.Available && item.Visible && item.Enabled && item.CanSelect;

        private static ToolStripItem? FindSelectedItem(ToolStripDropDown menu)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                if (item.Selected && IsNavigable(item)) return item;
            }
            return null;
        }

        /// <summary>Also handles nested and dynamically created drop-downs. Registration is idempotent.</summary>
        public static void Attach(ToolStrip? menu)
        {
            if (menu is ToolStripDropDown dropDown && !dropDown.IsDisposed)
                _ = States.GetValue(dropDown, value => new OpeningState(value));
        }

        public static void EnsureFirstItemFocusedOnOpen(Control owner, ContextMenuStrip menu, string menuName, Action<string>? log = null)
        {
            if (owner == null || owner.IsDisposed || menu == null || menu.IsDisposed) return;
            Attach(menu);
            States.GetValue(menu, value => new OpeningState(value)).Schedule(menuName, log);
        }

        public static void PrimeForAccessibility(ToolStripDropDown? menu)
        {
            if (menu == null || menu.IsDisposed) return;
            Attach(menu);
            try
            {
                _ = menu.Handle;
                _ = menu.AccessibilityObject;
                foreach (ToolStripItem item in menu.Items)
                {
                    if (!item.IsDisposed && item.Available) _ = item.AccessibilityObject;
                }
            }
            catch (Exception ex) { Debug.WriteLine("[MenuAccessibility] Priming: " + ex.Message); }
        }

        /// <summary>An Up/Down key in a one-item menu is still a navigation request. Report its
        /// actual item without toggling selection, adding fake items, or invoking a speech API.</summary>
        public static void NotifySingleItemNavigation(ToolStrip menu, ToolStripItem item, bool wasSelected)
        {
            if (menu is not ToolStripDropDown dropDown) return;
            if (States.TryGetValue(dropDown, out var state)) state.CancelPending();
            if (wasSelected) NotifyItemFocus(dropDown, item);
        }

        private static bool NotifyItemFocus(ToolStripDropDown menu, ToolStripItem item)
        {
            if (menu.IsDisposed || !menu.Visible || !menu.IsHandleCreated || !IsNavigable(item) || !item.Selected ||
                item.GetCurrentParent() != menu) return false;
            try
            {
                // MSAA child IDs follow the accessible tree, NOT Items.IndexOf: hidden items,
                // alignment, overflow and custom providers can change the exposed child order.
                var parent = menu.AccessibilityObject;
                var target = item.AccessibilityObject;
                for (int index = 0; index < parent.GetChildCount(); index++)
                {
                    if (ReferenceEquals(parent.GetChild(index), target))
                    {
                        // WinForms retains its UIA focus provider. Supply the standard legacy
                        // focus event only after state is queryable, with the same item identity.
                        NotifyWinEvent(EventObjectFocus, menu.Handle, ObjIdClient, index + 1);
                        return true;
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[MenuAccessibility] Focus notification: " + ex.Message); }
            return false;
        }

        private sealed class OpeningState
        {
            private readonly ToolStripDropDown _menu;
            private Timer? _timer;
            private int _generation;
            private bool _scheduled;
            private ToolStripItem? _target;

            internal OpeningState(ToolStripDropDown menu)
            {
                _menu = menu;
                menu.Opening += (_, _) => { Reset(); PrimeForAccessibility(menu); };
                menu.Opened += (_, _) => Schedule(menu.Name, null);
                menu.Closed += (_, _) => Reset();
                menu.Disposed += (_, _) => Reset();
            }

            internal void Schedule(string menuName, Action<string>? log)
            {
                if (_scheduled || _menu.IsDisposed || !_menu.Visible || !_menu.IsHandleCreated) return;
                _scheduled = true;
                _target = FindSelectedItem(_menu) ?? FindFirstNavigableItem(_menu.Items);
                if (_target == null) return;
                int generation = _generation;
                try
                {
                    // Leave the opening callback before focusing. The menu must finish showing
                    // and constructing its providers before an accessibility client queries them.
                    _menu.BeginInvoke(new Action(() =>
                    {
                        if (!IsCurrent(generation)) return;
                        try
                        {
                            var current = FindSelectedItem(_menu);
                            if (current != null && current != _target) return;
                            if (HasOpenChild()) return;
                            // ToolStrip's modal menu filter already routes keyboard input here.
                            // Focusing the popup HWND creates extra container-focus events; it
                            // also leaves UIA's focused element pointing at the menu container.
                            // Let the opening event finish before selecting the first item once.
                            var timer = new Timer { Interval = InitialSelectionDelayMs };
                            _timer = timer;
                            timer.Tick += (_, _) =>
                            {
                                if (!ReferenceEquals(_timer, timer)) return;
                                StopTimer();
                                try
                                {
                                    if (!IsCurrent(generation)) return;
                                    var selected = FindSelectedItem(_menu);
                                    if (selected != null && selected != _target) return;
                                    // Do not re-enter a parent when its child menu has opened.
                                    if (HasOpenChild()) return;
                                    // A genuine selection raises WinForms' MSAA and UIA focus
                                    // events, just like arrow navigation. Do not replay it later.
                                    if (!_target!.Selected) _target.Select();
                                }
                                catch (Exception ex) { log?.Invoke($"[{menuName}] 菜单首项选择失败: {ex.Message}"); }
                            };
                            timer.Start();
                        }
                        catch (Exception ex) { log?.Invoke($"[{menuName}] 菜单焦点初始化失败: {ex.Message}"); }
                    }));
                }
                catch (InvalidOperationException) { /* The menu may have closed/disposed before posting. */ }
            }

            private bool IsCurrent(int generation) => generation == _generation && !_menu.IsDisposed && _menu.Visible &&
                _target != null && IsNavigable(_target) && _target.GetCurrentParent() == _menu;

            private bool HasOpenChild() => _target is ToolStripDropDownItem child && child.HasDropDownItems && child.DropDown.Visible;

            internal void CancelPending()
            {
                unchecked { _generation++; }
                StopTimer();
            }

            private void Reset()
            {
                CancelPending();
                _scheduled = false;
                _target = null;
            }

            private void StopTimer()
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
