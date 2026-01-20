using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Utils;

namespace YTPlayer.Launcher
{
    internal static class Program
    {
        private const string LibsFolderName = "libs";
        private const string MainExeName = "YTPlayer.App.exe";
        private const string RootEnvVar = "YTPLAYER_ROOT";

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

                if (!File.Exists(mainExe))
                {
                    ShowError("Main app not found:\r\n" + mainExe);
                    return;
                }

                Environment.SetEnvironmentVariable(RootEnvVar, rootDir);

                var startInfo = new ProcessStartInfo
                {
                    FileName = mainExe,
                    Arguments = BuildArgumentString(args),
                    WorkingDirectory = rootDir,
                    UseShellExecute = false
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                ShowError("Failed to launch YTPlayer.\r\n\r\n" + ex.Message);
            }
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
