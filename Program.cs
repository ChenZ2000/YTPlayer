using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Utils;

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
            // ✅ 初始化日志系统（优先执行）
            DebugLogger.Initialize();
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "════════════════════════════════════════");
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "应用程序启动");
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", $"命令行参数: {string.Join(" ", args)}");
            DebugLogger.Log(DebugLogger.LogLevel.Info, "Program", "════════════════════════════════════════");

            // ✅ 配置网络连接池以支持高吞吐量并发下载（解决高码率无损音频卡顿）
            ConfigureNetworkSettings();

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
            }
        }

        /// <summary>
        /// 配置网络设置以支持高吞吐量并发下载
        /// 解决高码率无损音频（lossless/hires/sky）播放卡顿问题
        /// </summary>
        private static void ConfigureNetworkSettings()
        {
            try
            {
                // 🔧 核心修复：增加每个服务器的最大并发连接数
                // .NET Framework 默认值是 2，这是导致高码率音频卡顿的根本原因
                // 研究表明：并发 HTTP Range 请求需要多个 TCP 连接才能充分利用带宽
                System.Net.ServicePointManager.DefaultConnectionLimit = 100;

                // 🔧 禁用 Expect: 100-continue 头，减少请求延迟
                // 此头会在发送请求体前先等待服务器响应，对 Range 请求无意义
                System.Net.ServicePointManager.Expect100Continue = false;

                // 🔧 禁用 Nagle 算法，减少小包延迟
                // Nagle 算法会合并小包，但对音频流式传输来说延迟比吞吐量更重要
                System.Net.ServicePointManager.UseNagleAlgorithm = false;

                DebugLogger.Log(DebugLogger.LogLevel.Info, "Program",
                    "✓ 网络设置已优化: DefaultConnectionLimit=100, Expect100Continue=False, UseNagleAlgorithm=False");
                DebugLogger.Log(DebugLogger.LogLevel.Info, "Program",
                    "  目标：支持高码率无损音频流式播放，榨干带宽，永不卡顿");
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("Program", ex, "配置网络设置失败（非致命）");
            }
        }

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
                Application.Exit();
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

            string musicU = config["MusicU"]?.Value<string>();
            string csrfToken = config["CsrfToken"]?.Value<string>();
            string deviceId = config["DeviceId"]?.Value<string>();

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
