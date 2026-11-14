using System;
using System.Reflection;

namespace ScPad
{
    /// <summary>
    /// 让灵动岛「先藏起来再截图」的桥。
    ///
    /// 为什么不用直接引用：宿主是单文件发布，2.2.0 的不同构建之间
    /// <c>Renderer.IslandHidden</c> / <c>NotchWindow.ToggleIslandVisibility()</c>
    /// 的可见性与存在性并不稳定（真实踩过 MissingFieldException 把整条抓屏打断）。
    /// 这里全部走反射：找到就用，找不到就安静地退回「不藏岛」，
    /// **绝不让异常冒到抓屏主流程**。
    /// </summary>
    internal static class HostIsland
    {
        private static bool _probed;

        /// <summary>宿主的藏岛入口是否可用（探测过后才有意义）。</summary>
        internal static bool Available { get { Probe(); return _toggle != null || _hiddenField != null || _hiddenProperty != null; } }
        private static MethodInfo _toggle;
        private static FieldInfo _hiddenField;
        private static PropertyInfo _hiddenProperty;
        private static bool _toggleFailed;
        private static bool _warned;

        /// <summary>上一次是不是我们自己把岛藏起来的（用来决定要不要唤回）。</summary>
        private static bool _weHid;

        /// <summary>用宿主的「显示/隐藏灵动岛」开关切一次：它和自动隐藏共用同一条退场动画。</summary>
        internal static bool TryToggle()
        {
            Probe();
            if (_toggle == null) return false;

            try
            {
                _toggle.Invoke(null, null);
                return true;
            }
            catch (Exception ex)
            {
                if (!_toggleFailed)
                {
                    _toggleFailed = true;
                    Log.Warn("[ScPad] 调用宿主 ToggleIslandVisibility 失败，本次不藏岛：" + ex.Message);
                }
                return false;
            }
        }

        /// <summary>岛当前是否处于隐藏态（读不到就返回 false）。</summary>
        internal static bool IsHidden()
        {
            Probe();
            if (_hiddenField == null) return false;

            try
            {
                if (_hiddenField != null) return (bool)_hiddenField.GetValue(null);
                if (_hiddenProperty != null) return (bool)_hiddenProperty.GetValue(null, null);
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 抓屏前把岛收起来：优先走宿主的退场动画，成功返回 true。
        /// 已经被用户藏起来时返回 true（不用再动）。
        /// </summary>
        internal static bool Hide()
        {
            if (IsHidden())
            {
                _weHid = false;
                return true;
            }

            _weHid = TryToggle();
            return _weHid;
        }

        /// <summary>
        /// 岛缩在屏幕顶端时，先把它滑回来（走同一条退场/入场动画），再长实况窗。
        /// 返回 true = 现在是可见的（本来就没藏，或已唤醒）。
        /// </summary>
        internal static bool EnsureVisible()
        {
            Probe();
            if (!IsHidden()) return true;

            bool ok = TryToggle();
            if (ok)
            {
                // 宿主入场动画约 0.45 秒，等它站定再长气泡
                try { System.Threading.Thread.Sleep(520); } catch { }
            }
            return ok;
        }

        /// <summary>
        /// 等岛**完全可见**（拖动动画结束）再返回：先唤醒，再轮询 IsHidden 直到为 false，
        /// 最后再等约 0.12 秒让弹簧动画站定。
        /// </summary>
        internal static bool WaitUntilVisible(int timeoutMs)
        {
            Probe();

            long deadline = Environment.TickCount64 + (timeoutMs < 200 ? 200 : timeoutMs);

            if (IsHidden())
            {
                TryToggle();

                while (Environment.TickCount64 < deadline)
                {
                    if (!IsHidden()) break;
                    try { System.Threading.Thread.Sleep(40); } catch { }
                }
            }

            if (IsHidden()) return false;

            // 已经不再"藏"，但弹簧还在收尾：再让一帧动画站定
            try { System.Threading.Thread.Sleep(120); } catch { }
            return true;
        }

        /// <summary>抓完把岛唤回来（只在我们自己藏过的时候动）。</summary>
        internal static void Restore()
        {
            if (!_weHid) return;
            _weHid = false;

            try { TryToggle(); }
            catch { }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly == null) continue;

                    string name = assembly.GetName().Name;
                    if (name == null || name.IndexOf("NotchPeninsula", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (_toggle == null)
                    {
                        Type window = FindType(assembly, "NotchWindow");
                        if (window != null)
                        {
                            _toggle = window.GetMethod("ToggleIslandVisibility",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                null, Type.EmptyTypes, null);
                        }
                    }

                    if (_hiddenField == null)
                    {
                        Type renderer = FindType(assembly, "Renderer");
                        if (renderer != null)
                        {
                            _hiddenField = renderer.GetField("IslandHidden",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            if (_hiddenField == null)
                            {
                                _hiddenProperty = renderer.GetProperty("IslandHidden",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Log.Warn("[ScPad] 探测宿主藏岛接口失败，将不藏岛：" + ex.Message);
                }
            }

            try
            {
                Log.Info("[ScPad] 藏岛探测：toggle=" + (_toggle != null ? "有" : "无") +
                    " field=" + (_hiddenField != null ? "有" : "无") +
                    " prop=" + (_hiddenProperty != null ? "有" : "无"));
            }
            catch { }

            if (!_warned && _toggle == null)
            {
                _warned = true;
                Log.Info("[ScPad] 宿主没有可用的「显示/隐藏灵动岛」入口，抓屏时不藏岛（不影响截图）");
            }
        }

        private static Type FindType(Assembly assembly, string simpleName)
        {
            try
            {
                Type[] types = assembly.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i] != null && types[i].Name == simpleName) return types[i];
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                Type[] partial = ex.Types;
                if (partial != null)
                {
                    for (int i = 0; i < partial.Length; i++)
                    {
                        if (partial[i] != null && partial[i].Name == simpleName) return partial[i];
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
