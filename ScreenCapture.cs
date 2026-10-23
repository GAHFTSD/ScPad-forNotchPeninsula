using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using NotchPeninsula;

namespace ScPad
{
    /// <summary>
    /// 整屏截图。
    ///
    /// 一个细节：灵动岛自己是置顶的层窗口，直接拷屏会把岛体也拍进去。所以抓之前用
    /// NotchWindow.InstanceHandle 把岛体藏起来 ~140ms（给合成器一帧时间），抓完立刻恢复。
    /// 恢复放在 finally 里；拿不到句柄 / 宿主没这个成员时照常抓（最多画面里多个岛体）。
    /// </summary>
    internal static class ScreenCapture
    {
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpHideWindow = 0x0080;

        private const uint Flags = SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate;

        /// <summary>
        /// 抓所有显示器组成的整块桌面，返回 PNG 字节。失败时 error 里有原因。
        ///
        /// hideIsland = 抓之前把灵动岛藏起来（设置里可关）。
        /// setDim 是"让插件自己的组件先淡出/再淡入"的钩子 —— 宿主只提供瞬间的
        /// SWP_HIDEWINDOW，整岛没法渐隐，所以我们至少让自己那块先淡掉，看起来不那么"啪一下没"。
        /// </summary>
        internal static byte[] CaptureAllScreens(bool hideIsland, Action<float> setDim, out string error)
        {
            error = null;

            try
            {
                Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    error = "拿不到屏幕尺寸";
                    return null;
                }

                IntPtr island = IntPtr.Zero;
                bool hidden = false;

                try
                {
                    try { island = NotchWindow.InstanceHandle; }
                    catch (Exception ex) { Log.Warn("[ScPad] 取不到岛体句柄（照常抓屏）：" + ex.Message); }

                    if (hideIsland && island != IntPtr.Zero && IsWindowVisible(island))
                    {
                        // 先让我们自己的组件淡出（~80ms，渲染线程每帧读这个值）
                        if (setDim != null) setDim(1f);
                        Thread.Sleep(80);

                        hidden = SetWindowPos(island, IntPtr.Zero, 0, 0, 0, 0, Flags | SwpHideWindow);
                        if (hidden) Thread.Sleep(90);
                    }

                    using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size,
                                CopyPixelOperation.SourceCopy);
                        }
                        return ClipboardOps.EncodePng(bitmap);
                    }
                }
                finally
                {
                    if (setDim != null) setDim(0f);   // 让组件淡回来

                    if (hidden)
                    {
                        if (!SetWindowPos(island, IntPtr.Zero, 0, 0, 0, 0, Flags | SwpShowWindow))
                            Log.Error("[ScPad] 抓屏后恢复岛体失败，请重启灵动岛", null);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Warn("[ScPad] 抓屏失败：" + ex.Message);
                return null;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
