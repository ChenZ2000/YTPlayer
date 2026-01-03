using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal sealed class SafeTextBox : TextBox
    {
        private bool _autoCompleteDisabled;
        private bool _createHandleRetried;

        internal bool IsAutoCompleteDisabled => _autoCompleteDisabled;

        protected override void CreateHandle()
        {
            try
            {
                base.CreateHandle();
            }
            catch (Win32Exception ex)
            {
                if (_createHandleRetried)
                {
                    DebugLogger.LogException("SafeTextBox", ex,
                        $"CreateHandle failed for {Name ?? "TextBox"}, error={ex.NativeErrorCode}");
                    throw;
                }

                _createHandleRetried = true;
                DebugLogger.LogException("SafeTextBox", ex,
                    $"CreateHandle failed for {Name ?? "TextBox"}, retrying without AutoComplete (error={ex.NativeErrorCode})");
                DisableAutoComplete();
                base.CreateHandle();
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
    }
}
