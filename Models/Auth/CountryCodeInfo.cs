using System;

namespace YTPlayer.Models.Auth
{
    /// <summary>
    /// 国家/地区区号信息
    /// </summary>
    public class CountryCodeInfo
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string EnglishName { get; set; } = string.Empty;

        public string DisplayName
        {
            get
            {
                string primary = !string.IsNullOrWhiteSpace(Name) ? Name : EnglishName;
                if (string.IsNullOrWhiteSpace(primary))
                {
                    primary = "未知";
                }
                return $"{primary} +{Code}";
            }
        }
    }
}
