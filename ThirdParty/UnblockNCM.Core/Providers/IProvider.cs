using System.Threading;
using System.Threading.Tasks;
using UnblockNCM.Core.Models;

namespace UnblockNCM.Core.Providers
{
    public interface IProvider
    {
        /// <summary>Return a playable audio result or null if not available.</summary>
        Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct);
    }
}
