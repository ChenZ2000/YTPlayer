# YTPlayer 易听

一个专注高可用与无障碍体验的网易云音乐第三方桌面客户端，基于 .NET Framework 4.8 + WinForms 构建，提供简洁直观的 UI ，高质量音频播放及下载、可靠的播放指令队列以及面向键鼠与屏幕阅读器用户的统一交互体验。

## 项目概述

- **技术栈**：.NET Framework 4.8、Windows Forms、BASS 2.4、Newtonsoft.Json、QRCoder
- **定位**：桌面级流媒体播放器，结合网易云开放接口与自研播放/缓存策略，兼顾音质、性能、可访问性
- **架构理念**：所有 UI 请求均可被取消/覆盖，播放队列以最新指令为最高优先级，确保界面永不阻塞

## 设计亮点

- **🎧 多音质自适应**：标准/高品/无损/Hi-Res/臻品母带自动协商，音频块缓存+就近预加载带来尽可能的即点即播体验。
- **⚙️ 指令并发调度**：播放、下载、云盘、搜索请求全部以可取消的异步任务执行，后发指令自动抢占，避免 UI 卡顿。
- **🧭 一致的可访问导航**：涵盖键盘焦点保护、屏幕阅读器友好型 ComboBox、Backspace 历史导航、空格/功能键快捷控制。

## 功能总览

### 搜索、账户与浏览
- 支持歌曲 / 歌手 / 专辑 / 歌单多类型浏览和搜索、收藏/取消收藏、创建新歌单， Backspace 返回。
- 提供二维码与短信验证码登录。
- 云盘浏览、上传、下载与删除。
- 基于账户等级、歌曲可用资源和用户选择动态提供最佳音质。

### 播放控制
- 播放/暂停（空格）、上一曲/下一曲（F5/F6）、快退/快进（左右箭头）、音量（F7/F8）。
- 任意时间跳转（F12）支持**时:分:秒**或**百分比加%**格式。
- 队列插播、播放次序选择（列表循环/顺序播放/单曲循环/列表随机。
- 屏幕阅读器歌词输出开关（F11）。
- 输出设备切换（F9）。

### 下载与管理
- 歌曲 / 歌单 / 专辑/分类批量下载，下载任务支持排队、取消与状态可视化。

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
可执行文件位于 `bin\Release\YTPlayer.exe` 或 `bin\Debug\YTPlayer.exe`。

### 依赖还原
项目使用 `packages.config` 管理 NuGet 依赖，构建脚本会自动执行 `nuget restore`，包含：
- Newtonsoft.Json
- QRCoder
- BrotliSharpLib
- System.Runtime.CompilerServices.Unsafe
- taglib-sharp

## 快速开始

1. 访问 [Releases 页面](https://github.com/ChenZ2000/YTPlayer/releases) 并下载最新构建版压缩包。
2. 解压并运行 `YTPlayer.exe`。
3. 享受音乐吧！

## 鸣谢

### 人员
- 感谢[**@ZJN046** - https://github.com/zjn046](https://github.com/zjn046) 给本项目提供的灵感、 UI 交互优化和大量后端代码参考。
### 代码库
- [Binaryify/NeteaseCloudMusicApi](https://github.com/Binaryify/NeteaseCloudMusicApi)
- [chaunsin/netease-cloud-music](https://github.com/chaunsin/netease-cloud-music)
及本项目依赖的众多优秀第三方库

## 欢迎通过 Issue/PR 提出建议或提交改进

Enjoy the music 🎶

## 给开发者买点零食~

![微信扫一扫，给 ChenZ 买点零食](WeChatQRCode.jpg)