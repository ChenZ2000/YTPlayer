using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Lyrics;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Core.Download
{
    /// <summary>
    /// 音频文件元数据写入器
    /// 负责将歌曲信息、封面、歌词等元数据写入下载的音频文件
    /// </summary>
    public class AudioMetadataWriter
    {
        #region 常量

        /// <summary>
        /// 封面下载超时时间（秒）
        /// </summary>
        private const int COVER_DOWNLOAD_TIMEOUT_SECONDS = 30;

        /// <summary>
        /// 最大封面图片大小（10MB）
        /// </summary>
        private const int MAX_COVER_SIZE_BYTES = 10 * 1024 * 1024;

        #endregion

        #region 私有字段

        private readonly NeteaseApiClient _apiClient;
        private readonly HttpClient _httpClient;

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="apiClient">API客户端，用于获取歌词</param>
        public AudioMetadataWriter(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));

            // 创建用于下载封面的 HttpClient
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(COVER_DOWNLOAD_TIMEOUT_SECONDS)
            };
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 将元数据和歌词写入音频文件
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <param name="song">歌曲信息</param>
        /// <param name="trackNumber">曲目编号（可选，用于批量下载）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>写入是否成功</returns>
        public async Task<bool> WriteMetadataAsync(
            string filePath,
            SongInfo song,
            int? trackNumber = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("文件路径不能为空", nameof(filePath));
            }

            if (song == null)
            {
                throw new ArgumentNullException(nameof(song));
            }

            if (!File.Exists(filePath))
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Error,
                    "AudioMetadataWriter",
                    $"文件不存在: {filePath}");
                return false;
            }

            try
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    $"开始写入元数据: {song.Name} - {song.Artist}");

                // 使用 TagLib 打开文件
                using (var file = TagLib.File.Create(filePath))
                {
                    // 1. 写入基本元数据
                    WriteBasicMetadata(file, song, trackNumber);

                    // 2. 下载并嵌入封面（异步）
                    await EmbedAlbumArtAsync(file, song, cancellationToken).ConfigureAwait(false);

                    // 3. 获取并写入歌词（异步）
                    await WriteLyricsAsync(file, song, cancellationToken).ConfigureAwait(false);

                    // 4. 保存所有更改
                    file.Save();

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "AudioMetadataWriter",
                        $"✓ 元数据写入成功: {song.Name}");

                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    $"元数据写入被取消: {song.Name}");
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException(
                    "AudioMetadataWriter",
                    ex,
                    $"元数据写入失败: {song.Name}");
                return false;
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 写入基本元数据
        /// </summary>
        private void WriteBasicMetadata(TagLib.File file, SongInfo song, int? trackNumber)
        {
            var tag = file.Tag;

            // 标题
            if (!string.IsNullOrWhiteSpace(song.Name))
            {
                tag.Title = song.Name;
            }

            // 艺术家
            if (!string.IsNullOrWhiteSpace(song.Artist))
            {
                tag.Performers = new[] { song.Artist };
                tag.AlbumArtists = new[] { song.Artist };
            }

            // 专辑
            if (!string.IsNullOrWhiteSpace(song.Album))
            {
                tag.Album = song.Album;
            }

            // 曲目编号
            if (trackNumber.HasValue && trackNumber.Value > 0)
            {
                tag.Track = (uint)trackNumber.Value;
            }

            // 发行年份（从 PublishTime 解析）
            if (!string.IsNullOrWhiteSpace(song.PublishTime))
            {
                if (DateTime.TryParse(song.PublishTime, out DateTime publishDate))
                {
                    tag.Year = (uint)publishDate.Year;
                }
            }

            // 注释（包含歌曲ID和来源信息）
            tag.Comment = $"网易云音乐 ID: {song.Id}\n来源: 易听 YTPlayer\n下载时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "AudioMetadataWriter",
                $"已写入基本元数据: 标题={song.Name}, 艺术家={song.Artist}, 专辑={song.Album}, 曲目={trackNumber?.ToString() ?? "无"}");
        }

        /// <summary>
        /// 下载并嵌入封面图片
        /// </summary>
        private async Task EmbedAlbumArtAsync(TagLib.File file, SongInfo song, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(song.PicUrl))
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    "封面URL为空，跳过封面嵌入");
                return;
            }

            try
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    $"开始下载封面: {song.PicUrl}");

                // 下载封面数据
                byte[] coverData = await _httpClient.GetByteArrayAsync(song.PicUrl).ConfigureAwait(false);

                // 检查取消
                cancellationToken.ThrowIfCancellationRequested();

                // 检查大小
                if (coverData.Length > MAX_COVER_SIZE_BYTES)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "AudioMetadataWriter",
                        $"封面图片过大 ({coverData.Length} bytes)，跳过嵌入");
                    return;
                }

                // 创建图片对象
                var picture = new TagLib.Picture(new TagLib.ByteVector(coverData))
                {
                    Type = TagLib.PictureType.FrontCover,
                    Description = "Album Cover",
                    MimeType = GetMimeTypeFromUrl(song.PicUrl)
                };

                // 嵌入封面
                file.Tag.Pictures = new TagLib.IPicture[] { picture };

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    $"✓ 封面嵌入成功: {coverData.Length} bytes");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException(
                    "AudioMetadataWriter",
                    ex,
                    "封面下载或嵌入失败（非致命错误）");
            }
        }

        /// <summary>
        /// 获取并写入歌词
        /// </summary>
        private async Task WriteLyricsAsync(TagLib.File file, SongInfo song, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(song.Id))
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Warning,
                    "AudioMetadataWriter",
                    "歌曲ID为空，跳过歌词写入");
                return;
            }

            try
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    $"开始获取歌词: songId={song.Id}");

                // 从API获取歌词
                var lyricInfo = await _apiClient.GetLyricsAsync(song.Id).ConfigureAwait(false);

                // 检查取消
                cancellationToken.ThrowIfCancellationRequested();

                if (lyricInfo == null)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "AudioMetadataWriter",
                        "API返回空歌词，跳过歌词写入");
                    return;
                }

                // 优先使用 Lyric（标准 LRC 格式），如果没有则使用 YrcLyric
                string lyricsText = !string.IsNullOrWhiteSpace(lyricInfo.Lyric)
                    ? lyricInfo.Lyric
                    : lyricInfo.YrcLyric;

                if (string.IsNullOrWhiteSpace(lyricsText))
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "AudioMetadataWriter",
                        "歌词内容为空，跳过歌词写入");
                    return;
                }

                // 如果有翻译，附加到歌词末尾
                if (!string.IsNullOrWhiteSpace(lyricInfo.TLyric))
                {
                    lyricsText += "\n\n=== 翻译 ===\n" + lyricInfo.TLyric;
                }

                // 如果有罗马音，附加到歌词末尾
                if (!string.IsNullOrWhiteSpace(lyricInfo.RomaLyric))
                {
                    lyricsText += "\n\n=== 罗马音 ===\n" + lyricInfo.RomaLyric;
                }

                // 写入歌词到 Lyrics 标签（ID3v2 USLT 或 FLAC Vorbis Comment）
                file.Tag.Lyrics = lyricsText;

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "AudioMetadataWriter",
                    $"✓ 歌词写入成功: {lyricsText.Length} 字符, 翻译={!string.IsNullOrWhiteSpace(lyricInfo.TLyric)}, 罗马音={!string.IsNullOrWhiteSpace(lyricInfo.RomaLyric)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException(
                    "AudioMetadataWriter",
                    ex,
                    "歌词获取或写入失败（非致命错误）");
            }
        }

        /// <summary>
        /// 从URL推断MIME类型
        /// </summary>
        private string GetMimeTypeFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "image/jpeg";

            url = url.ToLowerInvariant();

            if (url.Contains(".png"))
                return "image/png";
            if (url.Contains(".jpg") || url.Contains(".jpeg"))
                return "image/jpeg";
            if (url.Contains(".gif"))
                return "image/gif";
            if (url.Contains(".bmp"))
                return "image/bmp";
            if (url.Contains(".webp"))
                return "image/webp";

            // 默认返回 JPEG
            return "image/jpeg";
        }

        #endregion

        #region 释放资源

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        #endregion
    }
}
