using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 网易云音乐加密助手类
    /// 实现WEAPI和EAPI加密算法
    /// </summary>
    public static class EncryptionHelper
    {
        // WEAPI 常量
        private const string WEAPI_NONCE = "0CoJUm6Qyw8W8jud";
        private const string WEAPI_IV = "0102030405060708";
        private const string WEAPI_PUBLIC_KEY = "010001";
        private const string WEAPI_MODULUS = "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";

        // EAPI 常量
        private const string EAPI_KEY = "e82ckenh8dichen8";
        private const string EAPI_IV = "0102030405060708";

        // 随机字符串字符集
        private const string RANDOM_CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        private static readonly Random _random = new Random();

        #region WEAPI 加密

        /// <summary>
        /// WEAPI 加密
        /// </summary>
        /// <param name="text">要加密的文本（通常是JSON字符串）</param>
        /// <returns>包含params和encSecKey的加密结果</returns>
        public static WeapiResult EncryptWeapi(string text)
        {
            // 生成16位随机密钥
            string secretKey = GenerateRandomString(16);

            // 第一次AES加密：使用NONCE
            string firstEncrypt = AesEncrypt(text, WEAPI_NONCE, WEAPI_IV);

            // 第二次AES加密：使用随机密钥
            string secondEncrypt = AesEncrypt(firstEncrypt, secretKey, WEAPI_IV);

            // RSA加密密钥
            string encSecKey = RsaEncrypt(secretKey, WEAPI_PUBLIC_KEY, WEAPI_MODULUS);

            return new WeapiResult
            {
                Params = secondEncrypt,
                EncSecKey = encSecKey
            };
        }

        /// <summary>
        /// AES-128-CBC 加密
        /// </summary>
        private static string AesEncrypt(string text, string key, string iv)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] ivBytes = Encoding.UTF8.GetBytes(iv);

            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.KeySize = 128;
                aes.Key = keyBytes;
                aes.IV = ivBytes;

                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                using (MemoryStream ms = new MemoryStream())
                using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    cs.Write(textBytes, 0, textBytes.Length);
                    cs.FlushFinalBlock();
                    byte[] encrypted = ms.ToArray();
                    return Convert.ToBase64String(encrypted);
                }
            }
        }

        /// <summary>
        /// RSA 加密（网易云音乐特定实现）
        /// Python源码：Netease-music.py:2536-2552
        /// </summary>
        private static string RsaEncrypt(string text, string pubKey, string modulus)
        {
            // 反转文本（Python: text = sec_key[::-1]）
            string reversedText = new string(text.Reverse().ToArray());

            // 转换为字节数组
            byte[] textBytes = Encoding.UTF8.GetBytes(reversedText);

            // 转换为十六进制字符串（Python: hex_text = binascii.hexlify(text.encode("utf-8"))）
            // 使用大端序（big-endian），与Python的binascii.hexlify保持一致
            string hexText = BitConverter.ToString(textBytes).Replace("-", "");

            // 从十六进制字符串创建BigInteger（Python: big_int = int(hex_text, 16)）
            // 前面加"0"确保被解析为正数
            BigInteger textNum = BigInteger.Parse("0" + hexText, System.Globalization.NumberStyles.HexNumber);

            // RSA 加密：c = m^e mod n（Python: enc = pow(big_int, pubkey, modulus)）
            BigInteger e = BigInteger.Parse(pubKey, System.Globalization.NumberStyles.HexNumber);
            BigInteger n = BigInteger.Parse(modulus, System.Globalization.NumberStyles.HexNumber);
            BigInteger encrypted = BigInteger.ModPow(textNum, e, n);

            // 转换为十六进制字符串，并补齐到256位（Python: return format(enc, "x").zfill(256)）
            string encryptedHex = encrypted.ToString("x");
            return encryptedHex.PadLeft(256, '0');
        }

        #endregion

        #region EAPI 加密

        /// <summary>
        /// EAPI 加密
        /// </summary>
        /// <param name="url">API URL路径</param>
        /// <param name="text">要加密的文本（通常是JSON字符串）</param>
        /// <returns>加密后的十六进制字符串</returns>
        public static string EncryptEapi(string url, string text)
        {
            // 构造消息：nobody{url}use{text}md5forencrypt
            string message = $"nobody{url}use{text}md5forencrypt";

            // 计算MD5
            string md5Hash = ComputeMd5(message);

            // 构造最终文本：{url}-36cd479b6b5-{text}-36cd479b6b5-{md5Hash}
            string finalText = $"{url}-36cd479b6b5-{text}-36cd479b6b5-{md5Hash}";

            // AES-ECB 加密
            return AesEncryptEcb(finalText, EAPI_KEY);
        }

        /// <summary>
        /// EAPI 解密
        /// </summary>
        /// <param name="data">EAPI 加密后的原始字节</param>
        /// <returns>解密后的文本</returns>
        public static string DecryptEapi(byte[] data)
        {
            var decryptedBytes = DecryptEapiToBytes(data);
            return Encoding.UTF8.GetString(decryptedBytes ?? Array.Empty<byte>());
        }

        /// <summary>
        /// EAPI 解密（返回原始字节，供调用方自行处理编码/压缩）
        /// </summary>
        public static byte[] DecryptEapiToBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (TryDecryptEapiBlock(data, out var decrypted))
            {
                Debug.WriteLine("[DEBUG EAPI] Decrypt succeeded with original buffer.");
                return decrypted;
            }

            // 尝试移除前缀/后缀对齐（部分接口会在密文前后附加长度信息等）
            for (int prefix = 0; prefix <= 64 && prefix < data.Length; prefix++)
            {
                for (int suffix = 0; suffix <= 64 && suffix < data.Length; suffix++)
                {
                    int length = data.Length - prefix - suffix;
                    if (length <= 0 || (length % 16) != 0)
                    {
                        continue;
                    }

                    var slice = new byte[length];
                    Buffer.BlockCopy(data, prefix, slice, 0, length);
                    if (TryDecryptEapiBlock(slice, out decrypted))
                    {
                        Debug.WriteLine($"[DEBUG EAPI] Decrypt succeeded after removing prefix {prefix} bytes and suffix {suffix} bytes (length={length}).");
                        return decrypted;
                    }
                }
            }

            throw new CryptographicException("EAPI 解密失败：密文无法对齐为完整块。");
        }

        private static bool TryDecryptEapiBlock(byte[] data, out byte[] decrypted)
        {
            decrypted = Array.Empty<byte>();

            if (data == null || data.Length == 0 || (data.Length % 16) != 0)
            {
                return false;
            }

            try
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(EAPI_KEY);
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;
                    aes.KeySize = 128;
                    aes.Key = keyBytes;

                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        var buffer = decryptor.TransformFinalBlock(data, 0, data.Length);
                        decrypted = RemovePkcs7PaddingIfPresent(buffer);
                        return true;
                    }
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static byte[] RemovePkcs7PaddingIfPresent(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return buffer ?? Array.Empty<byte>();
            }

            byte pad = buffer[buffer.Length - 1];
            if (pad == 0 || pad > 16)
            {
                return buffer;
            }

            for (int i = buffer.Length - pad; i < buffer.Length; i++)
            {
                if (buffer[i] != pad)
                {
                    return buffer;
                }
            }

            var trimmed = new byte[buffer.Length - pad];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        /// <summary>
        /// AES-128-ECB 加密（用于EAPI）
        /// </summary>
        private static string AesEncryptEcb(string text, string key)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                aes.KeySize = 128;
                aes.Key = keyBytes;

                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                using (MemoryStream ms = new MemoryStream())
                using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    cs.Write(textBytes, 0, textBytes.Length);
                    cs.FlushFinalBlock();
                    byte[] encrypted = ms.ToArray();

                    // 转换为大写十六进制字符串（匹配Node.js: .toUpperCase()）
                    return BitConverter.ToString(encrypted).Replace("-", "").ToUpper();
                }
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 生成随机字符串
        /// </summary>
        /// <param name="length">字符串长度</param>
        /// <returns>随机字符串</returns>
        public static string GenerateRandomString(int length)
        {
            StringBuilder sb = new StringBuilder(length);
            lock (_random)
            {
                for (int i = 0; i < length; i++)
                {
                    sb.Append(RANDOM_CHARS[_random.Next(RANDOM_CHARS.Length)]);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成设备ID
        /// </summary>
        /// <returns>设备ID（33位十六进制字符串）</returns>
        public static string GenerateDeviceId()
        {
            StringBuilder sb = new StringBuilder();
            lock (_random)
            {
                for (int i = 0; i < 32; i++)
                {
                    sb.Append(_random.Next(16).ToString("x"));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成请求ID
        /// </summary>
        /// <returns>请求ID（时间戳 + 4位随机数）</returns>
        public static string GenerateRequestId()
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int randomNum;
            lock (_random)
            {
                randomNum = _random.Next(1000, 9999);
            }
            return $"{timestamp}_{randomNum}";
        }

        /// <summary>
        /// 生成反作弊token（128位十六进制）
        /// </summary>
        /// <returns>128位十六进制字符串</returns>
        public static string GenerateAntiCheatToken()
        {
            StringBuilder sb = new StringBuilder();
            lock (_random)
            {
                for (int i = 0; i < 128; i++)
                {
                    sb.Append(_random.Next(16).ToString("x"));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 计算MD5哈希
        /// </summary>
        /// <param name="text">要计算哈希的文本</param>
        /// <returns>32位小写十六进制MD5哈希</returns>
        public static string ComputeMd5(string text)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(text);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 计算SHA256哈希
        /// </summary>
        /// <param name="text">要计算哈希的文本</param>
        /// <returns>64位小写十六进制SHA256哈希</returns>
        public static string ComputeSha256(string text)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(text);
                byte[] hashBytes = sha256.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 生成随机十六进制字符串（用于Cookie参数）
        /// </summary>
        public static string GenerateRandomHex(int length)
        {
            StringBuilder sb = new StringBuilder();
            lock (_random)
            {
                for (int i = 0; i < length; i++)
                {
                    sb.Append(_random.Next(16).ToString("x"));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成WNMCID格式（格式：abcdef.timestamp.01.0）
        /// </summary>
        public static string GenerateWNMCID()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz";
            StringBuilder sb = new StringBuilder();
            lock (_random)
            {
                for (int i = 0; i < 6; i++)
                {
                    sb.Append(chars[_random.Next(chars.Length)]);
                }
            }
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return string.Format("{0}.{1}.01.0", sb.ToString(), timestamp);
        }

        #endregion
    }

    /// <summary>
    /// WEAPI 加密结果
    /// </summary>
    public class WeapiResult
    {
        /// <summary>
        /// 加密后的参数
        /// </summary>
        public string Params { get; set; } = string.Empty;

        /// <summary>
        /// RSA加密后的密钥
        /// </summary>
        public string EncSecKey { get; set; } = string.Empty;
    }
}


