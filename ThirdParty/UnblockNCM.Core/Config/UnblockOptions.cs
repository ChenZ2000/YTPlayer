using System;
using System.Collections.Generic;
using System.Linq;

namespace UnblockNCM.Core.Config
{
    /// <summary>
    /// Runtime options aligned with the original NodeJS CLI flags.
    /// </summary>
    public sealed class UnblockOptions
    {
        public string Address { get; set; } = string.Empty; // 0.0.0.0 by default
        /// <summary>HTTP and HTTPS ports. Example: [8080,8081]</summary>
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8081;
        public string UpstreamProxy { get; set; }
        public string ForceHost { get; set; }
        public List<string> MatchOrder { get; set; } = new List<string>();
        public string Token { get; set; }
        public string Endpoint { get; set; } = "https://music.163.com";
        public bool Strict { get; set; }
        public string CnRelay { get; set; }
        public bool EnableLocalVip { get; set; }
        public bool EnableLocalSvip { get; set; }
        public List<long> LocalVipUids { get; set; } = new List<long>();
        public bool BlockAds { get; set; }
        public bool DisableUpgradeCheck { get; set; }
        public string NeteaseCookie { get; set; }
        public int MinBr { get; set; }
        public bool SelectMaxBr { get; set; }
        public bool FollowSourceOrder { get; set; }
        public bool EnableFlac { get; set; }
        public bool NoCache { get; set; }
        public string SignCertPath { get; set; }
        public string SignKeyPath { get; set; }
        public string LogFile { get; set; }
        public string LogLevel { get; set; } = "info";

        public IEnumerable<string> Whitelist { get; set; } = new[]
        {
            "://[\\w.]*music\\.126\\.net",
            "://[\\w.]*vod\\.126\\.net",
            "://acstatic-dun.126.net",
            "://[\\w.]*\\.netease.com",
            "://[\\w.]*\\.163yun.com",
        };

        public IEnumerable<string> Blacklist { get; set; } = new[]
        {
            "://127\\.\\d+\\.\\d+\\.\\d+",
            "://localhost"
        };

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Endpoint)) Endpoint = string.Empty;
            if (Endpoint == "-") Endpoint = string.Empty;

            if (MatchOrder == null) MatchOrder = new List<string>();
            MatchOrder = MatchOrder.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        }
    }
}
