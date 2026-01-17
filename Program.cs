using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Utils;
using Microsoft.Win32;

namespace YTPlayer
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            TryEnableLegacyAccessibility();
            // ✅ 初始化日志系统（优先执行）
            DebugLogger.Initialize();
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "════════════════════════════════════════");
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "应用程序启动");
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", $"命令行参数: {string.Join(" ", args)}");
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "════════════════════════════════════════");

            // ✅ 配置崩溃转储（抓取原生崩溃）
            ConfigureCrashDumps();

            // ✅ 配置依赖加载路径（托管与本机）
            ConfigurePrivateLibPath();

            // 与 .NET Framework 4.8 视觉一致（默认字体/高 DPI 行为）
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetDefaultFont(new Font("Microsoft Sans Serif", 8.25f));

            // ✅ 注册全局异常处理器
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.ThreadException += OnThreadException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 清理旧日志（保留7天）
            DebugLogger.CleanOldLogs(7);

            try
            {
                // 检查命令行参数
                if (args.Length > 0 && args[0] == "--check-vip")
                {
                    DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "运行 VIP 诊断模式");
                    // 运行VIP诊断
                    CheckVIPLevelAsync().GetAwaiter().GetResult();
                    return;
                }

                // ✅ 初始化质量管理器（设置默认音质为"极高"）
                InitializeQualityManager();

                // 正常启动GUI
                DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "启动 GUI 模式");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("Program", ex, "Main 函数顶层异常");
                MessageBox.Show(
                    $"应用程序启动失败:\n\n{ex.Message}\n\n详细信息已记录到日志文件。",
                    "严重错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "应用程序退出");
                DebugLogger.Shutdown();
            }
        }

        /// <summary>
        /// 初始化质量管理器
        /// 设置默认音质为"极高"，处理登录状态联动
        /// </summary>
        private static void InitializeQualityManager()
        {
            try
            {
                var configManager = ConfigManager.Instance;
                var config = configManager.Load();

                // 检查登录状态并设置默认音质
                // 如果未登录且配置的音质不可用，降级到"极高音质"
                var accountStore = new AccountStateStore();
                var accountState = accountStore.Load();
                bool isLoggedIn = accountState.IsLoggedIn;

                if (string.IsNullOrEmpty(config.DefaultQuality))
                {
                    config.DefaultQuality = "极高音质"; // 默认极高
                    configManager.Save(config);
                }
                else if (!isLoggedIn)
                {
                    // 未登录时，仅允许标准和极高
                    if (config.DefaultQuality != "标准音质" && config.DefaultQuality != "极高音质")
                    {
                        DebugLogger.Log(DebugLogger.LogLevel.Info, "Program",
                            $"User not logged in, downgrading quality from {config.DefaultQuality} to 极高音质");
                        config.DefaultQuality = "极高音质";
                        configManager.Save(config);
                    }
                }

                DebugLogger.Log(DebugLogger.LogLevel.Info, "Program",
                    $"✓ 默认音质已设置为: {config.DefaultQuality}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("Program", ex, "初始化默认音质失败（非致命）");
            }
        }

        private static void TryEnableLegacyAccessibility()
        {
            try
            {
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures", true);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.2", true);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.3", true);
                bool value1;
                bool value2;
                bool value3;
                AppContext.TryGetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures", out value1);
                AppContext.TryGetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.2", out value2);
                AppContext.TryGetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.3", out value3);
                DebugLogger.Log(DebugLogger.LogLevel.Info, "Program",
                    $"Legacy accessibility switches: {value1}, {value2}, {value3}");
            }
            catch
            {
            }
        }

        /// <summary>
        /// 将 libs 目录加入依赖搜索路径（托管与本机）
        /// </summary>
        private static void ConfigurePrivateLibPath()
        {
            try
            {
                string libsPath = PathHelper.LibsDirectory;
                string? runtimeNativePath = GetRuntimeNativePath();
                bool hasLibs = Directory.Exists(libsPath);
                bool hasRuntimeNative = !string.IsNullOrWhiteSpace(runtimeNativePath) &&
                                        Directory.Exists(runtimeNativePath);
                if (!hasLibs && !hasRuntimeNative)
                {
                    return;
                }

                if (hasLibs)
                {
                    SetDllDirectory(libsPath);
                }
                else if (hasRuntimeNative)
                {
                    SetDllDirectory(runtimeNativePath!);
                }

                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var pathEntries = currentPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                var pathSet = new HashSet<string>(pathEntries, StringComparer.OrdinalIgnoreCase);
                bool pathChanged = false;
                if (hasLibs && pathSet.Add(libsPath))
                {
                    pathEntries.Insert(0, libsPath);
                    pathChanged = true;
                }
                if (hasRuntimeNative && runtimeNativePath != null && pathSet.Add(runtimeNativePath))
                {
                    pathEntries.Insert(0, runtimeNativePath);
                    pathChanged = true;
                }
                if (pathChanged)
                {
                    Environment.SetEnvironmentVariable("PATH", string.Join(";", pathEntries));
                }

                if (!hasLibs)
                {
                    return;
                }

                Assembly? ResolveFromLibs(AssemblyName name)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(name.Name))
                        {
                            return null;
                        }

                        var loaded = AssemblyLoadContext.Default.Assemblies
                            .FirstOrDefault(asm => string.Equals(asm.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
                        if (loaded != null)
                        {
                            return loaded;
                        }

                        string candidate = Path.Combine(libsPath, name.Name + ".dll");
                        if (File.Exists(candidate))
                        {
                            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogException("Program", ex, $"AssemblyResolve 处理失败: {name.FullName}");
                    }
                    return null;
                }

                // 托管依赖兜底解析（确保 Newtonsoft.Json 等从 libs 解析）
                AssemblyLoadContext.Default.Resolving += (_, name) => ResolveFromLibs(name);
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) => ResolveFromLibs(new AssemblyName(args.Name));
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("Program", ex, "配置依赖目录失败（非致命）");
            }
        }

        /// <summary>
        /// 启用 Windows Error Reporting 本地转储（抓取原生崩溃）
        /// </summary>
        private static void ConfigureCrashDumps()
        {
            try
            {
                string dumpRoot = Path.Combine(PathHelper.ApplicationRootDirectory, "Dumps");
                Directory.CreateDirectory(dumpRoot);

                string currentExe = $"{Process.GetCurrentProcess().ProcessName}.exe";
                ConfigureCrashDumpsForExe(currentExe, dumpRoot);

                if (!string.Equals(currentExe, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                {
                    ConfigureCrashDumpsForExe("dotnet.exe", dumpRoot);
                }

                DebugLogger.Log(DebugLogger.LogLevel.Info, "CrashDump",
                    $"已启用本地转储: exe={currentExe}, dir={dumpRoot}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("CrashDump", ex, "配置崩溃转储失败（非致命）");
            }
        }

        private static void ConfigureCrashDumpsForExe(string exeName, string dumpRoot)
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\{exeName}");
            if (key == null)
            {
                return;
            }

            key.SetValue("DumpFolder", dumpRoot, RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 2, RegistryValueKind.DWord);   // 2 = full dump
            key.SetValue("DumpCount", 10, RegistryValueKind.DWord);
        }

        private static string? GetRuntimeNativePath()
        {
            string? rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                Architecture.Arm => "win-arm",
                _ => null
            };

            if (string.IsNullOrWhiteSpace(rid))
            {
                return null;
            }

            return Path.Combine(PathHelper.RuntimesDirectory, rid, "native");
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// 捕获未处理的异常（非UI线程）
        /// </summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            DebugLogger.LogException("UnhandledException", ex ?? new Exception(e.ExceptionObject?.ToString()),
                $"IsTerminating: {e.IsTerminating}");

            if (e.IsTerminating)
            {
                MessageBox.Show(
                    $"应用程序遇到致命错误即将崩溃:\n\n{ex?.Message ?? e.ExceptionObject?.ToString()}\n\n日志已保存。",
                    "致命错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 捕获UI线程异常
        /// </summary>
        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            DebugLogger.LogException("ThreadException", e.Exception, "UI线程异常");

            var result = MessageBox.Show(
                $"应用程序遇到错误:\n\n{e.Exception.Message}\n\n详细信息已记录到日志。\n\n是否继续运行？",
                "错误",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Error);

            if (result == DialogResult.No)
            {
                RequestShutdown();
            }
        }

        private static void RequestShutdown()
        {
            try
            {
                var forms = Application.OpenForms.Cast<Form>().ToArray();
                if (forms.Length == 0)
                {
                    Application.ExitThread();
                    return;
                }

                foreach (var form in forms)
                {
                    try
                    {
                        if (form.IsDisposed)
                        {
                            continue;
                        }

                        if (form.InvokeRequired)
                        {
                            form.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (!form.IsDisposed)
                                    {
                                        form.Close();
                                    }
                                }
                                catch (Exception closeEx)
                                {
                                    DebugLogger.LogException("Program", closeEx, "关闭窗体失败");
                                }
                            }));
                        }
                        else
                        {
                            form.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogException("Program", ex, "请求关闭窗体失败");
                    }
                }
            }
            catch (Exception outer)
            {
                DebugLogger.LogException("Program", outer, "RequestShutdown 失败");
                Application.ExitThread();
            }
        }

        /// <summary>
        /// 检查VIP等级的命令行工具
        /// </summary>
        private static async Task CheckVIPLevelAsync()
        {
            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   NetEase Cloud Music - VIP Level Verification Tool      ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            // 读取配置文件
            string configPath = "config.json";
            if (!File.Exists(configPath))
            {
                Console.WriteLine("❌ Config file not found: config.json");
                Console.WriteLine("   Please run from project root directory.");
                return;
            }

            string configJson = File.ReadAllText(configPath);
            var config = JObject.Parse(configJson);

            string musicU = config["MusicU"]?.Value<string>() ?? string.Empty;
            string csrfToken = config["CsrfToken"]?.Value<string>() ?? string.Empty;
            string deviceId = config["DeviceId"]?.Value<string>() ?? string.Empty;

            if (string.IsNullOrEmpty(musicU))
            {
                Console.WriteLine("❌ MUSIC_U not found in config.json");
                Console.WriteLine("   Please login first.");
                return;
            }

            Console.WriteLine("✅ Config loaded");
            Console.WriteLine($"   MUSIC_U length: {musicU.Length} characters");
            Console.WriteLine();

            // 创建API客户端
            var apiClient = new NeteaseApiClient(musicU, csrfToken, deviceId);

            Console.WriteLine("──────────────────────────────────────────────────────────");
            Console.WriteLine("Checking account information...");
            Console.WriteLine("──────────────────────────────────────────────────────────");
            Console.WriteLine();

            try
            {
                var userInfo = await apiClient.GetUserInfoAsync();

                if (userInfo == null)
                {
                    Console.WriteLine("❌ Failed to get user info");
                    Console.WriteLine("   Cookie may be expired or invalid.");
                    return;
                }

                // 显示用户信息
                Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
                Console.WriteLine("║                    Account Information                    ║");
                Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
                Console.WriteLine();
                Console.WriteLine($"User ID:   {userInfo.UserId}");
                Console.WriteLine($"Nickname:  {userInfo.Nickname}");
                Console.WriteLine($"VIP Type:  {userInfo.VipType}");
                Console.WriteLine();

                // 分析VIP等级
                Console.WriteLine("──────────────────────────────────────────────────────────");
                Console.WriteLine("VIP Level Analysis:");
                Console.WriteLine("──────────────────────────────────────────────────────────");
                Console.WriteLine();

                switch (userInfo.VipType)
                {
                    case 0:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("❌ No VIP Subscription");
                        Console.ResetColor();
                        Console.WriteLine();
                        Console.WriteLine("Available: standard (128k), exhigh (320k)");
                        Console.WriteLine("Unavailable: lossless, hires, jymaster, sky (require VIP/SVIP)");
                        break;

                    case 1:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("⚠️  Regular VIP (黑胶VIP)");
                        Console.ResetColor();
                        Console.WriteLine();
                        Console.WriteLine("Available Quality:");
                        Console.WriteLine("  ✅ standard (128 kbps)");
                        Console.WriteLine("  ✅ exhigh (320 kbps)");
                        Console.WriteLine("  ✅ lossless (1411 kbps)");
                        Console.WriteLine();
                        Console.WriteLine("Unavailable Quality:");
                        Console.WriteLine("  ❌ hires (2822 kbps) - Requires SVIP");
                        Console.WriteLine("  ❌ jyeffect - Requires SVIP");
                        Console.WriteLine("  ❌ sky - Requires SVIP");
                        Console.WriteLine("  ❌ jymaster - Requires SVIP");
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("⚠️  THIS IS WHY YOU CAN ONLY GET LOSSLESS!");
                        Console.WriteLine("    To access jymaster/sky/hires:");
                        Console.WriteLine("    → Upgrade to 黑胶SVIP (VipType=11)");
                        Console.ResetColor();
                        break;

                    case 11:
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("✅ SVIP (黑胶SVIP)");
                        Console.ResetColor();
                        Console.WriteLine();
                        Console.WriteLine("✅ You have access to ALL quality levels:");
                        Console.WriteLine("  • standard, exhigh, lossless");
                        Console.WriteLine("  • hires, jyeffect, sky, jymaster");
                        Console.WriteLine();
                        Console.WriteLine("Note: If you can't get SVIP quality:");
                        Console.WriteLine("  1. Song may not have ultra-HD version");
                        Console.WriteLine("  2. Try newer songs");
                        Console.WriteLine("  3. Check debug logs for server downgrade");
                        break;

                    default:
                        Console.WriteLine($"⚠️  Unknown VIP Type: {userInfo.VipType}");
                        break;
                }

                Console.WriteLine();
                Console.WriteLine("──────────────────────────────────────────────────────────");
                Console.WriteLine("VIP Type Reference:");
                Console.WriteLine("  0 = No VIP");
                Console.WriteLine("  1 = Regular VIP (黑胶VIP)");
                Console.WriteLine("  11 = SVIP (黑胶SVIP)");
                Console.WriteLine("──────────────────────────────────────────────────────────");
                Console.WriteLine();

                // 结论
                if (userInfo.VipType == 11)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✅ Your account has SVIP privileges.");
                    Console.WriteLine("   You should be able to access ultra-HD quality.");
                    Console.ResetColor();
                }
                else if (userInfo.VipType == 1)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("⚠️  ROOT CAUSE FOUND!");
                    Console.WriteLine();
                    Console.WriteLine("   Your account is Regular VIP, NOT SVIP.");
                    Console.WriteLine("   This explains the lossless-only limitation.");
                    Console.WriteLine();
                    Console.WriteLine("   Solution: Upgrade to 黑胶SVIP");
                    Console.WriteLine("   (No code fix can bypass this)");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("❌ No VIP subscription detected.");
                    Console.WriteLine("   Purchase 黑胶SVIP to access ultra-HD quality.");
                    Console.ResetColor();
                }

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}

