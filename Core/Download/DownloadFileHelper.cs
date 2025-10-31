using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YTPlayer.Models;

namespace YTPlayer.Core.Download
{
    /// <summary>
    /// 下载文件辅助工具类
    /// 提供文件名合法化、路径检查、冲突检测等功能
    /// </summary>
    public static class DownloadFileHelper
    {
        /// <summary>
        /// Windows 文件名最大长度限制（保留扩展名空间）
        /// </summary>
        private const int MAX_FILENAME_LENGTH = 250;

        /// <summary>
        /// Windows 路径最大长度限制
        /// </summary>
        private const int MAX_PATH_LENGTH = 260;

        /// <summary>
        /// 非法文件名字符（Windows）
        /// </summary>
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// 非法路径字符（Windows）
        /// </summary>
        private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

        /// <summary>
        /// 将歌曲名和歌手名组合成安全的文件名（不含扩展名）
        /// </summary>
        /// <param name="songName">歌曲名</param>
        /// <param name="artistName">歌手名</param>
        /// <param name="trackNumber">曲目编号（可选，用于歌单批量下载）</param>
        /// <param name="isTrial">是否为试听版本（默认false）</param>
        /// <param name="maxLength">最大长度限制（可选，动态计算以避免路径过长）</param>
        /// <returns>安全的文件名（不含扩展名）</returns>
        public static string CreateSafeFileName(string songName, string artistName, int? trackNumber = null, bool isTrial = false, int? maxLength = null)
        {
            if (string.IsNullOrWhiteSpace(songName))
            {
                songName = "未知歌曲";
            }

            if (string.IsNullOrWhiteSpace(artistName))
            {
                artistName = "未知艺人";
            }

            // 使用动态长度限制或默认值
            int lengthLimit = maxLength ?? MAX_FILENAME_LENGTH;

            // 如果是试听版，在歌曲名后添加标记
            string trialSuffix = isTrial ? " (试听版)" : "";

            // 计算固定部分的长度
            string trackPrefix = trackNumber.HasValue ? $"{trackNumber.Value:D2}. " : "";
            const string separator = " - ";
            int fixedLength = trackPrefix.Length + trialSuffix.Length + separator.Length;

            // 计算可用于歌曲名和艺术家名的长度
            int availableLength = lengthLimit - fixedLength;
            if (availableLength <= 20)
            {
                // 如果可用长度太短，使用极简模式
                availableLength = Math.Max(20, availableLength);
                songName = TruncateString(songName, availableLength);
                artistName = "";
            }
            else
            {
                // 智能分配：歌曲名优先，占65%；艺术家名占35%
                int songNameLimit = (int)(availableLength * 0.65);
                int artistNameLimit = availableLength - songNameLimit;

                // 截断歌曲名和艺术家名
                songName = TruncateString(songName, songNameLimit);
                artistName = TruncateString(artistName, artistNameLimit);
            }

            // 构建文件名
            string baseFileName;
            if (string.IsNullOrEmpty(artistName))
            {
                baseFileName = $"{trackPrefix}{songName}{trialSuffix}";
            }
            else
            {
                baseFileName = $"{trackPrefix}{songName}{trialSuffix}{separator}{artistName}";
            }

            // 替换非法字符为下划线
            string safeName = ReplaceInvalidChars(baseFileName, "_");

            // 最终安全检查：确保不超过限制
            if (safeName.Length > lengthLimit)
            {
                // 如果仍需截断，保留省略号以标记截断位置
                if (lengthLimit >= 10)
                {
                    safeName = safeName.Substring(0, lengthLimit - 3) + "...";
                }
                else
                {
                    safeName = safeName.Substring(0, lengthLimit);
                }
            }

            // 去除首尾空格
            safeName = safeName.Trim();

            // 智能处理末尾的点：只移除单个或两个点（Windows特殊名称），保留三个点（省略号）
            // 这样可以保留截断标记，同时避免 "." 或 ".." 这样的非法文件名
            while (safeName.Length > 0 && safeName.EndsWith(".") && !safeName.EndsWith("..."))
            {
                safeName = safeName.Substring(0, safeName.Length - 1);
            }

            // 再次去除空格（移除点后可能露出空格）
            safeName = safeName.Trim();

            return safeName;
        }

        /// <summary>
        /// 智能截断字符串（保留有意义的部分）
        /// </summary>
        /// <param name="text">要截断的文本</param>
        /// <param name="maxLength">最大长度</param>
        /// <returns>截断后的文本</returns>
        private static string TruncateString(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            // 如果长度足够，添加省略号；否则直接截断
            if (maxLength >= 10)
            {
                return text.Substring(0, maxLength - 3) + "...";
            }
            else
            {
                return text.Substring(0, maxLength);
            }
        }

