using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer
{
    public partial class MainForm
    {
        private List<ArtistInfo> _currentArtists = new List<ArtistInfo>();
        private ArtistInfo? _currentArtist;
        private ArtistDetail? _currentArtistDetail;
        private int _currentArtistSongsOffset;
        private bool _currentArtistSongsHasMore;
        private int _currentArtistAlbumsOffset;
        private bool _currentArtistAlbumsHasMore;
        private int _currentArtistAlbumsTotalCount;
        private SortState<ArtistSongSortOption> _artistSongSortState = new SortState<ArtistSongSortOption>(
            ArtistSongSortOption.Hot,
            new Dictionary<ArtistSongSortOption, string>
            {
                { ArtistSongSortOption.Hot, "当前排序：按热门" },
                { ArtistSongSortOption.Time, "当前排序：按发布时间" }
            });

        private SortState<ArtistAlbumSortOption> _artistAlbumSortState = new SortState<ArtistAlbumSortOption>(
            ArtistAlbumSortOption.Latest,
            new Dictionary<ArtistAlbumSortOption, string>
            {
                { ArtistAlbumSortOption.Latest, "当前排序：按最新发布" },
                { ArtistAlbumSortOption.Oldest, "当前排序：按最早发布" }
            });
        private readonly Dictionary<long, List<AlbumInfo>> _artistAlbumsAscendingCache = new Dictionary<long, List<AlbumInfo>>();
        private const int ArtistAlbumsAscendingCacheLimit = 4;
        private int _currentArtistTypeFilter = -1;
        private int _currentArtistAreaFilter = -1;
        private bool _currentArtistCategoryHasMore;
        private readonly Dictionary<long, (int MusicCount, int AlbumCount)> _artistStatsCache = new Dictionary<long, (int MusicCount, int AlbumCount)>();
        private readonly HashSet<long> _artistStatsInFlight = new HashSet<long>();
        private CancellationTokenSource? _artistStatsRefreshCts;

        private const int ArtistSongsPageSize = 100;
        private const int ArtistAlbumsPageSize = 100;

        private void ConfigureListViewDefault()
        {
            columnHeader1.Text = "#";
            columnHeader2.Text = "标题";
            columnHeader3.Text = "歌手/创建者";
            columnHeader4.Text = "专辑/曲目数";
            columnHeader5.Text = "时长/描述";
        }

        private void ConfigureListViewForArtists()
        {
            columnHeader1.Text = "#";
            columnHeader2.Text = "歌手";
            columnHeader3.Text = "作品统计";
            columnHeader4.Text = "简介";
            columnHeader5.Text = string.Empty;
        }

        private void DisplayArtists(
            List<ArtistInfo> artists,
            bool showPagination = false,
            bool hasNextPage = false,
            int startIndex = 1,
            bool preserveSelection = false,
            string? viewSource = null,
            string? accessibleName = null)
        {
            int previousSelectedIndex = -1;
            if (preserveSelection && resultListView.SelectedIndices.Count > 0)
            {
                previousSelectedIndex = resultListView.SelectedIndices[0];
            }

            _currentArtists = artists ?? new List<ArtistInfo>();
            _currentSongs.Clear();
            _currentPlaylists.Clear();
            _currentAlbums.Clear();
            _currentListItems.Clear();
            _currentPodcasts.Clear();
            _currentPodcastSounds.Clear();
            _currentPodcast = null;
            ApplyArtistSubscriptionStates(_currentArtists);

            resultListView.BeginUpdate();
            resultListView.Items.Clear();

            if (_currentArtists.Count == 0)
            {
                resultListView.EndUpdate();
                ConfigureListViewForArtists();
                SetViewContext(viewSource, accessibleName ?? "歌手列表");
                return;
            }

            int displayNumber = startIndex;
            foreach (var artist in _currentArtists)
            {
                if ((artist.MusicCount <= 0 || artist.AlbumCount <= 0) &&
                    TryGetCachedArtistStats(artist.Id, out var cachedStats))
                {
                    if (artist.MusicCount <= 0)
                    {
                        artist.MusicCount = cachedStats.MusicCount;
                    }

                    if (artist.AlbumCount <= 0)
                    {
                        artist.AlbumCount = cachedStats.AlbumCount;
                    }
                }

                string counts = $"歌曲 {artist.MusicCount} / 专辑 {artist.AlbumCount}";
                string description = !string.IsNullOrWhiteSpace(artist.Description)
                    ? artist.Description
                    : artist.BriefDesc;

                var item = new ListViewItem(new[]
                {
                    displayNumber.ToString(),
                    artist.Name ?? "未知",
                    counts,
                    description ?? string.Empty,
                    string.Empty
                })
                {
                    Tag = artist
                };

                resultListView.Items.Add(item);
                displayNumber++;
            }

            if (showPagination)
            {
                if (startIndex > 1)
                {
                    var prevItem = resultListView.Items.Add("上一页");
                    prevItem.Tag = -2;
                }

                if (hasNextPage)
                {
                    var nextItem = resultListView.Items.Add("下一页");
                    nextItem.Tag = -3;
                }
            }

            resultListView.EndUpdate();

            ConfigureListViewForArtists();
            ScheduleArtistStatsRefresh(_currentArtists);

            string defaultAccessibleName = accessibleName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(defaultAccessibleName))
            {
                defaultAccessibleName = "歌手列表";
            }

            SetViewContext(viewSource, defaultAccessibleName);

            if (!IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
            {
                int targetIndex = previousSelectedIndex >= 0
                    ? Math.Min(previousSelectedIndex, resultListView.Items.Count - 1)
                    : 0;

                RestoreListViewFocus(targetIndex);
            }
        }

        private async Task OpenArtistAsync(ArtistInfo artist, bool skipSave = false)
        {
            if (artist == null)
            {
                return;
            }

            try
            {
                UpdateStatusBar($"正在加载歌手：{artist.Name}...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                _currentArtist = artist;
                _artistSongSortState.SetOption(ArtistSongSortOption.Hot);
                _artistAlbumSortState.SetOption(ArtistAlbumSortOption.Latest);
                _currentArtistSongsOffset = 0;
                _currentArtistAlbumsOffset = 0;
                _currentArtistSongsHasMore = false;
                _currentArtistAlbumsHasMore = false;
                _currentArtistAlbumsTotalCount = 0;
                ClearArtistAlbumsAscendingCache(artist.Id);

                _currentArtistDetail = await _apiClient.GetArtistDetailAsync(artist.Id, includeIntroduction: true);
                if (_currentArtistDetail != null)
                {
                    ApplyArtistDetailToArtist(artist, _currentArtistDetail);
                }

                var entryItems = BuildArtistEntryItems(artist, _currentArtistDetail);
                var listAccessibleName = string.IsNullOrWhiteSpace(artist.Name) ? "歌手" : artist.Name;
                DisplayListItems(entryItems,
                    viewSource: $"artist_entries:{artist.Id}",
                    accessibleName: listAccessibleName);

                UpdateStatusBar($"已打开歌手：{artist.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 打开歌手失败: {ex}");
                MessageBox.Show($"加载歌手信息失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载歌手失败");
            }
        }

        private List<ListItemInfo> BuildArtistEntryItems(ArtistInfo artist, ArtistDetail? detail)
        {
            var result = new List<ListItemInfo>();

            var info = detail ?? artist;

            result.Add(new ListItemInfo
            {
                Type = ListItemType.Artist,
                Artist = new ArtistInfo
                {
                    Id = artist.Id,
                    Name = info.Name,
                    Alias = info.Alias,
                    PicUrl = info.PicUrl,
                    AreaCode = info.AreaCode,
                    AreaName = info.AreaName,
                    TypeCode = info.TypeCode,
                    TypeName = info.TypeName,
                    MusicCount = info.MusicCount,
                    AlbumCount = info.AlbumCount,
                    MvCount = info.MvCount,
                    BriefDesc = info.BriefDesc,
                    Description = info.Description,
                    IsSubscribed = info.IsSubscribed
                }
            });

            result.Add(new ListItemInfo
            {
                Type = ListItemType.Category,
                CategoryId = $"artist_top_{artist.Id}",
                CategoryName = "热门 50 首",
                CategoryDescription = "网易云热门 50 首歌曲",
                ItemCount = Math.Min(50, info.MusicCount > 0 ? info.MusicCount : 50),
                ItemUnit = "首"
            });

            result.Add(new ListItemInfo
            {
                Type = ListItemType.Category,
                CategoryId = $"artist_songs_{artist.Id}",
                CategoryName = "全部单曲",
                CategoryDescription = "按热度排序，可分页浏览",
                ItemCount = info.MusicCount,
                ItemUnit = "首"
            });

            result.Add(new ListItemInfo
            {
                Type = ListItemType.Category,
                CategoryId = $"artist_albums_{artist.Id}",
                CategoryName = "全部专辑",
                CategoryDescription = "歌手专辑列表",
                ItemCount = info.AlbumCount,
                ItemUnit = "张"
            });

            return result;
        }

        private async Task LoadArtistTopSongsAsync(long artistId, bool skipSave = false)
        {
            try
            {
                UpdateStatusBar("正在加载热门歌曲...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                var songs = await _apiClient.GetArtistTopSongsAsync(artistId);
                DisplaySongs(songs,
                    showPagination: false,
                    startIndex: 1,
                    preserveSelection: false,
                    viewSource: $"artist_songs_top:{artistId}",
                    accessibleName: "热门歌曲");

                UpdateStatusBar("热门歌曲已加载");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 加载热门歌曲失败: {ex}");
                MessageBox.Show($"加载热门歌曲失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载热门歌曲失败");
            }
        }

        private async Task LoadArtistSongsAsync(
            long artistId,
            int offset = 0,
            bool skipSave = false,
            ArtistSongSortOption? orderOverride = null)
        {
            try
            {
                UpdateStatusBar("正在加载歌手单曲...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                if (orderOverride.HasValue)
                {
                    _artistSongSortState.SetOption(orderOverride.Value);
                }

                string orderToken = MapArtistSongsOrder(_artistSongSortState.CurrentOption);
                var (songs, hasMore, total) = await _apiClient.GetArtistSongsAsync(artistId, ArtistSongsPageSize, offset, orderToken);

                _currentArtistSongsOffset = offset;
                _currentArtistSongsHasMore = hasMore;

                DisplaySongs(songs,
                    showPagination: true,
                    hasNextPage: hasMore,
                    startIndex: offset + 1,
                    preserveSelection: false,
                    viewSource: $"artist_songs:{artistId}:order{orderToken}:offset{offset}",
                    accessibleName: "歌手单曲列表");
                UpdateArtistSongsSortMenuChecks();

                UpdateStatusBar($"已加载单曲（{offset + 1}-{offset + songs.Count} / {total}）");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 加载单曲失败: {ex}");
                MessageBox.Show($"加载歌手单曲失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载歌手单曲失败");
            }
        }

        private async Task LoadArtistAlbumsAsync(
            long artistId,
            int offset = 0,
            bool skipSave = false,
            ArtistAlbumSortOption? sortOverride = null)
        {
            try
            {
                UpdateStatusBar("正在加载歌手专辑...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                if (sortOverride.HasValue)
                {
                    _artistAlbumSortState.SetOption(sortOverride.Value);
                }

                List<AlbumInfo> albumsToDisplay;
                bool viewHasMore;
                int totalCount;
                int appliedOffset = offset;

                if (_artistAlbumSortState.CurrentOption == ArtistAlbumSortOption.Oldest)
                {
                    var ascendingResult = await LoadArtistAlbumsAscendingPageAsync(artistId, offset);
                    albumsToDisplay = ascendingResult.Albums;
                    appliedOffset = ascendingResult.NormalizedOffset;
                    totalCount = _currentArtistAlbumsTotalCount;
                    viewHasMore = ascendingResult.HasMore;
                }
                else
                {
                var result = await _apiClient.GetArtistAlbumsAsync(artistId, ArtistAlbumsPageSize, offset);
                albumsToDisplay = result.Albums ?? new List<AlbumInfo>();
                totalCount = result.TotalCount;
                _currentArtistAlbumsTotalCount = totalCount;
                viewHasMore = result.HasMore;
                }

                _currentArtistAlbumsOffset = appliedOffset;
                _currentArtistAlbumsHasMore = viewHasMore;

                DisplayAlbums(albumsToDisplay,
                    preserveSelection: false,
                viewSource: $"artist_albums:{artistId}:order{MapArtistAlbumSort(_artistAlbumSortState.CurrentOption)}:offset{appliedOffset}",
                    accessibleName: "歌手专辑列表",
                    startIndex: appliedOffset + 1,
                    showPagination: true,
                    hasNextPage: viewHasMore);
                UpdateArtistAlbumsSortMenuChecks();

                UpdateStatusBar($"已加载专辑（{appliedOffset + 1}-{appliedOffset + albumsToDisplay.Count} / {totalCount}）");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 加载专辑失败: {ex}");
                MessageBox.Show($"加载歌手专辑失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载歌手专辑失败");
            }
        }

        private async Task LoadArtistFavoritesAsync(bool skipSave = false, bool preserveSelection = false)
        {
            try
            {
                UpdateStatusBar("正在加载收藏的歌手...");

                if (!IsUserLoggedIn())
                {
                    MessageBox.Show("请先登录后查看收藏的歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("请先登录");
                    return;
                }

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
                var result = await _apiClient.GetArtistSubscriptionsAsync(limit: 200, offset: 0);
                var favoriteArtists = result?.Items ?? new List<ArtistInfo>();
                bool hasMore = result?.HasMore ?? false;
                int offset = result?.Offset ?? 0;
                foreach (var artist in favoriteArtists)
                {
                    artist.IsSubscribed = true;
                }

                DisplayArtists(favoriteArtists,
                    showPagination: hasMore,
                    hasNextPage: hasMore,
                    startIndex: offset + 1,
                    preserveSelection: preserveSelection,
                    viewSource: "artist_favorites",
                    accessibleName: "收藏的歌手");

                UpdateStatusBar($"收藏的歌手：{favoriteArtists.Count} 位");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 加载收藏歌手失败: {ex}");
                MessageBox.Show($"加载收藏的歌手失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载收藏的歌手失败");
            }
        }

        private Task LoadArtistCategoryTypesAsync(bool skipSave = false)
        {
            if (!skipSave)
            {
                SaveNavigationState();
            }

            var typeOptions = ArtistMetadataHelper.GetTypeOptions(includeAll: true);
            var items = typeOptions.Select(option => new ListItemInfo
            {
                Type = ListItemType.Category,
                CategoryId = $"artist_type_{option.Code}",
                CategoryName = option.DisplayName,
            }).ToList();

            DisplayListItems(items,
                viewSource: "artist_category_types",
                accessibleName: "歌手类型分类");

            UpdateStatusBar("请选择歌手类型");

            return Task.CompletedTask;
        }

        private Task LoadArtistCategoryAreasAsync(int typeCode, bool skipSave = false)
        {
            if (!skipSave)
            {
                SaveNavigationState();
            }

            _currentArtistTypeFilter = typeCode;

            var areaOptions = ArtistMetadataHelper.GetAreaOptions(includeAll: true);
            var items = areaOptions.Select(option => new ListItemInfo
            {
                Type = ListItemType.Category,
                CategoryId = $"artist_area_{typeCode}_{option.Code}",
                CategoryName = option.DisplayName,
            }).ToList();

            DisplayListItems(items,
                viewSource: $"artist_category_type:{typeCode}",
                accessibleName: "歌手地区分类");

            UpdateStatusBar("请选择地区");

            return Task.CompletedTask;
        }

        private async Task LoadArtistsByCategoryAsync(int typeCode, int areaCode, int offset = 0, bool skipSave = false)
        {
            try
            {
                UpdateStatusBar("正在加载歌手列表...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                _currentArtistTypeFilter = typeCode;
                _currentArtistAreaFilter = areaCode;

                var result = await _apiClient.GetArtistsByCategoryAsync(typeCode, areaCode, limit: ArtistSongsPageSize, offset: offset);

                _currentArtistCategoryHasMore = result.HasMore;

                DisplayArtists(result.Items,
                    showPagination: result.HasMore,
                    hasNextPage: result.HasMore,
                    startIndex: result.Offset + 1,
                    preserveSelection: false,
                    viewSource: $"artist_category_list:{typeCode}:{areaCode}:offset{offset}",
                    accessibleName: "歌手分类列表");

                UpdateStatusBar($"歌手分类列表：{result.Items.Count} 位");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 加载分类歌手失败: {ex}");
                MessageBox.Show($"加载歌手分类失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载歌手分类失败");
            }
        }

        private void ConfigureArtistContextMenu(ArtistInfo artist)
        {
            insertPlayMenuItem.Visible = false;
            likeSongMenuItem.Visible = false;
            unlikeSongMenuItem.Visible = false;
            addToPlaylistMenuItem.Visible = false;
            removeFromPlaylistMenuItem.Visible = false;
            downloadSongMenuItem.Visible = false;
            downloadPlaylistMenuItem.Visible = false;
            downloadAlbumMenuItem.Visible = false;
            batchDownloadMenuItem.Visible = false;
            downloadCategoryMenuItem.Visible = false;
            batchDownloadPlaylistsMenuItem.Visible = false;
            cloudMenuSeparator.Visible = false;
            subscribePlaylistMenuItem.Visible = false;
            unsubscribePlaylistMenuItem.Visible = false;
            deletePlaylistMenuItem.Visible = false;
            subscribeAlbumMenuItem.Visible = false;
            unsubscribeAlbumMenuItem.Visible = false;

            shareArtistMenuItem.Visible = true;
            shareArtistMenuItem.Tag = artist;
            subscribeArtistMenuItem.Tag = artist;
            unsubscribeArtistMenuItem.Tag = artist;

            bool hasArtistActions = shareArtistMenuItem.Visible;

            if (IsUserLoggedIn())
            {
                bool artistSubscribed = IsArtistSubscribed(artist);
                subscribeArtistMenuItem.Visible = !artistSubscribed;
                unsubscribeArtistMenuItem.Visible = artistSubscribed;
                hasArtistActions |= subscribeArtistMenuItem.Visible || unsubscribeArtistMenuItem.Visible;
            }
            else
            {
                subscribeArtistMenuItem.Visible = false;
                unsubscribeArtistMenuItem.Visible = false;
            }

            toolStripSeparatorArtist.Visible = hasArtistActions;
        }

        private ArtistInfo? GetArtistFromMenuSender(object sender)
        {
            if (sender is ToolStripMenuItem menuItem && menuItem.Tag is ArtistInfo taggedArtist)
            {
                return taggedArtist;
            }

            return GetSelectedArtistFromSelection();
        }

        private ArtistInfo? GetSelectedArtistFromSelection()
        {
            if (resultListView.SelectedItems.Count == 0)
            {
                return null;
            }

            var selectedItem = resultListView.SelectedItems[0];
            if (selectedItem.Tag is ArtistInfo artist)
            {
                return artist;
            }

            if (selectedItem.Tag is ListItemInfo listItem && listItem.Type == ListItemType.Artist)
            {
                return listItem.Artist;
            }

            return null;
        }

        private void shareArtistMenuItem_Click(object sender, EventArgs e)
        {
            var artist = GetArtistFromMenuSender(sender);
            if (artist == null)
            {
                return;
            }

            try
            {
                string url = $"https://music.163.com/#/artist?id={artist.Id}";
                Clipboard.SetText(url);
                UpdateStatusBar("歌手链接已复制到剪贴板");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制链接失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("复制链接失败");
            }
        }

        private async void subscribeArtistMenuItem_Click(object sender, EventArgs e)
        {
            var artist = GetArtistFromMenuSender(sender);
            if (artist == null)
            {
                return;
            }

            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录后再收藏歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar("正在收藏歌手...");
                bool success = await _apiClient.SetArtistSubscriptionAsync(artist.Id, true);
                if (success)
                {
                    artist.IsSubscribed = true;
                    UpdateArtistSubscriptionState(artist.Id, true);
                    MessageBox.Show($"已收藏歌手：{artist.Name}", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("收藏歌手成功");
                    await RefreshArtistListAfterSubscriptionAsync(artist.Id);
                }
                else
                {
                    MessageBox.Show("收藏歌手失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("收藏歌手失败");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"收藏歌手失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("收藏歌手失败");
            }
        }

        private async void unsubscribeArtistMenuItem_Click(object sender, EventArgs e)
        {
            var artist = GetArtistFromMenuSender(sender);
            if (artist == null)
            {
                return;
            }

            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录后再取消收藏歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar("正在取消收藏歌手...");
                bool success = await _apiClient.SetArtistSubscriptionAsync(artist.Id, false);
                if (success)
                {
                    artist.IsSubscribed = false;
                    UpdateArtistSubscriptionState(artist.Id, false);
                    MessageBox.Show($"已取消收藏歌手：{artist.Name}", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("取消收藏歌手成功");
                    await RefreshArtistListAfterSubscriptionAsync(artist.Id);
                }
                else
                {
                    MessageBox.Show("取消收藏歌手失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("取消收藏歌手失败");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"取消收藏歌手失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("取消收藏歌手失败");
            }
        }

        #region 歌手统计同步

        private void ApplyArtistDetailToArtist(ArtistInfo artist, ArtistDetail detail)
        {
            if (artist == null || detail == null)
            {
                return;
            }

            bool hasValidDetailName = !string.IsNullOrWhiteSpace(detail.Name);
            bool isPlaceholderName =
                string.IsNullOrWhiteSpace(artist.Name) ||
                string.Equals(artist.Name, "歌手", StringComparison.OrdinalIgnoreCase) ||
                (artist.Id > 0 &&
                 string.Equals(artist.Name, $"歌手 {artist.Id}", StringComparison.OrdinalIgnoreCase));

            if (hasValidDetailName && (isPlaceholderName ||
                !string.Equals(artist.Name, detail.Name, StringComparison.OrdinalIgnoreCase)))
            {
                artist.Name = detail.Name;
            }

            artist.MusicCount = detail.MusicCount;
            artist.AlbumCount = detail.AlbumCount;
            artist.MvCount = detail.MvCount;
            artist.BriefDesc = string.IsNullOrWhiteSpace(detail.BriefDesc) ? artist.BriefDesc : detail.BriefDesc;
            artist.Description = string.IsNullOrWhiteSpace(detail.Description) ? artist.Description : detail.Description;
            artist.IsSubscribed = detail.IsSubscribed;

            UpdateArtistStatsCache(artist.Id, detail.MusicCount, detail.AlbumCount);
            UpdateArtistStatsInView(artist.Id, detail.MusicCount, detail.AlbumCount);
        }

        private bool TryGetCachedArtistStats(long artistId, out (int MusicCount, int AlbumCount) stats)
        {
            lock (_artistStatsCache)
            {
                return _artistStatsCache.TryGetValue(artistId, out stats);
            }
        }

        private void UpdateArtistStatsCache(long artistId, int musicCount, int albumCount)
        {
            if (artistId <= 0)
            {
                return;
            }

            lock (_artistStatsCache)
            {
                _artistStatsCache[artistId] = (musicCount, albumCount);
            }
        }

        private void UpdateArtistStatsInView(long artistId, int musicCount, int albumCount)
        {
            SafeInvoke(() =>
            {
                foreach (ListViewItem item in resultListView.Items)
                {
                    if (item.Tag is ArtistInfo info && info.Id == artistId)
                    {
                        info.MusicCount = musicCount;
                        info.AlbumCount = albumCount;
                        if (item.SubItems.Count > 2)
                        {
                            item.SubItems[2].Text = $"歌曲 {musicCount} / 专辑 {albumCount}";
                        }

                        break;
                    }
                }
            });
        }

        private void ScheduleArtistStatsRefresh(IEnumerable<ArtistInfo> artists)
        {
            if (_apiClient == null)
            {
                return;
            }

            var pending = artists?
                .Where(a => a != null && a.Id > 0 && (a.MusicCount <= 0 || a.AlbumCount <= 0))
                .GroupBy(a => a.Id)
                .Select(g => g.First())
                .Take(20)
                .ToList();

            if (pending == null || pending.Count == 0)
            {
                return;
            }

            _artistStatsRefreshCts?.Cancel();
            _artistStatsRefreshCts?.Dispose();

            var cts = new CancellationTokenSource();
            _artistStatsRefreshCts = cts;
            var token = cts.Token;

            lock (_artistStatsInFlight)
            {
                _artistStatsInFlight.Clear();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var artist in pending)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (TryGetCachedArtistStats(artist.Id, out var cachedStats))
                        {
                            UpdateArtistStatsInView(artist.Id, cachedStats.MusicCount, cachedStats.AlbumCount);
                            continue;
                        }

                        bool shouldFetch;
                        lock (_artistStatsInFlight)
                        {
                            shouldFetch = _artistStatsInFlight.Add(artist.Id);
                        }

                        if (!shouldFetch)
                        {
                            continue;
                        }

                        try
                        {
                            var detail = await _apiClient.GetArtistDetailAsync(artist.Id, includeIntroduction: false).ConfigureAwait(false);
                            if (detail != null)
                            {
                                UpdateArtistStatsCache(artist.Id, detail.MusicCount, detail.AlbumCount);
                                UpdateArtistStatsInView(artist.Id, detail.MusicCount, detail.AlbumCount);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Artist] 刷新歌手统计失败: {ex.Message}");
                        }
                        finally
                        {
                            lock (_artistStatsInFlight)
                            {
                                _artistStatsInFlight.Remove(artist.Id);
                            }
                        }

                        try
                        {
                            await Task.Delay(150, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 忽略取消
                }
            }, token);
        }

        #endregion

        private async Task RefreshArtistListAfterSubscriptionAsync(long artistId)
        {
            try
            {
                if (string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadArtistFavoritesAsync(skipSave: true, preserveSelection: true);
                }
                else if (_currentViewSource != null && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadArtistSongsAsync(artistId, _currentArtistSongsOffset, skipSave: true, orderOverride: _artistSongSortState.CurrentOption);
                }
                else if (_currentViewSource != null && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadArtistAlbumsAsync(artistId, _currentArtistAlbumsOffset, skipSave: true, sortOverride: _artistAlbumSortState.CurrentOption);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artist] 刷新列表失败: {ex}");
            }
        }

        private static long ParseArtistIdFromViewSource(string source, string prefix)
        {
            if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var slice = source.Substring(prefix.Length);
            int colonIndex = slice.IndexOf(':');
            if (colonIndex >= 0)
            {
                slice = slice.Substring(0, colonIndex);
            }

            return long.TryParse(slice, out var value) ? value : 0;
        }

        private static void ParseArtistListViewSource(string source, out long artistId, out int offset, out string order, string defaultOrder = "hot")
        {
            artistId = 0;
            offset = 0;
            order = defaultOrder;

            var parts = source.Split(':');
            if (parts.Length >= 2)
            {
                long.TryParse(parts[1], out artistId);
            }

            var offsetPart = parts.LastOrDefault(p => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(offsetPart))
            {
                int.TryParse(offsetPart.Substring("offset".Length), out offset);
            }

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.StartsWith("order", StringComparison.OrdinalIgnoreCase))
                {
                    if (part.Length > "order".Length)
                    {
                        order = part.Substring("order".Length);
                        break;
                    }
                    else if (i + 1 < parts.Length)
                    {
                        order = parts[i + 1];
                        break;
                    }
                }
            }
        }

        private static void ParseArtistCategoryListViewSource(string source, out int typeCode, out int areaCode, out int offset)
        {
            typeCode = -1;
            areaCode = -1;
            offset = 0;

            var parts = source.Split(':');
            if (parts.Length >= 3)
            {
                int.TryParse(parts[1], out typeCode);
                int.TryParse(parts[2], out areaCode);
            }

            var offsetPart = parts.LastOrDefault(p => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(offsetPart))
            {
                int.TryParse(offsetPart.Substring("offset".Length), out offset);
            }
        }

        private static string MapArtistSongsOrder(ArtistSongSortOption order)
        {
            return order == ArtistSongSortOption.Time ? "time" : "hot";
        }

        private static ArtistSongSortOption ResolveArtistSongsOrder(string? order)
        {
            return string.Equals(order, "time", StringComparison.OrdinalIgnoreCase)
                ? ArtistSongSortOption.Time
                : ArtistSongSortOption.Hot;
        }

        private static string MapArtistAlbumSort(ArtistAlbumSortOption sort)
        {
            return sort == ArtistAlbumSortOption.Oldest ? "oldest" : "latest";
        }

        private static ArtistAlbumSortOption ResolveArtistAlbumSort(string? sort)
        {
            return string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase)
                ? ArtistAlbumSortOption.Oldest
                : ArtistAlbumSortOption.Latest;
        }

        private async Task EnsureArtistAlbumsTotalCountAsync(long artistId)
        {
            if (_currentArtistAlbumsTotalCount > 0)
            {
                return;
            }

            var (_, _, totalCount) = await _apiClient.GetArtistAlbumsAsync(artistId, 1, 0);
            _currentArtistAlbumsTotalCount = totalCount;
        }

        private void ClearArtistAlbumsAscendingCache(long? artistId = null)
        {
            if (artistId.HasValue)
            {
                _artistAlbumsAscendingCache.Remove(artistId.Value);
            }
            else
            {
                _artistAlbumsAscendingCache.Clear();
            }
        }

        private void TrimArtistAlbumsAscendingCache()
        {
            while (_artistAlbumsAscendingCache.Count > ArtistAlbumsAscendingCacheLimit)
            {
                var keyToRemove = _artistAlbumsAscendingCache.Keys.FirstOrDefault();
                if (keyToRemove != 0)
                {
                    _artistAlbumsAscendingCache.Remove(keyToRemove);
                }
                else
                {
                    break;
                }
            }
        }

        private async Task<List<AlbumInfo>> LoadArtistAlbumsAscendingListAsync(long artistId)
        {
            var allAlbums = new List<AlbumInfo>();
            int offset = 0;
            bool hasMore = true;
            int safety = 0;
            while (hasMore && safety < 200)
            {
                var (albums, more, total) = await _apiClient.GetArtistAlbumsAsync(artistId, ArtistAlbumsPageSize, offset);
                if (albums == null || albums.Count == 0)
                {
                    break;
                }

                allAlbums.AddRange(albums);
                offset += albums.Count;
                hasMore = more;
                _currentArtistAlbumsTotalCount = total;
                safety++;
            }

            allAlbums.Reverse();
            return allAlbums;
        }

        private async Task<(List<AlbumInfo> Albums, int NormalizedOffset, bool HasMore)> LoadArtistAlbumsAscendingPageAsync(long artistId, int offset)
        {
            if (!_artistAlbumsAscendingCache.TryGetValue(artistId, out var cachedAlbums))
            {
                var fullResult = await LoadArtistAlbumsAscendingListAsync(artistId);
                cachedAlbums = fullResult;
                _artistAlbumsAscendingCache[artistId] = cachedAlbums;
                TrimArtistAlbumsAscendingCache();
            }

            int totalCount = cachedAlbums.Count;
            if (totalCount == 0)
            {
                return (new List<AlbumInfo>(), 0, false);
            }

            int normalizedOffset = Math.Max(0, Math.Min(offset, Math.Max(0, totalCount - 1)));
            int remaining = Math.Max(0, totalCount - normalizedOffset);
            int takeCount = Math.Min(ArtistAlbumsPageSize, remaining);
            if (takeCount <= 0)
            {
                return (new List<AlbumInfo>(), normalizedOffset, false);
            }

            var page = cachedAlbums.Skip(normalizedOffset).Take(takeCount).ToList();
            bool hasMore = normalizedOffset + takeCount < totalCount;
            return (page, normalizedOffset, hasMore);
        }


    }

    internal enum ArtistSongSortOption
    {
        Hot,
        Time
    }

    internal enum ArtistAlbumSortOption
    {
        Latest,
        Oldest
    }
}
