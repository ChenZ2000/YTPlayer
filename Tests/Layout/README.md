# WinForms 布局回归

在仓库根目录运行：

```powershell
.\scripts\Test-DialogLayout.ps1
```

脚本先使用项目指定的 Visual Studio MSBuild 构建 Debug/x64，检查成品更新器旁的 DPI 配置，再还原并运行 NUnitLite 5.0.0。测试工具包仅安装到 `artifacts/layout/packages`，不成为应用发布依赖。结果、UTF-8 控件尺寸记录和 PNG 渲染图保存在 `artifacts/layout/regression`。

测试直接加载 Debug 产物的真实 WinForms 控件。更新器使用 .NET Framework 4.8，主程序使用 .NET 10。每套测试分别在当前显示器 DPI 和进程内的 96 DPI 虚拟化模式运行；脚本不会修改系统显示设置。当前验证机器原生 DPI 为 192（200%）。额外的字体放大是独立的布局压力测试，不冒充实际的 125%/150%/300% 显示器测试。

覆盖 22 类应用对话框、主界面和独立更新器。检查内容包括控件非零尺寸、父容器边界、换行文字高度、按钮及表格单元格重叠、可调整窗口的最小尺寸、长错误信息及滚动到底部按钮、可滚动日志、主题消息框的完整可访问文本、焦点和 Tab/Esc 行为。仅排除评论窗口中专供读屏通知的离屏标签；隐藏的 ContextMenuHost 也属于有意设计，不进行可见内容断言。主界面继续使用现有的运行时列表布局。

测试设置独立的 `YTPLAYER_ROOT`，更新器使用每次新建的隔离计划目录和空下载 URL，在下载前进入可预测的失败状态。登录、评论、检查更新和歌单加载使用离线入口，不访问账号、下载更新包、替换安装文件或启动音频录制。二维码测试验证布局和图片框范围，不验证联网生成/扫码流程。安装依赖和传输窗口使用空测试配置。

如果检测到随应用提供的 NVDA 控制 DLL，测试会输出 `nvdaController_testIfRunning` 的结果；`0` 表示 NVDA 正在运行。可访问性断言检查 WinForms/MSAA 名称、角色、完整文本和实际焦点，不等同于逐字核对 NVDA 合成语音。

本次复现的主要问题：

| 位置 | 可验证的原因 | 修复方式 |
| --- | --- | --- |
| 共用主题消息框 | `Panel.AutoSize` 不计入 `Dock=Top` 子控件的宽度，实际内容超出父画布；旧源码在更新器运行时的 4 组按钮用例均失败 | 内容容器由首选尺寸驱动，字体变化时重新测量，并允许尺寸缩回 |
| 独立更新器 | .NET Framework 高 DPI 未启用，字体在布局完成后切换，固定坐标与固定高度不能容纳缩放/长文本 | Windows 10/11 兼容清单、PerMonitorV2 配置、96 DPI 基准、初始化时设置字体、内容行和可伸缩日志 |
| 更新器配置分发 | 原构建把 EXE 配置搬入 libs，直接运行不能读取；启动临时更新器时也必须保持配置匹配 | 配置保留在 EXE 旁，启动复制优先使用配套配置并兼容旧 libs 目录；测试两种安装布局的临时目录复制 |
| 手工创建的对话框 | 缺少初始 DPI 基准，文字放大而固定尺寸未同步；隐式 AutoSize 列可撑出窗口 | 在 SuspendLayout/ResumeLayout 期间完成初始化，声明基准，明确百分比列 |
| 长文本、一起听、听歌识曲、输出设备 | 百分比行在内容超过窗口时把底部控件拉到前面的行上；原生下拉框字体变化后的实际高度可大于表格缓存行高 | 按实际可用宽度测量最小滚动范围，为下拉框明确预留行高，保留键盘滚动和焦点顺序 |
| 歌手详情 | 自动尺寸 Panel 包含右停靠按钮行，首选高度为 0 | 直接使用自动尺寸 FlowLayoutPanel |
| 登录 | 在布局后重写窗口尺寸，按过时的 TabPage 尺寸和固定像素计算二维码/状态位置 | 跟随实际页面尺寸、字体测量及 DPI，图片完整缩放 |
| 批量下载、添加歌单、传输管理 | 缩小窗口或扩大字体后固定位置按钮互相遮盖/内容超出窗口 | 流式按钮行、内容布局、填充式页签 |
| 自绘进度条 | 进度刚开始或跑马灯进入边缘时，固定圆角直径大于可绘制段宽度，路径越出画布；4 个边界用例中 3 个在旧代码失败 | 按实际段宽高限制圆角，验证 1 像素宽/高等边界；同时检查普通控件的裁剪区域没有缩小 |

性能用例向更新器的下载进度报告器连续提交 5,001 次回调，验证首条和完成回调必达且 UI 队列不会被逐缓冲区更新淹没。生产代码在投递 UI 之前将中间进度限制为每秒最多约 10 次，阶段完成仍立即显示。

