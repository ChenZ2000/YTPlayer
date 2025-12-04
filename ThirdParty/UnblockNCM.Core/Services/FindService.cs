using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Services
{
    public class FindService
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly string _scope = "find";
        private readonly bool _searchAlbum;
        private readonly bool _noCache;

        public FindService(HttpHelper http, CacheStorage cache, bool searchAlbum, bool noCache)
        {
            _http = http;
            _cache = cache;
            _searchAlbum = searchAlbum;
            _noCache = noCache;
        }

        private SongInfo GetFormatData(JObject data)
        {
            try
            {
                var info = new SongInfo
                {
                    Id = data.Value<string>("id"),
                    Name = (data.Value<string>("name") ?? string.Empty)
                        .Replace("（cover", "(")
                        .Replace("（翻自", "(")
                        .Replace("）", ")")
                        .Replace("）", ")"),
                    Duration = data.Value<long?>("duration") ?? 0,
                    Alias = data["alias"]?.Select(t => t.ToString()).ToList() ?? new(),
                    Album = new SongInfo.AlbumInfo
                    {
                        Id = data["album"]?["id"]?.ToString(),
                        Name = data["album"]?["name"]?.ToString()
                    },
                    Artists = data["artists"]?.Select(a => new SongInfo.ArtistInfo
                    {
                        Id = a["id"]?.ToString(),
                        Name = a["name"]?.ToString()
                    }).ToList() ?? new()
                };

                var artistSegment = string.Join(" / ", info.Artists.Select(a => a.Name));
                info.Keyword = $"{info.Name} - {artistSegment}";
                if (_searchAlbum)
                {
                    var album = info.Album?.Name;
                    if (!string.IsNullOrWhiteSpace(album) && album != info.Name)
                        info.Keyword += $" {album}";
                }
                return info;
            }
            catch (Exception ex)
            {
                Log.Error(_scope, "Failed to format song data", ex);
                return null;
            }
        }

        private async Task<SongInfo> FetchAsync(string id, CancellationToken ct)
        {
            var url = $"https://music.163.com/api/song/detail?ids=[{id}]";
            var resp = await _http.GetAsync(url, null, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"fetch fail {resp.StatusCode}");
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var song = json["songs"]?.FirstOrDefault() as JObject;
            if (song == null) throw new InvalidOperationException("empty songs");
            var info = GetFormatData(song);
            return info ?? throw new InvalidOperationException("format fail");
        }

        public Task<SongInfo> GetAsync(string id, JObject data, CancellationToken ct)
        {
            if (data != null) return Task.FromResult(GetFormatData(data));

            return _cache.GetOrAddAsync($"find:{id}", () => FetchAsync(id, ct), null, _noCache);
        }
    }
}
