# ScPad · 截屏图片存取（NotchPeninsula 插件）

本项目是面向 NotchPeninsula 的截屏图片存取插件，交互与产品取舍参考 jiuyang5354 的 snapline-windows 与 Alejandro Buján 的 Tendedero，但面向 NotchPeninsula 插件 API 独立实现，不含、也不再分发参考项目的源代码、图标或图片资源。

作者：GAHFTSD
许可：MIT
版本：1.0.0-preview-2（预发布）
插件标识：com.gahftsd.scpad

🖼 演示

以下都是自检台用假宿主渲染出来的真实画面，不是设计稿。

- 岛上（有图）：docs/widget-dark.png（宽 380）
- 岛上（没图也常驻）：docs/widget-empty-dark.png（宽 290）
- 截图夹面板（v1.0.0 紧凑版）：docs/panel-dark.png（宽 560）
- 复制成功的反馈：docs/panel-dark-copied.png（宽 560）
- 鼠标移到夹子上 → 变红色取下按钮：docs/panel-dark-remove.png（宽 560）
- 多图时点两端箭头翻页：docs/panel-crowded-dark.png（宽 560）
- 空面板（预留 4 格，点 + 上传）：docs/panel-empty-dark.png（宽 560）
- 把文件拖进面板：docs/panel-dark-drop.png（宽 560）
- 亮色主题：docs/panel-light.png（宽 560）

📌 项目出发点

ScPad 做的事情很具体：把截图收进来、挂在灵动岛上、点一下复制、按住拖到别的应用里粘贴，外加一页设置。设置界面交给官方「插件设置中心」渲染，岛上不再有设置页签。

- 不替换、不接管任何原生功能：媒体控制器、时间日期、硬件占用照旧。
- 常驻一条小状态栏：显示 [框] [数量]，没有图片时也显示 [空框] [0]。
- 有截图时最多叠 4 张缩略图，宽度约 160~260 逻辑像素，不抢媒体位置。
- 面板不因鼠标移开自动收起，保证拖出粘贴时拖放目标仍在。
- 本版岛上叠放与面板翻页以「点两端 ‹ ›」为主，不接管滚轮。
- 预览图由自检台用假宿主渲染，不是设计稿。

🧭 项目流程

- 插件装配：INotchPlugin 入口，注册岛上组件、面板、设置页与快捷键。
- 截图采集：STA 后台线程轮询剪贴板、监听保存目录、接收拖入图片、整屏抓图。
- 截图处理：保存落盘、SHA-256 判重、生成缩略图、维护不可变快照与 state.txt。
- 岛上组件：IWidget，4 张缩略图叠放 + 数字框，常驻显示，点击复制/展开。
- 截图夹面板：IDetailPage，单行卡片带、预留格、两端翻页、单击复制、按住拖出、↓ 取下。
- 设置中心：ISettingsPage 注入官方插件设置中心 NpsSettingsHub，无独立设置窗口。
- 构建发布：.NET SDK 构建或离线 csc 编译，产物 ScPad.dll + plugin.json。
- 自检与对账：PowerShell 自检台 97 项检查；MetaDump 比对宿主插件接口签名。

🧰 技术栈

- 语言与框架：C#、.NET 10、net10.0-windows10.0.19041.0
- 渲染：SkiaSharp（由宿主提供），无 WPF
- 宿主接口：NotchPeninsula Plugin API（IWidget / IDetailPage / ISettingsPage）
- Win32：RegisterHotKey、WM_HOTKEY、SetWindowPos；本版不接管滚轮
- 数据：state.txt、Recycle 目录、宿主注册表设置键
- 构建：dotnet build；离线 csc.exe；PowerShell 自检
- 许可：MIT

🚀 快速开始

1. 下载与解压

从 Releases 下载 ScPad-v1.0.0-preview-2.zip，解压得到 ScPad 文件夹。

2. 放入插件目录

插件目录 = 灵动岛 exe 所在文件夹下的 plugins。
将整个 ScPad 文件夹放入：

