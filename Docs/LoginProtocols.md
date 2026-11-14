# 手机号与微信登录验证记录

验证日期：2026-11-14。本轮已由账号本人提供有效短信验证码并完成微信扫码确认，两条链路均通过真实凭据与账号验证。以下区分当前结论、历史实验及尚未覆盖的边界。

## 实现与依据

- **外部源码参考**：获用户授权，将 `D:\GitHub\api-enhanced` 从 `2db6684` 快进更新至 `4a7d557`。没有在该参考目录编写补丁或安装依赖。请求契约依据 `module/login_cellphone.js`、`module/captcha_sent.js`、`util/crypto.js`。
- **当前官方网页源码参考**：短信登录使用 `/api/login/cellphone`，密码登录才使用 `/api/w/login/cellphone`。此前只参考通用手机号 API 而混用了这两条路线。当前官网还在短信兑换和微信授权前调用 Watchman 登录业务 `0b0cdd23ed1144a0b78de049edc09824` 生成 `checkToken`；参考后端的通用业务标识不能代替登录业务。官网微信入口仍为 `/api/sns/authorize?clientType=web2&snsType=10&callbackType=Login&forcelogin=true`，另附本次登录令牌。
- **运行时环境**：Windows 11，Android 11 / API 30 / x86_64，Frida 客户端和服务端 17.23.3。工作区已有官方 APK 为网易云音乐 8.6.76，versionCode 8006076。旧版 APK 的结果不能等同于所有当前安卓版行为。
- APK 本地校验记录：`NetA/phase1/apk-verification.json`；SHA-256 为 `60121058aab6730a05e15c65fb924548f8d4be7f607b132285748e9c723b54f3`，原始来源为网易官方 CDN，已有 v2 签名校验记录。
- 模拟器因迁移保留了旧目录引用。本次只在本工作区内修复 AVD 配置及 system/vendor qcow2 backing 路径，没有清除模拟器数据或登出原账号。

## 生产链路

手机号发码：先初始化隔离的官方浏览器环境并同步 Cookie 与实际 User-Agent；WEAPI POST `/weapi/sms/captcha/sent`，JSON 明文包含 `cellphone`、`ctcode` 及会话 CSRF，与当前官网一致。请求体为 URL 编码的 `params`、`encSecKey`。

短信登录：正常界面保持发码时的独立 Cookie 会话，重新向官方安全组件申请登录 `checkToken`，WEAPI POST `/weapi/login/cellphone`，发送 `phone`、`countrycode`、`captcha`、`rememberLogin=true`（布尔值）、`checkToken` 与 CSRF。验证码保持字符串。令牌缺失时停止请求；不使用随机字符串或通用业务令牌充数。

微信登录：用户点击“加载微信登录二维码”（进入选项卡不自动加载）→ 获取新的官方登录 `checkToken` → 官方授权入口（令牌作为 URL 编码查询参数）→ 微信授权页 → `/connect/qrcode/{uuid}` 图片 → 官方页面选择的 `lp.open.weixin.qq.com` 或 `long.open.weixin.qq.com` 轮询 → 405 授权码 → 网易云官方 `/back/weichat` 回调 → Cookie 和账号验证。**回调 state 从微信页面最终渲染的 JavaScript 读取**；观察到它与初始重定向 URL 中的 state 不同。轮询 JavaScript 只解析白名单字段，不执行脚本。402=过期、403=取消、404=待确认、405=已授权、408=等待扫码。

官方安全组件运行在 `https://music.163.com/` 的真实 WebView2 页面中，使用官网初始化的 `window.WM`。每条登录链路使用独立的 InPrivate 配置，保留默认浏览器环境，不合成设备指纹。每次短信兑换和每次微信二维码创建都重新取令牌；SDK 环境与 HTTP 请求同步 Cookie、实际 User-Agent。登录窗口关闭后释放浏览器，进程退出后删除其临时目录。网页登录的 OAuth 弹窗与父窗口共享相同隐私配置及 opener 关系。

两条链路均先验证新会话的 `MUSIC_U`，再用同一个新会话请求 `/weapi/w/nuser/account/get` 验证真实账号，成功后才提交到播放器。失败、超时和取消不提交新登录态。短信发送及一次性 OAuth 授权码兑换没有隐式重试；二维码刷新后旧会话不能提交。选项卡切换和窗口关闭会取消相关任务。

## 加密修复与离线验证

