using System;
using System.Diagnostics;

namespace YTPlayer.Utils
{
    /// <summary>集中管理骨架/丰富阶段的日志与 token。</summary>
    public static class WorkScopes
    {
        public static SkeletonScope BeginSkeleton(string name, string? viewSource = null)
        {
            return new SkeletonScope(name, viewSource);
        }

        public static EnrichmentScope BeginEnrichment(string name, string? viewSource = null)
        {
            return new EnrichmentScope(name, viewSource);
        }
    }

    public abstract class WorkScopeBase : IDisposable
    {
        private bool _disposed;

        protected WorkScopeBase(string category, string name, string? viewSource)
        {
            Category = category;
            Name = name;
            ViewSource = string.IsNullOrWhiteSpace(viewSource) ? "unknown" : viewSource!;
            Token = Guid.NewGuid().ToString("N");

            Debug.WriteLine($"[{Category}] start {Name} view={ViewSource} token={Token}");
        }

        public string Category { get; } = string.Empty;
        public string Name { get; } = string.Empty;
        public string ViewSource { get; } = string.Empty;
        public string Token { get; } = string.Empty;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine($"[{Category}] end {Name} view={ViewSource} token={Token}");
        }
    }

    public sealed class SkeletonScope : WorkScopeBase
    {
        public SkeletonScope(string name, string? viewSource) : base("Skeleton", name, viewSource) { }
    }

    public sealed class EnrichmentScope : WorkScopeBase
    {
        public EnrichmentScope(string name, string? viewSource) : base("Enrichment", name, viewSource) { }
    }
}
