using System;
using System.Collections.Generic;
using System.Linq;
using UnblockNCM.Core.Models;

namespace UnblockNCM.Core.Providers
{
    public static class SelectHelper
    {
        public static bool EnableFlac { get; set; }

        public static T PickByDuration<T>(IEnumerable<T> list, SongInfo info, Func<T, long> durationSelector)
        {
            var items = list?.Take(5).Where(item => item != null).ToList() ?? new List<T>();
            if (items.Count == 0)
            {
                return default;
            }

            if (info == null || info.Duration <= 0 || durationSelector == null)
            {
                return items.FirstOrDefault();
            }

            var timedItems = items
                .Select(item => new
                {
                    Item = item,
                    Duration = durationSelector(item)
                })
                .Where(item => item.Duration > 0)
                .OrderBy(item => Math.Abs(item.Duration - info.Duration))
                .ToList();

            if (timedItems.Count == 0)
            {
                return items.FirstOrDefault();
            }

            var best = timedItems.First();
            long tolerance = GetDurationToleranceMs(info.Duration);
            return Math.Abs(best.Duration - info.Duration) <= tolerance
                ? best.Item
                : default;
        }

        private static long GetDurationToleranceMs(long durationMs)
        {
            if (durationMs <= 0)
            {
                return 5000;
            }

            // Keep upstream's 5s behavior for typical songs, while allowing
            // slightly wider tolerance on very long tracks and stricter checks
            // on short tracks.
            return Math.Max(3000, Math.Min(8000, durationMs / 50));
        }
    }
}
