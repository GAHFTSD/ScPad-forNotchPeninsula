using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NotchPeninsula.Plugins;

namespace ScPad
{
    /// <summary>
    /// 采集服务：一条专用的 STA 后台线程，负责
    ///   1. 轮询剪贴板序号，把新图片抓下来（截图工具、Ctrl+C 的图片都算）；
    ///   2. 监听保存目录里新出现的图片，自动存入截图夹；
    ///   3. 执行渲染线程 / 面板 / 快捷键派过来的任务
    ///      （复制、存入、移出、打开目录、整屏抓图、选择目录）。
    ///
    /// 为什么要单独一条 STA 线程：System.Windows.Forms.Clipboard 与文件夹选择对话框都必须在
    /// STA 线程上用，而宿主的渲染线程一帧都不能卡。所有跨线程共享的状态都是并发容器或
    /// Interlocked / volatile 字段。
    /// </summary>
    internal sealed class CaptureService : IDisposable
    {
        private const int PeekSeconds = 6;

        private readonly ScPadPlugin _plugin;
        private readonly ShotStore _store;
        private readonly IPluginHost _host;

        private readonly ConcurrentQueue<Action> _jobs = new ConcurrentQueue<Action>();
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ManualResetEvent _wake = new ManualResetEvent(false);

        private Thread _thread;
        private volatile bool _stop;
        private bool _started;

        private uint _lastSequence;
        private int _ticks;

        private FileSystemWatcher _watcher;
        private string _watchedFolder;

        private long _peekDeadlineTicks;

        private SettingsButtonPoller _buttons;

        // 「图片编辑」跟踪：编辑哪张、编辑前的修改戳、多久后放弃跟踪
        private string _editPath;
        private string _editStamp;
        private long _editDeadlineTicks;

        // 设置变更：宿主可能在一次拖动里连发几十个事件，合并成"每 250ms 处理一次"
        private int _settingsDirty;
        private int _settingsApplying;

        // 上一次真正应用过的值 —— 只有变了才做那些有代价的事（重注册热键 / 重写 state / 重排布局）
        private int _appliedMaxItems = -1;
        private double _appliedScale = -1;
        private string _appliedFolder;
        private bool _appliedPaused;
        private int _appliedSlots = -1;
        private bool _appliedPanelActions = true;
        private string _appliedHotkeyA;
        private string _appliedHotkeyB;

        internal CaptureService(ScPadPlugin plugin, ShotStore store, IPluginHost host)
        {
            _plugin = plugin;
            _store = store;
            _host = host;
        }

        internal ShotStore Store { get { return _store; } }

        internal void Start()
        {
            if (_started) return;
            _started = true;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ScPad.Capture";
            try { _thread.SetApartmentState(ApartmentState.STA); }
            catch { }
            _thread.Start();
        }

