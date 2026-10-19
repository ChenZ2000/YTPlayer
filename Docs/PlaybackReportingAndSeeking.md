# 播放上报与未缓存位置跳转：修复及验证记录

验证日期：2026-10-19。运行环境：Windows 11、.NET 10 x64、BASS，以及现有 Android 11 模拟器中的网易云音乐 8.6.76。APK 分析仅使用运行时挂钩，没有反编译 DEX。

## 上报问题与修复

旧链路优先向普通音乐 API 发送 WEAPI weblog，并以业务 `code=200` 判断成功；备用 EAPI 仍发到 `interface3.music.163.com`。这只能证明接口响应了，不能证明计数服务采纳了记录。

参考项目对相同现象的复现和实现已改用 `clientlog.music.163.com`、EAPI、桌面 `os=osx` weblog 方言，并分别发送开始、结束事件。本项目将这个组合用于播放上报，保留已有会话凭证，仅对本次请求指定报告方言，不修改全局登录状态。

- 参考：[api-enhanced scrobble 实现](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/main/module/scrobble.js)
- 问题记录：[api-enhanced #167](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/issues/167)

Android 运行时另观察到 `clientlog3.music.163.com/api/clientlog/encrypt/upload` 和 `/api/clientlog/upload`。这是 Android 客户端的独立日志上传路径；本次没有把它的加密上传体完整解析成桌面 weblog，也不声称两者是同一个协议。桌面请求组合是否有效，由下述真实账号播放实验独立验证。

修复后的结束事件结构（示意，数值均为占位值）：

```json
{
  "action": "play",
  "json": {
    "type": "song",
    "wifi": 0,
    "download": 0,
    "id": "<song-id>",
    "time": 122,
    "end": "playend",
    "source": "album",
    "sourceId": "<album-id>",
    "mainsite": "1",
    "mainsiteWeb": "1",
    "content": "id=<album-id>"
  }
}
```

外层 `logs` 是 JSON 字符串，EAPI 签名路径为 `/api/feedback/weblog`，HTTP 路径为 `/eapi/feedback/weblog`。`end` 按真实的完成、切歌、停止原因取值，`time` 是累计实际播放推进秒数，向下取整。

同时修复了以下本地问题：

1. 开始时多发一条没有时长的 `play`，结束时又重复发送两份 `play`。
2. 用进度条绝对位置或暂停期间墙钟时间当作听歌时长；读取到零时甚至回退为整首歌时长。
3. 未经验证地只上报达到半首或 240 秒的记录。现保留真实正数时长，由服务端判断是否有效，不人为制造播放时长。
4. 退出时取消队列，丢失刚加入的结束记录。正常关闭现在异步等待最多约 3 秒；网络未完成时在退出前保存待重试记录。
5. 重试记录没有账号归属。现在以会话摘要绑定，不保存凭证，切换账号后不把旧收听记录提交给新账号。

服务端响应成功仍不等同于立即增加总数。重复收听、短时播放和统计延迟的所有服务端规则并没有公开稳定合同，本项目不尝试伪造或绕过它们。

### 实测

| 场景 | 实际播放 | 观察 |
|---|---:|---|
| 账号 1，完整可播放歌曲 1395135968 | 122.084 秒 | 等级计数 424 → 425，个人页累计听歌数随后 3424 → 3425 |
| 账号 2，完整可播放歌曲 5057027 | 213.078 秒 | 结束后等级计数 174 → 175；后续个人页 374 → 376，其中包含同时进行的 Android 对照播放，不能把两首增量都归因于 .NET 测试 |

账号 1 的个人页值比等级接口晚更新，因此验证分成了即时响应和稍后读取两个步骤。未用重放伪造长时收听，没有批量刷计数。

本地证据：`artifacts/playback-protocol/live-listen-1.log`、`live-listen-2.log`、`counts-before.log`、`counts-after-1.log`、`counts-after-2.log`。这些产物不进入源码提交。

## 跳转协议证据

运行时在 Android 播放进程 `com.netease.cloudmusic:play` 中观察到：

- 取歌曲 URL：`/api/song/enhance/player/url/v1`；样本参数包含 `ids` JSON 字符串、`level=higher`、`encodeType=aac`、`trialMode=1`。
- M4A 媒体取数使用 `Range: bytes=...`，响应为 `206 Partial Content` 和匹配的 `Content-Range`。样本包括 `bytes=400007-4311977` 和 `bytes=131072-400016`。
- 没有观察到通用的“传入秒数即可从该秒开始返回歌曲”的歌曲 API。不能据此宣称不存在任何其他播放协议，也不能把字节 Range 称为秒数 API。

独立验证使用账号有权访问的完整 MP3，BASS 经自定义只按需读取的 HTTP 文件接口定位。每次重新创建空缓存；先完成目标片段解码，再下载完整文件作为对照，避免让完整文件预热影响结果。

| 目标秒数 | 初始化取数 | 跳转后总取数 | 文件大小 | 跳转并解码耗时 | 与完整文件同位置 PCM |
|---:|---:|---:|---:|---:|---|
| 24.528 | 62,654 B | 128,190 B | 1,963,198 B | 24.7 ms | 逐字节一致 |
| 67.452 | 62,654 B | 128,190 B | 1,963,198 B | 37.2 ms | 逐字节一致 |
| 104.244 | 62,654 B | 128,190 B | 1,963,198 B | 23.7 ms | 逐字节一致 |

对应 `range-proof.json`。最初使用生产缓存器的 `range-before.json` 实验在初始化时已预取整首，**不能作为未缓存跳转证据**，因此单独保留并换用隔离的按需读取实验。上述结果只覆盖这首完整 MP3 和这些目标，不保证任何歌曲、格式、权限或网络都能在同样时间内完成。

## 播放器修复边界

没有替换缓存架构，没有新增未经证实的秒数请求接口，也没有改变歌曲访问权限、整曲下载策略或编解码器选择。

`BASS_ChannelSeconds2Bytes` 返回解码后样本字节位置；原来把它交给压缩文件的缓存分块器，目标预取很容易越过真正的音频文件末尾。现区分：

- BASS 定位仍使用 PCM 位置。
- 预取使用受边界限制的文件偏移估计；这个估计不负责准确定位。
- 解码器自身的 FILESEEK/READ 回调负责索引和实际文件位置。正常播放的缓存优先级使用文件读取位置，尾块预取使用编码文件长度。

此外，整个目标块下载和等待纳入同一取消/超时范围；可能需要文件读取的原生定位移到后台线程；连续拖动时串行执行原生定位，只保留最后一个目标；前向跳转的监测任务在结束时主动取消，避免暂停时一直等待；结束拖动不再丢弃尚未消费的最后一个目标。

Range 下载现在验证响应状态、起止偏移、总长度及内容长度。返回整文件的 `200`、错误的 `Content-Range` 或截断内容不会写入错误块。探测过程尊重取消，`Accept-Ranges: none` 不再被误判为支持 Range。

参考 BASS 官方文档：[时间与字节](https://www.un4seen.com/doc/bass/BASS_ChannelSeconds2Bytes.html)、[文件偏移](https://www.un4seen.com/doc/bass/BASS_StreamGetFilePosition.html)、[自定义文件读取](https://www.un4seen.com/doc/bass/BASS_StreamCreateFileUser.html)。

## 自动验证与复现

- 播放上报、持久化、账号切换、Range 校验：29 项断言。
- 实际 BASS + 生产缓存器 + 限速模拟服务器：46 项断言。跳到尚未缓存的 180 秒约 78–80 ms；该服务器人为设置每个 Range 延迟 75 ms，此数字不是公网性能承诺。执行完时缓存覆盖约 33.1%。包含取消、超时、暂停、最后目标优先、30 次往返跳转和停止期间取消。
- 一起听回归：119 项断言。
- 听歌识曲回归：54 项断言。以上合计 248 项断言全部通过；最终 Debug x64 构建为零警告、零错误。

```powershell
Set-Location D:\GitHub\YTPlayer
$build = 'E:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
& $build YTPlayer.sln /restore /t:Build /p:Configuration=Debug /p:Platform=x64 /m
& $build Tests/Playback/Playback.Tests.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64 /m
& Tests/Playback/bin/x64/Debug/net10.0-windows/Playback.Tests.exe

# 自行生成不含版权音乐的离线 seek 测试素材。
ffmpeg -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=360' -ac 2 -c:a libmp3lame -b:a 192k -y artifacts/playback-protocol/seek-fixture.mp3
& Tests/Playback/bin/x64/Debug/net10.0-windows/Playback.Tests.exe --seek-tests artifacts/playback-protocol/seek-fixture.mp3
```

完整离线回归也可运行 `pwsh -File scripts/Test-ProtocolRegression.ps1`。它自动寻找 VS MSBuild（可通过 `-MSBuild` 指定），生成合成音频进行 seek 测试并在结束后删除素材，日志保存在忽略的 `artifacts/verification/`。

动态观察脚本为 `Tests/Playback/android_capture.py` 和同名 `.js`。需要已运行的 Frida 服务及 Java bridge 包，支持 `--remote` 和 `--java-bridge`，不再依赖另一仓库的私有 bundle。环境恢复和依赖版本见 [动态分析 skill](../.agents/skills/netease-runtime-analysis/SKILL.md)。同时观察主进程和 `:play`，因为媒体请求不一定来自 UI 进程。脚本不上传凭证，输出中隐藏请求认证头；原始输出仍只保存在忽略目录，分享前检查嵌套标识和媒体地址。

真实 CDN 实验使用测试工具的 `--audio` 获取本账号可访问的资源，再使用 `--range <metadata.json> <result.json>`。账号文件、临时带签名媒体地址、音频素材和原始运行时记录均只放在忽略目录。`--listen` 会实际播放并提交该次真实收听，用于小规模回归；不要用假时长调用上报来替代验证。
