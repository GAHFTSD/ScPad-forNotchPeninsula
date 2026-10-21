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
            if (_store.Paused || !_store.ClipboardEnabled)
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
            try { shot.Sway(); } catch { }

            try
            {
                if (_store.AutoPeek)
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
            string error;
            byte[] png = ScreenCapture.CaptureAllScreens(out error);
            if (png == null)
            {
                Reminders.Post(_host, "ScPad", "抓屏失败：" + (error == null ? "未知原因" : error), null);
                return;
            }

            Shot shot = _store.AddCaptured(png);
            if (shot == null)
            {
                Reminders.Post(_host, "ScPad", "这张和已有截图一模一样，没有重复存入", null);
                return;
            }

            Log.Info("[ScPad] 抓屏并存入：" + shot.Path);
            OnCaptured(shot);
            CopyNow(shot);
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
                    ScreenCaptureNow();
                    break;
            }
        }

        internal void RequestImport(IEnumerable<string> paths)
        {
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
