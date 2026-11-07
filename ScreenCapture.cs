using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace ScPad
{
    /// <summary>
    /// 整屏截图。
    ///
    /// 「先藏岛」不再用 SetWindowPos(SWPHIDEWINDOW) 直接“啪”一下抹掉，而是走宿主的
    /// 显示/隐藏灵动岛入口（和「自动隐藏」共用同一条退场动画）：先把岛收到屏幕顶端，
    /// 再抓屏；抓完再走同一条动画回来。
    ///
    /// 宿主入口完全走反射（见 HostIsland）：不同 2.2.0 构建里成员名/可见性并不稳定，
    /// 拿不到就按原样抓屏 —— **任何情况下都不能因为藏岛失败而丢掉截图**。
    /// </summary>
    internal static class ScreenCapture
    {
        /// <summary>等退场动画走完的时间（宿主弹簧约 0.45 秒）。</summary>
        private const int HideWaitMs = 520;

        /// <summary>
        /// 抓所有显示器组成的整块桌面，返回 PNG 字节。失败时 error 里有原因。
        /// hideIsland = 抓之前让灵动岛走隐藏动画收起（设置项，默认关）。
        /// </summary>
        internal static byte[] CaptureAllScreens(bool hideIsland, out string error)
        {
            error = null;

            bool hid = false;

            try
            {
                Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    error = "拿不到屏幕尺寸";
                    return null;
                }

                if (hideIsland)
                {
                    try
                    {
                        hid = HostIsland.Hide();
                        // 走动画收起要给宿主时间；本来就藏着就只等一帧。
                        Thread.Sleep(hid ? HideWaitMs : 80);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[ScPad] 藏岛失败，按原样抓屏：" + ex.Message);
                        hid = false;
                    }
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
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Warn("[ScPad] 抓屏失败：" + ex.Message);
                return null;
            }
            finally
            {
                if (hid)
                {
                    try { HostIsland.Restore(); }
                    catch (Exception ex) { Log.Warn("[ScPad] 抓屏后唤回灵动岛失败：" + ex.Message); }
                }
            }
        }
    }
}
