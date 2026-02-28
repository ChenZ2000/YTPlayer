using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using YTPlayer.Models.Auth;
using YTPlayer.Models;

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        public List<CookieItem> GetAllCookies()
        {
            var result = new List<CookieItem>();

            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uris = new[]
                {
                    MUSIC_URI,
                    INTERFACE_URI,
                    EAPI_URI  // ⭐ 添加 interface3 域名支持
                };

                foreach (var uri in uris)
                {
                    CookieCollection? collection = null;
                    try
                    {
                        collection = _cookieContainer.GetCookies(uri);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Cookie] GetCookies failed: {ex.Message}");
                    }

                    if (collection == null || collection.Count == 0)
                        continue;

                    foreach (Cookie cookie in collection)
                    {
                        string key = $"{cookie.Name}|{cookie.Domain}|{cookie.Path}";
                        if (seen.Add(key))
                        {
                            result.Add(new CookieItem
                            {
                                Name = cookie.Name,
                                Value = cookie.Value,
                                Domain = cookie.Domain,
                                Path = cookie.Path
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] 获取Cookie列表失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 获取当前登录状态的快照副本，供上层安全读取。
        /// </summary>
        public AccountState GetAccountStateSnapshot()
        {
            if (_authContext == null)
            {
                return new AccountState { IsLoggedIn = false };
            }

            try
            {
                var state = _authContext.CurrentAccountState;
                return CloneAccountState(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] 获取登录状态快照失败: {ex.Message}");
                return new AccountState { IsLoggedIn = false };
            }
        }

        private static AccountState CloneAccountState(AccountState source)
        {
            if (source == null)
            {
                return new AccountState { IsLoggedIn = false };
            }

            var clone = new AccountState
            {
                IsLoggedIn = source.IsLoggedIn,
                Cookie = source.Cookie,
                MusicU = source.MusicU,
                CsrfToken = source.CsrfToken,
                UserId = source.UserId,
                Nickname = source.Nickname,
                AvatarUrl = source.AvatarUrl,
                VipType = source.VipType,
                LastUpdated = source.LastUpdated,
                DeviceId = source.DeviceId,
                NmtId = source.NmtId,
                NtesNuid = source.NtesNuid,
                WnmCid = source.WnmCid,
                AntiCheatToken = source.AntiCheatToken,
                AntiCheatTokenExpiresAt = source.AntiCheatTokenExpiresAt
            };

            clone.Cookies = CloneCookieItems(source.Cookies);
            clone.SongLyricLanguagePreferences = CloneSongLyricLanguagePreferences(source.SongLyricLanguagePreferences);
            return clone;
        }

        private static List<CookieItem> CloneCookieItems(IEnumerable<CookieItem> items)
        {
            var clone = new List<CookieItem>();
            if (items == null)
            {
                return clone;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                clone.Add(new CookieItem
                {
                    Name = item.Name,
                    Value = item.Value,
                    Domain = item.Domain,
                    Path = item.Path
                });
            }

            return clone;
        }

        private static Dictionary<string, SongLyricLanguagePreference> CloneSongLyricLanguagePreferences(
            IDictionary<string, SongLyricLanguagePreference>? source)
        {
            var clone = new Dictionary<string, SongLyricLanguagePreference>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return clone;
            }

            foreach (var pair in source)
            {
                string songId = pair.Key?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(songId) || pair.Value == null)
                {
                    continue;
                }

                var selectedKeys = (pair.Value.SelectedLanguageKeys ?? new List<string>())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (selectedKeys.Count == 0)
                {
                    continue;
                }

                clone[songId] = new SongLyricLanguagePreference
                {
                    SongId = songId,
                    SelectedLanguageKeys = selectedKeys
                };
            }

            return clone;
        }

        public IReadOnlyList<string> GetSongLyricLanguagePreference(string songId)
        {
            if (_authContext == null || string.IsNullOrWhiteSpace(songId))
            {
                return Array.Empty<string>();
            }

            return _authContext.GetSongLyricLanguagePreference(songId);
        }

        public void SetSongLyricLanguagePreference(
            string songId,
            IReadOnlyCollection<string> selectedLanguageKeys,
            IReadOnlyCollection<string> defaultLanguageKeys)
        {
            if (_authContext == null || string.IsNullOrWhiteSpace(songId))
            {
                return;
            }

            _authContext.SetSongLyricLanguagePreference(songId, selectedLanguageKeys, defaultLanguageKeys);
        }

        /// <summary>
        /// 应用配置中保存的 Cookie 列表。
        /// </summary>
        /// <param name="cookies">Cookie 集合</param>
    }
}
