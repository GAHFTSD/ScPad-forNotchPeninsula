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

    public void RegisterSettingsPage(ISettingsPage page) { }

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

    public void SetSetting(string key, string value) { Settings[key] = value; }

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

        Check(widget.MeasureWidth(34f) == 0f, "没有截图时组件宽度为 0（完全不影响媒体）");
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

        // 空面板下也看一眼设置页（这时候面板是矮版，验证紧凑布局）
        ClickButton(panel, 120f, 21f);
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-settings-compact.png"));
        ClickButton(panel, 54f, 21f);

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

        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark.png"));
        RenderPanel(panel, MockHost.LightTheme(), Path.Combine(outputDir, "panel-light.png"));

        panel.OnMouseMove(14f + 74f, 64f + 60f);
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark-hover.png"));

        // ---- 4. 单击复制 ----
        panel.OnMouseDown(14f + 74f, 64f + 60f);
        panel.OnMouseUp(14f + 74f, 64f + 60f);
        bool clipboardOk = WaitFor(delegate { return ClipboardHasImage(); }, 9000);
        Check(clipboardOk, "单击卡片 → 剪贴板里出现了图片（可直接 Ctrl+V 粘贴）");
        Check(ClipboardHasPng(), "剪贴板里同时有 PNG 格式");
        Check(ClipboardHasFileDrop(), "剪贴板里同时有文件路径（可在资源管理器粘贴）");
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-dark-copied.png"));

        // ---- 5. 按住拖出 ----
        int dragsBefore = host.DragRequests;
        panel.OnMouseDown(14f + 148f + 12f + 74f, 64f + 60f);
        panel.OnMouseMove(14f + 148f + 12f + 74f + 30f, 64f + 60f + 12f);
        panel.OnMouseUp(14f + 148f + 12f + 74f + 30f, 64f + 60f + 12f);
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
        int filesBeforeRemove = CountShots(shotsDir);
        panel.OnMouseDown(162f - 13f, 64f + 13f);
        panel.OnMouseUp(162f - 13f, 64f + 13f);
        bool trimmed = WaitFor(delegate { return CountShots(shotsDir) == filesBeforeRemove - 1; }, 5000);
        Check(trimmed, "点卡片右上角的 × → 图片移入 Recycle 子目录");
        string recycleDir = Path.Combine(dataRoot, "Recycle");
        Check(Directory.Exists(recycleDir) && Directory.GetFiles(recycleDir).Length > 0,
            "移出的图片进了 Recycle，可手动找回");

        // ---- 10. 设置页 ----
        ClickButton(panel, 120f, 21f);   // 「设置」页签

        // 渲染预览图前先换一个「肯定没人占用」的组合：这台机器上可能正跑着灵动岛，
        // 它已经占着默认的 Ctrl+PrtSc，直接截出来的图会是一格红色的「注册失败」。
        plugin.Settings.SetHotkey(0, new HotkeySpec(HotkeySpec.ModAlt | HotkeySpec.ModControl, 0x41));  // Alt + Ctrl + A
        plugin.Hotkeys.Refresh();
        WaitFor(delegate { return plugin.Hotkeys.IsRegistered(0); }, 2500);
        Console.WriteLine("        预览用组合键：Alt + Ctrl + A（注册成功 = " + plugin.Hotkeys.IsRegistered(0).ToString() + "）");

        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-settings-dark.png"));
        RenderPanel(panel, MockHost.LightTheme(), Path.Combine(outputDir, "panel-settings-light.png"));

        // ① 清除快捷键 1 → 变成未设置
        ClickButton(panel, HotkeyClearX(panel), HotkeyClearY(panel));
        Check(!plugin.Settings.HotkeyA.IsSet, "设置页「清除」把快捷键 1 变成了未设置");
        bool unregistered = WaitFor(delegate { return !plugin.Hotkeys.IsRegistered(0); }, 3000);
        Check(unregistered, "清除后系统里的注册也一起摘掉了");

        // ② 录制：点「录制」开始，再点一次取消
        ClickButton(panel, HotkeyRecordX(panel, 1), HotkeyRecordY(panel, 1));
        Check(plugin.Hotkeys.RecordingSlot == 1, "点「录制」进入录制状态");
        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-settings-recording.png"));
        ClickButton(panel, HotkeyRecordX(panel, 1), HotkeyRecordY(panel, 1));
        Check(plugin.Hotkeys.RecordingSlot < 0, "再点一次取消录制");

        // ③ 改动作：点一下循环到下一个
        int actionBefore = plugin.Settings.HotkeyAAction;
        ClickButton(panel, HotkeyActionX(panel), HotkeyActionY(panel));
        Check(plugin.Settings.HotkeyAAction != actionBefore, "设置页「动作」按钮能切换动作类型");

        // ④ 恢复默认保存位置（应回到系统「屏幕截图」）
        ClickButton(panel, DefaultFolderX(panel), DefaultFolderY(panel));
        Check(string.Equals(plugin.Settings.SaveFolder, ShotStore.SystemScreenshotsFolder(), StringComparison.OrdinalIgnoreCase),
            "设置页「恢复默认」把保存位置改回系统「屏幕截图」");
        plugin.Settings.SetSaveFolder(shotsDir);

        // ⑤ 换成另一个目录 → 新图真的落到那里
        plugin.Settings.SetSaveFolder(shotsDir2);
        plugin.Capture.Store.EnsureSaveFolder();
        int before2 = CountShots(shotsDir2);
        SetClipboardImage(200, 130);
        bool moved = WaitFor(delegate { return CountShots(shotsDir2) > before2; }, 9000);
        Check(moved, "改保存位置后，新截图写进新目录");

        RenderPanel(panel, MockHost.DarkTheme(), Path.Combine(outputDir, "panel-settings-dark2.png"));

        // ---- 11. 暂停 ----
        ClickButton(panel, 54f, 21f);   // 回到截图夹页签
        float pauseX = panel.MeasureWidth() - 14f - 34f - 30f;   // 面板可能已经变宽了，按实际宽度算
        panel.OnMouseDown(pauseX, 21f);
        panel.OnMouseUp(pauseX, 21f);
        Thread.Sleep(200);
        Console.WriteLine("        paused=" + plugin.Settings.Paused.ToString() +
            " host=" + (host.Settings.ContainsKey("paused") ? host.Settings["paused"] : "<none>"));
        Check(host.Settings.ContainsKey("paused") && host.Settings["paused"] == "1", "「暂停收集」写进了宿主设置");

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

    // ---- 面板坐标（和 ShotClipPanel 里的常量保持一致）----

    private static void ClickButton(IDetailPage panel, float x, float y)
    {
        panel.OnMouseDown(x, y);
        panel.OnMouseUp(x, y);
        Thread.Sleep(120);
    }

    /// <summary>和 ShotClipPanel.SettingsTop 同一套算法（内容整体垂直居中）。</summary>
    private static float SettingsTop(IDetailPage panel)
    {
        float available = panel.MeasureHeight() - 44f - 28f;
        float contentHeight = available >= 272f ? 270f : 196f;
        float offset = (available - contentHeight) * 0.5f;
        if (offset < 0f) offset = 0f;
        return 44f + 8f + offset;
    }

    private static float HotkeyRowTop(IDetailPage panel, int slot)
    {
        return SettingsTop(panel) + 116f + (slot == 1 ? 32f : 0f);
    }

    private static float HotkeyComboWidth(IDetailPage panel)
    {
        float width = panel.MeasureWidth() - 14f * 2f - 46f - 44f - 96f - 18f;
        return width < 120f ? 120f : width;
    }

    private static float HotkeyRecordX(IDetailPage panel, int slot)
    {
        return 14f + HotkeyComboWidth(panel) + 6f + 23f;
    }

    private static float HotkeyRecordY(IDetailPage panel, int slot)
    {
        return HotkeyRowTop(panel, slot) + 13f;
    }

    private static float HotkeyClearX(IDetailPage panel)
    {
        return HotkeyRecordX(panel, 0) + 23f + 6f + 22f;
    }

    private static float HotkeyClearY(IDetailPage panel)
    {
        return HotkeyRecordY(panel, 0);
    }

    private static float HotkeyActionX(IDetailPage panel)
    {
        return panel.MeasureWidth() - 14f - 48f;
    }

    private static float HotkeyActionY(IDetailPage panel)
    {
        return HotkeyRecordY(panel, 0);
    }

    private static float DefaultFolderX(IDetailPage panel)
    {
        return panel.MeasureWidth() - 14f - 30f;
    }

    private static float DefaultFolderY(IDetailPage panel)
    {
        return SettingsTop(panel) + 8f;
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
