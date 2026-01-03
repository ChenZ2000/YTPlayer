using System;
using System.IO;
using System.Runtime.InteropServices;

namespace YTPlayer.Utils
{
    /// <summary>
    /// TTS（文字转语音）助手类
    /// 支持多种屏幕阅读器：NVDA、争渡读屏、阳光读屏
    /// </summary>
    public static class TtsHelper
    {
        #region P/Invoke 声明

        /// <summary>
        /// NVDA 屏幕阅读器 - 测试是否运行
        /// </summary>
        [DllImport("nvdaControllerClient.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int nvdaController_testIfRunning();

        /// <summary>
        /// NVDA 屏幕阅读器 - 朗读文本
        /// </summary>
        [DllImport("nvdaControllerClient.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int nvdaController_speakText(string text);

        /// <summary>
        /// NVDA 屏幕阅读器 - 取消当前朗读
        /// </summary>
        [DllImport("nvdaControllerClient.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int nvdaController_cancelSpeech();

        /// <summary>
        /// 争渡读屏 - 初始化TTS
        /// </summary>
        [DllImport("ZDSRAPI.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int InitTTS(int iType, string? pszParam, bool bSync);

        /// <summary>
        /// 争渡读屏 - 朗读文本
        /// </summary>
        [DllImport("ZDSRAPI.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int Speak(string text, bool interrupt);

        /// <summary>
        /// 阳光读屏 - 初始化
        /// </summary>
        [DllImport("BoyCtrl.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int BoyCtrlInitialize(string? reserved);

        /// <summary>
        /// 阳光读屏 - 朗读文本
        /// </summary>
        [DllImport("BoyCtrl.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int BoyCtrlSpeak(string text, bool async, bool purge, bool spell, IntPtr reserved);

        #endregion

        #region 字段

        private static bool _ttsInitialized = false;
        private static int _ttsType = -1; // -1: 未初始化, 0: NVDA, 1: 争渡, 2: 阳光
        private static readonly object _lock = new object();
        private static bool _globalInterruptSuppressed = false;
        private const string NvdaDllName = "nvdaControllerClient.dll";
        private const string ZdsrDllName = "ZDSRAPI.dll";
        private const string BoyCtrlDllName = "BoyCtrl.dll";

        #endregion

        #region 公共方法

        /// <summary>
        /// 朗读文本（自动检测可用的TTS引擎）
        /// </summary>
        /// <param name="text">要朗读的文本</param>
        /// <returns>是否成功朗读</returns>
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
                    System.Diagnostics.Debug.WriteLine($"[TTS] 尝试朗读文本: '{text}' (已初始化={_ttsInitialized}, 类型={_ttsType}, interrupt={interrupt}, suppressGlobal={suppressGlobalInterrupt})");

                    if (suppressGlobalInterrupt)
                    {
                        _globalInterruptSuppressed = true;
                    }
                    else
                    {
                        _globalInterruptSuppressed = false;
                    }

                    if (interrupt)
                    {
                        CancelActiveSpeech(_ttsInitialized && _ttsType >= 0 ? _ttsType : null, suppressGlobalInterrupt);
                    }

                    // 如果已初始化，先尝试使用上次成功的TTS类型
                    if (_ttsInitialized && _ttsType >= 0)
                    {
                        try
                        {
                            if (TrySpeakWithType(_ttsType, text, interrupt))
                            {
                                return true;
                            }

                            System.Diagnostics.Debug.WriteLine($"[TTS] TTS type {_ttsType} failed, will retry with all types");
                            _ttsInitialized = false;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TTS] TTS type {_ttsType} threw exception: {ex.Message}");
                            _ttsInitialized = false;
                        }
                    }

                    // 按顺序尝试所有TTS引擎
                    if (TryNvda(text, interrupt))
                    {
                        _ttsType = 0;
                        _ttsInitialized = true;
                        System.Diagnostics.Debug.WriteLine("[TTS] Successfully initialized NVDA");
                        return true;
                    }

                    if (TryZdsrapi(text, interrupt))
                    {
                        _ttsType = 1;
                        _ttsInitialized = true;
                        System.Diagnostics.Debug.WriteLine("[TTS] Successfully initialized 争渡读屏");
                        return true;
                    }

                    if (TryBoyCtrl(text, interrupt))
                    {
                        _ttsType = 2;
                        _ttsInitialized = true;
                        System.Diagnostics.Debug.WriteLine("[TTS] Successfully initialized 阳光读屏");
                        return true;
                    }

                    System.Diagnostics.Debug.WriteLine("[TTS] No TTS engine available");
                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] SpeakText failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 立即停止当前朗读
        /// </summary>
        public static void StopSpeaking()
        {
            lock (_lock)
            {
                if (_globalInterruptSuppressed)
                {
                    return;
                }
                CancelActiveSpeech(_ttsInitialized && _ttsType >= 0 ? _ttsType : null, suppressGlobal: false);
            }
        }

        /// <summary>
        /// 重置TTS引擎（强制重新检测）
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _ttsInitialized = false;
                _ttsType = -1;
                System.Diagnostics.Debug.WriteLine("[TTS] TTS engine reset");
            }
        }

        /// <summary>
        /// 获取当前TTS引擎名称
        /// </summary>
        public static string GetCurrentEngineName()
        {
            lock (_lock)
            {
                if (!_ttsInitialized || _ttsType < 0)
                {
                    return "未检测到屏幕阅读器";
                }

                return _ttsType switch
                {
                    0 => "NVDA",
                    1 => "争渡读屏",
                    2 => "阳光读屏",
                    _ => "未知"
                };
            }
        }

        /// <summary>
        /// 判断 NVDA 是否正在运行
        /// </summary>
        public static bool IsNvdaRunning()
        {
            lock (_lock)
            {
                try
                {
                    if (!DllExists(NvdaDllName))
                    {
                        return false;
                    }
                    return nvdaController_testIfRunning() == 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        #endregion

        #region 私有方法

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

        private static void CancelActiveSpeech(int? preferredType, bool suppressGlobal = false)
        {
            int[] engines;
            if (preferredType.HasValue)
            {
                engines = new[] { preferredType.Value };
            }
            else if (_ttsInitialized && _ttsType >= 0)
            {
                engines = new[] { _ttsType };
            }
            else
            {
                engines = new[] { 0 };
            }

            foreach (var engine in engines)
            {
                try
                {
                    switch (engine)
                    {
                        case 0:
                            if (DllExists(NvdaDllName))
                            {
                                nvdaController_cancelSpeech();
                            }
                            break;
                        case 1:
                            if (DllExists(ZdsrDllName))
                            {
                                Speak(string.Empty, true);
                            }
                            break;
                        case 2:
                            if (DllExists(BoyCtrlDllName))
                            {
                                BoyCtrlSpeak(string.Empty, async: false, purge: true, spell: false, IntPtr.Zero);
                            }
                            break;
                    }
                    }
                    catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] Cancel speech failed for engine {engine}: {ex.Message}");
                }
            }
        }

        private static bool TrySpeakWithType(int type, string text, bool interrupt)
        {
            try
            {
                switch (type)
                {
                    case 0:
                        return TryNvda(text, interrupt);

                    case 1:
                        return TryZdsrapi(text, interrupt);

                    case 2:
                        return TryBoyCtrl(text, interrupt);

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNvda(string text, bool interrupt)
        {
            try
            {
                // 检查 DLL 是否存在
                string dllPath = ResolveDllPath(NvdaDllName);
                if (!File.Exists(dllPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] NVDA DLL not found at: {dllPath}");
                    return false;
                }

                // 检查 NVDA 是否运行
                int testResult = nvdaController_testIfRunning();
                if (testResult != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] NVDA not running (test returned {testResult})");
                    return false;
                }

                if (interrupt)
                {
                    CancelActiveSpeech(0, suppressGlobal: false);
                }

                // ⭐ 队列模式：直接朗读新内容（不打断当前朗读）
                int result = nvdaController_speakText(text);
                if (result == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[TTS] NVDA speak succeeded (queue mode)");
                    return true;
                }
                System.Diagnostics.Debug.WriteLine($"[TTS] NVDA speak failed with code {result}");
                return false;
            }
            catch (DllNotFoundException dllEx)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] NVDA DLL not found: {dllEx.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] NVDA error: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static bool TryZdsrapi(string text, bool interrupt)
        {
            try
            {
                // 检查 DLL 是否存在
                string dllPath = ResolveDllPath(ZdsrDllName);
                if (!File.Exists(dllPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] 争渡 DLL not found at: {dllPath}");
                    return false;
                }

                // 初始化争渡读屏
                int initResult = InitTTS(0, null, bSync: true);
                if (initResult != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] 争渡 init failed with code {initResult}");
                    return false;
                }

                // ⭐ 朗读文本
                int speakResult = Speak(text, interrupt);
                if (speakResult == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[TTS] 争渡 speak succeeded (queue mode)");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine($"[TTS] 争渡 speak failed with code {speakResult}");
                return false;
            }
            catch (DllNotFoundException dllEx)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] 争渡 DLL not found: {dllEx.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] 争渡 error: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static bool TryBoyCtrl(string text, bool interrupt)
        {
            try
            {
                // 检查 DLL 是否存在
                string dllPath = ResolveDllPath(BoyCtrlDllName);
                if (!File.Exists(dllPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] 阳光 DLL not found at: {dllPath}");
                    return false;
                }

                // 初始化阳光读屏
                int initResult = BoyCtrlInitialize(null);
                if (initResult != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] 阳光 init failed with code {initResult}");
                    return false;
                }

                // ⭐ 朗读文本（使用 purge=false 队列模式）
                int speakResult = BoyCtrlSpeak(text, async: false, purge: interrupt, spell: true, IntPtr.Zero);
                if (speakResult == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[TTS] 阳光 speak succeeded (queue mode)");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine($"[TTS] 阳光 speak failed with code {speakResult}");
                return false;
            }
            catch (DllNotFoundException dllEx)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] 阳光 DLL not found: {dllEx.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] 阳光 error: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
