using System;
using System.Collections.Generic;
using System.Linq;

namespace YTPlayer.Update
{
    public static class UpdateFormatting
    {
        public static UpdateAsset? SelectPreferredAsset(IReadOnlyList<UpdateAsset>? assets)
        {
            if (assets == null || assets.Count == 0)
            {
                return null;
            }

            UpdateAsset? preferred = assets
                .FirstOrDefault(a => a?.Name != null &&
                                     a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                     a.Name.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0);

            if (preferred != null)
            {
                return preferred;
            }

            preferred = assets.FirstOrDefault(a => a?.Name != null &&
                                                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            return preferred ?? assets[0];
        }

        public static string FormatVersionLabel(UpdatePlan? plan, string? semanticFallback)
        {
            string label = plan?.DisplayVersion ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = plan?.TargetTag ?? semanticFallback ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                return "最新版本";
            }

            return label.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? label : $"v{label}";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "未知大小";
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return $"{value:0.##}{units[unitIndex]}";
        }
    }
}
