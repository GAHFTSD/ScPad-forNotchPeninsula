using System;

namespace ScPad
{
    /// <summary>
    /// 一张卡片的"悬挂弹簧"：被冲击后按阻尼弹簧回位，用来做物理惯性抖动。
    ///
    /// 和 <see cref="ScrollModel"/> 的区别：滚动模型管的是"整条带子平移"，
    /// 这个是管**单张卡片自己的晃动**（新图弹进来时旁边几张会被震一下、
    /// 左右滑动时卡片会往滑动方向"甩"一下再荡回来）。
    /// </summary>
    internal sealed class SpringMotion
    {
        /// <summary>弹簧刚度：越大回得越快。</summary>
        private const float Stiffness = 190f;

        /// <summary>阻尼：小一点会多荡几下，大一点更快停。</summary>
        private const float Damping = 11f;

        private float _value;
        private float _velocity;

        internal float Value { get { return _value; } }

        internal float Velocity { get { return _velocity; } }

        internal bool Idle { get { return _value == 0f && _velocity == 0f; } }

        /// <summary>给一个冲击（正负决定方向）。</summary>
        internal void Impulse(float velocity)
        {
            if (velocity == 0f) return;
            _velocity += velocity;

            float limit = 2600f;
            if (_velocity > limit) _velocity = limit;
            if (_velocity < -limit) _velocity = -limit;
        }

        /// <summary>按半隐式欧拉推进一帧。</summary>
        internal void Step(float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return;
            if (deltaSeconds > 0.05f) deltaSeconds = 0.05f;
            if (_velocity == 0f && _value == 0f) return;

            float acceleration = -Stiffness * _value - Damping * _velocity;
            _velocity += acceleration * deltaSeconds;
            _value += _velocity * deltaSeconds;

            // 收敛：别让余震永远抖下去（每帧都在重绘，抖太久也费电）
            if (Math.Abs(_value) < 0.05f && Math.Abs(_velocity) < 2f)
            {
                _value = 0f;
                _velocity = 0f;
            }
        }

        internal void Reset()
        {
            _value = 0f;
            _velocity = 0f;
        }
    }
}
