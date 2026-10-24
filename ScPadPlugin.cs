using System;
using NotchPeninsula;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// ScPad —— 为 NotchPeninsula 设计的截屏图片存取插件。
    ///
    /// 定位：交互与产品取舍**参考了 jiuyang5354 的 snapline-windows**（Tendedero 的 Windows 移植版，
    /// 原作者 Alejandro Buján），面向 NotchPeninsula 的插件 API 独立实现；
    /// 不含、也不再分发上述项目的源代码、图标或图片资源。
    ///
    /// 具体做的事：
    ///   * 自动收集：剪贴板里的图片（Win + Shift + S 截图、任何 Ctrl+C 的图片）+
    ///     保存目录里新出现的图片，都会自动存入「截图夹」；
    ///   * 截图显示：岛上一条小截图夹挂着最近两张缩略图 + 张数，点开是完整面板；
    ///   * 复制 / 拖动粘贴：面板里单击卡片 = 复制到剪贴板，按住拖出 = 拖到别的应用里
    ///     直接粘贴；把图片拖进面板 = 加入；
    ///   * 自定义设置（面板里的「设置」页）：图片保存位置、两个全局快捷键及其动作；
    ///   * 不影响媒体：没有截图时组件宽度为 0（宿主整个隐藏它），有截图时也只占
    ///     70~108 像素；不碰媒体控制器、不抢焦点、默认不自动展开面板。
    ///
    /// 数据：状态与回收站在 %LOCALAPPDATA%\NotchPeninsula\ScPad\，
    /// 截图本身存在用户选的保存位置（默认 = 系统「屏幕截图」文件夹）；
    /// 开关 / 路径 / 快捷键存在宿主的插件设置里（HKCU\SOFTWARE\NotchPeninsula\Plugin.com.gahftsd.scpad.*）。
    /// </summary>
    public sealed class ScPadPlugin : INotchPlugin, IDisposable
    {
        internal const string WidgetId = "com.gahftsd.scpad.shotclip";

        private const string PluginId = "com.gahftsd.scpad";

        private IPluginHost _host;
        private PluginSettings _settings;
        private ShotStore _store;
        private CaptureService _capture;
        private HotkeyHost _hotkeys;
        private ShotClipPanel _panel;
        private ScPadWidget _widget;
        private ScPadSettingsPage _settingsPage;
        private WheelHook _wheel;
        private bool _settingsHooked;
        private bool _fontHooked;
        private bool _disposed;

        public string Id { get { return PluginId; } }

        public string DisplayName { get { return "ScPad"; } }

        public string Version { get { return "1.0.0"; } }

        public string Author { get { return "GAHFTSD"; } }

        internal ShotStore Store { get { return _store; } }

        internal PluginSettings Settings { get { return _settings; } }

        internal ShotClipPanel Panel { get { return _panel; } }

        internal ScPadWidget Widget { get { return _widget; } }

        internal CaptureService Capture { get { return _capture; } }

        internal HotkeyHost Hotkeys { get { return _hotkeys; } }

        public void Initialize(IPluginHost host)
        {
            if (host == null) return;

            _host = host;

            try
            {
                Log.Info("[ScPad] 插件加载中… (v" + Version + ")");

                _settings = new PluginSettings(host);
                _store = new ShotStore(host, _settings);
                _panel = new ShotClipPanel(this, _store, host);
                _widget = new ScPadWidget(this, _store, host);
                _capture = new CaptureService(this, _store, host);
                _hotkeys = new HotkeyHost(_settings, OnHotkeyPressed);

                // 先注册组件，再启动采集：OnActivate 里能拿到宿主引用
                host.RegisterWidget(_widget);

                // 设置界面交给官方「插件设置中心」渲染（只在 Initialize 里注册一次！）
                // 自定义控件是运行时按宿主的 SettingControl 反射生成的，先生成一次：
                // 万一宿主改了抽象成员，这里会立刻露出来，而且不牵连插件的其余功能。
                bool settingsReady = true;
                try
                {
                    DynamicControls.EnsureCreated();
                }
                catch (Exception ex)
                {
                    settingsReady = false;
                    Log.Error("[ScPad] 生成设置控件类型失败，本次跳过设置页（插件其余功能照常）", ex);
                }

                if (settingsReady)
                {
                    _settingsPage = new ScPadSettingsPage(_settings);
                    try
                    {
                        host.RegisterSettingsPage(_settingsPage);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[ScPad] 注册设置页失败：" + ex.Message);
                    }
                }

                _settings.PublishHotkeyHints();

                try
                {
                    host.SettingsChanged += OnSettingsChanged;
                    _settingsHooked = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("[ScPad] 订阅设置变更失败：" + ex.Message);
                }

                // 滚轮：宿主窗口过程不转滚轮，代码留着（WheelHook / ScrollModel），
                // 但**默认不启用**、也不在设置里出现 —— 用户要求未经允许不要接管滚轮。
                // 需要时把下面两行放开即可（同时把面板/组件的 WheelRouter.Take 恢复）。
                // _wheel = new WheelHook(WheelRouter.Push, IsWheelWanted);
                // _wheel.Start();

                _capture.Start();
                _hotkeys.Refresh();

                HookFontChanged();
                Welcome(host);

                Log.Info("[ScPad] 插件已就绪，保存位置：" + _settings.SaveFolder);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 插件初始化失败", ex);
            }
        }

        private void OnSettingsChanged()
        {
            CaptureService capture = _capture;
            if (capture == null) return;
            try { capture.RequestSettingsChanged(); }
            catch (Exception ex) { Log.Warn("[ScPad] 处理设置变更失败：" + ex.Message); }
        }

        /// <summary>滚轮钩子的闸门：截图夹有溢出才吃滚轮（由渲染线程每帧更新）。</summary>
        private bool IsWheelWanted()
        {
            ScPadWidget widget = _widget;
            return widget != null && widget.CanScroll;
        }

        /// <summary>设置变了（缩略图百分比/上限），让宿主重测组件宽度。</summary>
        internal void NotifyLayoutChanged()
        {
            try
            {
                if (_host != null) _host.InvalidateWidgetLayout();
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 请求重新布局失败：" + ex.Message);
            }
            WheelRouter.Clear();
        }

        /// <summary>截图夹里是否已经有那几张新手引导图（guide-1..4）。</summary>
        private bool HasGuideImages()
        {
            try
            {
                Shot[] shots = _store == null ? null : _store.Snapshot;
                if (shots == null) return false;

                for (int i = 0; i < shots.Length; i++)
                {
                    if (shots[i].FileName != null && shots[i].FileName.StartsWith("guide-", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>快捷键按下：转到采集线程去执行（别在快捷键线程里直接动宿主）。</summary>
        private void OnHotkeyPressed(int slot)
        {
            CaptureService capture = _capture;
            if (capture == null) return;
            capture.RequestHotkey(slot);
        }

        /// <summary>宿主换字体后字宽会变，让宿主重测一次组件宽度。</summary>
        private void HookFontChanged()
        {
            try
            {
                FontConfig.Changed += OnFontChanged;
                _fontHooked = true;
            }
            catch (Exception ex)
            {
                // 旧宿主没有这个事件也不该牵连插件其余功能
                Log.Warn("[ScPad] 订阅字体变更失败（可忽略）：" + ex.Message);
            }
        }

        private void OnFontChanged()
        {
            try
            {
                if (_host != null) _host.InvalidateWidgetLayout();
            }
            catch { }
        }

        /// <summary>
        /// 加载/重载后打一声招呼：告诉用户岛上那个小框怎么用。
        /// 官方要求每次安装或重载都提示一次，所以这里不再用「已提示过」的标记。
        /// </summary>
        private void Welcome(IPluginHost host)
        {
            try
            {

                // 文案按官方要求固定，不随用户改键变化
                Reminders.Post(host, "截图夹已就绪",
                    "Ctrl+PrtSc 以开始。在「插件设置中心」中详细设置。",
                    delegate { OpenPanel(); },
                    TimeSpan.FromSeconds(9));
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 欢迎提醒失败：" + ex.Message);
            }
        }

        internal void OpenPanel()
        {
            try
            {
                if (_host != null) _host.TryOpenDetailPage(WidgetId);
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 展开面板失败：" + ex.Message);
            }
        }

        internal void ClosePanel()
        {
            try
            {
                if (_host != null) _host.CloseDetailPage();
            }
            catch { }
        }

        internal void CancelPeek()
        {
            CaptureService capture = _capture;
            if (capture != null) capture.CancelPeek();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_fontHooked)
                {
                    FontConfig.Changed -= OnFontChanged;
                    _fontHooked = false;
                }
            }
            catch { }

            try
            {
                if (_settingsHooked)
                {
                    _host.SettingsChanged -= OnSettingsChanged;
                    _settingsHooked = false;
                }
            }
            catch { }

            try
            {
                if (_wheel != null) _wheel.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 卸载滚轮钩子失败：" + ex.Message);
            }
            _wheel = null;

            // 先摘掉快捷键，免得卸载过程中还往采集线程派活
            try
            {
                if (_hotkeys != null) _hotkeys.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 关闭快捷键支持失败：" + ex.Message);
            }
            _hotkeys = null;

            try
            {
                if (_capture != null) _capture.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 关闭采集服务失败：" + ex.Message);
            }

            _capture = null;
            Log.Info("[ScPad] 插件已卸载");
        }
    }
}
