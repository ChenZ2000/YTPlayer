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

        #endregion

        #region 公共方法

        /// <summary>
        /// 朗读文本（自动检测可用的TTS引擎）
        /// </summary>
        /// <param name="text">要朗读的文本</param>
        /// <returns>是否成功朗读</returns>
        public static bool SpeakText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            lock (_lock)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] 尝试朗读文本: '{text}' (当前状态: 已初始化={_ttsInitialized}, 类型={_ttsType})");

                    // 如果已初始化，先尝试使用上次成功的TTS类型
                    if (_ttsInitialized && _ttsType >= 0)
                    {
                        try
                        {
                            if (TrySpeakWithType(_ttsType, text))
                            {
                                return true;
                            }
                            else
                            {
                                // 如果失败，重置初始化状态
                                System.Diagnostics.Debug.WriteLine($"[TTS] TTS type {_ttsType} failed, will retry with all types");
                                _ttsInitialized = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TTS] TTS type {_ttsType} threw exception: {ex.Message}");
                            _ttsInitialized = false;
                        }
                    }

                    // 按顺序尝试所有TTS引擎
                    // 1. 尝试 NVDA
                    if (TryNvda(text))
                    {
                        _ttsType = 0;
                        _ttsInitialized = true;
                        System.Diagnostics.Debug.WriteLine("[TTS] Successfully initialized NVDA");
                        return true;
                    }

                    // 2. 尝试争渡读屏
                    if (TryZdsrapi(text))
                    {
                        _ttsType = 1;
                        _ttsInitialized = true;
                        System.Diagnostics.Debug.WriteLine("[TTS] Successfully initialized 争渡读屏");
                        return true;
                    }

                    // 3. 尝试阳光读屏
                    if (TryBoyCtrl(text))
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

        #endregion

        #region 私有方法

        private static bool TrySpeakWithType(int type, string text)
        {
            try
            {
                switch (type)
                {
                    case 0: // NVDA - 队列模式（不打断当前朗读）
                        return nvdaController_speakText(text) == 0;

                    case 1: // 争渡 - 使用 interrupt=false 队列模式
                        return Speak(text, interrupt: false) == 0;

                    case 2: // 阳光 - 使用 purge=false 队列模式
                        return BoyCtrlSpeak(text, async: false, purge: false, spell: true, IntPtr.Zero) == 0;

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNvda(string text)
        {
            try
            {
                // 检查 DLL 是否存在
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvdaControllerClient.dll");
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

        private static bool TryZdsrapi(string text)
        {
            try
            {
                // 检查 DLL 是否存在
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZDSRAPI.dll");
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

                // ⭐ 朗读文本（使用 interrupt=false 队列模式）
                int speakResult = Speak(text, interrupt: false);
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

        private static bool TryBoyCtrl(string text)
        {
            try
            {
                // 检查 DLL 是否存在
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BoyCtrl.dll");
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
                int speakResult = BoyCtrlSpeak(text, async: false, purge: false, spell: true, IntPtr.Zero);
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
