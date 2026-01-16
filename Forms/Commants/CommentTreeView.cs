using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using System.Windows.Automation.Provider;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class CommentTreeView : TreeView
    {
        private const int TVM_SELECTITEM = 0x1100 + 11;
        private const int TVM_EXPAND = 0x1100 + 2;
        private const int TVM_GETITEMA = 0x1100 + 12;
        private const int TVM_GETITEMW = 0x1100 + 62;
        private const int TVM_MAPACCIDTOHTREEITEM = 0x1100 + 42;
        private const int TVM_MAPHTREEITEMTOACCID = 0x1100 + 43;
        private const int WM_DESTROY = 0x0002;
        private const int WM_SETFOCUS = 0x0007;
        private const int WM_KILLFOCUS = 0x0008;
        private const int WM_ENABLE = 0x000A;
        private const int WM_SHOWWINDOW = 0x0018;
        private const int WM_GETOBJECT = 0x003D;
        private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
        private const int TVM_SETITEMW = 0x1100 + 63;
        private const uint TVIF_TEXT = 0x0001;
        private const uint TVIF_CHILDREN = 0x0040;
        private static readonly Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        private static readonly int UiaRootObjectId = AutomationInteropProvider.RootObjectId;
        private const int UIA_PFIA_DEFAULT = 0x0;
        private static readonly bool LogNativeItemRequests = false;
        private static readonly bool LogAccIdMapping = false;
        private static readonly TimeSpan AccNameLogThrottle = TimeSpan.FromMilliseconds(500);
        private static DateTime _lastAccNameLogAt = DateTime.MinValue;
        private const int ControlRoleRestoreDelayMs = 80;
        private const int AccessibleTextRestoreDelayMs = 120;
        private const int AccessibilityRefreshDelayMs = 60;
        private DateTime _suppressControlRoleUntil = DateTime.MinValue;
        private DateTime _suppressAccessibleTextUntil = DateTime.MinValue;
        private bool _suppressControlRole;
        private readonly Timer _restoreControlRoleTimer;
        private bool _suppressAccessibleText;
        private readonly Timer _restoreAccessibleTextTimer;
        private readonly Timer _accessibilityRefreshTimer;
        private string? _pendingAccessibilityRefreshReason;

        public CommentTreeView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            UpdateStyles();
            _restoreControlRoleTimer = new Timer
            {
                Interval = ControlRoleRestoreDelayMs
            };
            _restoreControlRoleTimer.Tick += (_, _) =>
            {
                _restoreControlRoleTimer.Stop();
                if (IsDisposed)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (_suppressControlRoleUntil > now)
                {
                    int remaining = (int)Math.Ceiling((_suppressControlRoleUntil - now).TotalMilliseconds);
                    _restoreControlRoleTimer.Interval = Math.Max(ControlRoleRestoreDelayMs, Math.Min(remaining, 1000));
                    _restoreControlRoleTimer.Start();
                    return;
                }

                _restoreControlRoleTimer.Interval = ControlRoleRestoreDelayMs;
                _suppressControlRoleUntil = DateTime.MinValue;
                SuppressControlRole(false);
            };
            _restoreAccessibleTextTimer = new Timer
            {
                Interval = AccessibleTextRestoreDelayMs
            };
            _restoreAccessibleTextTimer.Tick += (_, _) =>
            {
                _restoreAccessibleTextTimer.Stop();
                if (IsDisposed)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (_suppressAccessibleTextUntil > now)
                {
                    int remaining = (int)Math.Ceiling((_suppressAccessibleTextUntil - now).TotalMilliseconds);
                    _restoreAccessibleTextTimer.Interval = Math.Max(AccessibleTextRestoreDelayMs, Math.Min(remaining, 1000));
                    _restoreAccessibleTextTimer.Start();
                    return;
                }

                _restoreAccessibleTextTimer.Interval = AccessibleTextRestoreDelayMs;
                _suppressAccessibleTextUntil = DateTime.MinValue;
                SuppressAccessibleText(false, "timer");
            };
            _accessibilityRefreshTimer = new Timer
            {
                Interval = AccessibilityRefreshDelayMs
            };
            _accessibilityRefreshTimer.Tick += (_, _) =>
            {
                _accessibilityRefreshTimer.Stop();
                FlushAccessibilityRefresh();
            };
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<TreeNode, string?>? DisplayTextResolver { get; set; }

        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            if (e.Node == null)
            {
                base.OnDrawNode(e);
                return;
            }

            string text = GetResolvedNodeText(e.Node);
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            bool focused = (e.State & TreeNodeStates.Focused) != 0;
            Color backColor = selected && ContainsFocus ? SystemColors.Highlight : BackColor;
            Color foreColor = selected && ContainsFocus ? SystemColors.HighlightText : ForeColor;

            using (var backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }

            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, text, Font, e.Bounds, foreColor, flags);

            if (focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, foreColor, backColor);
            }
        }

        internal string GetResolvedNodeText(TreeNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            string? resolved = null;
            try
            {
                resolved = DisplayTextResolver?.Invoke(node);
            }
            catch
            {
            }

            return resolved ?? node.Text ?? string.Empty;
        }

        [Conditional("DEBUG")]
        private void LogAccNameForLastVisible(string source, TreeNode node, string name)
        {
            if (node == null)
            {
                return;
            }

            if (node.NextVisibleNode != null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now - _lastAccNameLogAt < AccNameLogThrottle)
            {
                return;
            }

            _lastAccNameLogAt = now;
            string rawText = node.Text ?? string.Empty;
            string tagInfo = DescribeTreeNode(node);
            LogTree($"AccNameLastVisible source={source} name='{TrimPreview(name ?? string.Empty, 60)}' nodeText='{TrimPreview(rawText, 60)}' node={tagInfo}");
        }

        public void SetNodeHasChildren(TreeNode node, bool hasChildren)
        {
            if (node == null || !IsHandleCreated || node.Handle == IntPtr.Zero)
            {
                LogTree($"SetNodeHasChildren skip hasChildren={hasChildren} nodeNull={node == null} handleCreated={IsHandleCreated} nodeHandle={(node?.Handle ?? IntPtr.Zero)}");
                return;
            }

            TVITEM item = new TVITEM
            {
                mask = TVIF_CHILDREN,
                hItem = node.Handle,
                cChildren = hasChildren ? 1 : 0
            };

            SendMessage(Handle, TVM_SETITEMW, IntPtr.Zero, ref item);
        }

        public void NotifyAccessibilityReorder(string reason)
        {
            if (!IsHandleCreated)
            {
                return;
            }

            if (!ContainsFocus)
            {
                string? activeName = null;
                try
                {
                    var form = FindForm();
                    var active = form?.ActiveControl;
                    activeName = active?.Name ?? active?.GetType().Name;
                }
                catch
                {
                }
                LogTree($"AccessibilityReorder skip (no focus) reason={reason} focused={Focused} containsFocus={ContainsFocus} active={activeName ?? "null"}");
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

        public void ScheduleAccessibilityRefresh(string reason)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            _pendingAccessibilityRefreshReason = reason;
            if (!_accessibilityRefreshTimer.Enabled)
            {
                _accessibilityRefreshTimer.Start();
                return;
            }

            _accessibilityRefreshTimer.Stop();
            _accessibilityRefreshTimer.Start();
        }

        public void NotifyAccessibilityItemNameChange(TreeNode node)
        {
            if (node == null || !IsHandleCreated || node.Handle == IntPtr.Zero)
            {
                return;
            }

            if (!ContainsFocus || !ReferenceEquals(SelectedNode, node))
            {
                return;
            }

            if (!TryGetAccessibleChildId(node, out int childId))
            {
                return;
            }

            var tag = node.Tag as CommentNodeTag;
            string id = tag?.CommentId ?? "null";
            string text = node.Text ?? string.Empty;
            if (text.Length > 48)
            {
                text = text.Substring(0, 48);
            }

            long handleValueForLog = node.Handle.ToInt64();
            if (LogAccIdMapping)
            {
                int accId = TryGetAccIdForNode(node);
                int index = GetVisibleNodeIndex(node);
                LogTree($"AccessibilityNameChange id={id} level={node.Level} index={index} accId={accId} handle=0x{handleValueForLog:X} childId={childId} text='{text}'");
            }

            try
            {
                AccessibilityNotifyClients(AccessibleEvents.NameChange, childId);
            }
            catch
            {
            }
        }

        public void NotifyAccessibilitySelection(TreeNode node)
        {
            if (node == null || !IsHandleCreated || node.Handle == IntPtr.Zero)
            {
                return;
            }

            if (!ContainsFocus || !ReferenceEquals(SelectedNode, node))
            {
                return;
            }

            if (!TryGetAccessibleChildId(node, out int childId))
            {
                return;
            }

            try
            {
                AccessibilityNotifyClients(AccessibleEvents.Selection, childId);
            }
            catch
            {
            }

            try
            {
                AccessibilityNotifyClients(AccessibleEvents.Focus, childId);
            }
            catch
            {
            }
        }

        public void NotifyAccessibilityStateChange(TreeNode node)
        {
            if (node == null || !IsHandleCreated || node.Handle == IntPtr.Zero)
            {
                return;
            }

            if (!ContainsFocus)
            {
                return;
            }

            if (!TryGetAccessibleChildId(node, out int childId))
            {
                return;
            }

            try
            {
                AccessibilityNotifyClients(AccessibleEvents.StateChange, childId);
            }
            catch
            {
            }
        }

        private bool TryGetAccessibleChildId(TreeNode node, out int childId)
        {
            childId = -1;
            if (node == null || !IsHandleCreated || node.Handle == IntPtr.Zero)
            {
                return false;
            }

            int accId = TryGetAccIdForNode(node);
            if (accId > 0)
            {
                childId = accId;
                return true;
            }

            int index = GetVisibleNodeIndex(node);
            if (index >= 0)
            {
                childId = index + 1;
                return true;
            }

            return false;
        }

        public void SuppressControlRole(bool suppress)
        {
            if (_suppressControlRole == suppress)
            {
                return;
            }

            _suppressControlRole = suppress;
            if (!suppress)
            {
                _suppressControlRoleUntil = DateTime.MinValue;
            }
            LogTree($"SuppressControlRole suppress={suppress}");
        }

        public void ScheduleRestoreControlRole()
        {
            if (!_suppressControlRole || IsDisposed)
            {
                return;
            }

            if (!_restoreControlRoleTimer.Enabled)
            {
                _restoreControlRoleTimer.Start();
                return;
            }

            _restoreControlRoleTimer.Stop();
            _restoreControlRoleTimer.Start();
        }

        public void SuppressAccessibleText(bool suppress, string reason)
        {
            if (_suppressAccessibleText == suppress)
            {
                return;
            }

            _suppressAccessibleText = suppress;
            if (!suppress)
            {
                _suppressAccessibleTextUntil = DateTime.MinValue;
            }
            LogTree($"SuppressAccessibleText suppress={suppress} reason={reason}");
        }

        public void ScheduleRestoreAccessibleText(string reason)
        {
            if (!_suppressAccessibleText || IsDisposed)
            {
                return;
            }

            if (!_restoreAccessibleTextTimer.Enabled)
            {
                _restoreAccessibleTextTimer.Start();
                LogTree($"RestoreAccessibleText scheduled reason={reason}");
                return;
            }

            _restoreAccessibleTextTimer.Stop();
            _restoreAccessibleTextTimer.Start();
            LogTree($"RestoreAccessibleText rescheduled reason={reason}");
        }

        public void SuppressNavigationA11y(string reason, int durationMs)
        {
            if (IsDisposed)
            {
                return;
            }

            if (durationMs <= 0)
            {
                durationMs = Math.Max(AccessibleTextRestoreDelayMs, ControlRoleRestoreDelayMs);
            }

            SuppressAccessibleText(true, reason);
            SuppressControlRole(true);
            ExtendAccessibleTextSuppression(durationMs);
            ExtendControlRoleSuppression(durationMs);
        }

        private void ExtendControlRoleSuppression(int durationMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(durationMs);
            if (until > _suppressControlRoleUntil)
            {
                _suppressControlRoleUntil = until;
            }

            if (!_restoreControlRoleTimer.Enabled)
            {
                _restoreControlRoleTimer.Start();
                return;
            }

            _restoreControlRoleTimer.Stop();
            _restoreControlRoleTimer.Start();
        }

        private void ExtendAccessibleTextSuppression(int durationMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(durationMs);
            if (until > _suppressAccessibleTextUntil)
            {
                _suppressAccessibleTextUntil = until;
            }

            if (!_restoreAccessibleTextTimer.Enabled)
            {
                _restoreAccessibleTextTimer.Start();
                return;
            }

            _restoreAccessibleTextTimer.Stop();
            _restoreAccessibleTextTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _restoreControlRoleTimer.Dispose();
                _restoreAccessibleTextTimer.Dispose();
                _accessibilityRefreshTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_DESTROY:
                    LogTree("WM_DESTROY");
                    break;
                case WM_SETFOCUS:
                    LogTree($"WM_SETFOCUS focused={ContainsFocus}");
                    break;
                case WM_KILLFOCUS:
                    LogTree("WM_KILLFOCUS");
                    break;
                case WM_ENABLE:
                    LogTree($"WM_ENABLE enabled={(m.WParam != IntPtr.Zero)}");
                    break;
                case WM_SHOWWINDOW:
                    LogTree($"WM_SHOWWINDOW show={(m.WParam != IntPtr.Zero)}");
                    break;
                case WM_GETOBJECT:
                    LogTree($"WM_GETOBJECT objId=0x{m.LParam.ToInt64():X}");
                    break;
                case TVM_SELECTITEM:
                    LogTree($"TVM_SELECTITEM wParam=0x{m.WParam.ToInt64():X} {DescribeNode(m.LParam)}");
                    break;
                case TVM_EXPAND:
                    LogTree($"TVM_EXPAND wParam=0x{m.WParam.ToInt64():X} {DescribeNode(m.LParam)}");
                    break;
                case TVM_GETITEMA:
                    if (LogNativeItemRequests)
                    {
                        LogTree(DescribeGetItemRequest(m.LParam, unicode: false));
                    }
                    break;
                case TVM_GETITEMW:
                    if (LogNativeItemRequests)
                    {
                        LogTree(DescribeGetItemRequest(m.LParam, unicode: true));
                    }
                    break;
            }

            if (m.Msg == WM_GETOBJECT)
            {
                int objId = unchecked((int)m.LParam.ToInt64());
                if (objId == UiaRootObjectId)
                {
                    try
                    {
                        var acc = AccessibilityObject;
                        if (acc is IRawElementProviderSimple provider)
                        {
                            m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, provider);
                            LogTree("WM_GETOBJECT override UIA (direct)");
                            return;
                        }

                        if (acc is IAccessible iacc)
                        {
                            int hr = UiaProviderFromIAccessible(iacc, 0, UIA_PFIA_DEFAULT, out var msaaProvider);
                            if (hr >= 0 && msaaProvider != null)
                            {
                                m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, msaaProvider);
                                LogTree($"WM_GETOBJECT override UIA (from MSAA) hr=0x{hr:X}");
                                return;
                            }

                            LogTree($"WM_GETOBJECT UIA provider failed hr=0x{hr:X} providerNull={msaaProvider == null}");
                        }
                        else
                        {
                            string accType = acc?.GetType().Name ?? "null";
                            LogTree($"WM_GETOBJECT UIA no IAccessible accType={accType}");
                        }
                    }
                    catch
                    {
                    }
                }
                else if (objId == OBJID_CLIENT)
                {
                    try
                    {
                        var acc = AccessibilityObject;
                        if (acc != null)
                        {
                            IntPtr unk = Marshal.GetIUnknownForObject(acc);
                            try
                            {
                                Guid iid = IID_IAccessible;
                                m.Result = LresultFromObject(ref iid, m.WParam, unk);
                                LogTree("WM_GETOBJECT override OBJID_CLIENT");
                                return;
                            }
                            finally
                            {
                                Marshal.Release(unk);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            base.WndProc(ref m);

            if (m.Msg == TVM_GETITEMW)
            {
                TryOverrideNativeItemText(m.LParam, unicode: true);
            }
            else if (m.Msg == TVM_GETITEMA)
            {
                TryOverrideNativeItemText(m.LParam, unicode: false);
            }

            if (LogNativeItemRequests && m.Msg == TVM_GETITEMW)
            {
                LogGetItemResult(m.LParam, unicode: true);
            }
            else if (LogNativeItemRequests && m.Msg == TVM_GETITEMA)
            {
                LogGetItemResult(m.LParam, unicode: false);
            }

            if (m.Msg == WM_DESTROY)
            {
                LogTree("WM_DESTROY");
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            var inner = base.CreateAccessibilityInstance();
            return new CommentTreeViewAccessibleObject(this, inner);
        }


        [Conditional("DEBUG")]
        private void LogTree(string message)
        {
            DebugLogger.Log(DebugLogger.LogLevel.Info, "TreeView", message);
        }

        private string DescribeNode(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return "node=null";
            }

            try
            {
                TreeNode? node = TreeNode.FromHandle(this, handle);
                if (node == null)
                {
                    return $"nodeHandle=0x{handle.ToInt64():X}";
                }

                var tag = node.Tag as CommentNodeTag;
                string id = tag?.CommentId ?? "null";
                return $"level={node.Level} id={id}";
            }
            catch
            {
                return $"nodeHandle=0x{handle.ToInt64():X}";
            }
        }

        private string DescribeGetItemRequest(IntPtr lParam, bool unicode)
        {
            if (lParam == IntPtr.Zero)
            {
                return "TVM_GETITEMW lParam=null";
            }

            try
            {
                var item = Marshal.PtrToStructure<TVITEM>(lParam);
                bool wantsText = (item.mask & TVIF_TEXT) != 0;
                string kind = unicode ? "TVM_GETITEMW" : "TVM_GETITEMA";
                return $"{kind} req handle=0x{item.hItem.ToInt64():X} wantsText={wantsText} cch={item.cchTextMax}";
            }
            catch
            {
                return "TVM_GETITEMW req (parse failed)";
            }
        }

        private void LogGetItemResult(IntPtr lParam, bool unicode)
        {
            if (lParam == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var item = Marshal.PtrToStructure<TVITEM>(lParam);
                bool wantsText = (item.mask & TVIF_TEXT) != 0;
                if (!wantsText || item.pszText == IntPtr.Zero || item.cchTextMax <= 0)
                {
                    return;
                }

                string raw = ReadBuffer(item.pszText, item.cchTextMax, unicode);
                string text = TrimPreview(raw, 60);
                TreeNode? node = null;
                try
                {
                    node = TreeNode.FromHandle(this, item.hItem);
                }
                catch
                {
                }

                string kind = unicode ? "TVM_GETITEMW" : "TVM_GETITEMA";
                if (node == null)
                {
                    LogTree($"{kind} res handle=0x{item.hItem.ToInt64():X} node=null text='{text}'");
                    return;
                }

                var tag = node.Tag as CommentNodeTag;
                string id = tag?.CommentId ?? "null";
                string nodeText = node.Text ?? string.Empty;
                string nodePreview = TrimPreview(nodeText, 60);
                LogTree($"{kind} res handle=0x{item.hItem.ToInt64():X} id={id} level={node.Level} text='{text}' nodeText='{nodePreview}'");
                if (!string.Equals(nodeText, raw, StringComparison.Ordinal))
                {
                    LogTree($"{kind} mismatch handle=0x{item.hItem.ToInt64():X} id={id}");
                }
            }
            catch
            {
            }
        }

        private void TryOverrideNativeItemText(IntPtr lParam, bool unicode)
        {
            if (lParam == IntPtr.Zero || DisplayTextResolver == null)
            {
                return;
            }

            try
            {
                var item = Marshal.PtrToStructure<TVITEM>(lParam);
                if ((item.mask & TVIF_TEXT) == 0 || item.pszText == IntPtr.Zero || item.cchTextMax <= 0)
                {
                    return;
                }

                TreeNode? node = null;
                try
                {
                    node = TreeNode.FromHandle(this, item.hItem);
                }
                catch
                {
                }

                if (node == null)
                {
                    return;
                }

                string resolved = GetResolvedNodeText(node);
                if (string.IsNullOrEmpty(resolved))
                {
                    return;
                }

                WriteBuffer(item.pszText, item.cchTextMax, unicode, resolved);
            }
            catch
            {
            }
        }

        private static void WriteBuffer(IntPtr dest, int cchTextMax, bool unicode, string text)
        {
            if (dest == IntPtr.Zero || cchTextMax <= 0)
            {
                return;
            }

            int maxChars = Math.Max(0, cchTextMax - 1);
            if (unicode)
            {
                string clipped = text.Length > maxChars ? text.Substring(0, maxChars) : text;
                char[] buffer = new char[cchTextMax];
                if (clipped.Length > 0)
                {
                    clipped.CopyTo(0, buffer, 0, clipped.Length);
                }
                buffer[Math.Min(clipped.Length, cchTextMax - 1)] = '\0';
                Marshal.Copy(buffer, 0, dest, cchTextMax);
                return;
            }

            Encoding encoding = Encoding.Default;
            byte[] bytes = encoding.GetBytes(text);
            if (bytes.Length > maxChars)
            {
                Array.Resize(ref bytes, maxChars);
            }

            byte[] raw = new byte[cchTextMax];
            if (bytes.Length > 0)
            {
                Array.Copy(bytes, raw, bytes.Length);
            }
            raw[Math.Min(bytes.Length, cchTextMax - 1)] = 0;
            Marshal.Copy(raw, 0, dest, cchTextMax);
        }

        private static string ReadBuffer(IntPtr ptr, int maxChars, bool unicode)
        {
            if (ptr == IntPtr.Zero || maxChars <= 0)
            {
                return string.Empty;
            }

            int length = Math.Min(maxChars, 512);
            string raw = unicode
                ? (Marshal.PtrToStringUni(ptr, length) ?? string.Empty)
                : (Marshal.PtrToStringAnsi(ptr, length) ?? string.Empty);
            int nullIndex = raw.IndexOf('\0');
            if (nullIndex >= 0)
            {
                return raw.Substring(0, nullIndex);
            }
            return raw;
        }

        private static string TrimPreview(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > maxLength ? text.Substring(0, maxLength) : text;
        }


        private sealed class CommentTreeViewAccessibleObject : Control.ControlAccessibleObject
        {
            private readonly AccessibleObject _inner;
            private readonly CommentTreeView _owner;
            private readonly bool _logChildren;
            private int _lastChildCount = -1;

            public CommentTreeViewAccessibleObject(CommentTreeView owner, AccessibleObject inner) : base(owner)
            {
                _inner = inner;
                _owner = owner;
                _logChildren = false;
            }

            public override AccessibleRole Role => _owner._suppressControlRole ? AccessibleRole.None : _owner.AccessibleRole;

            public override string? Name
            {
                get => _owner._suppressAccessibleText ? string.Empty : _owner.AccessibleName ?? string.Empty;
                set { }
            }

            public override string Description => _owner._suppressAccessibleText
                ? string.Empty
                : _owner.AccessibleDescription ?? _inner.Description ?? string.Empty;

            public override AccessibleObject Parent => _inner.Parent ?? this;

            public override Rectangle Bounds => _inner.Bounds;

            public override AccessibleStates State => _inner.State;

            public override string? Value
            {
                get => _inner.Value ?? string.Empty;
                set { }
            }

            public override string DefaultAction => _inner.DefaultAction ?? string.Empty;

            public override string KeyboardShortcut => _inner.KeyboardShortcut ?? string.Empty;

            public override string Help => _inner.Help ?? string.Empty;

            public override int GetChildCount()
            {
                int count = _owner.GetVisibleNodeCountOrFallback();
                if (_logChildren && count != _lastChildCount)
                {
                    _lastChildCount = count;
                    _owner.LogTree($"AccChildCount count={count} visibleAll={_owner.CountVisibleNodes(false)} visibleTop={_owner.CountVisibleNodes(true)}");
                }
                return count;
            }

            public override AccessibleObject? GetChild(int index)
            {
                if (index < 0)
                {
                    return null;
                }

                int count = _owner.GetVisibleNodeCountOrFallback();
                int childId = index + 1;
                TreeNode? node = null;
                TreeNode? accNode = _owner.TryGetNodeByAccId(childId);
                if (accNode != null && accNode.IsVisible)
                {
                    node = accNode;
                    if (LogAccIdMapping)
                    {
                        int accIndex = _owner.GetVisibleNodeIndex(accNode);
                        if (accIndex >= 0 && accIndex != index)
                        {
                            _owner.LogTree($"AccChild accIdMapped idx={index} count={count} accId={childId} accIndex={accIndex} accNode={DescribeTreeNode(accNode)}");
                        }
                    }
                }
                else if (accNode != null && LogAccIdMapping)
                {
                    _owner.LogTree($"AccChild accIdInvisible idx={index} count={count} accId={childId} accNode={DescribeTreeNode(accNode)}");
                }

                if (node == null && index < count)
                {
                    node = _owner.GetVisibleNodeByIndex(index, fromTop: false);
                    if (LogAccIdMapping && node != null)
                    {
                        _owner.LogTree($"AccChild fallbackIndex idx={index} count={count} accId={childId} node={DescribeTreeNode(node)}");
                    }
                }

                if (node == null)
                {
                    node = _owner.TryGetNodeByHandle(index);
                }
                if (node == null)
                {
                    return null;
                }

                var child = new FlatTreeNodeAccessibleObject(_owner, this, node);

                if (_logChildren)
                {
                    _owner.LogAccessibleChildMapping(index, child);
                    return new LoggingAccessibleObject(_owner, child, $"root/{index}");
                }

                return child;
            }

            public override AccessibleObject? Navigate(AccessibleNavigation navdir)
            {
                if (navdir == AccessibleNavigation.FirstChild)
                {
                    return GetChild(0);
                }
                if (navdir == AccessibleNavigation.LastChild)
                {
                    int lastIndex = GetChildCount() - 1;
                    return lastIndex >= 0 ? GetChild(lastIndex) : null;
                }
                return null;
            }

            public override AccessibleObject? HitTest(int x, int y)
            {
                try
                {
                    if (_owner.IsHandleCreated)
                    {
                        var client = _owner.PointToClient(new Point(x, y));
                        var node = _owner.GetNodeAt(client);
                        if (node != null)
                        {
                            return new FlatTreeNodeAccessibleObject(_owner, this, node);
                        }
                    }
                }
                catch
                {
                }

                return null;
            }

            public override void Select(AccessibleSelection flags) => _inner.Select(flags);

            public override void DoDefaultAction() => _inner.DoDefaultAction();

            internal void ResetChildrenCache(string reason)
            {
                _owner.LogTree($"AccCacheReset start reason={reason} innerType={_inner.GetType().FullName}");
                int clearedLists = 0;
                int resetFlags = 0;
                try
                {
                    ClearCacheFields(_inner, ref clearedLists, ref resetFlags);
                }
                catch
                {
                }
                _owner.LogTree($"AccCacheReset done reason={reason} clearedLists={clearedLists} resetFlags={resetFlags}");
            }

            private static void ClearCacheFields(object target, ref int clearedLists, ref int resetFlags)
            {
                var type = target.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                while (type != null)
                {
                    foreach (var field in type.GetFields(flags))
                    {
                        string name = field.Name.ToLowerInvariant();
                        if (!name.Contains("child"))
                        {
                            continue;
                        }

                        try
                        {
                            if (field.FieldType == typeof(bool) && name.Contains("valid"))
                            {
                                field.SetValue(target, false);
                                resetFlags++;
                                continue;
                            }

                            if (typeof(System.Collections.IList).IsAssignableFrom(field.FieldType))
                            {
                                var list = field.GetValue(target) as System.Collections.IList;
                                if (list != null)
                                {
                                    list.Clear();
                                }
                                else
                                {
                                    field.SetValue(target, null);
                                }
                                clearedLists++;
                                continue;
                            }

                            if (field.FieldType.IsArray)
                            {
                                field.SetValue(target, null);
                                clearedLists++;
                            }
                        }
                        catch
                        {
                        }
                    }

                    type = type.BaseType;
                }
            }
        }

        private sealed class LoggingAccessibleObject : AccessibleObject
        {
            private static readonly object CacheLock = new();
            private static readonly Dictionary<Type, Func<object, TreeNode?>> NodeAccessorCache = new();
            private readonly CommentTreeView _owner;
            private readonly AccessibleObject _inner;
            private readonly string _path;

            public LoggingAccessibleObject(CommentTreeView owner, AccessibleObject inner, string path)
            {
                _owner = owner;
                _inner = inner;
                _path = path;
            }

            public override string? Name
            {
                get
                {
                    var node = TryGetTreeNode(_inner);
                    if (node != null)
                    {
                        string text = node.Text ?? string.Empty;
                        LogNameAccess(text, "Name");
                        return text;
                    }

                    string? name = _inner.Name ?? string.Empty;
                    LogNameAccess(name ?? string.Empty, "Name");
                    return name;
                }
                set { }
            }

            public override string? Value
            {
                get
                {
                    string? value = _inner.Value ?? string.Empty;
                    LogNameAccess(value ?? string.Empty, "Value");
                    return value;
                }
                set { }
            }

            public override AccessibleRole Role => _inner.Role;
            public override string Description => _inner.Description ?? string.Empty;
            public override AccessibleObject? Parent => _inner.Parent;
            public override Rectangle Bounds => _inner.Bounds;
            public override AccessibleStates State => _inner.State;
            public override string DefaultAction => _inner.DefaultAction ?? string.Empty;
            public override string KeyboardShortcut => _inner.KeyboardShortcut ?? string.Empty;
            public override string Help => _inner.Help ?? string.Empty;
            public override int GetChildCount() => _inner.GetChildCount();

            public override AccessibleObject? GetChild(int index)
            {
                var child = _inner.GetChild(index);
                if (child == null)
                {
                    return null;
                }

                return new LoggingAccessibleObject(_owner, child, $"{_path}/{index}");
            }

            public override AccessibleObject? Navigate(AccessibleNavigation navdir)
            {
                var target = _inner.Navigate(navdir);
                if (target == null)
                {
                    return null;
                }

                return new LoggingAccessibleObject(_owner, target, $"{_path}/nav:{navdir}");
            }

            public override AccessibleObject? HitTest(int x, int y)
            {
                var target = _inner.HitTest(x, y);
                if (target == null)
                {
                    return null;
                }

                return new LoggingAccessibleObject(_owner, target, $"{_path}/hit");
            }

            public override void Select(AccessibleSelection flags) => _inner.Select(flags);
            public override void DoDefaultAction() => _inner.DoDefaultAction();

            private void LogNameAccess(string raw, string source)
            {
                var node = TryGetTreeNode(_inner);
                if (node == null)
                {
                    _owner.LogTree($"Acc{source} path={_path} type={_inner.GetType().Name} node=null name='{TrimPreview(raw, 48)}'");
                    return;
                }

                var tag = node.Tag as CommentNodeTag;
                string id = tag?.CommentId ?? "null";
                string nodeText = node.Text ?? string.Empty;
                bool mismatch = !string.Equals(raw ?? string.Empty, nodeText, StringComparison.Ordinal);
                if (mismatch)
                {
                    _owner.LogTree($"Acc{source} mismatch path={_path} type={_inner.GetType().Name} id={id} level={node.Level} handle=0x{node.Handle.ToInt64():X} name='{TrimPreview(raw ?? string.Empty, 48)}' nodeText='{TrimPreview(nodeText, 48)}'");
                }
            }

            internal static TreeNode? TryGetTreeNode(AccessibleObject obj)
            {
                var type = obj.GetType();
                Func<object, TreeNode?>? accessor = null;
                lock (CacheLock)
                {
                    if (NodeAccessorCache.TryGetValue(type, out var cached) && cached != null)
                    {
                        return cached(obj);
                    }

                    accessor = BuildNodeAccessor(type);
                    NodeAccessorCache[type] = accessor;
                }

                return accessor?.Invoke(obj);
            }

            private static Func<object, TreeNode?> BuildNodeAccessor(Type type)
            {
                try
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (typeof(TreeNode).IsAssignableFrom(field.FieldType))
                        {
                            return obj => field.GetValue(obj) as TreeNode;
                        }
                    }

                    foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (!prop.CanRead || !typeof(TreeNode).IsAssignableFrom(prop.PropertyType))
                        {
                            continue;
                        }

                        return obj =>
                        {
                            try
                            {
                                return prop.GetValue(obj) as TreeNode;
                            }
                            catch
                            {
                                return null;
                            }
                        };
                    }
                }
                catch
                {
                }

                return _ => null;
            }
        }



        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TVITEM
        {
            public uint mask;
            public IntPtr hItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public int iSelectedImage;
            public int cChildren;
            public IntPtr lParam;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVITEM lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("oleacc.dll")]
        private static extern IntPtr LresultFromObject(ref Guid riid, IntPtr wParam, IntPtr punk);

        [DllImport("uiautomationcore.dll")]
        private static extern int UiaProviderFromIAccessible([MarshalAs(UnmanagedType.Interface)] IAccessible accessible, int idChild, int dwFlags, out IRawElementProviderSimple provider);

        private void FlushAccessibilityRefresh()
        {
            string reason = _pendingAccessibilityRefreshReason ?? "unknown";
            _pendingAccessibilityRefreshReason = null;

            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }
            ResetAccessibilityChildCache($"refresh_{reason}");
            LogTree($"AccessibilityRefresh reason={reason}");
        }

        public void ResetAccessibilityChildCache(string reason)
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            if (AccessibilityObject is CommentTreeViewAccessibleObject acc)
            {
                acc.ResetChildrenCache(reason);
            }
        }

        [Conditional("DEBUG")]
        private void LogAccessibleChildMapping(int index, AccessibleObject child)
        {
            try
            {
                var childNode = LoggingAccessibleObject.TryGetTreeNode(child);
                var visibleAll = GetVisibleNodeByIndex(index, fromTop: false);
                var visibleTop = GetVisibleNodeByIndex(index, fromTop: true);
                bool mismatch = !ReferenceEquals(childNode, visibleAll);
                if (childNode == null || visibleAll == null || mismatch)
                {
                    string childDesc = DescribeTreeNode(childNode);
                    string allDesc = DescribeTreeNode(visibleAll);
                    string topDesc = DescribeTreeNode(visibleTop);
                    string selectedDesc = DescribeTreeNode(SelectedNode);
                    string topNodeDesc = DescribeTreeNode(TopNode);
                    int accId = childNode != null ? TryGetAccIdForNode(childNode) : -1;
                    LogTree($"AccChildMap idx={index} accId={accId} type={child.GetType().Name} child={childDesc} visibleAll={allDesc} visibleTop={topDesc} selected={selectedDesc} topNode={topNodeDesc}");
                }
            }
            catch
            {
            }
        }

        private int CountVisibleNodes(bool fromTop)
        {
            TreeNode? node = fromTop ? TopNode : (Nodes.Count > 0 ? Nodes[0] : null);
            int count = 0;
            while (node != null)
            {
                count++;
                node = node.NextVisibleNode;
            }
            return count;
        }

        private TreeNode? GetVisibleNodeByIndex(int index, bool fromTop)
        {
            if (index < 0)
            {
                return null;
            }

            TreeNode? node = fromTop ? TopNode : (Nodes.Count > 0 ? Nodes[0] : null);
            int current = 0;
            while (node != null && current < index)
            {
                node = node.NextVisibleNode;
                current++;
            }
            return current == index ? node : null;
        }

        internal int GetVisibleNodeCountOrFallback()
        {
            if (Nodes.Count == 0)
            {
                return 0;
            }

            try
            {
                return CountVisibleNodes(false);
            }
            catch
            {
                return Nodes.Count;
            }
        }

        private int GetVisibleNodeIndex(TreeNode node)
        {
            if (node == null || Nodes.Count == 0)
            {
                return -1;
            }

            int index = 0;
            TreeNode? current = Nodes[0];
            while (current != null)
            {
                if (ReferenceEquals(current, node))
                {
                    return index;
                }
                current = current.NextVisibleNode;
                index++;
            }

            return -1;
        }

        private static string DescribeTreeNode(TreeNode? node)
        {
            if (node == null)
            {
                return "null";
            }

            var tag = node.Tag as CommentNodeTag;
            string id = tag?.CommentId ?? "null";
            string parentId = (node.Parent?.Tag as CommentNodeTag)?.CommentId ?? "null";
            string handle = node.Handle == IntPtr.Zero ? "0" : $"0x{node.Handle.ToInt64():X}";
            string text = TrimPreview(node.Text ?? string.Empty, 40);
            return $"id={id} parent={parentId} level={node.Level} handle={handle} text='{text}'";
        }

        private TreeNode? TryGetNodeByHandle(int handleValue)
        {
            if (!IsHandleCreated)
            {
                return null;
            }

            try
            {
                return TreeNode.FromHandle(this, new IntPtr(handleValue));
            }
            catch
            {
                return null;
            }
        }

        private TreeNode? TryGetNodeByAccId(int accId)
        {
            if (!IsHandleCreated || accId <= 0)
            {
                return null;
            }

            try
            {
                IntPtr handle = SendMessage(Handle, TVM_MAPACCIDTOHTREEITEM, new IntPtr(accId), IntPtr.Zero);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                return TreeNode.FromHandle(this, handle);
            }
            catch
            {
                return null;
            }
        }

        internal int TryGetAccIdForNode(TreeNode node)
        {
            if (!IsHandleCreated || node == null || node.Handle == IntPtr.Zero)
            {
                return -1;
            }

            try
            {
                IntPtr result = SendMessage(Handle, TVM_MAPHTREEITEMTOACCID, node.Handle, IntPtr.Zero);
                int accId = result.ToInt32();
                return accId > 0 ? accId : -1;
            }
            catch
            {
                return -1;
            }
        }

        private sealed class FlatTreeNodeAccessibleObject : AccessibleObject
        {
            private readonly CommentTreeView _owner;
            private readonly CommentTreeViewAccessibleObject _parent;
            private readonly TreeNode _node;

            public FlatTreeNodeAccessibleObject(CommentTreeView owner, CommentTreeViewAccessibleObject parent, TreeNode node)
            {
                _owner = owner;
                _parent = parent;
                _node = node;
            }

            public override string? Name
            {
                get
                {
                    string name = _owner.GetResolvedNodeText(_node);
                    _owner.LogAccNameForLastVisible("Name", _node, name);
                    return name;
                }
                set { }
            }

            public override string? Value
            {
                get
                {
                    string value = _owner.GetResolvedNodeText(_node);
                    _owner.LogAccNameForLastVisible("Value", _node, value);
                    return value;
                }
                set { }
            }

            public override AccessibleRole Role => AccessibleRole.OutlineItem;

            public override AccessibleObject? Parent => _parent;

            public override Rectangle Bounds
            {
                get
                {
                    if (!_owner.IsHandleCreated)
                    {
                        return Rectangle.Empty;
                    }

                    try
                    {
                        return _owner.RectangleToScreen(_node.Bounds);
                    }
                    catch
                    {
                        return Rectangle.Empty;
                    }
                }
            }

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = AccessibleStates.Focusable | AccessibleStates.Selectable;
                    if (_node.IsSelected)
                    {
                        state |= AccessibleStates.Selected;
                        if (_owner.ContainsFocus)
                        {
                            state |= AccessibleStates.Focused;
                        }
                    }

                    if (_node.Nodes.Count > 0)
                    {
                        state |= _node.IsExpanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed;
                    }

                    if (!_node.IsVisible)
                    {
                        state |= AccessibleStates.Offscreen;
                    }

                    return state;
                }
            }

            public override AccessibleObject? Navigate(AccessibleNavigation navdir)
            {
                TreeNode? target = navdir switch
                {
                    AccessibleNavigation.Next => _node.NextVisibleNode,
                    AccessibleNavigation.Previous => _node.PrevVisibleNode,
                    AccessibleNavigation.FirstChild => null,
                    AccessibleNavigation.LastChild => null,
                    _ => null
                };

                if (target == null)
                {
                    return null;
                }

                return new FlatTreeNodeAccessibleObject(_owner, _parent, target);
            }

            public override int GetChildCount() => 0;

            public override AccessibleObject? GetChild(int index) => null;

            public override void Select(AccessibleSelection flags)
            {
                if (_owner.IsDisposed)
                {
                    return;
                }

                try
                {
                    _owner.SelectedNode = _node;
                    _node.EnsureVisible();
                    if ((flags & AccessibleSelection.TakeFocus) != 0 && !_owner.Focused)
                    {
                        _owner.Focus();
                    }
                }
                catch
                {
                }
            }

            public override void DoDefaultAction()
            {
                if (_node.Nodes.Count == 0)
                {
                    return;
                }

                try
                {
                    if (_node.IsExpanded)
                    {
                        _node.Collapse();
                    }
                    else
                    {
                        _node.Expand();
                    }
                }
                catch
                {
                }
            }
        }

    }
}