发现 `BigInteger.ToString("x")` 会给部分 RSA 密文附加正数符号位，产生 257 位输出。现按无符号大端字节序输出并补齐到固定 256 位。AES-CBC 双层加密、RSA 反转密钥、EAPI 签名路径和 AES-ECB 请求向量均与参考 JS 实现对照；另有中文、嵌套 JSON 和 64 个随机 WEAPI 密钥长度回归。

`Tests/Auth` 覆盖请求契约、微信状态机、页面格式变化、缺失授权码、无凭据或匿名账号冒充成功、HTTP 错误与业务成功矛盾、空响应、HTML 响应、取消后不得提交、失败保留原会话、一次性兑换、选项卡顺序、Windows 无障碍文本角色和键盘字段顺序。界面按三种字体缩放生成截图并检查输入框和按钮边界。

通用入口：

```powershell
pwsh -File scripts/Test-ProtocolRegression.ps1
```

该入口运行 Debug 构建及 Auth、ListenTogether、Recognition、Playback 离线回归，不发短信、不扫码、不提交真实登录。

参考 JS 向量可重新生成，命令只读参考仓库，结果写入当前仓库：

```powershell
node Tests/Auth/upstream_vectors.cjs D:/GitHub/api-enhanced > Tests/Auth/crypto-vector.json
```

## 初次实现的实验记录（历史）

| 实验 | 已观察的结果 | 能证明与不能证明的范围 |
| --- | --- | --- |
| 安卓官方运行时加密 | 通过 `NeteaseMusicUtils.serialdata` 对合成请求加密，与参考 API 的 EAPI 密文完全相同；生产 .NET 使用同一向量通过对照 | 证明此路径与请求的加密结果一致；合成向量事件不是官方 UI 自行产生的登录请求 |
| 官方安卓版发码操作 | 在已安装官方客户端输入授权测试手机号，界面进入短信输入及倒计时；原地解密边界观察到业务 `code=200` | 证明官方发码操作被接受；未读取用户手机短信，没有实际兑换验证码。仅 code/schema 日志不足以把每条并发响应与完整 URL 一一绑定 |
| 易听真实短信发码 | 生产 `SendCaptchaAsync` 返回业务成功 `200` | 证明服务端接受该请求；不能证明手机实际收到短信 |
| 易听真实短信登录失败处理 | 单次使用明确的测试验证码，服务端返回业务 `503`，播放器会话保持不变 | 证明新登录端点能解析业务拒绝并保护原会话；不能证明正确验证码一定兑换成功 |
| 易听微信 API | 两次独立会话均取得可解码的 470×470 图片，轮询均返回 `408` | 证明生成、图片和未扫码轮询链路可用；未完成微信本人扫码确认 |
| 易听微信 UI | 实际窗体自动加载二维码，切换到手机号页后取消轮询；截图已检查 | 证明界面加载和取消；不证明确认后的真实凭据兑换 |

真实实验均使用隔离的 `YTPLAYER_ROOT`，没有覆盖用户主应用的账号文件。号码、验证码、Cookie、OAuth 授权码不写入跟踪文件。原始页面、本地截图和脱敏实验日志位于忽略目录 `artifacts/login/`；截图中的二维码属于临时会话。

APK 动态观察工具：`Tests/Auth/android_login_capture.py`，可传 `--java-bridge NetA/phase2/node_modules/frida-java-bridge/index.js`。工具只观察运行时、输出字段类型和业务码，不会自行点击、发码或修改设备代理。结束时自动 detach。

初次交付的 Debug/x64 解决方案构建：0 个错误、0 个警告。当时 Auth 为 125 项断言；完整协议回归中 ListenTogether 119 项、Recognition 54 项、Playback 29 项、跳转 46 项、MP3 恢复 44 项、FLAC 恢复 44 项通过。日志分别见 `artifacts/login/auth-final-tests.log`、`artifacts/login/final-debug-build.log`、`artifacts/login/final-regression.log`。该轮回归未覆盖手机号按钮的初始状态，后续发现与修复见下节。

收尾时已卸载挂钩、停止本次启动的 Frida 服务端、移除本次 ADB 转发、恢复模拟器原代理值，并关闭本次启动的无窗口模拟器。参考 API 工作区仍无未提交修改。

## 手机号界面溢出修复