        private void Loop()
        {
            Log.Info("[ScPad] 采集线程已启动");

            try
            {
                EnsureWatcher();
                _lastSequence = ClipboardOps.Sequence();

                // 设置中心里的按钮（选择文件夹 / 录制快捷键 / 恢复默认…）
                _buttons = new SettingsButtonPoller(_host, OnSettingsButton);

                // 状态文件与首次导入放到后台，别卡住宿主的 Initialize
                _jobs.Enqueue(delegate
                {
                    _store.Load();
                });

                while (!_stop)
                {
                    Action job;
                    while (_jobs.TryDequeue(out job))
                    {
                        try { job(); }
                        catch (Exception ex) { Log.Error("[ScPad] 后台任务异常", ex); }
                    }

                    _ticks++;

                    try { PollClipboard(); }
                    catch (Exception ex) { Log.Warn("[ScPad] 轮询剪贴板失败：" + ex.Message); }

                    try { DrainPending(); }
                    catch (Exception ex) { Log.Warn("[ScPad] 存入新图片失败：" + ex.Message); }

                    if ((_ticks % 40) == 0)
                    {
                        try { _store.Prune(); } catch { }
                        try { EnsureWatcher(); } catch { }
                    }

                    try { CheckPeek(); } catch { }
                    try { CheckEditResult(); } catch { }

                    try { PumpSettingsChanges(); }
                    catch (Exception ex) { Log.Warn("[ScPad] 应用设置变更失败：" + ex.Message); }

                    // 设置中心里的「按钮」靠比对自增计数发现；测一下开销可控就每 3 个 tick 查一次
                    if ((_ticks % 3) == 0)
                    {
                        try { if (_buttons != null) _buttons.Poll(); }
                        catch (Exception ex) { Log.Warn("[ScPad] 设置按钮轮询失败：" + ex.Message); }
                    }

                    _wake.WaitOne(150);
                    _wake.Reset();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 采集线程异常退出", ex);
            }
            finally
            {
                DisposeWatcher();
                Log.Info("[ScPad] 采集线程已停止");
            }
        }

        // ---- 剪贴板 ----

        private void PollClipboard()
        {
            if (_store.CollectMode != PluginSettings.CollectAuto || !_store.ClipboardEnabled)
            {
                _lastSequence = ClipboardOps.Sequence();
                return;
            }

            uint sequence = ClipboardOps.Sequence();
            if (sequence == 0u || sequence == _lastSequence) return;
            _lastSequence = sequence;

            byte[] png = ClipboardOps.TryReadImage();
            if (png == null) return;

            Shot shot = _store.AddCaptured(png);
            if (shot != null)
            {
                Log.Info("[ScPad] 存入剪贴板截图：" + shot.Path);
                OnCaptured(shot);
            }
        }

        // ---- 目录监听（保存目录 = 监听目录）----

        private void EnsureWatcher()
        {
            string folder = _store.SaveFolder;

            if (!string.Equals(folder, _watchedFolder, StringComparison.OrdinalIgnoreCase))
            {
                DisposeWatcher();
                _watchedFolder = folder;
            }
            else if (_watcher != null)
            {
                return;   // 已经盯着了
            }

            if (string.IsNullOrEmpty(folder)) return;
            if (!_store.EnsureSaveFolder()) return;   // 目录还没建出来：过一会儿再看

            _watcher = CreateWatcher(folder);
            if (_watcher != null) Log.Info("[ScPad] 正在监听保存目录：" + folder);
        }

        private FileSystemWatcher CreateWatcher(string folder)
        {
            try
            {
                var watcher = new FileSystemWatcher(folder);
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.IncludeSubdirectories = false;
                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Deleted += OnFileDeleted;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 监听保存目录失败：" + folder + " — " + ex.Message);
                return null;
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!ShotStore.IsImageFile(e.FullPath)) return;
            if (_store.ContainsPath(e.FullPath)) return;   // 自己刚写下去的，已经在列表里了
            _pending.Enqueue(e.FullPath);
            _wake.Set();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!ShotStore.IsImageFile(e.FullPath)) return;
            if (_store.ContainsPath(e.FullPath)) return;
            _pending.Enqueue(e.FullPath);
            _wake.Set();
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            _jobs.Enqueue(delegate { _store.Prune(); });
            _wake.Set();
        }

        /// <summary>新文件可能还在写，读失败就留着重试（最多 15 个来回）。</summary>
        private void DrainPending()
        {
            int guard = 0;
            string path;
            while (guard++ < 8 && _pending.TryDequeue(out path))
            {
                if (_store.ContainsPath(path))
                {
                    _attempts.Remove(path);
                    continue;
                }

                try
                {
                    Shot shot = _store.AddExternalFile(path);
                    _attempts.Remove(path);
                    if (shot != null)
                    {
                        Log.Info("[ScPad] 存入新截图：" + path);
                        OnCaptured(shot);
                    }
                }
                catch (IOException)
                {
                    int count;
                    if (!_attempts.TryGetValue(path, out count)) count = 0;
                    count++;
                    if (count < 15)
                    {
                        _attempts[path] = count;
                        _pending.Enqueue(path);
                    }
                    else
                    {
                        _attempts.Remove(path);
                    }
                }
                catch (Exception ex)
                {
                    _attempts.Remove(path);
                    Log.Warn("[ScPad] 存入图片失败：" + path + " — " + ex.Message);
                }
            }
        }

