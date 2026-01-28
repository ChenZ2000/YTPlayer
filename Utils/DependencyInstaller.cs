using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
#if NET48
using System.Net;
#else
using System.Net.Http;
#endif

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
        private const string DotNetInstallScriptUrl = "https://dot.net/v1/dotnet-install.ps1";
        private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private const string DesktopRuntimeName = "Microsoft.WindowsDesktop.App";

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

            var candidateRoots = new List<string>();
            var rootSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string? root)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }
                string value = root!;
                if (rootSet.Add(value))
                {
                    candidateRoots.Add(value);
                }
            }

            AddRoot(localRoot);
            AddRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT(x64)"));
            AddRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT"));

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

            Version? bestVersion = null;
            string? bestRoot = null;
            foreach (string root in candidateRoots)
            {
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
            var roots = new List<string>();
            var rootSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string? root)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }
                string value = root!;
                if (rootSet.Add(value))
                {
                    roots.Add(value);
                }
            }

            AddRoot(preferredRoot);
            AddRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT(x64)"));
            AddRoot(Environment.GetEnvironmentVariable("DOTNET_ROOT"));

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                AddRoot(Path.Combine(programFiles, "dotnet"));
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                AddRoot(Path.Combine(localAppData, "Microsoft", "dotnet"));
            }

            foreach (string root in roots)
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

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (string entry in pathEnv.Split(';'))
                {
                    string trimmed = entry.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }
                    if (IsLikelyX86Path(trimmed))
                    {
                        continue;
                    }
                    string candidate = Path.Combine(trimmed, "dotnet.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
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
            bool useCustomInstallRoot = !string.IsNullOrWhiteSpace(installRoot);

            try
            {
                Directory.CreateDirectory(tempDir);
                if (useCustomInstallRoot)
                {
                    Directory.CreateDirectory(installRoot!);
                }

                progress?.Report(new DependencyInstallProgress("正在获取 .NET 运行时安装脚本...", isIndeterminate: true));
                await DownloadFileAsync(DotNetInstallScriptUrl, scriptPath, progress, cancellationToken).ConfigureAwait(false);

                progress?.Report(new DependencyInstallProgress("正在下载并安装 .NET 运行时组件（核心）...", isIndeterminate: true));

                string baseArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Channel {channel} -Architecture x64";
                if (useCustomInstallRoot)
                {
                    baseArgs += $" -InstallDir \"{installRoot}\"";
                }
                string dotnetArgs = baseArgs + " -Runtime dotnet";
                int exitCode = await RunProcessAsync("powershell", dotnetArgs, tempDir, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0 && exitCode != 3010)
                {
                    return new DependencyInstallResult(false, $"安装程序返回错误码 {exitCode}", exitCode);
                }

                progress?.Report(new DependencyInstallProgress("正在下载并安装 .NET 运行时组件（桌面）...", isIndeterminate: true));

                string desktopArgs = baseArgs + " -Runtime windowsdesktop";
                exitCode = await RunProcessAsync("powershell", desktopArgs, tempDir, cancellationToken).ConfigureAwait(false);

                bool requiresRestart = exitCode == 3010;
                if (exitCode != 0 && exitCode != 3010)
                {
                    return new DependencyInstallResult(false, $"安装程序返回错误码 {exitCode}", exitCode);
                }

                if (useCustomInstallRoot)
                {
                    var installedVersion = GetDesktopRuntimeVersionFromRoot(installRoot!);
                    if (installedVersion == null || installedVersion < requiredVersion)
                    {
                        return new DependencyInstallResult(false, "未检测到已安装的 .NET 桌面运行时。", exitCode);
                    }
                }

                return new DependencyInstallResult(true, null, exitCode, requiresRestart, useCustomInstallRoot ? installRoot : null);
            }
            catch (OperationCanceledException)
            {
                return new DependencyInstallResult(false, "安装已取消。");
            }
            catch (Exception ex)
            {
                return new DependencyInstallResult(false, ex.Message ?? "安装失败。");
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
                Directory.CreateDirectory(tempDir);

                progress?.Report(new DependencyInstallProgress("正在下载 WebView2 运行时...", isIndeterminate: true));
                await DownloadFileAsync(WebView2BootstrapperUrl, installerPath, progress, cancellationToken).ConfigureAwait(false);

                progress?.Report(new DependencyInstallProgress("正在安装 WebView2 运行时...", isIndeterminate: true));
                int exitCode = await RunProcessAsync(installerPath, "/silent /install", tempDir, cancellationToken).ConfigureAwait(false);

                bool requiresRestart = exitCode == 3010;
                if (exitCode != 0 && exitCode != 3010)
                {
                    return new DependencyInstallResult(false, $"WebView2 安装程序返回错误码 {exitCode}", exitCode);
                }

                return new DependencyInstallResult(true, null, exitCode, requiresRestart);
            }
            catch (OperationCanceledException)
            {
                return new DependencyInstallResult(false, "安装已取消。");
            }
            catch (Exception ex)
            {
                return new DependencyInstallResult(false, ex.Message ?? "安装失败。");
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

        private static async Task DownloadFileAsync(string url, string destination, IProgress<DependencyInstallProgress>? progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("下载地址为空。", nameof(url));
            }

            string? folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

#if NET48
            using (var client = new WebClient())
            {
                client.Headers.Add("user-agent", "YTPlayer-Installer");
                client.DownloadProgressChanged += (_, e) =>
                {
                    if (e.ProgressPercentage >= 0 && e.ProgressPercentage <= 100)
                    {
                        progress?.Report(new DependencyInstallProgress("正在下载依赖文件...", e.ProgressPercentage));
                    }
                };

                using (cancellationToken.Register(client.CancelAsync))
                {
                    await client.DownloadFileTaskAsync(new Uri(url), destination).ConfigureAwait(false);
                }
            }
#else
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long readTotal = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        readTotal += read;
                        if (total.HasValue && total.Value > 0)
                        {
                            int percent = (int)Math.Min(100, Math.Max(0, readTotal * 100 / total.Value));
                            progress?.Report(new DependencyInstallProgress("正在下载依赖文件...", percent));
                        }
                    }
                }
            }
#endif
        }

        private static async Task<int> RunProcessAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
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
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法启动安装进程。");
                }

                using (cancellationToken.Register(() =>
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
                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                }

                return process.ExitCode;
            }
        }
    }
}