用户反馈首次点击发送验证码就触发 `Arithmetic operation resulted in an overflow`。本地两份用户日志均将异常定位在 `UpdateSmsCountdown → SetPhoneBusy → SendPhoneCaptchaAsync`。初始发送截止时间为 `DateTimeOffset.MinValue`，减当前时间后得到约负 639 亿秒；原代码先把它转换成 `int` 再执行 `Math.Max(0, ...)`，所以在 Debug 的溢出检查下先崩溃。发码前的状态更新位于原 `try` 外，错误无法按普通登录失败处理。直接点击登录、无效输入恢复后再次刷新倒计时也会触发同一问题。

修复以可空的单调时钟起点表示尚未发码，倒计时和按钮拦截均按 `Stopwatch` 的经过时间计算；任何转换都先把剩余秒数限制在 0–60。输入验证通过后才预留冷却期，界面忙碌状态更新进入 `try/finally`。网络或业务拒绝保留已发出请求的冷却期，输入错误不创建冷却期。评论无障碍恢复的两处毫秒转换、播放请求节流的毫秒转换也改为先限制范围后转换，避免系统时钟回拨或远期时间造成同类溢出。

新增 `Tests/Auth/PhoneInteractionTests.cs`，通过实际按钮 `PerformClick`、WinForms 消息上下文及模拟 HTTP 验证首次发码、错误号码后重试、冷却期重复点击、到期重新发码、初始状态直接登录、业务拒绝、网络错误及关闭窗口。修复前实际点击测试复现了相同溢出堆栈，记录于 `artifacts/login-overflow/repro-tests.log`。修复后 Auth 共 175 项断言通过，另覆盖 `TimeSpan.MinValue/MaxValue`、秒与毫秒边界，以及评论计时器实际 Tick 回调。此轮按钮测试没有发送真实短信。

本轮日志位于 `artifacts/login-overflow/`，完整 Debug 构建及协议回归入口仍为 `scripts/Test-ProtocolRegression.ps1`。

## 风控链路修复与真实闭环

此前的 `10004` 不能仅凭错误文字判断为账号被封禁。本轮确认了客户端缺少登录安全令牌、短信端点和字段契约混用的问题。微信客户端还忽略了回调响应体，把未下发凭据的所有原因统一解释为安全验证。现在回调先检查业务错误，记录响应类型及 Cookie 是否存在，再独立验证账号；网络中断不会重复兑换已使用的 OAuth 授权码。

