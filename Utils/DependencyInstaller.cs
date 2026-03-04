using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Net.Http;

namespace YTPlayer.Utils
{
    internal sealed class DependencyInstallProgress
    {
        public DependencyInstallProgress(string message, int? percent = null, bool isIndeterminate = false, bool isError = false)
        {
            Message = message ?? string.Empty;
            Percent = percent;
            IsIndeterminate = isIndeterminate;
            IsError = isError;
        }

        public string Message { get; }
        public int? Percent { get; }
        public bool IsIndeterminate { get; }
        public bool IsError { get; }
    }

    internal sealed class DependencyInstallResult
    {
        public DependencyInstallResult(bool success, string? errorMessage, int exitCode = 0, bool requiresRestart = false, string? installRoot = null)
        {
            Success = success;
            ErrorMessage = errorMessage;
            ExitCode = exitCode;
            RequiresRestart = requiresRestart;
            InstallRoot = installRoot;
        }

        public bool Success { get; }
        public string? ErrorMessage { get; }
        public int ExitCode { get; }
        public bool RequiresRestart { get; }
        public string? InstallRoot { get; }
    }

    internal static class DependencyInstaller
    {
        private static readonly string[] DotNetInstallScriptUrls =
        {
            "https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1",
            "https://dotnetcli.azureedge.net/dotnet/scripts/v1/dotnet-install.ps1"
        };
        private static readonly string[] DotNetAssetFeeds =
        {
            "https://dotnetcli.azureedge.net/dotnet",
            "https://builds.dotnet.microsoft.com/dotnet"
        };
        private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private const string DesktopRuntimeName = "Microsoft.WindowsDesktop.App";
        private static readonly string[] AllowedDownloadHostSuffixes =
        {
            "microsoft.com",
            "dot.net",
            "aka.ms",
            "azureedge.net",
            "blob.core.windows.net",
            "visualstudio.com"
        };
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan InstallProcessTimeout = TimeSpan.FromMinutes(20);
        private const int DownloadMaxAttemptsPerUrl = 2;
        private static readonly string InstallerLogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YTPlayer",
            "logs",
            "dependency-installer.log");
        private static readonly object InstallerLogSync = new object();

        public static string GetInstallerLogPath()
        {
            return InstallerLogFilePath;
        }