        // ---- 新截图 ----

        private void OnCaptured(Shot shot)
        {
            OnCaptured(shot, false);
        }

        private void OnCaptured(Shot shot, bool panelAlreadyOpen)
        {
            try { shot.Sway(); } catch { }

            try
            {
                if (_store.AutoPeek && !panelAlreadyOpen)
                {
                    Interlocked.Exchange(ref _peekDeadlineTicks, DateTime.UtcNow.AddSeconds(PeekSeconds).Ticks);
                    _host.TryOpenDetailPage(ScPadPlugin.WidgetId);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 自动展开失败：" + ex.Message);
            }
        }

        private void CheckPeek()
        {
            long deadline = Interlocked.Read(ref _peekDeadlineTicks);
            if (deadline == 0) return;
            if (DateTime.UtcNow.Ticks < deadline) return;

            Interlocked.Exchange(ref _peekDeadlineTicks, 0);
            _host.CloseDetailPage();
        }

        /// <summary>用户自己动了面板就别再自动收起来。</summary>
        internal void CancelPeek()
        {
            Interlocked.Exchange(ref _peekDeadlineTicks, 0);
        }

        // ---- 面板 / 组件 / 快捷键派下来的任务 ----

        internal void RequestCopy(Shot shot)
        {
            if (shot == null) return;
            _jobs.Enqueue(delegate { CopyNow(shot); });
            _wake.Set();
        }

        private void CopyNow(Shot shot)
        {
            string error;
            bool ok;
            try { ok = ClipboardOps.CopyImage(shot.Path, out error); }
            catch (Exception ex) { ok = false; error = ex.Message; }

            if (ok)
            {
                shot.MarkCopied();
                _lastSequence = ClipboardOps.Sequence();   // 自己写的剪贴板不要被当成新截图
                Log.Info("[ScPad] 已复制到剪贴板：" + shot.Path);
                return;
            }

            Log.Warn("[ScPad] 复制失败：" + error);
            Reminders.Post(_host, "ScPad", "复制失败：" + (error == null ? "剪贴板被占用" : error), null);
        }

        internal void RequestCopyLatest()
        {
            Shot[] shots = _store.Snapshot;
            if (shots.Length == 0)
            {
                Reminders.Post(_host, "ScPad", "还没有截到图片", null);
                return;
            }
            RequestCopy(shots[shots.Length - 1]);
        }

        /// <summary>
        /// 整屏抓一张：先存入截图夹，同时放进剪贴板
        /// （Ctrl + PrtSc 本来就应该能直接 Ctrl + V 粘）。
        /// </summary>
        internal void RequestScreenCapture()
        {
            _jobs.Enqueue(delegate { ScreenCaptureNow(); });
            _wake.Set();
        }

        private void ScreenCaptureNow()
        {
            bool hide = _store.Settings.HideOnCapture;

            string error;
            byte[] png = ScreenCapture.CaptureAllScreens(hide, ApplyDim, out error);
            if (png == null)
            {
                Reminders.Post(_host, "ScPad", "抓屏失败：" + (error == null ? "未知原因" : error), null);
                return;
            }

            Shot shot = _store.AddCaptured(png);
            if (shot == null)
            {
                // 判重提示放在面板里（不弹通知）
                string reason = _store.LastSkipReason;
                if (reason == "dup") Hint("这张已经存过了");
                else Hint("这张没能存进来");
                return;
            }

            Log.Info("[ScPad] 抓屏并存入：" + shot.Path);
            OnCaptured(shot);
            CopyNow(shot);
        }

        /// <summary>抓屏时让插件自己的组件淡出/淡入（由 ScreenCapture 在抓的那一瞬间调用）。</summary>
        private void ApplyDim(float amount)
        {
            try
            {
                if (_plugin.Widget != null) _plugin.Widget.SetDim(amount);
                if (_plugin.Panel != null) _plugin.Panel.SetDim(amount);
            }
            catch { }
        }

        /// <summary>提示：优先写在面板里；面板没开就等下次打开时给。</summary>
        private void Hint(string text)
        {
            try
            {
                if (_plugin.Panel != null) _plugin.Panel.ShowHint(text);
            }
            catch { }
        }

        /// <summary>打开项目仓库的 Issues 页（设置中心里的「反馈问题」按钮）。</summary>
        internal void RequestOpenIssues()
        {
            _jobs.Enqueue(delegate
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo("https://github.com/GAHFTSD/ScPad-forNotchPeninsula/issues");
                    info.UseShellExecute = true;
                    System.Diagnostics.Process.Start(info);
                }
                catch (Exception ex)
                {
                    Log.Warn("[ScPad] 打开 issues 页面失败：" + ex.Message);
                    Reminders.Post(_host, "反馈问题",
                        "欢迎在同名 GitHub 仓库（ScPad-forNotchPeninsula）中留下 issues。",
                        null, TimeSpan.FromSeconds(8));
                }
            });
            _wake.Set();
        }

