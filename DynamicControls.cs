using System;
using System.Reflection;
using System.Reflection.Emit;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// 运行时生成「设置中心的自定义控件形态」。
    ///
    /// 为什么必须这么绕：设置中心按**属性名**识别控件（Options / DefaultValue / Min+Max /
    /// Action / 都没有 = 文本），所以文本、按钮、只读这三种形态得自己派生
    /// <c>SettingControl</c> —— 而它是宿主的 `abstract record`，record 会带一个
    /// **abstract 的克隆方法 `<Clone>$`**：
    ///
    ///   * C# 9+ 编译器只允许 record 继承 record（于是要 record 版源码）；
    ///   * C# 5 编译器（我们离线编译用的）不认识 record，编出来的普通 class 不会生成
    ///     `<Clone>$`，运行期真宿主一加载就抛
    ///     `TypeLoadException: Method '&lt;Clone>$' ... does not have an implementation`。
    ///
    /// 折中办法：用 <see cref="TypeBuilder"/> 在运行时造这几个类型 —— IL 里可以给方法起
    /// `<Clone>$` 这种标识符，于是同一份源码既能被 C# 5 编译，又能在真宿主里正确加载。
    /// 抽象方法按**运行期发现**逐个实现，宿主以后再加抽象成员也不会漏。
    /// </summary>
    internal static class DynamicControls
    {
        private static readonly object Sync = new object();

        private static bool _created;
        private static Type _text;
        private static Type _action;
        private static Type _readonly;

        /// <summary>纯文本输入项（无判别属性 → 设置中心判成文本）。参数：key, label, placeholder, maxLength, description。</summary>
        internal static SettingControl Text(string key, string label, string placeholder, int maxLength, string description)
        {
            EnsureCreated();
            return (SettingControl)Activator.CreateInstance(_text,
                new object[] { key, label, placeholder, maxLength, description });
        }

        /// <summary>按钮项（有 Action → 按钮；点击时设置中心把该 Key 的值 +1）。参数：key, label, action, description。</summary>
        internal static SettingControl Action(string key, string label, string action, string description)
        {
            EnsureCreated();
            return (SettingControl)Activator.CreateInstance(_action,
                new object[] { key, label, action, description });
        }

        /// <summary>只读文本项（ReadOnly = true）。参数：key, label, placeholder, description。</summary>
        internal static SettingControl ReadonlyText(string key, string label, string placeholder, string description)
        {
            EnsureCreated();
            return (SettingControl)Activator.CreateInstance(_readonly,
                new object[] { key, label, placeholder, description });
        }

        internal static bool Created { get { return _created; } }

        /// <summary>提前把类型造出来（宿主加载插件时就能发现 IL 问题，别等到设置中心去读属性）。</summary>
        internal static void EnsureCreated()
        {
            if (_created) return;

            lock (Sync)
            {
                if (_created) return;

                AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName("ScPad.DynamicControls"), AssemblyBuilderAccess.Run);
                ModuleBuilder module = assembly.DefineDynamicModule("ScPad.DynamicControls");

                _text = Build(module, "TextSettingControl",
                    new string[] { "Placeholder", "MaxLength", "Description" },
                    new Type[] { typeof(string), typeof(int), typeof(string) },
                    -1);

                _action = Build(module, "ActionSettingControl",
                    new string[] { "Action", "Description" },
                    new Type[] { typeof(string), typeof(string) },
                    -1);

                // ReadOnly 固定为 true（不是构造参数）
                _readonly = Build(module, "ReadonlyTextSettingControl",
                    new string[] { "Placeholder", "Description", "ReadOnly" },
                    new Type[] { typeof(string), typeof(string), typeof(bool) },
                    2);

                _created = true;
                Log.Info("[ScPad] 设置中心的自定义控件类型已生成：" +
                         _text.Name + " / " + _action.Name + " / " + _readonly.Name);
            }
        }

        /// <summary>
        /// 造一个 <c>SettingControl</c> 的派生类型：字段 = Key + Label + 传入的若干只读属性，
        /// 构造签名 = (key, label, 各属性…)，并实现基类的所有抽象方法（`<Clone>$` 返回自身副本）。
        /// </summary>
        private static Type Build(ModuleBuilder module, string name, string[] names, Type[] types, int constantIndex)
        {
            Type baseType = typeof(SettingControl);
            TypeBuilder builder = module.DefineType(name,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, baseType);

            // 所有字段：_key、_label 先存一份（`<Clone>$` 重建副本时要用）
            int own = 2 + names.Length;
            FieldBuilder[] fields = new FieldBuilder[own];
            Type[] fieldTypes = new Type[own];

            fields[0] = builder.DefineField("_key", typeof(string), FieldAttributes.Private);
            fields[1] = builder.DefineField("_label", typeof(string), FieldAttributes.Private);
            fieldTypes[0] = typeof(string);
            fieldTypes[1] = typeof(string);

            for (int i = 0; i < names.Length; i++)
            {
                fields[2 + i] = builder.DefineField("_" + names[i], types[i], FieldAttributes.Private);
                fieldTypes[2 + i] = types[i];
            }

            // ---- 构造 ----
            ConstructorInfo baseCtor = baseType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { typeof(string), typeof(string) }, null);

            if (baseCtor == null)
                throw new MissingMethodException("SettingControl 找不到 (string, string) 构造函数");

            var parameters = new System.Collections.Generic.List<Type>();
            parameters.Add(typeof(string));
            parameters.Add(typeof(string));
            for (int i = 0; i < names.Length; i++)
            {
                if (i == constantIndex) continue;      // 固定值不占构造参数
                parameters.Add(types[i]);
            }

            ConstructorBuilder ctor = builder.DefineConstructor(MethodAttributes.Public,
                CallingConventions.Standard, parameters.ToArray());
            ILGenerator il = ctor.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, baseCtor);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, fields[0]);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, fields[1]);

            int argument = 3;
            for (int i = 0; i < names.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_0);
                if (i == constantIndex)
                {
                    if (types[i] == typeof(bool)) il.Emit(OpCodes.Ldc_I4_1);
                    else il.Emit(OpCodes.Ldnull);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg, (short)argument);
                    argument++;
                }
                il.Emit(OpCodes.Stfld, fields[2 + i]);
            }

            il.Emit(OpCodes.Ret);

            // ---- 只读属性 ----
            for (int i = 0; i < names.Length; i++)
            {
                PropertyBuilder property = builder.DefineProperty(names[i], PropertyAttributes.None, types[i], null);
                MethodBuilder getter = builder.DefineMethod("get_" + names[i],
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    types[i], Type.EmptyTypes);
                ILGenerator getterIl = getter.GetILGenerator();
                getterIl.Emit(OpCodes.Ldarg_0);
                getterIl.Emit(OpCodes.Ldfld, fields[2 + i]);
                getterIl.Emit(OpCodes.Ret);
                property.SetGetMethod(getter);
            }

            // ---- 实现基类的抽象方法（真宿主里就是 record 的 <Clone>$）----
            MethodInfo[] members = baseType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < members.Length; i++)
            {
                MethodInfo abstractMethod = members[i];
                if (!abstractMethod.IsAbstract) continue;
                if (abstractMethod.DeclaringType != baseType) continue;
                if (abstractMethod.IsStatic) continue;

                MethodBuilder implementation = builder.DefineMethod(abstractMethod.Name,
                    MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    abstractMethod.ReturnType, ParameterTypes(abstractMethod));

                ILGenerator methodIl = implementation.GetILGenerator();

                bool isClone = abstractMethod.GetParameters().Length == 0
                               && typeof(SettingControl).IsAssignableFrom(abstractMethod.ReturnType);

                if (isClone)
                {
                    // 用同样的字段重新构造一个副本
                    methodIl.Emit(OpCodes.Ldarg_0);
                    methodIl.Emit(OpCodes.Ldfld, fields[0]);
                    methodIl.Emit(OpCodes.Ldarg_0);
                    methodIl.Emit(OpCodes.Ldfld, fields[1]);
                    for (int f = 0; f < names.Length; f++)
                    {
                        if (f == constantIndex)
                        {
                            if (types[f] == typeof(bool)) methodIl.Emit(OpCodes.Ldc_I4_1);
                            else methodIl.Emit(OpCodes.Ldnull);
                        }
                        else
                        {
                            methodIl.Emit(OpCodes.Ldarg_0);
                            methodIl.Emit(OpCodes.Ldfld, fields[2 + f]);
                        }
                    }
                    methodIl.Emit(OpCodes.Newobj, ctor);
                    methodIl.Emit(OpCodes.Ret);
                }
                else
                {
                    methodIl.Emit(OpCodes.Ldstr, "ScPad: " + name + "." + abstractMethod.Name + " 未实现");
                    methodIl.Emit(OpCodes.Newobj, typeof(NotImplementedException).GetConstructor(new Type[] { typeof(string) }));
                    methodIl.Emit(OpCodes.Throw);
                }

                builder.DefineMethodOverride(implementation, abstractMethod);
            }

            return builder.CreateType();
        }

        private static Type[] ParameterTypes(MethodInfo method)
        {
            ParameterInfo[] infos = method.GetParameters();
            Type[] types = new Type[infos.Length];
            for (int i = 0; i < infos.Length; i++) types[i] = infos[i].ParameterType;
            return types;
        }
    }
}
