using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YTPlayer.Core.ListenTogether
{
    public sealed class ListenTogetherInvitation
    {
        public string RoomId { get; }
        public string InviterId { get; }
        public string SongId { get; }

        public ListenTogetherInvitation(string roomId, string inviterId, string songId = "")
        {
            if (!Regex.IsMatch(roomId ?? "", @"^[A-Za-z0-9_-]{1,200}$") || !IsSongId(inviterId))
                throw new FormatException("一起听链接缺少有效的 roomId 或 inviterId。");
            RoomId = roomId!;
            InviterId = inviterId;
            SongId = IsSongId(songId) ? songId : "";
        }

        public static bool IsSongId(string? value) => !string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"^[1-9][0-9]{0,19}$");

        public static ListenTogetherInvitation Parse(string text)
        {
            foreach (Match match in Regex.Matches(text ?? "", "https?://[^\\s<>\"‘’“”]+", RegexOptions.IgnoreCase))
            {
                string candidate = match.Value.TrimEnd('。', '，', '！', '）', ')', ']', '】', '；');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                    !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                    !(uri.Host.Equals("st.music.163.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.Equals("music.163.com", StringComparison.OrdinalIgnoreCase))) continue;
                if (!uri.AbsolutePath.TrimEnd('/').Equals("/listen-together/share", StringComparison.OrdinalIgnoreCase)) continue;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string part in uri.Query.TrimStart('?').Split('&'))
                {
                    string[] pair = part.Split(new[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    string key = Uri.UnescapeDataString(pair[0]);
                    if (!values.TryAdd(key, Uri.UnescapeDataString(pair[1])))
                        throw new FormatException("一起听链接含有重复参数，请重新复制完整邀请链接。");
                }
                return new ListenTogetherInvitation(values.GetValueOrDefault("roomId", ""),
                    values.GetValueOrDefault("inviterId", ""), values.GetValueOrDefault("songId", ""));
            }
            throw new FormatException("请粘贴网易云一起听完整链接（https://st.music.163.com/listen-together/share/）。短链接请先在浏览器打开，再复制完整地址。");
        }

        public static async Task<ListenTogetherInvitation> ResolveAsync(string text, CancellationToken token,
            Func<Uri, CancellationToken, Task<Uri?>>? redirect = null)
        {
            token.ThrowIfCancellationRequested();
            try { return Parse(text); }
            catch (FormatException) { }
            foreach (Match match in Regex.Matches(text ?? "", "https?://[^\\s<>\"‘’“”]+", RegexOptions.IgnoreCase))
            {
                if (!Uri.TryCreate(match.Value.TrimEnd('。', '，', '！', '）', ')', ']', '】', '；'), UriKind.Absolute, out var current) ||
                    current.Host != "163cn.tv" || !AllowedRedirect(current)) continue;
                // Never attach the player's cookies to a share-link request or follow arbitrary redirects.
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                for (int hop = 0; hop < 5; hop++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!AllowedRedirect(current)) throw new FormatException("一起听短链接跳转到了非网易云地址。");
                    current = new UriBuilder(current) { Scheme = "https", Port = -1 }.Uri;
                    try { return Parse(current.AbsoluteUri); }
                    catch (FormatException) { }
                    Uri? next;
                    if (redirect != null) next = await redirect(current, token).ConfigureAwait(false);
                    else
                    {
                        using var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        next = (int)response.StatusCode >= 300 && (int)response.StatusCode < 400 ? response.Headers.Location : null;
                    }
                    if (next == null) break;
                    current = next.IsAbsoluteUri ? next : new Uri(current, next);
                }
                throw new FormatException("无法展开一起听短链接，请在浏览器打开后复制完整邀请地址。");
            }
            return Parse(text ?? "");
        }

        private static bool AllowedRedirect(Uri uri) => uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            (uri.Scheme == "http" || uri.Scheme == "https") &&
            (uri.Host == "163cn.tv" || uri.Host == "music.163.com" || uri.Host.EndsWith(".music.163.com", StringComparison.OrdinalIgnoreCase));

        public override string ToString() => "https://st.music.163.com/listen-together/share/?roomId=" +
            Uri.EscapeDataString(RoomId) + "&inviterId=" + Uri.EscapeDataString(InviterId) +
            (SongId.Length == 0 ? "" : "&songId=" + Uri.EscapeDataString(SongId));
    }
}
