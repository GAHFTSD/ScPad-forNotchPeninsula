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
        private bool _fontHooked;
        private bool _disposed;

        public string Id { get { return PluginId; } }

        public string DisplayName { get { return "ScPad"; } }

        public string Version { get { return "1.0.0-preview"; } }

        public string Author { get { return "GAHFTSD"; } }

        internal ShotStore Store { get { return _store; } }

        internal PluginSettings Settings { get { return _settings; } }

        internal ShotClipPanel Panel { get { return _panel; } }

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

        /// <summary>第一次加载时打一声招呼，之后不再打扰。</summary>
        private void Welcome(IPluginHost host)
        {
            try
            {
                if (host.GetSetting("welcomed", "0") == "1") return;
                host.SetSetting("welcomed", "1");

                HotkeySpec hotkey = _settings.HotkeyA;
                string extra = hotkey.IsSet ? "（" + hotkey.Text + " 可随手抓一张）" : "";

                Reminders.Post(host, "ScPad 已就绪",
                    "截图或复制图片后会自动存入灵动岛。点缩略图打开截图夹；面板里的「设置」页可以改保存位置和快捷键。" + extra,
                    delegate { OpenPanel(); },
                    TimeSpan.FromSeconds(7));
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
