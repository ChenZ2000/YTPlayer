using System;

namespace YTPlayer.Core.Auth
{
    public enum WeChatLoginStatus { WaitingForScan, AwaitingConfirmation, Authorized, Denied, Expired, Error }

    public sealed class WeChatLoginSession
    {
        internal WeChatLoginSession(string uuid, string state, Uri authorizationUri, string pollHost)
        { Uuid = uuid; State = state; AuthorizationUri = authorizationUri; PollHost = pollHost; }
        internal string Uuid { get; }
        internal string State { get; }
        internal string PollHost { get; }
        internal int? LastCode { get; set; }
        internal int CodeRedeemed;
        public Uri AuthorizationUri { get; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }

    public sealed class WeChatLoginPollResult
    {
        public int Code { get; internal set; }
        public WeChatLoginStatus Status { get; internal set; }
        internal string? AuthorizationCode { get; set; }
        public LoginResult? Login { get; internal set; }
    }
}
