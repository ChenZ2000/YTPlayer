using System.Collections.Generic;

namespace UnblockNCM.Core.Services
{
    internal static class NeteaseTargets
    {
        public static readonly HashSet<string> Paths = new HashSet<string>
        {
            "/api/v3/playlist/detail",
            "/api/v3/song/detail",
            "/api/v6/playlist/detail",
            "/api/album/play",
            "/api/artist/privilege",
            "/api/album/privilege",
            "/api/v1/artist",
            "/api/v1/artist/songs",
            "/api/v2/artist/songs",
            "/api/artist/top/song",
            "/api/v1/album",
            "/api/album/v3/detail",
            "/api/playlist/privilege",
            "/api/song/enhance/player/url",
            "/api/song/enhance/player/url/v1",
            "/api/song/enhance/download/url",
            "/api/song/enhance/download/url/v1",
            "/api/song/enhance/privilege",
            "/api/ad",
            "/batch",
            "/api/batch",
            "/api/listen/together/privilege/get",
            "/api/playmode/intelligence/list",
            "/api/v1/search/get",
            "/api/v1/search/song/get",
            "/api/search/complex/get",
            "/api/search/complex/page",
            "/api/search/pc/complex/get",
            "/api/search/pc/complex/page",
            "/api/search/song/list/page",
            "/api/search/song/page",
            "/api/cloudsearch/pc",
            "/api/v1/playlist/manipulate/tracks",
            "/api/song/like",
            "/api/v1/play/record",
            "/api/playlist/v4/detail",
            "/api/v1/radio/get",
            "/api/v1/discovery/recommend/songs",
            "/api/usertool/sound/mobile/promote",
            "/api/usertool/sound/mobile/theme",
            "/api/usertool/sound/mobile/animationList",
            "/api/usertool/sound/mobile/all",
            "/api/usertool/sound/mobile/detail",
            "/api/vipauth/app/auth/query",
            "/api/music-vip-membership/client/vip/info",
        };
    }
}