<灵动岛目录>\plugins\ScPad\
    ScPad.dll
    plugin.json
    使用说明.md
    LICENSE
    THIRD-PARTY-NOTICES.txt

目录名不要改。

3. 启用插件

打开 设置窗口 → 我的插件，确认 ScPad 为「运行中」。
打开 设置窗口 → 显示设置 → 显示内容，勾选「ScPad 截图夹」。

4. 从 Snapline 迁移

ScPad 不读取 Snapline 数据目录。旧截图想继续用：
设置 → 保存位置 → 选择文件夹… → 指向 %LocalAppData%\Snapline\Inbox。
ScPad 会把它当普通图片目录监听，不复制、不移动。

📁 项目结构

- ScPad/ — 本仓库根目录
- ScPad/ScPad.csproj — 工程文件（ProjectReference 指向宿主）
- ScPad/AssemblyInfo.cs — 程序集信息；InternalsVisibleTo 给自检台
- ScPad/ScPadPlugin.cs — 入口类：INotchPlugin，装配组件与页面
- ScPad/Settings.cs — PluginSettings（保存位置 / 开关 / 收集模式 / 快捷键）+ HotkeySpec
- ScPad/HotkeyHost.cs — 全局快捷键线程：RegisterHotKey + WM_HOTKEY + 录制
- ScPad/ScreenCapture.cs — 整屏抓图（抓之前把岛体藏约 140ms）
- ScPad/ShotStore.cs — 截图仓库：保存目录、落盘、判重、缩略图、快照、state.txt
- ScPad/CaptureService.cs — STA 后台线程：剪贴板轮询 + 目录监听 + 任务队列
- ScPad/ClipboardOps.cs — 剪贴板读写（PNG / 位图 / 文件路径三种形态）
- ScPad/ScPadWidget.cs — 岛上组件（IWidget）：4 张叠成一摞、无文字、点击复制/展开
- ScPad/ShotClipPanel.cs — 面板（IDetailPage）：单行卡片带、预留格、两端翻页、拖出
- ScPad/SettingsPage.cs — 设置页（ISettingsPage）：注入官方「插件设置中心」的控件列表
- ScPad/DynamicControls.cs — 设置中心自定义控件形态（运行时用 TypeBuilder 生成）
- ScPad/ScrollModel.cs — 横向滚动/翻页模型（本版未接管滚轮，保留为后续扩展）
- ScPad/WheelHook.cs — 低级鼠标钩子（本版未接管滚轮，保留为后续扩展）
- ScPad/Ui.cs — 绘制小工具（字体现取现用、画笔临时创建、路径尾部省略）
- ScPad/Reminders.cs — 提醒投递（init 属性走反射）
- ScPad/Log.cs — 宿主日志薄封装
- ScPad/build/ — 离线编译替身 + 自检台 + 元数据比对（不参与插件编译）
- ScPad/docs/ — 预览图：widget-dark.png、widget-empty-dark.png、panel-dark.png 等
- ScPad/requirements.txt — 无；本项目为 .NET 插件，依赖见 csproj
- ScPad/.gitignore — 忽略构建产物与本地调试文件
- ScPad/README.md — 本文档

✨ 核心内容

🖥 岛上（收起态）

- 右边数字框显示总数，0 也显示；左边是缩略图叠放，没图时用空框铺满。
- 点最上面那张 = 复制到剪贴板；点露出的窄边 = 复制那一张。
- 点数字 / 空框 / 空白 = 展开面板；右键 = 展开面板。
- 叠放是静态的（本版不接管滚轮）；想看更早的请打开面板。
- 把图片拖到这一摞上 = 自动展开面板并收图。
- 新截图到手默认自动展开面板，可在设置里关闭。
- 没有截图时也常驻：空框 + 右边 0。
- 缩略图大小随「截图夹上限」自动缩放，可解锁固定百分比。

📋 截图夹页

