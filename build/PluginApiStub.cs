// ============================================================================
//  NotchPeninsula 插件 API 编译用参考声明（仅用于「没有 .NET SDK」时的离线编译）
//
//  本文件**不属于插件源码**，也不会被 csproj 编译（见 ScPad.csproj 里的
//  <Compile Remove="build\**\*.cs" />），它只是把 NotchPeninsula-main\Plugin\PluginApi.cs
//  里插件要用到的公开类型原样抄一份，编译成一个名为 NotchPeninsula.dll 的「假宿主」，
//  让本机自带的 C# 编译器（%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe）
//  能在没有 .NET SDK / 没有 NuGet 的情况下把插件编出来。
//
//  这些声明必须与真宿主的签名逐字一致：插件 dll 里留下的是「按名字 + 签名」的引用，
//  运行时全部由真正加载的 NotchPeninsula.exe 里的类型来兑现。也就是说：
//    * 这个 dll 永远不会被真正加载；
//    * 但任何一个签名写错，插件在宿主里就会 TypeLoadException / MissingMethodException。
//  有 .NET SDK 的机器请直接用 ScPad.csproj（ProjectReference 引用真宿主），
//  不要用这份替身。
// ============================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

// 版本号故意取一个「低而正常」的值：运行时按简单名称绑定，不会因为宿主版本更高而失败。
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace NotchPeninsula.Plugins
{
    public interface INotchPlugin
    {
        string Id { get; }
        string DisplayName { get; }
        string Version { get; }
        string Author { get; }
        void Initialize(IPluginHost host);
    }

    // ---- 命中模型：readonly record struct WidgetHit(string? Action) ----

    public struct WidgetHit
    {
        private readonly string _action;

        // record 的静态字段 None = new(null)
        public static readonly WidgetHit None;

        public WidgetHit(string action) { _action = action; }

        public string Action { get { return _action; } }

        public bool IsHit { get { return _action != null; } }
    }

    // ---- 主题 / 渲染上下文 ----

    public struct RenderTheme
    {
        private readonly SKColor _textColor;
        private readonly SKColor _subTextColor;
        private readonly SKColor _backgroundColor;
        private readonly float _globalDpi;
        private readonly float _notchBottomRadius;

        public RenderTheme(SKColor textColor, SKColor subTextColor, SKColor backgroundColor,
            float globalDpi, float notchBottomRadius)
        {
            _textColor = textColor;
            _subTextColor = subTextColor;
            _backgroundColor = backgroundColor;
            _globalDpi = globalDpi;
            _notchBottomRadius = notchBottomRadius;
        }

        public SKColor TextColor { get { return _textColor; } }
        public SKColor SubTextColor { get { return _subTextColor; } }
        public SKColor BackgroundColor { get { return _backgroundColor; } }
        public float GlobalDpi { get { return _globalDpi; } }
        public float NotchBottomRadius { get { return _notchBottomRadius; } }
    }

    public struct WidgetFrame
    {
        private readonly RenderTheme _theme;
        private readonly byte _alpha;
        private readonly float _textOffsetY;
        private readonly float[] _bars;
        private readonly bool _isHovered;

        public WidgetFrame(RenderTheme theme, byte alpha, float textOffsetY, float[] bars, bool isHovered)
        {
            _theme = theme;
            _alpha = alpha;
            _textOffsetY = textOffsetY;
            _bars = bars;
            _isHovered = isHovered;
        }

        public RenderTheme Theme { get { return _theme; } }
        public byte Alpha { get { return _alpha; } }
        public float TextOffsetY { get { return _textOffsetY; } }
        public float[] Bars { get { return _bars; } }
        public bool IsHovered { get { return _isHovered; } }
    }

    // ---- 五种「页」 ----

    public interface IWidget
    {
        string Id { get; }
        string DisplayName { get; }
        IDetailPage DetailPage { get; }

        bool AcceptsFileDropWhenCollapsed { get; }

        float MeasureWidth(float availableHeight);

        void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);

        WidgetHit HitTest(float x, float y, SKRect rect);

        void OnLeftClick(string action, float x, float y);

        void OnRightClick();

        bool AcceptsRightClick { get; }

        bool AcceptsDoubleClick { get; }

        void OnLeftDoubleClick(float x, float y);

        void OnRightDoubleClick(float x, float y);

        void OnActivate(IPluginHost host);

        void OnDeactivate();
    }

    public interface IDetailPage
    {
        float MeasureWidth();
        float MeasureHeight();
        void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
        WidgetHit HitTest(float x, float y, SKRect rect);
        void OnAction(string action, float x, float y);

        void OnMouseDown(float x, float y);

        void OnMouseMove(float x, float y);

        void OnMouseUp(float x, float y);

        void OnMouseLeave();

        bool AcceptsRightClick { get; }

        void OnRightClick(float x, float y);

        bool AcceptsDoubleClick { get; }

        void OnLeftDoubleClick(float x, float y);

        void OnRightDoubleClick(float x, float y);

        bool OnFilesDragEnter(int count);

        void OnFilesDragOver(float x, float y);

        void OnFilesDragLeave();

        void OnFilesDrop(string[] paths);

        TimeSpan AutoCollapseDelay { get; }
    }

    public interface ISecondaryWidget
    {
        string Id { get; }
        void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
    }

    public interface ISettingsPage
    {
        string Title { get; }
        IReadOnlyList<SettingControl> Controls { get; }
    }

    public interface ICustomSettingsPage
    {
        float MeasureHeight();
        void Draw(SKCanvas canvas, SKRect rect, RenderTheme theme);
        void OnMouseDown(float x, float y);
        void OnMouseMove(float x, float y);
        void OnMouseUp(float x, float y);
    }

    public interface IPluginWindow
    {
        void SetDraw(Action<SKCanvas, int, int> draw);
        void SetMouse(Action<float, float> down, Action<float, float> move, Action<float, float> up);
        void SetKey(Action<char> key);

        void SetFilesDrop(Action<string[]> onFiles);

        void SetDragHover(Action<int> onEnter, Action<float, float> onOver, Action onLeave);

        bool StartDragFiles(IReadOnlyList<string> paths, bool allowMove);

        void RequestRedraw();
        void Close();
    }

    public abstract class SettingControl
    {
        protected SettingControl(string key, string label) { Key = key; Label = label; }
        public string Key { get; private set; }
        public string Label { get; private set; }

        // 真宿主里 SettingControl 是 abstract record，带一个 abstract 的克隆方法 <Clone>$；
        // C# 5 打不出 <Clone>$ 这个标识符，所以替身用「抽象无参、返回 SettingControl」的占位方法，
        // 让自检台也能覆盖到「派生的自定义控件必须实现抽象成员」这条路径。
        protected abstract SettingControl CloneCore();
    }

    public sealed class ToggleSetting : SettingControl
    {
        public ToggleSetting(string key, string label, bool defaultValue) : base(key, label) { DefaultValue = defaultValue; }
        protected override SettingControl CloneCore() { return this; }
        public bool DefaultValue { get; private set; }
    }

    public sealed class ChoiceSetting : SettingControl
    {
        public ChoiceSetting(string key, string label, string[] options, int defaultIndex) : base(key, label)
        {
            Options = options;
            DefaultIndex = defaultIndex;
        }
        protected override SettingControl CloneCore() { return this; }
        public string[] Options { get; private set; }
        public int DefaultIndex { get; private set; }
    }

    public sealed class NumberSetting : SettingControl
    {
        public NumberSetting(string key, string label, float min, float max, float step, float defaultValue) : base(key, label)
        {
            Min = min; Max = max; Step = step; Default = defaultValue;
        }
        protected override SettingControl CloneCore() { return this; }
        public float Min { get; private set; }
        public float Max { get; private set; }
        public float Step { get; private set; }
        public float Default { get; private set; }
    }

    // 宿主里的属性都是 init 存取器（setter 带 IsExternalInit modreq）。
    // 这里故意只声明 getter：插件必须用反射写这些属性，否则会编出一个
    // 缺 modreq 的 setter 引用，运行期直接 MissingMethodException。
    public sealed class ReminderData
    {
        public string Title { get; private set; }
        public string Body { get; private set; }
        public string Source { get; private set; }
        public string IconPath { get; private set; }
        public TimeSpan Duration { get; private set; }
        public Action OnClick { get; private set; }
    }

    // ---- 主机 ----

    public interface IPluginHost
    {
        void RegisterWidget(IWidget widget);
        void RegisterSecondaryWidget(ISecondaryWidget widget);
        void RegisterSettingsPage(ISettingsPage page);

        RenderTheme CurrentTheme { get; }

        void PostReminder(ReminderData reminder);

        string GetSetting(string key, string fallback);
        void SetSetting(string key, string value);

        event Action SettingsChanged;

        IDisposable ScheduleRefresh(TimeSpan interval, Action callback);

        void RequestRedraw();
        void OpenDetailPage(string widgetId);

        bool TryOpenDetailPage(string widgetId);

        void CloseDetailPage();

        bool ToggleDetailPage(string widgetId);

        void InvalidateWidgetLayout();

        float GetPluginRowBudget();

        IPluginWindow CreateWindow(string title, int width, int height);

        bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove);
    }
}

