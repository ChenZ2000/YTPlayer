# YTPlayer 易听

一个专注高可用与无障碍体验的网易云音乐第三方桌面客户端，基于 .NET Framework 4.8 + WinForms 构建，提供简洁直观的 UI ，高质量音频播放及下载、可靠的播放指令队列以及面向键鼠与屏幕阅读器用户的统一交互体验。

## 系统要求

- Windows 10/11 64 位

## 项目概述

- **技术栈**：.NET Framework 4.8、Windows Forms、BASS 2.4、Newtonsoft.Json、QRCoder
- **定位**：桌面级流媒体播放器，结合网易云开放接口与自研播放/缓存策略，兼顾音质、性能、可访问性
- **架构理念**：所有 UI 请求均可被取消/覆盖，播放队列以最新指令为最高优先级，确保界面永不阻塞

## 设计亮点

- **🎧 多音质自适应**：标准/高品/无损/Hi-Res/臻品母带自动协商，音频块缓存+就近预加载带来尽可能的即点即播体验。
- **⚙️ 指令并发调度**：播放、下载、云盘、搜索请求全部以可取消的异步任务执行，后发指令自动抢占，避免 UI 卡顿。
- **🧭 一致的可访问导航**：涵盖键盘焦点保护、屏幕阅读器友好型 ComboBox、Backspace 历史导航、空格/功能键快捷控制。

## 功能总览

### 登录与账号
- 提供网易云音乐移动端 App 扫码授权登录
- 提供嵌入式网页登录，支持所有登录方式，应用会在后台监听并截获 Cookie 以完成登录流程。
- 网页登录的 WebView 页面加载速度可能会很慢，请耐心等待，多刷新几次尝试。

### 搜索和浏览
- 支持歌曲 / 歌手 / 专辑 / 歌单多类型浏览和搜索、收藏/取消收藏、创建新歌单， Backspace 返回。
- 主页提供“最近听过”入口，集中展示最近歌曲/歌单/专辑/播客的动态总数（启动、返回主页或再次激活“主页”命令时都会刷新）。
- 登录后可在主页直接访问“收藏的播客”，快速浏览并打开所有已订阅的播客。
- 歌手“全部歌曲”“全部专辑”列表新增上下文“排序”菜单，分别支持热门/发布时间、最新/最早顺序切换，刷新不产生新的历史记录。
- 所有专辑条目会在曲目数量后附带发行年份标签，方便快速判断发行时间。
- 支持播客搜索、节目浏览与收藏，进入播客详情后自动加入历史导航、上下文菜单和下载工作流。
- 搜索框支持直接粘贴网易云歌曲 / 歌单 / 专辑 / 歌手网页 URL，多 URL 用分号分隔，类型组合框自动切换到相应分类并在主列表自动显示解析结果。
- 云盘浏览、上传、下载与删除。
- 基于账户等级、歌曲可用资源和用户选择动态提供最佳音质。
- 文件/操作菜单下动态隐藏的“当前播放”入口，可随时执行正在播放的歌曲相关操作。

### 播放控制
- 播放/暂停（空格）、上一曲/下一曲（F3/F4）、快退/快进（左右箭头）、音量（F1/F2）、刷新列表（F5）。
- 任意时间跳转（F12）支持**时:分:秒**或**百分比加%**格式。
- 队列插播、播放次序选择（列表循环/顺序播放/单曲循环/列表随机）。
- 屏幕阅读器歌词输出开关（F11）。
- 序号隐藏/显示开关（F8），可随时切换列表序号的显示。
- 输出设备切换（F9）。

### 下载与管理
- 歌曲 / 歌单 / 专辑/播客/分类批量下载，下载任务支持排队、取消与状态可视化。
- 下载歌词，自动检测可用歌词并以 LRC 格式下载。

### 评论互动
- 在任意歌曲 / 专辑 / 歌单的上下文菜单中打开评论对话框，树形查看楼层回复，支持快捷键导航与焦点保持。
- 可直接在窗口底部发表顶层评论（Shift + Enter 发送），或在树节点上使用回车/上下文菜单进行回复（回复输入框同样支持 Shift + Enter 发送）、Ctrl+C 复制、Delete 删除（仅限本人评论）。

### 听歌识曲
- 支持快捷键 CTRL + L 或通过文件/操作菜单下的入口执行听歌识曲。
- 允许识别任意输入或输出设备的声音。

### 歌曲解封/解灰
- 遇到未登录/账号无 VIP 导致无法完整播放、歌曲在网易云音乐官方无资源/已下架时，应用会自动尝试查找和匹配替代资源并播放。

### 自动更新
- 通过“帮助 → 检查更新”触发版本检测，有更新时列出下载包信息。
- 若用户选择立即更新，内置更新器会在独立进程下载官方压缩包、解压、请求主程序退出后替换全部二进制，并在验证版本一致后重新启动易听。
- 更新器全程提供进度条与日志提示。

## 构建方法

### 环境要求
- Windows 10/11 64 位
- .NET Framework 4.8 SDK
- Visual Studio 2022 (含 MSBuild) | PowerShell 7+

### 构建脚本
```powershell
#### Release
powershell -ExecutionPolicy Bypass -File .\Build.ps1

#### Debug
powershell -ExecutionPolicy Bypass -File .\Build-Debug.ps1
```
可执行文件位于 `bin\Release\YTPlayer.exe` 或 `bin\Debug\YTPlayer.exe`，同时会生成并复制自动更新器 `YTPlayer.Updater.exe`。

### 依赖还原
项目使用 `packages.config` 管理 NuGet 依赖，构建脚本会自动执行 `nuget restore`，包含：
- Newtonsoft.Json
- QRCoder
- BrotliSharpLib
- System.Runtime.CompilerServices.Unsafe
- taglib-sharp

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
