# 网易云音乐播放器 (NetEase Cloud Music Player)

一个基于 .NET Framework 的网易云音乐第三方桌面客户端，提供流畅的音乐播放体验和高质量音频支持。

## 项目概述

本项目是一个使用 C# WinForms 开发的网易云音乐第三方客户端，通过网易云音乐的公开 API 提供音乐搜索、播放和管理功能。应用基于 BASS 音频库实现高质量音频播放，并采用智能缓存系统确保流畅的播放体验。

**技术栈：**
- .NET Framework 4.8 (C# 9.0)
- Windows Forms UI
- BASS Audio Library (bass.dll, bassflac.dll)
- Newtonsoft.Json
- QRCoder (二维码登录)

## 项目特色

### 🎵 高质量音频支持
- 支持多种音质等级：标准 (128kbps)、高品 (320kbps)、无损 (FLAC)、Hi-Res、臻品母带
- 根据会员等级自动选择最佳音质

### ⚡ 智能缓存系统
- 基于 512KB 分块的智能缓存机制
- 支持三种下载策略：HTTP Range、完整顺序下载、跳跃式下载
- 优先级下载调度器，优先缓存播放位置附近的音频块
- 智能预加载下一曲，在播放进度 25% 时触发，实现无缝播放

### 🎯 流畅播放体验
- 即时跳转到任意播放位置（支持 HTTP Range 的服务器）
- 动态带宽分配（主播放 70%，预加载 30%）
- 全异步操作，UI 始终保持响应
- 播放队列管理，支持随机播放和循环模式

### 🔐 完整登录系统
- 二维码扫码登录
- 短信验证码登录
- 设备指纹管理，模拟官方客户端行为
- 安全的会话状态持久化

## 构建方法

### 环境要求
- Windows 操作系统
- .NET Framework 4.8
- PowerShell（用于运行构建脚本）

### Release 构建
```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1
```
输出文件：`bin\Release\NeteaseMusic.exe`

### Debug 构建
```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Debug.ps1
```
输出文件：`bin\Debug\NeteaseMusic.exe`

### 依赖安装
项目使用 NuGet 包管理器管理依赖。构建脚本会自动还原所需的包：
- Newtonsoft.Json
- QRCoder
- BrotliSharpLib

## 使用方法

### 首次使用

1. 运行构建脚本生成可执行文件
2. 运行 `NeteaseMusic.exe` 启动程序
3. 点击登录按钮，选择二维码登录或短信登录
4. 登录成功后即可开始使用

### 主要功能

#### 🔍 搜索音乐
- 在搜索框输入歌曲名、歌手名或专辑名
- 支持搜索歌曲、歌单、专辑和歌手
- 点击搜索结果即可播放

#### 🎼 播放控制
- **播放/暂停**：点击播放按钮或使用空格键
- **上一曲/下一曲**：使用播放器控制按钮
- **进度调节**：拖动进度条跳转到指定位置
- **音量调节**：使用音量滑块调整音量
- **播放模式**：支持顺序播放、随机播放、单曲循环

#### 📝 播放列表
- 查看当前播放队列
- 添加歌曲到队列
- 管理播放列表

#### 👤 账户管理
- 查看个人信息和 VIP 状态
- 访问我的歌单和收藏
- 退出登录

### VIP 等级检查工具
```powershell
.\bin\Release\NeteaseMusic.exe --check-vip
```
诊断工具，用于验证 VIP 订阅等级和可用音频质量。

## 注意事项

- 本项目仅供学习交流使用
- 请遵守网易云音乐服务条款
- 高质量音频需要相应的会员等级支持
- 首次播放时需要缓冲音频数据，可能有短暂延迟

## 许可证

本项目采用 MIT 许可证。详见 [LICENSE](YTPlayer/LICENSE) 文件。

## 致谢

- 网易云音乐提供的 API 接口
- BASS Audio Library 音频播放引擎
- 开源社区的各项依赖库

---

**声明**：本项目为第三方客户端，与网易云音乐官方无关。
