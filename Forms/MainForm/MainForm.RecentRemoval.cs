#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer
{
public partial class MainForm
{
    private sealed class RecentResourceRemovalTarget
    {
        public RecentPlayResourceType Type { get; }
        public string Id { get; }
        public string Name { get; }
        public string ViewSource { get; }
        public long AccountId { get; }

        public RecentResourceRemovalTarget(RecentPlayResourceType type, string id, string name, string source, long accountId)
        {
            Type = type;
            Id = id;
            Name = name;
            ViewSource = source;
            AccountId = accountId;
        }
    }

    private bool TryGetRecentResourceRemovalTarget(MenuContextSnapshot snapshot, out RecentResourceRemovalTarget target)
    {
        target = null;
        if (snapshot == null || !snapshot.IsValid || !snapshot.IsLoggedIn || snapshot.IsCurrentPlayback) return false;
        RecentPlayResourceType type;
        string id, name;
        switch (snapshot.ViewSource?.ToLowerInvariant())
        {
            case "recent_playlists" when snapshot.PrimaryEntity == MenuEntityKind.Playlist && snapshot.Playlist != null:
                type = RecentPlayResourceType.Playlist; id = snapshot.Playlist.Id; name = snapshot.Playlist.Name;
                break;
            case "recent_albums" when snapshot.PrimaryEntity == MenuEntityKind.Album && snapshot.Album != null:
                type = RecentPlayResourceType.Album; id = snapshot.Album.Id; name = snapshot.Album.Name;
                break;
            case "recent_podcasts" when snapshot.PrimaryEntity == MenuEntityKind.Podcast && snapshot.Podcast != null:
                type = RecentPlayResourceType.Podcast; id = snapshot.Podcast.Id.ToString(CultureInfo.InvariantCulture); name = snapshot.Podcast.Name;
                break;
            default: return false;
        }
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedId) || parsedId <= 0) return false;
        target = new RecentResourceRemovalTarget(type, id, string.IsNullOrWhiteSpace(name) ? "所选项目" : name,
            snapshot.ViewSource, GetCurrentUserId());
        return true;
    }

    private void ConfigureRecentResourceRemovalMenu(MenuContextSnapshot snapshot)
    {
        if (!TryGetRecentResourceRemovalTarget(snapshot, out var target)) return;
        deleteRecentPlayMenuItem.Visible = true;
        deleteRecentPlayMenuItem.Tag = target;
    }

    private async Task DeleteRecentResourceAsync(RecentResourceRemovalTarget target)
    {
        if (!IsUserLoggedIn() || !IsRemovalViewCurrent(target.ViewSource, target.AccountId)) return;
        string key = $"{target.AccountId}:{target.Type}:{target.Id}";
        if (_pendingRecentPlayDeletes.Contains(key)) return;
        if (MessageBox.Show("确定要从最近播放记录中移除“" + target.Name + "”？\n\n（仅移除播放记录，保留原项目及收藏。）",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!_pendingRecentPlayDeletes.Add(key)) return;
        try
        {
            AnnounceStatusBarMessage("正在删除最近播放记录...");
            if (await _apiClient.DeleteRecentPlayedResourceAsync(target.Type, target.Id))
            {
                if (IsDisposed || target.AccountId != GetCurrentUserId()) return;
                RemoveRecentResourceLocally(target);
                AnnounceStatusBarMessage("已删除播放记录：" + target.Name);
            }
            else
            {
                MessageBox.Show("删除最近播放记录失败，请刷新列表后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show("删除最近播放记录失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
        finally { _pendingRecentPlayDeletes.Remove(key); }
    }

    private void RemoveRecentResourceLocally(RecentResourceRemovalTarget target)
    {
        bool updateView = IsRemovalViewCurrent(target.ViewSource, target.AccountId);
        if (updateView) BeginViewContentOperation(cancelNavigation: false);
        switch (target.Type)
        {
            case RecentPlayResourceType.Playlist:
                _recentPlaylistsCache.RemoveAll(item => item.Id == target.Id);
                _recentPlaylistCount = _recentPlaylistsCache.Count;
                if (updateView) RemoveRecentListItem(_currentPlaylists, item => item.Id, target.Id,
                    items => PatchPlaylists(items, _currentSequenceStartIndex, allowSelection: false));
                break;
            case RecentPlayResourceType.Album:
                _recentAlbumsCache.RemoveAll(item => item.Id == target.Id);
                _recentAlbumCount = _recentAlbumsCache.Count;
                if (updateView) RemoveRecentListItem(_currentAlbums, item => item.Id, target.Id,
                    items => PatchAlbums(items, _currentSequenceStartIndex, allowSelection: false));
                break;
            case RecentPlayResourceType.Podcast:
                _recentPodcastsCache.RemoveAll(item => item.Id.ToString(CultureInfo.InvariantCulture) == target.Id);
                _recentPodcastCount = _recentPodcastsCache.Count;
                if (updateView) RemoveRecentListItem(_currentPodcasts, item => item.Id.ToString(CultureInfo.InvariantCulture), target.Id,
                    items => PatchPodcasts(items, _currentSequenceStartIndex, allowSelection: false));
                break;
        }
    }

    private void RemoveRecentListItem<T>(List<T> items, Func<T, string> getId, string id, Action<List<T>> patch)
    {
        int index = items.FindIndex(item => string.Equals(getId(item), id, StringComparison.Ordinal));
        if (index < 0) return;
        int selectedIndex = GetSelectedListViewItemSafe()?.Index ?? -1;
        var updated = new List<T>(items);
        updated.RemoveAt(index);
        int focusIndex = ListRemovalSelection.ResolveIndex(selectedIndex, index, updated.Count);
        patch(updated);
        CompleteListRemovalSelection(focusIndex, selectedIndex == index);
    }
}
}
