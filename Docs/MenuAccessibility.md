# 菜单首项、单项导航与重复提示的读屏兼容性

## 问题与诊断边界

争渡读屏在易听中可能不朗读刚打开的上下文菜单首项，但在切换菜单项时可以朗读；只有一个可用项目时，上下键没有切换目标，会导致整个菜单无法获知。

本次使用 Windows 11、.NET Windows Desktop 10.0.15、真实 WinForms 菜单进行事件采集。探针通过 `SetWinEventHook` 接收 MSAA 事件，再用 `AccessibleObjectFromEvent` 按事件中的 HWND、object ID、child ID 查询名称、角色和状态；同时监听 UIA 的焦点事件。

修复前的记录明确显示：

- 首次打开并非完全没有焦点事件：可以看到菜单容器、菜单项以及 UIA 焦点事件。不能据此断言争渡内部为什么丢弃或没有播报首项。
- 单项菜单按上、下键时，没有新的菜单项焦点事件。原导航保护代码拦截了按键，再对已经选中的项目调用 `Select()`；再次选中同一项目不会触发正常的选择变化。
- 原先打开菜单后的多轮 `Select()` / `AccessibilityObject.Select(TakeFocus | TakeSelection)`，不能可靠地代替可解析的 MSAA 焦点事件，还可能将用户已经移动的选择重新拉回第一项。

第一轮修复后，用户确认首项和单项菜单朗读已经恢复；随后反馈 NVDA 打开时重复提示“菜单”，争渡在多项菜单首次移动时再次提示“菜单”。

第二轮增加 `UIA.MenuOpened` / `MenuClosed`、时间戳、UIA RuntimeId 和父级 RuntimeId 记录，并对比四种真实菜单打开方式，确认：

- 上一轮保留的根菜单 `Focus()` 会在菜单进入事件之外增加菜单容器的焦点通知。在本机采集中，同次调用出现两条 `OBJID_CLIENT / CHILDID_SELF` 的菜单容器焦点事件；取消该调用后这两条消失。WinForms 的 `ModalMenuFilter` 本身就会把按键路由给活动菜单，无需额外夺取 HWND 焦点。
- 首项正常 `Select()` 已经发出 MSAA 和 UIA 通知；原先 32 毫秒后的无条件 MSAA 重放使首项有两次原生焦点通知。Windows 还会桥接事件，UIA 中能够观察到原生 provider 和 MSAA 代理使用不同 RuntimeId。因此不能仅凭条目名称相同就认为读屏必然去重。
- 去掉强制窗口焦点，将 32 毫秒等待放到首次选择之前，且只选择一次后，原生事件成为一次菜单进入、一次首项焦点；后续移动只有菜单项焦点。首项与后续方向键使用相同的 WinForms 通知流程。

这些证据确认了应用额外注入的重复通知及不一致时序。争渡内部如何缓存父级、最终为何重读，没有可供核查的源码，仍属依据症状与事件顺序作出的推断，不能冒充已验证其内部实现。

## 实现

公共实现位于 `Utils/ContextMenuAccessibilityHelper.cs`，由 `MenuNavigationBoundaryHelper.Attach` 统一注册。覆盖歌曲、搜索框、下载、评论树、托盘上下文菜单，以及菜单栏下拉菜单、动态添加的子菜单。

1. 在菜单打开时准备当前层的无障碍对象，离开 `Opened` 回调并等待一次 32 毫秒 UI 定时器后，选择第一个可操作项目。已有选中项时保留原选择，用户已移动到其他项时取消首项初始化。
2. 菜单进入交由 WinForms 的标准菜单打开通知处理。根菜单和子菜单都不额外调用窗口 `Focus()`；首次选择使用 WinForms 正常的 MSAA/UIA 通知，不再延迟重放首项焦点。
3. 仅在单项菜单需要重新报告当前项时补发标准 `EVENT_OBJECT_FOCUS`。从 `AccessibilityObject.GetChild()` 的实际顺序查找子项 ID，保证隐藏项、分隔符、禁用项混排时指向正确项目。
4. 对只有一个可操作项目的菜单，上、下键仍被作为一次明确的导航操作处理：若项目已经选中，就补发一次焦点事件；若此次才选中，沿用正常选择事件。
5. 保留 WinForms 原有 UIA provider、菜单项角色、勾选状态、访问键、命令快捷键及执行逻辑。没有添加语音 SDK、读屏进程检测、空白菜单项或新的第三方依赖。
6. 方向键边界处理也不再额外调用 `AccessibilityObject.Select(TakeFocus | TakeSelection)`，避免在正常选择之外再走一次无障碍选中路径。

