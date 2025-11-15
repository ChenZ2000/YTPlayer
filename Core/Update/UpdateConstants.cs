using System;

namespace YTPlayer.Update
{
    internal static class UpdateConstants
    {
        public const string DefaultEndpoint = "https://yt.chenz.cloud/update.php";

        public const string DefaultPlanFileName = "update-plan.json";

        public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(15);

        public static string CreateUserAgent(string product, string version)
        {
            if (string.IsNullOrWhiteSpace(product))
            {
                product = "YTPlayer";
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                version = "0.0.0";
            }

            return $"{product}/{version}";
        }
    }
}
