using System;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace ScPad
{
    /// <summary>
    /// 截图夹面板（右键组件展开的详情页）—— Snapline 那条「夹在轨道上的截图」，
    /// 外加一页自己的设置（保存位置 / 两个自定义快捷键）。
    ///
    /// 交互（和 Snapline 原版对齐）：
    ///   * 单击卡片 = 复制这张图到剪贴板（随后 Ctrl+V 就能粘）；
    ///   * 按住卡片拖出 = 拖到别的应用里直接粘贴（走宿主 IPluginHost.StartFileDrag）；
    ///   * 卡片右上角的 × = 移出（自己收的进 Recycle 子目录，外部原文件不动）；
    ///   * 把图片拖进面板 = 存入截图夹；
    ///   * 右键面板 = 收起（宿主默认手势，另外也留了 × 关闭按钮）。
    ///
    /// 设置页：宿主没有给插件做设置面板的能力（RegisterSettingsPage 注册了也不会显示），
    /// 所以这里自己画一份 —— 选目录、录快捷键、切动作，全部走 PluginSettings 存进注册表。
    ///
    /// 两页共用同一套尺寸（Measure* 在展开时只算一次），这样切页不会让岛体跳动。
    /// AutoCollapseDelay 返回「永不自动收起」：拖出去粘贴时鼠标必然离开岛体。
    /// </summary>
    internal sealed class ShotClipPanel : IDetailPage
    {
        private const float Pad = 14f;
        private const float CardW = 148f;
        private const float CardH = 176f;
        private const float GapX = 12f;
        private const float RowPitch = 200f;
        private const float CardTopGap = 20f;
        private const float HeaderH = 44f;
        private const float FooterH = 28f;

        private const int TabClip = 0;
        private const int TabSettings = 1;

        private const int BtnCopy = 0;
        private const int BtnPause = 1;
        private const int BtnFolder = 2;
        private const int BtnPeek = 3;
        private const int BtnClose = 4;
        private const int BtnRemove = 100;
        private const int BtnTab0 = 200;
        private const int BtnTab1 = 201;
        private const int BtnChooseFolder = 210;
        private const int BtnOpenFolder = 211;
        private const int BtnClearList = 212;
        private const int BtnDefaultFolder = 213;
        private const int BtnRecordA = 220;
        private const int BtnRecordB = 221;
        private const int BtnClearA = 222;
        private const int BtnClearB = 223;
        private const int BtnActionA = 224;
        private const int BtnActionB = 225;

        private const float DragThreshold = 4f;

        private static readonly SKColor Accent = new SKColor(88, 214, 141);
        private static readonly SKColor Warning = new SKColor(232, 126, 100);

        private readonly ScPadPlugin _plugin;
        private readonly ShotStore _store;
        private readonly IPluginHost _host;

        // 布局（MeasureWidth / MeasureHeight 时算，Draw 与命中检测共用同一套数）
        private int _cols = 3;
        private int _rows = 1;
        private int _capacity = 3;
        private float _layoutW = 496f;
        private float _layoutH = 272f;

        // 最近一帧的实际矩形（弹簧动画期间是插值尺寸，用来把指针坐标换回排版坐标）
        private float _rectW = 496f;
        private float _rectH = 272f;

        private int _tab = TabClip;

        // 指针状态
        private bool _pressed;
        private int _pressCard = -1;
        private int _pressButton = -1;
        private float _pressX;
        private float _pressY;
        private float _mouseX = -1000f;
        private float _mouseY = -1000f;
        private bool _dragging;

        // 拖入状态
        private int _dragHover;

        internal ShotClipPanel(ScPadPlugin plugin, ShotStore store, IPluginHost host)
        {
            _plugin = plugin;
            _store = store;
            _host = host;
            RecomputeLayout();
        }

        // ---- 尺寸 ----

        private void RecomputeLayout()
        {
            int count = _store.Snapshot.Length;

            int cols = count > 6 ? 6 : 3;
            int rows = (count == 0 || count <= cols) ? 1 : 2;

            _cols = cols;
            _rows = rows;
            _capacity = cols * rows;
            _layoutW = Pad * 2f + cols * CardW + (cols - 1) * GapX;
            _layoutH = HeaderH + rows * RowPitch + FooterH;

            // 设置页也要放得下：从上往下摆大约需要 196px
            if (_layoutH < HeaderH + FooterH + 196f) _layoutH = HeaderH + FooterH + 196f;

            _rectW = _layoutW;
            _rectH = _layoutH;
        }

        public float MeasureWidth()
        {
            RecomputeLayout();
            return _layoutW;
        }

        public float MeasureHeight()
        {
            RecomputeLayout();
            return _layoutH;
        }

        public TimeSpan AutoCollapseDelay
        {
            get { return Timeout.InfiniteTimeSpan; }
        }

        public bool AcceptsRightClick { get { return false; } }

        public bool AcceptsDoubleClick { get { return false; } }

        // ---- 绘制 ----

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            if (canvas == null || frame.Alpha == 0) return;

            _rectW = rect.Width;
            _rectH = rect.Height;

            float scale = _layoutH > 1f ? rect.Height / _layoutH : 1f;
            if (scale > 1f) scale = 1f;
            if (scale < 0.05f) scale = 0.05f;

            float left = rect.Left + rect.Width * 0.5f - _layoutW * scale * 0.5f;
            float top = rect.Top + rect.Height * 0.5f - _layoutH * scale * 0.5f;

            canvas.Save();
            canvas.Translate(left, top);
            canvas.Scale(scale);
            try
            {
                DrawContent(canvas, frame);
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 面板绘制异常", ex);
            }
            finally
            {
                canvas.Restore();
            }
        }

        private void DrawContent(SKCanvas canvas, WidgetFrame frame)
        {
            Shot[] shots = _store.Snapshot;
            int slots = Math.Min(shots.Length, _capacity <= 0 ? 3 : _capacity);

            DrawHeader(canvas, frame, shots.Length);

            if (_tab == TabSettings)
            {
                DrawSettings(canvas, frame);
            }
            else
            {
                DrawRail(canvas, frame);

                if (slots == 0)
                {
                    DrawEmpty(canvas, frame);
                }
                else
                {
                    int hoverSlot = HitCard(_mouseX, _mouseY);
                    for (int slot = 0; slot < slots; slot++)
                    {
                        int index = shots.Length - 1 - slot;   // 最近的一张在最左
                        if (index < 0) break;

                        SKRect body = CardRect(slot);
                        DrawCard(canvas, body, shots[index], frame, slot == hoverSlot);
                    }
                }

                DrawFooter(canvas, frame, shots.Length);
            }

            DrawDropOverlay(canvas, frame);
        }

        // ---- 头部（两页共用：左边页签、右边按钮）----

        private void DrawHeader(SKCanvas canvas, WidgetFrame frame, int count)
        {
            string tab0 = count > 0 ? "截图夹 · " + count.ToString() : "截图夹";
            DrawTab(canvas, HeaderTabRect(0), tab0, _tab == TabClip, frame);
            DrawTab(canvas, HeaderTabRect(1), "设置", _tab == TabSettings, frame);

            if (_tab == TabClip)
            {
                DrawButton(canvas, HeaderButtonRect(BtnCopy), "复制最近一张", frame, true, count > 0);
                DrawButton(canvas, HeaderButtonRect(BtnPause), _store.Paused ? "恢复收集" : "暂停收集", frame, false, true);
            }

            DrawButton(canvas, HeaderButtonRect(BtnClose), "×", frame, false, true);
        }

        private void DrawTab(SKCanvas canvas, SKRect rect, string label, bool selected, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            bool hover = Contains(rect, _mouseX, _mouseY);

            if (selected)
            {
                Ui.FillRound(canvas, rect, 8f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 30)));
                Ui.StrokeRound(canvas, rect, 8f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 55)), 1f);
            }
            else if (hover)
            {
                Ui.FillRound(canvas, rect, 8f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 16)));
            }

            byte alpha = selected ? (byte)240 : (byte)(hover ? 190 : 140);
            Ui.Text(canvas, label, rect.MidX, Ui.Baseline(rect.MidY, Ui.BodySize), Ui.BodySize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, alpha)), selected, rect.Width - 10f, SKTextAlign.Center);
        }

        private void DrawRail(SKCanvas canvas, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            byte railAlpha = Ui.Alpha(frame.Alpha, 55);

            for (int row = 0; row < _rows; row++)
            {
                float y = HeaderH + row * RowPitch + 9f;
                Ui.Line(canvas, Pad - 2f, y, _layoutW - Pad + 2f, y, ink.WithAlpha(railAlpha), 1.4f);

                // 轨道两端的小圆点
                Ui.FillCircle(canvas, Pad - 2f, y, 2f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 80)));
                Ui.FillCircle(canvas, _layoutW - Pad + 2f, y, 2f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 80)));
            }
        }

        private void DrawEmpty(SKCanvas canvas, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            float cx = _layoutW * 0.5f;
            float cy = HeaderH + RowPitch * 0.5f;

            Ui.Text(canvas, "还没有截图", cx, cy - 4f, 13f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 190)),
                true, 0f, SKTextAlign.Center);

            string hint = _store.Paused
                ? "自动收集已暂停 · 按上面的「恢复收集」继续"
                : "按 Win + Shift + S 截图，或把图片直接拖到这里加入";
            Ui.Text(canvas, hint, cx, cy + 18f, Ui.SmallSize, ink.WithAlpha(Ui.Alpha(frame.Alpha, 120)),
                false, 0f, SKTextAlign.Center);
        }

        private void DrawFooter(SKCanvas canvas, WidgetFrame frame, int count)
        {
            SKColor ink = frame.Theme.TextColor;
            float baseline = _layoutH - 13f;

            if (_tab == TabSettings)
            {
                Ui.Text(canvas, "设置立即生效并记住（存在宿主的插件设置里）", Pad, baseline, Ui.SmallSize,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 110)), false, _layoutW - Pad * 2f, SKTextAlign.Left);
                return;
            }

            string hint = count == 0 ? "右键关闭面板" : "单击复制 · 按住拖到其他应用即可粘贴 · 右键关闭";
            Ui.Text(canvas, hint, Pad, baseline, Ui.SmallSize, ink.WithAlpha(Ui.Alpha(frame.Alpha, 110)),
                false, _layoutW - Pad * 2f - 200f, SKTextAlign.Left);

            DrawLink(canvas, FooterButtonRect(BtnFolder), "打开保存位置", frame);
            DrawLink(canvas, FooterButtonRect(BtnPeek), _store.AutoPeek ? "自动展开 · 开" : "自动展开 · 关", frame);
        }

        // ---- 设置页 ----

        private void DrawSettings(SKCanvas canvas, WidgetFrame frame)
        {
            SKColor ink = frame.Theme.TextColor;
            float y0 = SettingsTop;

            // ① 保存位置
            Ui.Text(canvas, "保存位置", Pad, y0 + 9f, Ui.BodySize, ink.WithAlpha(Ui.Alpha(frame.Alpha, 220)),
                true, 160f, SKTextAlign.Left);
            DrawLink(canvas, DefaultFolderRect(), "恢复默认", frame);

            SKRect pathRect = SavePathRect();
            Ui.FillRound(canvas, pathRect, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 18)));
            Ui.StrokeRound(canvas, pathRect, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 40)), 1f);

            string folder = _store.SaveFolder;
            string folderError = _store.SaveFolderError;
            SKColor pathColor = string.IsNullOrEmpty(folderError)
                ? ink.WithAlpha(Ui.Alpha(frame.Alpha, 225))
                : Warning.WithAlpha(Ui.Alpha(frame.Alpha, 235));

            Ui.TextTail(canvas, folder, pathRect.Left + 9f, Ui.Baseline(pathRect.MidY, Ui.BodySize),
                Ui.BodySize, pathColor, false, pathRect.Width - 18f);

            DrawButton(canvas, ChooseFolderRect(), "选择文件夹…", frame, false, true);
            DrawButton(canvas, OpenFolderRect(), "打开", frame, false, true);
            DrawButton(canvas, ClearListRect(), "清空列表", frame, false, true);

            if (!string.IsNullOrEmpty(folderError))
            {
                Ui.TextTail(canvas, "目录不可用：" + folderError, Pad, y0 + 84f, Ui.SmallSize,
                    Warning.WithAlpha(Ui.Alpha(frame.Alpha, 220)), false, _layoutW - Pad * 2f);
            }

            // ② 快捷键
            float hotkeyY = y0 + 100f;
            Ui.Text(canvas, "快捷键", Pad, hotkeyY + 9f, Ui.BodySize, ink.WithAlpha(Ui.Alpha(frame.Alpha, 220)),
                true, 120f, SKTextAlign.Left);

            HotkeyHost keys = _plugin.Hotkeys;
            int recording = keys == null ? -1 : keys.RecordingSlot;

            string hint;
            if (recording >= 0)
            {
                string message = keys.RecordingMessage;
                hint = string.IsNullOrEmpty(message) ? "按下组合键…" : message;
            }
            else
            {
                hint = "至少一个修饰键（或 F1~F24）· 默认 Ctrl + PrtSc";
            }

            SKColor hintColor = recording >= 0 ? Accent : ink.WithAlpha(Ui.Alpha(frame.Alpha, 110));
            Ui.Text(canvas, hint, Pad + 62f, hotkeyY + 9f, Ui.SmallSize, hintColor, false,
                _layoutW - Pad * 2f - 62f, SKTextAlign.Left);

            DrawHotkeyRow(canvas, frame, 0, recording == 0);
            DrawHotkeyRow(canvas, frame, 1, recording == 1);

            DrawSettingsHelp(canvas, frame);
        }

        /// <summary>面板够高时补一段动作说明（矮面板就省掉，免得挤）。</summary>
        private void DrawSettingsHelp(SKCanvas canvas, WidgetFrame frame)
        {
            if (!HelpVisible) return;

            float top = HotkeyRowTop(1) + 42f;
            float bottom = _layoutH - FooterH - 6f;
            if (bottom - top < 76f) return;

            SKColor ink = frame.Theme.TextColor;
            float baseline = top + 10f;

            Ui.Text(canvas, "快捷键动作", Pad, baseline, Ui.BodySize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 200)), true, 200f, SKTextAlign.Left);

            string[] lines = new string[]
            {
                "截图并存入 —— 抓一张整屏（岛体先自动藏一下），存入截图夹并放进剪贴板",
                "展开 / 收起 —— 打开或收起这个面板，和「右键组件」等价",
                "复制最近一张 —— 把最新那张放进剪贴板，直接 Ctrl+V 就能粘"
            };

            for (int i = 0; i < lines.Length; i++)
            {
                float y = baseline + 22f + i * 19f;
                if (y > bottom) break;

                Ui.FillCircle(canvas, Pad + 3f, y - 3.5f, 2f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 90)));
                Ui.Text(canvas, lines[i], Pad + 13f, y, Ui.SmallSize,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 125)), false, _layoutW - Pad * 2f - 13f, SKTextAlign.Left);
            }
        }

        private void DrawHotkeyRow(SKCanvas canvas, WidgetFrame frame, int slot, bool recording)
        {
            SKColor ink = frame.Theme.TextColor;
            PluginSettings settings = _store.Settings;

            HotkeySpec spec = settings.Hotkey(slot);
            int action = settings.HotkeyAction(slot);

            HotkeyHost keys = _plugin.Hotkeys;
            bool failed = spec.IsSet && keys != null && !keys.IsRegistered(slot);

            SKRect combo = HotkeyComboRect(slot);
            Ui.FillRound(canvas, combo, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 18)));

            if (recording) Ui.StrokeRound(canvas, combo, 7f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 220)), 1.5f);
            else if (failed) Ui.StrokeRound(canvas, combo, 7f, Warning.WithAlpha(Ui.Alpha(frame.Alpha, 200)), 1.5f);
            else Ui.StrokeRound(canvas, combo, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 40)), 1f);

            string text = recording ? "按下组合键…" : spec.Text;
            SKColor textColor = recording
                ? Accent.WithAlpha(Ui.Alpha(frame.Alpha, 240))
                : (failed ? Warning.WithAlpha(Ui.Alpha(frame.Alpha, 230)) : ink.WithAlpha(Ui.Alpha(frame.Alpha, 230)));

            Ui.Text(canvas, text, combo.Left + 9f, Ui.Baseline(combo.MidY, Ui.BodySize), Ui.BodySize,
                textColor, false, combo.Width - 18f, SKTextAlign.Left);

            if (failed)
            {
                string error = keys.RegistrationError(slot);
                if (string.IsNullOrEmpty(error)) error = "注册失败";
                Ui.Text(canvas, error, combo.Right - 9f, Ui.Baseline(combo.MidY, Ui.SmallSize), Ui.SmallSize,
                    Warning.WithAlpha(Ui.Alpha(frame.Alpha, 210)), false, combo.Width * 0.62f, SKTextAlign.Right);
            }

            DrawButton(canvas, HotkeyRecordRect(slot), recording ? "取消" : "录制", frame, recording, true);
            DrawButton(canvas, HotkeyClearRect(slot), "清除", frame, false, spec.IsSet);
            DrawButton(canvas, HotkeyActionRect(slot), PluginSettings.ActionText(action), frame, false, true);
        }

        private void DrawDropOverlay(SKCanvas canvas, WidgetFrame frame)
        {
            if (_dragHover <= 0) return;

            var box = new SKRect(3f, 3f, _layoutW - 3f, _layoutH - 3f);
            Ui.FillRound(canvas, box, 13f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 26)));
            Ui.StrokeRound(canvas, box, 13f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 150)), 1.6f);

            // 提示做成实心小胶囊：落在卡片上也能看清
            string text = "松手存入 " + _dragHover.ToString() + " 张图片";
            float textWidth = Ui.Measure(text, 13f, true) + 30f;
            var pill = new SKRect(_layoutW * 0.5f - textWidth * 0.5f, _layoutH * 0.5f - 17f,
                _layoutW * 0.5f + textWidth * 0.5f, _layoutH * 0.5f + 17f);
            Ui.FillRound(canvas, pill, 10f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, 240)));
            Ui.Text(canvas, text, pill.MidX, Ui.Baseline(pill.MidY, 13f), 13f,
                SKColors.White.WithAlpha(Ui.Alpha(frame.Alpha, 255)), true, 0f, SKTextAlign.Center);
        }

        private void DrawButton(SKCanvas canvas, SKRect rect, string label, WidgetFrame frame, bool primary, bool enabled)
        {
            if (rect.Width <= 0f) return;

            SKColor ink = frame.Theme.TextColor;
            bool hover = enabled && Contains(rect, _mouseX, _mouseY);

            int fill = primary ? 32 : 18;
            if (hover) fill += 14;
            if (!enabled) fill = 8;

            Ui.FillRound(canvas, rect, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, fill)));
            if (primary) Ui.StrokeRound(canvas, rect, 7f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 55)), 1f);

            byte textAlpha = (byte)(enabled ? (hover ? 245 : 215) : 95);
            Ui.Text(canvas, label, rect.MidX, Ui.Baseline(rect.MidY, Ui.SmallSize), Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, textAlpha)), false, rect.Width - 8f, SKTextAlign.Center);
        }

        private void DrawLink(SKCanvas canvas, SKRect rect, string label, WidgetFrame frame)
        {
            if (rect.Width <= 0f) return;

            SKColor ink = frame.Theme.TextColor;
            bool hover = Contains(rect, _mouseX, _mouseY);
            if (hover) Ui.FillRound(canvas, rect, 6f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 26)));

            Ui.Text(canvas, label, rect.MidX, Ui.Baseline(rect.MidY, Ui.SmallSize), Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, (byte)(hover ? 220 : 140))), false, rect.Width - 6f, SKTextAlign.Center);
        }

        private void DrawCard(SKCanvas canvas, SKRect body, Shot shot, WidgetFrame frame, bool hover)
        {
            SKColor ink = frame.Theme.TextColor;
            double tilt = shot.Tilt + Wobble(shot);

            canvas.Save();
            canvas.RotateDegrees((float)tilt, body.MidX, body.Top);

            // 夹子
            var pin = new SKRect(body.MidX - 4.5f, body.Top - 17f, body.MidX + 4.5f, body.Top + 6f);
            Ui.FillRound(canvas, pin, 3f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 130 : 95)));

            // 卡面（悬停时抬一点亮度，不位移，免得命中检测对不上）
            Ui.FillRound(canvas, body, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 30 : 18)));
            Ui.StrokeRound(canvas, body, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, hover ? 95 : 55)), 1f);

            // 图片区
            var imageArea = new SKRect(body.Left + 7f, body.Top + 7f, body.Right - 7f, body.Bottom - 33f);
            Ui.FillRound(canvas, imageArea, 5f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 14)));
            if (shot.Thumb != null)
            {
                SKRect destination = Ui.FitRect(imageArea, shot.Thumb.Width, shot.Thumb.Height);
                Ui.Bitmap(canvas, shot.Thumb, destination, frame.Alpha);
            }

            // 名称 + 时间
            float metaBaseline = body.Bottom - 12f;
            Ui.Text(canvas, shot.FileName, body.Left + 7f, metaBaseline, Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 210)), false, body.Width - 52f, SKTextAlign.Left);
            Ui.Text(canvas, shot.TimeText, body.Right - 7f, metaBaseline, Ui.SmallSize,
                ink.WithAlpha(Ui.Alpha(frame.Alpha, 120)), false, 40f, SKTextAlign.Right);

            // 悬停出现「移出」
            if (hover && !_dragging)
            {
                float cx = body.Right - 13f;
                float cy = body.Top + 13f;
                Ui.FillCircle(canvas, cx, cy, 9f, ink.WithAlpha(Ui.Alpha(frame.Alpha, 70)));
                Ui.Line(canvas, cx - 3.2f, cy - 3.2f, cx + 3.2f, cy + 3.2f,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 235)), 1.5f);
                Ui.Line(canvas, cx + 3.2f, cy - 3.2f, cx - 3.2f, cy + 3.2f,
                    ink.WithAlpha(Ui.Alpha(frame.Alpha, 235)), 1.5f);
            }

            // 「已复制」小徽标（打勾是画出来的，不依赖字体里有没有 ✓）
            double age = CopiedAge(shot);
            if (age >= 0.0 && age < 1.3)
            {
                byte fade = (byte)(255 * (1.0 - age / 1.3));
                var badge = new SKRect(body.MidX - 33f, body.Bottom - 58f, body.MidX + 33f, body.Bottom - 36f);
                Ui.FillRound(canvas, badge, 6f, Accent.WithAlpha(Ui.Alpha(frame.Alpha, fade)));

                SKColor tick = SKColors.White.WithAlpha(Ui.Alpha(frame.Alpha, fade));
                float tx = badge.Left + 13f;
                float ty = badge.MidY;
                Ui.Line(canvas, tx, ty, tx + 3.2f, ty + 3.4f, tick, 1.7f);
                Ui.Line(canvas, tx + 3.2f, ty + 3.4f, tx + 9.2f, ty - 3.8f, tick, 1.7f);

                Ui.Text(canvas, "已复制", badge.Left + 27f, Ui.Baseline(badge.MidY, Ui.SmallSize), Ui.SmallSize,
                    tick, false, badge.Width - 30f, SKTextAlign.Left);
            }

            canvas.Restore();
        }

        // ---- 布局几何 ----

        private SKRect HeaderTabRect(int tab)
        {
            if (tab == TabSettings) return new SKRect(Pad + 84f, 8f, Pad + 84f + 54f, 34f);
            return new SKRect(Pad, 8f, Pad + 80f, 34f);
        }

        private SKRect HeaderButtonRect(int id)
        {
            float right = _layoutW - Pad;
            float top = 8f;
            float height = 26f;

            switch (id)
            {
                case BtnClose:
                    return new SKRect(right - 26f, top, right, top + height);

                case BtnPause:
                    if (_tab != TabClip) return SKRect.Empty;
                    return new SKRect(right - 34f - 60f, top, right - 34f, top + height);

                case BtnCopy:
                    if (_tab != TabClip) return SKRect.Empty;
                    return new SKRect(right - 34f - 60f - 8f - 96f, top, right - 34f - 60f - 8f, top + height);
            }
            return SKRect.Empty;
        }

        private SKRect FooterButtonRect(int id)
        {
            if (_tab != TabClip) return SKRect.Empty;

            float right = _layoutW - Pad;
            float top = _layoutH - 24f;
            float height = 20f;

            switch (id)
            {
                case BtnFolder:
                    return new SKRect(right - 92f, top, right, top + height);
                case BtnPeek:
                    return new SKRect(right - 92f - 6f - 90f, top, right - 92f - 6f, top + height);
            }
            return SKRect.Empty;
        }

        /// <summary>面板够高时才补「快捷键动作」说明。</summary>
        private bool HelpVisible
        {
            get { return _layoutH - HeaderH - FooterH >= 272f; }
        }

        /// <summary>设置页内容整体垂直居中：矮面板贴顶，高面板不至于下面空一大片。</summary>
        private float SettingsTop
        {
            get
            {
                float available = _layoutH - HeaderH - FooterH;
                float contentHeight = HelpVisible ? 270f : 196f;
                float offset = (available - contentHeight) * 0.5f;
                if (offset < 0f) offset = 0f;
                return HeaderH + 8f + offset;
            }
        }

        private SKRect DefaultFolderRect()
        {
            float right = _layoutW - Pad;
            return new SKRect(right - 62f, SettingsTop - 1f, right, SettingsTop + 17f);
        }

        private SKRect SavePathRect()
        {
            return new SKRect(Pad, SettingsTop + 16f, _layoutW - Pad, SettingsTop + 42f);
        }

        private SKRect ChooseFolderRect()
        {
            return new SKRect(Pad, SettingsTop + 48f, Pad + 104f, SettingsTop + 70f);
        }

        private SKRect OpenFolderRect()
        {
            return new SKRect(Pad + 110f, SettingsTop + 48f, Pad + 164f, SettingsTop + 70f);
        }

        private SKRect ClearListRect()
        {
            return new SKRect(Pad + 170f, SettingsTop + 48f, Pad + 244f, SettingsTop + 70f);
        }

        private float HotkeyRowTop(int slot)
        {
            return SettingsTop + 116f + (slot == 1 ? 32f : 0f);
        }

        private SKRect HotkeyComboRect(int slot)
        {
            float top = HotkeyRowTop(slot);
            float width = _layoutW - Pad * 2f - 46f - 44f - 96f - 18f;
            if (width < 120f) width = 120f;
            return new SKRect(Pad, top, Pad + width, top + 26f);
        }

        private SKRect HotkeyRecordRect(int slot)
        {
            float top = HotkeyRowTop(slot);
            float left = HotkeyComboRect(slot).Right + 6f;
            return new SKRect(left, top, left + 46f, top + 26f);
        }

        private SKRect HotkeyClearRect(int slot)
        {
            float top = HotkeyRowTop(slot);
            float left = HotkeyRecordRect(slot).Right + 6f;
            return new SKRect(left, top, left + 44f, top + 26f);
        }

        private SKRect HotkeyActionRect(int slot)
        {
            float top = HotkeyRowTop(slot);
            float right = _layoutW - Pad;
            return new SKRect(right - 96f, top, right, top + 26f);
        }

        private SKRect CardRect(int slot)
        {
            int cols = _cols <= 0 ? 3 : _cols;
            int row = slot / cols;
            int col = slot % cols;

            float x = Pad + col * (CardW + GapX);
            float y = HeaderH + row * RowPitch + CardTopGap;
            return new SKRect(x, y, x + CardW, y + CardH);
        }

        private int HitCard(float lx, float ly)
        {
            if (_tab != TabClip) return -1;

            Shot[] shots = _store.Snapshot;
            int slots = Math.Min(shots.Length, _capacity <= 0 ? 3 : _capacity);

            for (int slot = 0; slot < slots; slot++)
            {
                SKRect body = CardRect(slot);
                if (Contains(body, lx, ly)) return slot;
            }
            return -1;
        }

        private bool HitRemove(int slot, float lx, float ly)
        {
            SKRect body = CardRect(slot);
            float dx = lx - (body.Right - 13f);
            float dy = ly - (body.Top + 13f);
            return dx * dx + dy * dy <= 11f * 11f;
        }

        private int HitButton(float lx, float ly)
        {
            if (Contains(HeaderTabRect(0), lx, ly)) return BtnTab0;
            if (Contains(HeaderTabRect(1), lx, ly)) return BtnTab1;
            if (Contains(HeaderButtonRect(BtnClose), lx, ly)) return BtnClose;

            if (_tab == TabClip)
            {
                if (Contains(HeaderButtonRect(BtnCopy), lx, ly)) return BtnCopy;
                if (Contains(HeaderButtonRect(BtnPause), lx, ly)) return BtnPause;
                if (Contains(FooterButtonRect(BtnFolder), lx, ly)) return BtnFolder;
                if (Contains(FooterButtonRect(BtnPeek), lx, ly)) return BtnPeek;
                return -1;
            }

            if (Contains(DefaultFolderRect(), lx, ly)) return BtnDefaultFolder;
            if (Contains(ChooseFolderRect(), lx, ly)) return BtnChooseFolder;
            if (Contains(OpenFolderRect(), lx, ly)) return BtnOpenFolder;
            if (Contains(ClearListRect(), lx, ly)) return BtnClearList;

            for (int slot = 0; slot < 2; slot++)
            {
                if (Contains(HotkeyRecordRect(slot), lx, ly)) return BtnRecordA + slot;
                if (Contains(HotkeyClearRect(slot), lx, ly)) return BtnClearA + slot;
                if (Contains(HotkeyActionRect(slot), lx, ly)) return BtnActionA + slot;
            }

            return -1;
        }

        private static bool Contains(SKRect rect, float x, float y)
        {
            return rect.Width > 0f && x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
        }

        private void ToLayout(float x, float y, out float lx, out float ly)
        {
            float scale = _layoutH > 1f && _rectH > 0f ? _rectH / _layoutH : 1f;
            if (scale > 1f) scale = 1f;
            if (scale < 0.05f) scale = 0.05f;

            float ox = _rectW * 0.5f - _layoutW * scale * 0.5f;
            float oy = _rectH * 0.5f - _layoutH * scale * 0.5f;

            lx = (x - ox) / scale;
            ly = (y - oy) / scale;
        }

        // ---- 指针 ----

        public void OnMouseDown(float x, float y)
        {
            if (_plugin != null) _plugin.CancelPeek();

            float lx, ly;
            ToLayout(x, y, out lx, out ly);
            _mouseX = lx;
            _mouseY = ly;

            _pressed = true;
            _pressX = lx;
            _pressY = ly;
            _pressCard = -1;
            _pressButton = -1;

            int button = HitButton(lx, ly);

            // 正在录快捷键时，点别处就取消录制（否则它还在偷偷吃掉按键）
            HotkeyHost keys = _plugin.Hotkeys;
            if (keys != null && keys.RecordingSlot >= 0)
            {
                if (button != BtnRecordA + keys.RecordingSlot) keys.CancelRecording();
            }

            if (button >= 0)
            {
                _pressButton = button;
                return;
            }

            int slot = HitCard(lx, ly);
            if (slot < 0) return;

            if (HitRemove(slot, lx, ly))
            {
                _pressButton = BtnRemove + slot;
                return;
            }

            _pressCard = slot;
        }

        public void OnMouseMove(float x, float y)
        {
            float lx, ly;
            ToLayout(x, y, out lx, out ly);
            _mouseX = lx;
            _mouseY = ly;

            if (!_pressed || _dragging || _pressCard < 0) return;

            float dx = lx - _pressX;
            float dy = ly - _pressY;
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

            StartDrag(_pressCard);
        }

        public void OnMouseUp(float x, float y)
        {
            float lx, ly;
            ToLayout(x, y, out lx, out ly);
            _mouseX = lx;
            _mouseY = ly;

            if (!_pressed) return;

            _pressed = false;
            int button = _pressButton;
            int slot = _pressCard;
            _pressButton = -1;
            _pressCard = -1;

            if (button >= BtnRemove && button < BtnRemove + 100)
            {
                RemoveSlot(button - BtnRemove);
                return;
            }

            if (button >= 0)
            {
                // 按下与抬起不在同一个按钮上 → 当作放弃
                if (HitButton(lx, ly) == button) RunButton(button);
                return;
            }

            if (slot >= 0) CopySlot(slot);
        }

        public void OnMouseLeave()
        {
            _pressed = false;
            _pressCard = -1;
            _pressButton = -1;
            _dragging = false;
            _mouseX = -1000f;
            _mouseY = -1000f;
            _dragHover = 0;
        }

        private void StartDrag(int slot)
        {
            Shot[] shots = _store.Snapshot;
            int slots = Math.Min(shots.Length, _capacity <= 0 ? 3 : _capacity);
            if (slot < 0 || slot >= slots) return;

            Shot shot = shots[shots.Length - 1 - slot];

            _dragging = true;
            _pressed = false;
            _pressCard = -1;

            try
            {
                shot.Sway();
                if (_host != null)
                {
                    // 拖出就是「拖动粘贴」：把这张 PNG 交给别的程序
                    _host.StartFileDrag(new string[] { shot.Path }, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ScPad] 拖出截图失败", ex);
            }
            finally
            {
                _dragging = false;
            }
        }

        private void CopySlot(int slot)
        {
            Shot shot = ShotAt(slot);
            if (shot == null) return;

            shot.Sway();
            _plugin.Capture.RequestCopy(shot);
        }

        private void RemoveSlot(int slot)
        {
            Shot shot = ShotAt(slot);
            if (shot == null) return;

            _plugin.Capture.RequestRemove(shot);
        }

        private Shot ShotAt(int slot)
        {
            Shot[] shots = _store.Snapshot;
            int slots = Math.Min(shots.Length, _capacity <= 0 ? 3 : _capacity);
            if (slot < 0 || slot >= slots) return null;
            return shots[shots.Length - 1 - slot];
        }

        private void RunButton(int button)
        {
            HotkeyHost keys = _plugin.Hotkeys;

            switch (button)
            {
                case BtnCopy:
                    _plugin.Capture.RequestCopyLatest();
                    break;

                case BtnPause:
                    _store.SetPaused(!_store.Paused);
                    break;

                case BtnFolder:
                    _plugin.Capture.RequestOpenFolder();
                    break;

                case BtnPeek:
                    _store.SetAutoPeek(!_store.AutoPeek);
                    break;

                case BtnClose:
                    if (_host != null) _host.CloseDetailPage();
                    break;

                case BtnTab0:
                    _tab = TabClip;
                    break;

                case BtnTab1:
                    _tab = TabSettings;
                    break;

                case BtnChooseFolder:
                    _plugin.Capture.RequestChooseFolder();
                    break;

                case BtnOpenFolder:
                    _plugin.Capture.RequestOpenFolder();
                    break;

                case BtnClearList:
                    _plugin.Capture.RequestClear();
                    break;

                case BtnDefaultFolder:
                    _store.Settings.ResetSaveFolder();
                    _store.EnsureSaveFolder();
                    break;

                case BtnRecordA:
                case BtnRecordB:
                    if (keys == null) break;
                    if (keys.RecordingSlot == button - BtnRecordA) keys.CancelRecording();
                    else keys.StartRecording(button - BtnRecordA);
                    break;

                case BtnClearA:
                case BtnClearB:
                {
                    int slot = button - BtnClearA;
                    _store.Settings.SetHotkey(slot, HotkeySpec.None);
                    if (keys != null) keys.Refresh();
                    break;
                }

                case BtnActionA:
                case BtnActionB:
                {
                    int slot = button - BtnActionA;
                    _store.Settings.CycleHotkeyAction(slot);
                    if (keys != null) keys.Refresh();
                    break;
                }
            }
        }

        // ---- 命中（老接口，故意让位给鼠标事件那一套）----

        public WidgetHit HitTest(float x, float y, SKRect rect)
        {
            return WidgetHit.None;
        }

        public void OnAction(string action, float x, float y)
        {
        }

        public void OnRightClick(float x, float y)
        {
        }

        public void OnLeftDoubleClick(float x, float y)
        {
        }

        public void OnRightDoubleClick(float x, float y)
        {
        }

        // ---- 文件拖入 ----

        public bool OnFilesDragEnter(int count)
        {
            _dragHover = count;
            return count > 0;
        }

        public void OnFilesDragOver(float x, float y)
        {
        }

        public void OnFilesDragLeave()
        {
            _dragHover = 0;
        }

        public void OnFilesDrop(string[] paths)
        {
            _dragHover = 0;
            if (paths == null || paths.Length == 0) return;
            _plugin.Capture.RequestImport(paths);
        }

        // ---- 小动画 ----

        /// <summary>刚复制 / 刚存入时卡片轻轻晃一下（Snapline 的「摇摆」）。</summary>
        private static double Wobble(Shot shot)
        {
            long ticks = shot.WobbleTicks;
            if (ticks == 0) return 0.0;

            double seconds = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
            if (seconds < 0.0 || seconds > 1.6) return 0.0;

            return Math.Sin(seconds * 9.0) * 2.4 * (1.0 - seconds / 1.6);
        }

        private static double CopiedAge(Shot shot)
        {
            long ticks = shot.CopiedTicks;
            if (ticks == 0) return -1.0;
            return (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
        }
    }
}
