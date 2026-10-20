# 真实听歌上报与未缓存跳转

协议代码在 `PlaybackReportingService.cs`、`NeteaseApiClient.Recommendations.cs`、`BassAudioEngine.cs`、`SeekManager.cs`。实验报告及原始输出保存在被忽略的 `artifacts/` 目录，不作为产品文档提交。

## 听歌计数

本实现实测有效的桌面组合为：`https://clientlog.music.163.com/eapi/feedback/weblog`，签名路径 `/api/feedback/weblog`，完整账号 cookie，并仅对本请求设置 `os=osx`、`appver=3.1.10.5100`、`osver=15.5`、`channel=netease`。外层 `logs` 为 JSON 字符串。开始事件 `startplay`；结束事件 `play` 带真实累计秒数、结束原因及 `mainsite/mainsiteWeb`。

Android 运行时观察到 `clientlog3.music.163.com/api/clientlog/encrypt/upload`/`upload`，但本次未完整解开其上传日志体。不要把桌面方案描述为 Android 日志解密成果。

计时必须基于实际播放推进：排除暂停、跳转、不连续位置和卡顿；不能用绝对进度、全曲时长或测试虚构时长。结束事件只入队一次。持久化重试记录只绑定会话摘要，不能包含凭证；换账号不重放旧账号记录。正常退出有界异步 drain，不能阻塞 UI 无限等待。

真实验证先读取个人页/等级计数，实际完整播放少量有权限且未用于本轮验证的歌曲，再观察记录、业务码、即时等级计数与稍后个人页总数。后台其他客户端会污染对照，需要记录并隔离。`code=200` 不等于计数立即增长，最近播放更新也不证明统计入账。

## 未缓存位置访问的证明标准

Android 当前样本取 URL 使用 `/api/song/enhance/player/url/v1`，媒体层使用 `Range: bytes=...`。没有证实任意歌曲/格式的秒数请求接口。字节 Range 只取编码文件片段，定位到时间依赖解码器索引。

1. 新建空缓存/独立按需 reader，记录初始化取数。
2. seek 到多个未下载位置并解码 PCM，记录每次 Range 和累计下载字节。
3. 必须确认没有初始化就预取整首；全曲已预热的实验不能证明冷跳转。
4. 完成以上步骤后再取完整文件，比较同位置解码 PCM，记录样本歌曲/格式、权限、耗时与边界。
5. CDN 需返回精确 `206`、匹配的 `Content-Range` 与长度；不能将忽略 Range 的 `200` 整文件写到某一个块。

本次独立 MP3 实验覆盖 24.528、67.452、104.244 秒，下载 128,190/1,963,198 字节即完成解码，PCM 与后取完整文件一致。该结果不保证所有格式/歌曲/网络具有同样行为；`RangeProbe` 用于重复验证。

## 生产实现的不同坐标系

- `BASS_ChannelSeconds2Bytes` 是解码后的 PCM 位置；不可交给压缩文件缓存器当偏移。
- 有界编码文件偏移估计仅用于预取；最终定位由解码器 FILESEEK/READ 完成。
- 缓存优先级跟踪文件读位置；预取尾部用编码文件总长度。
- 原生 seek 可能触发同步读取，放后台并串行处理，最后拖动目标优先。全流程有取消/截止时间；停止/换曲不能由旧工作者复活旧流。暂停下监测任务也必须能退出。

先用本仓库的离线 runner 运行真实 BASS + 生产缓存器 + 限速模拟服务器测试（46 断言），再决定是否需要真实 CDN 复验。覆盖取消、超时、暂停、连续拖动和停止，不为未经证实的新端点重写缓存。

真实探针为 `Playback.Tests.exe --counts <account>`、`--audio <account> <song-id> <output-dir>`、`--range <metadata> <result>`、`--listen <account> <metadata> <max-seconds>`。`--listen` 实际播放并提交真实记录，有账号副作用；目录必须处于忽略区域，运行前确认当前任务确实授权该实验。
