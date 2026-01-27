namespace YTPlayer.Core.Playback
{
    public enum SongResolveStatus
    {
        Success = 0,
        NotAvailable = 1,
        PaidAlbumNotPurchased = 2,
        Canceled = 3,
        Failed = 4
    }

    public sealed class SongResolveResult
    {
        public SongResolveStatus Status { get; }
        public bool UsedUnblock { get; }

        private SongResolveResult(SongResolveStatus status, bool usedUnblock = false)
        {
            Status = status;
            UsedUnblock = usedUnblock;
        }

        public static SongResolveResult Success(bool usedUnblock = false)
        {
            return new SongResolveResult(SongResolveStatus.Success, usedUnblock);
        }

        public static SongResolveResult NotAvailable()
        {
            return new SongResolveResult(SongResolveStatus.NotAvailable);
        }

        public static SongResolveResult PaidAlbumNotPurchased()
        {
            return new SongResolveResult(SongResolveStatus.PaidAlbumNotPurchased);
        }

        public static SongResolveResult Canceled()
        {
            return new SongResolveResult(SongResolveStatus.Canceled);
        }

        public static SongResolveResult Failed()
        {
            return new SongResolveResult(SongResolveStatus.Failed);
        }
    }
}
