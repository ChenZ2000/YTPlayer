using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YTPlayer.Updater
{
    internal sealed class UpdaterOptions
    {
        private UpdaterOptions(string planFile, string targetDirectory, string mainExecutablePath, int mainProcessId, string[] mainArguments)
        {
            PlanFile = planFile;
            TargetDirectory = targetDirectory;
            MainExecutablePath = mainExecutablePath;
            MainProcessId = mainProcessId;
            MainArguments = mainArguments ?? Array.Empty<string>();
        }

        public string PlanFile { get; }

        public string TargetDirectory { get; }

        public string MainExecutablePath { get; }

        public bool MainIsLauncher => MainExecutablePath.EndsWith("YTPlayer.exe", StringComparison.OrdinalIgnoreCase);

        public int MainProcessId { get; }

        public string[] MainArguments { get; }

        public static UpdaterOptions Parse(string[] args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key;
                string? value = null;
                int equalsIndex = argument.IndexOf('=');
                if (equalsIndex > 0)
                {
                    key = argument.Substring(2, equalsIndex - 2);
                    value = argument.Substring(equalsIndex + 1);
                }
                else
                {
                    key = argument.Substring(2);
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        value = args[++i];
                    }
                }

                if (!string.IsNullOrWhiteSpace(key) && value != null)
                {
                    map[key] = value;
                }
            }

            if (!map.TryGetValue("plan", out string? planFile) || string.IsNullOrWhiteSpace(planFile))
            {
                throw new ArgumentException("缺少 plan 参数");
            }

            if (!File.Exists(planFile))
            {
                throw new FileNotFoundException("未找到更新计划文件", planFile);
            }

            map.TryGetValue("main", out string? mainExecutable);
            map.TryGetValue("target", out string? targetDirectory);
            if (string.IsNullOrWhiteSpace(targetDirectory) && !string.IsNullOrWhiteSpace(mainExecutable))
            {
                targetDirectory = Path.GetDirectoryName(mainExecutable);
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                targetDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }

            int mainPid = -1;
            if (map.TryGetValue("pid", out string? pidValue) && int.TryParse(pidValue, out int parsedPid))
            {
                mainPid = parsedPid;
            }

            string[] mainArgs = Array.Empty<string>();
            if (map.TryGetValue("main-args", out string? encodedArgs) && !string.IsNullOrWhiteSpace(encodedArgs))
            {
                mainArgs = DecodeArguments(encodedArgs);
            }

            return new UpdaterOptions(planFile, targetDirectory!, mainExecutable ?? string.Empty, mainPid, mainArgs);
        }

        private static string[] DecodeArguments(string encoded)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                string joined = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrEmpty(joined))
                {
                    return Array.Empty<string>();
                }

                return joined.Split(new[] { "\u001f" }, StringSplitOptions.None)
                             .Where(arg => !string.IsNullOrEmpty(arg))
                             .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
