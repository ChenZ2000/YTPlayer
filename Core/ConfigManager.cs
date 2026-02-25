using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Models;
using YTPlayer.Utils;
using YTPlayer.Core.Auth;

namespace YTPlayer.Core
{
    /// <summary>
    /// 配置管理器 - 负责配置文件的加载、保存和管理
    /// 便携式设计：配置文件保存在程序目录，与 Python 版本保持一致
    /// </summary>
    public class ConfigManager
    {
        private static readonly object _lock = new object();
        private static ConfigManager? _instance;

        internal const int MaxSearchHistoryCount = 50;
        internal const int MaxLastPlayingQueueCount = 300;

        /// <summary>
        /// 获取程序所在目录
        /// </summary>
        private static string GetApplicationDirectory()
        {
            try
            {
                return PathHelper.ApplicationRootDirectory;
            }
            catch
            {
                return Directory.GetCurrentDirectory();
            }
        }

        /// <summary>
        /// 配置文件路径（保存在程序目录，便携式设计）
        /// </summary>
        private static readonly string ConfigFilePath = Path.Combine(
            GetApplicationDirectory(),
            "config.json"
        );

        /// <summary>
        /// 配置目录路径
        /// </summary>
        private static readonly string ConfigDirectory = GetApplicationDirectory();

        /// <summary>
        /// 单例实例
        /// </summary>
        public static ConfigManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ConfigManager();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 私有构造函数，确保单例模式
        /// </summary>
        private ConfigManager()
        {
            EnsureConfigDirectoryExists();
            MigrateLegacyConfigFileIfNeeded();
        }

        /// <summary>
        /// 确保配置目录存在
        /// </summary>
        private void EnsureConfigDirectoryExists()
        {
            try
            {
                if (!Directory.Exists(ConfigDirectory))
                {
                    Directory.CreateDirectory(ConfigDirectory);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法创建配置目录: {ConfigDirectory}", ex);
            }
        }

        private void MigrateLegacyConfigFileIfNeeded()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    return;
                }

                if (!IsDevelopmentBuildDirectory(ConfigDirectory))
                {
                    return;
                }

                string projectRoot = Path.GetFullPath(Path.Combine(ConfigDirectory, "..", ".."));
                string legacyPath = Path.Combine(projectRoot, "config.json");
                if (!File.Exists(legacyPath))
                {
                    return;
                }

