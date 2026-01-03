using System;
using System.IO;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 路径解析助手：统一处理应用根目录与 libs 子目录的资源定位。
    /// </summary>
    public static class PathHelper
    {
        private const string RootEnvVar = "YTPLAYER_ROOT";
        private const string RootMarkerName = "YTPlayer.exe";

        /// <summary>
        /// 启动器传入或外部指定的根目录（便携式根目录）。
        /// </summary>
        public static string ApplicationRootDirectory => ResolveApplicationRoot();

        /// <summary>
        /// 程序运行目录（AppDomain.BaseDirectory 总是以目录分隔符结尾）。
        /// </summary>
        public static string BaseDirectory =>
            AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;

        /// <summary>
        /// libs 目录的完整路径（可能不存在）。
        /// </summary>
        public static string LibsDirectory => Path.Combine(ApplicationRootDirectory, "libs");

        /// <summary>
        /// runtimes 目录的完整路径（可能不存在）。
        /// </summary>
        public static string RuntimesDirectory => Path.Combine(ApplicationRootDirectory, "runtimes");

        /// <summary>
        /// 优先在 libs 下查找指定文件名，找不到则回退到根目录。
        /// 返回的可能是不存在的路径，调用方自行判断。
        /// </summary>
        public static string ResolveFromLibsOrBase(string fileName)
        {
            var inLibs = Path.Combine(LibsDirectory, fileName);
            if (File.Exists(inLibs))
            {
                return inLibs;
            }

            return Path.Combine(ApplicationRootDirectory, fileName);
        }

        /// <summary>
        /// 优先在 libs 下查找指定相对路径（支持子目录），找不到则回退到根目录。
        /// </summary>
        public static string ResolveFromLibsOrRoot(string relativePath)
        {
            var inLibs = Path.Combine(LibsDirectory, relativePath);
            if (File.Exists(inLibs))
            {
                return inLibs;
            }

            return Path.Combine(ApplicationRootDirectory, relativePath);
        }

        private static string ResolveApplicationRoot()
        {
            try
            {
                string? fromEnv = Environment.GetEnvironmentVariable(RootEnvVar);
                if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
                {
                    return Path.GetFullPath(fromEnv);
                }
            }
            catch
            {
            }

            try
            {
                string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string candidate = Path.GetFullPath(baseDir);
                    if (File.Exists(Path.Combine(candidate, RootMarkerName)))
                    {
                        return candidate;
                    }

                    string trimmed = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string leaf = Path.GetFileName(trimmed);
                    if (string.Equals(leaf, "libs", StringComparison.OrdinalIgnoreCase))
                    {
                        string? parent = Path.GetDirectoryName(trimmed);
                        if (!string.IsNullOrEmpty(parent) &&
                            File.Exists(Path.Combine(parent, RootMarkerName)))
                        {
                            return parent;
                        }
                    }
                }
            }
            catch
            {
            }

            return Path.GetFullPath(BaseDirectory);
        }
    }
}
