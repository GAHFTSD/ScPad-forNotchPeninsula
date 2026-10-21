using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 剪贴板读写（必须在 STA 线程调用 —— CaptureService 的专用线程就是 STA）。
    ///
    /// 写入格式跟 Snapline 原版保持一致：PNG 流 + 位图（CF_DIB / CF_BITMAP）+ 文件路径，
    /// 这样「复制」既能粘到聊天窗口当图片，也能在资源管理器里当文件粘贴。
    /// 另外写一个 ScPad.Origin 标记，采集循环看到它就跳过，避免自己复制的东西又被挂回来。
    /// </summary>
    internal static class ClipboardOps
    {
        internal const string OriginFormat = "ScPad.Origin";
        internal const string OriginValue = "NotchPeninsula.ScPad";

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        internal static uint Sequence()
        {
            try { return GetClipboardSequenceNumber(); }
            catch { return 0u; }
        }

        /// <summary>把一张图片文件放进剪贴板（图片 + PNG + 文件路径三种形态）。</summary>
        internal static bool CopyImage(string path, out string error)
        {
            error = null;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "文件不存在";
                    return false;
                }

                byte[] png = File.ReadAllBytes(path);

                // ⚠️ 这两个对象交给剪贴板后会一直被引用（OLE 要拿它们渲染），
                // 所以这里**不** Dispose：GDI+ 要求 Bitmap 的源流保持打开，
                // 提前释放会让别的程序粘贴时拿到一张坏图。它们很小，交给你 GC。
                var stream = new MemoryStream(png);
                Bitmap bitmap = null;
                try { bitmap = new Bitmap(stream); }
                catch (Exception ex) { Log.Warn("[ScPad] 解码图片失败（只放 PNG）: " + ex.Message); }

                var data = new DataObject();
                try { data.SetData("PNG", new MemoryStream(png)); }
                catch (Exception ex) { Log.Warn("[ScPad] 写入 PNG 格式失败：" + ex.Message); }

                if (bitmap != null)
                {
                    try { data.SetImage(bitmap); }
                    catch (Exception ex) { Log.Warn("[ScPad] 写入位图格式失败：" + ex.Message); }
                }

                try { data.SetData(DataFormats.FileDrop, new string[] { path }); }
                catch (Exception ex) { Log.Warn("[ScPad] 写入文件路径失败：" + ex.Message); }

                try { data.SetData(OriginFormat, OriginValue); }
                catch { }

                // 剪贴板被别的程序占着是常事，重试几次就好
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Clipboard.SetDataObject(data, true);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        Thread.Sleep(120);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>读剪贴板里的图片，返回 PNG 字节；没有图片 / 是自己写进去的就返回 null。</summary>
        internal static byte[] TryReadImage()
        {
            IDataObject data;
            try { data = Clipboard.GetDataObject(); }
            catch { return null; }
            if (data == null) return null;

            try
            {
                string[] formats = data.GetFormats(false);
                if (formats != null && Array.IndexOf(formats, OriginFormat) >= 0) return null;
            }
            catch { }

            // 1) 首选 PNG（保留透明通道，质量也最好）
            try
            {
                var stream = data.GetData("PNG") as Stream;
                if (stream != null)
                {
                    using (stream)
                    {
                        var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        byte[] png = buffer.ToArray();
                        if (png.Length > 0) return png;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("[ScPad] 读取 PNG 剪贴板失败：" + ex.Message); }

            // 2) 退回位图（截图工具通常只给 CF_DIB）
            try
            {
                using (Image image = Clipboard.GetImage())
                {
                    if (image == null) return null;
                    return EncodePng(image);
                }
            }
            catch (Exception ex) { Log.Warn("[ScPad] 读取位图剪贴板失败：" + ex.Message); }

            return null;
        }

        /// <summary>
        /// 把 GDI+ 位图转成 PNG。截图来源的 DIB 经常把 alpha 全写成 0（全透明），
        /// 原版 Snapline 也是这么修的：这里统一按不透明处理。
        /// 整屏抓图（ScreenCapture）也复用它。
        /// </summary>
        internal static byte[] EncodePng(Image source)
        {
            Bitmap bitmap = source as Bitmap;
            bool dispose = false;
            if (bitmap == null)
            {
                bitmap = new Bitmap(source);
                dispose = true;
            }

            try
            {
                int width = bitmap.Width;
                int height = bitmap.Height;
                if (width <= 0 || height <= 0) return null;

                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using (var skia = new SKBitmap(info))
                {
                    BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int bytesPerRow = width * 4;
                        var row = new byte[bytesPerRow];
                        IntPtr source_ = locked.Scan0;
                        IntPtr target = skia.GetPixels();

                        for (int y = 0; y < height; y++)
                        {
                            Marshal.Copy(new IntPtr(source_.ToInt64() + (long)y * locked.Stride), row, 0, bytesPerRow);
                            for (int i = 3; i < bytesPerRow; i += 4) row[i] = 255;
                            Marshal.Copy(row, 0, new IntPtr(target.ToInt64() + (long)y * skia.RowBytes), bytesPerRow);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(locked);
                    }

                    using (SKImage image = SKImage.FromBitmap(skia))
                    using (SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100))
                    {
                        return encoded == null ? null : encoded.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 位图转 PNG 失败：" + ex.Message);
                return null;
            }
            finally
            {
                if (dispose) bitmap.Dispose();
            }
        }
    }
}