每次打开最多安排一次首项初始化；重复 Attach/Ensure 不会重复注册或重复安排。关闭、销毁、重新打开、用户改选项目、进入子菜单都会使过期操作失效。定时器执行一次即释放，空闲时没有轮询。仅准备实际打开层的无障碍对象，避免递归预热所有深层子项。

## 自动回归与复现

在仓库根目录运行：

```powershell
& 'E:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' Tests/MenuAccessibility/MenuAccessibility.Tests.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64 /m /nologo /v:minimal /warnaserror
& '.\Tests\MenuAccessibility\bin\x64\Debug\net10.0-windows\MenuAccessibility.Tests.exe' --test
```

测试会在独立窗口中打开真实菜单，监听操作系统事件，按真实 ToolStrip `KeyDown` 事件调用链检查边界，并通过 WinForms 自身的 `ProcessDialogKey` 检查普通方向键移动。无真实账号、网络请求或播放操作。测试中的 WPF 引用仅用于系统自带 UIAutomationClient，不改变应用依赖。

本次最终代码验证结果：完整解决方案 Debug x64 编译 0 警告、0 错误；菜单专项 80 项断言和原有导航回归 443 项断言通过。

菜单专项覆盖：

- 首项选择及 MSAA/UIA 可访问性；单项菜单上、下键；多项菜单切换和首尾循环。
- 每次打开只有一次原生菜单进入和一次首项焦点，没有额外菜单容器焦点；方向键移动不产生新的菜单进入或 UIA 菜单容器焦点。
- Windows 桥接产生的 UIA 菜单打开回调指向同一个菜单 RuntimeId。
- 隐藏、分隔符、禁用项混排时的正确 child ID。
- 勾选状态、访问键、命令快捷键仍然存在，导航不执行命令。
- 重复注册和重复初始化不放大通知；空闲时没有持续重发。
- 用户快速改选、关闭、销毁、重新打开时不出现过期首项通知。
- 只有禁用项时不伪造焦点。
- 动态子菜单、单项子菜单、立即展开子菜单、菜单栏下拉菜单，以及左右键的展开/返回边界。

本次事件记录位于工作区的 `artifacts/menu-accessibility/`，该目录不纳入版本控制：

| 文件 | 内容 |
| --- | --- |
| `baseline-events.json` | 修改前采集的基线，单项菜单上、下键没有菜单项焦点事件 |
| `before-repeat-events.json` | 第一轮修复后的记录，包含强制容器焦点和首项重放 |
| `before-repeat-open-strategies.json` | 第二轮修改前的打开策略对照，含时间戳、UIA 对象与父级身份 |
| `events.json` | 当前自动测试的 MSAA/UIA 事件及名称、角色、状态 |
| `debug-build-menu-repeat.log` | 第二轮完整解决方案 Debug 编译日志 |

自动测试确认的是事件和菜单行为，不等同于确认所有读屏版本的最终语音输出。Windows 可以将 MSAA 事件桥接到 UIA，因此不能将“没有显式调用 UIA RaiseEvent”解释为“没有额外 UIA 事件”。本次用户已完成实测，确认首项、单项导航及重复提示问题彻底解决；不再朗读“菜单”容器提示是读屏的自然表现，保留该行为。

## 读屏验收窗口

直接运行以下程序即可打开独立验收窗口，无参数或 `--preview` 均可：

```powershell
& '.\Tests\MenuAccessibility\bin\x64\Debug\net10.0-windows\MenuAccessibility.Tests.exe'
```

窗口提供单项、多项、含子菜单、过滤不可用项四个按钮。Tab 到按钮，按空格打开菜单，也可以使用 Shift+F10。所有命令只更改测试窗口内的状态标签。

分别用争渡和 NVDA 检查：

