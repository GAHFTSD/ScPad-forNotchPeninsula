using System;
using NotchPeninsula;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 绘制小工具：字体现取现用、临时画笔用完即释放（严格遵守插件文档第六 / 第九节）。
    /// 颜色一律从宿主主题的 TextColor 派生，浅色 / 深色主题下都不会瞎。
    /// </summary>
    internal static class Ui
    {
        internal const float TitleSize = 13.5f;
        internal const float BodySize = 11.5f;
        internal const float SmallSize = 10.5f;

        /// <summary>借宿主的正文字体；拿不到就返回 null（Skia 用默认字体）。绝不缓存、绝不释放。</summary>
        internal static SKTypeface Font()
        {
            try { return FontConfig.Normal; } catch { }
            try { return FontConfig.Fallback; } catch { }
            return null;
        }

        internal static SKTypeface BoldFont()
        {
            try { return FontConfig.SemiBold; } catch { }
            try { return FontConfig.Normal; } catch { }
            return null;
        }

        internal static SKColor Ink(WidgetFrame frame, byte alpha)
        {
            return frame.Theme.TextColor.WithAlpha(alpha);
        }

        /// <summary>把「本帧合成透明度」按百分比折进去（跟随宿主淡入 / 叠化）。</summary>
        internal static byte Alpha(byte frameAlpha, int percent)
        {
            int value = frameAlpha * percent / 255;
            if (value < 0) value = 0;
            if (value > 255) value = 255;
            return (byte)value;
        }

        internal static SKColor Sub(WidgetFrame frame, byte alpha)
        {
            SKColor color = frame.Theme.SubTextColor;
            if (color.Alpha == 0) color = frame.Theme.TextColor;
            return color.WithAlpha(alpha);
        }

        internal static void FillRound(SKCanvas canvas, SKRect rect, float radius, SKColor color)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Fill;
                paint.Color = color;
                canvas.DrawRoundRect(rect, radius, radius, paint);
            }
        }

        internal static void FillCircle(SKCanvas canvas, float cx, float cy, float radius, SKColor color)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Fill;
                paint.Color = color;
                canvas.DrawCircle(cx, cy, radius, paint);
            }
        }

        internal static void StrokeRound(SKCanvas canvas, SKRect rect, float radius, SKColor color, float width)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = width;
                paint.Color = color;
                canvas.DrawRoundRect(rect, radius, radius, paint);
            }
        }

        internal static void StrokeCircle(SKCanvas canvas, float cx, float cy, float radius, SKColor color, float width)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = width;
                paint.Color = color;
                canvas.DrawCircle(cx, cy, radius, paint);
            }
        }

        internal static void Line(SKCanvas canvas, float x0, float y0, float x1, float y1, SKColor color, float width)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = width;
                paint.Color = color;
                canvas.DrawLine(x0, y0, x1, y1, paint);
            }
        }

        internal static float Measure(string text, float size, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.TextSize = size;
                paint.Typeface = bold ? BoldFont() : Font();
                return paint.MeasureText(text);
            }
        }

        /// <summary>画一行字。maxWidth &gt; 0 时超宽会用省略号截断，绝不溢出。</summary>
        internal static void Text(SKCanvas canvas, string text, float x, float baseline, float size, SKColor color,
            bool bold, float maxWidth, SKTextAlign align)
        {
            if (string.IsNullOrEmpty(text)) return;

            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.TextSize = size;
                paint.Typeface = bold ? BoldFont() : Font();
                paint.Color = color;
                paint.TextAlign = align;

                string shown = maxWidth > 0f ? Fit(paint, text, maxWidth) : text;
                if (shown.Length == 0) return;
                canvas.DrawText(shown, x, baseline, paint);
            }
        }

        /// <summary>画一行字，超宽时**保留尾部**（路径这类内容尾部信息更多）。</summary>
        internal static void TextTail(SKCanvas canvas, string text, float x, float baseline, float size,
            SKColor color, bool bold, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return;

            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.TextSize = size;
                paint.Typeface = bold ? BoldFont() : Font();
                paint.Color = color;
                paint.TextAlign = SKTextAlign.Left;

                string shown = text;
                if (maxWidth > 0f && paint.MeasureText(text) > maxWidth)
                {
                    const string Ellipsis = "…";
                    float tail = paint.MeasureText(Ellipsis);
                    int low = 0;
                    int high = text.Length;
                    while (low < high)
                    {
                        int mid = (low + high + 1) / 2;
                        if (paint.MeasureText(text.Substring(text.Length - mid)) + tail <= maxWidth) low = mid;
                        else high = mid - 1;
                    }
                    shown = low <= 0 ? "" : Ellipsis + text.Substring(text.Length - low);
                }

                if (shown.Length == 0) return;
                canvas.DrawText(shown, x, baseline, paint);
            }
        }

        internal static string Fit(SKPaint paint, string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (paint.MeasureText(text) <= maxWidth) return text;

            const string Ellipsis = "…";
            float tail = paint.MeasureText(Ellipsis);
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (paint.MeasureText(text.Substring(0, mid)) + tail <= maxWidth) low = mid;
                else high = mid - 1;
            }
            if (low <= 0) return "";
            return text.Substring(0, low) + Ellipsis;
        }

        /// <summary>把位图等比「放进」目标区域（contain），返回居中后的目标矩形。</summary>
        internal static SKRect FitRect(SKRect area, int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return area;

            float scale = Math.Min(area.Width / pixelWidth, area.Height / pixelHeight);
            float width = pixelWidth * scale;
            float height = pixelHeight * scale;
            float left = area.MidX - width * 0.5f;
            float top = area.MidY - height * 0.5f;
            return new SKRect(left, top, left + width, top + height);
        }

        /// <summary>按位图绘制，带透明度与高质量缩放。</summary>
        internal static void Bitmap(SKCanvas canvas, SKBitmap bitmap, SKRect destination, byte alpha)
        {
            if (bitmap == null || alpha == 0) return;
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.FilterQuality = SKFilterQuality.High;
                paint.Color = SKColors.White.WithAlpha(alpha);
                canvas.DrawBitmap(bitmap, destination, paint);
            }
        }

        /// <summary>文字基线：把「视觉居中」换算成 baseline。</summary>
        internal static float Baseline(float centerY, float size)
        {
            return centerY + size * 0.36f;
        }
    }
}
