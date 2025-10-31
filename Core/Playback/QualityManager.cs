using System;
using System.Collections.Generic;
using System.Linq;
using YTPlayer.Core.Auth;
using YTPlayer.Models;
using YTPlayer.Models.Playback;

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 音质管理器
    /// 处理登录状态联动、VIP权限验证、音质降级等逻辑
    /// </summary>
    public sealed class QualityManager
    {
        private readonly ConfigManager _configManager;
        private readonly AuthContext _authContext;

        /// <summary>
        /// 音质名称到内部标识的映射
        /// ⭐ 修复：与 NeteaseApiClient.QualityMap 保持一致
        /// </summary>
        private static readonly Dictionary<string, string> QualityNameMap = new Dictionary<string, string>
        {
            { "标准音质", "standard" },
            { "极高音质", "exhigh" },
            { "无损音质", "lossless" },
            { "Hi-Res音质", "hires" },        // ⭐ 修复：Hi-Res音质 → hires（不是jymaster）
            { "高清环绕声", "jyeffect" },
            { "沉浸环绕声", "sky" },
            { "超清母带", "jymaster" }        // ⭐ 修复：超清母带 → jymaster（不是hires）
        };

        /// <summary>
        /// 内部标识到音质名称的映射
        /// ⭐ 修复：与 NeteaseApiClient.GetQualityDisplayName 保持一致
        /// </summary>
        private static readonly Dictionary<string, string> QualityLevelMap = new Dictionary<string, string>
        {
            { "standard", "标准音质" },
            { "exhigh", "极高音质" },
            { "lossless", "无损音质" },
            { "hires", "Hi-Res音质" },        // ⭐ 修复：hires → Hi-Res音质（不是超清母带）
            { "jyeffect", "高清环绕声" },
            { "sky", "沉浸环绕声" },
            { "jymaster", "超清母带" }        // ⭐ 修复：jymaster → 超清母带（不是Hi-Res音质）
        };

        public QualityManager(ConfigManager configManager, AuthContext authContext)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _authContext = authContext ?? throw new ArgumentNullException(nameof(authContext));
        }

        #region 启动和登录/退出处理

        /// <summary>
        /// 初始化默认音质（程序启动时调用）
        /// 未登录时默认"极高音质"，已登录保持用户配置
        /// </summary>
        public void InitializeDefaultQuality()
        {
            var config = _configManager.LoadConfig();
            bool isLoggedIn = _authContext.IsLoggedIn;

            // 如果配置中没有音质设置，设置默认值
            if (string.IsNullOrEmpty(config.DefaultQuality))
            {
                config.DefaultQuality = "极高音质"; // 默认极高
                _configManager.SaveConfig(config);
                return;
            }

            // 未登录时，强制降级到极高
            if (!isLoggedIn && !IsQualityAllowedForGuest(config.DefaultQuality))
            {
                Utils.DebugLogger.Log(Utils.LogLevel.Info, "QualityManager",
                    $"User not logged in, downgrading quality from {config.DefaultQuality} to 极高音质");

                config.DefaultQuality = "极高音质";
                _configManager.SaveConfig(config);
            }
        }

        /// <summary>
        /// 退出登录时处理（降级音质）
        /// </summary>
        public void OnLogout()
        {
            var config = _configManager.LoadConfig();

            if (!IsQualityAllowedForGuest(config.DefaultQuality))
            {
                Utils.DebugLogger.Log(Utils.LogLevel.Info, "QualityManager",
                    $"User logged out, downgrading quality from {config.DefaultQuality} to 极高音质");

                config.DefaultQuality = "极高音质"; // 降级到极高
                _configManager.SaveConfig(config);
            }
        }

        /// <summary>
        /// 登录成功时处理（可选：提示用户可以使用更高音质）
        /// </summary>
        public void OnLogin(int vipType)
        {
            Utils.DebugLogger.Log(Utils.LogLevel.Info, "QualityManager",
                $"User logged in with VIP type: {vipType}");

            // 可以在这里添加 UI 提示逻辑
        }

        #endregion

        #region 音质权限检查

        /// <summary>
        /// 检查特定音质是否允许游客（未登录用户）使用
        /// </summary>
        /// <param name="qualityName">音质名称</param>
        /// <returns>是否允许</returns>
        public bool IsQualityAllowedForGuest(string qualityName)
        {
            // 游客仅允许：标准、极高
            return qualityName == "标准音质" || qualityName == "极高音质";
        }

        /// <summary>
        /// 获取可用音质列表（根据登录状态和 VIP 等级）
        /// </summary>
        /// <returns>可用音质名称列表</returns>
        public List<string> GetAvailableQualities()
        {
            bool isLoggedIn = _authContext.IsLoggedIn;

            if (!isLoggedIn)
            {
                // 未登录：仅标准和极高
                return new List<string> { "标准音质", "极高音质" };
            }

            // 已登录：根据 VIP 等级返回
            var config = _configManager.LoadConfig();
            int vipType = config.LoginVipType;

            if (vipType >= 11)
            {
                // SVIP：所有音质（参考 NeteaseApiClient.QualityOrder）
                return new List<string>
                {
                    "标准音质", "极高音质", "无损音质", "Hi-Res音质", "高清环绕声", "沉浸环绕声", "超清母带"
                };
            }
            else if (vipType >= 1)
            {
                // VIP：up to Hi-Res音质
                return new List<string>
                {
                    "标准音质", "极高音质", "无损音质", "Hi-Res音质"
                };
            }
            else
            {
                // 普通用户：标准、极高
                return new List<string>
                {
                    "标准音质", "极高音质"
                };
            }
        }

        /// <summary>
        /// 检查特定音质是否在当前用户权限范围内
        /// </summary>
        public bool IsQualityAvailable(string qualityName)
        {
            return GetAvailableQualities().Contains(qualityName);
        }

        #endregion

        #region 音质选择逻辑

        /// <summary>
        /// 选择最佳可用音质（从歌曲数据模型中）
        /// </summary>
        /// <param name="song">歌曲数据模型</param>
        /// <returns>选中的音质标识（如 "exhigh"）</returns>
        public string SelectBestAvailableQuality(SongDataModel song)
        {
            if (song == null)
                throw new ArgumentNullException(nameof(song));

            var config = _configManager.LoadConfig();
            string preferredName = config.DefaultQuality ?? "极高音质";
            string preferredLevel = MapQualityNameToLevel(preferredName);

            var availableQualities = song.AvailableQualities.Value;

            // 如果没有可用音质信息，返回首选音质（稍后会验证）
            if (availableQualities == null || availableQualities.Count == 0)
            {
                return preferredLevel;
            }

            // 从首选音质开始降级查找
            var qualityOrder = new[] { "jymaster", "sky", "hires", "lossless", "exhigh", "higher", "standard" };
            int startIndex = Array.IndexOf(qualityOrder, preferredLevel);

            if (startIndex < 0)
                startIndex = Array.IndexOf(qualityOrder, "exhigh"); // 默认从极高开始

            for (int i = startIndex; i < qualityOrder.Length; i++)
            {
                string level = qualityOrder[i];

                if (availableQualities.TryGetValue(level, out var qualityInfo) && qualityInfo.IsAvailable)
                {
                    return level;
                }
            }

            // 如果都不可用，返回 standard（最低保障）
            return "standard";
        }

        /// <summary>
        /// 从 NeteaseApiClient 的 QualityLevel 枚举转换为内部标识
        /// </summary>
        public string MapQualityLevelToString(QualityLevel level)
        {
            switch (level)
            {
                case QualityLevel.Standard: return "standard";
                case QualityLevel.Higher: return "higher";
                case QualityLevel.ExHigh: return "exhigh";
                case QualityLevel.Lossless: return "lossless";
                case QualityLevel.HiRes: return "hires";
                case QualityLevel.JyMaster: return "jymaster";
                case QualityLevel.Sky: return "sky";
                default: return "standard";
            }
        }

        /// <summary>
        /// 从内部标识转换为 NeteaseApiClient 的 QualityLevel 枚举
        /// </summary>
        public QualityLevel MapStringToQualityLevel(string level)
        {
            switch (level?.ToLower())
            {
                case "standard": return QualityLevel.Standard;
                case "higher": return QualityLevel.Higher;
                case "exhigh": return QualityLevel.ExHigh;
                case "lossless": return QualityLevel.Lossless;
                case "hires": return QualityLevel.HiRes;
                case "jymaster": return QualityLevel.JyMaster;
                case "sky": return QualityLevel.Sky;
                default: return QualityLevel.Standard;
            }
        }

        /// <summary>
        /// 从音质名称映射到内部标识
        /// </summary>
        public string MapQualityNameToLevel(string qualityName)
        {
            if (string.IsNullOrEmpty(qualityName))
                return "exhigh"; // 默认极高

            if (QualityNameMap.TryGetValue(qualityName, out var level))
            {
                return level;
            }

            // 如果传入的已经是内部标识，直接返回
            if (QualityLevelMap.ContainsKey(qualityName.ToLower()))
            {
                return qualityName.ToLower();
            }

            return "exhigh"; // 默认极高
        }

        /// <summary>
        /// 从内部标识映射到音质名称
        /// </summary>
        public string MapLevelToQualityName(string level)
        {
            if (string.IsNullOrEmpty(level))
                return "极高音质";

            if (QualityLevelMap.TryGetValue(level.ToLower(), out var name))
            {
                return name;
            }

            return "极高音质"; // 默认极高
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 获取当前配置的音质（内部标识）
        /// </summary>
        public string GetCurrentQualityLevel()
        {
            var config = _configManager.LoadConfig();
            return MapQualityNameToLevel(config.DefaultQuality);
        }

        /// <summary>
        /// 设置当前音质（音质名称）
        /// </summary>
        public void SetCurrentQuality(string qualityName)
        {
            if (!IsQualityAvailable(qualityName))
            {
                throw new InvalidOperationException($"Quality '{qualityName}' is not available for current user");
            }

            var config = _configManager.LoadConfig();
            config.DefaultQuality = qualityName;
            _configManager.SaveConfig(config);

            Utils.DebugLogger.Log(Utils.LogLevel.Info, "QualityManager",
                $"Quality changed to: {qualityName}");
        }

        #endregion
    }
}
