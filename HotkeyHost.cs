using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScPad
{
    /// <summary>
    /// 全局快捷键：注册 / 录制 / 派发。
    ///
    /// 一条独立线程负责全部事情：
    ///   * RegisterHotKey(IntPtr.Zero, id, ...) —— 传 NULL 时 WM_HOTKEY 会投递到**调用线程**的消息队列，
    ///     所以连窗口都不用建；
    ///   * 用 PeekMessage 把 WM_HOTKEY 捞出来派发；
    ///   * 录制时先把已注册的热键全部注销（否则你想录 Ctrl+PrtSc，键会被自己吞掉），
    ///     再用 GetAsyncKeyState 轮询"下一个按下的非修饰键"。
    ///
    /// 录制只在我们的线程里做，界面线程只读状态标记，没有共享可变状态。
    /// </summary>
    internal sealed class HotkeyHost : IDisposable
    {
        private const int HotkeyIdA = 0x5A01;
        private const int HotkeyIdB = 0x5A02;
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private const uint PmRemove = 0x0001;

        private const int VkEscape = 0x1B;
        private const int VkBack = 0x08;

        private readonly PluginSettings _settings;
        private readonly Action<int> _onPressed;

        private readonly Thread _thread;
        private readonly ManualResetEvent _wake = new ManualResetEvent(false);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);

        private volatile bool _disposed;

        private int _generation;
        private int _seenGeneration = -1;

        private bool _registeredA;
        private bool _registeredB;
        private string _errorA = "";
        private string _errorB = "";

        private bool[] _wasDown = new bool[256];
        private bool _wasDownValid;

        private volatile int _recordingSlot = -1;
        private volatile string _recordingMessage = "";
        private long _recordIgnoreUntilTicks;

        internal HotkeyHost(PluginSettings settings, Action<int> onPressed)
        {
            _settings = settings;
            _onPressed = onPressed;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ScPad.Hotkeys";
            _thread.Start();
        }

        // ---- 给界面看的状态 ----

        internal int RecordingSlot { get { return _recordingSlot; } }

        internal string RecordingMessage { get { return _recordingMessage; } }

        internal bool IsRegistered(int slot)
        {
            return slot == 1 ? _registeredB : _registeredA;
        }

        internal string RegistrationError(int slot)
        {
            string text = slot == 1 ? _errorB : _errorA;
            return text == null ? "" : text;
        }

        /// <summary>设置变了 → 让线程重新注册。</summary>
        internal void Refresh()
        {
            Interlocked.Increment(ref _generation);
            try { _wake.Set(); } catch { }
        }

        internal void StartRecording(int slot)
        {
            // 忽略开始后 320ms 内的按键状态，免得把"点按钮"当成一次按键
            _recordIgnoreUntilTicks = DateTime.UtcNow.AddMilliseconds(320).Ticks;
            _wasDownValid = false;
            _recordingMessage = "按下组合键…（Esc 取消，Backspace 清除）";
            _recordingSlot = slot == 1 ? 1 : 0;

            Refresh();   // 会先把当前热键注销掉
        }

        internal void CancelRecording()
        {
            if (_recordingSlot < 0) return;
            _recordingSlot = -1;
            _recordingMessage = "";
            Refresh();
        }

        // ---- 线程主体 ----

        private void Loop()
        {
            try
            {
                Refresh();

                while (!_disposed)
                {
                    DrainMessages();
                    SyncRegistration();

                    if (_recordingSlot >= 0) PollRecording();

                    int index = WaitHandle.WaitAny(new WaitHandle[] { _wake, _stop }, 40);
                    if (index == 1) break;
                    _wake.Reset();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 快捷键线程异常退出", ex);
            }
            finally
            {
                UnregisterAll();
                Log.Info("[ScPad] 快捷键线程已停止");
            }
        }

        private void DrainMessages()
        {
            MSG message;
            while (PeekMessage(out message, IntPtr.Zero, 0, 0, PmRemove))
            {
                if (message.message != WmHotkey) continue;

                int id = (int)message.wParam.ToInt64();
                if (id == HotkeyIdA) Fire(0);
                else if (id == HotkeyIdB) Fire(1);
            }
        }

        private void Fire(int slot)
        {
            try
            {
                if (_onPressed != null) _onPressed(slot);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 快捷键回调异常", ex);
            }
        }

        private void SyncRegistration()
        {
            if (_seenGeneration == _generation) return;
            _seenGeneration = _generation;

            UnregisterAll();

            // 录制期间不注册：否则用户录的正好是当前热键，会被系统直接吞掉
            if (_recordingSlot >= 0) return;

            RegisterSlot(0);
            RegisterSlot(1);
        }

        private void RegisterSlot(int slot)
        {
            HotkeySpec spec = _settings.Hotkey(slot);
            if (spec == null || !spec.IsSet) return;

            string error = HotkeySpec.Validate(spec.Modifiers, spec.Key);
            if (error != null) { SetError(slot, error); return; }

            int id = slot == 1 ? HotkeyIdB : HotkeyIdA;
            uint modifiers = spec.Modifiers | ModNoRepeat;

            if (!RegisterHotKey(IntPtr.Zero, id, modifiers, spec.Key))
            {
                int code = Marshal.GetLastWin32Error();
                SetError(slot, "注册失败（可能被别的程序占用了，错误 " + code.ToString() + "）");
                Log.Warn("[ScPad] 快捷键注册失败：" + spec.Text + " code=" + code.ToString());
                return;
            }

            if (slot == 1) { _registeredB = true; _errorB = ""; }
            else { _registeredA = true; _errorA = ""; }

            Log.Info("[ScPad] 快捷键已注册：" + spec.Text + " → " + PluginSettings.ActionText(_settings.HotkeyAction(slot)));
        }

        private void SetError(int slot, string error)
        {
            if (slot == 1) { _registeredB = false; _errorB = error; }
            else { _registeredA = false; _errorA = error; }
        }

        private void UnregisterAll()
        {
            try
            {
                if (_registeredA) UnregisterHotKey(IntPtr.Zero, HotkeyIdA);
                if (_registeredB) UnregisterHotKey(IntPtr.Zero, HotkeyIdB);
            }
            catch { }
            _registeredA = false;
            _registeredB = false;
        }

        // ---- 录制 ----

        private void PollRecording()
        {
            if (DateTime.UtcNow.Ticks < _recordIgnoreUntilTicks)
            {
                SnapshotKeys();
                return;
            }

            bool control = Down(0x11) || Down(0xA2) || Down(0xA3);
            bool alt = Down(0x12) || Down(0xA4) || Down(0xA5);
            bool shift = Down(0x10) || Down(0xA0) || Down(0xA1);
            bool windows = Down(0x5B) || Down(0x5C);

            if (Down(VkEscape) && !_wasDown[VkEscape])
            {
                CancelRecording();
                return;
            }

            if (Down(VkBack) && !_wasDown[VkBack])
            {
                ApplyRecording(HotkeySpec.None);
                return;
            }

            uint key = 0;
            if (_wasDownValid)
            {
                for (int vk = 0x08; vk < 0xFE; vk++)
                {
                    uint code = (uint)vk;
                    if (HotkeySpec.IsModifierKey(code)) continue;
                    if (HotkeySpec.IsMouseKey(code)) continue;
                    if (!Down(code)) continue;
                    if (_wasDown[vk]) continue;
                    key = code;
                    break;
                }
            }

            SnapshotKeys();

            if (key == 0) return;

            uint modifiers = 0;
            if (control) modifiers |= HotkeySpec.ModControl;
            if (alt) modifiers |= HotkeySpec.ModAlt;
            if (shift) modifiers |= HotkeySpec.ModShift;
            if (windows) modifiers |= HotkeySpec.ModWin;

            string error = HotkeySpec.Validate(modifiers, key);
            if (error != null)
            {
                _recordingMessage = error;
                return;
            }

            ApplyRecording(new HotkeySpec(modifiers, key));
        }

        private void ApplyRecording(HotkeySpec spec)
        {
            int slot = _recordingSlot;
            _recordingSlot = -1;
            _recordingMessage = "";

            _settings.SetHotkey(slot, spec);
            Log.Info("[ScPad] 快捷键 " + (slot + 1).ToString() + " 设为：" + spec.Text);

            Refresh();
        }

        private void SnapshotKeys()
        {
            for (int vk = 0; vk < 256; vk++) _wasDown[vk] = Down((uint)vk);
            _wasDownValid = true;
        }

        private static bool Down(uint vk)
        {
            return (GetAsyncKeyState((int)vk) & 0x8000) != 0;
        }

        // ---- 收尾 ----

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _stop.Set(); } catch { }
            try { _wake.Set(); } catch { }

            try
            {
                if (_thread != null && _thread.IsAlive) _thread.Join(1200);
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 等待快捷键线程退出失败：" + ex.Message);
            }

            try { _wake.Dispose(); } catch { }
            try { _stop.Dispose(); } catch { }
        }

        // ---- Win32 ----

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG message, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);
    }
}
