namespace YTPlayer.Forms
{
    internal sealed class CommentReplyTarget
    {
        public string CommentId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public bool IsTopLevel { get; set; }
    }
}