        public static string GetLocalDotNetRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YTPlayer", "dotnet");
        }

        public static Version? TryGetRequiredDesktopRuntimeVersion(string runtimeConfigPath)
        {
            if (string.IsNullOrWhiteSpace(runtimeConfigPath) || !File.Exists(runtimeConfigPath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(runtimeConfigPath);
                var match = Regex.Match(json,
                    "\"name\"\\s*:\\s*\"Microsoft\\.WindowsDesktop\\.App\".*?\"version\"\\s*:\\s*\"([^\"]+)\"",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success && Version.TryParse(match.Groups[1].Value, out Version? version))
                {
                    return version;
                }
            }
            catch
            {
            }

            return null;
        }

        public static Version? TryGetInstalledDesktopRuntimeVersion(out string? installRoot, string? localRoot = null)
        {
            installRoot = null;
            var candidateRoots = BuildDotNetRootCandidates(includePathEntries: false, localRoot);

            Version? bestVersion = null;
            string? bestRoot = null;
            foreach (string root in candidateRoots)
            {
                if (IsLikelyX86Path(root))
                {
                    continue;
                }

                Version? version = GetDesktopRuntimeVersionFromRoot(root);
                if (version != null && (bestVersion == null || version > bestVersion))
                {
                    bestVersion = version;
                    bestRoot = root;
                }
            }

            Version? registryVersion = GetDesktopRuntimeVersionFromRegistry();
            if (registryVersion != null && (bestVersion == null || registryVersion > bestVersion))
            {
                installRoot = null;
                return registryVersion;
            }

            if (bestVersion != null)
            {
                installRoot = bestRoot;
                return bestVersion;
            }

            return null;
        }

        public static string? FindDotNetExecutable(string? preferredRoot = null)
        {
            foreach (string root in BuildDotNetRootCandidates(includePathEntries: true, preferredRoot))
            {
                if (IsLikelyX86Path(root))
                {
                    continue;
                }
                string candidate = Path.Combine(root, "dotnet.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public static bool TryResolveDotNetHostForDesktopRuntime(
            Version requiredVersion,
            out string? dotNetExe,
            out string? dotNetRoot,
            out Version? foundVersion,
            params string?[] preferredRoots)
        {
            dotNetExe = null;
            dotNetRoot = null;
            foundVersion = null;

            foreach (string root in BuildDotNetRootCandidates(includePathEntries: true, preferredRoots))
            {
                if (IsLikelyX86Path(root))
                {
                    continue;
                }

                string candidate = Path.Combine(root, "dotnet.exe");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (IsDesktopRuntimeAvailable(candidate, requiredVersion, out Version? candidateVersion))
                {
                    dotNetExe = candidate;
                    dotNetRoot = root;
                    foundVersion = candidateVersion;
                    return true;
                }

                if (candidateVersion != null && (foundVersion == null || candidateVersion > foundVersion))
                {
                    foundVersion = candidateVersion;
                }
            }

            return false;
        }

        private static List<string> BuildDotNetRootCandidates(bool includePathEntries, params string?[] preferredRoots)
        {
            var roots = new List<string>();
            var rootSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string? root)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }

                string value = root!.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (rootSet.Add(value))
                {
                    roots.Add(value);
                }
            }

            if (preferredRoots != null)
            {
                foreach (string? preferredRoot in preferredRoots)
                {
                    AddRoot(preferredRoot);
                }
            }

            AddRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT(x64)"));
            AddRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            AddRoot(GetLocalDotNetRoot());

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                AddRoot(Path.Combine(localAppData, "Microsoft", "dotnet"));
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                AddRoot(Path.Combine(programFiles, "dotnet"));
            }

            if (includePathEntries)
            {
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathEnv))
                {
                    foreach (string entry in pathEnv.Split(';'))
                    {
                        AddRoot(entry);
                    }
                }
            }

            return roots;
        }

        public static bool IsDesktopRuntimeAvailable(string dotnetExe, Version requiredVersion, out Version? foundVersion)
        {
            foundVersion = null;
            if (string.IsNullOrWhiteSpace(dotnetExe) || !File.Exists(dotnetExe))
            {
                return false;
            }

            try
            {
                if (!IsX64DotNetHost(dotnetExe))
                {
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = dotnetExe,
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    Version? best = null;
                    foreach (string line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.StartsWith(DesktopRuntimeName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var match = Regex.Match(line, DesktopRuntimeName + "\\s+([0-9\\.]+)\\s+\\[", RegexOptions.IgnoreCase);
                        if (match.Success && Version.TryParse(match.Groups[1].Value, out Version? version))
                        {
                            if (best == null || version > best)
                            {
                                best = version;
                            }
                        }
                    }

                    foundVersion = best;
                    return best != null && best >= requiredVersion;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsX64DotNetHost(string dotnetExe)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = dotnetExe,
                    Arguments = "--info",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (output.IndexOf("Architecture", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        output.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }

                    if (output.IndexOf("RID", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        output.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsLikelyX86Path(string path)
        {
            return path.IndexOf("(x86)", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void ApplyDotNetRootToProcess(string dotnetRoot)
        {
            if (string.IsNullOrWhiteSpace(dotnetRoot))
            {
                return;
            }

            Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
            Environment.SetEnvironmentVariable("DOTNET_ROOT(x64)", dotnetRoot);
        }

        public static async Task<DependencyInstallResult> InstallDotNetDesktopRuntimeAsync(
            Version requiredVersion,
            string? installRoot,
            IProgress<DependencyInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            string channel = $"{requiredVersion.Major}.{requiredVersion.Minor}";
            string tempDir = Path.Combine(Path.GetTempPath(), "YTPlayer", "Installers");
            string scriptPath = Path.Combine(tempDir, "dotnet-install.ps1");
            string effectiveInstallRoot = string.IsNullOrWhiteSpace(installRoot) ? GetLocalDotNetRoot() : installRoot!;

            try
            {
                WriteInstallerLog("============================================================");
                WriteInstallerLog($"开始 .NET 运行时安装，目标版本>={requiredVersion}，channel={channel}，installRoot={effectiveInstallRoot}");

                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(effectiveInstallRoot);
                WriteInstallerLog($"准备目录完成，tempDir={tempDir}");

                progress?.Report(new DependencyInstallProgress("正在获取 .NET 运行时安装脚本...", isIndeterminate: true));
                await DownloadFileFromSourcesAsync(DotNetInstallScriptUrls, scriptPath, ".NET 安装脚本", progress, cancellationToken).ConfigureAwait(false);
                WriteInstallerLog("安装脚本下载成功。");

                bool requiresRestart = false;
                string? localDotNet = FindDotNetExecutable(effectiveInstallRoot);
                if (string.IsNullOrWhiteSpace(localDotNet))
                {
                    var dotNetInstall = await InstallDotNetComponentWithFeedFallbackAsync(
                        scriptPath,
                        channel,
                        effectiveInstallRoot,
                        "dotnet",
                        "核心运行时",
                        progress,
                        tempDir,
                        cancellationToken).ConfigureAwait(false);
                    if (!dotNetInstall.Success)
                    {
                        return new DependencyInstallResult(false, AddInstallerLogHint(".NET 运行时安装失败：" + dotNetInstall.ErrorMessage));
                    }

                    requiresRestart |= dotNetInstall.RequiresRestart;
                }
                else
                {
                    WriteInstallerLog($"检测到本地已有 dotnet host：{localDotNet}");
                }

                Version? existingDesktop = GetDesktopRuntimeVersionFromRoot(effectiveInstallRoot);
                if (existingDesktop == null || existingDesktop < requiredVersion)
                {
                    var desktopInstall = await InstallDotNetComponentWithFeedFallbackAsync(
                        scriptPath,
                        channel,
                        effectiveInstallRoot,
                        "windowsdesktop",
                        "桌面运行时",
                        progress,
                        tempDir,
                        cancellationToken).ConfigureAwait(false);
                    if (!desktopInstall.Success)
                    {
                        return new DependencyInstallResult(false, AddInstallerLogHint(".NET 运行时安装失败：" + desktopInstall.ErrorMessage));
                    }

                    requiresRestart |= desktopInstall.RequiresRestart;
                }
                else
                {
                    WriteInstallerLog($"检测到本地已有 Desktop Runtime 版本：{existingDesktop}");
                }

                string? installedDotNet = FindDotNetExecutable(effectiveInstallRoot);
                if (string.IsNullOrWhiteSpace(installedDotNet))
                {
                    WriteInstallerLog("安装结束校验失败：未找到 dotnet.exe。");
                    return new DependencyInstallResult(false, AddInstallerLogHint("安装后未找到 dotnet.exe。"));
                }

                string resolvedDotNet = installedDotNet!;
                if (!IsDesktopRuntimeAvailable(resolvedDotNet, requiredVersion, out Version? foundVersion))
                {
                    string found = foundVersion == null ? "未检测到 Desktop Runtime" : $"检测到 {foundVersion}";
                    WriteInstallerLog($"安装结束校验失败：{found}，需求>={requiredVersion}，dotnet={resolvedDotNet}");
                    return new DependencyInstallResult(false, AddInstallerLogHint($"安装后运行时校验失败（{found}，需求 >= {requiredVersion}）。"));
                }

                WriteInstallerLog($"安装成功，dotnet={resolvedDotNet}，desktopVersion={foundVersion}");
                return new DependencyInstallResult(true, null, requiresRestart ? 3010 : 0, requiresRestart, effectiveInstallRoot);
            }
            catch (OperationCanceledException)
            {
                WriteInstallerLog("安装被取消。");
                return new DependencyInstallResult(false, AddInstallerLogHint("安装已取消。"));
            }
            catch (Exception ex)
            {
                WriteInstallerLog("安装异常：" + ex);
                return new DependencyInstallResult(false, AddInstallerLogHint(ex.Message ?? "安装失败。"));
            }
        }

        public static async Task<DependencyInstallResult> InstallWebView2RuntimeAsync(
            string downloadDirectory,
            IProgress<DependencyInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            string tempDir = string.IsNullOrWhiteSpace(downloadDirectory)
                ? Path.Combine(Path.GetTempPath(), "YTPlayer", "Installers")
                : downloadDirectory;
            string installerPath = Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe");

            try
            {
                WriteInstallerLog("开始 WebView2 安装。");
                Directory.CreateDirectory(tempDir);

                progress?.Report(new DependencyInstallProgress("正在下载 WebView2 运行时...", isIndeterminate: true));
                await DownloadFileAsync(WebView2BootstrapperUrl, installerPath, progress, cancellationToken).ConfigureAwait(false);

                progress?.Report(new DependencyInstallProgress("正在安装 WebView2 运行时...", isIndeterminate: true));
                ProcessExecutionResult installResult = await RunProcessAsync(
                    installerPath,
                    "/silent /install",
                    tempDir,
                    cancellationToken,
                    InstallProcessTimeout).ConfigureAwait(false);
                int exitCode = installResult.ExitCode;

                bool requiresRestart = exitCode == 3010;
                if (exitCode != 0 && exitCode != 3010)
                {
                    string details = SummarizeProcessFailure(installResult);
                    return new DependencyInstallResult(false, AddInstallerLogHint($"WebView2 安装程序返回错误码 {exitCode}。{details}"), exitCode);
                }

                WriteInstallerLog("WebView2 安装成功。");
                return new DependencyInstallResult(true, null, exitCode, requiresRestart);
            }
            catch (OperationCanceledException)
            {
                WriteInstallerLog("WebView2 安装取消。");
                return new DependencyInstallResult(false, AddInstallerLogHint("安装已取消。"));
            }
            catch (Exception ex)
            {
                WriteInstallerLog("WebView2 安装异常：" + ex);
                return new DependencyInstallResult(false, AddInstallerLogHint(ex.Message ?? "安装失败。"));
            }
        }

        private static Version? GetDesktopRuntimeVersionFromRegistry()
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\" + DesktopRuntimeName))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    Version? best = null;
                    foreach (string name in key.GetValueNames())
                    {
                        if (Version.TryParse(name, out Version? version))
                        {
                            if (best == null || version > best)
                            {
                                best = version;
                            }
                        }
                    }
                    return best;
                }
            }
            catch
            {
                return null;
            }
        }

        private static Version? GetDesktopRuntimeVersionFromRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            string sharedFxRoot = Path.Combine(root, "shared", DesktopRuntimeName);
            if (!Directory.Exists(sharedFxRoot))
            {
                return null;
            }

            Version? best = null;
            foreach (string dir in Directory.GetDirectories(sharedFxRoot))
            {
                string name = Path.GetFileName(dir);
                if (Version.TryParse(name, out Version? version))
                {
                    if (best == null || version > best)
                    {
                        best = version;
                    }
                }
            }

            return best;
        }

        private static async Task<ComponentInstallResult> InstallDotNetComponentWithFeedFallbackAsync(
            string scriptPath,
            string channel,
            string installRoot,
            string runtimeName,
            string displayName,
            IProgress<DependencyInstallProgress>? progress,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var failures = new List<string>();
            for (int i = 0; i < DotNetAssetFeeds.Length; i++)
            {
                string feed = DotNetAssetFeeds[i];
                progress?.Report(new DependencyInstallProgress(
                    $"正在下载并安装 .NET {displayName}（源 {i + 1}/{DotNetAssetFeeds.Length}）...",
                    isIndeterminate: true));
                WriteInstallerLog($"开始安装组件 {runtimeName}，源={feed}");

                string installArgs = BuildDotNetInstallArguments(scriptPath, channel, installRoot, feed, runtimeName);
                ProcessExecutionResult installResult;
                try
                {
                    installResult = await RunProcessAsync(
                        "powershell",
                        installArgs,
                        workingDirectory,
                        cancellationToken,
                        InstallProcessTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    string timeoutMessage = $"源 {feed} 安装超时：{ex.Message}";
                    failures.Add(timeoutMessage);
                    WriteInstallerLog(timeoutMessage);
                    continue;
                }

                bool requiresRestart = installResult.ExitCode == 3010;
                if (installResult.ExitCode == 0 || installResult.ExitCode == 3010)
                {
                    WriteInstallerLog($"组件 {runtimeName} 安装成功，源={feed}，exitCode={installResult.ExitCode}");
                    return new ComponentInstallResult(true, requiresRestart, null);
                }

                string failMessage = $"源 {feed} 安装失败：{SummarizeProcessFailure(installResult)}";
                failures.Add(failMessage);
                WriteInstallerLog(failMessage);
            }

            return new ComponentInstallResult(false, false,
                failures.Count == 0 ? $"组件 {runtimeName} 未获得有效安装结果。" : string.Join(" | ", failures));
        }

        private static string BuildDotNetInstallArguments(
            string scriptPath,
            string channel,
            string installRoot,
            string azureFeed,
            string runtimeName)
        {
            return
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Channel {channel} -Architecture x64 -Runtime {runtimeName} -InstallDir \"{installRoot}\" -Quality GA -AzureFeed \"{azureFeed}\"";
        }

        private static string AddInstallerLogHint(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return $"请查看安装日志：{InstallerLogFilePath}";
            }

            return $"{message}\r\n安装日志：{InstallerLogFilePath}";
        }

        private static string SummarizeProcessFailure(ProcessExecutionResult result)
        {
            string output = TailText(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError, 320);
            if (string.IsNullOrWhiteSpace(output))
            {
                return $"进程退出码 {result.ExitCode}。";
            }

            return $"进程退出码 {result.ExitCode}，输出：{CollapseWhitespace(output)}";
        }

        private static string TailText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = (value ?? string.Empty).Trim();
            if (text.Length <= maxLength)
            {
                return text;
            }

            return "..." + text.Substring(text.Length - maxLength, maxLength);
        }

        private static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return Regex.Replace(text, "\\s+", " ").Trim();
        }

        private static Task DownloadFileAsync(string url, string destination, IProgress<DependencyInstallProgress>? progress, CancellationToken cancellationToken)
        {
            return DownloadFileFromSourcesAsync(new[] { url }, destination, "依赖文件", progress, cancellationToken);
        }

        private static async Task DownloadFileFromSourcesAsync(
            IReadOnlyList<string> sourceUrls,
            string destination,
            string payloadName,
            IProgress<DependencyInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (sourceUrls == null || sourceUrls.Count == 0)
            {
                throw new ArgumentException("下载源为空。", nameof(sourceUrls));
            }

            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException("目标路径为空。", nameof(destination));
            }

            string? folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var failures = new List<string>();
            for (int sourceIndex = 0; sourceIndex < sourceUrls.Count; sourceIndex++)
            {
                string sourceUrl = sourceUrls[sourceIndex];
                for (int attempt = 1; attempt <= DownloadMaxAttemptsPerUrl; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteInstallerLog($"下载开始：payload={payloadName}，source={sourceUrl}，attempt={attempt}/{DownloadMaxAttemptsPerUrl}，target={destination}");
                    bool showAttempt = sourceUrls.Count > 1 || DownloadMaxAttemptsPerUrl > 1;
                    if (showAttempt)
                    {
                        progress?.Report(new DependencyInstallProgress(
                            $"正在下载{payloadName}（源 {sourceIndex + 1}/{sourceUrls.Count}，第 {attempt}/{DownloadMaxAttemptsPerUrl} 次）...",
                            isIndeterminate: true));
                    }

                    try
                    {
                        await DownloadFileFromUrlAsync(sourceUrl, destination, progress, cancellationToken).ConfigureAwait(false);
                        WriteInstallerLog($"下载成功：payload={payloadName}，source={sourceUrl}，attempt={attempt}");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        TryDeleteFile(destination);
                        failures.Add($"[{sourceUrl}] 第 {attempt} 次：{ex.Message}");
                        WriteInstallerLog($"下载失败：payload={payloadName}，source={sourceUrl}，attempt={attempt}，error={ex.Message}");
                        bool hasMoreAttempts =
                            sourceIndex < sourceUrls.Count - 1 || attempt < DownloadMaxAttemptsPerUrl;
                        if (hasMoreAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(6, attempt * 2)), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            throw new InvalidOperationException($"{payloadName} 下载失败。{string.Join(" | ", failures)}");
        }

        private static async Task DownloadFileFromUrlAsync(
            string url,
            string destination,
            IProgress<DependencyInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (!IsAllowedDownloadUrl(url, out Uri? sourceUri))
            {
                throw new InvalidOperationException("下载地址不是受信任的微软官方 HTTPS 地址。");
            }
            Uri trustedSourceUri = sourceUri!;

#if NET48
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif

            using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
            using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(DownloadTimeout);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, trustedSourceUri))
                    {
                        request.Headers.UserAgent.ParseAdd("YTPlayer-Installer");
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();

                            Uri? finalUri = response.RequestMessage?.RequestUri ?? trustedSourceUri;
                            if (finalUri == null || !IsAllowedDownloadHost(finalUri))
                            {
                                throw new InvalidOperationException("下载跳转到了非受信任站点，已拒绝。");
                            }
                            WriteInstallerLog($"下载响应：source={trustedSourceUri}，final={finalUri}，status={(int)response.StatusCode}，length={response.Content.Headers.ContentLength}");

                            long? total = response.Content.Headers.ContentLength;
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                            {
                                byte[] buffer = new byte[81920];
                                long readTotal = 0;
                                while (true)
                                {
                                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token).ConfigureAwait(false);
                                    if (read <= 0)
                                    {
                                        break;
                                    }

                                    await file.WriteAsync(buffer, 0, read, timeoutCts.Token).ConfigureAwait(false);
                                    readTotal += read;
                                    if (total.HasValue && total.Value > 0)
                                    {
                                        int percent = (int)Math.Min(100, Math.Max(0, readTotal * 100 / total.Value));
                                        progress?.Report(new DependencyInstallProgress("正在下载依赖文件...", percent));
                                    }
                                }

                                if (readTotal <= 0)
                                {
                                    throw new InvalidOperationException("下载内容为空。");
                                }

                                WriteInstallerLog($"下载写入完成：final={finalUri}，bytes={readTotal}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    WriteInstallerLog($"下载超时：source={trustedSourceUri}，timeout={DownloadTimeout}");
                    throw new TimeoutException($"下载超时（超过 {DownloadTimeout.TotalMinutes:0} 分钟）。");
                }
            }
        }

        private static bool IsAllowedDownloadUrl(string url, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUri))
            {
                return false;
            }

            if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!IsAllowedDownloadHost(parsedUri))
            {
                return false;
            }

            uri = parsedUri;
            return true;
        }

        private static bool IsAllowedDownloadHost(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return false;
            }

            string host = uri.Host;
            foreach (string suffix in AllowedDownloadHostSuffixes)
            {
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static async Task<ProcessExecutionResult> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("fileName");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                WriteInstallerLog($"启动进程：{fileName} {arguments}");
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法启动安装进程。");
                }

                Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                    {
                        linkedCts.CancelAfter(timeout.Value);
                    }

                    using (linkedCts.Token.Register(() =>
                           {
                               try
                               {
                                   if (!process.HasExited)
                                   {
                                       process.Kill();
                                   }
                               }
                               catch
                               {
                               }
                           }))
                    {
                        try
                        {
                            await Task.Run(() => process.WaitForExit(), linkedCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException(timeout.HasValue
                                ? $"安装进程执行超时（超过 {timeout.Value.TotalMinutes:0} 分钟）。"
                                : "安装进程执行超时。");
                        }
                    }
                }

                string stdOut = await stdOutTask.ConfigureAwait(false);
                string stdErr = await stdErrTask.ConfigureAwait(false);
                WriteInstallerLog($"进程结束：{fileName}，exitCode={process.ExitCode}，stdout={TailText(stdOut, 240)}，stderr={TailText(stdErr, 240)}");
                return new ProcessExecutionResult(process.ExitCode, stdOut, stdErr);
            }
        }

        private static void WriteInstallerLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                string? dir = Path.GetDirectoryName(InstallerLogFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                lock (InstallerLogSync)
                {
                    File.AppendAllText(InstallerLogFilePath, line);
                }
            }
            catch
            {
            }
        }

        private sealed class ProcessExecutionResult
        {
            public ProcessExecutionResult(int exitCode, string standardOutput, string standardError)
            {
                ExitCode = exitCode;
                StandardOutput = standardOutput ?? string.Empty;
                StandardError = standardError ?? string.Empty;
            }

            public int ExitCode { get; }
            public string StandardOutput { get; }
            public string StandardError { get; }
        }

        private sealed class ComponentInstallResult
        {
            public ComponentInstallResult(bool success, bool requiresRestart, string? errorMessage)
            {
                Success = success;
                RequiresRestart = requiresRestart;
                ErrorMessage = errorMessage;
            }

            public bool Success { get; }
            public bool RequiresRestart { get; }
            public string? ErrorMessage { get; }
        }
    }
}
