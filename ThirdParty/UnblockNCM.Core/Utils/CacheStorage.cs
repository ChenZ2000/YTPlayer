using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace UnblockNCM.Core.Utils
{
    /// <summary>
    /// Simple in-memory cache with expiration.
    /// </summary>
    public class CacheStorage
    {
        private class CacheEntry
        {
            public object Value { get; set; }
            public DateTimeOffset ExpireAt { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>();

        public TimeSpan AliveDuration { get; set; } = TimeSpan.FromMinutes(30);

        public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null, bool noCache = false)
        {
            if (noCache)
                return await factory().ConfigureAwait(false);

            var now = DateTimeOffset.Now;
            if (_cache.TryGetValue(key, out var entry) && entry.ExpireAt > now)
                return (T)entry.Value;

            var value = await factory().ConfigureAwait(false);
            _cache[key] = new CacheEntry { Value = value, ExpireAt = now + (ttl ?? AliveDuration) };
            return value;
        }

        public void Cleanup()
        {
            var now = DateTimeOffset.Now;
            foreach (var kv in _cache)
            {
                if (kv.Value.ExpireAt <= now)
                {
                    _cache.TryRemove(kv.Key, out _);
                }
            }
        }
    }
}
