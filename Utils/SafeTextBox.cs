using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal sealed class SafeTextBox : TextBox
    {
        private bool _autoCompleteDisabled;
        private bool _handleCreatePending;
        private bool _handleCreateInProgress;
        private int _handleCreateAttempts;
        private const int MaxHandleCreateAttempts = 3;
        private const int ErrorInvalidWindowHandle = 1400;

        internal bool IsAutoCompleteDisabled => _autoCompleteDisabled;

        protected override void CreateHandle()
        {
            if (_handleCreateInProgress || IsHandleCreated || IsDisposed)
            {
                return;
            }

            if (Parent == null || !Parent.IsHandleCreated)
            {
                ScheduleHandleCreate("parent-not-ready");
                return;
            }

            try
            {
                _handleCreateInProgress = true;
                base.CreateHandle();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorInvalidWindowHandle)
                {
                    ScheduleHandleCreate("invalid-window-handle");
                    return;
                }

                DebugLogger.LogException("SafeTextBox", ex,
                    $"CreateHandle failed for {Name ?? "TextBox"}, error={ex.NativeErrorCode}");
                DisableAutoComplete();
                ScheduleHandleCreate("create-failed");
            }
            finally
            {
                _handleCreateInProgress = false;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            try
            {
                base.OnHandleCreated(e);
            }
            catch (Exception ex) when (IsAutoCompleteFailure(ex))
            {
                DebugLogger.LogException("SafeTextBox", ex,
                    $"AutoComplete initialization failed for {Name ?? "TextBox"}, disabling");
                DisableAutoComplete();

                try
                {
                    base.OnHandleCreated(e);
                }
                catch (Exception retryEx)
                {
                    DebugLogger.LogException("SafeTextBox", retryEx,
                        $"AutoComplete retry failed for {Name ?? "TextBox"}");
                }
            }
        }

        private void DisableAutoComplete()
        {
            if (_autoCompleteDisabled)
            {
                return;
            }

            _autoCompleteDisabled = true;
            try
            {
                AutoCompleteMode = AutoCompleteMode.None;
                AutoCompleteSource = AutoCompleteSource.None;
                AutoCompleteCustomSource = null;
            }
            catch
            {
                // best effort
            }
        }

        private static bool IsAutoCompleteFailure(Exception ex)
        {
            if (ex is InvalidCastException)
            {
                return ex.Message?.IndexOf("Interface not registered", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (ex is COMException comEx)
            {
                const int REGDB_E_IIDNOTREG = unchecked((int)0x80040155);
                const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
                return comEx.HResult == REGDB_E_IIDNOTREG || comEx.HResult == REGDB_E_CLASSNOTREG;
            }

            return false;
        }

        private void ScheduleHandleCreate(string reason)
        {
            if (_handleCreatePending || IsDisposed || IsHandleCreated)
            {
                return;
            }

            if (_handleCreateAttempts >= MaxHandleCreateAttempts)
            {
                DebugLogger.Log(DebugLogger.LogLevel.Error, "SafeTextBox",
                    $"CreateHandle retries exhausted for {Name ?? "TextBox"} reason={reason}");
                return;
            }

            _handleCreatePending = true;
            _handleCreateAttempts++;

            Control? parent = Parent;
            if (parent == null)
            {
                ParentChanged += SafeTextBox_ParentChanged;
                return;
            }

            void OnParentHandleCreated(object? sender, EventArgs args)
            {
                parent.HandleCreated -= OnParentHandleCreated;
                _handleCreatePending = false;
                TryCreateHandleDeferred(reason);
            }

            if (!parent.IsHandleCreated)
            {
                parent.HandleCreated += OnParentHandleCreated;
                return;
            }

            try
            {
                parent.BeginInvoke((MethodInvoker)(() =>
                {
                    _handleCreatePending = false;
                    TryCreateHandleDeferred(reason);
                }));
            }
            catch
            {
                _handleCreatePending = false;
                TryCreateHandleDeferred(reason);
            }
        }

        private void SafeTextBox_ParentChanged(object? sender, EventArgs e)
        {
            ParentChanged -= SafeTextBox_ParentChanged;
            if (!_handleCreatePending)
            {
                ScheduleHandleCreate("parent-changed");
            }
        }

        private void TryCreateHandleDeferred(string reason)
        {
            if (IsDisposed || IsHandleCreated)
            {
                return;
            }

            if (Parent == null || !Parent.IsHandleCreated)
            {
                ScheduleHandleCreate(reason);
                return;
            }

            try
            {
                _handleCreateInProgress = true;
                base.CreateHandle();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorInvalidWindowHandle)
                {
                    ScheduleHandleCreate("invalid-window-handle");
                    return;
                }

                DebugLogger.LogException("SafeTextBox", ex,
                    $"Deferred CreateHandle failed for {Name ?? "TextBox"}, error={ex.NativeErrorCode}");
                DisableAutoComplete();
                ScheduleHandleCreate("deferred-failed");
            }
            finally
            {
                _handleCreateInProgress = false;
            }
        }
    }
}
