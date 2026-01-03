using System;
using System.Windows.Forms;

namespace YTPlayer
{
    internal sealed class AccessibleTrackBar : TrackBar
    {
        public void RaiseNameChanged()
        {
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.NameChange, 0);
            }
            catch
            {
            }
        }

        public void RaiseFocusChanged()
        {
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.Focus, 0);
            }
            catch
            {
            }
        }

        public void RaiseValueChanged()
        {
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, 0);
            }
            catch
            {
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RaiseFocusChanged();
            RaiseValueChanged();
            RaiseNameChanged();
        }
    }
}
