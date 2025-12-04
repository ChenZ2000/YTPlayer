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
            var items = list.Take(5).ToList();
            var target = items.FirstOrDefault(song => Math.Abs(durationSelector(song) - info.Duration) < 5000);
            return target != null ? target : items.FirstOrDefault();
        }
    }
}
