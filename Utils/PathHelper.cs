using System;
using System.IO;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 路径解析助手：统一处理应用根目录与 libs 子目录的资源定位。
    /// </summary>
    public static class PathHelper
    {
        /// <summary>
        /// 程序运行目录（AppDomain.BaseDirectory 总是以目录分隔符结尾）。
        /// </summary>
        public static string BaseDirectory =>
            AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;

        /// <summary>
        /// libs 目录的完整路径（可能不存在）。
        /// </summary>
        public static string LibsDirectory => Path.Combine(BaseDirectory, "libs");

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

            return Path.Combine(BaseDirectory, fileName);
        }
    }
}
