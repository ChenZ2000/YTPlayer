using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YTPlayer.Update;

namespace YTPlayer.Updater
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ConfigurePrivateLibPath();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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

        /// <summary>
        /// 在更新器进程内启用 libs 目录的程序集/本机库探测，避免因工作目录变化导致依赖找不到。
        /// </summary>
        private static void ConfigurePrivateLibPath()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string libsPath = Path.Combine(baseDir, "libs");
                if (!Directory.Exists(libsPath))
                {
                    return;
                }

                // 让本机 DLL 也能从 libs 被探测到
                SetDllDirectory(libsPath);

                // 托管程序集兜底解析：优先从 libs 加载
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    try
                    {
                        string asmName = new AssemblyName(args.Name).Name + ".dll";
                        string candidate = Path.Combine(libsPath, asmName);
                        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                    }
                    catch
                    {
                        return null;
                    }
                };
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
