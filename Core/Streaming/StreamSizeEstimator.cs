using System;

namespace YTPlayer.Core.Streaming
{
    public static class StreamSizeEstimator
    {
        public static string NormalizeQualityLevel(string? level)
        {
            if (string.IsNullOrWhiteSpace(level))
            {
                return string.Empty;
            }

            level = level.Trim().ToLowerInvariant();
            switch (level)
            {
                case "high":
                    return "exhigh";
                case "surroundhd":
                    return "jyeffect";
                case "dolby":
                    return "sky";
                case "master":
                    return "jymaster";
                default:
                    return level;
            }
        }

        public static int GetApproxBitrateForLevel(string? level)
        {
            level = NormalizeQualityLevel(level);
            switch (level)
            {
                case "standard":
                    return 128000;
                case "higher":
                    return 192000;
                case "exhigh":
                    return 320000;
                case "lossless":
                    return 999000;
                case "hires":
                case "jyeffect":
                    return 2000000;
                case "sky":
                    return 3200000;
                case "jymaster":
                    return 4000000;
                default:
                    return 0;
            }
        }

        public static int NormalizeDurationSeconds(int durationSeconds)
        {
            if (durationSeconds <= 0)
            {
                return 0;
            }

            if (durationSeconds > 43200)
            {
                durationSeconds = (int)Math.Max(1, durationSeconds / 1000d);
            }

            return durationSeconds;
        }

        public static long EstimateSizeFromBitrate(int bitrate, int durationSeconds)
        {
            durationSeconds = NormalizeDurationSeconds(durationSeconds);
            if (durationSeconds <= 0 || bitrate <= 0)
            {
                return 0;
            }

            if (bitrate < 1000)
            {
                bitrate *= 1000;
            }

            double bytes = (bitrate / 8d) * durationSeconds;
            return (long)Math.Round(bytes);
        }
    }
}
