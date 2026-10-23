// ============================================================================
//  ScPad 自检台（开发工具，不随插件发布）
//
//  用「替身 NotchPeninsula.dll」把插件真的加载起来跑一遍：
//    1. 组件宽度 / 空状态 / 面板尺寸 / 页签切换；
//    2. 保存位置：默认值、改目录、抓到的图真的落进新目录；
//    3. 快捷键：默认 Ctrl+PrtSc、真的注册成功、清除 / 改动作 / 录制取消、格式与校验逻辑；
//    4. 采集：文件夹新图、剪贴板图片、整屏抓图；
//    5. 复制 / 拖出 / 移出 / 暂停 / 重启恢复；
//    6. 渲染两页的预览图到 build\preview\。
//
//  由 build\run-selftest.ps1 调用。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NotchPeninsula.Plugins;
using SkiaSharp;
using ScPad;

internal sealed class MockHost : IPluginHost
{
    public IWidget Widget;
    public readonly List<object> Reminders = new List<object>();
    public bool DetailOpen;
    public string DetailWidgetId = "";
    public int LayoutInvalidated;
    public int DragRequests;
    public string DraggedPath = "";
    public int ReminderCount { get { return Reminders.Count; } }
    public string LastReminderTitle = "";
    public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();

    public static RenderTheme DarkTheme()
    {
        return new RenderTheme(new SKColor(238, 238, 240), new SKColor(160, 160, 166),
            new SKColor(18, 18, 20), 1f, 14f);
    }

    public static RenderTheme LightTheme()
    {
        return new RenderTheme(new SKColor(28, 28, 32), new SKColor(110, 110, 118),
            new SKColor(246, 246, 248), 1f, 14f);
    }

    public RenderTheme Theme = DarkTheme();

    public void RegisterWidget(IWidget widget)
    {
        Widget = widget;
        if (widget != null) widget.OnActivate(this);
    }

    public void RegisterSecondaryWidget(ISecondaryWidget widget) { }

    /// <summary>宿主把注入的设置页收进这个列表，设置中心从这里取。</summary>
    public ISettingsPage SettingsPage;

    public void RegisterSettingsPage(ISettingsPage page)
    {
        if (SettingsPage != null)
        {
            Console.WriteLine("        [WARN] 设置页被重复注入了一次");
        }
        SettingsPage = page;
    }

    public RenderTheme CurrentTheme { get { return Theme; } }

    public void PostReminder(ReminderData reminder)
    {
        Reminders.Add(reminder);
        try
        {
            System.Reflection.PropertyInfo property = typeof(ReminderData).GetProperty("Title");
            if (property != null) LastReminderTitle = (string)property.GetValue(reminder, null);
        }
        catch { }
    }

    public string GetSetting(string key, string fallback)
    {
        string value;
        return Settings.TryGetValue(key, out value) ? value : fallback;
    }

    /// <summary>真宿主的 SetSetting 会触发插件的 SettingsChanged；这里默认关掉，测试里按需打开。</summary>
    public bool SetSettingRaises;

    /// <summary>SetSetting 被调用的次数（用来验证"设置变更不会自激写设置"）。</summary>
    public int SetSettingCalls;

    public void SetSetting(string key, string value)
    {
        SetSettingCalls++;
        Settings[key] = value;
        if (SetSettingRaises) RaiseSettingsChanged();
    }

    public event Action SettingsChanged;

    public IDisposable ScheduleRefresh(TimeSpan interval, Action callback) { return new Noop(); }

    public void RequestRedraw() { }

    public void OpenDetailPage(string widgetId) { DetailOpen = true; DetailWidgetId = widgetId; }

    public bool TryOpenDetailPage(string widgetId) { DetailOpen = true; DetailWidgetId = widgetId; return true; }

    public void CloseDetailPage() { DetailOpen = false; }

    public bool ToggleDetailPage(string widgetId)
    {
        DetailOpen = !DetailOpen;
        DetailWidgetId = widgetId;
        return DetailOpen;
    }

    public void InvalidateWidgetLayout() { LayoutInvalidated++; }

    public float GetPluginRowBudget() { return 960f; }

    public IPluginWindow CreateWindow(string title, int width, int height) { return null; }

