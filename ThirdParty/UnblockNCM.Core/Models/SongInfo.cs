using System.Collections.Generic;

namespace UnblockNCM.Core.Models
{
    public class SongInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> Alias { get; set; } = new();
        public long Duration { get; set; }
        public AlbumInfo Album { get; set; } = new();
        public List<ArtistInfo> Artists { get; set; } = new();
        public string Keyword { get; set; }

        public class AlbumInfo
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        public class ArtistInfo
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }
    }
}