- 单击卡片：复制这张图，PNG + 位图 + 文件路径一起进剪贴板。
- 按住卡片拖到别的应用：直接拖出 PNG，Word / PPT / 微信 / QQ / 浏览器 / PS 可接收。
- 卡片底部正中 ↓ 按钮：把这张从夹子上取下；自己收的进 Recycle，外部原图不动。
- 把文件拖进面板：收进截图夹；点空槽的 + 可从剪贴板上传。
- 岛内右键：收起面板；面板无按钮栏、无标题提示文字。
- 点两端 ‹ ›：超过预留格数时翻页（本版不接管滚轮）。
- 面板顶部 3 格（对称）：更多设置 / 收集状态 / 图片编辑。
- 收集状态：自动·绿 / 手动·蓝 / 关闭·红；点它循环切换。
- 图片编辑：编辑对象 = 最近复制的那张，保存后覆盖原图；设置里可关。

⚙ 设置（官方「插件设置中心」）

面板里没有设置页，岛上只有截图夹本身。所有设置项注入官方插件「插件设置中心」NpsSettingsHub：
岛上齿轮组件 → 左侧插件列表选 ScPad。
没装设置中心不影响插件运行，只是没有统一设置界面。

截图夹分组：

- 截图夹上限（张）：默认 10，可设 4~100，推荐 4~16；超出时最老的移出，自己收的进 Recycle。
- 缩略图大小 %：25~200；不动则跟随上限。推荐值 = 100% × (10 / 上限)^0.6：4 张约 173%、10 张 100%、16 张约 75%。
- 新截图自动展开面板：默认开。
- 收集剪贴板里的图片：默认开。
- 收集模式：自动（全收）/ 手动（只认拖入）/ 关闭（全不收）；点面板顶部状态块也能切。
- 面板预留格数：1~8，默认 4；空槽也占位，超过才翻页。
- 抓屏时先藏起灵动岛：默认开；抓之前自己的组件先淡出约 80ms，抓完淡回。
- 面板顶部状态区：默认开；关掉后整行消失、面板更矮。
- 重新查看新手引导：按钮；再往截图夹里放一次 4 张教学图。
- 反馈问题（GitHub Issues）：按钮；打开仓库 Issues 页。
- 「图片编辑」用哪个应用：照片（系统默认）/ 画图；编辑对象 = 最近复制的那张，保存后覆盖原图。

保存位置分组：

- 保存位置（文本）：留空 = 系统默认截图位置 %USERPROFILE%\Pictures\Screenshots；按当前 Windows 用户解析，改过位置会跟随。
- 选择文件夹…：系统文件夹选择框。
- 打开：资源管理器。
- 录制：点「录制」后直接按组合键；Esc 取消、Backspace 清除；PrtSc 单独按下是系统截图键，插件不占用。
- 动作（选择）：截图并存入 / 展开·收起 / 复制最近一张。

其它分组：

- 恢复默认：只重置设置项，不动保存位置与已收集的截图。

改完立即生效：设置中心写入 → 宿主触发 SettingsChanged → 插件重读设置并让宿主重排组件宽度。
底层仍然是普通的插件设置键：HKCU\SOFTWARE\NotchPeninsula\Plugin.com.gahftsd.scpad.*。

三个动作的差别：

- 截图并存入：抓一张整屏，抓的瞬间把岛体藏约 140ms，存入截图夹同时放进剪贴板。
- 展开 / 收起：开关面板，等价于右键组件。
- 复制最近一张：最新那张进剪贴板。

📥 收集了什么

1. 剪贴板里的图片：Win + Shift + S 截图、任何程序里 Ctrl + C 的图片；文字、文件、自己刚复制/刚抓的图（带 ScPad.Origin 标记）会跳过。
2. 保存目录里新出现的图片：默认系统「屏幕截图」文件夹。
3. 拖进来的图片：面板里，或直接拖到岛上图标。
4. 快捷键 1（默认 Ctrl + PrtSc）抓的整屏。

收集模式：