        /// <summary>面板底部「更多设置」：宿主没有打开设置中心的 API，只能引导一下。</summary>
        internal void RequestSettingsHelp()
        {
            _jobs.Enqueue(delegate
            {
                Hint("点岛上的齿轮 → 选 ScPad，可详细设置");
                Reminders.Post(_host, "详细设置",
                    "点灵动岛上的「齿轮」组件 → 左侧列表选 ScPad，就是全部设置项。",
                    null,
                    TimeSpan.FromSeconds(7));
            });
            _wake.Set();
        }

        /// <summary>
        /// 面板底部「图片编辑」：编辑**最近一次复制的那张**（没有就退回最新一张）。
        /// 打开前记下文件的修改戳，保存回原路径后自动刷新缩略图（位置不变）。
        /// </summary>
        internal void RequestEditTarget()
        {
            _jobs.Enqueue(delegate { EditTargetNow(); });
            _wake.Set();
        }

        private void EditTargetNow()
        {
            try
            {
                Shot shot = PickEditTarget();
                if (shot == null)
                {
                    Hint("还没有截图可以编辑");
                    return;
                }

                string path = shot.Path;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    Hint("这张图已经不在磁盘上了");
                    return;
                }

                if (_store.Settings.EditorMode == 1)
                {
                    // 画图
                    try
                    {
                        var info = new System.Diagnostics.ProcessStartInfo("mspaint.exe", "\"" + path + "\"");
                        info.UseShellExecute = true;
                        System.Diagnostics.Process.Start(info);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[ScPad] 打开画图失败，退回系统默认：" + ex.Message);
                        OpenWithShell(path);
                    }
                }
                else
                {
                    // 系统默认（通常是「照片」，在里面点编辑即可）
                    OpenWithShell(path);
                }

                // 记下修改戳：保存回原路径后，tick 会检测到并原位刷新缩略图
                _editPath = path;
                _editStamp = StampOf(path);
                _editDeadlineTicks = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 30L;

                Hint("已打开，保存后会覆盖原图");
                Log.Info("[ScPad] 打开图片编辑：" + path);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 打开图片编辑失败", ex);
                Hint("打开图片编辑失败");
            }
        }

        /// <summary>自检用：直接问「图片编辑」会选哪张。</summary>
        internal Shot PickEditTargetForTest()
        {
            return PickEditTarget();
        }

        /// <summary>挑编辑目标：最近一次复制的那张（CopiedTicks 最大），没有就最新一张。</summary>
        private Shot PickEditTarget()
        {
            Shot[] shots = _store.Snapshot;
            if (shots.Length == 0) return null;

            Shot best = null;
            long bestTicks = 0;
            for (int i = 0; i < shots.Length; i++)
            {
                long ticks = shots[i].CopiedTicks;
                if (ticks > bestTicks)
                {
                    bestTicks = ticks;
                    best = shots[i];
                }
            }

            return best != null ? best : shots[shots.Length - 1];
        }

        private static string StampOf(string path)
        {
            try
            {
                var info = new System.IO.FileInfo(path);
                if (!info.Exists) return "";
                return info.LastWriteTimeUtc.Ticks.ToString() + "|" + info.Length.ToString();
            }
            catch { return ""; }
        }

        /// <summary>采集线程 tick：编辑保存回原路径后，原位刷新缩略图。</summary>
        private void CheckEditResult()
        {
            if (_editPath == null) return;

            if (DateTime.UtcNow.Ticks > _editDeadlineTicks)
            {
                _editPath = null;
                return;
            }

            string stamp = StampOf(_editPath);
            if (stamp.Length == 0 || string.Equals(stamp, _editStamp, StringComparison.Ordinal)) return;

            _editStamp = stamp;
            Shot refreshed = _store.Refresh(_editPath);
            if (refreshed != null)
            {
                Hint("已更新编辑后的图片");
                Log.Info("[ScPad] 编辑后的图片已原位刷新：" + refreshed.Path);
            }
        }

        private static void OpenWithShell(string path)
        {
            var info = new System.Diagnostics.ProcessStartInfo(path);
            info.UseShellExecute = true;
            System.Diagnostics.Process.Start(info);
        }

        /// <summary>从剪贴板上传一张（面板里点空槽 / 设置中心的按钮）。</summary>
        internal void RequestPasteFromClipboard()
        {
            _jobs.Enqueue(delegate { PasteFromClipboardNow(); });
            _wake.Set();
        }

        private void PasteFromClipboardNow()
        {
            try
            {
                byte[] png = ClipboardOps.TryReadImage();
                if (png == null)
                {
                    Hint("剪贴板里没有图片");
                    return;
                }

                Shot shot = _store.AddCaptured(png);
                if (shot == null)
                {
                    string reason = _store.LastSkipReason;
                    Hint(reason == "dup" ? "这张已经存过了" : "这张没能存进来");
                    return;
                }

                Log.Info("[ScPad] 从剪贴板上传：" + shot.Path);
                Hint("已从剪贴板存入");
                OnCaptured(shot, true);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 从剪贴板上传失败", ex);
                Hint("从剪贴板上传失败");
            }
        }

        /// <summary>快捷键触发的动作（按设置里那个槽位的动作类型执行）。</summary>
        internal void RequestHotkey(int slot)
        {
            _jobs.Enqueue(delegate { RunHotkey(slot); });
            _wake.Set();
        }

        private void RunHotkey(int slot)
        {
            PluginSettings settings = _store.Settings;
            int action = settings.HotkeyAction(slot);

            Log.Info("[ScPad] 快捷键 " + (slot + 1).ToString() + " → " + PluginSettings.ActionText(action));

            switch (action)
            {
                case PluginSettings.ActionTogglePanel:
                    _host.ToggleDetailPage(ScPadPlugin.WidgetId);
                    break;

                case PluginSettings.ActionCopyLatest:
                    RequestCopyLatest();
                    break;

                default:
                    if (settings.CollectMode != PluginSettings.CollectAuto)
                    {
                        Hint(settings.CollectMode == PluginSettings.CollectOff ? "收集已关闭" : "手动收集下请用拖入加图");
                        return;
                    }

                    ScreenCaptureNow();
                    break;
            }
        }

        internal void RequestImport(IEnumerable<string> paths)
        {
            if (_store.CollectMode == PluginSettings.CollectOff)
            {
                Hint("收集已关闭");
                return;
            }

            if (paths == null) return;
            var list = new List<string>(paths);
            if (list.Count == 0) return;

            _jobs.Enqueue(delegate
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        Shot shot = _store.AddExternalFile(list[i]);
                        if (shot != null) OnCaptured(shot);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[ScPad] 拖入图片失败：" + list[i] + " — " + ex.Message);
                    }
                }
            });
            _wake.Set();
        }

        internal void RequestRemove(Shot shot)
        {
            if (shot == null) return;
            _jobs.Enqueue(delegate { _store.Remove(shot); });
            _wake.Set();
        }

        internal void RequestClear()
        {
            _jobs.Enqueue(delegate { _store.Clear(); });
            _wake.Set();
        }

        internal void RequestOpenFolder()
        {
            _jobs.Enqueue(delegate { OpenPath(_store.SaveFolder); });
            _wake.Set();
        }

        internal void RequestOpenFile(Shot shot)
        {
            if (shot == null) return;
            _jobs.Enqueue(delegate { OpenPath(shot.Path); });
            _wake.Set();
        }

        /// <summary>换保存目录：走系统文件夹选择框（必须在这个 STA 线程上弹）。</summary>
        /// <summary>
        /// 用户在设置中心里改了任何一项。
        /// 只置一个脏标记（不直接干活）：宿主拖滑块时会连发大量事件，逐条处理会把自己累死。
        /// </summary>
        internal void RequestSettingsChanged()
        {
            System.Threading.Interlocked.Exchange(ref _settingsDirty, 1);
            _wake.Set();
        }

        /// <summary>采集线程的 tick 里调用：把攒下来的设置变更一次性应用掉。</summary>
        private void PumpSettingsChanges()
        {
            if (System.Threading.Interlocked.Exchange(ref _settingsDirty, 0) == 0) return;
            ApplySettingsChanged();
        }

        private void ApplySettingsChanged()
        {
            // 防重入：万一某处又写了设置（会再触发一次 SettingsChanged），这里直接吞掉
            if (System.Threading.Interlocked.CompareExchange(ref _settingsApplying, 1, 0) != 0) return;

            try
            {
                _store.Settings.Reload();

                PluginSettings settings = _store.Settings;
                bool needLayout = false;
                bool needWarm = false;

                int maxItems = settings.MaxItems;
                if (maxItems != _appliedMaxItems)
                {
                    _appliedMaxItems = maxItems;
                    _store.TrimToLimit();
                    needLayout = true;
                }

                double scale = settings.EffectiveThumbScale;
                if (Math.Abs(scale - _appliedScale) > 0.001)
                {
                    _appliedScale = scale;
                    needLayout = true;
                }

                bool paused = settings.Paused;
                if (paused != _appliedPaused)
                {
                    _appliedPaused = paused;
                    needLayout = true;
                }

                int slots = settings.PanelSlots;
                if (slots != _appliedSlots)
                {
                    _appliedSlots = slots;
                    needLayout = true;
                }

                string folder = settings.SaveFolder;
                if (!string.Equals(folder, _appliedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    _appliedFolder = folder;
                    bool folderOk = _store.EnsureSaveFolder();
                    EnsureWatcher();
                    needWarm = true;

                    if (!folderOk && !string.IsNullOrEmpty(_store.SaveFolderError))
                    {
                        Reminders.Post(_host, "保存位置不可用", _store.SaveFolderError, null);
                    }
                }

                // 热键：只有组合真的变了才重注册（RegisterHotKey/UnregisterHotKey 都不便宜）
                string hotkeyA = settings.HotkeyA.Text + "|" + settings.HotkeyAAction.ToString();
                string hotkeyB = settings.HotkeyB.Text + "|" + settings.HotkeyBAction.ToString();
                bool hotkeyChanged = !string.Equals(hotkeyA, _appliedHotkeyA, StringComparison.Ordinal)
                                     || !string.Equals(hotkeyB, _appliedHotkeyB, StringComparison.Ordinal);
                if (hotkeyChanged)
                {
                    _appliedHotkeyA = hotkeyA;
                    _appliedHotkeyB = hotkeyB;
                    if (_plugin.Hotkeys != null) _plugin.Hotkeys.Refresh();
                    needWarm = true;
                }

                settings.PublishHotkeyHints();

                if (needLayout) _plugin.NotifyLayoutChanged();
                if (needWarm && _buttons != null)
                {
                    // 让按钮轮询重新对齐基准，避免"恢复默认"之后又把自己当成被点了一次
                    _buttons = new SettingsButtonPoller(_host, OnSettingsButton);
                }

                Log.Info("[ScPad] 设置已更新：上限 " + maxItems.ToString() + " 张，缩略图 " +
                         settings.EffectiveThumbScale.ToString() + "%，保存位置 " + folder);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 应用设置变更失败", ex);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _settingsApplying, 0);
            }
        }

        /// <summary>设置中心里点了某个按钮（Key 命名的那个）。</summary>
        private void OnSettingsButton(string key)
        {
            try
            {
                switch (key)
                {
                    case "pickFolder":
                        RequestChooseFolder();
                        break;

                    case "openFolder":
                        RequestOpenFolder();
                        break;

                    case "recordHotkeyA":
                        StartRecording(0);
                        break;

                    case "recordHotkeyB":
                        StartRecording(1);
                        break;

                    case "clearHotkeyA":
                        ClearHotkey(0);
                        break;

                    case "clearHotkeyB":
                        ClearHotkey(1);
                        break;

                    case "pasteFromClipboard":
                        RequestPasteFromClipboard();
                        break;

                    case "resetAll":
                        ResetAll();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 设置按钮处理失败：" + key, ex);
            }
        }

        private void StartRecording(int slot)
        {
            if (_plugin.Hotkeys == null) return;
            _plugin.Hotkeys.StartRecording(slot);
            Reminders.Post(_host, "录制快捷键 " + (slot == 0 ? "1" : "2"),
                "现在按下想用的组合键（至少一个修饰键，或 F1~F24）；Esc 取消。", null);
        }

        private void ClearHotkey(int slot)
        {
            PluginSettings settings = _store.Settings;
            settings.SetHotkey(slot, HotkeySpec.None);
            if (_plugin.Hotkeys != null) _plugin.Hotkeys.Refresh();
            settings.PublishHotkeyHints();
            Log.Info("[ScPad] 已清除快捷键 " + (slot + 1).ToString());
        }

        private void ResetAll()
        {
            PluginSettings settings = _store.Settings;
            settings.SetCollectMode(PluginSettings.CollectAuto);
            settings.AutoPeek = true;
            settings.SetMaxItems(PluginSettings.DefaultShots);
            settings.SetThumbScale(0);
            settings.SetPanelSlots(PluginSettings.DefaultSlots);
            settings.SetHideOnCapture(true);
            settings.SetClipboardEnabled(true);
            settings.SetHotkey(0, new HotkeySpec(HotkeySpec.ModControl, HotkeySpec.VkSnapshot));
            settings.SetHotkeyAction(0, PluginSettings.ActionCapture);
            settings.SetHotkey(1, HotkeySpec.None);
            settings.SetHotkeyAction(1, PluginSettings.ActionTogglePanel);

            ApplySettingsChanged();
            Reminders.Post(_host, "已恢复默认设置", "保存位置与已收集的截图没有改动。", null);
        }

        internal void RequestChooseFolder()
        {
            _jobs.Enqueue(delegate { ChooseFolderNow(); });
            _wake.Set();
        }

        private void ChooseFolderNow()
        {
            try
            {
                string current = _store.SaveFolder;

                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择截图保存位置";
                    dialog.ShowNewFolderButton = true;
                    try
                    {
                        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.SelectedPath = current;
                    }
                    catch { }

                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                    string folder = dialog.SelectedPath;
                    if (string.IsNullOrEmpty(folder)) return;

                    _store.Settings.SetSaveFolder(folder);
                    _store.EnsureSaveFolder();
                    Log.Info("[ScPad] 保存位置改为：" + folder);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 选择保存位置失败：" + ex.Message);
                Reminders.Post(_host, "ScPad", "打不开文件夹选择框：" + ex.Message, null);
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var info = new ProcessStartInfo();
                info.FileName = path;
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 打开失败：" + path + " — " + ex.Message);
            }
        }

        // ---- 收尾 ----

        private void DisposeWatcher()
        {
            FileSystemWatcher watcher = _watcher;
            _watcher = null;
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _stop = true;
            try { _wake.Set(); } catch { }

            try
            {
                if (_thread != null && _thread.IsAlive) _thread.Join(1500);
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 等待采集线程退出失败：" + ex.Message);
            }

            DisposeWatcher();
            try { _wake.Dispose(); } catch { }
        }
    }
}
