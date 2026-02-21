using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Auth;
using YTPlayer.Models;
using YTPlayer.Utils;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        #region 评论相关
        private sealed class CommentCursorCache
        {
            public Dictionary<int, string> CursorByPage { get; } = new Dictionary<int, string>();
            public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
        }

        private string ResolveCommentApiBaseUrl()
        {
            string? configured = _config?.CommentApiBaseUrl;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim().TrimEnd('/');
            }

            return SIMPLIFIED_API_BASE;
        }

        private async Task<JObject> GetCommentNewApiAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            string baseUrl = ResolveCommentApiBaseUrl();
            string url = $"{baseUrl}/comment/new";
            foreach (var kv in parameters)
            {
                url = AppendQueryParameter(url, kv.Key, kv.Value ?? string.Empty);
            }

            var response = await _simplifiedClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JObject.Parse(responseText);
        }


        /// <summary>
        /// 获取评论
        /// </summary>
        public async Task<CommentResult> GetCommentsAsync(string resourceId, CommentType type = CommentType.Song,
            int pageNo = 1, int pageSize = 20, CommentSortType sortType = CommentSortType.Hot,
            string? cursor = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            resourceId = resourceId.Trim();
            if (pageNo <= 0) pageNo = 1;
            if (pageSize <= 0) pageSize = 20;

            string threadId = BuildCommentThreadId(type, resourceId);
            int sortCode = MapCommentSortType(sortType);
            string resolvedCursor = BuildCommentCursor(sortCode, pageNo, pageSize, cursor);

            var payload = new Dictionary<string, object>
            {
                { "threadId", threadId },
                { "pageNo", pageNo },
                { "pageSize", pageSize },
                { "cursor", resolvedCursor },
                { "sortType", sortCode },
                { "showInner", false }
            };

            var response = await PostEApiAsync<JObject>("/api/v2/resource/comments", payload, useIosHeaders: false);
            int code = response["code"]?.Value<int>() ?? -1;
            if (code != 200)
            {
                string? message = response["message"]?.Value<string>()
                    ?? response["msg"]?.Value<string>()
                    ?? "未知错误";
                throw new InvalidOperationException($"评论接口请求失败: code={code}, message={message}");
            }

            var parsed = ParseComments(response, sortType);
            if (parsed.PageNumber <= 0)
            {
                parsed.PageNumber = pageNo;
            }
            if (parsed.PageSize <= 0 || parsed.PageSize != pageSize)
            {
                parsed.PageSize = pageSize;
            }
            return parsed;
        }

        private static string? ResolveTimeCursorFromComments(CommentResult result)
        {
            if (result == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(result.Cursor))
            {
                return result.Cursor;
            }

            if (result.Comments != null && result.Comments.Count > 0)
            {
                var last = result.Comments[result.Comments.Count - 1];
                if (last != null && last.TimeMilliseconds > 0)
                {
                    return last.TimeMilliseconds.ToString(CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        private async Task<CommentResult> GetCommentsNewAsync(
            string resourceId,
            CommentType type,
            int pageNo,
            int pageSize,
            CommentSortType sortType,
            string? cursor,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            resourceId = resourceId.Trim();
            if (pageNo <= 0) pageNo = 1;
            if (pageSize <= 0) pageSize = 20;

            int sortCode = MapCommentSortType(sortType);
            var parameters = new Dictionary<string, string>
            {
                ["type"] = ((int)type).ToString(CultureInfo.InvariantCulture),
                ["id"] = resourceId,
                ["pageNo"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture),
                ["sortType"] = sortCode.ToString(CultureInfo.InvariantCulture)
            };

            if (sortType == CommentSortType.Time && pageNo > 1 && !string.IsNullOrWhiteSpace(cursor))
            {
                parameters["cursor"] = cursor!;
            }

            JObject response;
            try
            {
                response = await GetCommentNewApiAsync(parameters, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return await GetCommentsAsync(resourceId, type, pageNo, pageSize, sortType, cursor, cancellationToken)
                    .ConfigureAwait(false);
            }
            int code = response["code"]?.Value<int>() ?? -1;
            if (code != 200)
            {
                string? message = response["message"]?.Value<string>()
                    ?? response["msg"]?.Value<string>()
                    ?? "未知错误";
                throw new InvalidOperationException($"评论接口请求失败: code={code}, message={message}");
            }

            var parsed = ParseComments(response, sortType);
            if (parsed.PageNumber <= 0)
            {
                parsed.PageNumber = pageNo;
            }
            if (parsed.PageSize <= 0 || parsed.PageSize != pageSize)
            {
                parsed.PageSize = pageSize;
            }

            if (sortType == CommentSortType.Time && string.IsNullOrWhiteSpace(parsed.Cursor))
            {
                parsed.Cursor = ResolveTimeCursorFromComments(parsed);
            }

            return parsed;
        }

        /// <summary>
        /// 获取楼层评论（指定父级评论的回复列表）
        /// </summary>
        public async Task<CommentFloorResult> GetCommentFloorAsync(string resourceId, string parentCommentId,
            CommentType type = CommentType.Song, long? timeCursor = null, int limit = 20,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            if (string.IsNullOrWhiteSpace(parentCommentId))
            {
                throw new ArgumentException("parentCommentId cannot be null or empty", nameof(parentCommentId));
            }

            resourceId = resourceId.Trim();
            parentCommentId = parentCommentId.Trim();

            var payload = new Dictionary<string, object>
            {
                { "parentCommentId", parentCommentId },
                { "threadId", BuildCommentThreadId(type, resourceId) },
                { "time", timeCursor ?? -1 },
                { "limit", limit <= 0 ? 20 : limit },
                { "id", resourceId },
                { "type", (int)type }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/resource/comment/floor/get",
                payload,
                cancellationToken: cancellationToken,
                autoConvertApiSegment: true);

            return ParseCommentFloor(response, parentCommentId);
        }

        public async Task<CommentResult> GetCommentsPageAsync(string resourceId, CommentType type = CommentType.Song,
            int pageNo = 1, int pageSize = 20, CommentSortType sortType = CommentSortType.Hot,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            resourceId = resourceId.Trim();
            if (pageNo <= 0) pageNo = 1;
            if (pageSize <= 0) pageSize = 20;

            if (sortType != CommentSortType.Time)
            {
                return await GetCommentsAsync(resourceId, type, pageNo, pageSize, sortType, null, cancellationToken).ConfigureAwait(false);
            }

            string threadId = BuildCommentThreadId(type, resourceId);
            int sortCode = MapCommentSortType(sortType);
            string cacheKey = BuildCommentCursorCacheKey(threadId, sortCode, pageSize);
            return await GetCommentsPageWithCursorCacheAsync(resourceId, type, pageNo, pageSize, sortType, cacheKey, cancellationToken).ConfigureAwait(false);  
        }

        public async Task<CommentResult> GetCommentsNewPageAsync(string resourceId, CommentType type = CommentType.Song,
            int pageNo = 1, int pageSize = 20, CommentSortType sortType = CommentSortType.Recommend,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            resourceId = resourceId.Trim();
            if (pageNo <= 0) pageNo = 1;
            if (pageSize <= 0) pageSize = 20;

            if (sortType != CommentSortType.Time)
            {
                return await GetCommentsNewAsync(resourceId, type, pageNo, pageSize, sortType, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            string threadId = BuildCommentThreadId(type, resourceId);
            int sortCode = MapCommentSortType(sortType);
            string cacheKey = BuildCommentCursorCacheKey($"{threadId}:new", sortCode, pageSize);
            return await GetCommentsNewPageWithCursorCacheAsync(resourceId, type, pageNo, pageSize, sortType, cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<CommentFloorResult> GetCommentFloorPageAsync(string resourceId, string parentCommentId,
            CommentType type = CommentType.Song, int pageNo = 1, int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            if (string.IsNullOrWhiteSpace(parentCommentId))
            {
                throw new ArgumentException("parentCommentId cannot be null or empty", nameof(parentCommentId));
            }

            resourceId = resourceId.Trim();
            parentCommentId = parentCommentId.Trim();
            if (pageNo <= 0) pageNo = 1;
            if (pageSize <= 0) pageSize = 20;

            string threadId = BuildCommentThreadId(type, resourceId);
            string cacheKey = BuildCommentFloorCursorCacheKey(threadId, parentCommentId, pageSize);
            return await GetCommentFloorPageWithCursorCacheAsync(resourceId, parentCommentId, type, pageNo, pageSize, cacheKey, cancellationToken).ConfigureAwait(false);
        }

        private async Task<CommentResult> GetCommentsPageWithCursorCacheAsync(string resourceId, CommentType type, int pageNo, int pageSize,
            CommentSortType sortType, string cacheKey, CancellationToken cancellationToken)
        {
            int safePageNo = Math.Max(1, pageNo);
            int safePageSize = Math.Max(1, pageSize);

            TrimCommentCursorCaches();

            var gate = _commentCursorLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cache = _commentCursorCaches.GetOrAdd(cacheKey, _ => new CommentCursorCache());
                cache.LastAccessUtc = DateTime.UtcNow;
                if (cache.CursorByPage.Count == 0)
                {
                    cache.CursorByPage[1] = "0";
                }

                if (cache.CursorByPage.TryGetValue(safePageNo, out string? cachedCursor))
                {
                    var result = await GetCommentsAsync(resourceId, type, safePageNo, safePageSize, sortType, cachedCursor, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.Cursor))
                    {
                        cache.CursorByPage[safePageNo + 1] = result.Cursor;
                    }
                    return result;
                }

                int nearestPage = cache.CursorByPage.Keys.Where(key => key <= safePageNo).DefaultIfEmpty(1).Max();
                string cursor = cache.CursorByPage.TryGetValue(nearestPage, out var nearestCursor)
                    ? nearestCursor
                    : "0";

                CommentResult? lastResult = null;
                for (int page = nearestPage; page <= safePageNo; page++)
                {
                    var result = await GetCommentsAsync(resourceId, type, page, safePageSize, sortType, cursor, cancellationToken)
                        .ConfigureAwait(false);
                    lastResult = result;

                    string? nextCursor = result.Cursor;
                    if (!string.IsNullOrWhiteSpace(nextCursor))
                    {
                        cache.CursorByPage[page + 1] = nextCursor;
                    }

                    if (page == safePageNo)
                    {
                        return result;
                    }

                    if (string.IsNullOrWhiteSpace(nextCursor) || !result.HasMore)
                    {
                        break;
                    }

                    cursor = nextCursor;
                }

                return new CommentResult
                {
                    PageNumber = safePageNo,
                    PageSize = safePageSize,
                    SortType = sortType,
                    TotalCount = lastResult?.TotalCount ?? 0,
                    HasMore = false
                };
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<CommentResult> GetCommentsNewPageWithCursorCacheAsync(string resourceId, CommentType type, int pageNo, int pageSize,
            CommentSortType sortType, string cacheKey, CancellationToken cancellationToken)
        {
            int safePageNo = Math.Max(1, pageNo);
            int safePageSize = Math.Max(1, pageSize);

            TrimCommentCursorCaches();

            var gate = _commentCursorLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cache = _commentCursorCaches.GetOrAdd(cacheKey, _ => new CommentCursorCache());
                cache.LastAccessUtc = DateTime.UtcNow;
                if (cache.CursorByPage.Count == 0)
                {
                    cache.CursorByPage[1] = "0";
                }

                if (cache.CursorByPage.TryGetValue(safePageNo, out string? cachedCursor))
                {
                    var result = await GetCommentsNewAsync(resourceId, type, safePageNo, safePageSize, sortType, cachedCursor, cancellationToken)
                        .ConfigureAwait(false);
                    string? nextCursor = ResolveTimeCursorFromComments(result);
                    if (!string.IsNullOrWhiteSpace(nextCursor))
                    {
                        cache.CursorByPage[safePageNo + 1] = nextCursor;
                    }
                    return result;
                }

                int nearestPage = cache.CursorByPage.Keys.Where(key => key <= safePageNo).DefaultIfEmpty(1).Max();
                string cursor = cache.CursorByPage.TryGetValue(nearestPage, out var nearestCursor)
                    ? nearestCursor
                    : "0";

                CommentResult? lastResult = null;
                for (int page = nearestPage; page <= safePageNo; page++)
                {
                    var result = await GetCommentsNewAsync(resourceId, type, page, safePageSize, sortType, cursor, cancellationToken)
                        .ConfigureAwait(false);
                    lastResult = result;

                    string? nextCursor = ResolveTimeCursorFromComments(result);
                    if (!string.IsNullOrWhiteSpace(nextCursor))
                    {
                        cache.CursorByPage[page + 1] = nextCursor;
                    }

                    if (page == safePageNo)
                    {
                        return result;
                    }

                    if (!result.HasMore || string.IsNullOrWhiteSpace(nextCursor))
                    {
                        break;
                    }

                    cursor = nextCursor;
                }

                return new CommentResult
                {
                    PageNumber = safePageNo,
                    PageSize = safePageSize,
                    SortType = sortType,
                    TotalCount = lastResult?.TotalCount ?? 0,
                    HasMore = false
                };
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<CommentFloorResult> GetCommentFloorPageWithCursorCacheAsync(string resourceId, string parentCommentId, CommentType type,
            int pageNo, int pageSize, string cacheKey, CancellationToken cancellationToken)
        {
            int safePageNo = Math.Max(1, pageNo);
            int safePageSize = Math.Max(1, pageSize);

            TrimCommentCursorCaches();

            var gate = _commentCursorLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cache = _commentCursorCaches.GetOrAdd(cacheKey, _ => new CommentCursorCache());
                cache.LastAccessUtc = DateTime.UtcNow;
                if (cache.CursorByPage.Count == 0)
                {
                    cache.CursorByPage[1] = "-1";
                }

                if (cache.CursorByPage.TryGetValue(safePageNo, out string? cachedCursor))
                {
                    long cursorValue = ParseFloorCursor(cachedCursor);
                    var result = await GetCommentFloorAsync(resourceId, parentCommentId, type, cursorValue, safePageSize, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.NextTime.HasValue)
                    {
                        cache.CursorByPage[safePageNo + 1] = result.NextTime.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    return result;
                }

                int nearestPage = cache.CursorByPage.Keys.Where(key => key <= safePageNo).DefaultIfEmpty(1).Max();
                string cursorText = cache.CursorByPage.TryGetValue(nearestPage, out var nearestCursor)
                    ? nearestCursor
                    : "-1";
                long cursor = ParseFloorCursor(cursorText);

                CommentFloorResult? lastResult = null;
                for (int page = nearestPage; page <= safePageNo; page++)
                {
                    var result = await GetCommentFloorAsync(resourceId, parentCommentId, type, cursor, safePageSize, cancellationToken)
                        .ConfigureAwait(false);
                    lastResult = result;

                    if (result.NextTime.HasValue)
                    {
                        cache.CursorByPage[page + 1] = result.NextTime.Value.ToString(CultureInfo.InvariantCulture);
                    }

                    if (page == safePageNo)
                    {
                        return result;
                    }

                    if (!result.HasMore || !result.NextTime.HasValue)
                    {
                        break;
                    }

                    cursor = result.NextTime.Value;
                }

                return new CommentFloorResult
                {
                    ParentCommentId = parentCommentId,
                    TotalCount = lastResult?.TotalCount ?? 0,
                    HasMore = false
                };
            }
            finally
            {
                gate.Release();
            }
        }

        private static long ParseFloorCursor(string? cursorText)
        {
            if (string.IsNullOrWhiteSpace(cursorText))
            {
                return -1;
            }

            if (long.TryParse(cursorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return parsed;
            }

            return -1;
        }

        private static string BuildCommentCursorCacheKey(string threadId, int sortCode, int pageSize)
        {
            return $"comment_new:{threadId}:{sortCode}:{pageSize}";
        }

        private static string BuildCommentFloorCursorCacheKey(string threadId, string parentCommentId, int pageSize)
        {
            return $"comment_floor:{threadId}:{parentCommentId}:{pageSize}";
        }

        private void TrimCommentCursorCaches()
        {
            var cutoff = DateTime.UtcNow - CommentCursorCacheTtl;
            foreach (var entry in _commentCursorCaches)
            {
                if (entry.Value.LastAccessUtc < cutoff)
                {
                    _commentCursorCaches.TryRemove(entry.Key, out _);
                    _commentCursorLocks.TryRemove(entry.Key, out _);
                }
            }
        }

        /// <summary>
        /// 发表评论
        /// </summary>
        public Task<CommentMutationResult> AddCommentAsync(string resourceId, string content,
            CommentType type = CommentType.Song, CancellationToken cancellationToken = default)
        {
            return ExecuteCommentMutationAsync(CommentMutationAction.Add, type, resourceId, content, null, cancellationToken);
        }

        /// <summary>
        /// 回复评论
        /// </summary>
        public Task<CommentMutationResult> ReplyCommentAsync(string resourceId, string commentId, string content,
            CommentType type = CommentType.Song, CancellationToken cancellationToken = default)
        {
            return ExecuteCommentMutationAsync(CommentMutationAction.Reply, type, resourceId, content, commentId, cancellationToken);
        }

        /// <summary>
        /// 删除评论
        /// </summary>
        public Task<CommentMutationResult> DeleteCommentAsync(string resourceId, string commentId,
            CommentType type = CommentType.Song, CancellationToken cancellationToken = default)
        {
            return ExecuteCommentMutationAsync(CommentMutationAction.Delete, type, resourceId, null, commentId, cancellationToken);
        }

        private void PrepareCommentRequestFingerprint()
        {
            try
            {
                ApplyBaseCookies(includeAnonymousToken: string.IsNullOrEmpty(_musicU));

                var fingerprint = _authContext?.GetFingerprintSnapshot() ?? new FingerprintSnapshot();
                string ntesNuid = fingerprint.NtesNuid;
                if (string.IsNullOrWhiteSpace(ntesNuid))
                {
                    ntesNuid = EncryptionHelper.GenerateRandomHex(32);
                }

                string nmtId = EncryptionHelper.GenerateRandomHex(32);
                string ntesNnid = $"{ntesNuid},{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

                UpsertCookie("__remember_me", "true");
                UpsertCookie("NMTID", nmtId);
                UpsertCookie("_ntes_nuid", ntesNuid);
                UpsertCookie("_ntes_nnid", ntesNnid);
                UpsertCookie("WEVNSM", "1.0.0");
                UpsertCookie("ntes_kaola_ad", "1");

                if (!string.IsNullOrEmpty(fingerprint.WnmCid))
                {
                    UpsertCookie("WNMCID", fingerprint.WnmCid);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Comments] 准备评论指纹失败: {ex.Message}");
            }
        }

        private async Task<CommentMutationResult> ExecuteCommentMutationAsync(
            CommentMutationAction action,
            CommentType type,
            string resourceId,
            string? content,
            string? commentId,
            CancellationToken cancellationToken)
        {
            PrepareCommentRequestFingerprint();

            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or empty", nameof(resourceId));
            }

            resourceId = resourceId.Trim();
            var payload = new Dictionary<string, object>
            {
                { "threadId", BuildCommentThreadId(type, resourceId) }
            };

            switch (action)
            {
                case CommentMutationAction.Add:
                    {
                        var normalized = content?.Trim();
                        if (string.IsNullOrWhiteSpace(normalized))
                        {
                            throw new ArgumentException("content cannot be null or whitespace", nameof(content));
                        }

                        payload["content"] = normalized!;
                        break;
                    }
                case CommentMutationAction.Reply:
                    {
                        var normalized = content?.Trim();
                        if (string.IsNullOrWhiteSpace(normalized))
                        {
                            throw new ArgumentException("content cannot be null or whitespace", nameof(content));
                        }

                        if (string.IsNullOrWhiteSpace(commentId))
                        {
                            throw new ArgumentException("commentId cannot be null or empty", nameof(commentId));
                        }

                        payload["content"] = normalized!;
                        payload["commentId"] = commentId.Trim();
                        break;
                    }
                case CommentMutationAction.Delete:
                    {
                        if (string.IsNullOrWhiteSpace(commentId))
                        {
                            throw new ArgumentException("commentId cannot be null or empty", nameof(commentId));
                        }

                        payload["commentId"] = commentId.Trim();
                        break;
                    }
            }

            string actionPath = action switch
            {
                CommentMutationAction.Add => "add",
                CommentMutationAction.Reply => "reply",
                CommentMutationAction.Delete => "delete",
                _ => "add"
            };

            try
            {
                const int maxAttempts = 3;
                int attempt = 0;
                JObject? response = null;
                int code = -1;

                while (attempt < maxAttempts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempt++;

                    try
                    {
                        response = await PostWeApiAsync<JObject>(
                            $"/resource/comments/{actionPath}",
                            payload,
                            skipErrorHandling: true,
                            cancellationToken: cancellationToken,
                            userAgentOverride: AuthConstants.WeapiUserAgent);
                    }
                    catch (Exception) when (attempt < maxAttempts)
                    {
                        int delayMs = GetRandomRetryDelay();
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        return new CommentMutationResult
                        {
                            Success = false,
                            Code = -1,
                            Message = ex.Message,
                            CommentId = commentId
                        };
                    }

                    code = response?["code"]?.Value<int>() ?? -1;
                    if (code == 301 && attempt < maxAttempts)
                    {
                        bool refreshed = await TryAutoRefreshLoginAsync().ConfigureAwait(false);
                        if (refreshed)
                        {
                            int delayMs = GetRandomRetryDelay();
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    if ((code == 429 || code == 500 || code == 502 || code == 503 || code == 504) && attempt < maxAttempts)
                    {
                        int delayMs = GetRandomRetryDelay();
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }

                if (response == null)
                {
                    return new CommentMutationResult
                    {
                        Success = false,
                        Code = -1,
                        Message = "未获取到评论接口响应",
                        CommentId = commentId
                    };
                }

                string? message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>();
                string? riskTitle = null;
                string? riskSubtitle = null;
                string? riskUrl = null;
                var dialog = response["data"]?["dialog"] as JObject;
                if (dialog != null)
                {
                    riskTitle = dialog["title"]?.Value<string>();
                    riskSubtitle = dialog["subtitle"]?.Value<string>();
                    riskUrl = dialog["buttonUrl"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = riskSubtitle ?? riskTitle;
                    }
                }

                var result = new CommentMutationResult
                {
                    Success = code == 200,
                    Code = code,
                    Message = message,
                    CommentId = commentId,
                    RiskTitle = riskTitle,
                    RiskSubtitle = riskSubtitle,
                    RiskUrl = riskUrl
                };

                if (result.Success && action != CommentMutationAction.Delete)
                {
                    var commentToken = response["comment"] as JObject
                        ?? response["data"]?["comment"] as JObject;

                    if (commentToken != null)
                    {
                        var parsed = ParseCommentToken(commentToken,
                            action == CommentMutationAction.Reply ? commentId : null);
                        if (parsed != null)
                        {
                            result.Comment = parsed;
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 评论操作失败: {ex.Message}");
                return new CommentMutationResult
                {
                    Success = false,
                    Message = ex.Message,
                    CommentId = commentId
                };
            }
        }

        private enum CommentMutationAction
        {
            Add,
            Reply,
            Delete
        }

        private static string BuildCommentThreadId(CommentType type, string resourceId)
        {
            string prefix = GetCommentThreadPrefix(type);
            return $"{prefix}{resourceId}";
        }

        private static int MapCommentSortType(CommentSortType sortType)
        {
            return sortType switch
            {
                CommentSortType.Recommend => 1,
                CommentSortType.Hot => 2,
                CommentSortType.Time => 3,
                _ => 1
            };
        }

        private static string BuildCommentCursor(int apiSortType, int pageNo, int pageSize, string? cursor)
        {
            int safePageNo = Math.Max(1, pageNo);
            int safePageSize = Math.Max(1, pageSize);
            int offset = (safePageNo - 1) * safePageSize;

            return apiSortType switch
            {
                2 => $"normalHot#{offset}",
                3 => string.IsNullOrWhiteSpace(cursor) ? "0" : cursor!,
                _ => offset.ToString()
            };
        }

        private static string GetCommentThreadPrefix(CommentType type)
        {
            return type switch
            {
                CommentType.Song => "R_SO_4_",
                CommentType.MV => "R_MV_5_",
                CommentType.Playlist => "A_PL_0_",
                CommentType.Album => "R_AL_3_",
                CommentType.DJRadio => "A_DJ_1_",
                CommentType.Video => "R_VI_62_",
                _ => "R_SO_4_"
            };
        }

        #endregion

    }
}
