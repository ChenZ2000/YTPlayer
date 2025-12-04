using System.Security.Cryptography;
using System.Text;
using System;

namespace UnblockNCM.Core.Utils
{
    /// <summary>
    /// Kuwo DES helper (convert_url2) — pure .NET, no Node dependency.
    /// </summary>
    public static class KwDes
    {
        private static readonly byte[] Key = Encoding.ASCII.GetBytes("ylzsxkwm");

        /// <summary>
        /// Encrypt query for convert_url2 using DES/ECB/ZeroPadding.
        /// </summary>
        public static string EncryptQuery(string query)
        {
            using var des = DES.Create();
            des.Key = Key;
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.Zeros;
            using var enc = des.CreateEncryptor();
            var input = Encoding.UTF8.GetBytes(query);
            var cipher = enc.TransformFinalBlock(input, 0, input.Length);
            return Convert.ToBase64String(cipher);
        }
    }
}
