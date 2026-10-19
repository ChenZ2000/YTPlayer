# 环境、动态观察与恢复

已验证基线：Windows 11、Android 11/API 30、网易云音乐 8.6.76、Frida 17.23.3。这是复现实验的基线，不是对新版本类名/签名的保证。

## 先探测，后启动

- 检查现有 `adb devices -l`、进程和本地调试目录，复用可用模拟器；先获取 serial，避免向多台设备同时发命令。
- 读取 APK 的包版本和运行进程即可，不反编译。包名 `com.netease.cloudmusic`，媒体请求可能在 `com.netease.cloudmusic:play`。
- 使用已有工具路径，或用 ADB/Frida 官方发行版安装到本仓库忽略目录。不要从未知 APK 镜像下载后直接宣称“干净”；优先复用已验证安装，记录来源、SHA-256 和签名证书摘要。

```powershell
$adb = (Get-Command adb -ErrorAction Stop).Source # 若不在 PATH，设为已有 adb.exe 完整路径
$serial = 'emulator-5554'                       # 替换为实际 serial
& $adb devices -l
& $adb -s $serial shell dumpsys package com.netease.cloudmusic |
    Select-String 'versionName|versionCode'
& $adb -s $serial shell ps -A | Select-String 'com.netease.cloudmusic'
& $adb -s $serial shell settings get global http_proxy
& $adb -s $serial forward tcp:27043 tcp:27042
```

使用 `$appPid`/`$playPid`，不要给 PowerShell 只读 `$PID` 赋值。不要把 Python 探针命名为 `inspect.py`，它会遮蔽标准库，导致 Frida 导入失败。

## 代理与证书

先保存当前代理设置。代理可选 Reqable 虚拟接口或 mitmproxy；只绑定本机，通过 ADB reverse 连接。系统 CA 安装仅在用户授权的可 root 测试模拟器进行：校验证书格式、subject hash、目标 Android 的信任存储与挂载方式，验证重启后的实际信任，不把 `adb push` 成功当成安装成功。不同 Android 版本的系统/Conscrypt 存储不同，不能硬套同一个 remount 命令。

```powershell
# 在另一个终端启动，日志仅保存在忽略目录。
mitmdump --listen-host 127.0.0.1 --listen-port 8080 -w artifacts/capture.flows
# 已保存原值之后再设置。
& $adb -s $serial reverse tcp:8080 tcp:8080
& $adb -s $serial shell settings put global http_proxy 127.0.0.1:8080
```

代理只能解释 wire 层；应用内 EAPI 仍需内存挂钩。Cronet/原生路径可能绕过系统代理。不要把代理看不到流量当成“没有请求”。证书/钉扎策略连续失败两次，改从解密后的运行时缓冲区观察，不堆叠未经验证的万能绕过脚本。

## 可移植的 Java bridge

`Tests/Runtime/frida_support.py` 用 Frida Compiler 编译明确指定的 bridge，不再剪切私有 bundle 的内部变量。现有环境可通过 `--java-bridge <index.js>` 复用；全新环境可在忽略目录准备依赖：

```powershell
python -m venv artifacts/frida/venv
& artifacts/frida/venv/Scripts/python.exe -m pip install 'frida==17.23.3'
npm install --prefix artifacts/frida frida-java-bridge@7.0.13
# 服务端版本必须与客户端对应；校验设备 ABI 后再选择 frida-server。
& artifacts/frida/venv/Scripts/python.exe Tests/Playback/android_capture.py --help
```

`--remote` 默认 `127.0.0.1:27043`；`--java-bridge` 默认 `artifacts/frida/node_modules/frida-java-bridge/index.js`，也支持 `FRIDA_JAVA_BRIDGE`。bridge 7.0.13 与 Frida 17.23.3 是本次构建验证组合；升级需重新验证，不能仅改服务端版本。

## 挂钩纪律

- 先 attach 已运行进程。候选类来自已有运行时证据；可对已知类反射其重载、字段，不全量枚举/解析混淆文件。
- 原方法只调用一次，保留其返回值和异常；观察代码失败不能改变生产请求。不要读取并消耗一次性响应流；复制有界 buffer 或使用 peek。
- Cronet `mRequestHeaders` 的元素可能是 `android.util.Pair` 或 `Map.Entry`，先判类型。
- 给每种策略记录尝试次数和失败原因。两次失败即切换已知 OkHttp `BridgeInterceptor`、Cronet `start`/上传缓冲区，或对应 native TLS 明文读写边界。
- Frida `session.detach()`、文件关闭和音频 JNI release 放在 `finally`。回调保存强引用，避免 GC 后 native 再调用失效委托。
- 退出时恢复开始前的代理值，不统一写死 `:0`；只删除本次新增的 ADB 转发和临时进程。