        /// <summary>
        /// 创建安全的目录名（用于歌单/专辑批量下载）
        /// </summary>
        /// <param name="playlistName">歌单/专辑名</param>
        /// <param name="artistName">歌手名（可选）</param>
        /// <returns>安全的目录名</returns>
        public static string CreateSafeDirName(string playlistName, string? artistName = null)
        {
            if (string.IsNullOrWhiteSpace(playlistName))
            {
                playlistName = "未知歌单";
            }

            // 构建目录名
            string dirName = string.IsNullOrWhiteSpace(artistName)
                ? playlistName
                : $"{playlistName} - {artistName}";

            // 替换非法字符
            string safeName = ReplaceInvalidChars(dirName, "_");

            // 限制长度，截断时添加省略号
            if (safeName.Length > MAX_FILENAME_LENGTH)
            {
                if (MAX_FILENAME_LENGTH >= 10)
                {
                    safeName = safeName.Substring(0, MAX_FILENAME_LENGTH - 3) + "...";
                }
                else
                {
                    safeName = safeName.Substring(0, MAX_FILENAME_LENGTH);
                }
            }

            // 去除首尾空格
            safeName = safeName.Trim();

            // 智能处理末尾的点：只移除单个或两个点，保留三个点（省略号）
            while (safeName.Length > 0 && safeName.EndsWith(".") && !safeName.EndsWith("..."))
            {
                safeName = safeName.Substring(0, safeName.Length - 1);
            }

            // 再次去除空格
            safeName = safeName.Trim();

            return safeName;
        }

        /// <summary>
        /// 根据音质级别获取文件扩展名
        /// </summary>
        /// <param name="quality">音质级别</param>
        /// <returns>文件扩展名（带点，如 ".flac"）</returns>
        public static string GetFileExtension(QualityLevel quality)
        {
            switch (quality)
            {
                case QualityLevel.Lossless:  // 无损
                case QualityLevel.HiRes:     // 超清
                case QualityLevel.Master:    // 超清母带
                    return ".flac";

                case QualityLevel.Standard:  // 标准
                case QualityLevel.High:      // 极高
                case QualityLevel.SurroundHD:  // 高清环绕声
                case QualityLevel.Dolby:     // 沉浸环绕声
                default:
                    return ".mp3";
            }
        }

        /// <summary>
        /// 构建完整的下载文件路径（动态计算文件名长度以避免路径过长）
        /// ⭐ 支持多级子目录路径（如 "分类名/子分类名/歌单名"）
        /// </summary>
        /// <param name="downloadDirectory">下载根目录</param>
        /// <param name="song">歌曲信息</param>
        /// <param name="quality">音质级别</param>
        /// <param name="trackNumber">曲目编号（可选）</param>
        /// <param name="subDirectory">子目录（可选，用于批量下载，支持多级路径如 "分类/子分类/歌单"）</param>
        /// <returns>完整的文件路径</returns>
        public static string BuildFilePath(
            string downloadDirectory,
            SongInfo song,
            QualityLevel quality,
            int? trackNumber = null,
            string? subDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                throw new ArgumentException("下载目录不能为空", nameof(downloadDirectory));
            }

            if (song == null)
            {
                throw new ArgumentNullException(nameof(song));
            }

            // 获取扩展名
            string extension = GetFileExtension(quality);

            // 计算已占用的路径长度
            int usedLength = downloadDirectory.Length;

            // ⭐ 处理多级子目录路径
            string[]? safeSubDirParts = null;
            if (!string.IsNullOrWhiteSpace(subDirectory))
            {
                // 将子目录按路径分隔符拆分（支持 \ 和 /）
                string[] subDirParts = subDirectory.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

                // 对每个部分分别进行安全化处理
                safeSubDirParts = new string[subDirParts.Length];
                for (int i = 0; i < subDirParts.Length; i++)
                {
                    safeSubDirParts[i] = CreateSafeDirName(subDirParts[i]);
                    usedLength += safeSubDirParts[i].Length + 1; // +1 for directory separator
                }
            }

            // 加上扩展名长度和分隔符
            usedLength += extension.Length + 1; // +1 for directory separator before filename

            // 计算安全边距（考虑路径分隔符、非ASCII字符等边界情况）
            const int safeMargin = 15;
            usedLength += safeMargin;

            // 计算可用于文件名的最大长度
            int maxFileNameLength = MAX_PATH_LENGTH - usedLength;

            // 确保至少有50个字符可用于文件名
            if (maxFileNameLength < 50)
            {
                maxFileNameLength = 50; // 最小保证长度
            }

            // 使用动态计算的长度限制创建文件名
            string fileName = CreateSafeFileName(song.Name, song.Artist, trackNumber, song.IsTrial, maxFileNameLength);
            string fullFileName = fileName + extension;

