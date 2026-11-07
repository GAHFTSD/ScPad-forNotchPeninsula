using System;
using System.Reflection;
using System.Threading;
using SkiaSharp;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// 实况窗截图键 / 倒计时。
    ///
    /// 视觉：
    ///   * 按下默认截屏键后注册（之后常驻），外圈由宿主按 Progress 画白圈；
    ///   * 1..10 秒倒计时期间外圈从满圈走空，内圈纯白；
    ///   * 倒计时结束白色闪一下，然后变灰，随后抓屏并存入截图夹。
    ///
    /// 点击：
    ///   宿主对实况窗左右键的处理固定为“展开详情页”，不会回调 OnLeftClick。
    ///   所以这里把详情页做成 ShotClipPanel 的轻量代理：宿主为了展开面板会调用
    ///   MeasureWidth，代理在那个时机启动一次倒计时 —— 点气泡 = 展开面板 + 截屏，
    ///   图片仍然进同一个面板。不需要改宿主。
    /// </summary>
    internal sealed class ScPadLiveWindow : ILiveWindow
    {
        internal const string LiveId = "com.gahftsd.scpad.capture";

        private const int PhaseIdle = 0;
        private const int PhaseCountdown = 1;
        private const int PhaseFlash = 2;
        private const int PhaseGray = 3;
        private const int TickMs = 30;
        private const int FlashMs = 450;

        private readonly ScPadPlugin _plugin;
        private readonly CaptureLiveDetailPage _detail;

        private Timer _timer;
        private int _phase = PhaseIdle;
        private int _delayMs = 3000;
        private long _endTicks;
        private long _flashStartTicks;
        private int _completionSent;
        private volatile float _progress = 1f;

        // 宿主用 C# 10 的 readonly record struct + init 属性编译 LiveWindowVisual。
        // 插件这边是 C# 5/离线编译，直接调 set_* 可能撞上 modreq(IsExternalInit)；
        // 用反射写属性最稳，且只在一帧一次的状态快照上做，不在 DrawInner 里做。
        private static readonly PropertyInfo PProgress = typeof(LiveWindowVisual).GetProperty("Progress");
        private static readonly PropertyInfo PProgressColor = typeof(LiveWindowVisual).GetProperty("ProgressColor");
        private static readonly PropertyInfo PCompleted = typeof(LiveWindowVisual).GetProperty("Completed");
        private static readonly PropertyInfo PSpinInner = typeof(LiveWindowVisual).GetProperty("SpinInner");
        private static int _visualReflectionWarned;

        internal ScPadLiveWindow(ScPadPlugin plugin)
        {
            _plugin = plugin;
            _detail = new CaptureLiveDetailPage(plugin, plugin.Panel, this);
        }

        public string Id { get { return LiveId; } }

        public IDetailPage DetailPage { get { return _detail; } }

        internal bool IsBusy
        {
            get
            {
                int p = Volatile.Read(ref _phase);
                return p == PhaseCountdown || p == PhaseFlash;
            }
        }

        internal void BeginCountdownMs(int delayMs)
        {
            if (delayMs < 100) delayMs = 100;
            if (delayMs > 3000) delayMs = 3000;

            StopTimer();

            _delayMs = delayMs;
            _endTicks = Environment.TickCount64 + _delayMs;
            _progress = 1f;
            _completionSent = 0;
            Volatile.Write(ref _phase, PhaseCountdown);

            _timer = new Timer(Tick, null, TickMs, TickMs);
            _plugin.RequestRedrawSafe();
            Log.Info("[ScPad] 实况窗倒计时 " + delayMs.ToString() + " 毫秒");
        }

        /// <summary>连按截屏键 / 点气泡：把倒计时立刻推到 0（下一拍就闪 + 抓屏）。</summary>
        internal void FinishNow()
        {
            if (Volatile.Read(ref _phase) != PhaseCountdown) return;
            _endTicks = Environment.TickCount64;
            ThreadPool.QueueUserWorkItem(delegate { try { Tick(null); } catch { } });
        }

        internal void Cancel()
        {
            StopTimer();
            Volatile.Write(ref _phase, PhaseGray);
            _progress = 0f;
            _plugin.RequestRedrawSafe();
        }

        private void StopTimer()
        {
            Timer t = _timer;
            _timer = null;
            if (t != null)
            {
                try { t.Dispose(); } catch { }
            }
        }

        private void Tick(object state)
        {
            try
            {
                long now = Environment.TickCount64;
                int phase = Volatile.Read(ref _phase);

                if (phase == PhaseCountdown)
                {
                    long remain = _endTicks - now;
                    if (remain <= 0)
                    {
                        _progress = 0f;
                        _flashStartTicks = now;
                        Volatile.Write(ref _phase, PhaseFlash);
                        _plugin.RequestRedrawSafe();

                        // 白圈走完 = 延迟到点：立刻抓屏（下面 450ms 的白色闪烁只是在“拍照”反馈）
                        if (Interlocked.Exchange(ref _completionSent, 1) == 0)
                        {
                            _plugin.OnLiveCountdownFinished();
                        }
                    }
                    else
                    {
                        _progress = (float)remain / (float)_delayMs;
                    }
                }
                else if (phase == PhaseFlash)
                {
                    if (now - _flashStartTicks >= FlashMs)
                    {
                        StopTimer();
                        Volatile.Write(ref _phase, PhaseGray);
                        _progress = 0f;
                        _plugin.RequestRedrawSafe();

                        // 闪完变灰，停一会儿把气泡收掉；下次按键再重新长出来
                        _plugin.RequestReleaseLiveWindow(900);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 实况窗倒计时异常：" + ex.Message);
            }
        }

        public LiveWindowVisual GetVisual()
        {
            int phase = Volatile.Read(ref _phase);
            float progress = phase == PhaseCountdown ? _progress : 0f;

            // 内圈由 DrawInner 自己画；不要把标题/百分比交给宿主的默认三行文字。
            object box = new LiveWindowVisual();
            Set(PProgress, box, progress);
            Set(PProgressColor, box, SKColors.White);
            Set(PCompleted, box, false);
            Set(PSpinInner, box, false);
            return (LiveWindowVisual)box;
        }

        private static void Set(PropertyInfo property, object box, object value)
        {
            if (property == null) return;
            try
            {
                property.SetValue(box, value, null);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _visualReflectionWarned, 1) == 0)
                    Log.Warn("[ScPad] 写入实况窗视觉失败：" + ex.Message);
            }
        }

        public void DrawInner(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            try
            {
                float size = Math.Min(rect.Width, rect.Height);
                if (size <= 2f) return;
                float radius = size * 0.34f;
                int phase = Volatile.Read(ref _phase);

                SKColor color;
                if (phase == PhaseFlash)
                {
                    // 450ms 内闪两下：白色亮度在 0.55..1.0 之间摆动。
                    double elapsed = Environment.TickCount64 - _flashStartTicks;
                    double wave = 0.5 + 0.5 * Math.Cos(elapsed / 90.0 * Math.PI * 2.0);
                    byte alpha = (byte)(140 + 115 * wave);
                    color = SKColors.White.WithAlpha(alpha);
                }
                else if (phase == PhaseGray || phase == PhaseIdle)
                {
                    color = new SKColor(0x8E, 0x8E, 0x93, 0xFF);
                }
                else
                {
                    color = SKColors.White;
                }

                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = color;
                    canvas.DrawCircle(rect.MidX, rect.MidY, radius, paint);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 实况窗内圈绘制异常：" + ex.Message);
            }
        }

        public void OnExpand()
        {
            // DetailPage 非空，宿主会直接展开面板；此回调通常不会走到。
            _plugin.StartTimedCaptureFromLiveWindow();
        }

        public void OnLeftClick()
        {
            // 2.2.0 宿主仍声明了这个成员（二进制兼容用），但左右键统一走展开；
            // 保留空实现，避免按真实宿主接口编译时缺成员。
        }

        public void OnRemoved()
        {
            StopTimer();
            _plugin.OnLiveWindowRemoved(this);
        }

        public void OnHostEnabledChanged(bool enabled)
        {
            if (!enabled)
            {
                StopTimer();
                Volatile.Write(ref _phase, PhaseIdle);
                _progress = 0f;
            }
        }
    }

    /// <summary>
    /// 实况窗展开时复用截图夹面板；MeasureWidth 只在“展开面板”时被宿主调用，
    /// 正好作为“点气泡 = 开始截图”的触发点。
    /// </summary>
    internal sealed class CaptureLiveDetailPage : IDetailPage
    {
        private readonly ScPadPlugin _plugin;
        private readonly ShotClipPanel _panel;
        private readonly ScPadLiveWindow _live;
        private long _lastTriggerTicks = -100000;

        internal CaptureLiveDetailPage(ScPadPlugin plugin, ShotClipPanel panel, ScPadLiveWindow live)
        {
            _plugin = plugin;
            _panel = panel;
            _live = live;
        }

        public float MeasureWidth()
        {
            TriggerIfIdle();
            return _panel.MeasureWidth();
        }

        public float MeasureHeight()
        {
            return _panel.MeasureHeight();
        }

        private void TriggerIfIdle()
        {
            if (_live != null && _live.IsBusy) return;

            long now = Environment.TickCount64;
            if (now - _lastTriggerTicks < 800) return;
            _lastTriggerTicks = now;
            _plugin.StartTimedCaptureFromLiveWindow();
        }

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            _panel.Draw(canvas, rect, frame);
        }

        public WidgetHit HitTest(float x, float y, SKRect rect)
        {
            return _panel.HitTest(x, y, rect);
        }

        public void OnAction(string action, float x, float y)
        {
            _panel.OnAction(action, x, y);
        }

        public void OnMouseDown(float x, float y) { _panel.OnMouseDown(x, y); }
        public void OnMouseMove(float x, float y) { _panel.OnMouseMove(x, y); }
        public void OnMouseUp(float x, float y) { _panel.OnMouseUp(x, y); }
        public void OnMouseLeave() { _panel.OnMouseLeave(); }
        public bool AcceptsRightClick { get { return _panel.AcceptsRightClick; } }
        public void OnRightClick(float x, float y) { _panel.OnRightClick(x, y); }
        public bool AcceptsDoubleClick { get { return _panel.AcceptsDoubleClick; } }
        public void OnLeftDoubleClick(float x, float y) { _panel.OnLeftDoubleClick(x, y); }
        public void OnRightDoubleClick(float x, float y) { _panel.OnRightDoubleClick(x, y); }
        public bool OnFilesDragEnter(int count) { return _panel.OnFilesDragEnter(count); }
        public void OnFilesDragOver(float x, float y) { _panel.OnFilesDragOver(x, y); }
        public void OnFilesDragLeave() { _panel.OnFilesDragLeave(); }
        public void OnFilesDrop(string[] paths) { _panel.OnFilesDrop(paths); }
        public TimeSpan AutoCollapseDelay { get { return _panel.AutoCollapseDelay; } }
        public bool ResizeWithContent { get { return false; } }
    }
}
