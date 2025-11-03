using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 用户账号信息
    /// 参考: NeteaseCloudMusicApi/module/user_account.js
    /// </summary>
    public class UserAccountInfo
    {
        /// <summary>用户ID</summary>
        public long UserId { get; set; }

        /// <summary>昵称</summary>
        public string Nickname { get; set; } = string.Empty;

        /// <summary>头像URL</summary>
        public string AvatarUrl { get; set; } = string.Empty;

        /// <summary>个性签名</summary>
        public string Signature { get; set; } = string.Empty;

        /// <summary>
        /// VIP类型
        /// 0=普通用户, 1=VIP, 11=黑胶VIP
        /// </summary>
        public int VipType { get; set; }

        /// <summary>VIP类型名称</summary>
        public string VipTypeName
        {
            get
            {
                switch (VipType)
                {
                    case 11:
                        return "黑胶VIP";
                    case 1:
                        return "VIP";
                    default:
                        return "免费用户";
                }
            }
        }

        /// <summary>用户等级</summary>
        public int Level { get; set; }

        /// <summary>
        /// 性别
        /// 0=保密, 1=男, 2=女
        /// </summary>
        public int Gender { get; set; }

        /// <summary>性别名称</summary>
        public string GenderName
        {
            get
            {
                switch (Gender)
                {
                    case 1:
                        return "男";
                    case 2:
                        return "女";
                    default:
                        return "保密";
                }
            }
        }

        /// <summary>生日</summary>
        public DateTime? Birthday { get; set; }

        /// <summary>所在省份</summary>
        public int Province { get; set; }

        /// <summary>所在城市</summary>
        public int City { get; set; }

        /// <summary>听歌数量</summary>
        public int ListenSongs { get; set; }

        /// <summary>粉丝数</summary>
        public int Followers { get; set; }

        /// <summary>关注数</summary>
        public int Follows { get; set; }

        /// <summary>动态数</summary>
        public int EventCount { get; set; }

        /// <summary>创建时间</summary>
        public DateTime? CreateTime { get; set; }

        /// <summary>艺人名称（如果是音乐人）</summary>
        public string ArtistName { get; set; } = string.Empty;

        /// <summary>艺人ID（如果是音乐人）</summary>
        public long? ArtistId { get; set; }

        /// <summary>用户类型（0=普通用户, 4=音乐人等）</summary>
        public int UserType { get; set; }

        /// <summary>
        /// 用户类型名称
        /// </summary>
        public string UserTypeName
        {
            get
            {
                switch (UserType)
                {
                    case 4:
                        return "音乐人";
                    case 0:
                        return "普通用户";
                    default:
                        return $"类型{UserType}";
                }
            }
        }

        /// <summary>歌单数量</summary>
        public int PlaylistCount { get; set; }

        /// <summary>歌单被收藏数</summary>
        public int PlaylistBeSubscribedCount { get; set; }

        /// <summary>注册天数</summary>
        public int CreateDays { get; set; }

        /// <summary>认证类型列表（JSON格式）</summary>
        public string AuthTypeDesc { get; set; } = string.Empty;

        /// <summary>DJ节目数</summary>
        public int DjProgramCount { get; set; }

        /// <summary>是否是黑名单</summary>
        public bool InBlacklist { get; set; }
    }
}
