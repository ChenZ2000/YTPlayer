using System;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    // Only volume-originated saves are coalesced. The timer is stopped while a mouse
    // gesture is active, and at rest, so pointer movement never runs the save callback.
    internal sealed class VolumeSaveScheduler : IDisposable
    {
        private readonly Action _save;
        private readonly Timer _timer;
        private bool _pending;
        private bool _gesture;
        internal VolumeSaveScheduler(Action save)
        {
            _save = save;
            _timer = new Timer { Interval = 300 };
            _timer.Tick += (_, _) => Flush();
        }
        internal void BeginGesture() { _gesture = true; _timer.Stop(); }
        internal void Request()
        {
            _pending = true;
            _timer.Stop();
            if (!_gesture) _timer.Start();
        }
        internal void EndGesture() { _gesture = false; Flush(); }
        internal void Flush()
        {
            _timer.Stop();
            if (!_pending) return;
            _pending = false;
            _save();
        }
        public void Dispose() { _timer.Stop(); _timer.Dispose(); }
    }
}
