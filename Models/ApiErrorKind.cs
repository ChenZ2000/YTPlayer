namespace YTPlayer.Models
{
    public enum ApiErrorKind
    {
        None = 0,
        Unauthorized = 1,
        AccessRestricted = 2,
        ResourceUnavailable = 3,
        Throttled = 4,
        Transient = 5,
        Unknown = 6
    }
}
