using System;
using NotchPeninsula;

namespace ScPad
{
    /// <summary>
    /// 宿主日志的薄封装。Logger 属于「非冻结契约」，任何时候都不该让它的失败牵连插件，
    /// 所以这里每一个入口都自己吞异常。
    /// </summary>
    internal static class Log
    {
        internal static void Info(string message)
        {
            try { Logger.Info(message); } catch { }
        }

        internal static void Warn(string message)
        {
            try { Logger.Warn(message); } catch { }
        }

        internal static void Error(string message, Exception ex)
        {
            try { Logger.Error(message, ex); }
            catch { }
        }
    }
}