- 自动：以上来源全收。
- 手动：只认拖入/手动上传，不自动收剪贴板与目录新图。
- 关闭：全不收。

同一张图按像素内容 SHA-256 判重，不会重复挂两次；收集侧最多保留最近 12 张，列表上限由设置决定。

🗂 数据与设置

- 图片本身（可配置）：默认 %USERPROFILE%\Pictures\Screenshots；自己抓的图命名 ScPad-yyyyMMdd-HHmmss-fff-xxxxxx.png。
- 列表状态 / 回收站：%LocalAppData%\NotchPeninsula\ScPad\（state.txt、Recycle\）。
- 开关 / 路径 / 快捷键：HKCU\SOFTWARE\NotchPeninsula\Plugin.com.gahftsd.scpad.*。
- 日志：%LocalAppData%\NotchPeninsula\app.log（统一前缀 [ScPad]）。

设置键包括：saveFolder、paused、clipboard、autoPeek、maxShots（4~100，默认 10）、thumbAuto / thumbScale（25~200；0 表示没设过 → 用推荐值）、collectMode、topStatus、hotkeyA / hotkeyAAction、hotkeyB / hotkeyBAction、hotkeyAView / hotkeyBView（只读展示用）。
快捷键存成 修饰键:虚拟键码，例如 2:44 = Ctrl + PrtSc。
想换插件自己的数据目录（便携 / 多开调试）：设环境变量 SCPAD_DATA 指向目标文件夹；只影响 state.txt 与 Recycle，不影响图片保存位置。

🛠 构建与自检

插件最终只需要一个文件：ScPad.dll。它不打包 SkiaSharp / WinForms，这些由宿主提供。

方式一：直接下载（只想用插件的人）

到 Releases 下载 ScPad-v1.0.0-preview-2.zip，解压出 ScPad/ 文件夹，整个丢进灵动岛插件目录：

<灵动岛目录>\plugins\ScPad\ScPad.dll
<灵动岛目录>\plugins\ScPad\plugin.json

方式二：用 .NET SDK 自己编（推荐，最稳）

前置条件：

- .NET SDK 10：dotnet --list-sdks 里要有 10.x；插件与宿主都必须是 net10.0-windows10.0.19041.0。
- NotchPeninsula 源码：插件要引用宿主程序集；本仓库用 ProjectReference 指向 ..\NotchPeninsula-main\NotchPeninsula.csproj。

目录结构关键在两者同级：

工作目录\
├─ ScPad\                      ← 本仓库
│   ├─ ScPad.csproj
│   └─ ...
└─ NotchPeninsula-main\        ← 宿主源码（解压出来的那一层，含 NotchPeninsula.csproj）
    ├─ NotchPeninsula.csproj
    └─ Plugin\PluginApi.cs

一条命令：

dotnet build ScPad.csproj -c Release

宿主源码不在默认位置时，用属性覆盖，别去改 csproj：

dotnet build ScPad.csproj -c Release -p:NotchPeninsulaProject="D:\src\NotchPeninsula-main\NotchPeninsula.csproj"

产物在哪：

- ScPad\bin\Release\net10.0-windows10.0.19041.0\ScPad.dll — 这个就是要装的插件。
- ..\ScPad-Plugin\ — 顺手生成的「可直接安装」文件夹。
- ..\ScPad-Plugin\ScPad.dll
- ..\ScPad-Plugin\plugin.json — { "dll": "ScPad.dll" }

把 ScPad.dll + plugin.json 一起放进 <灵动岛目录>\plugins\ScPad\ 即可。不要改名：插件 Id 与入口文件名在市场里是配对识别的，发布后不能再改。

常见编译错误：

- 找不到 NotchPeninsula.csproj：宿主源码不在同级目录 → 用 -p:NotchPeninsulaProject=... 指定。
- not found: net10.0-windows10.0.19041.0：没装 .NET 10 SDK，或只装了运行时。
- 宿主编译报缺包：宿主自己依赖 NuGet 包（SkiaSharp / NAudio / System.Management / ModelSensingProtocol），首次需要联网 dotnet restore。