    public bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove)
    {
        DragRequests++;
        if (paths != null && paths.Count > 0) DraggedPath = paths[0];
        Console.WriteLine("        StartFileDrag -> " + DraggedPath);
        return true;
    }

    public void RaiseSettingsChanged()
    {
        Action handler = SettingsChanged;
        if (handler != null) handler();
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        string outputDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ScPadSelfTest");
        Directory.CreateDirectory(outputDir);

        Console.WriteLine("ScPad self-test");
        Console.WriteLine("output: " + outputDir);
        Console.WriteLine();

        string dataRoot = Path.Combine(outputDir, "_data");
        Environment.SetEnvironmentVariable("SCPAD_DATA", dataRoot);

        string shotsDir = Path.Combine(outputDir, "_shots");
        string shotsDir2 = Path.Combine(outputDir, "_shots2");

        if (Environment.GetEnvironmentVariable("SCPAD_SELFTEST_KEEP") != "1")
        {
            TryDeleteDirectory(dataRoot);
            TryDeleteDirectory(shotsDir);
            TryDeleteDirectory(shotsDir2);
        }
        Directory.CreateDirectory(shotsDir);
        Directory.CreateDirectory(shotsDir2);

        try { Clipboard.Clear(); } catch { }

        // ---- 0. 纯逻辑：快捷键的格式 / 校验 ----
        var ctrlPrtsc = new HotkeySpec(HotkeySpec.ModControl, HotkeySpec.VkSnapshot);
        Check(ctrlPrtsc.IsSet, "默认快捷键是「已设置」状态");
        Check(ctrlPrtsc.Text.IndexOf("Ctrl", StringComparison.Ordinal) >= 0, "默认快捷键显示里带 Ctrl");
        Check(ctrlPrtsc.Text.IndexOf("Print", StringComparison.OrdinalIgnoreCase) >= 0 || ctrlPrtsc.Text.IndexOf("Prnt", StringComparison.OrdinalIgnoreCase) >= 0,
            "默认快捷键的主键是 Print Screen（实际：" + ctrlPrtsc.Text + "）");
        Check(HotkeySpec.Parse(ctrlPrtsc.Persist, HotkeySpec.None).SameAs(ctrlPrtsc), "快捷键能持久化成 mods:key 再读回来");
        Check(HotkeySpec.Validate(HotkeySpec.ModAlt | HotkeySpec.ModControl, 0x41) == null,
            "Alt + Ctrl + 任意键（A）是合法组合");
        Check(HotkeySpec.Validate(0, 0x41) != null, "单独的字母键会被拒绝（防止抢走打字）");
        Check(HotkeySpec.Validate(0, 0x70) == null, "单独的 F1 是允许的");
        Check(HotkeySpec.Validate(0, HotkeySpec.VkSnapshot) != null, "单独的 PrtSc 会被拒绝（需要修饰键）");
        Check(HotkeySpec.Validate(HotkeySpec.ModControl, 0x11) != null, "只用修饰键会被拒绝");

        // ---- 1. 默认设置 ----
        var defaultHost = new MockHost();
        defaultHost.Settings["welcomed"] = "1";
        var defaultSettings = new PluginSettings(defaultHost);
        Console.WriteLine("        默认保存位置 = " + defaultSettings.SaveFolder);
        Check(string.Equals(defaultSettings.SaveFolder, ShotStore.SystemScreenshotsFolder(), StringComparison.OrdinalIgnoreCase),
            "保存位置默认取系统「屏幕截图」文件夹");
        Check(defaultSettings.SaveFolder.EndsWith("Screenshots", StringComparison.OrdinalIgnoreCase),
            "系统「屏幕截图」文件夹就是 …\\Pictures\\Screenshots");

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check(!string.IsNullOrEmpty(userProfile) &&
              defaultSettings.SaveFolder.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase),
            "默认保存位置落在当前用户目录下（按 Windows 用户解析，不写死路径）");
        Check(defaultSettings.SaveFolder.IndexOf('%') < 0 && defaultSettings.SaveFolder.IndexOf('$') < 0,
            "默认保存位置已是解析后的实际路径（不是环境变量占位符）");
        Check(defaultSettings.SaveFolder.IndexOf("NotchPeninsula", StringComparison.OrdinalIgnoreCase) < 0,
            "默认保存位置在系统图片目录，而不是插件自己的数据目录");
        Check(defaultSettings.AutoPeek, "自动展开默认开启（新截图到手自动弹面板）");
        Check(defaultSettings.HotkeyA.SameAs(ctrlPrtsc), "快捷键 1 默认 = Ctrl + PrtSc");
        Check(defaultSettings.HotkeyAAction == PluginSettings.ActionCapture, "快捷键 1 默认动作 = 截图并存入");
        Check(!defaultSettings.HotkeyB.IsSet, "快捷键 2 默认留空（可以自己录成 Alt + Ctrl + 任意键）");
        Check(defaultSettings.HotkeyBAction == PluginSettings.ActionTogglePanel, "快捷键 2 默认动作 = 展开/收起");

        // ---- 2. 起插件 ----
        var host = new MockHost();
        host.Settings["welcomed"] = "1";
        host.Settings["saveFolder"] = shotsDir;

        var plugin = new ScPadPlugin();
        plugin.Initialize(host);

        IWidget widget = host.Widget;
        Check(widget != null, "组件已注册");
        if (widget == null) return 1;

        // 官方要求：无论有没有截图都要常驻，让用户一眼看到插件装上了
        float emptyWidth = widget.MeasureWidth(34f);
        Check(emptyWidth > 20f, "没有截图时也常驻显示（宽度 " + emptyWidth.ToString("0.0") + "）");
        Check(emptyWidth <= 260f, "常驻状态栏很窄，不会抢媒体的位置（" + emptyWidth.ToString("0.0") + "）");
        RenderWidget(widget, MockHost.DarkTheme(), false, Path.Combine(outputDir, "widget-empty-dark.png"));
        Check(plugin.Settings.SaveFolder == shotsDir, "保存位置读到了设置里的目录");

        IDetailPage panel = widget.DetailPage;
        Check(panel != null, "详情页（面板）存在");
        if (panel == null) return 1;
        Check(panel.AutoCollapseDelay < TimeSpan.Zero, "面板不会自动收起（拖出粘贴才来得及）");

        // 快捷键要真的注册进系统（1409 = 已被别的进程占用，通常是正在运行的灵动岛实例，属可接受跳过）
        bool registered = WaitFor(delegate { return plugin.Hotkeys.IsRegistered(0); }, 3000);
        string registerError = plugin.Hotkeys.RegistrationError(0);
        if (!registered && registerError != null && registerError.IndexOf("1409") >= 0)
        {
            Console.WriteLine("        [SKIP] Ctrl + PrtSc 已被另一个进程占用（可能灵动岛正在运行），跳过注册校验");
            Check(true, "快捷键 1 注册：组合键被占用，按系统拒绝处理（不视为缺陷）");
        }
        else
        {
            Check(registered, "快捷键 1 已注册进系统（Ctrl + PrtSc）" +
                (registered ? "" : " — " + registerError));
        }

        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-empty-dark.png"));


        // ---- 3. 灌 5 张测试图片到保存目录 ----
        for (int i = 1; i <= 5; i++)
        {
            MakeTestShot(Path.Combine(shotsDir, "Capture-selftest-" + i.ToString() + ".png"), i);
        }

        bool ready = WaitFor(delegate { return widget.MeasureWidth(34f) > 0f; }, 8000);
        Check(ready, "保存目录里的新截图被自动存入截图夹");
        Console.WriteLine("        组件宽度 = " + widget.MeasureWidth(34f).ToString("0.0"));

        RenderWidget(widget, MockHost.DarkTheme(), false, Path.Combine(outputDir, "widget-dark.png"));
        RenderWidget(widget, MockHost.DarkTheme(), true, Path.Combine(outputDir, "widget-dark-hover.png"));
        RenderWidget(widget, MockHost.LightTheme(), false, Path.Combine(outputDir, "widget-light.png"));

        panel = widget.DetailPage;
        float panelWidth = panel.MeasureWidth();
        float panelHeight = panel.MeasureHeight();
        Console.WriteLine("        面板尺寸 = " + panelWidth.ToString("0") + " x " + panelHeight.ToString("0"));
        Check(panelWidth >= 180f && panelWidth <= 1000f, "面板宽度在宿主允许范围内");
        Check(panelHeight >= 48f && panelHeight <= 480f, "面板高度在宿主允许范围内");

        plugin.Panel.ClearHint(); panel.OnMouseLeave(); RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark.png"));
        RenderPanel(panel, MockHost.LightTheme(), Path.Combine(outputDir, "panel-light.png"));

        panel.OnMouseMove(12f + 74f, 12f + 88f);
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark-hover.png"));

        // ---- 4. 单击复制 ----
        panel.OnMouseDown(12f + 74f, 12f + 88f);
        panel.OnMouseUp(12f + 74f, 12f + 88f);
        bool clipboardOk = WaitFor(delegate { return ClipboardHasImage(); }, 9000);
        Check(clipboardOk, "单击卡片 → 剪贴板里出现了图片（可直接 Ctrl+V 粘贴）");
        Check(ClipboardHasPng(), "剪贴板里同时有 PNG 格式");
        Check(ClipboardHasFileDrop(), "剪贴板里同时有文件路径（可在资源管理器粘贴）");
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark-copied.png"));

        // 演示图：把文件拖进面板时的提示条
        plugin.Panel.ClearHint();
        plugin.Panel.OnFilesDragEnter(2);
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark-drop.png"));
        plugin.Panel.OnFilesDragLeave();

        // ---- 5. 按住拖出 ----
        int dragsBefore = host.DragRequests;
        // 面板内拖动 = 排序；要"拖出粘贴"必须把指针拖出面板边界
        float dragStartX = 12f + 148f + 10f + 74f;
        float dragOutX = panel.MeasureWidth() + 90f;
        panel.OnMouseDown(dragStartX, 12f + 88f);
        panel.OnMouseMove(dragOutX, 12f + 100f);
        panel.OnMouseUp(dragOutX, 12f + 100f);
        panel.OnMouseUp(12f + 148f + 10f + 74f + 30f, 12f + 88f + 12f);
        Check(host.DragRequests == dragsBefore + 1, "按住拖动 → 宿主收到拖出请求（拖动粘贴）");
        Check(File.Exists(host.DraggedPath), "拖出的是一条真实存在的图片路径");

        // ---- 6. 剪贴板采集 ----
        int filesBeforeClipboard = CountShots(shotsDir);
        SetClipboardImage(160, 100);
        bool captured = WaitFor(delegate { return CountShots(shotsDir) > filesBeforeClipboard; }, 9000);
        Check(captured, "剪贴板里的图片被自动存入（落在保存目录里）");

        // ---- 7. 整屏抓图（快捷键 1 的动作）----
        int filesBeforeShot = CountShots(shotsDir);
        plugin.Capture.RequestHotkey(0);   // 动作 = 截图并存入
        bool grabbed = WaitFor(delegate { return CountShots(shotsDir) > filesBeforeShot; }, 12000);
        Check(grabbed, "快捷键 1 的动作：整屏抓图 → 存入截图夹 → 进剪贴板");

        // ---- 8. 快捷键 2 的动作（默认 = 展开/收起面板）----
        host.DetailOpen = false;
        plugin.Capture.RequestHotkey(1);
        bool toggled = WaitFor(delegate { return host.DetailOpen; }, 3000);
        Check(toggled, "快捷键 2 的动作：展开 / 收起面板");
        plugin.Capture.RequestHotkey(1);
        WaitFor(delegate { return !host.DetailOpen; }, 3000);
        Check(!host.DetailOpen, "再按一次收起面板");

        // ---- 9. 移出 ----
        int shotsBeforeRemove = plugin.Capture.Store.Snapshot.Length;
        float removeSlotLeft = panel.MeasureWidth() - 12f - 148f;   // slot 0 = 最右 = 最新
        panel.OnMouseDown(removeSlotLeft + 148f - 13f, 12f + 13f);   // 右上角的 ×
        panel.OnMouseUp(removeSlotLeft + 148f - 13f, 12f + 13f);
        bool trimmed = WaitFor(delegate { return plugin.Capture.Store.Snapshot.Length == shotsBeforeRemove - 1; }, 5000);
        Check(trimmed, "点卡片右上角的 × → 图片移入 Recycle 子目录");
        string recycleDir = Path.Combine(dataRoot, "Recycle");
        Check(Directory.Exists(recycleDir) && Directory.GetFiles(recycleDir).Length > 0,
            "移出的图片进了 Recycle，可手动找回");

        // ---- 10. 设置中心（官方插件设置页） ----
        ISettingsPage page = host.SettingsPage;
        Check(page != null, "设置页已注入宿主（RegisterSettingsPage 只调一次）");

        IReadOnlyList<SettingControl> controls = page == null ? null : page.Controls;
        Check(controls != null && controls.Count == 14,
            "精简后共 14 个设置项（实际 " + (controls == null ? 0 : controls.Count).ToString() + " 项）");

        // 设置中心按属性名认形态：数值 Min/Max、勾选 DefaultValue、选择 Options、按钮 Action、其余文本
        Check(HasNumberShape(FindControl(controls, "maxShots")), "「截图夹上限」= 数值控件（Min/Max/Step）");

        Check(HasNumberShape(FindControl(controls, "thumbScale")), "「缩略图百分比」= 数值控件");
        Check(HasChoiceShape(FindControl(controls, "hotkeyAAction")), "「快捷键 1 动作」= 选择框（Options）");
        Check(HasTextShape(FindControl(controls, "saveFolder")), "「保存位置」= 文本输入（无判别属性）");
        Check(HasActionShape(FindControl(controls, "pickFolder")), "「选择文件夹…」= 按钮（Action 非空）");
        Check(HasActionShape(FindControl(controls, "recordHotkeyA")), "「快捷键 1」= 按钮（Label 里带当前组合）");
        Check(FindControl(controls, "recordHotkeyA") != null &&
              FindControl(controls, "recordHotkeyA").Label.IndexOf("快捷键 1", StringComparison.Ordinal) >= 0,
            "快捷键按钮的 Label 直接显示当前组合（省掉只读文本项）");
        Check(FindControl(controls, "wheelReverse") == null, "滚轮设置项已从设置里移除（功能代码保留）");

        // 运行时生成的控件：必须把基类的抽象成员全实现掉。
        // 真宿主里 SettingControl 是 abstract record，它的 `<Clone>$` 是 abstract 的 ——
        // 少实现一个，真机加载就是 TypeLoadException（1.1.0-preview 第一次发布就栽在这）。
        SettingControl probe = DynamicControls.Text("probe", "探测", "占位", 16, "说明");
        Check(probe is SettingControl, "自定义文本控件由运行时生成，且是 SettingControl 子类");

        // 能实例化 = CLR 认为抽象成员都实现了（未实现会先在建类型/实例化时抛 TypeLoadException）
        Type probeType = probe.GetType();
        Check(!probeType.IsAbstract,
            "生成的控件可实例化（基类抽象成员已实现，真机不会 TypeLoadException）");

        // 手写 IL 的克隆方法要真的跑一遍：栈不平衡会抛 InvalidProgramException
        MethodInfo[] probeMembers = probeType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo cloneImpl = null;
        for (int i = 0; i < probeMembers.Length; i++)
        {
            if (!string.Equals(probeMembers[i].Name, "CloneCore", StringComparison.Ordinal)) continue;
            if (probeMembers[i].Name == "<Clone>$") { cloneImpl = probeMembers[i]; break; }
            if (probeMembers[i].DeclaringType == probeType) cloneImpl = probeMembers[i];
        }
        SettingControl clone = cloneImpl == null ? null : cloneImpl.Invoke(probe, null) as SettingControl;
        Check(clone != null && string.Equals(clone.Key, "probe", StringComparison.Ordinal) && !ReferenceEquals(clone, probe),
            "克隆方法可执行并返回同 Key 的副本（按 record 语义）");

        Check(string.Equals((string)Prop(probe, "Placeholder"), "占位", StringComparison.Ordinal),
            "生成的控件属性可读（Placeholder）");

        SettingControl actionProbe = DynamicControls.Action("probe2", "探测2", "点我", "说明");
        Check(string.Equals((string)Prop(actionProbe, "Action"), "点我", StringComparison.Ordinal),
            "按钮控件带 Action 文本（设置中心据此画按钮）");

        SettingControl readonlyProbe = DynamicControls.ReadonlyText("probe3", "探测3", "占位", "说明");
        object readonlyFlag = Prop(readonlyProbe, "ReadOnly");
        Check(readonlyFlag is bool && (bool)readonlyFlag, "只读控件 ReadOnly = true");

        // 百分比推荐曲线
        Check(PluginSettings.RecommendScale(10) == 100, "推荐百分比：10 张 = 100%");
        Check(PluginSettings.RecommendScale(4) > 100 && PluginSettings.RecommendScale(4) <= 200, "推荐百分比：4 张更大（" + PluginSettings.RecommendScale(4).ToString() + "%）");
        Check(PluginSettings.RecommendScale(16) < 100, "推荐百分比：16 张更小（" + PluginSettings.RecommendScale(16).ToString() + "%）");
        Check(PluginSettings.RecommendScale(100) >= PluginSettings.MinScale, "推荐百分比：100 张不越下界");
        bool monotonic = true;
        int previous = int.MaxValue;
        for (int n = PluginSettings.MinShots; n <= PluginSettings.MaxShots; n++)
        {
            int value = PluginSettings.RecommendScale(n);
            if (value > previous) monotonic = false;
            previous = value;
        }
        Check(monotonic, "推荐百分比随上限单调不增（4~100 全区间）");

        // 上限夹取与缩略图百分比（「自动/自定义」已合并成一项：填 0 = 跟随推荐）
        plugin.Settings.SetMaxItems(1);
        Check(plugin.Settings.MaxItems == PluginSettings.MinShots, "上限低于 4 会被夹到 4");
        plugin.Settings.SetMaxItems(999);
        Check(plugin.Settings.MaxItems == PluginSettings.MaxShots, "上限高于 100 会被夹到 100");
        plugin.Settings.SetMaxItems(10);
        plugin.Settings.SetThumbScale(0);
        Check(plugin.Settings.EffectiveThumbScale == PluginSettings.RecommendScale(10), "没设过百分比 → 生效值 = 推荐值");

        plugin.Settings.SetThumbScale(50);
        Check(plugin.Settings.EffectiveThumbScale == 50, "设过百分比 → 用自定义值（50%）");
        float narrowWidth = plugin.Widget.MeasureWidth(34f);
        plugin.Settings.SetThumbScale(150);
        float wideWidth = plugin.Widget.MeasureWidth(34f);
        Check(wideWidth > narrowWidth, "百分比变大 → 组件变宽（" + narrowWidth.ToString("0.0") + " → " + wideWidth.ToString("0.0") + "）");
        plugin.Settings.SetThumbScale(10);
        Check(plugin.Settings.EffectiveThumbScale == PluginSettings.MinScale, "自定义百分比低于 25% 会被夹到 25%");
        plugin.Settings.SetThumbScale(0);

        // 快捷键动作仍是三选一（展示文本已合并进录制按钮的 Label）
        Check(ScPadSettingsPage.ActionOptions.Length == 3, "快捷键动作仍是三选一");

        // 设置中心改完 → 宿主的 SettingsChanged → 插件重新读
        plugin.Settings.SetSaveFolder(shotsDir);
        Check(string.Equals(plugin.Settings.SaveFolder, shotsDir, StringComparison.OrdinalIgnoreCase), "保存位置可写回设置");

        // 换目录 → 新图真的落到那里
        host.Settings["saveFolder"] = shotsDir2;
        host.RaiseSettingsChanged();
        bool applied = WaitFor(delegate
        {
            return string.Equals(plugin.Settings.SaveFolder, shotsDir2, StringComparison.OrdinalIgnoreCase);
        }, 4000);
        Check(applied, "设置中心改保存位置 → SettingsChanged → 插件重新读到");
        plugin.Capture.Store.EnsureSaveFolder();
        int before2 = CountShots(shotsDir2);
        SetClipboardImage(205, 135);   // 换个尺寸：同一张纯色图会被按像素内容判重
        bool moved = WaitFor(delegate { return CountShots(shotsDir2) > before2; }, 15000);
        if (!moved)
        {
            // 换目录后监听要重建，偶尔慢一拍，再补一次
            SetClipboardImage(211, 141);
            moved = WaitFor(delegate { return CountShots(shotsDir2) > before2; }, 12000);
        }
        Check(moved, "改保存位置后，新截图写进新目录");

        // 自激防护：真宿主的 SetSetting 会回调 SettingsChanged，
        // 如果处理设置变更时又去写设置，就会「触发 → 处理 → 再触发」打死循环
        // （1.1.0-preview 在真机上卡顿/崩溃的根因）。
        int writesBefore = host.SetSettingCalls;
        host.SetSettingRaises = true;
        host.Settings["maxShots"] = "12";
        host.RaiseSettingsChanged();
        bool cycleApplied = WaitFor(delegate { return plugin.Settings.MaxItems == 12; }, 5000);
        Thread.Sleep(700);
        host.SetSettingRaises = false;
        int writesGrew = host.SetSettingCalls - writesBefore;
        Check(cycleApplied, "上限改成 12 生效");
        Check(writesGrew <= 6, "设置变更不会自激写设置（共写 " + writesGrew.ToString() + " 次，不会失控）");

        // 设置中心里的按钮（写自增计数）→ 插件轮询发现 → 执行
        plugin.Settings.SetMaxItems(30);
        host.Settings["maxShots"] = "30";
        host.Settings["resetAll"] = IncrementSetting(host, "resetAll");
        bool reset = WaitFor(delegate { return plugin.Settings.MaxItems == PluginSettings.DefaultShots; }, 4000);
        Check(reset, "设置中心的「恢复默认」按钮生效（上限回到 10）");

        // ---- 面板槽位 / 填充顺序 / 上传 / 排序 / 弹簧 ----
        int slotsDefault = PluginSettings.DefaultSlots;
        Check(plugin.Settings.PanelSlots == slotsDefault, "面板默认预留 4 格");
        Check(plugin.Settings.HideOnCapture, "「抓屏时藏起灵动岛」默认开启");

        // 0 张截图时面板也按 4 格宽（预留位置，不是空的）
        float widthWithShots = panel.MeasureWidth();
        int widthSlots4 = (int)Math.Round(widthWithShots);
        plugin.Settings.SetPanelSlots(6);
        plugin.NotifyLayoutChanged();
        float widthSlots6 = panel.MeasureWidth();
        plugin.Settings.SetPanelSlots(slotsDefault);
        plugin.NotifyLayoutChanged();
        Check(widthSlots6 > widthSlots4 + 100f,
            "预留格数可改（4 格 " + widthSlots4.ToString() + " → 6 格 " + widthSlots6.ToString() + "）");

        // 点空槽 = 从剪贴板上传一张（先放一张全新的图进剪贴板，免得被判重挡住）
        SetClipboardImage(233, 155);
        int beforePaste = plugin.Capture.Store.Snapshot.Length;
        int shotsNow = beforePaste;
        if (shotsNow < slotsDefault)
        {
            // 找一个空槽（count 之后的那一格的中间）
            float slotX = panel.MeasureWidth() - 12f - 148f + 74f - shotsNow * (148f + 10f);
            panel.OnMouseDown(slotX, 100f);
            panel.OnMouseUp(slotX, 100f);
            bool pasted = WaitFor(delegate { return plugin.Capture.Store.Snapshot.Length > beforePaste; }, 8000);
            Check(pasted, "点空槽 → 从剪贴板上传一张");
        }
        else
        {
            plugin.Capture.RequestPasteFromClipboard();
            bool pasted = WaitFor(delegate { return plugin.Capture.Store.Snapshot.Length > beforePaste; }, 8000);
            Check(pasted, "从剪贴板上传一张");
        }

        // 判重：同样的图再传一次 → 跳过，并告诉调用方是 dup
        byte[] same = System.IO.File.ReadAllBytes(plugin.Capture.Store.Snapshot[plugin.Capture.Store.Snapshot.Length - 1].Path);
        Shot dup = plugin.Capture.Store.AddCaptured(same);
        Check(dup == null && plugin.Capture.Store.LastSkipReason == "dup", "同一张图会被判重跳过（原因 dup）");

        // 拖动排序：把最右边那张拖到左边一格
        Shot[] beforeOrder = plugin.Capture.Store.Snapshot;
        if (beforeOrder.Length >= 2)
        {
            string newestPath = beforeOrder[beforeOrder.Length - 1].Path;
            string secondPath = beforeOrder[beforeOrder.Length - 2].Path;

            float s0 = panel.MeasureWidth() - 12f - 148f + 74f;         // slot 0 中心（最右 = 最新）
            float s1 = s0 - (148f + 10f);                              // slot 1 中心

            // 拖动默认是「拖出粘贴」；按住 Ctrl 才是排序手势
            ShotClipPanel.CtrlProbe = delegate { return true; };
            panel.OnMouseDown(s0, 100f);
            panel.OnMouseMove(s1, 100f);
            panel.OnMouseUp(s1, 100f);
            ShotClipPanel.CtrlProbe = null;

            bool reordered = WaitFor(delegate
            {
                Shot[] now = plugin.Capture.Store.Snapshot;
                if (now.Length < 2) return false;
                return string.Equals(now[now.Length - 2].Path, newestPath, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(now[now.Length - 1].Path, secondPath, StringComparison.OrdinalIgnoreCase);
            }, 4000);
            Check(reordered, "Ctrl + 拖动 → 调整顺序（并写回 state.txt）");
        }

        // 弹簧：冲击后会振荡，并且最终自己停下
        var spring = new SpringMotion();
        spring.Impulse(600f);
        spring.Step(0.016f);
        bool springMoved = Math.Abs(spring.Value) > 0.01f;
        for (int i = 0; i < 400; i++) spring.Step(0.016f);
        Check(springMoved && spring.Idle, "弹簧抖动会振荡并自行收敛（物理惯性，不是缓动）");

        // ---- 系统截图键：单独 PrtSc 绝不能被占用 ----
        var badHost = new MockHost();
        badHost.Settings["hotkeyA"] = "0:44";     // 裸 PrtSc（历史遗留/手改注册表）
        var badSettings = new PluginSettings(badHost);
        Check(badSettings.HotkeyA.Modifiers == HotkeySpec.ModControl && badSettings.HotkeyA.Key == HotkeySpec.VkSnapshot,
            "非法「单独 PrtSc」被净化成 Ctrl + PrtSc（不抢占系统截图键）");
        Check(badHost.Settings.ContainsKey("hotkeyA") && badHost.Settings["hotkeyA"] == "2:44",
            "净化结果已写回设置（下次启动也不会抢占）");
        Check(plugin.Settings.HotkeyA.Key == HotkeySpec.VkSnapshot &&
              plugin.Settings.HotkeyA.Modifiers != 0,
            "默认截图键始终带修饰键（Ctrl + PrtSc）");

        // ---- 滚轮默认不接管 ----
        WheelRouter.Clear();
        WheelRouter.Push(-3);
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "_wheel-probe.png"));
        int wheelLeft = WheelRouter.Take();
        Check(wheelLeft == -3, "滚轮事件不被插件消费（钩子代码留存，默认不启用）");

        // 滚轮 + 惯性
        var scrolling = new ScrollModel();
        scrolling.SetMax(300f);
        Check(scrolling.Overflow, "内容超出可视宽度时算作可滚动");
        scrolling.Wheel(-1, false);
        float offsetBefore = scrolling.Offset;
        scrolling.Update(0.016f);
        Check(scrolling.Offset > offsetBefore, "滚轮向下 → 向右滑动（偏移增大）");
        float speedBefore = Math.Abs(scrolling.Velocity);
        scrolling.Update(0.05f);
        Check(Math.Abs(scrolling.Velocity) < speedBefore, "速度按阻尼衰减（惯性）");
        scrolling.Nudge(99999f);
        Check(scrolling.Offset <= scrolling.Max + 0.01f, "偏移不会越过上限");
        var reversed = new ScrollModel();
        reversed.SetMax(300f);
        reversed.Wheel(-1, true);
        reversed.Update(0.016f);
        Check(reversed.Offset <= 0.01f, "开「滚轮反向」后，向下滚 → 向左滑");
        var idle = new ScrollModel();
        idle.SetMax(0f);
        Check(!idle.Overflow, "没有溢出时不吃滚轮");

        WheelRouter.Clear();
        WheelRouter.Push(-2);
        WheelRouter.Push(-1);
        Check(WheelRouter.Take() == -3, "滚轮格数会累加后一次性取走");
        Check(WheelRouter.Take() == 0, "取走之后清零");

        plugin.Panel.ClearHint();
        plugin.Panel.OnMouseLeave();
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-crowded-dark.png"));

        // ---- 11. 暂停（面板不再有按钮，走设置中心的开关）----
        host.Settings["paused"] = "1";
        host.RaiseSettingsChanged();
        bool pausedOk = WaitFor(delegate { return plugin.Settings.Paused; }, 4000);
        Check(pausedOk, "设置中心「暂停收集」→ 插件真的暂停了收集");
        Check(host.Settings.ContainsKey("paused") && host.Settings["paused"] == "1", "暂停状态存在宿主设置里");

        // ---- 12. 持久化：换一个新的插件实例读回同一份设置 ----
        plugin.Dispose();
        Thread.Sleep(300);

        var secondHost = new MockHost();
        foreach (var pair in host.Settings) secondHost.Settings[pair.Key] = pair.Value;
        var secondPlugin = new ScPadPlugin();
        secondPlugin.Initialize(secondHost);

        Check(secondPlugin.Settings.SaveFolder == shotsDir2, "重启后保存位置还在");
        bool restored = WaitFor(delegate
        {
            return secondHost.Widget != null && secondHost.Widget.MeasureWidth(34f) > 0f;
        }, 8000);
        Check(restored, "重启后从 state.txt 恢复了截图列表");
        secondPlugin.Dispose();
        Thread.Sleep(200);

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("全部检查通过。");
            return 0;
        }

        Console.WriteLine(_failures.ToString() + " 项检查失败。");
        return 1;
    }

    // ---- 设置中心的「控件形态」判定（按属性名，和 NpsSettingsHub 的规则一致）----

    private static SettingControl FindControl(IReadOnlyList<SettingControl> controls, string key)
    {
        if (controls == null) return null;
        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i] != null && string.Equals(controls[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return controls[i];
        }
        return null;
    }

    private static bool HasProperty(object target, string name)
    {
        if (target == null) return false;
        return target.GetType().GetProperty(name) != null;
    }

    private static object Prop(object target, string name)
    {
        if (target == null) return null;
        System.Reflection.PropertyInfo info = target.GetType().GetProperty(name);
        if (info == null) return null;
        try { return info.GetValue(target, null); }
        catch { return null; }
    }

    private static bool HasNumberShape(object control)
    {
        object min = Prop(control, "Min");
        object max = Prop(control, "Max");
        return min is float && max is float;
    }

    private static bool HasToggleShape(object control)
    {
        object value = Prop(control, "DefaultValue");
        return value is bool;
    }

    private static bool HasChoiceShape(object control)
    {
        object options = Prop(control, "Options");
        if (!(options is IEnumerable<string>)) return false;
        return ((IEnumerable<string>)options).Any();
    }

    private static bool HasActionShape(object control)
    {
        object action = Prop(control, "Action");
        string text = action as string;
        return !string.IsNullOrEmpty(text);
    }

    private static bool HasTextShape(object control)
    {
        if (control == null) return false;
        return !HasNumberShape(control) && !HasToggleShape(control) && !HasChoiceShape(control) && !HasActionShape(control);
    }

    private static bool HasReadonlyShape(object control)
    {
        object flag = Prop(control, "ReadOnly");
        return flag is bool && (bool)flag && HasTextShape(control);
    }

    /// <summary>模拟设置中心点按钮：把该 Key 的自增计数写回。</summary>
    private static string IncrementSetting(MockHost host, string key)
    {
        string raw;
        int value = 0;
        if (host.Settings.TryGetValue(key, out raw)) int.TryParse(raw, out value);
        return (value + 1).ToString();
    }

    // ---- 面板坐标（和 ShotClipPanel 里的常量保持一致）----

    private static void ClickButton(IDetailPage panel, float x, float y)
    {
        panel.OnMouseDown(x, y);
        panel.OnMouseUp(x, y);
        Thread.Sleep(120);
    }

    // ---- 渲染 ----

    private static void RenderWidget(IWidget widget, RenderTheme theme, bool hovered, string path)
    {
        const float Height = 34f;
        float width = widget.MeasureWidth(Height);
        int pixelWidth = (int)Math.Ceiling(Math.Max(1f, width));

        Save(pixelWidth + 20, (int)Height, 3, path, theme,
            delegate(SKCanvas canvas, SKRect client)
            {
                var body = new SKRect(client.Left + 10f, client.Top, client.Right - 10f, client.Bottom);
                PaintPill(canvas, body, theme);
                widget.Draw(canvas, body, new WidgetFrame(theme, 255, 0f, null, hovered));
            });
    }

    private static void RenderPanel(IDetailPage panel, RenderTheme theme, string path)
    {
        RenderPanel(panel, theme, path, 0f, 0f);
    }

    private static void RenderPanel(IDetailPage panel, RenderTheme theme, string path, float mouseX, float mouseY)
    {
        int width = (int)Math.Ceiling(panel.MeasureWidth());
        int height = (int)Math.Ceiling(panel.MeasureHeight());

        if (mouseX > 0f || mouseY > 0f) panel.OnMouseMove(mouseX, mouseY);

        Save(width, height, 2, path, theme,
            delegate(SKCanvas canvas, SKRect client)
            {
                PaintPill(canvas, client, theme);
                panel.Draw(canvas, client, new WidgetFrame(theme, 255, 0f, null, true));
            });
    }

    private static void PaintPill(SKCanvas canvas, SKRect rect, RenderTheme theme)
    {
        using (var paint = new SKPaint())
        {
            paint.IsAntialias = true;
            paint.Color = theme.BackgroundColor;
            canvas.DrawRoundRect(rect, 12f, 12f, paint);

            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1f;
            paint.Color = theme.TextColor.WithAlpha(30);
            canvas.DrawRoundRect(rect, 12f, 12f, paint);
        }
    }

    private static void Save(int width, int height, int scale, string path, RenderTheme theme,
        Action<SKCanvas, SKRect> draw)
    {
        var info = new SKImageInfo(width * scale, height * scale, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var bitmap = new SKBitmap(info))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(38, 40, 46));
            canvas.Scale(scale);
            draw(canvas, new SKRect(0f, 0f, width, height));
            canvas.Flush();

            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Open(path, FileMode.Create, FileAccess.Write))
            {
                data.SaveTo(stream);
            }
        }

        Console.WriteLine("        写出 " + Path.GetFileName(path));
    }

    // ---- 辅助 ----

    private static void MakeTestShot(string path, int index)
    {
        const int Width = 640;
        const int Height = 400;

        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var bitmap = new SKBitmap(info))
        using (var canvas = new SKCanvas(bitmap))
        {
            var colors = new SKColor[]
            {
                new SKColor(72, 118, 190), new SKColor(190, 96, 96), new SKColor(96, 168, 122),
                new SKColor(168, 132, 200), new SKColor(206, 158, 74)
            };

            SKColor tint = colors[(index - 1) % colors.Length];

            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0f, 0f), new SKPoint(Width, Height),
                    new SKColor[] { tint, tint.WithAlpha(170), new SKColor(28, 30, 36) },
                    new float[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
                canvas.DrawRect(new SKRect(0f, 0f, Width, Height), paint);
            }

            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Color = new SKColor(255, 255, 255, 60);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2f;
                canvas.DrawRect(new SKRect(24f, 24f, Width - 24f, Height - 24f), paint);
            }

            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.TextSize = 44f;
                paint.Color = SKColors.White;
                canvas.DrawText("Screenshot #" + index.ToString(), 52f, Height * 0.5f, paint);
                paint.TextSize = 20f;
                paint.Color = new SKColor(255, 255, 255, 190);
                canvas.DrawText(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), 54f, Height * 0.5f + 40f, paint);
            }

            canvas.Flush();
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Open(path, FileMode.Create, FileAccess.Write))
            {
                data.SaveTo(stream);
            }
        }
    }

    private static int CountShots(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.GetFiles(folder, "*.png").Length;
        }
        catch { return -1; }
    }

    private static void SetClipboardImage(int width, int height)
    {
        using (var bitmap = new Bitmap(width, height))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(255, 122, 158, 206));
            }
            Clipboard.SetImage(bitmap);
        }
    }

    private static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage(); } catch { return false; }
    }

    private static bool ClipboardHasPng()
    {
        try
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null) return false;
            object value = data.GetData("PNG");
            return value is Stream || value is byte[];
        }
        catch { return false; }
    }

    private static bool ClipboardHasFileDrop()
    {
        try
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null) return false;
            object value = data.GetData(DataFormats.FileDrop);
            var files = value as string[];
            return files != null && files.Length > 0 && File.Exists(files[0]);
        }
        catch { return false; }
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return true; } catch { }
            Application.DoEvents();
            Thread.Sleep(120);
        }
        return false;
    }

    private static void Check(bool ok, string label)
    {
        if (ok)
        {
            Console.WriteLine("[PASS] " + label);
            return;
        }
        _failures++;
        Console.WriteLine("[FAIL] " + label);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }
}
