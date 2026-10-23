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
    /// 形态：
    ///   * **固定预留 N 个格子**（默认 4，可在设置里改 1~8），空着也占位；
    ///   * 截图**从右往左**依次填入：最新的在最右，更早的往左排；
    ///   * 只有超过预留格数时才解锁横向滚动（滚轮，带惯性）；
    ///   * 面板里没有标题/底部提示/按钮栏 —— 只有这一条卡片带；提示（比如"已存过"）就地淡入淡出。
    ///
    /// 动效（都是弹簧 + 阻尼的真实物理，不是缓动曲线）：
    ///   * 新图进来时，其余卡片按距离依次被震一下；
    ///   * 左右滚动时，卡片按滑动方向被"甩"一下再荡回来；
    ///   * 拖动排序、从剪贴板上传也会引发抖动。
    ///
    /// 交互：单击卡片 = 复制；按住拖动 = 面板内**排序**，拖出面板边界 = **拖出一张 PNG**；
    /// 卡片右上角 × = 移出；点空槽 = 从剪贴板上传一张；岛内右键 = 关闭面板。
    /// </summary>
    internal sealed class ShotClipPanel : IDetailPage
    {
        private const float Pad = 12f;
        private const float CardW = 148f;
        private const float CardH = 176f;
        private const float Gap = 10f;

        private const float DragThreshold = 5f;

        /// <summary>拖动时越过面板边界这么多像素，就从"排序"切成"拖出粘贴"。</summary>
        private const float DragOutMargin = 26f;

        private static readonly SKColor Accent = new SKColor(88, 214, 141);

        private readonly ScPadPlugin _plugin;
        private readonly ShotStore _store;
        private readonly IPluginHost _host;
        private readonly ScrollModel _scroll = new ScrollModel();

        /// <summary>每张卡片自己的晃动（按图片路径记，排序后不会串味）。</summary>
        private readonly Dictionary<string, SpringMotion> _springs =
            new Dictionary<string, SpringMotion>(StringComparer.OrdinalIgnoreCase);

        private float _layoutW = 646f;
        private float _layoutH = 200f;

        private float _rectW = 646f;
        private float _rectH = 200f;

        private int _slots = 4;
        private int _layoutCount = 4;
        private int _count;
        private long _lastFrameTicks;
        private long _scrollImpulseTicks;
        private int _lastCount = -1;

        private bool _pressed;
        private int _pressSlot = -1;
        private int _pressRemove = -1;
        private float _pressX;
        private float _pressY;
        private float _mouseX = -1000f;
        private float _mouseY = -1000f;

        // 拖动排序
        private bool _reordering;
        private int _reorderFromSlot = -1;
        private int _reorderToSlot = -1;
        private float _dragX;

        private int _dragHover;

        /// <summary>抓屏时让位：0 = 正常，1 = 完全透明。</summary>
        private float _dim;

        // 就地提示（例如"这张已经存过了"）
        private string _hintText = "";
        private long _hintUntilTicks;

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

            // 宽度永远按"预留格数"算：空着也占位
            _layoutW = Pad * 2f + slots * CardW + (slots - 1) * Gap;
            _layoutH = Pad * 2f + CardH;

            _rectW = _layoutW;
            _rectH = _layoutH;
        }

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

        /// <summary>清掉就地提示（换页/预览时用）。</summary>
        internal void ClearHint()
        {
            _hintText = "";
            _hintUntilTicks = 0;
        }

        /// <summary>抓屏动画：0 正常 / 1 完全让位。</summary>
        internal void SetDim(float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            _dim = amount;
        }

        /// <summary>面板就地提示（不弹通知）。</summary>
        internal void ShowHint(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _hintText = text;
            _hintUntilTicks = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMillisecond * 2200L;
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
                // 抓屏那一瞬整体淡出（只用在这一瞬间，平时不额外开销）
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

            if (_lastCount >= 0 && total > _lastCount) OnNewShots(total - _lastCount, total);
            _lastCount = total;
            _count = total;

            // 只有超过预留格数才解锁滚动；空槽也占位，所以内容宽度至少是预留格数
            int layoutCount = total > _slots ? total : _slots;
            _layoutCount = layoutCount;
            float contentW = layoutCount * (CardW + Gap) - Gap;
            float viewportW = _slots * CardW + (_slots - 1) * Gap;
            _scroll.SetMax(Math.Max(0f, contentW - viewportW));

            // 滚轮默认不接管（用户要求未经允许不要用滚轮）：
            // 翻页改成点两端的箭头（见 OnMouseDown），这里只做惯性推进。
            // 约定：Offset = 0 是「停在最新那头（最右）」，增大 = 往左看更早的。
            _scroll.Update(dt);

            // 滑动 → 卡片按方向被甩一下（弹簧冲量，方向跟滚动一致）
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

            canvas.Save();
            canvas.ClipRect(new SKRect(Pad - 2f, Pad - 6f, _layoutW - Pad + 2f, _layoutH - Pad));
            try
            {
                int hover = _reordering ? -1 : HitSlot(_mouseX, _mouseY);

                // 从左往右画：空槽 + 卡片（最新的在最右）
                for (int slot = _slots - 1; slot >= 0; slot--)
                {
                    float x = SlotLeft(slot, contentLeft);

                    if (slot >= total)
                    {
                        DrawEmptySlot(canvas, new SKRect(x, Pad, x + CardW, Pad + CardH), frame);
                        continue;
                    }

                    if (_reordering && slot == _reorderFromSlot) continue;   // 被拖的那张最后单独画

                    // 让位：拖到别处时，中间几张往旁边错一下
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
                    float tilt = (float)((float)shot.Tilt * 0.6 + sway * 0.05 + Wobble(shot));

                    DrawCard(canvas, new SKRect(x + sway * 0.12f, Pad, x + CardW + sway * 0.12f, Pad + CardH),
                        shot, frame, slot == hover, tilt);
                }

                // 被拖动的卡片浮在最上面，跟手 + 倾斜
                if (_reordering && _reorderFromSlot >= 0 && _reorderFromSlot < total)
                {
                    Shot dragging = shots[shots.Length - 1 - _reorderFromSlot];
                    var body = new SKRect(_dragX - CardW * 0.5f, Pad - 7f, _dragX + CardW * 0.5f, Pad - 7f + CardH);
                    DrawCard(canvas, body, dragging, frame, false, 3.5f);
                }
            }
            finally
            {
                canvas.Restore();
            }

            DrawScrollHint(canvas, frame);
            DrawHint(canvas, frame, now);
            DrawDropOverlay(canvas, frame);
        }

        private float SlotLeft(int slot, float contentLeft)
        {
            // 槽位从右往左排：slot 0 在最右
            return contentLeft + (_layoutCount - 1 - slot) * (CardW + Gap);
        }

        private void DrawEmptySlot(SKCanvas canvas, SKRect body, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            Ui.StrokeRound(canvas, body, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 28)), 1.2f);

            // 一个淡淡的「+」：点空槽 = 从剪贴板上传一张
            float cx = body.MidX;
            float cy = body.MidY;
            byte plusAlpha = Ui.Alpha(frame.Alpha, 55);
            Ui.Line(canvas, cx - 7f, cy, cx + 7f, cy, ink.WithAlpha(plusAlpha), 1.4f);
            Ui.Line(canvas, cx, cy - 7f, cx, cy + 7f, ink.WithAlpha(plusAlpha), 1.4f);
        }

        private void DrawScrollHint(SKCanvas canvas, WidgetFrame frame)
        {
            if (!_scroll.Overflow) return;

            SKColor ink = frame.Theme.TextColor;
            byte alpha = Ui.Alpha(frame.Alpha, 110);
            float midY = _layoutH * 0.5f;

            if (_scroll.Offset < _scroll.Max - 1f)   // 左边还有更早的
            {
                float ax = Pad - 5f;
                Ui.Line(canvas, ax + 2.5f, midY - 4f, ax, midY, ink.WithAlpha(alpha), 1.4f);
                Ui.Line(canvas, ax, midY, ax + 2.5f, midY + 4f, ink.WithAlpha(alpha), 1.4f);
            }
            if (_scroll.Offset > 1f)                // 右边还有更新的
            {
                float ax = _layoutW - Pad + 5f;
                Ui.Line(canvas, ax - 2.5f, midY - 4f, ax, midY, ink.WithAlpha(alpha), 1.4f);
                Ui.Line(canvas, ax, midY, ax - 2.5f, midY + 4f, ink.WithAlpha(alpha), 1.4f);
            }
        }

        private void DrawHint(SKCanvas canvas, WidgetFrame frame, long now)
        {
            if (_hintText.Length == 0 || now >= _hintUntilTicks) return;

            long remaining = _hintUntilTicks - now;
            float fade = remaining > TimeSpan.TicksPerMillisecond * 400L
                ? 1f
                : (float)(remaining / (double)(TimeSpan.TicksPerMillisecond * 400L));

            byte alpha = Ui.Alpha(frame.Alpha, (byte)(205f * fade));
            float width = Ui.Measure(_hintText, 12f, false) + 26f;
            var pill = new SKRect(_layoutW * 0.5f - width * 0.5f, _layoutH - 33f,
                _layoutW * 0.5f + width * 0.5f, _layoutH - 11f);

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

        private void DrawCard(SKCanvas canvas, SKRect body, Shot shot, WidgetFrame frame, bool hover, float tilt)
        {
            SKColor ink = frame.Theme.TextColor;

            canvas.Save();
            canvas.RotateDegrees(tilt, body.MidX, body.Top);

            var pin = new SKRect(body.MidX - 4.5f, body.Top - 14f, body.MidX + 4.5f, body.Top + 6f);
            Ui.FillRound(canvas, pin, 3f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 130 : 95)));

            Ui.FillRound(canvas, body, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 30 : 18)));
            Ui.StrokeRound(canvas, body, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 95 : 55)), 1f);

            var imageArea = new SKRect(body.Left + 7f, body.Top + 7f, body.Right - 7f, body.Bottom - 33f);
            Ui.FillRound(canvas, imageArea, 5f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 14)));
            if (shot.Thumb != null)
            {
                Ui.Bitmap(canvas, shot.Thumb, Ui.FitRect(imageArea, shot.Thumb.Width, shot.Thumb.Height), frame.Alpha);
            }

            float metaBaseline = body.Bottom - 12f;
            Ui.Text(canvas, shot.FileName, body.Left + 7f, metaBaseline, Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 210)), false, body.Width - 52f, SKTextAlign.Left);
            Ui.Text(canvas, shot.TimeText, body.Right - 7f, metaBaseline, Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 120)), false, 40f, SKTextAlign.Right);

            if (hover && !_reordering)
            {
                float cx = body.Right - 13f;
                float cy = body.Top + 13f;
                Ui.FillCircle(canvas, cx, cy, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 70)));
                Ui.Line(canvas, cx - 3.2f, cy - 3.2f, cx + 3.2f, cy + 3.2f,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 235)), 1.5f);
                Ui.Line(canvas, cx + 3.2f, cy - 3.2f, cx - 3.2f, cy + 3.2f,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 235)), 1.5f);
            }

            double age = CopiedAge(shot);
            if (age >= 0.0 && age < 1.3)
            {
                byte fade = (byte)(255 * (1.0 - age / 1.3));
                var badge = new SKRect(body.MidX - 33f, body.Bottom - 58f, body.MidX + 33f, body.Bottom - 36f);
                Ui.FillRound(canvas, badge, 6f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, fade)));

                SKColor tick = SKColors.White.WithAlpha(Ui.Alpha(frame.Alpha, fade));
                float tx = badge.Left + 13f;
                float ty = badge.MidY;
                Ui.Line(canvas, tx, ty, tx + 3.2f, ty + 3.4f, tick, 1.7f);
                Ui.Line(canvas, tx + 3.2f, ty + 3.4f, tx + 9.2f, ty - 3.8f, tick, 1.7f);

                Ui.Text(canvas, "已复制", badge.Left + 27f, Ui.Baseline(badge.MidY, Ui.SmallSize), Ui.SmallSize,
                    tick, false, badge.Width - 30f, SKTextAlign.Left);
            }

            canvas.Restore();
        }

        // ---- 弹簧 ----

        private SpringMotion SpringOf(Shot shot)
        {
            string key = shot != null ? shot.Path : "";
            SpringMotion spring;
            if (_springs.TryGetValue(key, out spring)) return spring;

            spring = new SpringMotion();
            if (_springs.Count > 64) _springs.Clear();   // 保险：字典不无限长
            _springs[key] = spring;
            return spring;
        }

        /// <summary>有新图进来：其余卡片按距离依次被震一下（越近越明显、方向交替）。</summary>
        private void OnNewShots(int arrived, int total)
        {
            int added = arrived > 3 ? 3 : arrived;
            Shot[] shots = _store.Snapshot;

            for (int slot = 0; slot < total; slot++)
            {
                if (slot < added) continue;                  // 新来的那些不抖
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
            if (ly < Pad - 6f || ly > Pad + CardH + 6f) return -1;

            float contentLeft = ContentLeft();
            for (int slot = 0; slot < _slots; slot++)
            {
                float x = SlotLeft(slot, contentLeft);
                if (lx >= x && lx <= x + CardW) return slot;
            }
            return -1;
        }

        /// <summary>屏幕 x → "想放在第几格"（0 = 最右，越大越往左）。</summary>
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

        private bool HitRemove(int slot, float lx, float ly)
        {
            float x = SlotLeft(slot, ContentLeft());
            float dx = lx - (x + CardW - 13f);
            float dy = ly - (Pad + 13f);
            return dx * dx + dy * dy <= 11f * 11f;
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
            _reordering = false;
            _reorderFromSlot = -1;
            _reorderToSlot = -1;

            // 两侧箭头：点一下翻一页（滚轮不再接管之后，这是主要的浏览方式）
            if (_scroll.Overflow && ly > Pad - 8f && ly < Pad + CardH + 8f)
            {
                if (lx >= Pad - 12f && lx <= Pad + 2f)
                {
                    _pressed = false;
                    NudgeScroll(-(CardW + Gap));
                    return;
                }
                if (lx >= _layoutW - Pad - 2f && lx <= _layoutW - Pad + 12f)
                {
                    _pressed = false;
                    NudgeScroll(CardW + Gap);
                    return;
                }
            }

            int slot = HitSlot(lx, ly);
            if (slot < 0) return;

            if (slot >= _count)
            {
                // 点空槽 = 从剪贴板上传一张
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

            // 默认行为就是**拖出粘贴**（核心功能：拖到任何地方松手即成一张图）。
            // 想调整顺序请按住 Ctrl 再拖 —— 这样排序永远不会把拖出抢掉。
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
                return;
            }

            _pressed = false;
            int remove = _pressRemove;
            int slot = _pressSlot;
            _pressRemove = -1;
            _pressSlot = -1;

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
            _reordering = false;
            _reorderFromSlot = -1;
            _reorderToSlot = -1;
            _mouseX = -1000f;
            _mouseY = -1000f;
            _dragHover = 0;
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

                // 参与让位的几张震一下，表示"顺序变了"
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

        /// <summary>翻一页（往左 = 看更早，往右 = 回最新），并让卡片抖一下。</summary>
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
