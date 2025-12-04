using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace UnblockNCM.Core.Crypto
{
    public static class NeteaseCrypto
    {
        private static readonly byte[] EapiKey = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
        private static readonly byte[] LinuxKey = Encoding.UTF8.GetBytes("rFgB&h#%2?^eDg:Q");

        private static byte[] AesEcb(byte[] data, byte[] key, bool encrypt)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Key = key;
            aes.Padding = PaddingMode.PKCS7;
            using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        public static byte[] EApiEncrypt(byte[] data) => AesEcb(data, EapiKey, true);
        public static byte[] EApiDecrypt(byte[] data) => AesEcb(data, EapiKey, false);
        public static byte[] LinuxEncrypt(byte[] data) => AesEcb(data, LinuxKey, true);
        public static byte[] LinuxDecrypt(byte[] data) => AesEcb(data, LinuxKey, false);

        public static (string url, string body) EApiEncryptRequest(Uri url, JObject obj)
        {
            var path = url.PathAndQuery;
            var text = obj.ToString(Newtonsoft.Json.Formatting.None);
            var message = $"nobody{path}use{text}md5forencrypt";
            var digest = Md5Hex(message);
            var data = $"{path}-36cd479b6b5-{text}-36cd479b6b5-{digest}";
            var payload = EApiEncrypt(Encoding.UTF8.GetBytes(data)).ToHex().ToUpperInvariant();
            return (url.ToString().Replace("api", "eapi"), $"params={payload}");
        }

        public static (string url, string body) ApiEncryptRequest(Uri url, JObject obj)
        {
            return (url.ToString().Replace("api", "api"), string.Join("&", obj.Properties().Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value.ToString())}")));
        }

        public static (string url, string body) LinuxEncryptRequest(Uri url, JObject obj)
        {
            var payload = new JObject
            {
                ["method"] = "POST",
                ["url"] = url.ToString(),
                ["params"] = obj
            };
            var text = payload.ToString(Newtonsoft.Json.Formatting.None);
            var enc = LinuxEncrypt(Encoding.UTF8.GetBytes(text)).ToHex().ToUpperInvariant();
            var target = new Uri(url, "/api/linux/forward");
            return (target.ToString(), $"eparams={enc}");
        }

        public static string Md5Hex(string text)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            return bytes.ToHex();
        }

        public static string Sha1Hex(string text)
        {
            using var sha = SHA1.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(text)).ToHex();
        }

        public static string Base64UrlEncode(string plain, Encoding encoding = null)
        {
            encoding ??= Encoding.UTF8;
            return Convert.ToBase64String(encoding.GetBytes(plain))
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static string Base64UrlDecode(string encoded, Encoding encoding = null)
        {
            encoding ??= Encoding.UTF8;
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded);
            return encoding.GetString(bytes);
        }

        public static string UriRetrieve(string id)
        {
            id = id.Trim();
            var key = "3go8&$8*3*3h0k(2)2";
            var chars = id.Select((c, idx) => (char)(c ^ key[idx % key.Length]));
            var str = new string(chars.ToArray());
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(str));
            var result = Convert.ToBase64String(hash).Replace('/', '_').Replace('+', '-');
            return $"http://p1.music.126.net/{result}/{id}";
        }

        public static string ToHex(this byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }
    }
}
