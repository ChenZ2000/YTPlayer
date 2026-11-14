using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    // WinForms does not always forward a hidden ancestor's VisibleChanged to its
    // descendants. Observe the chain explicitly so timers and captures stop at once.
    internal sealed class ControlVisibilityObserver : IDisposable
    {
        private readonly Control _owner;
        private readonly Action _changed;
        private readonly List<Control> _ancestors = new List<Control>();
        private bool _disposed;
        internal ControlVisibilityObserver(Control owner, Action changed)
        {
            _owner = owner; _changed = changed;
            owner.ParentChanged += ParentChanged;
            Rebind();
        }
        private void ParentChanged(object? sender, EventArgs e) { Rebind(); _changed(); }
        private void StateChanged(object? sender, EventArgs e) => _changed();
        private void Rebind()
        {
            Detach();
            if (_disposed) return;
            for (Control? parent = _owner.Parent; parent != null; parent = parent.Parent)
            {
                _ancestors.Add(parent);
                parent.VisibleChanged += StateChanged;
                parent.EnabledChanged += StateChanged;
                parent.ParentChanged += ParentChanged;
                if (parent is Form) parent.SizeChanged += StateChanged;
            }
        }
        private void Detach()
        {
            foreach (Control parent in _ancestors)
            {
                parent.VisibleChanged -= StateChanged;
                parent.EnabledChanged -= StateChanged;
                parent.ParentChanged -= ParentChanged;
                if (parent is Form) parent.SizeChanged -= StateChanged;
            }
            _ancestors.Clear();
        }
        public void Dispose()
        {
            _disposed = true;
            _owner.ParentChanged -= ParentChanged;
            Detach();
        }
    }
}
