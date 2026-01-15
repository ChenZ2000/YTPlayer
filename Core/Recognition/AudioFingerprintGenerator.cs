using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace YTPlayer.Core.Recognition
{
    /// <summary>
    /// 基于 ClearScript V8 + afp.js/afp.wasm 的指纹生成器（完全本地，无需 Node）。
    /// </summary>
    internal sealed class AudioFingerprintGenerator : IDisposable
    {
        private bool _disposed;

        public async Task<string> GenerateAsync(byte[] pcmData, int sampleRate, int channels, CancellationToken cancellationToken)
        {
            if (pcmData == null || pcmData.Length == 0)
            {
                throw new ArgumentException("PCM 数据不能为空", nameof(pcmData));
            }

            if (sampleRate != 8000 || channels != 1)
            {
                throw new ArgumentException("指纹生成器要求 8000Hz 单声道 PCM 数据");
            }

            var floats = ConvertPcm16ToFloat(pcmData);

            using var engine = CreateEngine(out var timerHost);
            try
            {
                // ClearScript 会自动将 JS Promise 映射为 Task（EnableTaskPromiseConversion）。
                dynamic generateFp = engine.Script.__generateFpManaged;
                var taskObj = (Task<object>)generateFp(floats);
                var result = await taskObj.ConfigureAwait(false);
                string? output = result?.ToString();
                if (string.IsNullOrWhiteSpace(output))
                {
                    throw new InvalidOperationException("指纹生成返回空结果");
                }

                return output;
            }
            finally
            {
                timerHost?.Dispose();
                engine.CollectGarbage(true);
            }
        }

        private V8ScriptEngine CreateEngine(out JsTimerHost timerHost)
        {
            var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
            timerHost = new JsTimerHost(engine);
            engine.AddHostObject("hostTimers", timerHost);

            // 提供最小 CommonJS/浏览器兼容环境
            engine.Execute(@"
                // base64 polyfill (atob/btoa)
                (function () {
                    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=';
                    if (typeof atob === 'undefined') {
                        globalThis.atob = function (input) {
                            const str = String(input).replace(/=+$/, '');
                            let output = '';
                            for (let bc = 0, bs, buffer, idx = 0; buffer = str.charAt(idx++); ~buffer && (bs = bc % 4 ? bs * 64 + buffer : buffer, bc++ % 4) ? output += String.fromCharCode(255 & bs >> (-2 * bc & 6)) : 0) {
                                buffer = chars.indexOf(buffer);
                            }
                            return output;
                        };
                    }
                    if (typeof btoa === 'undefined') {
                        globalThis.btoa = function (input) {
                            let str = String(input);
                            let output = '';
                            for (let block, charCode, idx = 0, map = chars; str.charAt(idx | 0) || (map = '=', idx % 1); output += map.charAt(63 & block >> 8 - idx % 1 * 8)) {
                                charCode = str.charCodeAt(idx += 3 / 4);
                                if (charCode > 0xFF) throw new Error('btoa: invalid char');
                                block = block << 8 | charCode;
                            }
                            return output;
                        };
                    }
                    // 简单 console stub
                    if (typeof console === 'undefined') {
                        globalThis.console = { log() { }, info() { }, warn() { }, error() { } };
                    }

                    // 计时器 polyfill：使用宿主提供的定时器，保证 afp.js 依赖的 setTimeout/clearTimeout 可用
                    if (typeof setTimeout === 'undefined') {
                        const __pendingTimers = new Map();
                        globalThis.setTimeout = function (cb, ms) {
                            if (typeof cb !== 'function') return -1;
                            const id = hostTimers.GetNextId();
                            __pendingTimers.set(id, true);
                            hostTimers.SetTimeout(id, ms || 0, cb);
                            return id;
                        };
                        globalThis.clearTimeout = function (id) {
                            hostTimers.ClearTimeout(id);
                            __pendingTimers.delete(id);
                        };
                    }
                })();

                const module = { exports: {} };
                const exports = module.exports;
            ");

            var wasmScript = ReadEmbedded("ThirdParty.afp.afp.wasm.js");
            var afpScript = ReadEmbedded("ThirdParty.afp.afp.js");
            string wasmBase64 = ExtractWasmBase64(wasmScript);

            // 注入 require 以处理 afp.js 的依赖
            engine.Script.__afpWasmBinary = wasmBase64;
            engine.Execute(@"
                // Minimal CommonJS style require stub for afp.js
                (function () {
                    function __afpRequire(path) {
                        if (path === './afp.wasm.js') {
                            return { WASM_BINARY: __afpWasmBinary };
                        }
                        if (path && path.indexOf('logger') !== -1) {
                            const noop = function () { };
                            return { info: noop, warn: noop, error: noop, debug: noop, write: noop };
                        }
                        throw new Error('Unsupported require: ' + path);
                    }

                    // Expose both as global property and a var binding to satisfy strict-mode lookups
                    globalThis.require = __afpRequire;
                    var require = globalThis.require;

                    // Ensure module/exports are reachable via globalThis for scripts executed later
                    if (typeof globalThis.module === 'undefined') {
                        globalThis.module = { exports: {} };
                        globalThis.exports = globalThis.module.exports;
                    }
                })();
            ");

            engine.Execute(afpScript);

            // 统一暴露 GenerateFP，包装成可 await 的方法
            engine.Execute(@"
                if (typeof GenerateFP === 'undefined' && typeof exports !== 'undefined') {
                    GenerateFP = exports.GenerateFP;
                }
                if (!GenerateFP) {
                    throw new Error('afp.js 未正确加载，缺少 GenerateFP');
                }
                async function __generateFpManaged(samples) {
                    return await GenerateFP(samples);
                }
            ");

            return engine;
        }

        private static float[] ConvertPcm16ToFloat(byte[] pcm)
        {
            // 防止溢出：使用 BitConverter 解读小端 16-bit 有符号 PCM，并在调试配置下也不会触发 checked 溢出。
            int count = pcm.Length / 2;
            var floats = new float[count];
            for (int sample = 0; sample < count; sample++)
            {
                short v = BitConverter.ToInt16(pcm, sample * 2);
                floats[sample] = v / 32768f;
            }
            return floats;
        }

        private static string ExtractWasmBase64(string wasmJs)
        {
            var match = Regex.Match(wasmJs, @"WASM_BINARY\s*=\s*""(?<b64>[A-Za-z0-9+/=]+)""");
            if (!match.Success)
            {
                throw new InvalidOperationException("无法从 afp.wasm.js 提取 WASM_BINARY");
            }
            return match.Groups["b64"].Value;
        }

        private static string ReadEmbedded(string resourcePath)
        {
            var asm = Assembly.GetExecutingAssembly();
            var fullName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Replace('/', '.').EndsWith(resourcePath.Replace('\\', '.')));

            if (string.IsNullOrEmpty(fullName))
            {
                throw new FileNotFoundException($"找不到嵌入资源 {resourcePath}");
            }

            using var stream = asm.GetManifestResourceStream(fullName);
            if (stream == null)
            {
                throw new FileNotFoundException($"无法读取嵌入资源 {resourcePath}");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>
    /// 简易计时器宿主，为 V8 注入可用的 setTimeout/clearTimeout。
    /// </summary>
    public sealed class JsTimerHost : IDisposable
    {
        private readonly V8ScriptEngine _engine;
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _timers = new();
        private int _nextId;
        private readonly object _engineLock = new();
        private bool _disposed;

        public JsTimerHost(V8ScriptEngine engine)
        {
            _engine = engine;
        }

        public int GetNextId() => Interlocked.Increment(ref _nextId);

        public void SetTimeout(int id, int milliseconds, object callback)
        {
            if (callback is not ScriptObject scriptCallback)
            {
                return;
            }
            if (_disposed)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            if (!_timers.TryAdd(id, cts))
            {
                cts.Dispose();
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(0, milliseconds), cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested || _disposed) return;
                    lock (_engineLock)
                    {
                        if (!_disposed)
                        {
                            scriptCallback.Invoke(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (ObjectDisposedException)
                {
                    // ignore disposed timer token
                }
                finally
                {
                    _timers.TryRemove(id, out _);
                    try
                    {
                        cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // ignore
                    }
                }
            });
        }

        public void ClearTimeout(int id)
        {
            if (_disposed)
            {
                return;
            }
            if (_timers.TryRemove(id, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var kv in _timers)
            {
                try
                {
                    kv.Value.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
                try
                {
                    kv.Value.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
            }
            _timers.Clear();
        }
    }
}
