using YTPlayer.Models;

namespace YTPlayer.Forms
{
    internal sealed class CommentNodeTag
    {
        public CommentInfo? Comment { get; set; }
        public string? CommentId { get; set; }
        public string? ParentCommentId { get; set; }
        public bool IsPlaceholder { get; set; }
        public bool IsLoadMoreNode { get; set; }
        public bool IsLoading { get; set; }
        public bool LoadFailed { get; set; }
        public bool IsTopLevel { get; set; }
        public int SequenceNumber { get; set; }
        public int PageNumber { get; set; }
        public bool AutoLoadTriggered { get; set; }
        public bool IsVirtualFloor { get; set; }
        public int FixedSequenceNumber { get; set; }
    }
}
