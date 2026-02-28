using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace YTPlayer.Utils
{
    /// <summary>
    /// TTS helper that prefers BoyPCReader when available, otherwise UIA announcements.
    /// </summary>
    public static class TtsHelper
    {
        private static readonly object _lock = new object();
        private static bool _ttsInitialized = false;
        private static bool _globalInterruptSuppressed = false;

        private static DateTime _lastBoyCheckAt = DateTime.MinValue;
        private static bool _isBoyRunningCached = false;
        private const int BoyCheckIntervalMs = 1000;
        private const string BoyCtrlDllName = "BoyCtrl.dll";
        private const string NvdaControllerDllName64 = "nvdaControllerClient64.dll";

        public static Func<string, bool, bool>? NarratorFallbackSpeaker { get; set; }

        [DllImport("BoyCtrl.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int BoyCtrlInitialize(string? reserved);

        [DllImport("BoyCtrl.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int BoyCtrlSpeak(string text, bool async, bool purge, bool spell, IntPtr reserved);

        [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_cancelSpeech")]
        private static extern int NvdaControllerCancelSpeech();

        [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_testIfRunning")]
        private static extern int NvdaControllerTestIfRunning();

        /// <summary>
        /// Speak text using BoyPCReader when running, otherwise UIA announcements.
        /// </summary>
        public static bool SpeakText(string text, bool interrupt = true, bool suppressGlobalInterrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            lock (_lock)
            {
                try
                {
                    _globalInterruptSuppressed = suppressGlobalInterrupt;

                    if (IsBoyPcReaderRunningCached())
                    {
                        if (TryBoyCtrl(text, interrupt, asyncMode: false))
                        {
                            _ttsInitialized = true;
                            return true;
                        }
                    }

                    if (NarratorFallbackSpeaker == null)
                    {
                        _ttsInitialized = false;
                        return false;
                    }

                    bool result = NarratorFallbackSpeaker(text, interrupt);
                    _ttsInitialized = result;
                    return result;
                }
                catch
                {
                    _ttsInitialized = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// Stop speaking. Prefer NVDA controller cancel when available, then BoyCtrl purge.
        /// </summary>
        public static void StopSpeaking()
        {
            lock (_lock)
            {
                if (_globalInterruptSuppressed)
                {
                    return;
                }

                TryNvdaControllerCancelSpeech();

                if (IsBoyPcReaderRunningCached() && DllExists(BoyCtrlDllName))
                {
                    try
                    {
                        BoyCtrlSpeak(string.Empty, async: false, purge: true, spell: false, IntPtr.Zero);
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Speak with high priority (BoyCtrl async+purge when available).
        /// </summary>
        public static bool SpeakPriorityText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            lock (_lock)
            {
                try
                {
                    if (TryBoyCtrl(text, interrupt: true, asyncMode: true))
                    {
                        _ttsInitialized = true;
                        return true;
                    }

                    if (NarratorFallbackSpeaker == null)
                    {
                        _ttsInitialized = false;
                        return false;
                    }

                    bool result = NarratorFallbackSpeaker(text, true);
                    _ttsInitialized = result;
                    return result;
                }
                catch
                {
                    _ttsInitialized = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// Reset TTS state.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _ttsInitialized = false;
            }
        }

        /// <summary>
        /// Get current engine name.
        /// </summary>
        public static string GetCurrentEngineName()
        {
            lock (_lock)
            {
                if (!_ttsInitialized)
                {
                    return "?????????";
                }

                return IsBoyPcReaderRunningCached() ? "????" : "UIA ??";
            }
        }

        private static bool TryBoyCtrl(string text, bool interrupt, bool asyncMode)
        {
            try
            {
                string dllPath = ResolveDllPath(BoyCtrlDllName);
                if (!File.Exists(dllPath))
                {
                    return false;
                }

                int initResult = BoyCtrlInitialize(null);
                if (initResult != 0)
                {
                    return false;
                }

                int speakResult = BoyCtrlSpeak(text, async: asyncMode, purge: interrupt, spell: true, IntPtr.Zero);
                return speakResult == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNvdaControllerCancelSpeech()
        {
            if (!DllExists(NvdaControllerDllName64))
            {
                return false;
            }

            try
            {
                if (NvdaControllerTestIfRunning() != 0)
                {
                    return false;
                }

                return NvdaControllerCancelSpeech() == 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsBoyPcReaderActive()
        {
            return IsBoyPcReaderRunningCached();
        }

        private static bool IsBoyPcReaderRunningCached()
        {
            DateTime utcNow = DateTime.UtcNow;
            if ((utcNow - _lastBoyCheckAt).TotalMilliseconds >= BoyCheckIntervalMs)
            {
                _isBoyRunningCached = IsBoyPcReaderRunning();
                _lastBoyCheckAt = utcNow;
            }

            return _isBoyRunningCached;
        }

        private static bool IsBoyPcReaderRunning()
        {
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    string name = process.ProcessName ?? string.Empty;
                    if (name.Length == 0)
                    {
                        continue;
                    }
                    if (name.Equals("BoyPCReader", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("BoyPcReader", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("BoyPCReader", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("BoyPcReader", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static string ResolveDllPath(string dllName)
        {
            return PathHelper.ResolveFromLibsOrBase(dllName);
        }

        private static bool DllExists(string dllName)
        {
            try
            {
                return File.Exists(ResolveDllPath(dllName));
            }
            catch
            {
                return false;
            }
        }
    }
}
