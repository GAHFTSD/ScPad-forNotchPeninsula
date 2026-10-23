using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 灵动岛上的截图夹：**常驻**的一条小状态栏。
    ///
    /// 布局（从左到右）：
    ///   [ 数字 ]  [ 框 ]
    ///     数字 = 截图夹里一共有几张（0 也显示）
    ///     框   = 有图时是叠成一摞的缩略图（最多 4 张，最右那张完整可见）；
    ///            没有图时留一个**空框**，让用户一眼看到"插件装了、还没截图"。
    ///
    /// 官方要求：无论有没有截图都要常驻，避免用户装完不知道它在哪。
    /// 交互：点缩略图 = 复制那一张；点数字 / 空框 / 空白 = 展开面板；右键 = 宿主默认；
    /// 把图片拖到这条栏上 = 收进截图夹。
    ///
    /// 注意：插件**不接管滚轮**（用户要求），所以这里的叠放是静态的，看更早的请打开面板。
    /// </summary>
    internal sealed class ScPadWidget : IWidget
    {
        private const float Pad = 3f;
        private const float BaseCountW = 19f;
        private const float BaseCardW = 48f;
        private const float BaseCardH = 35f;
        private const float BaseStep = 14f;
        private const float BaseGap = 5f;
        private const float CountTextSize = 10.5f;

        /// <summary>普通模式最多叠几张。</summary>
        private const int MaxShown = 4;

        private readonly ScPadPlugin _plugin;
        private readonly ShotStore _store;
        private readonly ScrollModel _scroll = new ScrollModel();

        private IPluginHost _host;
        private long _lastFrameTicks;

        // 最近一帧的布局（命中测试要用同一套数）
        private float _measuredZoom = 1f;   // MeasureWidth 用的缩放，Draw 沿用同一套
        private float _countLeft;
        private float _countW = BaseCountW;
        private float _areaLeft;
        private float _cardW = BaseCardW;
        private float _step = BaseStep;
        private int _firstIndex;
        private int _shown;
        private int _total;

        private volatile bool _canScroll;

        /// <summary>抓屏时让位：0 = 正常，1 = 完全透明（由 CaptureService 驱动）。</summary>
        private float _dim;

        internal ScPadWidget(ScPadPlugin plugin, ShotStore store, IPluginHost host)
        {
            _plugin = plugin;
            _store = store;
            _host = host;
        }

        public string Id { get { return ScPadPlugin.WidgetId; } }

        public string DisplayName { get { return "ScPad 截图夹"; } }

        public IDetailPage DetailPage { get { return _plugin.Panel; } }

        public bool AcceptsFileDropWhenCollapsed { get { return true; } }

        public bool AcceptsRightClick { get { return false; } }

        public bool AcceptsDoubleClick { get { return false; } }

        /// <summary>滚轮钩子问：现在该不该消费岛上的滚轮（默认不接管，恒为 false）。</summary>
        internal bool CanScroll { get { return _canScroll; } }

        /// <summary>抓屏动画：0 正常 / 1 完全让位。</summary>
        internal void SetDim(float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            _dim = amount;
        }

        public float MeasureWidth(float availableHeight)
        {
            Shot[] shots = _store.Snapshot;
            int limit = Math.Max(1, _store.Settings.MaxItems);
            int total = Math.Min(shots.Length, limit);

            double scale = _store.Settings.EffectiveThumbScale / 100.0;

            // 关键：岛体就这么高，卡片高度会被裁；宽度必须用**裁剪之后**的缩放来算，
            // 否则宿主按大尺寸排版、我们画得小，两侧就会留出一条空白。
            float cardH = (float)(BaseCardH * scale);
            float maxH = availableHeight > 1f ? Math.Max(8f, availableHeight - 4f) : cardH;
            if (cardH > maxH) cardH = maxH;
            float zoom = cardH / BaseCardH;
            _measuredZoom = zoom;

            float cardW = (float)(BaseCardW * zoom);
            float step = (float)(BaseStep * zoom);
            float countW = (float)(BaseCountW * zoom);
            float gap = (float)(BaseGap * zoom);

            int shown = Math.Min(total, MaxShown);
            float desired = Pad * 2f + countW + gap + cardW;
            if (shown > 1) desired += (shown - 1) * step;

            // 给其它插件（尤其是媒体）留位置；常驻但不能贪心
            float budget = 0f;
            try { budget = _host == null ? 0f : _host.GetPluginRowBudget(); }
            catch { budget = 0f; }

            if (budget > 1f && !float.IsInfinity(budget))
            {
                float cap = budget * 0.45f;
                float floor = Pad * 2f + countW + gap + cardW;   // 至少「数字 + 一个框」
                if (cap < floor) cap = floor;
                if (desired > cap) desired = cap;
            }

            return desired;
        }

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            if (canvas == null || frame.Alpha == 0) return;

            float visible = 1f - _dim;
            if (visible <= 0.01f) return;
            byte alpha = (byte)Math.Round(frame.Alpha * visible);

            Shot[] shots = _store.Snapshot;
            int limit = Math.Max(1, _store.Settings.MaxItems);
            int total = Math.Min(shots.Length, limit);

            long now = DateTime.UtcNow.Ticks;
            float dt = _lastFrameTicks == 0L ? 0f : (float)((now - _lastFrameTicks) / (double)TimeSpan.TicksPerSecond);
            _lastFrameTicks = now;

            double scale = _store.Settings.EffectiveThumbScale / 100.0;
            float cardH = (float)(BaseCardH * scale);
            float maxH = Math.Max(8f, rect.Height - 4f);
            if (cardH > maxH) cardH = maxH;
            float zoom = cardH / BaseCardH;
            if (_measuredZoom > 0.01f && _measuredZoom < zoom) zoom = _measuredZoom;

            float cardW = (float)(BaseCardW * zoom);
            float step = (float)(BaseStep * zoom);
            float countW = (float)(BaseCountW * zoom);
            float gap = (float)(BaseGap * zoom);

            int shown = Math.Min(total, MaxShown);
            int first = total - shown;
            if (first < 0) first = 0;

            // [框] [数字]：数字在右边。
            // 宿主给的矩形有时比我们量出来的宽 —— 多出来的宽度直接给"框"，
            // 这样不会在右侧留一条空荡荡的空白（没图时最明显）。
            float cardsW = cardW + (shown > 1 ? (shown - 1) * step : 0f);
            float availW = rect.Width - Pad * 2f - gap - countW;

            float areaLeft;
            float countLeft;

            if (shown <= 0)
            {
                // 没图：框铺满可用宽度，数字贴最右
                if (availW > cardW) cardsW = availW;
                areaLeft = rect.Left + Pad;
                countLeft = areaLeft + cardsW + gap;
            }
            else
            {
                float contentW = cardsW + gap + countW;
                float contentLeft = rect.Left + (rect.Width - contentW) * 0.5f;
                if (contentLeft < rect.Left + Pad) contentLeft = rect.Left + Pad;
                areaLeft = contentLeft;
                countLeft = contentLeft + cardsW + gap;
            }

            float boxW = cardsW;

            _countLeft = countLeft;
            _countW = countW;
            _areaLeft = areaLeft;
            _cardW = cardW;
            _step = step;
            _shown = shown;
            _firstIndex = first;
            _total = total;
            _canScroll = false;   // 不接管滚轮

            SKColor ink = frame.Theme.TextColor;
            float top = rect.MidY - cardH * 0.5f;

            if (shown <= 0)
            {
                // 没有截图：留一个空框（官方要求常驻，别让用户以为没装上）
                DrawEmptyBox(canvas, new SKRect(areaLeft, top, areaLeft + boxW, top + cardH), alpha, ink);
                DrawCount(canvas, new SKRect(countLeft, top, countLeft + countW, top + cardH), total, alpha, ink);
                return;
            }

            _scroll.SetMax(0f);
            _scroll.Update(dt);

            float sway = _scroll.SwayDegrees(0.0022f);
            int baseShift = 0;
            float frac = 0f;

            canvas.Save();
            canvas.ClipRect(new SKRect(areaLeft - 2f, rect.Top, rect.Right, rect.Bottom));
            try
            {
                // 从底层往顶层画：最右边（最后画的）那张完整可见
                for (int k = 0; k < shown; k++)
                {
                    int slot = ((k + baseShift) % shown + shown) % shown;
                    float x = areaLeft + k * step - frac;

                    Shot shot = shots[first + slot];
                    float tilt = (float)shot.Tilt * 0.35f + sway * (1f + 0.1f * k);

                    // 叠放的深度：越靠下越淡，一眼能看出这是"一摞"而不是一排
                    float depth = shown <= 1 ? 1f : (0.55f + 0.45f * (k / (float)(shown - 1)));
                    DrawMiniCard(canvas, new SKRect(x, top, x + cardW, top + cardH), shot, alpha, frame,
                        k == shown - 1, tilt, depth);
                }
            }
            finally
            {
                canvas.Restore();
            }

            // 右边：数量（在缩略图之后画，保证不被卡片压住）
            DrawCount(canvas, new SKRect(countLeft, top, countLeft + countW, top + cardH), total, alpha, ink);
        }

        private void DrawCount(SKCanvas canvas, SKRect box, int total, byte baseAlpha, SKColor ink)
        {
            Ui.FillRound(canvas, box, 6f, ink.WithAlpha(Ui.Alpha(baseAlpha, 30)));
            Ui.StrokeRound(canvas, box, 6f, ink.WithAlpha(Ui.Alpha(baseAlpha, 55)), 1f);

            string text = total > 99 ? "99+" : total.ToString();
            Ui.Text(canvas, text, box.MidX, Ui.Baseline(box.MidY, CountTextSize), CountTextSize,
                ink.WithAlpha(Ui.Alpha(baseAlpha, total > 0 ? 235 : 150)), true, box.Width - 2f, SKTextAlign.Center);
        }

        private void DrawEmptyBox(SKCanvas canvas, SKRect body, byte baseAlpha, SKColor ink)
        {
            Ui.FillRound(canvas, body, 4f, ink.WithAlpha(Ui.Alpha(baseAlpha, 14)));
            Ui.StrokeRound(canvas, body, 4f, ink.WithAlpha(Ui.Alpha(baseAlpha, 75)), 1f);

            // 中间一个很淡的「+」：暗示这里可以点开、也可以把图片拖进来
            float cx = body.MidX;
            float cy = body.MidY;
            byte plus = Ui.Alpha(baseAlpha, 70);
            Ui.Line(canvas, cx - 5.5f, cy, cx + 5.5f, cy, ink.WithAlpha(plus), 1.2f);
            Ui.Line(canvas, cx, cy - 5.5f, cx, cy + 5.5f, ink.WithAlpha(plus), 1.2f);
        }

        private void DrawMiniCard(SKCanvas canvas, SKRect body, Shot shot, byte baseAlpha, WidgetFrame frame, bool topMost, float tilt, float depth)
        {
            bool rotated = Math.Abs(tilt) > 0.2f;
            if (!rotated)
            {
                DrawMiniCardBody(canvas, body, shot, baseAlpha, frame, topMost, depth);
                return;
            }

            canvas.Save();
            canvas.RotateDegrees(tilt, body.MidX, body.Top);
            try { DrawMiniCardBody(canvas, body, shot, baseAlpha, frame, topMost, depth); }
            finally { canvas.Restore(); }
        }

        private void DrawMiniCardBody(SKCanvas canvas, SKRect body, Shot shot, byte baseAlpha, WidgetFrame frame, bool topMost, float depth)
        {
            SKColor ink = frame.Theme.TextColor;
            byte alpha = Ui.Alpha(baseAlpha, (byte)(255f * depth));

            Ui.FillRound(canvas, body, 3.5f, ink.WithAlpha(Ui.Alpha(alpha, topMost ? 40 : 30)));
            Ui.StrokeRound(canvas, body, 3.5f, ink.WithAlpha(Ui.Alpha(alpha, topMost ? 90 : 55)), 1f);

            if (topMost)
            {
                // 只有最上面那张画缩略图；下面几张只留卡背，一摞卡片的层次才干净
                var inner = new SKRect(body.Left + 1.5f, body.Top + 1.5f, body.Right - 1.5f, body.Bottom - 1.5f);
                if (shot.Thumb != null)
                {
                    Ui.Bitmap(canvas, shot.Thumb, Ui.FitRect(inner, shot.Thumb.Width, shot.Thumb.Height), alpha);
                }
            }
            else
            {
                Ui.Line(canvas, body.Left + 3f, body.Top + 3f, body.Right - 3f, body.Top + 3f,
                    ink.WithAlpha(Ui.Alpha(alpha, 26)), 1f);
            }

            // 最上面那张、且刚存进来几秒 → 点个亮点提示"新的在这里"
            if (topMost && IsFresh(shot))
            {
                float cx = body.Right - 5f;
                float cy = body.Top + 5f;
                Ui.FillCircle(canvas, cx, cy, 2.4f, new SKColor(88, 214, 141).WithAlpha(alpha));
            }
        }

        private static bool IsFresh(Shot shot)
        {
            try { return (DateTime.Now - shot.Created).TotalSeconds < 5.0; }
            catch { return false; }
        }

        public WidgetHit HitTest(float x, float y, SKRect rect)
        {
            // 数字区 / 空框 / 卡片外的空白：都算「打开面板」
            if (_shown <= 0) return new WidgetHit("open");
            if (x > _areaLeft + _cardW + (_shown - 1) * _step + 1f) return new WidgetHit("open");

            float rel = x - _areaLeft;
            float width = _cardW + (_shown - 1) * _step;
            if (rel < 0f || rel > width) return new WidgetHit("open");

            float step = _step > 0.01f ? _step : 1f;
            int k = (int)Math.Floor(rel / step);
            if (k >= _shown) k = _shown - 1;

            int baseShift = 0;
            int slot = ((k + baseShift) % _shown + _shown) % _shown;

            return new WidgetHit("c" + (_firstIndex + slot).ToString());
        }

        public void OnLeftClick(string action, float x, float y)
        {
            if (action == null) return;

            if (action.Length > 1 && action[0] == 'c')
            {
                int index;
                if (!int.TryParse(action.Substring(1), out index)) return;
                CopyAt(index);
                return;
            }

            _plugin.OpenPanel();
        }

        private void CopyAt(int index)
        {
            Shot[] shots = _store.Snapshot;
            if (index < 0 || index >= shots.Length) return;

            Shot shot = shots[index];
            try { shot.Sway(); } catch { }
            _plugin.Capture.RequestCopy(shot);
        }

        public void OnRightClick()
        {
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
            _canScroll = false;
        }
    }
}
