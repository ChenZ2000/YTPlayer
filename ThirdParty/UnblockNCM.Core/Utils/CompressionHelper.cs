using System;
using System.IO;
using System.IO.Compression;

namespace UnblockNCM.Core.Utils
{
    public static class CompressionHelper
    {
        public static byte[] Decompress(byte[] input, string encoding)
        {
            if (input == null || input.Length == 0) return input;
            if (string.IsNullOrEmpty(encoding)) return input;
            encoding = encoding.ToLowerInvariant();
            try
            {
                if (encoding.Contains("gzip"))
                {
                    using var ms = new MemoryStream(input);
                    using var gz = new GZipStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    gz.CopyTo(outMs);
                    return outMs.ToArray();
                }
                if (encoding.Contains("deflate"))
                {
                    using var ms = new MemoryStream(input);
                    using var df = new DeflateStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    df.CopyTo(outMs);
                    return outMs.ToArray();
                }
            }
            catch
            {
                return input;
            }
            return input;
        }
    }
}
