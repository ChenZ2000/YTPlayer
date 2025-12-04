using Newtonsoft.Json.Linq;

namespace UnblockNCM.Core.Models
{
    public enum NeteaseCryptoKind
    {
        None,
        LinuxApi,
        EApi,
        Api
    }

    public class NeteaseContext
    {
        public NeteaseCryptoKind CryptoKind { get; set; }
        public string Path { get; set; }
        public JObject Param { get; set; }
        public string Pad { get; set; } = string.Empty;
        public bool Web { get; set; }
        public bool ER { get; set; }
        public JObject JsonBody { get; set; }
    }
}