                File.Copy(legacyPath, ConfigFilePath, false);
                System.Diagnostics.Debug.WriteLine($"[ConfigManager] legacy config.json 已复制到程序目录: {ConfigFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConfigManager] 迁移 legacy config.json 失败: {ex.Message}");
            }
        }

        private static bool IsDevelopmentBuildDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            string normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalized.EndsWith(@"bin\Debug", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(@"bin\Release", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(@"bin/Debug", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(@"bin/Release", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取默认下载路径（相对路径：程序目录的 Downloads 文件夹）
        /// </summary>
        /// <returns>默认下载路径（相对路径）</returns>
        public static string GetDefaultDownloadPath()
        {
            // 返回相对路径：程序目录的 Downloads 文件夹
            // 例如：如果程序在 D:\Program\YTPlayer\bin\Debug\，则返回 .\Downloads
            return Path.Combine(".", "Downloads");
        }

        /// <summary>
        /// 获取下载目录的完整路径（将相对路径转换为绝对路径）
        /// </summary>
        /// <param name="downloadDirectory">下载目录（可能是相对路径或绝对路径）</param>
        /// <returns>绝对路径</returns>
        public static string GetFullDownloadPath(string downloadDirectory)
        {
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                downloadDirectory = GetDefaultDownloadPath();
            }

            // 如果是相对路径，转换为基于程序目录的绝对路径
            if (!Path.IsPathRooted(downloadDirectory))
            {
                downloadDirectory = Path.Combine(GetApplicationDirectory(), downloadDirectory);
            }

            return Path.GetFullPath(downloadDirectory);
        }

        /// <summary>
        /// 从文件加载配置
        /// </summary>
        /// <returns>配置对象</returns>
        public ConfigModel Load()
        {
            lock (_lock)
            {
                try
                {
                    // 如果配置文件不存在，创建默认配置
                    if (!File.Exists(ConfigFilePath))
                    {
                        var defaultConfig = CreateDefaultConfig();
                        Save(defaultConfig);
                        return defaultConfig;
                    }

                    // 读取配置文件
                    string json = File.ReadAllText(ConfigFilePath);

                    bool hasFocusFollowPlaybackField = false;
                    bool hasValidFocusFollowPlaybackField = false;
                    try
                    {
                        var root = JObject.Parse(json);
                        if (root.TryGetValue(nameof(ConfigModel.FocusFollowPlayback), StringComparison.OrdinalIgnoreCase, out JToken? focusFollowPlaybackToken))
                        {
                            hasFocusFollowPlaybackField = true;
                            hasValidFocusFollowPlaybackField = focusFollowPlaybackToken.Type == JTokenType.Boolean;
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore here; the outer JsonException handler will process corrupted config.
                    }

                    // Deserialize config.
                    var config = JsonConvert.DeserializeObject<ConfigModel>(json);

                    // Fallback to default config when deserialization fails.
                    if (config == null)
                    {
                        config = CreateDefaultConfig();
                        Save(config);
                    }
                    else
                    {
                        bool changed = false;
                        if (!hasFocusFollowPlaybackField || !hasValidFocusFollowPlaybackField)
                        {
                            config.FocusFollowPlayback = true;
                            changed = true;
                        }

                        // Validate and auto-fix persisted config.
                        changed = ValidateAndFixConfig(config) || changed;
                        if (changed)
                        {
                            Save(config);
                        }
                    }

                    return config;

                }
                catch (JsonException ex)
                {
                    // JSON 解析错误，备份旧文件并创建新配置
                    BackupCorruptedConfig();
                    var defaultConfig = CreateDefaultConfig();
                    Save(defaultConfig);

                    throw new InvalidOperationException("配置文件损坏，已创建新的默认配置", ex);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException("无法读取配置文件", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("加载配置时发生未知错误", ex);
                }
            }
        }

        /// <summary>
        /// 保存配置到文件
        /// </summary>
        /// <param name="config">配置对象</param>
        public void Save(ConfigModel config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config), "配置对象不能为空");
            }

            lock (_lock)
            {
                try
                {
                    // 确保配置目录存在
                    EnsureConfigDirectoryExists();

                    // 验证和修复配置
                    ValidateAndFixConfig(config);

                    // 序列化配置
                    var settings = new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        NullValueHandling = NullValueHandling.Include
                    };

                    string json = JsonConvert.SerializeObject(config, settings);

                    // 写入临时文件
                    string tempFile = ConfigFilePath + ".tmp";
                    File.WriteAllText(tempFile, json);

                    // 用临时文件替换原文件（原子操作，不再生成 .bak 文件）
                    File.Copy(tempFile, ConfigFilePath, true);
                    File.Delete(tempFile);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException("无法保存配置文件", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("保存配置时发生未知错误", ex);
                }
            }
        }

        /// <summary>
        /// 创建默认配置（参考 Python 版本 Netease-music.py:2505-2525）
        /// </summary>
        /// <returns>默认配置对象</returns>
        public ConfigModel CreateDefaultConfig()
        {
            return new ConfigModel
            {
                // 基础设置
                Volume = 0.5,
                PlaybackOrder = "顺序播放",
                DefaultQuality = "超清母带",

                // 搜索历史
                SearchHistory = new List<string>(),

                // 下载设置
                DownloadDirectory = GetDefaultDownloadPath(),
                OutputDevice = AudioOutputDeviceInfo.WindowsDefaultId,

                // 听歌识曲
                RecognitionInputDeviceId = AudioInputDeviceInfo.WindowsDefaultId,
                RecognitionAutoCloseDialog = true,
                // 识曲后端：默认指向公开可用的 api-enhanced 部署
                RecognitionApiBaseUrl = "http://159.75.21.45:5000",

                // Note: Account-related fields (Cookies, MusicU, CsrfToken, MusicA, etc.) are now managed by AccountState
                // Note: Device fingerprint fields are now managed by AccountState
                // Note: Window configuration fields are optional and populated on demand

                // UI 和交互
                FollowCursor = true,
                FocusFollowPlayback = true,
                SeekMinIntervalMs = 30,
                SequenceNumberHidden = false,
                CommentSequenceNumberHidden = false,
                TransferListSequenceNumberHidden = false,
                BatchDownloadDialogSequenceNumberHidden = false,
                ControlBarHidden = false,

                // 其他设置
                LastPlayingSongId = string.Empty,
                LastPlayingSource = string.Empty,
                LastPlayingSongName = string.Empty,
                LastPlayingDuration = 0,
                LastPlayingQueue = new List<string>(),
                LastPlayingQueueIndex = -1,
                LastPlayingSourceIndex = -1,
                LyricsFontSize = 12
            };
        }

        /// <summary>
        /// 验证和修复配置（参考 Python 版本的默认值设置逻辑）
        /// </summary>
        /// <param name="config">配置对象</param>
        private bool ValidateAndFixConfig(ConfigModel config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            bool changed = false;

            // 验证音量范围（0-1）
            if (config.Volume < 0 || config.Volume > 1)
            {
                config.Volume = 0.5;
                changed = true;
            }

            // 验证播放顺序
            var validPlaybackOrders = new[] { "顺序播放", "列表循环", "单曲循环", "随机播放" };
            if (string.IsNullOrWhiteSpace(config.PlaybackOrder) ||
                !System.Array.Exists(validPlaybackOrders, x => x == config.PlaybackOrder))
            {
                config.PlaybackOrder = "顺序播放";
                changed = true;
            }

            // 验证默认音质（使用完整名称，与 NeteaseApiClient.QualityMap 保持一致）
            var validQualities = new[] { "标准音质", "极高音质", "无损音质", "Hi-Res音质", "高清环绕声", "沉浸环绕声", "超清母带" };
            if (string.IsNullOrWhiteSpace(config.DefaultQuality) ||
                !System.Array.Exists(validQualities, x => x == config.DefaultQuality))
            {
                config.DefaultQuality = "超清母带";
                changed = true;
            }

            // 验证搜索历史
            if (config.SearchHistory == null)
            {
                config.SearchHistory = new List<string>();
                changed = true;
            }
            else
            {
                List<string> cleanedHistory = new List<string>(config.SearchHistory.Count);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string item in config.SearchHistory)
                {
                    if (string.IsNullOrWhiteSpace(item))
                    {
                        continue;
                    }

                    string trimmed = item.Trim();
                    if (!seen.Add(trimmed))
                    {
                        continue;
                    }

                    cleanedHistory.Add(trimmed);
                    if (cleanedHistory.Count >= MaxSearchHistoryCount)
                    {
                        break;
                    }
                }

                if (cleanedHistory.Count != config.SearchHistory.Count ||
                    !config.SearchHistory.SequenceEqual(cleanedHistory, StringComparer.OrdinalIgnoreCase))
                {
                    config.SearchHistory = cleanedHistory;
                    changed = true;
                }
            }

            // 验证下载目录路径（仅在为空时设置默认值，不要重置已有配置）
            if (string.IsNullOrWhiteSpace(config.DownloadDirectory))
            {
                config.DownloadDirectory = GetDefaultDownloadPath();
                changed = true;
            }

            // 尝试创建下载目录（如果失败，不重置配置，让用户自己处理）
            try
            {
                string fullPath = GetFullDownloadPath(config.DownloadDirectory);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }
            catch (Exception ex)
            {
                // 记录警告但不重置配置
                System.Diagnostics.Debug.WriteLine($"[ConfigManager] 无法创建下载目录: {ex.Message}");
            }

            // Note: Cookies and LoginVipType are now managed by AccountState, not ConfigModel
            // Note: Device fingerprint fields (DeviceId, SDeviceId, etc.) are now managed by AccountState
            // Note: NmtId, NtesNuid, WnmCid, MusicA, AntiCheatToken are managed by AccountState

            if (string.IsNullOrWhiteSpace(config.OutputDevice))
            {
                config.OutputDevice = AudioOutputDeviceInfo.WindowsDefaultId;
                changed = true;
            }

            // 验证听歌识曲相关配置
            if (string.IsNullOrWhiteSpace(config.RecognitionInputDeviceId))
            {
                config.RecognitionInputDeviceId = AudioInputDeviceInfo.WindowsDefaultId;
                changed = true;
            }

            // RecognitionAutoCloseDialog 为 bool，无需额外验证

            if (string.IsNullOrWhiteSpace(config.RecognitionApiBaseUrl))
            {
                config.RecognitionApiBaseUrl = "http://159.75.21.45:5000";
                changed = true;
            }
            else
            {
                var trimmed = config.RecognitionApiBaseUrl.Trim();
                Uri? parsed;
                bool isValid = Uri.TryCreate(trimmed, UriKind.Absolute, out parsed) &&
                               parsed != null &&
                               (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
                bool isLoopback = isValid && parsed != null &&
                                  (parsed.IsLoopback || parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

                if (!isValid || isLoopback)
                {
                    // 防止默认写死到本地端口导致连接被拒绝
                    config.RecognitionApiBaseUrl = "http://159.75.21.45:5000";
                    changed = true;
                }
                else if (!string.Equals(trimmed, config.RecognitionApiBaseUrl, StringComparison.Ordinal))
                {
                    config.RecognitionApiBaseUrl = trimmed;
                    changed = true;
                }
            }

            // 验证上次播放队列
            if (config.LastPlayingQueue == null)
            {
                config.LastPlayingQueue = new List<string>();
                changed = true;
            }
            else
            {
                var cleanedQueue = new List<string>(config.LastPlayingQueue.Count);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string id in config.LastPlayingQueue)
                {
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }
                    string trimmed = id.Trim();
                    if (!seen.Add(trimmed))
                    {
                        continue;
                    }
                    cleanedQueue.Add(trimmed);
                    if (cleanedQueue.Count >= MaxLastPlayingQueueCount)
                    {
                        break;
                    }
                }
                if (!config.LastPlayingQueue.SequenceEqual(cleanedQueue, StringComparer.OrdinalIgnoreCase))
                {
                    config.LastPlayingQueue = cleanedQueue;
                    changed = true;
                }
            }

            if (config.LastPlayingQueueIndex >= config.LastPlayingQueue.Count)
            {
                config.LastPlayingQueueIndex = (config.LastPlayingQueue.Count > 0) ? 0 : -1;
                changed = true;
            }
            if (config.LastPlayingQueueIndex < -1)
            {
                config.LastPlayingQueueIndex = -1;
                changed = true;
            }


            if (config.LastPlayingSourceIndex < -1)
            {
                config.LastPlayingSourceIndex = -1;
                changed = true;
            }

            if (config.LastPlayingDuration < 0)
            {
                config.LastPlayingDuration = 0;
                changed = true;
            }

            if (config.LastPlayingSource == null)
            {
                config.LastPlayingSource = string.Empty;
                changed = true;
            }

            if (config.LastPlayingSongName == null)
            {
                config.LastPlayingSongName = string.Empty;
                changed = true;
            }

            // 验证跳转间隔
            if (config.SeekMinIntervalMs < 0 || config.SeekMinIntervalMs > 1000)
            {
                config.SeekMinIntervalMs = 30;
                changed = true;
            }

            // Note: Window configuration fields are optional; keep defaults when absent

            // 验证歌词字体大小
            if (config.LyricsFontSize < 8 || config.LyricsFontSize > 32)
            {
                config.LyricsFontSize = 12;
                changed = true;
            }

            // 验证窗口布局
            if (config.WindowWidth.HasValue && config.WindowWidth.Value <= 0)
            {
                config.WindowWidth = null;
                changed = true;
            }
            if (config.WindowHeight.HasValue && config.WindowHeight.Value <= 0)
            {
                config.WindowHeight = null;
                changed = true;
            }
            if (config.WindowX.HasValue && !config.WindowWidth.HasValue)
            {
                config.WindowX = null;
                changed = true;
            }
            if (config.WindowY.HasValue && !config.WindowHeight.HasValue)
            {
                config.WindowY = null;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(config.WindowState))
            {
                string state = config.WindowState.Trim();
                if (!string.Equals(state, "Normal", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(state, "Maximized", StringComparison.OrdinalIgnoreCase))
                {
                    config.WindowState = null;
                    changed = true;
                }
                else if (!string.Equals(state, config.WindowState, StringComparison.Ordinal))
                {
                    config.WindowState = state;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// 备份损坏的配置文件
        /// </summary>
        private void BackupCorruptedConfig()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    string backupPath = Path.Combine(
                        ConfigDirectory,
                        $"config.corrupted.{timestamp}.json"
                    );
                    File.Copy(ConfigFilePath, backupPath, true);
                }
            }
            catch
            {
                // 忽略备份失败
            }
        }

        /// <summary>
        /// 重置为默认配置
        /// </summary>
        /// <returns>默认配置对象</returns>
        public ConfigModel Reset()
        {
            lock (_lock)
            {
                var defaultConfig = CreateDefaultConfig();
                Save(defaultConfig);
                return defaultConfig;
            }
        }

        /// <summary>
        /// 获取配置文件路径
        /// </summary>
        /// <returns>配置文件完整路径</returns>
        public string GetConfigFilePath()
        {
            return ConfigFilePath;
        }

        /// <summary>
        /// 检查配置文件是否存在
        /// </summary>
        /// <returns>配置文件是否存在</returns>
        public bool ConfigFileExists()
        {
            return File.Exists(ConfigFilePath);
        }
    }
}