            // 构建完整路径
            string fullPath;
            if (safeSubDirParts != null && safeSubDirParts.Length > 0)
            {
                // 有子目录（批量下载），使用 Path.Combine 处理多级路径
                var pathParts = new List<string> { downloadDirectory };
                pathParts.AddRange(safeSubDirParts);
                pathParts.Add(fullFileName);
                fullPath = Path.Combine(pathParts.ToArray());
            }
            else
            {
                // 无子目录（单曲下载）
                fullPath = Path.Combine(downloadDirectory, fullFileName);
            }

            // 最终验证路径长度（如果仍然超长，说明目录本身就太长了）
            if (fullPath.Length > MAX_PATH_LENGTH)
            {
                // 尝试更激进的截断
                maxFileNameLength = Math.Max(30, maxFileNameLength - (fullPath.Length - MAX_PATH_LENGTH) - 10);
                fileName = CreateSafeFileName(song.Name, song.Artist, trackNumber, song.IsTrial, maxFileNameLength);
                fullFileName = fileName + extension;

                if (safeSubDirParts != null && safeSubDirParts.Length > 0)
                {
                    var pathParts = new List<string> { downloadDirectory };
                    pathParts.AddRange(safeSubDirParts);
                    pathParts.Add(fullFileName);
                    fullPath = Path.Combine(pathParts.ToArray());
                }
                else
                {
                    fullPath = Path.Combine(downloadDirectory, fullFileName);
                }

                // 如果还是超长，只能抛出异常
                if (fullPath.Length > MAX_PATH_LENGTH)
                {
                    throw new PathTooLongException($"文件路径过长（超过 {MAX_PATH_LENGTH} 字符），即使激进截断后仍然超限。请考虑缩短下载目录路径。\n" +
                        $"当前路径: {fullPath}\n" +
                        $"路径长度: {fullPath.Length} 字符");
                }
            }

            return fullPath;
        }

        /// <summary>
        /// 检查文件或目录是否存在冲突
        /// </summary>
        /// <param name="path">文件或目录路径</param>
        /// <returns>是否存在冲突</returns>
        public static bool HasConflict(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>
        /// 检查目录是否存在，不存在则创建
        /// </summary>
        /// <param name="directoryPath">目录路径</param>
        public static void EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("目录路径不能为空", nameof(directoryPath));
            }

            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法创建目录：{directoryPath}", ex);
            }
        }

        /// <summary>
        /// 获取临时下载文件路径
        /// </summary>
        /// <param name="finalPath">最终文件路径</param>
        /// <returns>临时文件路径</returns>
        public static string GetTempFilePath(string finalPath)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                throw new ArgumentException("文件路径不能为空", nameof(finalPath));
            }

            return finalPath + ".downloading";
        }

        /// <summary>
        /// 删除临时文件（如果存在）
        /// </summary>
        /// <param name="tempFilePath">临时文件路径</param>
        public static void DeleteTempFileIfExists(string tempFilePath)
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // 忽略删除失败
            }
        }

        /// <summary>
        /// 将临时文件重命名为最终文件
        /// </summary>
        /// <param name="tempFilePath">临时文件路径</param>
        /// <param name="finalPath">最终文件路径</param>
        public static void RenameTempFile(string tempFilePath, string finalPath)
        {
            if (!File.Exists(tempFilePath))
            {
                throw new FileNotFoundException("临时文件不存在", tempFilePath);
            }

            // 如果目标文件已存在，先删除
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            // 重命名
            File.Move(tempFilePath, finalPath);
        }

        /// <summary>
        /// 格式化文件大小显示
        /// </summary>
        /// <param name="bytes">字节数</param>
        /// <returns>格式化的大小字符串</returns>
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / 1024.0 / 1024.0:F2} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        /// <summary>
        /// 替换字符串中的非法字符
        /// </summary>
        /// <param name="input">输入字符串</param>
        /// <param name="replacement">替换字符</param>
        /// <returns>替换后的字符串</returns>
        private static string ReplaceInvalidChars(string input, string replacement)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            // 使用 StringBuilder 提高性能
            var sb = new StringBuilder(input);
            foreach (char c in InvalidFileNameChars)
            {
                sb.Replace(c.ToString(), replacement);
            }

            // 额外替换一些容易引起问题的字符
            sb.Replace(":", replacement);
            sb.Replace("*", replacement);
            sb.Replace("?", replacement);
            sb.Replace("\"", replacement);
            sb.Replace("<", replacement);
            sb.Replace(">", replacement);
            sb.Replace("|", replacement);

            return sb.ToString();
        }

        /// <summary>
        /// 验证文件路径是否合法
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>是否合法</returns>
        public static bool IsValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                // 尝试获取完整路径
                Path.GetFullPath(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
