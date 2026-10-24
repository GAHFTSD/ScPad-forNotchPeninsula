using System;
using System.Collections.Generic;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// 给官方「插件设置中心」（NpsSettingsHub）渲染的设置页 —— 精简版。
    ///
    /// 只实现宿主已有的 <see cref="ISettingsPage"/>，不引用设置中心的任何 dll；
    /// 设置中心按**属性名**识别控件形态（Options → 选择框、DefaultValue → 勾选框、
    /// Min/Max → 数值、Action → 按钮、都没有 → 文本）。
    ///
    /// 精简原则：
    ///   * 能合并的都合并 —— 「缩略图自动 / 自定义」两个开关合成一个数值项（不动就跟随上限）；
    ///   * 每个快捷键槽只留**一个按钮**：按钮的 Label 直接写着当前组合，点一下就进入录制，
    ///     Backspace 清除、Esc 取消（不再单独摆只读文本 + 清除按钮）；
    ///   * 滚轮相关的设置项全部拿掉（滚轮功能代码保留，但默认不启用、也不出现在设置里）。
    ///
    /// 一共 14 项：截图夹 7 + 保存位置 2 + 快捷键 4 + 恢复默认 1。
    /// </summary>
    internal sealed class ScPadSettingsPage : ISettingsPage
    {
        internal static readonly string[] ActionOptions = new string[]
        {
            "截图并存入", "展开 / 收起", "复制最近一张"
        };

        private readonly PluginSettings _settings;

        internal ScPadSettingsPage(PluginSettings settings)
        {
            _settings = settings;
        }

        public string Title { get { return "ScPad · 截图夹"; } }

        public IReadOnlyList<SettingControl> Controls
        {
            get
            {
                var list = new List<SettingControl>();

                int max = _settings.MaxItems;
                int recommend = PluginSettings.RecommendScale(max);
                int effective = _settings.EffectiveThumbScale;

                // ---- 截图夹 ----
                list.Add(new NumberSetting("maxShots",
                    "截图夹上限（张）· 4~100，推荐 4~16", 4f, 100f, 1f, 10f));

                list.Add(new NumberSetting("panelSlots",
                    "面板格子数 · 1~8（空格也占位，超出才可翻页）", 1f, 8f, 1f, 4f));

                list.Add(new NumberSetting("thumbScale",
                    "缩略图大小 % · 不改就跟随上限（现在生效 " + effective.ToString() + "%）",
                    25f, 200f, 5f, (float)recommend));

                list.Add(new ToggleSetting("autoPeek", "新截图自动展开面板", true));

                list.Add(new ToggleSetting("clipboard", "收集剪贴板里的图片（不只是截图）", true));

                list.Add(new ChoiceSetting("collectMode", "收集模式（自动 / 手动 / 关闭）",
                    new string[] { "自动收集", "手动收集", "关闭收集" }, _settings.CollectMode));

                list.Add(new ToggleSetting("hideOnCapture", "抓屏时先藏起灵动岛", true));

                list.Add(new ToggleSetting("panelActions", "面板底部显示设置区（更多设置 / 停止收集 / 图片编辑）", true));

                list.Add(new ChoiceSetting("editorMode", "「图片编辑」用哪个应用",
                    new string[] { "照片（系统默认）", "画图" }, 0));

                // ---- 保存位置 ----
                list.Add(DynamicControls.Text("saveFolder",
                    "保存位置（留空 = 系统默认截图位置）",
                    ShotStore.SystemScreenshotsFolder(), 260,
                    "按当前 Windows 用户解析；在资源管理器里改过「屏幕截图」位置也会跟着变"));

                list.Add(DynamicControls.Action("pickFolder", "选择保存文件夹…", "选择文件夹…",
                    "弹出系统的文件夹选择框，选完立即生效"));

                // ---- 快捷键（一个按钮既显示当前值又能录制）----
                list.Add(DynamicControls.Action("recordHotkeyA",
                    "快捷键 1 · " + Describe(_settings.HotkeyA), "录制",
                    "点完直接按组合键（至少一个修饰键，或 F1~F24）；Esc 取消、Backspace 清除。"
                    + " PrtSc 单独按下是系统截图键，插件不会占用。"));

                list.Add(new ChoiceSetting("hotkeyAAction", "快捷键 1 的动作",
                    ActionOptions, ActionIndexOf(_settings.HotkeyAAction)));

                list.Add(DynamicControls.Action("recordHotkeyB",
                    "快捷键 2 · " + Describe(_settings.HotkeyB), "录制",
                    "推荐录成 Alt + Ctrl + 任意键，例如 Alt + Ctrl + A；Esc 取消、Backspace 清除"));

                list.Add(new ChoiceSetting("hotkeyBAction", "快捷键 2 的动作",
                    ActionOptions, ActionIndexOf(_settings.HotkeyBAction)));

                // ---- 其它 ----
                list.Add(DynamicControls.Action("showGuide", "重新查看新手引导", "打开",
                    "在面板里再放一次 4 张教学卡（截图 / 挂图 / 编辑 / 收集模式）"));

                list.Add(DynamicControls.Action("openIssues", "反馈问题（GitHub Issues）", "打开",
                    "在 GitHub 同名仓库（ScPad-forNotchPeninsula）的 Issues 里反馈；打不开时可直接去仓库提 issue"));

                list.Add(DynamicControls.Action("resetAll", "恢复默认设置", "恢复默认",
                    "只重置 ScPad 的设置项，不删除已收集的截图"));

                return list;
            }
        }

        private static string Describe(HotkeySpec spec)
        {
            if (spec == null || !spec.IsSet) return "未设置";
            return spec.Text;
        }

        internal static int ActionIndexOf(int action)
        {
            if (action == PluginSettings.ActionTogglePanel) return 1;
            if (action == PluginSettings.ActionCopyLatest) return 2;
            return 0;
        }

        internal static int ActionFromIndex(int index)
        {
            if (index == 1) return PluginSettings.ActionTogglePanel;
            if (index == 2) return PluginSettings.ActionCopyLatest;
            return PluginSettings.ActionCapture;
        }
    }

    /// <summary>
    /// 设置中心里的「按钮」不是回调，而是**把 Key 的值 +1 写回**（接入文档 4.5）。
    /// 插件这边定期比对计数就知道用户点了哪个按钮 —— 这样设置中心不必持有我们的对象，
    /// 热重载也能干净回收。
    /// </summary>
    internal sealed class SettingsButtonPoller
    {
        private static readonly string[] Keys = new string[]
        {
            "pickFolder", "recordHotkeyA", "recordHotkeyB", "showGuide", "openIssues", "resetAll"
        };

        private readonly IPluginHost _host;
        private readonly Dictionary<string, int> _last = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> _onPressed;

        internal SettingsButtonPoller(IPluginHost host, Action<string> onPressed)
        {
            _host = host;
            _onPressed = onPressed;

            // 先把当前值记下来，免得启动时把一个历史计数当成"刚点的"
            for (int i = 0; i < Keys.Length; i++) _last[Keys[i]] = Read(Keys[i]);
        }

        /// <summary>在采集线程的 tick 里调用；返回是否有按钮被按下。</summary>
        internal bool Poll()
        {
            bool any = false;
            for (int i = 0; i < Keys.Length; i++)
            {
                string key = Keys[i];
                int now = Read(key);
                int before;
                if (!_last.TryGetValue(key, out before)) before = now;

                if (now != before)
                {
                    _last[key] = now;
                    any = true;
                    try { _onPressed(key); }
                    catch (Exception ex) { Log.Error("[ScPad] 设置按钮处理异常：" + key, ex); }
                }
            }
            return any;
        }

        private int Read(string key)
        {
            try
            {
                string raw = _host.GetSetting(key, "0");
                int value;
                return int.TryParse(raw, out value) ? value : 0;
            }
            catch { return 0; }
        }
    }
}
