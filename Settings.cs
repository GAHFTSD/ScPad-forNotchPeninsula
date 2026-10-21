using System;
using System.Runtime.InteropServices;
using System.Text;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// 一个快捷键（修饰键 + 主键）。Key == 0 表示「未设置」。
    /// 用两个数值就能完整描述，持久化写成 "mods:key"。
    /// </summary>
    internal sealed class HotkeySpec
    {
        internal const uint ModAlt = 0x0001;
        internal const uint ModControl = 0x0002;
        internal const uint ModShift = 0x0004;
        internal const uint ModWin = 0x0008;

        /// <summary>Print Screen。默认快捷键 = Ctrl + PrtSc。</summary>
        internal const uint VkSnapshot = 0x2C;

        internal static readonly HotkeySpec None = new HotkeySpec(0, 0);

        internal readonly uint Modifiers;
        internal readonly uint Key;

        internal HotkeySpec(uint modifiers, uint key)
        {
            Modifiers = key == 0 ? 0 : modifiers;
            Key = key;
        }

        internal bool IsSet { get { return Key != 0; } }

        internal bool SameAs(HotkeySpec other)
        {
            if (other == null) return !IsSet;
            return Modifiers == other.Modifiers && Key == other.Key;
        }

        /// <summary>给人看的写法，例如「Ctrl + Print Screen」「Ctrl + Alt + A」。</summary>
        internal string Text
        {
            get
            {
                if (!IsSet) return "未设置";

                var text = new StringBuilder();
                if ((Modifiers & ModControl) != 0) text.Append("Ctrl + ");
                if ((Modifiers & ModAlt) != 0) text.Append("Alt + ");
                if ((Modifiers & ModShift) != 0) text.Append("Shift + ");
                if ((Modifiers & ModWin) != 0) text.Append("Win + ");
                text.Append(KeyText(Key));
                return text.ToString();
            }
        }

        /// <summary>持久化写法："2:44"。</summary>
        internal string Persist
        {
            get { return Modifiers.ToString() + ":" + Key.ToString(); }
        }

        internal static HotkeySpec Parse(string text, HotkeySpec fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;

            int colon = text.IndexOf(':');
            if (colon <= 0) return fallback;

            uint modifiers;
            uint key;
            try
            {
                modifiers = uint.Parse(text.Substring(0, colon));
                key = uint.Parse(text.Substring(colon + 1));
            }
            catch
            {
                return fallback;
            }

            if (key == 0) return None;
            return new HotkeySpec(modifiers & 0x000F, key);
        }

        // ---- 按键名字 ----

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int lParam, StringBuilder text, int size);

        /// <summary>把虚拟键码翻成系统里的名字（PrtSc → "Prnt Scrn"、F5 → "F5"）。</summary>
        internal static string KeyText(uint vk)
        {
            if (vk == 0) return "未设置";

            // PrtSc 要特殊处理：MapVirtualKey 给的是 SysRq 的扫描码 0x54（名字会变成 "Sys Req"），
            // 真正叫 "Prnt Scrn" 的是扫描码 0x37 + 扩展位。
            string name = vk == VkSnapshot ? null : TryKeyName(vk);
            if (name == null && vk == VkSnapshot) name = TryScanName(0x37, true);
            if (name == null) name = TryKeyName(vk);
            if (name == null) name = FallbackKeyText(vk);
            return name;
        }

        private static string TryKeyName(uint vk)
        {
            try
            {
                uint scan = MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);
                if (scan == 0) return null;
                return TryScanName(scan, IsExtendedKey(vk));
            }
            catch
            {
                return null;
            }
        }

        private static string TryScanName(uint scan, bool extended)
        {
            try
            {
                int lParam = (int)(scan << 16);
                if (extended) lParam |= 1 << 24;

                var buffer = new StringBuilder(64);
                int length = GetKeyNameText(lParam, buffer, buffer.Capacity);
                if (length <= 0) return null;

                string text = buffer.ToString().Trim();
                if (text.Length == 0) return null;
                if (text[0] == '<') return null;   // "<00>" = 系统没有这个名字
                return text;
            }
            catch
            {
                return null;
            }
        }

        private static string FallbackKeyText(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();          // A-Z
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();          // 0-9
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F).ToString();   // F1-F24
            if (vk == VkSnapshot) return "Prnt Scrn";
            if (vk == 0x20) return "Space";
            if (vk == 0x08) return "Backspace";
            if (vk == 0x09) return "Tab";
            if (vk == 0x0D) return "Enter";
            if (vk == 0x1B) return "Esc";
            return "VK 0x" + vk.ToString("X2");
        }

        internal static bool IsExtendedKey(uint vk)
        {
            switch (vk)
            {
                case 0xA3:  // VK_RCONTROL
                case 0xA5:  // VK_RMENU
                case 0x2D:  // VK_INSERT
                case 0x2E:  // VK_DELETE
                case 0x24:  // VK_HOME
                case 0x23:  // VK_END
                case 0x21:  // VK_PRIOR
                case 0x22:  // VK_NEXT
                case 0x25: case 0x26: case 0x27: case 0x28:  // 方向键
                case 0x90:  // VK_NUMLOCK
                case 0x6F:  // VK_DIVIDE
                case 0x5B: case 0x5C:  // VK_LWIN / VK_RWIN
                case 0x03:  // VK_CANCEL
                    return true;
            }
            return false;
        }

        internal static bool IsModifierKey(uint vk)
        {
            switch (vk)
            {
                case 0x10: case 0x11: case 0x12:          // SHIFT / CONTROL / MENU
                case 0xA0: case 0xA1:                     // LSHIFT / RSHIFT
                case 0xA2: case 0xA3:                     // LCONTROL / RCONTROL
                case 0xA4: case 0xA5:                     // LMENU / RMENU
                case 0x5B: case 0x5C:                     // LWIN / RWIN
                    return true;
            }
            return false;
        }

        /// <summary>鼠标键（含 X1/X2）不算快捷键的主键。</summary>
        internal static bool IsMouseKey(uint vk)
        {
            return vk >= 0x01 && vk <= 0x06;
        }

        /// <summary>
        /// 合法性：不能是单独的修饰键；不需要修饰键的只有 F1~F24。
        /// </summary>
        internal static string Validate(uint modifiers, uint key)
        {
            if (key == 0) return null;   // 清空是合法的

            if (key < 0x08 || key > 0xFE) return "这个键不能当快捷键";
            if (IsModifierKey(key)) return "不能只用修饰键，请再按一个普通键";
            if (IsMouseKey(key)) return "鼠标键不能当快捷键";

            if ((modifiers & 0x000F) == 0)
            {
                bool functionKey = key >= 0x70 && key <= 0x87;
                if (!functionKey) return "请至少按住 Ctrl / Alt / Shift / Win 其中之一（单键只允许 F1~F24）";
            }

            return null;
        }
    }

    /// <summary>
    /// 插件自己的设置。全部存进宿主的注册表（`Plugin.com.gahftsd.scpad.*`），
    /// 宿主没有给插件做设置页的能力，所以界面得自己画 —— 见 ShotClipPanel 的「设置」页。
    /// </summary>
    internal sealed class PluginSettings
    {
        internal const int ActionCapture = 0;
        internal const int ActionTogglePanel = 1;
        internal const int ActionCopyLatest = 2;

        /// <summary>动作选择按钮的循环顺序。</summary>
        internal static readonly int[] ActionCycle = new int[] { ActionCapture, ActionTogglePanel, ActionCopyLatest };

        private const string KeySaveFolder = "saveFolder";
        private const string KeyPaused = "paused";
        private const string KeyClipboard = "clipboard";
        private const string KeyAutoPeek = "autoPeek";
        private const string KeyHotkeyA = "hotkeyA";
        private const string KeyHotkeyAAction = "hotkeyAAction";
        private const string KeyHotkeyB = "hotkeyB";
        private const string KeyHotkeyBAction = "hotkeyBAction";

        private readonly IPluginHost _host;
        private readonly object _lock = new object();

        private string _saveFolder;
        private bool _paused;
        private bool _clipboard;
        private bool _autoPeek;
        private HotkeySpec _hotkeyA;
        private int _hotkeyAAction;
        private HotkeySpec _hotkeyB;
        private int _hotkeyBAction;

        internal PluginSettings(IPluginHost host)
        {
            _host = host;

            string folder = Read(KeySaveFolder, "");
            _saveFolder = string.IsNullOrEmpty(folder) ? ShotStore.SystemScreenshotsFolder() : folder;

            _paused = Read(KeyPaused, "0") == "1";
            _clipboard = Read(KeyClipboard, "1") != "0";
            _autoPeek = Read(KeyAutoPeek, "1") != "0";   // 默认开启：新截图到手自动展开面板（可在设置页关掉）

            // 默认：Ctrl + PrtSc 抓一张存入；第二格留空，可以自己录成 Alt + Ctrl + 任意键
            _hotkeyA = HotkeySpec.Parse(Read(KeyHotkeyA, ""), new HotkeySpec(HotkeySpec.ModControl, HotkeySpec.VkSnapshot));
            _hotkeyAAction = (int)ReadNumber(KeyHotkeyAAction, ActionCapture);
            _hotkeyB = HotkeySpec.Parse(Read(KeyHotkeyB, ""), HotkeySpec.None);
            _hotkeyBAction = (int)ReadNumber(KeyHotkeyBAction, ActionTogglePanel);

            if (!IsKnownAction(_hotkeyAAction)) _hotkeyAAction = ActionCapture;
            if (!IsKnownAction(_hotkeyBAction)) _hotkeyBAction = ActionTogglePanel;
        }

        // ---- 保存位置 ----

        internal string SaveFolder
        {
            get { lock (_lock) return _saveFolder; }
        }

        internal void SetSaveFolder(string folder)
        {
            if (folder == null) return;
            folder = folder.Trim();
            if (folder.Length == 0) return;

            lock (_lock) _saveFolder = folder;
            Write(KeySaveFolder, folder);
        }

        /// <summary>恢复默认 = 系统「屏幕截图」文件夹。</summary>
        internal void ResetSaveFolder()
        {
            SetSaveFolder(ShotStore.SystemScreenshotsFolder());
        }

        // ---- 开关 ----

        internal bool Paused
        {
            get { lock (_lock) return _paused; }
            set
            {
                lock (_lock) _paused = value;
                Write(KeyPaused, value ? "1" : "0");
            }
        }

        internal bool ClipboardEnabled
        {
            get { lock (_lock) return _clipboard; }
            set
            {
                lock (_lock) _clipboard = value;
                Write(KeyClipboard, value ? "1" : "0");
            }
        }

        internal bool AutoPeek
        {
            get { lock (_lock) return _autoPeek; }
            set
            {
                lock (_lock) _autoPeek = value;
                Write(KeyAutoPeek, value ? "1" : "0");
            }
        }


        // ---- 快捷键 ----

        internal HotkeySpec HotkeyA { get { lock (_lock) return _hotkeyA; } }

        internal int HotkeyAAction { get { lock (_lock) return _hotkeyAAction; } }

        internal HotkeySpec HotkeyB { get { lock (_lock) return _hotkeyB; } }

        internal int HotkeyBAction { get { lock (_lock) return _hotkeyBAction; } }

        internal HotkeySpec Hotkey(int slot)
        {
            return slot == 1 ? HotkeyB : HotkeyA;
        }

        internal int HotkeyAction(int slot)
        {
            return slot == 1 ? HotkeyBAction : HotkeyAAction;
        }

        internal void SetHotkey(int slot, HotkeySpec spec)
        {
            if (spec == null) spec = HotkeySpec.None;

            lock (_lock)
            {
                if (slot == 1) _hotkeyB = spec;
                else _hotkeyA = spec;
            }

            Write(slot == 1 ? KeyHotkeyB : KeyHotkeyA, spec.Persist);
        }

        internal void SetHotkeyAction(int slot, int action)
        {
            if (!IsKnownAction(action)) action = ActionCapture;

            lock (_lock)
            {
                if (slot == 1) _hotkeyBAction = action;
                else _hotkeyAAction = action;
            }

            Write(slot == 1 ? KeyHotkeyBAction : KeyHotkeyAAction, action.ToString());
        }

        internal void CycleHotkeyAction(int slot)
        {
            int current = HotkeyAction(slot);
            int index = 0;
            for (int i = 0; i < ActionCycle.Length; i++)
            {
                if (ActionCycle[i] != current) continue;
                index = i;
                break;
            }
            index = (index + 1) % ActionCycle.Length;
            SetHotkeyAction(slot, ActionCycle[index]);
        }

        internal void ResetHotkeys()
        {
            SetHotkey(0, new HotkeySpec(HotkeySpec.ModControl, HotkeySpec.VkSnapshot));
            SetHotkeyAction(0, ActionCapture);
            SetHotkey(1, HotkeySpec.None);
            SetHotkeyAction(1, ActionTogglePanel);
        }

        internal static string ActionText(int action)
        {
            switch (action)
            {
                case ActionCapture: return "截图并存入";
                case ActionTogglePanel: return "展开 / 收起";
                case ActionCopyLatest: return "复制最近一张";
            }
            return "截图并存入";
        }

        private static bool IsKnownAction(int action)
        {
            return action == ActionCapture || action == ActionTogglePanel || action == ActionCopyLatest;
        }

        // ---- 读写 ----

        private string Read(string key, string fallback)
        {
            try { return _host.GetSetting(key, fallback); }
            catch { return fallback; }
        }

        private int ReadNumber(string key, int fallback)
        {
            string text = Read(key, "");
            int value;
            if (!string.IsNullOrEmpty(text) && int.TryParse(text, out value)) return value;
            return fallback;
        }

        private void Write(string key, string value)
        {
            try { _host.SetSetting(key, value); }
            catch (Exception ex) { Log.Warn("[ScPad] 保存设置失败：" + key + " — " + ex.Message); }
        }
    }
}
