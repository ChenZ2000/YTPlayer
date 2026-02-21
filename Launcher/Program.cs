using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Forms;
using YTPlayer.Utils;

namespace YTPlayer.Launcher
{
    internal static class Program
    {
        private const string LibsFolderName = "libs";
        private const string MainExeName = "YTPlayer.App.exe";
        private const string MainDllName = "YTPlayer.App.dll";
        private const string RootEnvVar = "YTPLAYER_ROOT";
        private static readonly Version FallbackDesktopRuntime = new Version(10, 0, 0);

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ThemeManager.Initialize();

                string rootDir = AppDomain.CurrentDomain.BaseDirectory;
                string libsDir = Path.Combine(rootDir, LibsFolderName);
                string mainExe = Path.Combine(libsDir, MainExeName);
                string mainDll = Path.Combine(libsDir, MainDllName);

                if (!File.Exists(mainExe))
                {
                    ShowError("Main app not found:\r\n" + mainExe);
                    return;
                }

                string runtimeConfigPath = Path.Combine(libsDir, "YTPlayer.App.runtimeconfig.json");
                string depsPath = Path.Combine(libsDir, "YTPlayer.App.deps.json");
                if (!EnsureDotNetDesktopRuntime(runtimeConfigPath, out string? dotNetRoot, out string? dotNetExe))
                {
                    return;
                }

                Environment.SetEnvironmentVariable(RootEnvVar, rootDir);

                var startInfo = BuildLaunchInfo(mainExe, mainDll, runtimeConfigPath, depsPath, dotNetExe, args, rootDir);

                if (!string.IsNullOrWhiteSpace(dotNetRoot))
                {
                    startInfo.EnvironmentVariables["DOTNET_ROOT"] = dotNetRoot;
                    startInfo.EnvironmentVariables["DOTNET_ROOT(x64)"] = dotNetRoot;
                }

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                ShowError("Failed to launch YTPlayer.\r\n\r\n" + ex.Message);
            }
        }

        private static bool EnsureDotNetDesktopRuntime(string runtimeConfigPath, out string? dotNetRoot, out string? dotNetExe)
        {
            dotNetRoot = null;
            dotNetExe = null;
            Version required = DependencyInstaller.TryGetRequiredDesktopRuntimeVersion(runtimeConfigPath) ?? FallbackDesktopRuntime;
            string localRoot = DependencyInstaller.GetLocalDotNetRoot();

            string? preferredRoot = null;
            Version? installed = DependencyInstaller.TryGetInstalledDesktopRuntimeVersion(out string? installRoot, localRoot);
            if (installed != null && installed >= required)
            {
                preferredRoot = installRoot;
            }

            string? detectedExe = DependencyInstaller.FindDotNetExecutable(preferredRoot);
            if (!string.IsNullOrWhiteSpace(detectedExe) &&
                DependencyInstaller.IsDesktopRuntimeAvailable(detectedExe!, required, out _))
            {
                string resolvedExe = detectedExe!;
                dotNetExe = resolvedExe;
                dotNetRoot = Path.GetDirectoryName(resolvedExe);
                if (!string.IsNullOrWhiteSpace(dotNetRoot))
                {
                    DependencyInstaller.ApplyDotNetRootToProcess(dotNetRoot);
                }
                return true;
            }

            DependencyInstallResult? result = null;
            using (var dialog = new DependencyInstallDialog("正在准备运行环境", "正在下载并安装 .NET 10 桌面运行时，请稍候..."))
            {
                dialog.Shown += async (_, __) =>
                {
                    try
                    {
                        var progress = new Progress<DependencyInstallProgress>(dialog.UpdateProgress);
                        result = await DependencyInstaller.InstallDotNetDesktopRuntimeAsync(required, null, progress, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        result = new DependencyInstallResult(false, ex.Message ?? "安装失败。");
                    }
                    finally
                    {
                        dialog.AllowClose();
                        dialog.Close();
                    }
                };

                dialog.ShowDialog();
            }

            if (result == null || !result.Success)
            {
                string message = result?.ErrorMessage ?? "安装 .NET 运行时失败。";
                ShowError("无法安装 .NET 运行时，应用无法启动。\r\n\r\n" + message);
                return false;
            }

            dotNetExe = DependencyInstaller.FindDotNetExecutable(null);
            if (!string.IsNullOrWhiteSpace(dotNetExe))
            {
                dotNetRoot = Path.GetDirectoryName(dotNetExe);
                if (!string.IsNullOrWhiteSpace(dotNetRoot))
                {
                    DependencyInstaller.ApplyDotNetRootToProcess(dotNetRoot);
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.InstallRoot))
            {
                dotNetExe = DependencyInstaller.FindDotNetExecutable(result.InstallRoot);
                if (!string.IsNullOrWhiteSpace(dotNetExe))
                {
                    dotNetRoot = Path.GetDirectoryName(dotNetExe);
                    if (!string.IsNullOrWhiteSpace(dotNetRoot))
                    {
                        DependencyInstaller.ApplyDotNetRootToProcess(dotNetRoot);
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(dotNetExe))
            {
                ShowError("安装完成后未能找到 dotnet.exe，应用无法启动。");
                return false;
            }
            return true;
        }

        private static ProcessStartInfo BuildLaunchInfo(
            string appExe,
            string appDll,
            string runtimeConfigPath,
            string depsPath,
            string? dotNetExe,
            string[] args,
            string rootDir)
        {
            // Prefer launching the app host directly so the shell and screen readers
            // identify the window as YTPlayer instead of generic ".NET Host".
            if (File.Exists(appExe))
            {
                return new ProcessStartInfo
                {
                    FileName = appExe,
                    Arguments = BuildArgumentString(args),
                    WorkingDirectory = rootDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }

            if (!string.IsNullOrWhiteSpace(dotNetExe) && File.Exists(dotNetExe))
            {
                string execArgs =
                    $"exec --runtimeconfig \"{runtimeConfigPath}\" --depsfile \"{depsPath}\" \"{appDll}\" {BuildArgumentString(args)}";
                return new ProcessStartInfo
                {
                    FileName = dotNetExe,
                    Arguments = execArgs.Trim(),
                    WorkingDirectory = rootDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }

            throw new FileNotFoundException("No valid app entry point found.", appExe);
        }

        private static string BuildArgumentString(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", args.Select(Quote));
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "YTPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
