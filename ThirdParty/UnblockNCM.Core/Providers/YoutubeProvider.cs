using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Utils;
using System.Linq;

namespace UnblockNCM.Core.Providers
{
    /// <summary>
    /// Youtube provider implemented via yt-dlp binary.
    /// </summary>
    public class YoutubeProvider : IProvider
    {
        private readonly string _ytDlpPath;

        public YoutubeProvider(string pathToYtDlp = "yt-dlp")
        {
            _ytDlpPath = pathToYtDlp;
        }

        private string BuildArguments(string query) => $"-f 140 --dump-json {query}";
        private string ByKeyword(string keyword) => $"ytsearch1:{keyword}";

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var args = BuildArguments(ByKeyword(info.Keyword));
            var (code, stdout, stderr) = await ProcessRunner.RunAsync(_ytDlpPath, args);
            if (code != 0) throw new InvalidOperationException($"yt-dlp exit {code}: {stderr}");

            var json = JObject.Parse(stdout);
            var url = json.Value<string>("url");
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("youtube empty url");

            return new AudioResult
            {
                Url = url,
                BitRate = 128000,
                Source = "youtube",
                DurationMs = info.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" },
                    { "Referer", "https://www.youtube.com" }
                }
            };
        }
    }
}
