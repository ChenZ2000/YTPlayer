namespace UnblockNCM.Core.Models
{
    public class AudioResult
    {
        public string Url { get; set; }
        public int? BitRate { get; set; }
        public string Md5 { get; set; }
        public long Size { get; set; }
        public string Source { get; set; }
        public string Type { get; set; }
        public long? DurationMs { get; set; }
        public string Title { get; set; }
        public string Artists { get; set; }
        /// <summary>
        /// Optional headers required by upstream when fetching Url directly.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> Headers { get; set; }
    }
}
