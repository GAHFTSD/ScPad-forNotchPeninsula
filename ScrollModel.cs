using System;

namespace ScPad
{
    /// <summary>
    /// 截图夹的横向滚动模型：速度 + 阻尼 + 边界回弹，做成"有惯性"的手感。
    ///
    /// 约定：Offset 增大 = 内容左移 = 视口向右（看到后面/更早的截图）。
    /// 每次 Draw 调一次 <see cref="Update"/>，传入上一帧到这一帧的秒数。
    /// </summary>
    internal sealed class ScrollModel
    {
        /// <summary>每格滚轮给的速度冲量（像素/秒）。</summary>
        private const float WheelImpulse = 1100f;

        /// <summary>速度上限（像素/秒）。</summary>
        private const float MaxSpeed = 9000f;

        /// <summary>阻尼系数：越大停得越快。</summary>
        private const float Damping = 6.2f;

        /// <summary>边界反弹保留的比例。</summary>
        private const float Bounce = 0.34f;

        /// <summary>速度低于这个就当停了。</summary>
        private const float RestSpeed = 3f;

        private float _offset;
        private float _velocity;
        private float _max;

        /// <summary>当前偏移（像素）。</summary>
        internal float Offset { get { return _offset; } }

        /// <summary>当前速度（像素/秒），正 = 向右浏览。</summary>
        internal float Velocity { get { return _velocity; } }

        /// <summary>可滚动的最大偏移；0 = 没有溢出。</summary>
        internal float Max { get { return _max; } }

        /// <summary>内容是否超出了可视宽度（只有这时候才吃滚轮）。</summary>
        internal bool Overflow { get { return _max > 0.5f; } }

        internal bool Moving { get { return _velocity != 0f; } }

        /// <summary>内容/尺寸变化时更新上限，并把偏移夹回去。</summary>
        internal void SetMax(float value)
        {
            _max = value < 0f ? 0f : value;
            if (_offset > _max) _offset = _max;
            if (_offset < 0f) _offset = 0f;
            if (_max <= 0.5f) _velocity = 0f;
        }

        /// <summary>滚轮：notches &gt; 0 = 向上滚一格，&lt; 0 = 向下滚一格。</summary>
        internal void Wheel(int notches, bool reverse)
        {
            if (notches == 0 || !Overflow) return;

            float impulse = -notches * WheelImpulse;
            if (reverse) impulse = -impulse;

            _velocity += impulse;
            if (_velocity > MaxSpeed) _velocity = MaxSpeed;
            if (_velocity < -MaxSpeed) _velocity = -MaxSpeed;
        }

        /// <summary>直接跳（拖拽或点击提示箭头用）。</summary>
        internal void Nudge(float pixels)
        {
            _offset += pixels;
            if (_offset > _max) _offset = _max;
            if (_offset < 0f) _offset = 0f;
        }

        /// <summary>
        /// 推进一帧，返回这一帧的位移（供"悬挂卡片"按速度摆动用）。
        /// </summary>
        internal float Update(float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return 0f;
            if (deltaSeconds > 0.1f) deltaSeconds = 0.1f;   // 掉帧时不至于瞬移

            float before = _offset;
            if (_velocity != 0f)
            {
                _offset += _velocity * deltaSeconds;

                // 指数阻尼：一开始快、越拖越慢，看着像真惯性
                _velocity *= (float)Math.Exp(-Damping * deltaSeconds);
                if (Math.Abs(_velocity) < RestSpeed) _velocity = 0f;

                if (_offset < 0f)
                {
                    _offset = 0f;
                    if (_velocity < 0f) _velocity = -_velocity * Bounce;
                }
                else if (_offset > _max)
                {
                    _offset = _max;
                    if (_velocity > 0f) _velocity = -_velocity * Bounce;
                }
            }

            return _offset - before;
        }

        /// <summary>每帧的"悬挂"摆动角度：跟速度同向、带一点滞后感。</summary>
        internal float SwayDegrees(float pixelVelocityScale)
        {
            float degrees = -_velocity * pixelVelocityScale;
            if (degrees > 7f) degrees = 7f;
            if (degrees < -7f) degrees = -7f;
            return degrees;
        }
    }

    /// <summary>
    /// 滚轮事件的"中转站"：低级鼠标钩子线程把格数攒进来，渲染线程每帧取走。
    /// 两个线程之间只交换一个 int，用 Interlocked 就够。
    ///
    /// 「面板」和「岛上的截图夹」都想要滚轮：谁最近被绘制过就归谁。
    /// 面板一关就不再绘制，自然把滚轮回给岛上。
    /// </summary>
    internal static class WheelRouter
    {
        private static int _pending;
        private static long _panelDrawnTicks;

        /// <summary>钩子线程调用：累加格数。</summary>
        internal static void Push(int notches)
        {
            if (notches == 0) return;
            System.Threading.Interlocked.Add(ref _pending, notches);
        }

        /// <summary>面板每帧调一次，声明「滚轮归我」。</summary>
        internal static void MarkPanelDrawn()
        {
            System.Threading.Volatile.Write(ref _panelDrawnTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>面板最近 400ms 内被绘制过吗（= 面板是打开的）。</summary>
        internal static bool PanelActive
        {
            get
            {
                long ticks = System.Threading.Volatile.Read(ref _panelDrawnTicks);
                if (ticks == 0L) return false;
                return (DateTime.UtcNow.Ticks - ticks) < TimeSpan.TicksPerMillisecond * 400L;
            }
        }

        /// <summary>渲染线程调用：取走并清零。</summary>
        internal static int Take()
        {
            if (_pending == 0) return 0;
            return System.Threading.Interlocked.Exchange(ref _pending, 0);
        }

        /// <summary>丢到下一次绘制之前（例如面板刚收起）。</summary>
        internal static void Clear()
        {
            System.Threading.Interlocked.Exchange(ref _pending, 0);
        }
    }
}