注意：自定义控件是运行时生成的（DynamicControls.cs），不是源码里的类型。直接写一个普通 class 继承 SettingControl 会在真宿主里 TypeLoadException。

方式三：没有 .NET SDK（离线编译，复现发布包用的就是这条路）

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\build-offline.ps1

用系统自带的 %windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe，对着本机已有的 .NET 运行时程序集编译，不需要 SDK、不需要联网、不需要 NuGet。
原理写在 build\PluginApiStub.cs 顶部：把宿主 Plugin/PluginApi.cs 里插件用到的那部分类型抄成一个名为 NotchPeninsula.dll 的「签名替身」当编译引用；替身只在编译时存在（不发布、不会被加载），插件 dll 里留下的是“按名字+签名”的引用，运行时由真正的 NotchPeninsula.exe 兑现。
build\PluginApiStub.cs 里的每个声明都必须和真宿主签名逐字一致；宿主升级后请同步。有 SDK 的机器优先走方式二。

自检（不需要装进灵动岛）：

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\run-selftest.ps1

用假宿主把插件真的跑起来，97 项检查：组件宽度 / 面板尺寸 / 卡片翻页与滚动边界、保存位置默认值与改目录后新图真的落进新目录、默认保存位置确实按当前用户解析、自动展开默认开启、设置中心的控件形态 / 推荐百分比 / 按钮轮询 / 运行时生成的控件、常驻状态栏宽度、设置变更不自激、预留格数 / 右到左填充 / 拖动排序 / 剪贴板上传 / 弹簧收敛、快捷键默认值与真·系统注册（被别的进程占用时按 1409 跳过）、清除 / 录制 / 动作切换、剪贴板采集、整屏抓图（含“抓完进剪贴板”）、单击复制、按住拖出、↓ 移出并进回收、暂停、重启后恢复列表与设置；顺带把预览图渲染到 build\preview\。

和真宿主的接口对账：

build\metadump\MetaDump.cs 是个元数据比对小工具（用 System.Reflection.Metadata，不加载程序集）：

dotnet exec MetaDump.dll out.tsv 本插件.dll <市场里的其它插件>.dll ...

把本插件和已装的市场插件（OneSaying / FileStation / NpsIslandTuner / NpsSettingsHub / NtfyIsland）的清单对着看：凡是两边都调用的宿主成员（GetSetting / SetSetting / RegisterWidget / CloseDetailPage / ToggleDetailPage / InvalidateWidgetLayout / StartFileDrag / WidgetHit..ctor / ReminderData..ctor / Logger.* / NotchWindow.InstanceHandle …），签名完全一致 —— 这是“这个 dll 能被真宿主接住”最硬的证据。

顺带一个发现（也是本插件用反射写 ReminderData 的原因）：市场插件里 ReminderData::set_Title 的签名是 20 01 1F xx 01 0E，那个 1F 就是 modreq(IsExternalInit)；C# 5 编译器写不出这个 modreq，用对象初始化器赋值会在运行期 MissingMethodException，走 GetSetMethod(true).Invoke 才稳。

🧱 实现结构

