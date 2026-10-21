using System;
using System.Reflection;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// 提醒（Toast）投递器。
    ///
    /// ReminderData 的属性全是 C# 9 的 init 存取器，它们的 setter 在元数据里带
    /// IsExternalInit modreq。插件源码必须保持「任何 C# 版本都能编过」的写法（见 README），
    /// 直接写对象初始化器会编出一个签名不匹配的 setter 引用，运行期 MissingMethodException。
    /// 所以这里统一用反射写属性 —— 反射不看 modreq，拿到 setter 就能调。
    /// </summary>
    internal static class Reminders
    {
        private static readonly object Sync = new object();

        private static bool _resolved;
        private static MethodInfo _setTitle;
        private static MethodInfo _setBody;
        private static MethodInfo _setDuration;
        private static MethodInfo _setOnClick;

        internal static void Post(IPluginHost host, string title, string body, Action onClick)
        {
            Post(host, title, body, onClick, TimeSpan.FromSeconds(4));
        }

        internal static void Post(IPluginHost host, string title, string body, Action onClick, TimeSpan duration)
        {
            if (host == null) return;

            try
            {
                Resolve();

                ReminderData data = new ReminderData();
                Write(_setTitle, data, title);
                Write(_setBody, data, body);
                Write(_setDuration, data, duration);
                if (onClick != null) Write(_setOnClick, data, onClick);

                host.PostReminder(data);
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 提醒投递失败：" + ex.Message);
            }
        }

        private static void Write(MethodInfo setter, object target, object value)
        {
            if (setter == null) return;
            setter.Invoke(target, new object[] { value });
        }

        private static void Resolve()
        {
            lock (Sync)
            {
                if (_resolved) return;
                _resolved = true;

                try
                {
                    Type type = typeof(ReminderData);
                    _setTitle = Setter(type, "Title");
                    _setBody = Setter(type, "Body");
                    _setDuration = Setter(type, "Duration");
                    _setOnClick = Setter(type, "OnClick");
                }
                catch (Exception ex)
                {
                    Log.Warn("[ScPad] 解析 ReminderData 属性失败：" + ex.Message);
                }
            }
        }

        private static MethodInfo Setter(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null) return null;
                return property.GetSetMethod(true);
            }
            catch
            {
                return null;
            }
        }
    }
}
