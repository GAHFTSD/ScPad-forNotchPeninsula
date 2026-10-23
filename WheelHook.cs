using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScPad
{
    /// <summary>
    /// 在灵动岛上接住鼠标滚轮。
    ///
    /// 为什么需要它：宿主的窗口过程**不处理 WM_MOUSEWHEEL**，插件 API 里也没有滚轮事件
    /// （`IWidget` / `IDetailPage` 只有点击、移动、拖放），所以只能自己装一个低级鼠标钩子
    /// （WH_MOUSE_LL）。
    ///
    /// 为什么不会误伤别的窗口：HostWindow 是**逐像素 alpha 的分层窗口**（UpdateLayeredWindow），
    /// 只有不透明的像素才参与命中测试。所以 `WindowFromPoint(光标) == 岛体句柄` 就等于
    /// "光标真的压在岛（或面板/toast）的可见像素上"——透明区域会穿透到下层窗口，不会误吃。
    ///
    /// 钩子必须装在有消息循环的线程上，而且回调要快：这里只做「取坐标 → 命中判断 → 塞进队列」，
    /// 真正的滚动在渲染线程里做（<see cref="WheelRouter"/>）。
    /// </summary>
    internal sealed class WheelHook : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WmMouseWheel = 0x020A;
        private const uint WmAppQuit = 0x8000 + 0x51;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

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

        private readonly Action<int> _onWheel;
        private readonly Func<bool> _canConsume;

        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private HookProc _proc;               // 必须保持引用：委托被回收后回调会崩
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);

        private IntPtr _island;
        private long _islandRefreshedTicks;

        private volatile bool _disposed;
        private volatile string _error = "";

        internal WheelHook(Action<int> onWheel, Func<bool> canConsume)
        {
            _onWheel = onWheel;
            _canConsume = canConsume;
        }

        /// <summary>安装失败时的原因（给日志/自检用）。</summary>
        internal string LastError { get { return _error; } }

        internal bool Installed { get { return _hook != IntPtr.Zero; } }

        internal void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ScPad.WheelHook";
            _thread.Start();
            _ready.WaitOne(2500);   // 等它把钩子装好（失败也会 Set）
        }

        private void Loop()
        {
            _threadId = GetCurrentThreadId();
            _proc = Callback;

            // 线程级钩子：dwThreadId = 0 表示全局鼠标钩子，必须在有消息循环的线程上装
            _hook = SetWindowsHookEx(WhMouseLl, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                _error = "SetWindowsHookEx 失败，错误码 " + Marshal.GetLastWin32Error().ToString();
                Log.Warn("[ScPad] 滚轮钩子安装失败：" + _error);
            }
            else
            {
                Log.Info("[ScPad] 滚轮钩子已就绪（只在截图夹有溢出时消费岛上滚轮）");
            }
            _ready.Set();

            MSG message;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WmAppQuit) break;
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (!_disposed && nCode >= 0 && wParam.ToInt64() == WmMouseWheel)
                {
                    Func<bool> gate = _canConsume;
                    if (gate != null && gate())
                    {
                        IntPtr island = Island();
                        if (island != IntPtr.Zero && IsWindowVisible(island))
                        {
                            var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                            if (WindowFromPoint(data.pt) == island)
                            {
                                int raw = (short)((data.mouseData >> 16) & 0xFFFF);
                                int notches = raw / 120;
                                if (notches == 0) notches = raw > 0 ? 1 : -1;

                                Action<int> handler = _onWheel;
                                if (handler != null) handler(notches);
                                return (IntPtr)1;   // 消费掉，别再传给下层
                            }
                        }
                    }
                }
            }
            catch
            {
                // 钩子回调里绝不能抛：吞掉
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>岛体句柄（宿主每次重建窗口都会换，所以每秒刷一次缓存）。</summary>
        private IntPtr Island()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - _islandRefreshedTicks < TimeSpan.TicksPerSecond) return _island;

            _islandRefreshedTicks = now;
            try { _island = NotchPeninsula.NotchWindow.InstanceHandle; }
            catch { _island = IntPtr.Zero; }
            return _island;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_threadId != 0) PostThreadMessage(_threadId, WmAppQuit, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }

            try
            {
                if (_thread != null && _thread.IsAlive) _thread.Join(1200);
            }
            catch { }

            try { _ready.Dispose(); } catch { }
        }

        // ---- Win32 ----

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG message);
    }
}
