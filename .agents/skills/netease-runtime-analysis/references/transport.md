# EAPI/WEAPI 传输取证

## 记录的最小契约

每个动作记录：HTTP host/path/method、加密签名路径、请求类型、字段类型与单位、必要 headers、响应 HTTP 状态/字节数/编码、解密后的业务码及数据结构。响应为空、解密失败、业务拒绝和合法空结果必须分开。

## 已知运行时网关

8.6.76 中可观察 `com.netease.cloudmusic.utils.NeteaseMusicUtils` 的 `serialdata`、`serialdatanew`、`serialurl`、`deserialdata`、`deserialdata2`、`deserialdatanew`、`deserialdata2new`。不要假定所有版本都存在这些重载。

- 加密前：检查字符串参数中的路径和 JSON；把 URL 与同一次方法调用中的 body 关联。
- 解密后：部分方法原地修改 `byte[]`，返回有效长度。返回后仅取 `[0, length)`；不要把未写入的尾部解析成 JSON，也不要将返回的整数当作明文。
- 未观察到网关不代表没有协议；遵守两次失败切换低层的规则。native 库名和地址必须现场核验，不对猜测的 `libwyy.so`/`libbox.so` 宣称已定位。
- 主进程与 `:play` 分别采集。输出时间/PID，必要时按线程或请求标识关联；不能把并发相邻事件必然配成一对。

## 会话一致性

一起听实测需要保存会话的完整 cookie map，EAPI 内层 `header` 与 HTTP Cookie 必须来自同一次快照。只带 `MUSIC_U`/csrf、请求模板覆盖 `os/appver`、对已转义 cookie 二次编码，均可能导致路由/会话不一致。不要输出值；只比较字段名、哈希或 `sessionMatches`。

生产入口是 `Core/NeteaseApiClient/NeteaseApiClient.Transport.cs`。一起听/识曲使用 `useIosHeaders:false`，由模块构建自己的 header，避免通用 iOS 模板覆盖已有会话。主机覆盖必须仅影响目标模块，不能全局改变登录/其他 API 的网络行为。

## 返回解密与压缩

EAPI 的签名路径一般为 `/api/...`，wire 路径为 `/eapi/...`；按实际捕获确认。`e_r=true` 请求加密响应，但仍需容忍 JSON 错误响应和不同压缩层。

当前 transport 按观测处理明文 JSON、外层压缩、EAPI 二进制/十六进制密文及解密后的压缩内容。先检查 HTTP 编码与魔数，再通过已有 `EncryptionHelper` 解密字节；在得到 JSON 前不要先把任意密文当 UTF-8 文本。使用已验证算法实现，不靠静态反编译重做密码数学。

回归覆盖 gzip 明文、加密 JSON、加密内层 gzip、外层 gzip 包裹密文、空响应和业务失败。修改解密逻辑时重跑一起听和识曲测试，避免只修好一个端点。

## 重试

读操作可按上层节奏有界重试；建房、加入、命令、上报等操作先确认幂等性。`PostEApiAsync` 中 `retryCount: MAX_RETRY_COUNT` 用于关闭其通用隐式重试，不能望文生义解释为额外重试四次。HTTP 请求、读取和业务状态机都要有取消/超时，并区分调用方取消与网络超时。