当前官网的两个源码依据（2026-11-14 获取）：[核心脚本](https://s3.music.126.net/web/s/core_d6d4797f1d4a94aa615ceca52518c8a3.js)、[登录界面脚本](https://s3.music.126.net/web/s/pt_frame_index_f8fa68369f5c6b260f4bdd85eeb5f954.js)。本地副本在忽略目录 `artifacts/login-risk/current-web0.js`、`current-web1.js`。核心脚本区分 `captcha-login` 和 `mobile-login`，登录界面分别调用两条路由，并为手机号及第三方授权生成相同登录业务的安全令牌。

| 验证 | 当前已观察的结果 | 证据 |
| --- | --- | --- |
| 官方安全组件 | 连续获取两个不同的真实登录令牌；新浏览器会话没有 MUSIC_U；初始化取消后可继续初始化和取令牌 | `artifacts/login-risk/security-probe.log` |
| 更新后的短信发送 | 仅发起一次真实发码，HTTP 200、业务 200；本人随后确认收到短信并提供验证码 | `artifacts/login-risk/sms-live.jsonl` |
| 真实短信兑换 | 使用本人提供的验证码，`/weapi/login/cellphone` 返回 200，新增 MUSIC_U 和 CSRF；`/weapi/w/nuser/account/get` 返回 200，验证真实账号并提交至隔离测试客户端 | `artifacts/login-risk/sms-redeem.jsonl` |
| 微信授权准备 | 两次独立会话均取得 470×470 图片，等待扫码轮询为 408 | `artifacts/login-risk/real-wechat.log` |
| 真实微信完整界面 | 本人扫描易听诊断窗口的二维码并确认，轮询 404 → 405；`/back/sns` 返回 HTML 并下发 MUSIC_U 与 CSRF；账号接口返回 200；登录窗口触发成功事件并正常关闭；本人明确确认此次扫码操作 | `artifacts/login-risk/wechat-interactive-20261114-195251/protocol.jsonl` |

本轮短信发码和兑换分别由两个隔离测试进程执行，两者均成功；应用正常界面会保留同一手机号会话。本轮微信则完整走过实际生产登录窗体、扫码、回调、账号验证、成功事件和关闭流程。两条真实链路均使用当前生产实现，测试没有修改原始服务端响应，没有沿用播放器已有账号凭据。所有个人标识、验证码及 Cookie 值均不记录在本文件。

网页登录还修复了首次进入 `about:blank` 被误认为已打开页面、从而跳过导航的问题。每个窗口使用独立隐私配置，官方授权弹窗继承相同配置并保留 opener。真实 WebView 测试已验证共享 Cookie、共享浏览器进程、opener 存在及弹窗关闭，共 4 项断言（`artifacts/login-risk/web-popup.log`）。

新的 Auth 回归目前共 200 项断言，覆盖缺失令牌不得发请求、每次兑换重新取令牌、真实浏览器 User-Agent、微信查询参数编码、回调业务拒绝、成功码但缺失凭据、一次性授权码、异常轮询整数及嵌套日志脱敏。最终 Debug/x64 解决方案构建为 0 个错误、0 个警告。Auth 200、ListenTogether 119、Recognition 54、Playback 29、跳转 46、MP3 恢复 44、FLAC 恢复 44，共 536 项离线断言通过。日志见 `artifacts/verification/` 与 `artifacts/login-risk/final-regression.log`。诊断脚本的自动启动、关闭及生成压缩包也已通过验证，压缩包仅包含 `summary.txt` 与 `protocol.jsonl`。

## 需要再次排查时的采集工具

运行：

```powershell
pwsh -File scripts/Capture-LoginDiagnostics.ps1
```

工具先执行 Debug 编译，再依次打开手机号、微信诊断窗口。按平时操作填写手机号、获取及填写验证码；微信页先点击“加载微信登录二维码”，听到“正在加载微信登录二维码”后等待显示，再扫码并确认。遇到失败可切换到同一窗口的“网页登录”对照官方流程；关闭当前窗口进入下一项。工具使用独立测试账号目录，不替换日常播放器的已登录账号。

可用 `-Mode Phone`、`-Mode WeChat` 或 `-Mode Official` 单独测试。`-SmokeTest` 自动打开并关闭手机号窗口，只验证工具启动、关闭和诊断包生成，不发送短信或登录。

退出后生成 `artifacts/login-capture/<时间>/login-diagnostics.zip`。包内只包括 `protocol.jsonl` 和运行摘要；记录请求字段类型、HTTP 状态、业务码、响应类型、登录凭据是否存在与成功事件。过滤器采用白名单，不记录响应正文、任意查询值、手机号、短信码、用户标识、Cookie、令牌、微信授权码或截图。普通应用运行时也会写入应用目录下的 `logs/login-protocol.jsonl`，便于复现后直接排查。

## 剩余验证边界

本轮已完成上述账号的短信和微信真实登录。单次成功不能保证所有账号、所有网络及后续服务端策略下都不会触发安全验证。若服务端明确要求本人确认或其他官方验证，保留真实业务提示并提供官方网页登录入口；不将 HTTP 200、微信 405 或单独出现 Cookie 当作登录完成。

本轮尚未由本人使用 NVDA 完整检查每个交互，也未在日常播放器目录完成登录后重启保留状态的验收。键盘字段顺序、Windows 无障碍角色与状态通知、字体缩放和按钮交互由现有自动回归覆盖；日常账号保存沿用现有登录成功事件链。

## 微信二维码改为主动加载

微信选项卡初始只显示“加载微信登录二维码”按钮及操作说明，不启动官网安全组件、不创建二维码或轮询。按钮位于页面首个键盘焦点位置。点击后先通过 UI Automation 发出高优先级“正在加载微信登录二维码”通知，再开始异步加载；加载期间禁用按钮并拦截重复调用。二维码就绪后显示图片和扫码提示，按钮变为“刷新微信登录二维码”。

离开选项卡会取消当前加载或轮询并清除旧图，返回后恢复加载按钮，由用户再次主动启动。取消的旧任务不会覆盖新的按钮状态。登录交互回归通过实际按钮点击和被阻塞的 API 调用验证初始不请求、加载提示、连续点击、离开时取消以及返回后再次可用；使用模拟等待，不发送真实授权请求。

本轮微信手动加载调整：Debug/x64 解决方案与 Auth 测试项目均为 0 个错误、0 个警告，登录回归共 211 项断言通过。日志位于 `artifacts/wechat-manual-load/`。