高 DPI 配置依据：[Microsoft：.NET Framework WinForms 高 DPI 支持](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms?view=netframeworkdesktop-4.8)。

## 控件视觉与鼠标交互回归

完整离线验证入口：

```powershell
.\scripts\Test-ControlVisuals.ps1
# 如有现成的本地 MP3 回归素材，同时验证真实 BASS 缓存定位和播放恢复：
.\scripts\Test-ControlVisuals.ps1 `
  -SeekFixture .\artifacts\playback-protocol\seek-fixture.mp3 `
  -RecoveryFixture .\artifacts\playback-recovery\fixture.mp3
```

输出位于 `artifacts/controls`。脚本使用指定的 Visual Studio MSBuild 构建 Debug/x64，并依次运行布局/交互、播放、导航、一起听和听歌识曲回归。UI 用例串行运行，避免不同窗口争抢焦点影响结果。所有账号/配置测试写入仓库内的隔离测试目录。

新增的三个测试类有以下职责：

- `ControlBaselineTests`：真实 TrackBar 的 MSAA/UIA 快照、原生整数范围、渲染 PNG，以及 500 次鼠标消息到同步 WM_PAINT 完成的耗时。可设置 `YTPLAYER_CONTROL_BASELINE_ASSEMBLY` 指向保存的旧 `YTPlayer.App.dll`，在独立 AssemblyLoadContext 中复核修改前的实现。未设置时使用当前 Debug 程序集。此耗时不包含屏幕合成、显示器延迟和语音合成延迟。
- `ControlInteractionTests`：五档可注入 DPI（96/120/144/192/288）的轨道、命中区、抓取偏移、越界拖动、提示边界；主窗口在播放、暂停、缓冲状态下的最终浮点提交；捕获丢失、窗口失活、隐藏、切歌和旧异步回调；音量 300ms 合并保存与退出补存；宽窄窗口、长音频/标题、字体放大、Tab 顺序、四主题及系统颜色；颜色对比、纯视觉更新的 WinEvent、3,000 次操作后的 GDI/USER 资源和静置时绘制次数。
- `SharedControlVisualTests`：在 .NET 10 和 .NET Framework 4.8 上检查按钮状态渲染、Enter/Esc、原生按钮角色和最小高度；进度条的确定/不确定状态，以及隐藏、禁用、停止、销毁时停止动画。

实现仍使用原生 TrackBar 和 Button 实例。播放滑块只有视觉位置使用小数，整数值变化继续进入原有 SeekManager。正常松手补交尚未提交的最终浮点目标；中断手势结束现有 Seek 序列，不额外提交一个位置。切歌在取消旧请求的同时使旧手势失效，并防止延迟到达的取消回调结束新手势。音量只合并自身来源的配置保存请求，其他 SaveConfig 调用保持原行为。

主题对比测试分别计算主按钮普通/悬停/按下状态的文字对比，以及普通按钮各状态的文字、边框和焦点轮廓。当前四套主按钮白字对比约 5.02–5.32:1，焦点环对白底约 6.00–6.41:1。计算门槛依据 [W3C 文字对比说明](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html) 和 [W3C 非文字对比说明](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast)。

真实窗口验证区分当前显示器 DPI 和 Windows 96 DPI 进程虚拟化。125%、150%、300% 是几何/绘图参数注入，不代表有这些缩放比例的物理显示器。系统颜色测试使用内部覆盖开关，不修改 Windows 高对比度设置。当前 Windows/WinForms 环境的滑块 UIA 暴露 ValuePattern，原生范围通过 TrackBar 消息核验；不会把不存在的 RangeValuePattern 声称为已验证。

检测到 NVDA 正在运行，只说明控制 DLL 可联通。自动化会验证角色、完整名称、值、范围、焦点顺序和纯视觉刷新不增加名称/值/焦点事件；NVDA 的实际逐字朗读、真实跨显示器 DPI 切换，以及系统高对比度切换后的桌面表现，仍应由人工实际体验确认。

## 多行提示与相关回归

`MessageTextTests` 随布局脚本一并运行，覆盖多种换行符的统一、原生编辑框逐行光标移动、UIA 文本行读取、选择复制，以及提示窗口的 Enter/Esc 和 Tab 焦点行为。测试同时检查更新器、一起听、购买提示及网页式内容的纯文本备用视图。

其他相关测试位于 `Tests/Documents`（语义文档、WebView2 与 Tab 导航）、`Tests/MenuAccessibility`（菜单的 MSAA/UIA 事件，使用 `--test` 运行）、`Tests/Navigation`（移除操作、分批加载焦点恢复和私人 FM 模式切换）及 `Tests/PersonalFm`（模式参数和响应协议）。这些测试使用隔离数据；`Tests/PersonalFm` 只有显式传入 `--live <账号文件路径>` 时才读取已有会话并执行真实取歌验证，日志仅输出脱敏摘要。
