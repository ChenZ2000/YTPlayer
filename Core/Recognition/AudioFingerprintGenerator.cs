using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript.V8;

namespace YTPlayer.Core.Recognition
{
    /// <summary>
    /// 基于 ClearScript V8 + afp.js/afp.wasm 的指纹生成器（完全本地，无需 Node）。
    /// </summary>
    internal sealed class AudioFingerprintGenerator : IAudioFingerprintGenerator, IDisposable
    {
        // This shipped module has a fixed 16 MiB memory maximum. Runtime probes fail
        // for long cumulative input; overlapping windows keep allocation bounded.
        internal const int MaximumWindowSeconds = 9;
        private bool _disposed;

        static AudioFingerprintGenerator()
        {
            // The bundled V8 build can abort in concurrent code/GC work during repeated
            // WASM sessions on Windows. Recognition does not need background compilation.
            V8Settings.GlobalFlags |= V8GlobalFlags.DisableBackgroundWork;
        }

        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<string> GenerateAsync(byte[] pcmData, int sampleRate, int channels, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (pcmData == null || pcmData.Length == 0 || pcmData.Length % 2 != 0)
                throw new ArgumentException("PCM 数据必须是完整的 16 位采样。", nameof(pcmData));
            if (sampleRate != 8000 || channels != 1)
                throw new ArgumentException("指纹生成器要求 8000Hz 单声道 PCM 数据。");
            if (pcmData.Length > MaximumWindowSeconds * 16000)
                throw new ArgumentException("单次指纹最多支持 9 秒音频，请使用连续重叠窗口。", nameof(pcmData));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    return await Task.Run(async () =>
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        using var engine = CreateEngine();
                        using var interrupt = deadline.Token.Register(() => { try { engine.Interrupt(); } catch (ObjectDisposedException) { } });
                        dynamic generate = engine.Script.__generateFpManaged;
                        var pending = (Task<object>)generate(Convert.ToBase64String(pcmData));
                        var result = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        string output = result?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("指纹生成返回空结果。");
                        _ = Convert.FromBase64String(output);
                        return output;
                    }, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                { throw new OperationCanceledException(cancellationToken); }
                catch (Exception) when (deadline.IsCancellationRequested)
                { throw new TimeoutException("音频指纹生成超时，请重试。"); }
            }
            finally { _gate.Release(); }
        }

        private V8ScriptEngine CreateEngine()
        {
            var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
            try
            {

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
                    // Resolve WASM readiness directly. The upstream polling timer neither
                    // stopped reliably nor rejected initialization failures.
                    instantiateRuntime = function() {
                        return new Promise((resolve, reject) => {
                            const runtime = AudioFingerprintRuntime();
                            runtime.onRuntimeInitialized = () => resolve(runtime);
                            if (typeof runtime.ExtractQueryFP === 'function') resolve(runtime);
                        });
                    };
                    async function __generateFpManaged(pcmBase64) {
                        const runtime = await instantiateRuntime();
                        const bytesIn = globalThis.b64decode(pcmBase64);
                        const samples = new Float32Array(bytesIn.length / 2);
                        for (let i = 0; i < samples.length; i++) {
                            const value = bytesIn[i * 2] | (bytesIn[i * 2 + 1] << 8);
                            samples[i] = (value >= 32768 ? value - 65536 : value) / 32768;
                        }
                        const vector = runtime.ExtractQueryFP(samples.buffer);
                        try {
                            const bytes = new Uint8Array(vector.size());
                            for (let i = 0; i < bytes.length; i++) bytes[i] = vector.get(i);
                            return globalThis.b64encode(bytes);
                        } finally { vector.delete(); }
                    }
                ");

                return engine;
            }
            catch { engine.Dispose(); throw; }
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

}
