using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 灵动岛上的收起态组件：一条小截图夹上挂着最近两张截图，后面跟一个张数小胶囊。
    ///
    /// 「不影响媒体」的具体做法：
    ///   * 没有任何截图（且没暂停）时 MeasureWidth 返回 0 —— 宿主会把这个组件整个隐藏，
    ///     岛体宽度、媒体控制器一个像素都不受影响；
    ///   * 有截图时也只占 70~108 逻辑像素，媒体标题再长也不会被它挤掉（装不下时宿主会
    ///     优先保证原生内容，本组件整块不显示）；
    ///   * 不做任何媒体相关操作、不抢焦点、不弹窗。
    ///
    /// 交互：点缩略图 = 复制这张（可直接 Ctrl+V 粘贴）；点胶囊 / 空白 = 展开截图夹面板；
    /// 右键沿用宿主默认行为（有详情页 → 展开面板）。
    /// </summary>
    internal sealed class ScPadWidget : IWidget
    {
        private const float Pad = 4f;
        private const float CardW = 34f;
        private const float CardH = 25f;
        private const float Gap = 4f;
        private const float ChipW = 22f;
        private const float ChipH = 16f;

        private readonly ScPadPlugin _plugin;
        private readonly ShotStore _store;

        private IPluginHost _host;

        internal ScPadWidget(ScPadPlugin plugin, ShotStore store, IPluginHost host)
        {
            _plugin = plugin;
            _store = store;
            _host = host;
        }

        public string Id { get { return ScPadPlugin.WidgetId; } }

        public string DisplayName { get { return "ScPad 截图夹"; } }

        public IDetailPage DetailPage { get { return _plugin.Panel; } }

        /// <summary>把图片拖到收起态的图标上时，自动展开截图夹并把图片挂进来。</summary>
        public bool AcceptsFileDropWhenCollapsed { get { return true; } }

        public bool AcceptsRightClick { get { return false; } }

        public bool AcceptsDoubleClick { get { return false; } }

        public float MeasureWidth(float availableHeight)
        {
            Shot[] shots = _store.Snapshot;
            if (shots.Length == 0) return _store.Paused ? 78f : 0f;

            int count = shots.Length > 2 ? 2 : shots.Length;
            return Pad + count * CardW + (count - 1) * Gap + 6f + ChipW + Pad;
        }

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            if (canvas == null || frame.Alpha == 0) return;

            Shot[] shots = _store.Snapshot;
            bool paused = _store.Paused;

            if (shots.Length == 0)
            {
                if (paused) DrawPausedPill(canvas, rect, frame);
                return;
            }

            int count = shots.Length > 2 ? 2 : shots.Length;

            // 高度按宿主给的来（原生内容高度变了也贴得住），宽度等比缩一下保持形状
            float cardHeight = Math.Min(CardH, Math.Max(8f, rect.Height - 4f));
            float zoom = cardHeight / CardH;
            float cardWidth = CardW * zoom;

            float chipWidth = ChipW;
            float totalWidth = Pad + count * cardWidth + (count - 1) * Gap + 6f + chipWidth + Pad;
            float left = rect.Left + Math.Max(0f, (rect.Width - totalWidth) * 0.5f);
            float centerY = rect.MidY;
            float top = centerY - cardHeight * 0.5f;

            SKColor ink = frame.Theme.TextColor;

            // 截图夹
            float railX0 = left + Pad;
            float railX1 = left + totalWidth - Pad;
            Ui.Line(canvas, railX0, top - 2.5f, railX1, top - 2.5f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 70)), 1f);

            // 最近的一张在最左
            for (int i = 0; i < count; i++)
            {
                int index = shots.Length - 1 - i;
                if (index < 0) break;

                float x = left + Pad + i * (cardWidth + Gap);
                var body = new SKRect(x, top, x + cardWidth, top + cardHeight);
                DrawMiniCard(canvas, body, shots[index], frame, i == 0);
            }

            // 张数胶囊
            float chipX = left + Pad + count * (cardWidth + Gap) - Gap + 6f;
            var chip = new SKRect(chipX, centerY - ChipH * 0.5f, chipX + chipWidth, centerY + ChipH * 0.5f);
            Ui.FillRound(canvas, chip, ChipH * 0.5f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 24)));

            if (paused)
            {
                DrawPauseBars(canvas, chip, ink.WithAlpha(Ui.Alpha(frame.Alpha, 190)));
            }
            else
            {
                string text = shots.Length > 99 ? "99+" : shots.Length.ToString();
                Ui.Text(canvas, text, chip.MidX, Ui.Baseline(chip.MidY, Ui.SmallSize), Ui.SmallSize,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 210)), false, chip.Width - 4f, SKTextAlign.Center);
            }

            // 悬停时给整条轨道一点存在感
            if (frame.IsHovered)
            {
                var outline = new SKRect(left + 0.5f, top - 6f, left + totalWidth - 0.5f, top + cardHeight + 2f);
                Ui.StrokeRound(canvas, outline, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 40)), 1f);
            }
        }

        private void DrawMiniCard(SKCanvas canvas, SKRect body, Shot shot, WidgetFrame frame, bool newest)
        {
            SKColor ink = frame.Theme.TextColor;

            Ui.FillRound(canvas, body, 3.5f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 26)));
            Ui.StrokeRound(canvas, body, 3.5f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 70)), 1f);

            var inner = new SKRect(body.Left + 1.5f, body.Top + 1.5f, body.Right - 1.5f, body.Bottom - 1.5f);
            if (shot.Thumb != null)
            {
                SKRect destination = Ui.FitRect(inner, shot.Thumb.Width, shot.Thumb.Height);
                Ui.Bitmap(canvas, shot.Thumb, destination, frame.Alpha);
            }

            // 新存入来的那几秒点一个亮点（不打扰，只是告诉你「又收了一张」）
            if (newest && IsFresh(shot))
            {
                float cx = body.Right - 4.5f;
                float cy = body.Top + 4.5f;
                Ui.FillCircle(canvas, cx, cy, 2.4f, new SKColor(88, 214, 141).WithAlpha(frame.Alpha));
            }
        }

        private static bool IsFresh(Shot shot)
        {
            try
            {
                return (DateTime.Now - shot.Created).TotalSeconds < 5.0;
            }
            catch
            {
                return false;
            }
        }

        private void DrawPausedPill(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;

            float width = Math.Min(74f, rect.Width);
            var pill = new SKRect(rect.Left + (rect.Width - width) * 0.5f, rect.MidY - 11f,
                rect.Left + (rect.Width + width) * 0.5f, rect.MidY + 11f);

            Ui.FillRound(canvas, pill, 11f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 22)));
            Ui.StrokeRound(canvas, pill, 11f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 50)), 1f);

            DrawPauseBars(canvas, pill, ink.WithAlpha(Ui.Alpha(frame.Alpha, 200)));

            Ui.Text(canvas, "已暂停", pill.MidX + 9f, Ui.Baseline(pill.MidY, Ui.SmallSize), Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 190)), false, pill.Width - 24f, SKTextAlign.Center);
        }

        private static void DrawPauseBars(SKCanvas canvas, SKRect area, SKColor color)
        {
            float barWidth = 2.6f;
            float barHeight = 8f;
            float gap = 2.6f;
            float centerX = area.MidX;
            float top = area.MidY - barHeight * 0.5f;

            Ui.FillRound(canvas, new SKRect(centerX - gap * 0.5f - barWidth, top, centerX - gap * 0.5f, top + barHeight), 1.2f, color);
            Ui.FillRound(canvas, new SKRect(centerX + gap * 0.5f, top, centerX + gap * 0.5f + barWidth, top + barHeight), 1.2f, color);
        }

        public WidgetHit HitTest(float x, float y, SKRect rect)
        {
            Shot[] shots = _store.Snapshot;
            if (shots.Length == 0) return new WidgetHit("open");

            int count = shots.Length > 2 ? 2 : shots.Length;

            float cardHeight = Math.Min(CardH, Math.Max(8f, rect.Height - 4f));
            float zoom = cardHeight / CardH;
            float cardWidth = CardW * zoom;

            float chipWidth = ChipW;
            float totalWidth = Pad + count * cardWidth + (count - 1) * Gap + 6f + chipWidth + Pad;
            float left = rect.Left + Math.Max(0f, (rect.Width - totalWidth) * 0.5f);
            float top = rect.MidY - cardHeight * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float x0 = left + Pad + i * (cardWidth + Gap);
                float x1 = x0 + cardWidth;
                if (x < x0 - 1f || x > x1 + 1f) continue;
                if (y < top - 4f || y > top + cardHeight + 2f) continue;
                return new WidgetHit("c" + i.ToString());
            }

            return new WidgetHit("open");
        }

        public void OnLeftClick(string action, float x, float y)
        {
            if (action == null) return;

            if (action.Length > 1 && action[0] == 'c')
            {
                int offset;
                if (!int.TryParse(action.Substring(1), out offset)) offset = 0;
                CopyFromIsland(offset);
                return;
            }

            _plugin.OpenPanel();
        }

        private void CopyFromIsland(int offset)
        {
            Shot[] shots = _store.Snapshot;
            if (offset < 0 || offset >= shots.Length) return;

            Shot shot = shots[shots.Length - 1 - offset];
            try { shot.Sway(); } catch { }
            _plugin.Capture.RequestCopy(shot);
        }

        public void OnRightClick()
        {
            // 右键的默认行为（展开详情页）由宿主完成，这里不用插手
        }

        public void OnLeftDoubleClick(float x, float y)
        {
        }

        public void OnRightDoubleClick(float x, float y)
        {
        }

        public void OnActivate(IPluginHost host)
        {
            if (host != null) _host = host;
        }

        public void OnDeactivate()
        {
            _host = null;
        }
    }
}
