# 听歌识曲：同源音频对照

## 已验证数据链

麦克风和系统回环均使用 8000 Hz、单声道、PCM16（小端）。持续采集，每约 3 秒取快照；指纹窗口最多 9 秒。窗口时长按实际样本数计算，不能将录音总墙钟时间当作本次指纹长度。默认录音 15 秒，可选 6/24 秒；超过 9 秒时滚动使用尾部窗口。

当前嵌入的官方 WASM 内存为固定 16 MiB，超过已验证窗口有内存风险。Base64 批量传入 JS 并转换样本，避免逐样本 .NET/V8 互操作。释放 native/vector、根引用录音回调；BASS 录音设备为线程局部状态，释放前在该线程选择正确设备。V8 后台工作禁用的原因是重复引擎压测曾触发致命退出，修改前必须重跑循环压力测试。

Android 已知运行时类：`com.netease.music.jni.FPExtractorHyaiJni`，方法 `native_init`、`native_addPCM`、`native_extract_query_fp`、`native_duration_by_now`、`native_release`/`native_reset`。始终成对初始化/释放，不让 JNI 探针影响正常录音。新 APK 的签名需现场核验。

## 请求与结果

EAPI `/api/music/audio/match`，完整会话、`useIosHeaders:false`：

```json
{
  "rawdata": "<base64-fingerprint>",
  "duration": "6.0",
  "times": "2",
  "sessionId": "<one-recognition-session>",
  "sceneParams": "{\"code\":2}",
  "decrypt": "1",
  "algorithmCode": "shazam_v2",
  "e_r": "true"
}
```

`duration` 为秒、使用 invariant 小数；`times` 是实际网络提交次数，一次识别使用同一 sessionId。指纹是字节流的 Base64，不把它臆造为公开的 JSON 音频特征矩阵。

响应要求 `code==200`、`data.result` 为数组或 null。数组项读取 `song`、`startTime`、`score`，保留服务端顺序、按歌曲 ID 去重。缺字段或业务错误不能变成“没有匹配”；空数组/null 才是合法无匹配。

传输一次约 8 秒超时，最多一次第一方 GET 回退并保持相同会话参数；业务拒绝不走回退。移除了自动第三方识曲回退。协调器在两次连续传输错误后停止，总时限约 50 秒；取消/ESC 等待采集释放并丢弃迟到响应。

## 对照方法

1. 使用有权播放的相同片段，固定转换为 8 kHz mono s16le。不要提交随意录到的麦克风对话；麦克风采集释放测试可不上传。
2. `Tests/Recognition/prepare_benchmark.py --pcm <song-id>=<pcm-path>` 生成干净、小音量、噪声、削波、前导静音以及负例。样本写入 `artifacts/recognition/`。
3. `native_fingerprint_probe.py --pid <app-pid> --manifest <manifest>` 获取 Android JNI 指纹；以相同窗口/次数运行 `Recognition.Tests.exe --benchmark <account> <manifest> <results>`。
4. 比较有无命中、候选 ID 顺序、耗时和异常。不同专辑同录音可能返回不同 ID；不要仅用源歌曲 ID 判断失败。单独测两种采集设备、热插拔、取消、立即重开和静音。
5. `Recognition.Tests.exe --stress <manifest>` 连续运行 120 次指纹生成，另跑默认 54 项离线断言。

2026-10-19 的 12 个音乐样本和 2 个负例中，生产协调器与 Android JNI 候选顺序相同；旧 6 秒方案 10/12 音乐样本命中，渐进方案 12/12。额外的 12 秒静音后出现音乐样本在 15 秒匹配。这支持已覆盖条件的改进，不能外推为任意音乐/噪声下 100% 命中。
