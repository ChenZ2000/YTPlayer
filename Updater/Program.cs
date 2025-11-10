using System;
using System.Windows.Forms;
using YTPlayer.Update;

namespace YTPlayer.Updater
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
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
    }
}
