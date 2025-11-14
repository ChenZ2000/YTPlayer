using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace YTPlayer.Utils
{
    internal static class DocumentationLoader
    {
        public static string ReadMarkdown(string relativeResourcePath)
        {
            if (string.IsNullOrWhiteSpace(relativeResourcePath))
            {
                throw new ArgumentException("资源路径不能为空。", nameof(relativeResourcePath));
            }

            var assembly = typeof(DocumentationLoader).GetTypeInfo().Assembly;
            var normalizedSuffix = Normalize(relativeResourcePath);

            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                throw new InvalidOperationException($"无法找到文档资源 {relativeResourcePath}。");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                throw new InvalidOperationException($"无法打开文档资源流 {resourceName}。");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static string Normalize(string path)
        {
            return path
                .Replace('\\', '.')
                .Replace('/', '.')
                .TrimStart('.');
        }
    }
}
