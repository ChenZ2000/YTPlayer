using System;
using System.Collections.Generic;
using System.Linq;

namespace YTPlayer.Models
{
    /// <summary>Validated request choices for /api/v1/radio/get; wire spelling is case-sensitive.</summary>
    public sealed class PersonalFmMode
    {
        public string Mode { get; }
        public string? SubMode { get; }
        public string Name { get; }
        public string? Group { get; }
        public string Key => SubMode == null ? Mode : Mode + ":" + SubMode;

        private PersonalFmMode(string mode, string? subMode, string name, string? group = null)
        {
            Mode = mode;
            SubMode = subMode;
            Name = name;
            Group = group;
        }

        // Source: api-enhanced/module/personal_fm_mode.js and its mode documentation.
        // Keep these tokens verbatim, including ORIGINAL_MUSICIAL and FRANCH.
        public static IReadOnlyList<PersonalFmMode> All { get; } = Array.AsReadOnly(new[]
        {
            new PersonalFmMode("DEFAULT", null, "默认模式"),
            new PersonalFmMode("FAMILIAR", null, "熟悉模式"),
            new PersonalFmMode("EXPLORE", null, "探索模式"),
            new PersonalFmMode("PUZZLE_MODE_RCMD", null, "拼图模式"),
            new PersonalFmMode("SCENE_RCMD", "EXERCISE", "运动", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "FOCUS", "专注", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "NIGHT_EMO", "伤感", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "SLEEP_HELP", "助眠", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "RELAX", "放松", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "CHEERFUL", "欢快", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "LYRICAL", "抒情", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "CURE", "治愈", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "SWEET", "情歌", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "RHYTHM_BLUES", "R&B", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "RAINY", "雨天", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "GAMES", "游戏", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "RAP", "说唱", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "K_POP", "K-Pop", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "ORIGINAL_MUSICIAL", "宝藏原创", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "ELECTRONIC", "电音", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "COMMUTE", "出行", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "TAKE_SHOWER", "洗澡", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "COFFEE_SHOP", "咖啡馆", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "ROCK", "摇滚", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "INSPIRATIONAL", "励志", "场景与心情"),
            new PersonalFmMode("SCENE_RCMD", "CHINESE", "华语", "语言与地区"),
            new PersonalFmMode("SCENE_RCMD", "ENGLISH", "欧美", "语言与地区"),
            new PersonalFmMode("SCENE_RCMD", "YUEYU", "粤语", "语言与地区"),
            new PersonalFmMode("SCENE_RCMD", "MANYAO", "慢摇DJ", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "JINGDIAN", "经典", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "LIGHT", "轻音乐", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "GUOFENG", "国风", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "FOLK", "民谣", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "ACG", "二次元", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "GUDIAN", "古典", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "JAZZ", "爵士", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "JAPANESE", "日语", "语言与地区"),
            new PersonalFmMode("SCENE_RCMD", "GLOBAL", "全球", "语言与地区"),
            new PersonalFmMode("SCENE_RCMD", "FRANCH", "法语", "语言与地区"),
            new PersonalFmMode("SCENE_RCMD", "BLUE", "蓝调", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "DANCE", "舞蹈", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "LATIN", "拉丁", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "PUNK", "放克", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "COUNTRY", "乡村乐", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "MUSICAL", "音乐剧", "曲风与主题"),
            new PersonalFmMode("SCENE_RCMD", "YINGSHI", "影视", "曲风与主题"),
        });

        public static PersonalFmMode Default => All[0];
        public static PersonalFmMode FromKey(string? key) =>
            All.FirstOrDefault(mode => string.Equals(mode.Key, key, StringComparison.Ordinal)) ?? Default;
    }
}