- ScPad.csproj — 工程（ProjectReference 指向宿主）。
- AssemblyInfo.cs — 程序集信息（关掉自动生成；InternalsVisibleTo 给自检台）。
- ScPadPlugin.cs — 入口类：INotchPlugin，装配下面这些东西。
- Settings.cs — PluginSettings（保存位置 / 开关 / 收集模式 / 两个快捷键）+ HotkeySpec（格式化与校验）。
- HotkeyHost.cs — 全局快捷键线程：RegisterHotKey + WM_HOTKEY + 录制。
- ScreenCapture.cs — 整屏抓图（抓之前把岛体藏约 140ms）。
- ShotStore.cs — 截图仓库：保存目录、落盘、判重、缩略图、不可变快照、state.txt。
- CaptureService.cs — STA 后台线程：剪贴板轮询 + 目录监听 + 任务队列（复制/抓屏/选目录…）。
- ClipboardOps.cs — 剪贴板读写（PNG / 位图 / 文件路径三种形态）。
- ScPadWidget.cs — 岛上组件（IWidget）：4 张叠成一摞、无文字、点击复制/展开。
- ShotClipPanel.cs — 面板（IDetailPage）：单行卡片带 + 预留格 + 两端翻页，无标题/提示/按钮栏。
- SettingsPage.cs — 设置页（ISettingsPage）：注入官方「插件设置中心」的控件列表 + 按钮轮询。
- DynamicControls.cs — 设置中心的自定义控件形态（运行时用 TypeBuilder 生成）。
- ScrollModel.cs — 横向滚动/翻页模型：翻页冲量 → 阻尼 → 边界回弹；本版未接管滚轮。
- WheelHook.cs — 低级鼠标钩子（WH_MOUSE_LL）：本版未接管滚轮，保留为后续扩展。
- Ui.cs — 绘制小工具（字体现取现用、画笔临时创建、路径尾部省略）。
- Reminders.cs — 提醒投递（init 属性走反射）。
- Log.cs — 宿主日志薄封装。
- build\ — 离线编译替身 + 自检台 + 元数据比对（不参与插件编译）。
- docs\ — 预览图资源。

几个关键取舍：

- 三条线程，各管一件事。渲染线程（宿主）只画；采集线程（STA，插件自建）管剪贴板、文件监听、抓屏、文件夹选择框；快捷键线程（插件自建）管 RegisterHotKey 与录制。三者之间只有 ConcurrentQueue<Action>、Interlocked / volatile 字段与不可变快照。
- 快捷键为什么自带消息循环：RegisterHotKey(IntPtr.Zero, ...) 在 hWnd 为 NULL 时把 WM_HOTKEY 投递到调用线程的消息队列，连窗口都不用建 —— 一条线程 + PeekMessage 就够。录制时先把所有热键注销（否则重录当前热键会被自己吞掉），再用 GetAsyncKeyState 轮询“下一个按下的非修饰键”，用 GetKeyNameText 翻名字（PrtSc 要特判：扫描码 0x37 + 扩展位，名字是 "Prnt Scrn"）。
- 不可变快照。渲染线程只读 ShotStore.Snapshot；每次变更整体换一个新数组（Volatile.Write），老的位图从不显式 Dispose（交给终结器），不存在 use-after-free。
- 字体现取现用、画笔临时创建。不缓存 SKTypeface，每帧 new SKPaint 用完 using 释放；颜色全部从 frame.Theme.TextColor 派生。订阅 FontConfig.Changed 后调 host.InvalidateWidgetLayout() 重测宽度。
- 面板只有一个尺寸。MeasureWidth/Height 只在展开时算一次，内容整体垂直居中；卡片数量变化（上限调小 / 移出）时才重算，避免拖放过程中面板忽然变形。卡片带是单行的：一屏最多 4 张，多的靠两端 ‹ › 翻页。
- 拖出用宿主 StartFileDrag（文档第 14 条：详情页没有自己的窗口，必须由宿主代为发起）。在 OnMouseMove 里按 4px 阈值触发；拖放期间 OnMouseUp 不会来，「正在拖动」在 finally 复位。
- 抓屏时藏岛体：NotchWindow.InstanceHandle 是宿主公开的岛体句柄，抓图前 SetWindowPos(...SWP_HIDEWINDOW)、抓完在 finally 里 SWP_SHOWWINDOW；拿不到句柄时照常抓。

🔁 与参考实现（Snapline）的差异

