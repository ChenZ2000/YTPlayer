using System;

namespace YTPlayer.Core.Exceptions
{
    /// <summary>
    /// 表示网易云服务器基于地区/版权限制拒绝请求的异常。
    /// 捕获后可触发 REAL IP 回退策略并重试。
    /// </summary>
    public class RegionRestrictedException : InvalidOperationException
    {
        public RegionRestrictedException(string message)
            : base(message)
        {
        }
    }
}