// ---- 宿主公共设施（非冻结契约，插件里一律 try/catch 包住）----

namespace NotchPeninsula
{
    // 真宿主把日志写进 %LocalAppData%\NotchPeninsula\app.log；替身只活在本机自检里，
    // 直接打到控制台更容易看（这个 dll 永远不会被真正的宿主加载）。
    public static class Logger
    {
        public static void Debug(string msg) { Write("DEBUG", msg); }
        public static void DebugThrottled(string template) { Write("DEBUG", template); }
        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg, Exception ex = null) { Write("ERROR", ex == null ? msg : msg + " | " + ex); }

        private static void Write(string level, string msg)
        {
            try { Console.WriteLine("        [" + level + "] " + msg); } catch { }
        }
    }

    // 岛体窗口句柄：整屏抓图前先把岛体藏起来（真实成员是 public static IntPtr InstanceHandle）。
    public static class NotchWindow
    {
        public static IntPtr InstanceHandle { get { return IntPtr.Zero; } }
        public static bool IsAutoHideEnabled;
        public static bool IsAutoHideEffective { get { return false; } }
    }

    public static class FontConfig
    {
        // 替身里给一份真字体，这样自检渲染出来的预览图和宿主里看到的一致（含中文）
        private static readonly SKTypeface NormalFace = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        private static readonly SKTypeface BoldFace = SKTypeface.FromFamilyName("Microsoft YaHei UI",
            SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        private static readonly SKTypeface SemiBoldFace = SKTypeface.FromFamilyName("Microsoft YaHei UI",
            SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        public static SKTypeface Normal { get { return NormalFace; } }
        public static SKTypeface Bold { get { return BoldFace; } }
        public static SKTypeface SemiBold { get { return SemiBoldFace; } }
        public static SKTypeface Fallback { get { return NormalFace; } }
        public static bool HasCustomFont { get { return false; } }
        public static string CustomFontPath { get { return ""; } }
        public static string DisplayName { get { return "系统字体"; } }

        public static event Action Changed
        {
            add { }
            remove { }
        }
    }
}