- 窗口形态：Snapline 是屏幕顶部一条独立 WPF 窗口；ScPad 是灵动岛组件 + 右键展开的面板，不额外开窗口、不抢焦点。
- 快捷键默认：Snapline 默认 Ctrl + Alt + T；ScPad 两格可自定义，默认 Ctrl + PrtSc = 截图并存入，动作三选一。
- 设置界面：Snapline 是 WPF 设置窗口（四页）；ScPad 设置注入官方插件设置中心，岛上无设置页签。
- 多显示器 / 全屏应用检测：Snapline 自己做；ScPad 交给灵动岛自己的自动隐藏 / 全屏策略。
- 预览与编辑：Snapline 双击预览、长按画图编辑、右键菜单；ScPad 单击复制、↓ 移出、打开保存位置、图片编辑用系统应用；其它交给系统。
- 拖出内容：Snapline 拖出携带“图片 + PNG + FileDrop”；ScPad 拖出携带 PNG 文件路径（宿主 API 的形态），图片形态用单击复制。
- 更新 / 启动 / 外观：Snapline 有更新检查、开机启动、透明度 / 主题；ScPad 不做，宿主统一管外观与更新。
- 数据位置：Snapline 数据在 %LOCALAPPDATA%\Snapline；ScPad 数据在 %LOCALAPPDATA%\NotchPeninsula\ScPad，与 Snapline 完全不共享。
- 收集能力：Snapline 12 张、判重、暂停收集、文件夹监听；ScPad 保留判重、收集模式、文件夹监听，列表上限可设。

🩺 排错

1. 「我的插件」里显示加载失败：看 %LocalAppData%\NotchPeninsula\app.log 的 [ScPad] 行；多数是宿主版本与插件 API 不匹配，用方式二重新编译一份。
2. 岛上看不到：确认「显示内容」勾了 ScPad 截图夹，且插件运行中；没有截图时常驻 [0] + 空框。
3. 快捷键没反应：设置页里那一格若是警示色，说明注册失败（被别的程序占用），换一个组合；Alt + Ctrl + 字母这类组合通常很空。
4. 复制没反应：剪贴板可能被别的程序占着；插件会重试 5 次并弹提醒。
5. 拖不出去：宿主是单线程渲染，拖放期间岛体会短暂“卡住”，属 OLE 拖放循环的正常表现。
6. 抓屏时岛体闪了一下：那是刻意藏了约 140ms，这样照片里才没有灵动岛。
7. 设置中心里看不到 ScPad：先确认插件「运行中」，再在设置窗口点底部「重新扫描」；点进去显示「该插件不在此平台设置」= RegisterSettingsPage 没执行（看日志有没有注册异常）。
8. 面板翻不动：只有卡片超过「面板预留格数」时，两端 ‹ › 才可用；本版不接管滚轮。
9. 收集状态不对：点面板顶部状态块循环切换自动 / 手动 / 关闭；也可在设置中心改「收集模式」。
10. 图片编辑应用不对：设置里选「照片（系统默认）」或「画图」；编辑对象是最近复制的那张，保存后覆盖原图。

📜 许可与署名

ScPad 以 MIT 许可发布，全文见 LICENSE。

它在交互与产品取舍上参考了下面两个项目，因此主动保留署名（全文见 THIRD-PARTY-NOTICES.txt）：

- Snapline · Windows 版：作者 jiuyang5354；主要参考，参考版本 v1.3.0。
- Tendedero：作者 Alejandro Buján；交互概念的源头，Snapline 的上游。

三点说明：

1. 独立实现：C# 重写、面向 NotchPeninsula 的插件 API（.NET 10 + SkiaSharp，无 WPF），未包含、未再分发上述项目的源代码、图标或图片；界面元素全部由代码绘制。
2. 独立的名称与标识：ScPad / com.gahftsd.scpad / ScPad.dll，不使用对方的名称、图标或插件标识，也不暗示与它们同源或获其背书。
3. 不耦合数据：不读取、不写入 %LOCALAPPDATA%\Snapline；不复用其设置、剪贴板标记与文件名约定。

📬 联系方式

- 作者：GAHFTSD
- 邮箱：gahftsd@hotmail.com
- GitHub：https://github.com/GAHFTSD
- 许可：MIT

最后更新：ScPad 1.0.0-preview-2（预发布）

内容部分由 AI 生成
