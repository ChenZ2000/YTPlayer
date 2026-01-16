# YTPlayer 易听

一个专注高可用与无障碍体验的网易云音乐第三方桌面客户端。
基于 .NET 10 + WinForms 构建，提供简洁直观的 UI ，高质量音频播放及下载、可靠的播放指令队列以及面向键鼠与屏幕阅读器用户的统一交互体验。

## 系统要求

- Windows 10/11 64 位

## 项目概述

- **技术栈**：.NET 10、Windows Forms、BASS 2.4、Newtonsoft.Json、QRCoder、WebView2、ClearScript
- **定位**：桌面流媒体播放器，基于第三方 API 自研播放/缓存策略，兼顾音质、性能和可访问性体验

## 设计亮点

- 🛋️ 极简 UI ，完善的快捷键支持，操作如风
- 🎧 可根据当前用户会员权限、歌曲可用音质选项和用户选择，自动协调最高音质
- ⚙️ 支持显示或隐藏无障碍层的序号、播放控制栏和歌词的屏幕阅读器输出

## 功能总览

### 登录与账号
- 提供网易云音乐移动端 App 扫码授权登录
- 提供嵌入式网页登录，支持所有登录方式，应用会在后台监听并截获 Cookie 以完成登录流程。
（网页登录的 WebView 页面加载有时可能会比较慢，请耐心等待，多刷新几次尝试。）

### 搜索和浏览
- 支持歌曲 / 歌手 / 专辑 / 歌单/播客多类型浏览和搜索、收藏/取消收藏、新歌单的创建和自建歌单的管理。
- UI 支持退格键返回和 F5 刷新。
- 主页提供“最近听过”入口，集中展示最近歌曲/歌单/专辑/播客的动态总数。
- 搜索框支持直接粘贴网易云歌曲 / 歌单 / 专辑 / 歌手/播客的网页 URL，多 URL 用分号分隔，类型组合框自动切换到相应分类并在主列表自动显示解析结果。
- 云盘浏览、上传、下载与删除。
- 文件/操作菜单下提供“当前播放”入口，可随时对正在播放的歌曲执行相关操作。

### 播放与控制
- 支持基于账户等级、歌曲可用资源和用户选择动态提供最佳音质
- 播放/暂停（空格）
- 上一曲/下一曲（F3/F4）
- 快退/快进（左右箭头）
- 音量减/音量加（F1/F2）
- 任意时间跳转（F12）支持**时:分:秒**或**百分比加%**格式
- 队列插播、播放次序选择（列表循环/顺序播放/单曲循环/列表随机）
- 屏幕阅读器歌词输出开关（F11）
- 播放控件隐藏/显示开关：（F7），可随时切换播放控件的显示（包括播放/暂停按钮，进度滑块和音量滑块）
- 序号隐藏/显示开关（F8），可随时切换列表序号的显示
- 输出设备切换（F9）

### 歌曲解封/解灰
- 遇到未登录/账号无 VIP 、歌曲在网易云音乐官方无资源/已下架时，应用会自动尝试查找和匹配替代资源并播放。
（不保证百分百命中率）
- 下载和直链分享同样支持解封/解灰

### 下载与分享
- 歌曲 / 歌单 / 专辑/播客/分类批量下载，下载任务支持排队、取消与状态可视化。
- 下载歌词，自动检测可用歌词并以 LRC 格式下载。
- 支持分享歌曲网页/直链和歌单、专辑、歌手、播客的网页。

### 评论互动
- 在任意歌曲 / 专辑 / 歌单的上下文菜单中打开评论对话框，以树视图形式查看评论和楼层回复。
- 支持 F5 刷新和单独的 F8 序号显示/隐藏控制。
- 支持 Ctrl+C 复制和本人评论/回复的 Delete 删除。
- 可直接在窗口底部编辑和发表评论， Enter 换行， Shift + Enter 发送，或在树节点上使用回车/通过上下文菜单打开回复对话框，编辑框同样支持 Enter 换行和 Shift + Enter 发送。

### 听歌识曲
- 支持快捷键 CTRL + L 或通过文件/操作菜单下的入口执行听歌识曲。
- 允许识别任意输入或输出设备的声音。

### 自动更新
- 通过“帮助 → 检查更新”触发版本检测，有更新时列出下载包信息。
- 若用户选择立即更新，内置更新器会在独立进程下载官方压缩包、解压、请求主程序退出后替换全部二进制，并在验证版本一致后重新启动易听。
- 更新器全程提供进度条与日志提示。

## 构建方法

### 环境要求
- Windows 10/11 64 位
- .NET 10 SDK
- Visual Studio 2022/2025 (含 MSBuild) | PowerShell 7+

### 构建脚本
```powershell
#### Release
powershell -ExecutionPolicy Bypass -File .\Build.ps1

#### Debug
powershell -ExecutionPolicy Bypass -File .\Build-Debug.ps1
```
可执行文件位于 `bin\Release\YTPlayer.exe` 或 `bin\Debug\YTPlayer.exe`，同时会生成并复制自动更新器 `YTPlayer.Updater.exe`。

### 依赖还原
项目使用 `PackageReference` 管理 NuGet 依赖，构建脚本会自动执行 `MSBuild /t:Restore` 或 `dotnet restore`。

## 文档

- `Docs/KeyboardShortcuts.md`：集中维护快捷键说明，构建时会被嵌入到快捷键参考对话框。
- `Docs/About.md`：维护关于对话框文案与按钮信息，文件顶部的 `--- ... ---` 元数据块可配置 `ProjectUrl`、`AuthorName`、`AuthorUrl`、`ContributorName`、`ContributorUrl` 等键，正文中继续支持 `{{Version}}` 等占位符。

## 快速开始

1. 访问 [Releases 页面](https://github.com/ChenZ2000/YTPlayer/releases) 并下载最新构建版压缩包。
2. 解压并运行 `YTPlayer.exe`。
3. 享受音乐吧！

## 鸣谢

### 人员
- 感谢[**@ZJN046**](https://github.com/zjn046) 给本项目提供的灵感、 UI 交互优化和大量后端代码参考。
### 代码库
- [**Binaryify/NeteaseCloudMusicApi**](https://github.com/Binaryify/NeteaseCloudMusicApi)
- [**chaunsin/netease-cloud-music**](https://github.com/chaunsin/netease-cloud-music)
- [**api-enhanced**](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced)

及本项目依赖的众多优秀第三方库

## 欢迎通过 Issue/PR 提出建议或提交改进

Enjoy the music 🎶

## 给开发者买点零食~

![微信扫一扫，给 ChenZ 买点零食](WeChatQRCode.jpg)
