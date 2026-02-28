using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using YTPlayer.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    internal static class MessageBox
    {
        private static Control? _cachedUiInvoker;
        private static int _activeDialogCount;

        internal static bool IsThemedModalDialogActive => Volatile.Read(ref _activeDialogCount) > 0;

        private static bool IsUsableControl(Control? control)
        {
            return control != null && !control.IsDisposed && control.IsHandleCreated;
        }

        private static void CacheUiInvoker(Control control)
        {
            if (IsUsableControl(control))
            {
                _cachedUiInvoker = control;
            }
        }

        private static Control? ResolveUiInvoker(IWin32Window? owner)
        {
            if (owner is Control ownerControl && ownerControl.IsHandleCreated && !ownerControl.IsDisposed)
            {
                CacheUiInvoker(ownerControl);
                return ownerControl;
            }

            try
            {
                var active = Form.ActiveForm;
                if (active != null && active.IsHandleCreated && !active.IsDisposed)
                {
                    CacheUiInvoker(active);
                    return active;
                }

                for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
                {
                    if (Application.OpenForms[i] is Form form &&
                        form.IsHandleCreated &&
                        !form.IsDisposed)
                    {
                        CacheUiInvoker(form);
                        return form;
                    }
                }
            }
            catch
            {
            }

            if (IsUsableControl(_cachedUiInvoker))
            {
                return _cachedUiInvoker;
            }

            return null;
        }

        private static IWin32Window? ResolveOwner(IWin32Window? owner)
        {
            if (owner != null)
            {
                return owner;
            }

            try
            {
                var active = Form.ActiveForm;
                if (active != null && active.IsHandleCreated && active.Visible)
                {
                    return active;
                }

                for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
                {
                    if (Application.OpenForms[i] is Form form &&
                        form.IsHandleCreated &&
                        form.Visible)
                    {
                        return form;
                    }
                }
            }
            catch
            {
            }

            Control? cachedInvoker = _cachedUiInvoker;
            if (cachedInvoker != null &&
                !cachedInvoker.IsDisposed &&
                cachedInvoker.IsHandleCreated &&
                cachedInvoker.Visible)
            {
                return cachedInvoker;
            }

            return null;
        }

        public static DialogResult Show(string text)
        {
            return Show(null, text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(string text, string caption)
        {
            return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        {
            return Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window? owner, string text, string caption)
        {
            return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons)
        {
            return Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(
            IWin32Window? owner,
            string text,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton)
        {
            DialogResult ShowCore()
            {
                ThemeManager.Initialize();
                IWin32Window? resolvedOwner = ResolveOwner(owner);
                TryStopSpeaking();
                Interlocked.Increment(ref _activeDialogCount);
                try
                {
                    using (var dialog = new ThemedMessageBoxForm(text, caption, buttons, icon, defaultButton))
                    {
                        return resolvedOwner == null ? dialog.ShowDialog() : dialog.ShowDialog(resolvedOwner);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeDialogCount);
                }
            }

            Control? invoker = ResolveUiInvoker(owner);
            if (invoker != null && invoker.InvokeRequired)
            {
                try
                {
                    return (DialogResult)invoker.Invoke((Func<DialogResult>)ShowCore);
                }
                catch
                {
                }
            }

            return ShowCore();
        }

        private static void TryStopSpeaking()
        {
            try
            {
                Type? ttsType = Type.GetType("YTPlayer.Utils.TtsHelper");
                MethodInfo? stopMethod = ttsType?.GetMethod("StopSpeaking", BindingFlags.Public | BindingFlags.Static);
                stopMethod?.Invoke(null, null);
            }
            catch
            {
            }
        }
    }
}
