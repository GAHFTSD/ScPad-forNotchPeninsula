using System.Reflection;

[assembly: AssemblyTitle("ScPad")]
[assembly: AssemblyDescription("ScPad —— NotchPeninsula 插件")]
[assembly: AssemblyCompany("GAHFTSD")]
[assembly: AssemblyProduct("ScPad")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// 自检台（build\selftest\SelfTest.cs）要摸到 internal 的 Settings / Hotkeys / Capture，
// 才能在没有真实键盘输入的情况下验证快捷键与保存目录逻辑。SelfTest 不随插件发布。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SelfTest")]
