using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
#if !NET48
using System.Runtime.Loader;
#endif
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Utils;
using YTPlayer.Update;

namespace YTPlayer.Updater
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (!IsInvokedFromMainProcess(args))
            {
                return;
            }

            BootstrapRootPath();
            ConfigurePrivateLibPath();
#if !NET48
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 10.5f));
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ThemeManager.Initialize();

            UpdaterOptions options;
            try
            {
                options = UpdaterOptions.Parse(args);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法解析更新参数：{ex.Message}", "更新程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            UpdatePlan plan;
            try
            {
                plan = UpdatePlan.LoadFrom(options.PlanFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法读取更新计划：{ex.Message}", "更新程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(new UpdaterForm(plan, options));
        }

        private static bool IsInvokedFromMainProcess(string[]? args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    return false;
                }

                string? target = Environment.GetEnvironmentVariable("YTPLAYER_TARGET");
                if (string.IsNullOrWhiteSpace(target))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void BootstrapRootPath()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YTPLAYER_ROOT")))
                {
                    return;
                }

                string? fromTarget = Environment.GetEnvironmentVariable("YTPLAYER_TARGET");
                if (!string.IsNullOrWhiteSpace(fromTarget) && Directory.Exists(fromTarget))
                {
                    Environment.SetEnvironmentVariable("YTPLAYER_ROOT", fromTarget);
                }
            }
            catch
            {
                // 忽略环境变量初始化失败
            }
        }

        /// <summary>
        /// 在更新器进程内启用 libs 目录的程序集/本机库探测，避免因工作目录变化导致依赖找不到。
        /// </summary>
        private static void ConfigurePrivateLibPath()
        {
            try
            {
                string libsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs");
                string updaterLibsPath = Path.Combine(libsPath, "updater");
                if (!Directory.Exists(libsPath) && !Directory.Exists(updaterLibsPath))
                {
                    return;
                }

                // 让本机 DLL 也能从 libs 被探测到
                if (Directory.Exists(updaterLibsPath))
                {
                    SetDllDirectory(updaterLibsPath);
                }
                else if (Directory.Exists(libsPath))
                {
                    SetDllDirectory(libsPath);
                }

                Assembly? ResolveFromLibs(AssemblyName name)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(name.Name))
                        {
                            return null;
                        }

#if !NET48
                        var loaded = AssemblyLoadContext.Default.Assemblies
                            .FirstOrDefault(asm => string.Equals(asm.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
                        if (loaded != null)
                        {
                            return loaded;
                        }
#endif

                        string candidate = string.Empty;
                        if (Directory.Exists(updaterLibsPath))
                        {
                            candidate = Path.Combine(updaterLibsPath, name.Name + ".dll");
                        }
                        if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                        {
                            candidate = Path.Combine(libsPath, name.Name + ".dll");
                        }
                        if (!File.Exists(candidate))
                        {
                            return null;
                        }
#if NET48
                        return Assembly.LoadFrom(candidate);
#else
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
#endif
                    }
                    catch
                    {
                        return null;
                    }
                }

                // 托管程序集兜底解析：优先从 libs 加载
#if !NET48
                AssemblyLoadContext.Default.Resolving += (_, name) => ResolveFromLibs(name);
#endif
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) => ResolveFromLibs(new AssemblyName(args.Name));
            }
            catch
            {
                // 忽略；保持默认探测
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
