using System;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 新手引导的四张教学图（首次安装时生成到插件数据目录，再作为图片加入截图夹）。
    ///
    /// 为什么画成真图片而不是面板里的"虚拟卡片"：
    ///   * 用户可以像普通截图一样**点开看、拖出去、编辑、删除** —— 顺便就学会了整套操作；
    ///   * 它们和截图一样从右往左排（第 1 课在最右），隐含告诉用户"新截图从右边进来"；
    ///   * 全部用代码绘制，没有外部图片资源，不联网、不写用户图片目录。
    ///
    /// 顺序：1/4 截图 · 2/4 挂上图片 · 3/4 编辑图片 · 4/4 收集模式。
    /// </summary>
    internal static class GuideImages
    {
        private const int Width = 560;
        private const int Height = 420;

        private static readonly SKColor Background = new SKColor(24, 25, 28);
        private static readonly SKColor Card = new SKColor(34, 36, 40);
        private static readonly SKColor Text = new SKColor(238, 240, 244);
        private static readonly SKColor Muted = new SKColor(150, 155, 165);
        private static readonly SKColor Accent = new SKColor(88, 214, 141);
        private static readonly SKColor Blue = new SKColor(86, 160, 245);
        private static readonly SKColor Red = new SKColor(232, 76, 82);

        internal static byte[] Render(int index)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var bitmap = new SKBitmap(info))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    Draw(canvas, index);
                }

                using (var image = SKImage.FromBitmap(bitmap))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 96))
                {
                    return data == null ? null : data.ToArray();
                }
            }
        }

        private static void Draw(SKCanvas canvas, int index)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                canvas.Clear(Background);

                // 顶部标题
                string title;
                string subtitle;
                switch (index)
                {
                    case 1: title = "1 / 4 · 截图"; subtitle = "按 Ctrl + PrtSc，或用 Win + Shift + S"; break;
                    case 2: title = "2 / 4 · 挂上图片"; subtitle = "把图片拖到岛上，或复制后自动收进来"; break;
                    case 3: title = "3 / 4 · 编辑图片"; subtitle = "单击图片（出现「已复制」）→ 点「图片编辑」"; break;
                    default: title = "4 / 4 · 收集模式"; subtitle = "自动收集 · 手动收集 · 关闭收集"; break;
                }

                DrawText(canvas, title, 28f, 46f, 26f, true, Text);
                DrawText(canvas, subtitle, 28f, 76f, 15f, false, Muted);

                // 中间图示区
                var area = new SKRect(28f, 100f, Width - 28f, Height - 96f);
                paint.Color = Card;
                canvas.DrawRoundRect(area, 14f, 14f, paint);

                switch (index)
                {
                    case 1: DrawKeys(canvas); break;
                    case 2: DrawDropArrow(canvas, area); break;
                    case 3: DrawEdit(canvas, area); break;
                    default: DrawModes(canvas, area); break;
                }

                // 底部提示
                DrawText(canvas, "看完点这张图顶部的小夹子 → 取下，就能删掉它", 28f, Height - 46f, 14f, false, Muted);
                DrawText(canvas, "ScPad · NotchPeninsula 截图夹", 28f, Height - 24f, 12f, false, new SKColor(110, 115, 125));
            }
        }

        private static void DrawKeys(SKCanvas canvas)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;

                // Ctrl 键帽
                var ctrl = new SKRect(70f, 190f, 210f, 262f);
                paint.Color = new SKColor(52, 56, 62);
                canvas.DrawRoundRect(ctrl, 12f, 12f, paint);
                DrawTextIn(canvas, ctrl, "Ctrl", 30f, true, Text);

                DrawText(canvas, "+", 222f, 234f, 26f, false, Muted);

                // PrtSc 键帽（高亮）
                var prtsc = new SKRect(252f, 190f, 480f, 262f);
                paint.Color = new SKColor(40, 62, 48);
                canvas.DrawRoundRect(prtsc, 12f, 12f, paint);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2f;
                paint.Color = Accent;
                canvas.DrawRoundRect(prtsc, 12f, 12f, paint);
                paint.Style = SKPaintStyle.Fill;
                DrawTextIn(canvas, prtsc, "PrtSc", 30f, true, Accent);

                DrawText(canvas, "截完会自动存进「图片\\屏幕截图」，并出现在岛上", 70f, 310f, 15f, false, Muted);
            }
        }

        private static void DrawDropArrow(SKCanvas canvas, SKRect area)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;

                float cx = area.MidX;
                float top = area.Top + 40f;

                // 向下的箭头
                paint.Color = Blue;
                paint.StrokeWidth = 6f;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeCap = SKStrokeCap.Round;
                canvas.DrawLine(cx, top, cx, top + 96f, paint);
                canvas.DrawLine(cx - 20f, top + 74f, cx, top + 98f, paint);
                canvas.DrawLine(cx + 20f, top + 74f, cx, top + 98f, paint);

                // 虚线图片框
                paint.StrokeWidth = 2f;
                paint.Color = new SKColor(90, 95, 105);
                var box = new SKRect(cx - 110f, top + 120f, cx + 110f, top + 220f);
                canvas.DrawRoundRect(box, 12f, 12f, paint);

                DrawText(canvas, "图片", cx, top + 180f, 16f, false, Muted, SKTextAlign.Center);
                DrawText(canvas, "或者点面板里的空槽 +，从剪贴板上传", area.MidX, area.Bottom - 24f, 15f, false, Muted, SKTextAlign.Center);
            }
        }

        private static void DrawEdit(SKCanvas canvas, SKRect area)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;

                float cx = area.MidX;
                float top = area.Top + 36f;

                var pick = new SKRect(cx - 150f, top, cx + 150f, top + 120f);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2f;
                paint.Color = Accent;
                canvas.DrawRoundRect(pick, 12f, 12f, paint);

                DrawText(canvas, "单击图片 → 已复制", cx, top + 48f, 17f, true, Text, SKTextAlign.Center);
                DrawText(canvas, "再点顶部的「图片编辑」", cx, top + 80f, 15f, false, Muted, SKTextAlign.Center);

                // 画笔
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 6f;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.Color = Blue;
                canvas.DrawLine(cx - 120f, top + 190f, cx + 20f, top + 140f, paint);
                paint.Style = SKPaintStyle.Fill;
                paint.Color = Blue;
                canvas.DrawCircle(cx - 122f, top + 192f, 7f, paint);

                DrawText(canvas, "默认用系统「照片」，可在设置里改成「画图」", area.MidX, area.Bottom - 24f, 15f, false, Muted, SKTextAlign.Center);
            }
        }

        private static void DrawModes(SKCanvas canvas, SKRect area)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;

                float cx = area.MidX;
                float cy = area.Top + 90f;
                float step = 152f;

                SKColor[] colors = new SKColor[] { Accent, Blue, Red };
                string[] labels = new string[] { "自动收集", "手动收集", "关闭收集" };
                string[] notes = new string[] { "剪贴板 / 目录 / 快捷键", "只收主动操作", "全部停止" };

                for (int i = 0; i < 3; i++)
                {
                    float x = cx + (i - 1) * step;
                    paint.Color = colors[i];
                    canvas.DrawCircle(x, cy, 18f, paint);
                    DrawText(canvas, labels[i], x, cy + 44f, 16f, true, Text, SKTextAlign.Center);
                    DrawText(canvas, notes[i], x, cy + 70f, 11f, false, Muted, SKTextAlign.Center);
                }

                DrawText(canvas, "点面板顶部中间的状态块就能切换", area.MidX, area.Bottom - 24f, 15f, false, Muted, SKTextAlign.Center);
            }
        }

        // ---- 文字 ----

        private static SKTypeface _typeface;

        private static SKTypeface Typeface()
        {
            if (_typeface != null) return _typeface;

            string[] candidates = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    var face = SKTypeface.FromFamilyName(candidates[i]);
                    if (face != null)
                    {
                        _typeface = face;
                        return face;
                    }
                }
                catch { }
            }

            _typeface = SKTypeface.Default;
            return _typeface;
        }

        private static void DrawText(SKCanvas canvas, string text, float x, float y, float size, bool bold, SKColor color)
        {
            DrawText(canvas, text, x, y, size, bold, color, SKTextAlign.Left);
        }

        private static void DrawText(SKCanvas canvas, string text, float x, float y, float size, bool bold, SKColor color, SKTextAlign align)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Color = color;
                paint.TextSize = size;
                paint.Typeface = Typeface();
                paint.TextAlign = align;
                paint.FakeBoldText = bold;
                canvas.DrawText(text, x, y, paint);
            }
        }

        private static void DrawTextIn(SKCanvas canvas, SKRect box, string text, float size, bool bold, SKColor color)
        {
            DrawText(canvas, text, box.MidX, box.MidY + size * 0.35f, size, bold, color, SKTextAlign.Center);
        }
    }
}
