using System;
using System.Collections.Generic;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 截图夹面板（右键收缩放态组件展开的详情页）。
    ///
    /// 形态（v1.0.0 起更紧凑）：
    ///   * 一条横向卡片带，**固定预留 N 格**（默认 4），空槽里点 + 可从剪贴板上传；
    ///   * 卡片从右往左填，最新在最右；超过格数点两端箭头翻页；
    ///   * 缩略图**铺满**卡片（cover 裁剪），不再上下留白；
    ///   * 卡片底部正中是「取下」按钮（↓ 在上、_ 在下）；点它把这张移出列表；
    ///   * 面板底部可选一条「设置区」：更多设置 / 停止收集 / 图片编辑（设置里可关）。
    ///
    /// 动效：弹簧 + 阻尼（新图、翻页、排序、上传都会让卡片按方向抖一下再荡回来）。
    ///
    /// 交互：单击图片 = 复制；按住拖出去 = 拖出一张 PNG；
    /// 按住 Ctrl 拖 = 调整顺序；点空槽的 + = 从剪贴板上传；岛内右键 = 关闭面板。
    /// </summary>
    internal sealed class ShotClipPanel : IDetailPage
    {
        private const float Pad = 8f;
        private const float CardW = 140f;
        private const float CardH = 132f;
        private const float Gap = 8f;

        private const float ActionBarGap = 6f;
        private const float ActionBarH = 22f;

        private const float PinHeight = 9f;
        private const float ImageInset = 5f;
        private const float MetaHeight = 20f;

        private const float RemoveRadius = 9f;

        private const float DragThreshold = 5f;

        private static readonly SKColor Accent = new SKColor(88, 214, 141);

        private readonly ScPadPlugin _plugin;
        private readonly ShotStore _store;
        private readonly IPluginHost _host;
        private readonly ScrollModel _scroll = new ScrollModel();

        private readonly Dictionary<string, SpringMotion> _springs =
            new Dictionary<string, SpringMotion>(StringComparer.OrdinalIgnoreCase);

        private float _layoutW = 576f;
        private float _layoutH = 174f;

        private float _rectW = 576f;
        private float _rectH = 174f;

        private int _slots = 4;
        private int _layoutCount = 4;
        private int _count;
        private long _lastFrameTicks;
        private long _scrollImpulseTicks;
        private int _lastCount = -1;

        private bool _pressed;
        private int _pressSlot = -1;
        private int _pressRemove = -1;
        private int _pressAction = -1;
        private float _pressX;
        private float _pressY;
        private float _mouseX = -1000f;
        private float _mouseY = -1000f;

        private bool _reordering;
        private int _reorderFromSlot = -1;
        private int _reorderToSlot = -1;
        private float _dragX;

        private int _dragHover;

        private string _hintText = "";
        private long _hintUntilTicks;

        private float _dim;

        /// <summary>自检渲染演示图用：强制显示「夹子已变成取下按钮」的瞬间（真实交互不读它）。</summary>
        internal static bool PreviewRemoveHover;

        // 取下按钮的悬停动画状态
        private string _removeHoverPath;
        private long _removeHoverTicks;
        private long _frameNow;

        internal ShotClipPanel(ScPadPlugin plugin, ShotStore store, IPluginHost host)
        {
            _plugin = plugin;
            _store = store;
            _host = host;
            RecomputeLayout();
        }

        // ---- 尺寸 ----

        private void RecomputeLayout()
        {
            int slots = Math.Max(1, Math.Min(PluginSettings.MaxSlots, _store.Settings.PanelSlots));
            _slots = slots;

            int total = Math.Min(_store.Snapshot.Length, Math.Max(1, _store.Settings.MaxItems));
            _count = total;
            _layoutCount = total > slots ? total : slots;

            _layoutW = Pad * 2f + slots * CardW + (slots - 1) * Gap;
            _layoutH = Pad * 2f + CardH + (ActionsVisible ? ActionBarH + ActionBarGap : 0f);

            _rectW = _layoutW;
            _rectH = _layoutH;
        }

        private bool ActionsVisible { get { return _store.Settings.PanelActions; } }

        /// <summary>卡片带的纵向起点：设置区在顶部，开启时往下让。</summary>
        private float CardsTop { get { return ActionsVisible ? Pad + ActionBarH + ActionBarGap : Pad; } }

        public float MeasureWidth()
        {
            RecomputeLayout();
            return _layoutW;
        }

        public float MeasureHeight()
        {
            RecomputeLayout();
            return _layoutH;
        }

        public TimeSpan AutoCollapseDelay { get { return Timeout.InfiniteTimeSpan; } }

        public bool AcceptsRightClick { get { return false; } }

        public bool AcceptsDoubleClick { get { return false; } }

        /// <summary>面板就地提示（不弹通知）。</summary>
        internal void ShowHint(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _hintText = text;
            _hintUntilTicks = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMillisecond * 2200L;
        }

        internal void ClearHint()
        {
            _hintText = "";
            _hintUntilTicks = 0;
        }

        internal void SetDim(float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            _dim = amount;
        }

        // ---- 绘制 ----

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            if (canvas == null || frame.Alpha == 0) return;
            if (_dim >= 0.99f) return;

            WheelRouter.MarkPanelDrawn();

            _rectW = rect.Width;
            _rectH = rect.Height;

            float scale = _layoutH > 1f ? rect.Height / _layoutH : 1f;
            if (scale > 1f) scale = 1f;
            if (scale < 0.05f) scale = 0.05f;

            float left = rect.Left + rect.Width * 0.5f - _layoutW * scale * 0.5f;
            float top = rect.Top + rect.Height * 0.5f - _layoutH * scale * 0.5f;

            canvas.Save();
            canvas.Translate(left, top);
            canvas.Scale(scale);

            bool dimmed = _dim > 0.01f;
            if (dimmed)
            {
                byte layerAlpha = (byte)Math.Round(255.0 * (1.0 - _dim));
                using (var paint = new SKPaint())
                {
                    paint.Color = new SKColor(255, 255, 255, layerAlpha);
                    canvas.SaveLayer(paint);
                }
            }

            try
            {
                DrawContent(canvas, frame);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 面板绘制异常", ex);
            }
            finally
            {
                if (dimmed) canvas.Restore();
                canvas.Restore();
            }
        }

        private void DrawContent(SKCanvas canvas, WidgetFrame frame)
        {
            Shot[] shots = _store.Snapshot;
            int total = Math.Min(shots.Length, Math.Max(1, _store.Settings.MaxItems));

            long now = DateTime.UtcNow.Ticks;
            float dt = _lastFrameTicks == 0L ? 0f : (float)((now - _lastFrameTicks) / (double)TimeSpan.TicksPerSecond);
            _lastFrameTicks = now;
            _frameNow = now;

            if (_lastCount >= 0 && total > _lastCount) OnNewShots(total - _lastCount, total);
            _lastCount = total;
            _count = total;

            int layoutCount = total > _slots ? total : _slots;
            _layoutCount = layoutCount;
            float contentW = layoutCount * (CardW + Gap) - Gap;
            float viewportW = _slots * CardW + (_slots - 1) * Gap;
            _scroll.SetMax(Math.Max(0f, contentW - viewportW));
            _scroll.Update(dt);

            if (_scroll.Velocity != 0f && now - _scrollImpulseTicks > TimeSpan.TicksPerMillisecond * 90L)
            {
                _scrollImpulseTicks = now;
                float impulse = _scroll.Velocity * 0.02f;
                if (impulse > 70f) impulse = 70f;
                if (impulse < -70f) impulse = -70f;
                for (int i = 0; i < total; i++) SpringOf(shots[shots.Length - 1 - i]).Impulse(impulse);
            }

            for (int i = 0; i < total; i++) SpringOf(shots[shots.Length - 1 - i]).Step(dt);

            float contentLeft = Pad + viewportW - contentW + _scroll.Offset;

            float cardTop = CardsTop;
            canvas.Save();
            canvas.ClipRect(new SKRect(Pad - 2f, 0f, _layoutW - Pad + 2f, cardTop + CardH + 4f));
            try
            {
                    int hover = _reordering ? -1 : HitSlot(_mouseX, _mouseY);

                    for (int slot = _slots - 1; slot >= 0; slot--)
                    {
                        float x = SlotLeft(slot, contentLeft);

                        if (slot >= total)
                        {
                            DrawEmptySlot(canvas, new SKRect(x, cardTop, x + CardW, cardTop + CardH), frame);
                            continue;
                        }

                        if (_reordering && slot == _reorderFromSlot) continue;

                        if (_reordering && _reorderFromSlot >= 0 && _reorderToSlot >= 0)
                        {
                            int lo = Math.Min(_reorderFromSlot, _reorderToSlot);
                            int hi = Math.Max(_reorderFromSlot, _reorderToSlot);
                            if (slot > lo && slot < hi)
                            {
                                x += (_reorderToSlot > _reorderFromSlot ? -1f : 1f) * (CardW + Gap) * 0.35f;
                            }
                        }

                        Shot shot = shots[shots.Length - 1 - slot];
                        float sway = SpringOf(shot).Value;
                        float tilt = (float)((float)shot.Tilt * 0.5 + sway * 0.05 + Wobble(shot));

                        DrawCard(canvas, new SKRect(x + sway * 0.12f, cardTop, x + CardW + sway * 0.12f, cardTop + CardH),
                            shot, frame, slot == hover, tilt, slot);
                    }

                    if (_reordering && _reorderFromSlot >= 0 && _reorderFromSlot < total)
                    {
                        Shot dragging = shots[shots.Length - 1 - _reorderFromSlot];
                        var body = new SKRect(_dragX - CardW * 0.5f, cardTop - 6f, _dragX + CardW * 0.5f, cardTop - 6f + CardH);
                        DrawCard(canvas, body, dragging, frame, false, 3.5f, _reorderFromSlot);
                    }

            }
            finally
            {
                canvas.Restore();
            }

            DrawScrollHint(canvas, frame);
            if (ActionsVisible) DrawActionBar(canvas, frame);
            DrawHint(canvas, frame, now);
            DrawDropOverlay(canvas, frame);
        }

        private float SlotLeft(int slot, float contentLeft)
        {
            return contentLeft + (_layoutCount - 1 - slot) * (CardW + Gap);
        }

        private void DrawEmptySlot(SKCanvas canvas, SKRect body, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            Ui.StrokeRound(canvas, body, 8f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 26)), 1.2f);

            float cx = body.MidX;
            float cy = body.MidY;
            byte plusAlpha = Ui.Alpha(frame.Alpha, 55);
            Ui.Line(canvas, cx - 6f, cy, cx + 6f, cy, ink.WithAlpha(plusAlpha), 1.3f);
            Ui.Line(canvas, cx, cy - 6f, cx, cy + 6f, ink.WithAlpha(plusAlpha), 1.3f);
        }

        private void DrawScrollHint(SKCanvas canvas, WidgetFrame frame)
        {
            if (!_scroll.Overflow) return;

            SKColor ink = frame.Theme.TextColor;
            byte alpha = Ui.Alpha(frame.Alpha, 110);
            float midY = CardsTop + CardH * 0.5f;

            if (_scroll.Offset < _scroll.Max - 1f)
            {
                float ax = Pad - 4f;
                Ui.Line(canvas, ax + 2.5f, midY - 4f, ax, midY, ink.WithAlpha(alpha), 1.4f);
                Ui.Line(canvas, ax, midY, ax + 2.5f, midY + 4f, ink.WithAlpha(alpha), 1.4f);
            }
            if (_scroll.Offset > 1f)
            {
                float ax = _layoutW - Pad + 4f;
                Ui.Line(canvas, ax - 2.5f, midY - 4f, ax, midY, ink.WithAlpha(alpha), 1.4f);
                Ui.Line(canvas, ax, midY, ax - 2.5f, midY + 4f, ink.WithAlpha(alpha), 1.4f);
            }
        }

        private void DrawActionBar(SKCanvas canvas, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            int mode = _store.CollectMode;

            // 1：收集状态（自动=绿 / 手动=蓝 / 关闭=红），文字直接写状态，照顾色盲用户
            SKRect status = ActionRect(1);
            bool statusHover = Contains(status, _mouseX, _mouseY);
            SKColor statusColor = mode == PluginSettings.CollectAuto
                ? new SKColor(88, 214, 141)
                : mode == PluginSettings.CollectManual
                    ? new SKColor(86, 160, 245)
                    : new SKColor(232, 76, 82);

            Ui.FillRound(canvas, status, 6f, statusColor.WithAlpha(Ui.Alpha(frame.Alpha, statusHover ? 62 : 38)));

            // 状态块内：点 + 文字整体居中（三格等宽，看起来对称）
            string statusText = PluginSettings.CollectModeText(mode);
            float statusTextW = Ui.Measure(statusText, Ui.SmallSize, false);
            float groupW = statusTextW + 9f;
            float groupLeft = status.MidX - groupW * 0.5f;
            Ui.FillCircle(canvas, groupLeft + 3f, status.MidY, 3f, statusColor.WithAlpha(Ui.Alpha(frame.Alpha, 245)));
            Ui.Text(canvas, statusText, groupLeft + 9f, Ui.Baseline(status.MidY, Ui.SmallSize), Ui.SmallSize,
                statusColor.WithAlpha(Ui.Alpha(frame.Alpha, 250)), false, statusTextW + 2f, SKTextAlign.Left);

            string[] labels = new string[] { "更多设置", "图片编辑" };
            for (int i = 0; i < 3; i += 2)
            {
                SKRect r = ActionRect(i);
                bool hover = Contains(r, _mouseX, _mouseY);
                string label = i == 0 ? labels[0] : labels[1];

                Ui.FillRound(canvas, r, 6f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 44 : 22)));
                Ui.Text(canvas, label, r.MidX, Ui.Baseline(r.MidY, Ui.SmallSize), Ui.SmallSize,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 235 : 190)), false, r.Width - 8f, SKTextAlign.Center);
            }
        }

        private SKRect ActionRect(int index)
        {
            const float gap = 6f;
            float width = (_layoutW - Pad * 2f - gap * 2f) / 3f;
            float top = Pad;
            float x = Pad + index * (width + gap);
            return new SKRect(x, top, x + width, top + ActionBarH);
        }
        private void DrawHint(SKCanvas canvas, WidgetFrame frame, long now)
        {
            if (_hintText.Length == 0 || now >= _hintUntilTicks) return;

            long remaining = _hintUntilTicks - now;
            float fade = remaining > TimeSpan.TicksPerMillisecond * 400L
                ? 1f
                : (float)(remaining / (double)(TimeSpan.TicksPerMillisecond * 400L));

            byte alpha = Ui.Alpha(frame.Alpha, (byte)(205f * fade));
            float width = Ui.Measure(_hintText, 12f, false) + 24f;
            var pill = new SKRect(_layoutW * 0.5f - width * 0.5f, CardsTop + CardH - 26f,
                _layoutW * 0.5f + width * 0.5f, CardsTop + CardH - 6f);

            Ui.FillRound(canvas, pill, 8f, new SKColor(18, 18, 20).WithAlpha(alpha));
            Ui.Text(canvas, _hintText, pill.MidX, Ui.Baseline(pill.MidY, 12f), 12f,
                SKColors.White.WithAlpha(alpha), false, 0f, SKTextAlign.Center);
        }

        private void DrawDropOverlay(SKCanvas canvas, WidgetFrame frame)
        {
            if (_dragHover <= 0) return;

            var box = new SKRect(2f, 2f, _layoutW - 2f, _layoutH - 2f);
            Ui.FillRound(canvas, box, 11f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 26)));
            Ui.StrokeRound(canvas, box, 11f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 150)), 1.6f);

            string text = "松手存入 " + _dragHover.ToString() + " 张图片";
            float textWidth = Ui.Measure(text, 13f, true) + 30f;
            var pill = new SKRect(_layoutW * 0.5f - textWidth * 0.5f, _layoutH * 0.5f - 17f,
                _layoutW * 0.5f + textWidth * 0.5f, _layoutH * 0.5f + 17f);
            Ui.FillRound(canvas, pill, 10f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 240)));
            Ui.Text(canvas, text, pill.MidX, Ui.Baseline(pill.MidY, 13f), 13f,
                SKColors.White.WithAlpha(Ui.Alpha(frame.Alpha, 255)), true, 0f, SKTextAlign.Center);
        }

        private void DrawCard(SKCanvas canvas, SKRect body, Shot shot, WidgetFrame frame, bool hover, float tilt, int slot)
        {
            SKColor ink = frame.Theme.TextColor;

            canvas.Save();
            canvas.RotateDegrees(tilt, body.MidX, body.Top);


            Ui.FillRound(canvas, body, 8f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 30 : 18)));
            Ui.StrokeRound(canvas, body, 8f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 95 : 55)), 1f);

            // 图片：cover 裁剪铺满（之前 fit 会在上下留白，正是要消掉的空白）
            var imageArea = new SKRect(body.Left + ImageInset, body.Top + ImageInset,
                body.Right - ImageInset, body.Bottom - MetaHeight);
            canvas.Save();
            canvas.ClipRect(imageArea);
            try
            {
                Ui.FillRound(canvas, imageArea, 4f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 14)));
                if (shot.Thumb != null)
                {
                    Ui.Bitmap(canvas, shot.Thumb, CoverRect(imageArea, shot.Thumb.Width, shot.Thumb.Height), frame.Alpha);
                }
            }
            finally
            {
                canvas.Restore();
            }

            // 底部一行：名称（左）· 时间（右）；取下按钮已经搬到卡片顶部夹子处
            float metaBaseline = body.Bottom - 7f;
            float nameWidth = Math.Max(40f, body.Width - 52f);
            // 名称只显示 SP-X（X = 面板顺序号，最右那张是 SP-1）
            Ui.Text(canvas, "SP-" + (slot + 1).ToString(), body.Left + 6f, metaBaseline, Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 205)), false, nameWidth, SKTextAlign.Left);
            Ui.Text(canvas, shot.TimeText, body.Right - 6f, metaBaseline, Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 120)), false, 40f, SKTextAlign.Right);

            // 卡片顶部正中：平时是**夹子**；鼠标滑到夹子上才变成「取下」按钮
            float cx = body.MidX;
            float cy = body.Top + 4f;
            const float halfW = 9f;
            const float halfH = 8f;
            bool hot = hover && HitRemoveShape(body, _mouseX, _mouseY);
            float phase = RemovePhase(shot, hot);

            // 夹子 → 按钮的交叉淡入（120ms）
            float appear = 0f;
            if (hot && _removeHoverTicks != 0L)
            {
                double ms = (_frameNow - _removeHoverTicks) / (double)TimeSpan.TicksPerMillisecond;
                appear = ms >= 120.0 ? 1f : (float)(ms / 120.0);
                if (appear < 0f) appear = 0f;
                if (appear > 1f) appear = 1f;
            }

            if (PreviewRemoveHover && hot)
            {
                // 自检只画一帧，攒不够 120ms：直接给"已切换、↓ 正在下坠"的样子
                appear = 1f;
                phase = 0.35f;
            }

            if (appear < 0.99f)
            {
                // 夹子（原来的造型：竖着的小夹片，半插在卡片顶边上）
                byte pinAlpha = (byte)Math.Round(Ui.Alpha(frame.Alpha, 95) * (1f - appear));
                var pin = new SKRect(cx - 4.5f, body.Top - 14f, cx + 4.5f, body.Top + 8f);
                Ui.FillRound(canvas, pin, 4f, ink.WithAlpha(pinAlpha));
                Ui.Line(canvas, cx - 3f, body.Top - 11.5f, cx + 3f, body.Top - 11.5f,
                    ink.WithAlpha((byte)(pinAlpha * 0.65)), 1f);
            }

            if (appear > 0.01f)
            {
                // 悬停：红色圆角方形按钮 + 「_ 在上、↓ 在下」
                var plate = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                Ui.FillRound(canvas, plate, 5f, new SKColor(232, 76, 82).WithAlpha((byte)Math.Round(frame.Alpha * appear)));

                SKColor icon = SKColors.White.WithAlpha((byte)Math.Round(frame.Alpha * appear));
                Ui.Line(canvas, cx - 3.4f, cy - 3.2f, cx + 3.4f, cy - 3.2f, icon, 1.3f);

                float pull = phase < 0.6f
                    ? (phase / 0.6f) * 2.6f
                    : 2.6f * (1f - (phase - 0.6f) / 0.4f);
                if (pull < 0f) pull = 0f;

                float headY = cy - 1.0f + pull;
                float tipY = cy + 4.2f + pull;
                Ui.Line(canvas, cx, headY, cx, tipY, icon, 1.3f);
                Ui.Line(canvas, cx - 2.0f, tipY - 2.2f, cx, tipY, icon, 1.3f);
                Ui.Line(canvas, cx + 2.0f, tipY - 2.2f, cx, tipY, icon, 1.3f);
            }
            double age = CopiedAge(shot);
            if (age >= 0.0 && age < 1.3)
            {
                byte fade = (byte)(255 * (1.0 - age / 1.3));
                var badge = new SKRect(body.MidX - 31f, imageArea.MidY - 11f, body.MidX + 31f, imageArea.MidY + 11f);
                Ui.FillRound(canvas, badge, 6f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, fade)));

                SKColor tick = SKColors.White.WithAlpha(Ui.Alpha(frame.Alpha, fade));
                float tx = badge.Left + 12f;
                float ty = badge.MidY;
                Ui.Line(canvas, tx, ty, tx + 3.2f, ty + 3.4f, tick, 1.7f);
                Ui.Line(canvas, tx + 3.2f, ty + 3.4f, tx + 9.2f, ty - 3.8f, tick, 1.7f);
                Ui.Text(canvas, "已复制", badge.Left + 26f, Ui.Baseline(badge.MidY, Ui.SmallSize), Ui.SmallSize,
                    tick, false, badge.Width - 28f, SKTextAlign.Left);
            }

            canvas.Restore();
        }

        /// <summary>取下按钮的动画相位（0~1 循环）：非悬停恒为 1（静止）。</summary>
        private float RemovePhase(Shot shot, bool hot)
        {
            string key = shot != null ? shot.Path : "";

            if (!hot)
            {
                if (string.Equals(_removeHoverPath, key, StringComparison.OrdinalIgnoreCase)) _removeHoverPath = null;
                return 1f;
            }

            if (_removeHoverTicks == 0L || !string.Equals(_removeHoverPath, key, StringComparison.OrdinalIgnoreCase))
            {
                _removeHoverPath = key;
                _removeHoverTicks = _frameNow;
                return 0f;
            }

            double seconds = (_frameNow - _removeHoverTicks) / (double)TimeSpan.TicksPerSecond;
            const double cycle = 0.62;
            return (float)((seconds % cycle) / cycle);
        }

        /// <summary>cover：按比例放大到铺满，超出部分由调用方裁剪。</summary>
        private static SKRect CoverRect(SKRect area, int bitmapWidth, int bitmapHeight)
        {
            if (bitmapWidth <= 0 || bitmapHeight <= 0) return area;

            float scale = Math.Max(area.Width / bitmapWidth, area.Height / bitmapHeight);
            float width = bitmapWidth * scale;
            float height = bitmapHeight * scale;
            return new SKRect(area.MidX - width * 0.5f, area.MidY - height * 0.5f,
                area.MidX + width * 0.5f, area.MidY + height * 0.5f);
        }

        // ---- 弹簧 ----

        private SpringMotion SpringOf(Shot shot)
        {
            string key = shot != null ? shot.Path : "";
            SpringMotion spring;
            if (_springs.TryGetValue(key, out spring)) return spring;

            spring = new SpringMotion();
            if (_springs.Count > 64) _springs.Clear();
            _springs[key] = spring;
            return spring;
        }

        private void OnNewShots(int arrived, int total)
        {
            int added = arrived > 3 ? 3 : arrived;
            Shot[] shots = _store.Snapshot;

            for (int slot = 0; slot < total; slot++)
            {
                if (slot < added) continue;
                if (slot >= shots.Length) break;

                float distance = slot - added + 1;
                float weight = 1f / (1f + distance * 0.55f);
                float direction = (slot % 2 == 0) ? 1f : -1f;
                SpringOf(shots[shots.Length - 1 - slot]).Impulse(direction * 420f * weight);
            }
        }

        // ---- 命中 ----

        private float ContentLeft()
        {
            int layoutCount = _count > _slots ? _count : _slots;
            float contentW = layoutCount * (CardW + Gap) - Gap;
            float viewportW = _slots * CardW + (_slots - 1) * Gap;
            return Pad + viewportW - contentW + _scroll.Offset;
        }

        private int HitSlot(float lx, float ly)
        {
            if (ly < CardsTop - 8f || ly > CardsTop + CardH + 6f) return -1;

            float contentLeft = ContentLeft();
            for (int slot = 0; slot < _slots; slot++)
            {
                float x = SlotLeft(slot, contentLeft);
                if (lx >= x && lx <= x + CardW) return slot;
            }
            return -1;
        }

        private int SlotAt(float lx)
        {
            float contentLeft = ContentLeft();
            for (int slot = 0; slot < _slots; slot++)
            {
                float x = SlotLeft(slot, contentLeft);
                if (lx >= x - Gap * 0.5f && lx <= x + CardW + Gap * 0.5f) return slot;
            }
            return lx > _layoutW * 0.5f ? 0 : _slots - 1;
        }

        private static bool HitRemoveShape(SKRect body, float lx, float ly)
        {
            float cx = body.MidX;
            return Math.Abs(lx - cx) <= 11f && ly >= body.Top - 16f && ly <= body.Top + 10f;
        }

        private bool HitRemove(int slot, float lx, float ly)
        {
            float x = SlotLeft(slot, ContentLeft());
            return HitRemoveShape(new SKRect(x, CardsTop, x + CardW, CardsTop + CardH), lx, ly);
        }

        private int HitAction(float lx, float ly)
        {
            if (!ActionsVisible) return -1;
            for (int i = 0; i < 3; i++)
            {
                if (Contains(ActionRect(i), lx, ly)) return i;
            }
            return -1;
        }

        private static bool Contains(SKRect rect, float x, float y)
        {
            return rect.Width > 0f && x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
        }

        private void ToLayout(float x, float y, out float lx, out float ly)
        {
            float scale = _layoutH > 1f && _rectH > 0f ? _rectH / _layoutH : 1f;
            if (scale > 1f) scale = 1f;
            if (scale < 0.05f) scale = 0.05f;

            float ox = _rectW * 0.5f - _layoutW * scale * 0.5f;
            float oy = _rectH * 0.5f - _layoutH * scale * 0.5f;

            lx = (x - ox) / scale;
            ly = (y - oy) / scale;
        }

        // ---- 指针 ----

        public void OnMouseDown(float x, float y)
        {
            if (_plugin != null) _plugin.CancelPeek();

            float lx, ly;
            ToLayout(x, y, out lx, out ly);
            _mouseX = lx;
            _mouseY = ly;
            _dragX = lx;

            _pressed = true;
            _pressX = lx;
            _pressY = ly;
            _pressSlot = -1;
            _pressRemove = -1;
            _pressAction = -1;
            _reordering = false;
            _reorderFromSlot = -1;
            _reorderToSlot = -1;

            int action = HitAction(lx, ly);
            if (action >= 0)
            {
                _pressAction = action;
                return;
            }

            int slot = HitSlot(lx, ly);
            if (slot < 0) return;

            if (slot >= _count)
            {
                _pressed = false;
                _plugin.Capture.RequestPasteFromClipboard();
                return;
            }

            if (HitRemove(slot, lx, ly))
            {
                _pressRemove = slot;
                return;
            }

            _pressSlot = slot;
        }

        public void OnMouseMove(float x, float y)
        {
            float lx, ly;
            ToLayout(x, y, out lx, out ly);
            _mouseX = lx;
            _mouseY = ly;

            if (!_pressed || _pressSlot < 0) return;

            float dx = lx - _pressX;
            float dy = ly - _pressY;
            if (!_reordering && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

            bool ctrl = CtrlProbe != null ? CtrlProbe() : IsControlDown();
            if (!ctrl)
            {
                int slot = _pressSlot;
                _pressSlot = -1;
                _pressed = false;
                _reordering = false;
                StartDrag(slot);
                return;
            }

            _reordering = true;
            _reorderFromSlot = _pressSlot;
            _reorderToSlot = SlotAt(lx);
            _dragX = lx;
        }

        public void OnMouseUp(float x, float y)
        {
            float lx, ly;
            ToLayout(x, y, out lx, out ly);
            _mouseX = lx;
            _mouseY = ly;

            if (!_pressed && !_reordering)
            {
                _pressRemove = -1;
                _pressAction = -1;
                return;
            }

            _pressed = false;
            int remove = _pressRemove;
            int action = _pressAction;
            int slot = _pressSlot;
            _pressRemove = -1;
            _pressAction = -1;
            _pressSlot = -1;

            if (action >= 0)
            {
                if (HitAction(lx, ly) == action) RunAction(action);
                return;
            }

            if (_reordering)
            {
                int from = _reorderFromSlot;
                int to = _reorderToSlot;
                _reordering = false;
                _reorderFromSlot = -1;
                _reorderToSlot = -1;

                if (from >= 0 && to >= 0 && from != to) CommitReorder(from, to);
                return;
            }

            if (remove >= 0)
            {
                if (HitSlot(lx, ly) == remove) RemoveSlot(remove);
                return;
            }

            if (slot >= 0 && HitSlot(lx, ly) == slot) CopySlot(slot);
        }

        public void OnMouseLeave()
        {
            _pressed = false;
            _pressSlot = -1;
            _pressRemove = -1;
            _pressAction = -1;
            _reordering = false;
            _reorderFromSlot = -1;
            _reorderToSlot = -1;
            _mouseX = -1000f;
            _mouseY = -1000f;
            _dragHover = 0;
        }

        private void RunAction(int action)
        {
            switch (action)
            {
                case 0:
                    _plugin.Capture.RequestSettingsHelp();
                    break;

                case 1:
                    // 循环：自动 → 手动 → 关闭 → 自动（状态块的色点和文字自己会变，不弹提示）
                    _store.SetCollectMode((_store.CollectMode + 1) % 3);
                    break;

                case 2:
                    _plugin.Capture.RequestEditTarget();
                    break;
            }
        }

        private void NudgeScroll(float pixels)
        {
            _scroll.Nudge(pixels);

            Shot[] shots = _store.Snapshot;
            for (int i = 0; i < shots.Length; i++)
            {
                float direction = pixels > 0f ? 1f : -1f;
                SpringOf(shots[shots.Length - 1 - i]).Impulse(direction * 240f);
            }
        }

        /// <summary>拖动时用 Ctrl 区分「排序」和「拖出」；自检台可以替换它。</summary>
        internal static Func<bool> CtrlProbe;

        private static bool IsControlDown()
        {
            try { return (GetAsyncKeyState(VkControl) & 0x8000) != 0; }
            catch { return false; }
        }

        private const int VkControl = 0x11;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        private void StartDrag(int slot)
        {
            Shot shot = ShotAt(slot);
            if (shot == null) return;

            try
            {
                shot.Sway();
                if (_host != null) _host.StartFileDrag(new string[] { shot.Path }, false);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 拖出截图失败", ex);
            }
        }

        private void CommitReorder(int fromSlot, int toSlot)
        {
            Shot[] shots = _store.Snapshot;
            int total = Math.Min(shots.Length, Math.Max(1, _store.Settings.MaxItems));
            if (fromSlot >= total || toSlot >= total) return;

            int from = shots.Length - 1 - fromSlot;
            int to = shots.Length - 1 - toSlot;
            if (from == to) return;

            try
            {
                _store.Move(from, to);

                Shot[] after = _store.Snapshot;
                for (int i = 0; i < total && i < after.Length; i++)
                {
                    SpringOf(after[after.Length - 1 - i]).Impulse(300f);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 拖动排序失败", ex);
            }
        }

        private void CopySlot(int slot)
        {
            Shot shot = ShotAt(slot);
            if (shot == null) return;

            shot.Sway();
            _plugin.Capture.RequestCopy(shot);
        }

        private void RemoveSlot(int slot)
        {
            Shot shot = ShotAt(slot);
            if (shot == null) return;

            _plugin.Capture.RequestRemove(shot);
        }

        private Shot ShotAt(int slot)
        {
            Shot[] shots = _store.Snapshot;
            int total = Math.Min(shots.Length, Math.Max(1, _store.Settings.MaxItems));
            if (slot < 0 || slot >= total) return null;

            int index = shots.Length - 1 - slot;
            if (index < 0 || index >= shots.Length) return null;
            return shots[index];
        }

        // ---- 老接口 ----

        public WidgetHit HitTest(float x, float y, SKRect rect)
        {
            return WidgetHit.None;
        }

        public void OnAction(string action, float x, float y)
        {
        }

        public void OnRightClick(float x, float y)
        {
        }

        public void OnLeftDoubleClick(float x, float y)
        {
        }

        public void OnRightDoubleClick(float x, float y)
        {
        }

        // ---- 文件拖入 ----

        public bool OnFilesDragEnter(int count)
        {
            if (_store.CollectMode == PluginSettings.CollectOff)
            {
                ShowHint("收集已关闭");
                _dragHover = 0;
                return false;
            }

            _dragHover = count;
            return count > 0;
        }

        public void OnFilesDragOver(float x, float y)
        {
        }

        public void OnFilesDragLeave()
        {
            _dragHover = 0;
        }

        public void OnFilesDrop(string[] paths)
        {
            _dragHover = 0;
            if (paths == null || paths.Length == 0) return;
            _plugin.Capture.RequestImport(paths);
        }

        // ---- 小动画 ----

        private static double Wobble(Shot shot)
        {
            long ticks = shot.WobbleTicks;
            if (ticks == 0) return 0.0;

            double seconds = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
            if (seconds < 0.0 || seconds > 1.6) return 0.0;

            return Math.Sin(seconds * 9.0) * 2.4 * (1.0 - seconds / 1.6);
        }

        private static double CopiedAge(Shot shot)
        {
            long ticks = shot.CopiedTicks;
            if (ticks == 0) return -1.0;
            return (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
        }
    }
}
