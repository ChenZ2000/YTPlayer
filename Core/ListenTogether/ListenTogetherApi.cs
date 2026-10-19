using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YTPlayer.Core.ListenTogether
{
    public sealed class ListenTogetherApiException : Exception
    {
        public ListenTogetherApiException(int code, string message, string? operation = null) : base($"{message}（错误码 {code}）") { Code = code; Operation = operation; }
        public int Code { get; }
        public string? Operation { get; }
    }

    /// <summary>Official room endpoints. Never retry room mutations implicitly.</summary>
    public sealed class ListenTogetherApi
    {
        private readonly Func<string, JObject, CancellationToken, Task<JObject>> _send;

        public ListenTogetherApi(Func<string, JObject, CancellationToken, Task<JObject>> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public async Task<JObject> RequestAsync(string operation, JObject payload, CancellationToken cancellationToken)
        {
            JObject result = await _send("/api/listen/together/" + operation, payload, cancellationToken).ConfigureAwait(false);
            int code = result.Value<int?>("code") ?? 0;
            if (code != 200)
            {
                string message = result.Value<string>("message") ?? result.Value<string>("msg") ?? "网易云一起听请求失败";
                if (code == 301) message = "登录会话已失效，请重新登录网易云音乐";
                throw new ListenTogetherApiException(code, message, operation);
            }

            var data = result["data"] as JObject
                ?? throw new ListenTogetherApiException(-1, "网易云一起听响应缺少 data 对象", operation);
            if (data.Value<bool?>("success") == false || data.Value<bool?>("result") == false)
                throw new ListenTogetherApiException(code, data.Value<string>("message") ?? "网易云未接受本次一起听操作", operation);
            return data;
        }

        public Task<JObject> CreateAsync(CancellationToken token) => RequestAsync("room/create", new JObject { ["refer"] = "songplay_more" }, token);
        public async Task<JObject> StatusAsync(CancellationToken token)
        {
            var data = await RequestAsync("status/get", new JObject(), token).ConfigureAwait(false);
            if (data["inRoom"]?.Type != JTokenType.Boolean)
                throw new ListenTogetherApiException(-1, "网易云未返回有效的房间状态");
            return data;
        }
        public Task<JObject> CheckAsync(string roomId, CancellationToken token) => RequestAsync("room/check", new JObject { ["roomId"] = roomId }, token);
        public Task<JObject> JoinAsync(string roomId, string inviterId, CancellationToken token) => RequestAsync("play/invitation/accept", new JObject { ["refer"] = "inbox_invite", ["roomId"] = roomId, ["inviterId"] = inviterId }, token);
        public async Task<JObject> EndAsync(string roomId, CancellationToken token)
        {
            var data = await RequestAsync("end/v2", new JObject { ["roomId"] = roomId }, token).ConfigureAwait(false);
            if (data.Value<bool?>("success") != true)
                throw new ListenTogetherApiException(-1, "网易云未确认退出房间");
            return data;
        }
        public Task<JObject> PlaylistAsync(string roomId, CancellationToken token) => RequestAsync("sync/playlist/get", new JObject { ["roomId"] = roomId }, token);
        public Task<JObject> HeartbeatAsync(string roomId, string songId, bool playing, long progress, CancellationToken token) => RequestAsync("heartbeat", new JObject { ["roomId"] = roomId, ["songId"] = songId, ["playStatus"] = playing ? "PLAY" : "PAUSE", ["progress"] = progress }, token);
        public Task<JObject> CommandAsync(string roomId, JObject command, CancellationToken token) => RequestAsync("play/command/report", new JObject { ["roomId"] = roomId, ["commandInfo"] = command.ToString(Newtonsoft.Json.Formatting.None) }, token);
        public Task<JObject> ReportPlaylistAsync(string roomId, JObject playlist, CancellationToken token) => RequestAsync("sync/list/command/report", new JObject { ["roomId"] = roomId, ["playlistParam"] = playlist.ToString(Newtonsoft.Json.Formatting.None) }, token);
    }
}
