# 一起听：房间与应用状态

实现依据：`Core/ListenTogether/ListenTogetherApi.cs`、`ListenTogetherSession.cs`、`ListenTogetherPlaybackApplier.cs`、`Core/NeteaseApiClient/NeteaseApiClient.ListenTogether.cs`。当前经过双账号实验和用户验收的互通实现采用 EAPI 快照轮询；没有把它冒称为已完整逆向的 WebSocket 协议。

## 端点结构

以下均在 `/api/listen/together/` 下签名，wire 为 `/eapi/listen/together/`；内层会话 header 与 Cookie 见 [传输契约](transport.md)。

| 操作 | 请求字段 | 主要响应/条件 |
|---|---|---|
| `status/get` | 空业务对象 | `data.inRoom` 必须为 bool |
| `room/create` | `refer: "songplay_more"` | `data.roomInfo.roomId` |
| `room/check` | `roomId` | `data.joinable`，拒绝说明 `copywriting` |
| `play/invitation/accept` | `refer: "inbox_invite"`, `roomId`, `inviterId` | 检查业务结果，随后确认成员状态 |
| `sync/playlist/get` | `roomId` | 带版本的 playlist 和 playCommand 快照 |
| `heartbeat` | `roomId`, `songId`, `playStatus: PLAY/PAUSE`, `progress` | 进度使用毫秒 |
| `play/command/report` | `roomId`, `commandInfo` | commandInfo 为 JSON 字符串，不是对象 |
| `sync/list/command/report` | `roomId`, `playlistParam` | playlistParam 为 JSON 字符串 |
| `end/v2` | `roomId` | `data.success == true` 才确认退出 |

所有请求先检查顶层 `code==200`，然后检查 `data` 对象和 `success/result`，不能只看 HTTP 状态。具体 command/playlist 字段、seq 和时间戳以当前 session 源码和运行时捕获为准；不要凭字段名字推断单位。

## 状态机要点

- 建房前检查当前账号是否已在房间，避免夺走用户其他设备会话。邀请链接校验 host、路径、必要参数和重复参数，短链接经受控解析；不把任意 URL 当作 roomId 来源。
- 当前 tick 约 2 秒，心跳约 30 秒；成员状态有建房/加入宽限和后续检查。与服务端 seq、服务器时钟偏移和最后本地操作版本结合，防止旧快照覆盖新操作。
- 远端应用过程中不回报同一变更形成循环；本地切歌/拖动可取消过期远端异步更新。播放、暂停、缓冲、进度、队列和播放模式必须走应用已有状态链，不能只改对话框标签。
- ESC 关闭对话框不等于退出房间；显式退出与应用关闭单独管理。连接中仍应可关闭；重复打开复用实例，避免双份 timer/事件订阅。NVDA 可读当前歌曲、播放状态和进度。

## 真实验证

使用两个不同、已授权账号，先检查是否在已有房间。A 建房 B 加入；双方分别执行播放、暂停、seek、切歌、队列修改，再退出并确认服务端成员状态。至少同时观察服务端快照、实际 BASS 状态与 UI，不能以按钮文字变化代替音频同步。

```powershell
# 默认不带参数执行离线测试；真实模式会改变账号房间状态。
& Tests/ListenTogether/bin/x64/Debug/net10.0-windows/ListenTogether.Tests.exe
# 运行时 capture 可直接使用在模拟器中已登录的测试账号。
& artifacts/frida/venv/Scripts/python.exe Tests/ListenTogether/android_probe.py capture --adb <adb.exe> --serial <serial> --seconds 120
```

`android_probe.py import` 是可选操作，必须提供 `--account` 和针对该 APK 版本验证过、导出 `importsession` 的 `--session-script`。仓库不分发私有会话脚本；优先让用户在测试模拟器正常登录。`android_interop.py --account <path>` 驱动生产 .NET session 并将邀请打开到本地模拟器，命令文件位于忽略的 `artifacts/listen-together/`；不向其他人发送邀请。

119 项离线断言覆盖邀请解析、wire 编码、会话控制、双向状态应用、异步过期处理及对话框 ESC。若版本升级导致新长连接实现，需要另捕获 WebSocket 握手/帧；不能从此表编造其二进制格式。
