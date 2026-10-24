using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace ScPad
{
    /// <summary>截图夹里的一张图。</summary>
    internal sealed class Shot
    {
        internal readonly string Path;
        internal readonly string Hash;
        internal readonly SKBitmap Thumb;
        internal readonly DateTime Created;
        internal readonly double Tilt;
        internal readonly bool Owned;
        internal readonly string Name;

        // 渲染线程会读这两个计时器；写入方是后台线程 / 渲染线程本身。
        // 用 Interlocked 处理，避免读到半个 long。
        private long _wobbleTicks;
        private long _copiedTicks;

        internal Shot(string path, string hash, SKBitmap thumb, DateTime created, double tilt, bool owned)
        {
            Path = path;
            Hash = hash;
            Thumb = thumb;
            Created = created;
            Tilt = tilt;
            Owned = owned;

            string name = "";
            try { name = System.IO.Path.GetFileNameWithoutExtension(path); }
            catch { }
            Name = name;
        }

        internal string FileName { get { return Name; } }

        // 时间在加入时就算好：绘制每帧都要用，绝不能在里面碰文件系统
        internal string TimeText { get { return Created.ToString("HH:mm"); } }

        internal long WobbleTicks { get { return Interlocked.Read(ref _wobbleTicks); } }

        internal long CopiedTicks { get { return Interlocked.Read(ref _copiedTicks); } }

        internal void MarkCopied()
        {
            Interlocked.Exchange(ref _copiedTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _wobbleTicks, DateTime.UtcNow.Ticks);
        }

        internal void Sway()
        {
            Interlocked.Exchange(ref _wobbleTicks, DateTime.UtcNow.Ticks);
        }
    }

    /// <summary>
    /// 截图仓库：保存目录、持久化、去重、缩略图与「不可变快照」。
    ///
    /// 保存目录由设置决定（默认 = 系统默认的截图位置，按当前 Windows 用户解析）；
    /// 剪贴板 / 整屏抓到的图直接落在那里，同一个目录也是监听来源，所以别处存进来的图片
    /// 会自动被收进来。状态文件与回收站留在插件自己的数据目录，不往用户的图片文件夹里添东西。
    ///
    /// 线程模型：渲染线程只读 <see cref="Snapshot"/>（每次变更整体换一个新数组，
    /// 老数组被 GC 收掉；位图从不显式 Dispose，所以不存在 use-after-free）；
    /// 所有写操作都走内部锁，可以来自后台采集线程，也可以来自面板回调。
    /// </summary>
    internal sealed class ShotStore
    {


        private const string StateHeader = "# ScPad state v2";
        private const string FilePrefix = "ScPad-";

        private readonly object _lock = new object();
        private readonly List<Shot> _items = new List<Shot>();
        private readonly HashSet<string> _hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Shot[] _snapshot = new Shot[0];

        private readonly IPluginHost _host;
        private readonly PluginSettings _settings;
        private readonly string _root;
        private readonly string _recycle;
        private readonly string _stateFile;

        private readonly Random _random = new Random();

        private string _lastFolderError = "";

        /// <summary>AddCaptured 返回 null 的原因："dup" = 判重跳过，"error" = 写盘/解码失败。</summary>
        internal string LastSkipReason { get { return _lastSkipReason; } }

        private volatile string _lastSkipReason = "";

        internal ShotStore(IPluginHost host, PluginSettings settings)
        {
            _host = host;
            _settings = settings;

            _root = ResolveRoot();
            _recycle = System.IO.Path.Combine(_root, "Recycle");
            _stateFile = System.IO.Path.Combine(_root, "state.txt");

            EnsureSaveFolder();
        }

        /// <summary>
        /// 插件自己的数据目录（状态文件 / 回收站）。默认 %LOCALAPPDATA%\NotchPeninsula\ScPad；
        /// 需要换位置（便携版 / 自检）时设环境变量 SCPAD_DATA。
        /// 注意：截图本身不在这里，在设置里的「保存位置」。
        /// </summary>
        private static string ResolveRoot()
        {
            try
            {
                string custom = Environment.GetEnvironmentVariable("SCPAD_DATA");
                if (!string.IsNullOrEmpty(custom)) return System.IO.Path.GetFullPath(custom);
            }
            catch { }

            return RecycleFolder();
        }

        internal Shot[] Snapshot { get { return Volatile.Read(ref _snapshot); } }

        /// <summary>当前生效的截图上限（设置里的「截图夹上限」，4~100）。</summary>
        internal int Limit { get { return _settings.MaxItems; } }

        /// <summary>上限被调小之后，把超出的最老几张从列表里移出（插件自己收的进回收目录）。</summary>
        internal void TrimToLimit()
        {
            var dropped = new System.Collections.Generic.List<Shot>();
            lock (_lock)
            {
                int limit = Limit;
                while (_items.Count > limit)
                {
                    dropped.Add(_items[0]);
                    _hashes.Remove(_items[0].Hash);
                    _items.RemoveAt(0);
                }
            }

            if (dropped.Count == 0) return;
            for (int i = 0; i < dropped.Count; i++)
            {
                if (dropped[i].Owned) RecycleFile(dropped[i].Path);
            }
            Publish(true);
        }

        /// <summary>图片保存目录（设置项，默认 = 系统默认截图位置）。</summary>
        internal string SaveFolder { get { return _settings.SaveFolder; } }

        internal string Root { get { return _root; } }

        internal PluginSettings Settings { get { return _settings; } }

        internal bool Paused { get { return _settings.Paused; } }

        /// <summary>收集模式：0 = 自动，1 = 手动，2 = 关闭。</summary>
        internal int CollectMode { get { return _settings.CollectMode; } }

        internal void SetCollectMode(int mode)
        {
            _settings.SetCollectMode(mode);
        }

        internal bool AutoPeek { get { return _settings.AutoPeek; } }

        internal bool ClipboardEnabled { get { return _settings.ClipboardEnabled; } }

        internal void SetPaused(bool paused)
        {
            // 兼容旧调用：暂停 = 关闭收集，恢复 = 自动收集
            _settings.SetCollectMode(paused ? PluginSettings.CollectOff : PluginSettings.CollectAuto);
        }

        internal void SetAutoPeek(bool on)
        {
            _settings.AutoPeek = on;
        }

        internal string SaveFolderError { get { lock (_lock) return _lastFolderError; } }

        /// <summary>把保存目录准备好；顺手建出来，免得用户选了不存在的目录就写不进去。</summary>
        internal bool EnsureSaveFolder()
        {
            try
            {
                string folder = _settings.SaveFolder;
                if (string.IsNullOrEmpty(folder)) return false;

                Directory.CreateDirectory(folder);

                lock (_lock) _lastFolderError = "";
                return true;
            }
            catch (Exception ex)
            {
                lock (_lock) _lastFolderError = ex.Message;
                Log.Warn("[ScPad] 保存目录不可用：" + ex.Message);
                return false;
            }
        }

        // ---- 持久化 ----

        /// <summary>把 state.txt 里的列表读回来（在后台线程调用，别卡宿主）。</summary>
        internal void Load()
        {
            try
            {
                if (!File.Exists(_stateFile)) return;

                string[] lines = File.ReadAllLines(_stateFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    bool owned = false;
                    int tab = line.IndexOf('\t');
                    if (tab > 0)
                    {
                        owned = line[0] == '1';
                        line = line.Substring(tab + 1).Trim();
                    }

                    if (line.Length == 0 || !File.Exists(line)) continue;

                    try { AddExternalFile(line, owned); }
                    catch (Exception ex) { Log.Warn("[ScPad] 恢复截图失败：" + line + " — " + ex.Message); }
                }

                Log.Info("[ScPad] 已恢复 " + Snapshot.Length + " 张截图");
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 读取状态失败：" + ex.Message);
            }
        }

        private void Save(Shot[] items)
        {
            try
            {
                Directory.CreateDirectory(_root);

                var text = new StringBuilder();
                text.AppendLine(StateHeader);
                for (int i = 0; i < items.Length; i++)
                {
                    text.Append(items[i].Owned ? "1" : "0");
                    text.Append('\t');
                    text.AppendLine(items[i].Path);
                }

                string temporary = _stateFile + ".tmp";
                File.WriteAllText(temporary, text.ToString(), Encoding.UTF8);

                if (!File.Exists(_stateFile))
                {
                    File.Move(temporary, _stateFile);
                    return;
                }

                try
                {
                    // 原子替换，最理想
                    File.Replace(temporary, _stateFile, null);
                }
                catch (Exception)
                {
                    // 少数环境（受限沙箱 / 某些网络盘）不允许替换已有文件 → 退化为覆盖
                    File.Copy(temporary, _stateFile, true);
                    try { File.Delete(temporary); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 保存状态失败：" + ex.GetType().Name + " — " + ex.Message + " @ " + _stateFile);
            }
        }

        // ---- 采集 ----

        /// <summary>把刚抓到的一屏（PNG 字节）写进保存目录并存入截图夹。</summary>
        internal Shot AddCaptured(byte[] png)
        {
            if (png == null || png.Length == 0) return null;

            SKBitmap full = null;
            try { full = SKBitmap.Decode(png); }
            catch (Exception ex) { Log.Warn("[ScPad] 解码截屏失败：" + ex.Message); }

            if (full == null)
            {
                _lastSkipReason = "error";
                return null;
            }

            try
            {
                string hash = Hash(png, full.Width, full.Height);
                lock (_lock)
                {
                    if (_hashes.Contains(hash))
                    {
                        _lastSkipReason = "dup";
                        return null;
                    }
                }

                string folder = _settings.SaveFolder;
                if (!EnsureSaveFolder())
                {
                    Log.Warn("[ScPad] 保存目录不可写，这张只留在列表里：" + folder);
                    folder = _root;
                    try { Directory.CreateDirectory(folder); } catch { }
                }

                // 命名：SP-yyMMdd-HHmmss-xxxx.png（面板里只显示 SP-X，X 是面板顺序号）
                string name = "SP-" + DateTime.Now.ToString("yyMMdd-HHmmss") + "-" +
                              Guid.NewGuid().ToString("N").Substring(0, 4) + ".png";
                string path = System.IO.Path.Combine(folder, name);
                File.WriteAllBytes(path, png);

                return Register(path, hash, full, true, DateTime.Now, true);
            }
            catch (Exception ex)
            {
                _lastSkipReason = "error";
                Log.Warn("[ScPad] 保存截屏失败：" + ex.Message);
                return null;
            }
            finally
            {
                try { full.Dispose(); } catch { }
            }
        }

        /// <summary>收进一个磁盘上的图片文件（引用原文件，不复制）。文件读不出来时抛 IOException。</summary>
        internal Shot AddExternalFile(string path)
        {
            return AddExternalFile(path, false);
        }

        internal Shot AddExternalFile(string path, bool owned)
        {
            string leaf = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(leaf) &&
                !leaf.StartsWith("guide-", StringComparison.OrdinalIgnoreCase))
            {
                _settings.SetGuideSeen(true);   // 用户已经有真实截图了，之后清空也不再弹引导
            }

            if (string.IsNullOrEmpty(path)) return null;
            if (!IsImageFile(path)) return null;

            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                if (length <= 0L) throw new IOException("文件还是空的");
                if (length > 64L * 1024L * 1024L) throw new IOException("文件太大");
                bytes = new byte[length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset != bytes.Length) throw new IOException("读取不完整");
            }

            SKBitmap full = null;
            try { full = SKBitmap.Decode(bytes); }
            catch (Exception ex) { throw new IOException("不是可识别的图片", ex); }

            if (full == null) throw new IOException("不是可识别的图片");

            try
            {
                string hash = Hash(bytes, full.Width, full.Height);
                string fullPath;
                try { fullPath = System.IO.Path.GetFullPath(path); }
                catch { fullPath = path; }

                lock (_lock)
                {
                    if (_hashes.Contains(hash)) return null;
                }

                DateTime created = DateTime.Now;
                try { created = File.GetLastWriteTime(fullPath); }
                catch { }

                return Register(fullPath, hash, full, owned, created, true);
            }
            finally
            {
                try { full.Dispose(); } catch { }
            }
        }

        private Shot Register(string path, string hash, SKBitmap full, bool owned, DateTime created, bool save)
        {
            SKBitmap thumb = MakeThumbnail(full);
            var shot = new Shot(path, hash, thumb, created, NextTilt(), owned);

            lock (_lock)
            {
                int existing = -1;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (!string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                    existing = i;
                    break;
                }

                if (existing >= 0)
                {
                    // 同一个文件被重新收进来（图片被改过）→ 原位换掉
                    _hashes.Remove(_items[existing].Hash);
                    _items[existing] = shot;
                    _hashes.Add(hash);
                }
                else
                {
                    _items.Add(shot);
                    _hashes.Add(hash);

                    // 超过上限就从最老的那张开始挤下去（外部文件只从列表移出，绝不删原文件）
                    int limit = Limit;
                    while (_items.Count > limit)
                    {
                        _hashes.Remove(_items[0].Hash);
                        _items.RemoveAt(0);
                    }
                }
            }

            Publish(save);
            return shot;
        }

        internal bool ContainsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>文件被删掉了就跟着从列表里移出（原文件不受影响）。</summary>
        internal void Prune()
        {
            bool removed = false;
            lock (_lock)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    bool alive;
                    try { alive = File.Exists(_items[i].Path); }
                    catch { alive = false; }

                    if (alive) continue;
                    _hashes.Remove(_items[i].Hash);
                    _items.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) Publish(true);
        }

        /// <summary>
        /// 在面板里拖动排序：把第 from 张插到第 to 张的位置（数组下标，0 = 最老）。
        /// 顺序会写进 state.txt，重启后保持。
        /// </summary>
        internal void Move(int from, int to)
        {
            bool changed = false;
            lock (_lock)
            {
                if (from < 0 || from >= _items.Count) return;
                if (to < 0) to = 0;
                if (to >= _items.Count) to = _items.Count - 1;
                if (from == to) return;

                Shot moved = _items[from];
                _items.RemoveAt(from);
                _items.Insert(to, moved);
                changed = true;
            }

            if (changed) Publish(true);
        }

        /// <summary>
        /// 图片被外部编辑（「图片编辑」保存后覆盖原文件）时调用：
        /// 重新解码、换缩略图与哈希，但**保持原来的位置与顺序**。
        /// </summary>
        internal Shot Refresh(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { Log.Warn("[ScPad] 读取编辑后的图片失败：" + ex.Message); return null; }

            SKBitmap full = null;
            try { full = SKBitmap.Decode(bytes); }
            catch (Exception ex) { Log.Warn("[ScPad] 解码编辑后的图片失败：" + ex.Message); }

            if (full == null) return null;

            try
            {
                string hash = Hash(bytes, full.Width, full.Height);
                Shot replaced = null;

                lock (_lock)
                {
                    int index = -1;
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (!string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                        index = i;
                        break;
                    }
                    if (index < 0) return null;

                    Shot old = _items[index];
                    _hashes.Remove(old.Hash);
                    replaced = new Shot(old.Path, hash, MakeThumbnail(full), old.Created, old.Tilt, old.Owned);
                    _items[index] = replaced;
                    _hashes.Add(hash);
                }

                Publish(true);
                return replaced;
            }
            finally
            {
                try { full.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 首次安装（还没有任何截图）时，生成 4 张教学图并放进截图夹：
        /// 从 4 号先加、1 号最后加，于是列表从右往左就是 1→2→3→4，
        /// 隐含告诉用户"新截图是从右边进来的"。
        /// 生成一次后写 guideSeen，之后（包括用户把它们全删了）都不会再自动出现。
        /// </summary>
        internal int EnsureGuide()
        {
            if (_settings.GuideSeen) return 0;

            if (Snapshot.Length > 0)
            {
                // 已经有截图 → 这次不打扰，但**不写标记**：
                // 用户把截图全删光、下次启动时还能看到一次教学图。
                return 0;
            }

            int added = PutGuideImages();
            _settings.SetGuideSeen(true);
            return added;
        }

        /// <summary>设置里的「重新查看新手引导」：把 4 张教学图重新放进来。</summary>
        internal int ShowGuideAgain()
        {
            _settings.SetGuideSeen(true);
            return PutGuideImages();
        }

        private int PutGuideImages()
        {
            int added = 0;
            try
            {
                string folder = System.IO.Path.Combine(_root, "Guide");
                Directory.CreateDirectory(folder);

                // 4 先入、1 最后入 → SP-1 在最右（第 1 课）
                for (int i = 4; i >= 1; i--)
                {
                    string path = System.IO.Path.Combine(folder, "guide-" + i.ToString() + ".png");
                    try
                    {
                        if (!File.Exists(path) || new FileInfo(path).Length == 0)
                        {
                            byte[] png = GuideImages.Render(i);
                            if (png == null || png.Length == 0) continue;
                            File.WriteAllBytes(path, png);
                        }

                        if (ContainsPath(path)) continue;
                        if (AddExternalFile(path, false) != null) added++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[ScPad] 放入引导图 " + i.ToString() + " 失败：" + ex.Message);
                    }
                }

                if (added > 0)
                {
                    Log.Info("[ScPad] 已放入 " + added.ToString() + " 张新手引导图：" + folder);
                    Reminders.Post(_host, "截图夹带你看 4 张图",
                        "第 1 张教截图、第 2 张教拖图、第 3 张教编辑、第 4 张讲收集模式；看完点图片顶部的小夹子就能删掉。",
                        null, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 生成新手引导图失败", ex);
            }

            return added;
        }

        // ---- 移出 ----

        internal void Remove(Shot shot)
        {
            if (shot != null && shot.FileName != null &&
                shot.FileName.StartsWith("guide-", StringComparison.OrdinalIgnoreCase))
            {
                // 用户把教学图删了：记下来，以后（包括清空列表、重启）都不再自动放
                _settings.SetGuideSeen(true);
            }

            if (shot == null) return;

            bool removed = false;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (!ReferenceEquals(_items[i], shot)) continue;
                    _items.RemoveAt(i);
                    _hashes.Remove(shot.Hash);
                    removed = true;
                    break;
                }
            }
            if (!removed) return;

            if (shot.Owned) RecycleFile(shot.Path);
            Publish(true);
        }

        internal void Clear()
        {
            Shot[] items;
            lock (_lock)
            {
                items = _items.ToArray();
                _items.Clear();
                _hashes.Clear();
            }

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Owned) RecycleFile(items[i].Path);
            }
            Publish(true);
        }

        /// <summary>插件自己落盘的图片移进 Recycle 子目录（可手动找回），外部原图一律不动。</summary>
        private void RecycleFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                Directory.CreateDirectory(_recycle);

                string target = System.IO.Path.Combine(_recycle, System.IO.Path.GetFileName(path));
                if (File.Exists(target)) target = target + "." + Guid.NewGuid().ToString("N").Substring(0, 6);
                File.Move(path, target);
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 移动截图到回收目录失败：" + ex.Message);
            }
        }

        // ---- 内部工具 ----

        private int _lastVisibleCount = -1;

        private void Publish(bool save)
        {
            Shot[] next;
            lock (_lock) next = _items.ToArray();

            Volatile.Write(ref _snapshot, next);

            // 岛上最多叠 4 张：第 5 张之后宽度不变，就别每次都惊动宿主重排布局了
            int visible = next.Length < 4 ? next.Length : 4;
            if (visible != _lastVisibleCount)
            {
                _lastVisibleCount = visible;
                try { _host.InvalidateWidgetLayout(); }
                catch (Exception ex) { Log.Warn("[ScPad] 请求重新布局失败：" + ex.Message); }
            }

            if (save) Save(next);
        }

        private double NextTilt()
        {
            lock (_random) return _random.NextDouble() * 5.0 - 2.5;
        }

        /// <summary>缩略图：最长边压到 360×300 以内，卡片和岛上的小图共用这一份。</summary>
        private static SKBitmap MakeThumbnail(SKBitmap full)
        {
            const int MaxWidth = 360;
            const int MaxHeight = 300;

            int width = full.Width;
            int height = full.Height;
            if (width <= 0 || height <= 0) return null;

            double scale = Math.Min((double)MaxWidth / width, (double)MaxHeight / height);
            if (scale > 1.0) scale = 1.0;

            int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            try
            {
                var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                SKBitmap scaled = full.Resize(info, SKFilterQuality.High);
                if (scaled != null) return scaled;
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 生成缩略图失败：" + ex.Message);
            }

            try
            {
                var fallback = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(fallback))
                {
                    canvas.Clear(SKColors.Transparent);
                    using (var paint = new SKPaint())
                    {
                        paint.FilterQuality = SKFilterQuality.High;
                        canvas.DrawBitmap(full, new SKRect(0, 0, targetWidth, targetHeight), paint);
                    }
                }
                return fallback;
            }
            catch (Exception ex)
            {
                Log.Warn("[ScPad] 缩略图兜底失败：" + ex.Message);
                return null;
            }
        }

        private static string Hash(byte[] bytes, int width, int height)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
            {
                digest = sha.ComputeHash(bytes);
            }
            return width.ToString() + "x" + height.ToString() + ":" + Convert.ToBase64String(digest);
        }

        internal static bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension;
            try { extension = System.IO.Path.GetExtension(path).ToLowerInvariant(); }
            catch { return false; }

            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".bmp" ||
                   extension == ".gif" || extension == ".tif" || extension == ".tiff" || extension == ".webp";
        }

        /// <summary>
        /// 系统默认的截图保存位置（FOLDERID_Screenshots，即当前用户的「图片\屏幕截图」）。
        ///
        /// 三点约定：
        ///   1. 由 Windows 按**当前登录用户**解析，代码里不写死任何用户名或盘符；
        ///   2. 用户在资源管理器里把「屏幕截图」的位置改到别处（或 OneDrive 重定向）后，
        ///      这里返回的就是改过之后的路径；
        ///   3. 系统没注册这个已知文件夹时退回「图片\Screenshots」；再不行才退到插件自己的
        ///      数据目录，绝不往桌面之类的地方丢文件。
        /// </summary>
        internal static string SystemScreenshotsFolder()
        {
            try
            {
                var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
                IntPtr buffer;
                // 0x4000 = KF_FLAG_DONT_VERIFY：目录还没建出来也照样返回路径（之后由 EnsureSaveFolder 建）
                if (Shell32.SHGetKnownFolderPath(ref id, 0x4000, IntPtr.Zero, out buffer) == 0)
                {
                    try
                    {
                        string path = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buffer); }
                }
            }
            catch { }

            try
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (!string.IsNullOrEmpty(pictures)) return System.IO.Path.Combine(pictures, "Screenshots");
            }
            catch { }

            return System.IO.Path.Combine(RecycleFolder(), "Shots");
        }

        internal static string RecycleFolder()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = System.IO.Path.GetTempPath();
            return System.IO.Path.Combine(System.IO.Path.Combine(local, "NotchPeninsula"), "ScPad");
        }

        /// <summary>SHGetKnownFolderPath 的声明（和 Market 里其它插件一致的写法）。</summary>
        private static class Shell32
        {
            [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            internal static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
        }
    }
}
