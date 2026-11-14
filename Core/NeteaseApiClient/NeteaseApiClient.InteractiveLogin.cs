using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Auth;

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        private LoginProtocolClient? _phoneLogin;
        private bool _phoneLoginCompleted;
        private string? _phoneLoginIdentity;
        private LoginProtocolClient? _weChatLogin;
        private WeChatLoginSession? _activeWeChatSession;
        private readonly SemaphoreSlim _phoneLoginGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _weChatLoginGate = new SemaphoreSlim(1, 1);

        internal async Task ReleaseInteractiveLoginSessionsAsync()
        {
            await _phoneLoginGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _phoneLogin?.Dispose(); _phoneLogin = null;
                _phoneLoginIdentity = null; _phoneLoginCompleted = false;
            }
            finally { _phoneLoginGate.Release(); }
            await _weChatLoginGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _activeWeChatSession = null;
                _weChatLogin?.Dispose(); _weChatLogin = null;
            }
            finally { _weChatLoginGate.Release(); }
        }

        public async Task<bool> SendCaptchaAsync(string phone, string ctcode = "86", CancellationToken cancellationToken = default)
        {
            LoginProtocolClient.ValidatePhone(phone, ctcode);
            await _phoneLoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsurePhoneLoginSession(phone, ctcode);
                var result = await _phoneLogin!.SendCaptchaAsync(phone, ctcode, cancellationToken).ConfigureAwait(false);
                if (result.Value<int?>("code") != 200) throw new InvalidOperationException(LoginFailure(result, "发送验证码失败"));
                return true;
            }
            finally { _phoneLoginGate.Release(); }
        }

        public async Task<LoginResult> LoginByCaptchaAsync(string phone, string captcha, string ctcode = "86", CancellationToken cancellationToken = default)
        {
            // Validate before creating any network request; keep exactly the session that sent the SMS.
            LoginProtocolClient.PhonePayload(phone, captcha, ctcode);
            await _phoneLoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsurePhoneLoginSession(phone, ctcode);
                var result = await _phoneLogin!.LoginAsync(phone, captcha, ctcode, cancellationToken).ConfigureAwait(false);
                if (result.Value<int?>("code") != 200) return new LoginResult
                { Code = result.Value<int?>("code") ?? -1, Message = LoginFailure(result, "手机号登录失败") };
                var login = await AcceptVerifiedLoginAsync(_phoneLogin, cancellationToken).ConfigureAwait(false);
                _phoneLoginCompleted = true;
                return login;
            }
            finally { _phoneLoginGate.Release(); }
        }

        private void EnsurePhoneLoginSession(string phone, string countryCode)
        {
            string identity = countryCode + ":" + phone;
            if (_phoneLoginCompleted || (_phoneLoginIdentity != null && _phoneLoginIdentity != identity))
            {
                _phoneLogin?.Dispose();
                _phoneLogin = null;
                _phoneLoginCompleted = false;
            }
            _phoneLogin ??= new LoginProtocolClient();
            _phoneLoginIdentity = identity;
        }

        public async Task<WeChatLoginSession> CreateWeChatLoginAsync(CancellationToken cancellationToken = default)
        {
            await _weChatLoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _activeWeChatSession = null;
                _weChatLogin?.Dispose();
                _weChatLogin = new LoginProtocolClient();
                _activeWeChatSession = await _weChatLogin.CreateWeChatAsync(cancellationToken).ConfigureAwait(false);
                return _activeWeChatSession;
            }
            finally { _weChatLoginGate.Release(); }
        }

        public async Task<byte[]> GetWeChatQrImageAsync(WeChatLoginSession session, CancellationToken cancellationToken = default)
        {
            await _weChatLoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await RequireWeChatSession(session).GetWeChatImageAsync(session, cancellationToken).ConfigureAwait(false); }
            finally { _weChatLoginGate.Release(); }
        }

        public async Task<WeChatLoginPollResult> PollWeChatLoginAsync(WeChatLoginSession session, CancellationToken cancellationToken = default)
        {
            await _weChatLoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var client = RequireWeChatSession(session);
                var result = await client.PollWeChatAsync(session, cancellationToken).ConfigureAwait(false);
                if (result.Status == WeChatLoginStatus.Authorized)
                {
                    try
                    {
                        await client.CompleteWeChatAsync(session, result.AuthorizationCode!, cancellationToken).ConfigureAwait(false);
                        result.Login = await AcceptVerifiedLoginAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                    catch (System.Net.Http.HttpRequestException)
                    { throw new InvalidOperationException("微信已确认，但授权回调或账号验证遇到网络错误。授权码只能使用一次，请刷新二维码后重试。"); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new InvalidOperationException("微信已确认，但授权回调或账号验证超时。请刷新二维码后重试。"); }
                }
                return result;
            }
            finally { _weChatLoginGate.Release(); }
        }

        private LoginProtocolClient RequireWeChatSession(WeChatLoginSession session)
        {
            if (session == null || !ReferenceEquals(session, _activeWeChatSession) || _weChatLogin == null)
                throw new InvalidOperationException("微信二维码已刷新，请扫描当前二维码。");
            return _weChatLogin;
        }

        private async Task<LoginResult> AcceptVerifiedLoginAsync(LoginProtocolClient client, CancellationToken token)
        {
            var accountData = await client.VerifyAccountAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var profile = accountData["profile"] as JObject;
            var account = accountData["account"] as JObject;
            // Commit only after both the credential and a real account have been verified.
            var result = new LoginResult
            {
                Code = 200, UserId = (profile?["userId"] ?? account?["id"])?.ToString() ?? "",
                Nickname = profile?.Value<string>("nickname") ?? "网易云用户",
                AvatarUrl = profile?.Value<string>("avatarUrl"), VipType = profile?.Value<int?>("vipType") ?? 0,
                Cookie = FinalizeLoginCookies(client.CookieSnapshot())
            };
            return result;
        }

        private static string LoginFailure(JObject result, string prefix)
        {
            int code = result.Value<int?>("code") ?? -1;
            string message = result.Value<string>("message") ?? result.Value<string>("msg") ?? "服务器拒绝了请求";
            if (code == -462 || code == 415 || code == 8821 || code == 10004)
                message += "。请使用网页登录查看官方要求的后续验证；本次请求的脱敏诊断已记录";
            return prefix + "：" + message + "（" + code + "）";
        }
    }
}