1. 打开单项菜单，不按方向键即可获知“查看歌曲信息”；再按上、下键时不出现静默死角或额外容器提示。
2. 打开多项菜单，首项和后续移动正常朗读，不重复提示“菜单”；首尾循环、Enter 执行和 Esc 返回均正常。无需强制朗读容器名称。
3. 展开子菜单、按左键返回、快速开关菜单，确认不回读旧项目、不抢走当前选择。
4. 在易听实际界面的歌曲信息、下载、评论及托盘菜单中复查相同行为。

第一轮窗口自动化曾返回 `failed to activate captured window`。第二轮已能打开测试菜单并操作方向键和 Esc，最终听感由用户实测验收通过。NVDA 的源码还表明其会根据焦点状态及待处理焦点过滤 MenuOpened，实际是否输出角色提示与读屏版本/设置有关。这里保留标准菜单角色和一次原生菜单进入，不通过直接 TTS、改写角色或读屏专用开关硬编码朗读文本。

## 文档和开源代码依据

- Microsoft [Selection and Focus Properties and Methods](https://learn.microsoft.com/en-us/windows/win32/winauto/selection-and-focus-properties-and-methods)：菜单高亮项目使用焦点事件，而非选中事件。
- Microsoft [NotifyWinEvent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-notifywinevent) 与 [AccessibleObjectFromEvent](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromevent)：事件应指向正确对象，客户端能够按事件参数解析对象并读取状态。
- .NET 10 [ToolStripItemAccessibleObject](https://github.com/dotnet/winforms/blob/v10.0.0/src/System.Windows.Forms/System/Windows/Forms/Controls/ToolStrips/ToolStripItem.ToolStripItemAccessibleObject.cs)、[ToolStripAccessibleObject](https://github.com/dotnet/winforms/blob/v10.0.0/src/System.Windows.Forms/System/Windows/Forms/Controls/ToolStrips/ToolStrip.ToolStripAccessibleObject.cs)、[ToolStripDropDownItem](https://github.com/dotnet/winforms/blob/v10.0.0/src/System.Windows.Forms/System/Windows/Forms/Controls/ToolStrips/ToolStripDropDownItem.cs)：核对角色、状态、UIA 焦点、MSAA 子项顺序以及菜单键盘展开路径。
- NVDA [IAccessibleHandler](https://github.com/nvaccess/nvda/blob/master/source/IAccessibleHandler/__init__.py)：核查其 MSAA 焦点处理与重复事件判断。保留同一对象身份，不通过虚假焦点或直接 TTS 迫使读屏重复输出。
- .NET [ToolStrip 的选择通知](https://github.com/dotnet/winforms/blob/release/10.0/src/System.Windows.Forms/System/Windows/Forms/Controls/ToolStrips/ToolStrip.cs)与 [ModalMenuFilter 的按键路由](https://github.com/dotnet/winforms/blob/release/10.0/src/System.Windows.Forms/System/Windows/Forms/Controls/ToolStrips/ToolStripManager.ModalMenuFilter.cs)：正常选择已发出 MSAA Focus，活动菜单无需夺取 HWND 焦点即可接收按键。
- NVDA [UIAHandler](https://github.com/nvaccess/nvda/blob/master/source/UIAHandler/__init__.py) 和 [UIA 对象的焦点过滤](https://github.com/nvaccess/nvda/blob/master/source/NVDAObjects/UIA/__init__.py)：MenuOpened 与 FocusChanged 的处理、待处理焦点去重和 HasKeyboardFocus 检查。
- 争渡 [官方 API](https://www.zdsr.com/docs/api/zdsr-api/) 提供语音和盲文输出接口；这类接口不能代替菜单的无障碍语义和焦点事件。
- C# 项目 [BaoYi-fake-nvdaControllerClient / ZDSRAPI.cs](https://github.com/sig-a11y/BaoYi-fake-nvdaControllerClient/blob/main/zdsr/ZDSRAPI.cs) 与 [AccessibleArena / PrismInterop.cs](https://github.com/JeanStiletto/AccessibleArena/blob/main/src/Core/Speech/PrismInterop.cs)：核对了争渡接口封装和多读屏输出适配。它们采用显式语音/盲文接口，没有可直接套用的 WinForms 菜单首项修复。本次没有引入这些库或复制其语音调用方式。
