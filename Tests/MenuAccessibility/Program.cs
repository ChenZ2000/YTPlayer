using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using YTPlayer.Utils;

internal static class Program
{
    private static readonly List<EventRecord> Events = new();
    private static readonly ConcurrentQueue<EventRecord> UiaEvents = new();
    private static readonly WinEventProc WinCallback = OnWinEvent;
    private static string _stage = "start";
    private static int _checks;
    private static readonly int ProcessId = Environment.ProcessId;
    private static readonly string Output = FindOutput();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string FindOutput()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "YTPlayer.csproj"))) return Path.Combine(directory.FullName, "artifacts", "menu-accessibility");
        throw new DirectoryNotFoundException("Run from the repository build output.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Directory.CreateDirectory(Output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 10));
        Control.CheckForIllegalCrossThreadCalls = true;
        int exit = 0;
        var hooks = new[] { SetWinEventHook(0x0004, 0x0007, IntPtr.Zero, WinCallback, (uint)ProcessId, 0, 0),
            SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, WinCallback, (uint)ProcessId, 0, 0) };
        AutomationFocusChangedEventHandler uiaHandler = (sender, _) =>
        {
            try
            {
                if (sender is AutomationElement element && element.Current.ProcessId == ProcessId)
                    RecordUia(element, "UIA.Focus");
            }
            catch (Exception) { }
        };
        AutomationEventHandler menuHandler = (sender, e) =>
        {
            try
            {
                if (sender is AutomationElement element && element.Current.ProcessId == ProcessId)
                    RecordUia(element, e.EventId == AutomationElement.MenuOpenedEvent ? "UIA.MenuOpened" : "UIA.MenuClosed");
            }
            catch (Exception) { }
        };
        Task.Run(() =>
        {
            Automation.AddAutomationFocusChangedEventHandler(uiaHandler);
            Automation.AddAutomationEventHandler(AutomationElement.MenuOpenedEvent, AutomationElement.RootElement, TreeScope.Subtree, menuHandler);
            Automation.AddAutomationEventHandler(AutomationElement.MenuClosedEvent, AutomationElement.RootElement, TreeScope.Subtree, menuHandler);
        }).GetAwaiter().GetResult();
        bool listening = true;
        async Task StopUiaAsync()
        {
            if (!listening) return;
            listening = false;
            // Unregister while the form's message loop still runs. A UIA callback
            // may be querying its provider and must not wait on a blocked UI thread.
            await Task.Run(() =>
            {
                Automation.RemoveAutomationFocusChangedEventHandler(uiaHandler);
                Automation.RemoveAutomationEventHandler(AutomationElement.MenuOpenedEvent, AutomationElement.RootElement, menuHandler);
                Automation.RemoveAutomationEventHandler(AutomationElement.MenuClosedEvent, AutomationElement.RootElement, menuHandler);
            });
        }
        using var form = new Form { Text = "易听 — 菜单无障碍验证", Size = new Size(600, 320), StartPosition = FormStartPosition.CenterScreen };
        var button = new Button { Text = "菜单测试", Dock = DockStyle.Fill };
        form.Controls.Add(button);
        bool probe = args.Contains("--probe-open-strategies");
        bool preview = !args.Contains("--test") && !probe;
        form.FormClosing += async (_, e) =>
        {
            if (!listening) return;
            e.Cancel = true;
            await StopUiaAsync();
            form.Close();
        };
        if (preview) ConfigurePreview(form, button);
        else form.Shown += async (_, _) =>
        {
            try
            {
                if (probe) await ProbeStrategiesAsync(button);
                else
                {
                    await ProbeAsync(form, button);
                    await RegressionAsync(form, button);
                }
                Console.WriteLine($"PASS: {_checks} assertions");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { form.Close(); }
        };
        try { Application.Run(form); }
        finally
        {
            foreach (var hook in hooks) if (hook != IntPtr.Zero) UnhookWinEvent(hook);
            var all = Events.Concat(UiaEvents).OrderBy(e => e.Milliseconds).ToArray();
            string name = preview ? "preview-events.json" : probe ? "open-strategies.json" : "events.json";
            File.WriteAllText(Path.Combine(Output, name), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            foreach (var group in all.Where(e => e.Kind == "MSAA.Focus" && e.Role == "MenuItem").GroupBy(e => e.Stage))
                Console.WriteLine($"{group.Key}: {string.Join(", ", group.Select(e => e.Name + " (child=" + e.ChildId + ")"))}");
        }
        return exit;
    }

    private static void RecordUia(AutomationElement element, string kind)
    {
        string stage = _stage;
        long time = Clock.ElapsedMilliseconds;
        var parent = TreeWalker.RawViewWalker.GetParent(element);
        UiaEvents.Enqueue(new EventRecord(stage, kind, 0, 0, element.Current.Name,
            element.Current.ControlType.ProgrammaticName, element.Current.HasKeyboardFocus.ToString(), time,
            string.Join(".", element.GetRuntimeId()), parent == null ? "" : string.Join(".", parent.GetRuntimeId())));
    }

    private static async Task ProbeStrategiesAsync(Control owner)
    {
        await Task.Delay(200);
        foreach (string strategy in new[] { "current", "select-only", "delayed-select", "focus-then-delay" })
        {
            using var menu = new ContextMenuStrip();
            menu.Items.Add("策略首项");
            menu.Items.Add("策略第二项");
            using var timer = new System.Windows.Forms.Timer { Interval = 32 };
            timer.Tick += (_, _) => { timer.Stop(); menu.Items[0].Select(); };
            if (strategy == "current") MenuNavigationBoundaryHelper.Attach(menu);
            else
            {
                _ = menu.Handle;
                _ = menu.AccessibilityObject;
                foreach (ToolStripItem item in menu.Items) _ = item.AccessibilityObject;
                menu.Opened += (_, _) => menu.BeginInvoke(() =>
                {
                    if (strategy == "select-only") menu.Items[0].Select();
                    else
                    {
                        if (strategy == "focus-then-delay") menu.Focus();
                        timer.Start();
                    }
                });
            }
            owner.Focus();
            await Task.Delay(100);
            _stage = strategy + ".open";
            menu.Show(owner, new Point(40, 40));
            await Task.Delay(600);
            _stage = strategy + ".down";
            typeof(ToolStrip).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, new object[] { Keys.Down });
            await Task.Delay(400);
            _stage = strategy + ".close";
            menu.Close();
            await Task.Delay(200);
        }
    }

    private static async Task ProbeAsync(Form form, Control owner)
    {
        await Task.Delay(200);
        using var menu = new ContextMenuStrip();
        var first = menu.Items.Add("唯一菜单项");
        ContextMenuAccessibilityHelper.PrimeForAccessibility(menu);
        MenuNavigationBoundaryHelper.Attach(menu);
        menu.Opened += (_, _) => ContextMenuAccessibilityHelper.EnsureFirstItemFocusedOnOpen(form, menu, "test", Console.WriteLine);
        _stage = "single.open";
        menu.Show(owner, new Point(40, 40));
        await Task.Delay(250);
        for (int attempt = 0; attempt < 40 && !UiaEvents.Any(e => e.Stage == _stage && e.Kind == "UIA.MenuOpened"); attempt++)
            await Task.Delay(50);
        await Task.Delay(60); // Let the initial cross-process UIA subscription finish warming up.
        Check(first.Selected, "single item selected");
        CheckFocus("single.open", "唯一菜单项", 1);
        CheckOpening("single.open");
        Check(UiaEvents.Any(e => e.Stage == "single.open" && e.Name == "唯一菜单项" && e.State == "True"), "UIA still exposes item focus");
        _stage = "single.down";
        RouteBoundaryKey(menu, Keys.Down);
        await Task.Delay(150);
        Check(first.Selected, "single item remains selected");
        CheckFocus("single.down", "唯一菜单项", 1, exactlyOne: true);
        CheckNavigation("single.down");
        _stage = "single.up";
        RouteBoundaryKey(menu, Keys.Up);
        await Task.Delay(150);
        CheckFocus("single.up", "唯一菜单项", 1, exactlyOne: true);
        CheckNavigation("single.up");
        _stage = "single.close";
        menu.Close();
        await Task.Delay(100);
        menu.Items.Add("第二项");
        _stage = "multiple.open";
        menu.Show(owner, new Point(40, 40));
        await Task.Delay(200);
        CheckFocus("multiple.open", "唯一菜单项", 1);
        CheckOpening("multiple.open");
        _stage = "multiple.next";
        RouteFrameworkKey(menu, Keys.Down);
        await Task.Delay(150);
        CheckFocus("multiple.next", "第二项", 2, exactlyOne: true);
        CheckNavigation("multiple.next");
        _stage = "multiple.wrap";
        RouteBoundaryKey(menu, Keys.Down);
        await Task.Delay(150);
        CheckFocus("multiple.wrap", "唯一菜单项", 1, exactlyOne: true);
        CheckNavigation("multiple.wrap");
        _stage = "multiple.close";
        menu.Close();
        await Task.Delay(100);
    }

    private static async Task RegressionAsync(Form form, Control owner)
    {
        using var menu = new ContextMenuStrip();
        // Disabled and separator children remain in MSAA, hidden children do not.
        // The event must use the provider's child order, not Items.IndexOf + 1.
        menu.Items.Add(new ToolStripMenuItem("隐藏项目") { Visible = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("不可用项目") { Enabled = false });
        var item = menu.Items.Add("实际可用项目");
        int invocations = 0;
        item.Click += (_, _) => invocations++;
        for (int i = 0; i < 5; i++) MenuNavigationBoundaryHelper.Attach(menu);
        _stage = "filtered.open";
        menu.Show(owner, new Point(40, 40));
        for (int i = 0; i < 5; i++) ContextMenuAccessibilityHelper.EnsureFirstItemFocusedOnOpen(form, menu, "filtered");
        await Task.Delay(200);
        Check(item.Selected, "skip hidden/disabled/separator items");
        CheckFocus(_stage, item.Text!, 3);
        Check(ItemFocus(_stage).Count() == 1, "one real selection despite repeated Attach/Ensure");
        _stage = "filtered.down";
        Check(RouteBoundaryKey(menu, Keys.Down).Handled, "single enabled item consumes Down");
        await Task.Delay(150);
        CheckFocus(_stage, item.Text!, 3, exactlyOne: true);
        _stage = "filtered.idle";
        await Task.Delay(180);
        Check(!ItemFocus(_stage).Any(), "no recurring focus announcements when idle");
        Check(invocations == 0, "opening/navigation never activates a command");
        _stage = "filtered.close";
        menu.Close();
        await Task.Delay(80);
        Check(!ItemFocus(_stage).Any(), "no stale item announcement after close");

        // A reader must not be sent back to the first item after the user moves.
        var second = menu.Items.Add("用户主动选择的第二项");
        _stage = "rapid.selection";
        menu.Show(owner, new Point(40, 40));
        second.Select();
        await Task.Delay(200);
        Check(second.Selected && !item.Selected, "deferred opening preserves a newer selection");
        Check(ItemFocus(_stage).All(e => e.Name == second.Text), "no delayed focus event for the old first item");
        _stage = "rapid.close";
        menu.Close();
        await Task.Delay(60);

        _stage = "immediate.close";
        menu.Show(owner, new Point(40, 40));
        menu.Close();
        await Task.Delay(150);
        Check(!ItemFocus(_stage).Any(), "closing before posted initialization cancels it");
        _stage = "rapid.reopen";
        menu.Show(owner, new Point(40, 40));
        menu.Close();
        menu.Show(owner, new Point(40, 40));
        await Task.Delay(200);
        Check(item.Selected, "reopened menu selects its first navigable item");
        CheckFocus(_stage, item.Text!, 3);
        Check(ItemFocus(_stage).Count() == 1, "old opening cannot announce in a new opening");
        menu.Close();
        await Task.Delay(80);

        using var disposable = new ContextMenuStrip();
        disposable.Items.Add("立即销毁");
        MenuNavigationBoundaryHelper.Attach(disposable);
        _stage = "dispose.pending";
        disposable.Show(owner, new Point(40, 40));
        disposable.Dispose();
        await Task.Delay(150);
        Check(!ItemFocus(_stage).Any(), "disposing cancels posted/timer callbacks");

        using var empty = new ContextMenuStrip();
        empty.Items.Add(new ToolStripMenuItem("没有可用命令") { Enabled = false });
        MenuNavigationBoundaryHelper.Attach(empty);
        _stage = "disabled.only";
        empty.Show(owner, new Point(40, 40));
        await Task.Delay(150);
        Check(!ItemFocus(_stage).Any(), "no focus fabricated for disabled-only menu");
        Check(!RouteBoundaryKey(empty, Keys.Down).Handled, "disabled-only menu is not forced to select a command");
        empty.Close();
        await Task.Delay(80);

        using var checkedMenu = new ContextMenuStrip();
        var checkedItem = new ToolStripMenuItem("已勾选的命令(&K)") { Checked = true, ShortcutKeys = Keys.Control | Keys.K };
        checkedMenu.Items.Add(checkedItem);
        MenuNavigationBoundaryHelper.Attach(checkedMenu);
        _stage = "checked.open";
        checkedMenu.Show(owner, new Point(40, 40));
        await Task.Delay(160);
        CheckFocus(_stage, "已勾选的命令(K)", 1);
        Check(ItemFocus(_stage).All(e => e.State.Contains("Checked")), "native checked semantics remain available to readers");
        Check(!string.IsNullOrWhiteSpace(checkedItem.AccessibilityObject.KeyboardShortcut), "access key remains exposed");
        Check(checkedItem.ShortcutKeys == (Keys.Control | Keys.K), "command shortcut is preserved");
        checkedMenu.Close();
        await Task.Delay(80);

        // Dynamic submenu: added after root registration, with one actionable child.
        using var parent = new ContextMenuStrip();
        MenuNavigationBoundaryHelper.Attach(parent);
        var submenu = new ToolStripMenuItem("动态子菜单");
        var leaf = submenu.DropDownItems.Add("子菜单唯一项目");
        parent.Items.Add(submenu);
        _stage = "nested.parent";
        parent.Show(owner, new Point(40, 40));
        await Task.Delay(180);
        Check(submenu.Selected, "parent initially selected");
        Check(!RouteBoundaryKey(parent, Keys.Right).Handled, "Right allows opening a submenu");
        _stage = "nested.open";
        submenu.ShowDropDown();
        await Task.Delay(180);
        Check(leaf.Selected, "child menu selects first item");
        CheckFocus(_stage, leaf.Text!, 1);
        Check(ItemFocus(_stage).All(e => e.Name == leaf.Text), "child focus is not replaced by parent notification");
        _stage = "nested.down";
        RouteBoundaryKey(submenu.DropDown, Keys.Down);
        await Task.Delay(120);
        CheckFocus(_stage, leaf.Text!, 1, exactlyOne: true);
        CheckNavigation(_stage);
        Check(!RouteBoundaryKey(submenu.DropDown, Keys.Left).Handled, "Left allows returning to parent");
        Check(RouteBoundaryKey(submenu.DropDown, Keys.Right).Handled, "Right at leaf stays in menu");
        _stage = "nested.close";
        parent.Close();
        await Task.Delay(100);

        _stage = "nested.immediate";
        parent.Show(owner, new Point(40, 40));
        submenu.Select();
        submenu.ShowDropDown();
        await Task.Delay(200);
        Check(leaf.Selected && submenu.DropDown.Visible, "opening child before deferred parent focus preserves child");
        var last = ItemFocus(_stage).LastOrDefault();
        Check(last?.Name == leaf.Text, "last focus remains in immediately opened submenu");
        parent.Close();
        await Task.Delay(80);

        using var menuBar = new MenuStrip();
        var top = new ToolStripMenuItem("测试菜单栏");
        var topLeaf = top.DropDownItems.Add("菜单栏唯一项目");
        menuBar.Items.Add(top);
        form.Controls.Add(menuBar);
        MenuNavigationBoundaryHelper.Attach(menuBar);
        _stage = "menubar.open";
        top.ShowDropDown();
        await Task.Delay(180);
        Check(topLeaf.Selected, "menu bar drop-down uses same accessibility lifecycle");
        CheckFocus(_stage, topLeaf.Text!, 1);
        Check(!RouteBoundaryKey(menuBar, Keys.Left).Handled, "top-level menu bar keeps standard Left navigation");
        Check(!RouteBoundaryKey(menuBar, Keys.Right).Handled, "top-level menu bar keeps standard Right navigation");
        top.HideDropDown();
        form.Controls.Remove(menuBar);
        await Task.Delay(80);
    }

    private static void ConfigurePreview(Form form, Button firstButton)
    {
        form.Text = "易听 — 菜单读屏验证（关闭窗口结束）";
        form.Size = new Size(650, 420);
        firstButton.Dock = DockStyle.None;
        firstButton.Text = "打开单项菜单";
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20) };
        form.Controls.Clear();
        form.Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "用 Tab 选择按钮，按空格打开。验证首项朗读、上下键、Esc 返回。", AutoSize = true, Margin = new Padding(3, 3, 3, 20) });
        var status = new Label { Text = "尚未执行菜单命令", AutoSize = true };
        foreach (var mode in new[] { "单项", "多项", "子菜单", "过滤不可用项" })
        {
            var trigger = mode == "单项" ? firstButton : new Button();
            trigger.Text = mode == "子菜单" ? "打开含子菜单的菜单" : "打开" + mode + "菜单";
            trigger.Size = new Size(270, 45);
            var menu = new ContextMenuStrip();
            if (mode == "过滤不可用项")
            {
                menu.Items.Add(new ToolStripMenuItem("隐藏项") { Visible = false });
                menu.Items.Add(new ToolStripMenuItem("不可用项") { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
            }
            if (mode == "子菜单")
            {
                var nested = new ToolStripMenuItem("进入子菜单");
                nested.DropDownItems.Add("子菜单中的唯一命令", null, (_, _) => status.Text = "已执行子菜单命令");
                menu.Items.Add(nested);
            }
            else
            {
                menu.Items.Add("查看歌曲信息", null, (_, _) => status.Text = "已执行查看歌曲信息");
                if (mode == "多项") menu.Items.Add("复制歌曲链接", null, (_, _) => status.Text = "已执行复制歌曲链接");
            }
            MenuNavigationBoundaryHelper.Attach(menu);
            trigger.ContextMenuStrip = menu;
            trigger.Click += (_, _) => { _stage = "preview." + mode; menu.Show(trigger, new Point(0, trigger.Height)); };
            form.FormClosed += (_, _) => menu.Dispose();
            panel.Controls.Add(trigger);
        }
        panel.Controls.Add(status);
        form.Shown += (_, _) => firstButton.Focus();
    }

    private static IEnumerable<EventRecord> ItemFocus(string stage) => Events.Where(e => e.Stage == stage && e.Kind == "MSAA.Focus" && e.Role == "MenuItem");

    private static void CheckOpening(string stage)
    {
        Check(Events.Count(e => e.Stage == stage && e.Kind == "0x0006" && e.ObjectId == -4 && e.Role == "MenuPopup") == 1,
            stage + ": exactly one native menu entry event");
        Check(ItemFocus(stage).Count() == 1, stage + ": first item has one real focus event, without delayed replay");
        Check(!Events.Any(e => e.Stage == stage && e.Kind == "MSAA.Focus" && e.Role == "MenuPopup"), stage + ": no extra popup-container focus");
        var opened = UiaEvents.Where(e => e.Stage == stage && e.Kind == "UIA.MenuOpened").ToArray();
        Check(opened.Length > 0 && opened.Select(e => e.RuntimeId).Distinct().Count() == 1,
            stage + ": bridged UIA menu-open events refer to one stable menu identity");
    }

    private static void CheckNavigation(string stage)
    {
        Check(!Events.Any(e => e.Stage == stage && e.Kind == "0x0006"), stage + ": navigation does not re-enter menu mode");
        Check(!Events.Any(e => e.Stage == stage && e.Kind == "MSAA.Focus" && e.Role == "MenuPopup"), stage + ": navigation does not focus the menu container");
        Check(!UiaEvents.Any(e => e.Stage == stage && (e.Kind == "UIA.MenuOpened" || e.Kind == "UIA.Focus" && e.Role == "ControlType.Menu")),
            stage + ": no UIA menu-open/container-focus replay while navigating");
    }

    private static void CheckFocus(string stage, string name, int childId, bool exactlyOne = false)
    {
        var records = ItemFocus(stage).ToArray();
        Check(exactlyOne ? records.Length == 1 : records.Length >= 1, stage + ": expected menu item focus notification(s), got " + records.Length);
        Check(records.All(e => e.Name == name && e.ObjectId == -4 && e.ChildId == childId && e.State.Contains("Focused")), stage + ": event resolves to the correct focused MSAA item");
    }

    private static KeyEventArgs RouteBoundaryKey(ToolStrip menu, Keys key)
    {
        var args = new KeyEventArgs(key);
        // Dispatch the real ToolStrip event to exercise registration and de-duplication,
        // rather than calling the helper's implementation directly.
        typeof(ToolStrip).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(menu, new object[] { args });
        return args;
    }

    private static void RouteFrameworkKey(ToolStrip menu, Keys key) =>
        typeof(ToolStrip).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(menu, new object[] { key });
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void OnWinEvent(IntPtr hook, uint id, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        string name = "", role = "", state = "";
        try
        {
            int hr = AccessibleObjectFromEvent(hwnd, objectId, childId, out var accessible, out var child);
            if (hr == 0 && accessible != null)
            {
                name = accessible.get_accName(child) ?? "";
                role = ((AccessibleRole)Convert.ToInt32(accessible.get_accRole(child))).ToString();
                state = ((AccessibleStates)Convert.ToInt32(accessible.get_accState(child))).ToString();
            }
            else name = "HRESULT=" + hr.ToString("X8");
        }
        catch (Exception ex) { name = ex.GetType().Name; }
        Events.Add(new EventRecord(_stage, id == 0x8005 ? "MSAA.Focus" : $"0x{id:X4}", objectId, childId, name, role, state, Clock.ElapsedMilliseconds));
    }
    private sealed record EventRecord(string Stage, string Kind, int ObjectId, int ChildId, string Name, string Role, string State,
        long Milliseconds = 0, string RuntimeId = "", string ParentRuntimeId = "");
    private delegate void WinEventProc(IntPtr hook, uint id, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromEvent(IntPtr hwnd, int objectId, int childId,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
}
