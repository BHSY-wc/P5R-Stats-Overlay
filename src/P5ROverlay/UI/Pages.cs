using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>一套配色，改了颜色就整屏重建一份。</summary>
    public sealed class Theme
    {
        public Color Bg, Bg2, Text, Accent, Dim, Line;

        public static Theme Make(Color bg, Color text, Color accent)
        {
            return new Theme
            {
                Bg = bg,
                Bg2 = Blend(bg, Color.White, 0.05),
                Text = text,
                Accent = accent,
                Dim = Blend(text, bg, 0.30),
                Line = Blend(text, bg, 0.72),
            };
        }

        public static Color Blend(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
    }

    public abstract class PageBase : Panel
    {
        protected Theme T = Theme.Make(Color.FromArgb(7, 7, 9), Color.FromArgb(246, 246, 249), Color.FromArgb(226, 0, 44));

        // ---- 逐行动画（只有「这一次扫描和上一次相比变了」的行才播，播完停表，不常驻吃 CPU）
        //      键 = 行的稳定标识（五维用索引 0~4，Coop 用 Confidant ID），
        //      不在表里的行视为已播完（进度 1），所以页面切换、滚动都不会重播。
        private readonly Timer _anim = new Timer { Interval = 30 };
        private readonly Dictionary<int, DateTime> _rowT = new Dictionary<int, DateTime>();
        private const int AnimMs = 700;

        // ---- 增长动画（点数真的涨了才播）
        //      进度条从旧值扫到新值，多出来那一段用高亮色单独画，
        //      条上方同色飘出「+N」，扫完再淡出。只有变化的那一行播。
        private sealed class GrowFx
        {
            public float From;       // 旧进度 0~1
            public int Delta;        // 涨了多少点
            public DateTime T0;
            public float Freeze = -1f;   // >=0 时定格在这个进度（只给自检截图用）
        }

        private readonly Dictionary<int, GrowFx> _grow = new Dictionary<int, GrowFx>();
        private readonly Timer _growTick = new Timer { Interval = 15 };

        /// <summary>增长动画默认时长（毫秒）。设置页可改，改完下一帧就按新时长播。</summary>
        public const int DefGrowMs = 900;

        /// <summary>增长动画默认高亮色：P5R 的荧光黄，红底、灰底、白条上都看得清。</summary>
        public static readonly Color DefGrowTint = Color.FromArgb(255, 222, 76);

        /// <summary>增长动画时长（毫秒），设置页写、绘制时读，所有页面共用一份。</summary>
        public static int GrowMs = DefGrowMs;

        /// <summary>增长动画高亮色（新涨出来那一段 + 「+N」），设置页可改。</summary>
        public static Color GrowTint = DefGrowTint;

        /// <summary>从旧进度 fromRatio 起播一次增长动画；delta 是涨的点数（用来在条上方标数字）。</summary>
        protected void PlayGrow(int key, float fromRatio, int delta)
        {
            if (delta <= 0) return;
            _grow[key] = new GrowFx { From = P5R.Clamp01(fromRatio), Delta = delta, T0 = DateTime.Now };
            if (!_growTick.Enabled) _growTick.Start();
            Invalidate();
        }

        /// <summary>取消某一行的增长动画（掉点 / 换存档时用）。</summary>
        protected void CancelGrow(int key) => _grow.Remove(key);

        /// <summary>自检用：把某个 key 的增长动画定格在进度 p（0~1），方便逐帧截图。</summary>
        protected void FreezeGrow(int key, float p)
        {
            if (_grow.TryGetValue(key, out var fx)) { fx.Freeze = P5R.Clamp01(p); Invalidate(); }
        }

        /// <summary>自检用：解除定格，让动画照常播完。</summary>
        protected void ClearGrow()
        {
            foreach (var fx in _grow.Values) fx.Freeze = -1f;
        }

        /// <summary>这一行是否正在播增长动画。</summary>
        protected bool GrowBusy(int key) => _grow.ContainsKey(key);

        /// <summary>
        /// 取这一行该画的进度：正在播增长动画时返回「旧值→新值」的插值，
        /// 同时给出多出来那段的起点 from、点数 delta 和标签亮度 fade（末尾淡出）。
        /// 没在播就直接返回 to，delta = 0。
        /// </summary>
        protected float GrowRatio(int key, float to, out float from, out int delta, out float fade)
        {
            from = to; delta = 0; fade = 0f;
            if (!_grow.TryGetValue(key, out var fx)) return to;

            if (fx.Freeze >= 0f)
            {
                from = fx.From; delta = fx.Delta; fade = 1f;
                return fx.From + (to - fx.From) * P5R.EaseOut(fx.Freeze);
            }

            double ms = (DateTime.Now - fx.T0).TotalMilliseconds;
            if (ms >= GrowMs) { _grow.Remove(key); return to; }

            float p = P5R.Clamp01((float)(ms / GrowMs));
            from = fx.From;
            delta = fx.Delta;
            fade = p < 0.70f ? 1f : 1f - (p - 0.70f) / 0.30f;     // 最后 30% 淡出
            return fx.From + (to - fx.From) * P5R.EaseOut(p);
        }

        /// <summary>某一行的入场进度 0→1；没有正在播的动画就是 1。</summary>
        protected float RowIntro(int key)
        {
            if (!_rowT.TryGetValue(key, out DateTime t0)) return 1f;
            float ms = (float)(DateTime.Now - t0).TotalMilliseconds;
            return P5R.Clamp01(ms / AnimMs);
        }

        /// <summary>只给这些行播动画；已经在播的会重置进度（连着涨点也不会卡在中途）。</summary>
        protected void PlayRows(params int[] keys)
        {
            if (keys == null || keys.Length == 0) return;
            DateTime now = DateTime.Now;
            foreach (int k in keys) _rowT[k] = now;
            if (!_anim.Enabled) _anim.Start();
            Invalidate();
        }

        protected PageBase()
        {
            BackColor = T.Bg2;      // 实色底，避免分层窗口把游戏画面透上来
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BorderStyle = BorderStyle.None;
            _anim.Tick += (s, e) =>
            {
                DateTime now = DateTime.Now;
                var done = new List<int>();
                foreach (var kv in _rowT)
                    if ((now - kv.Value).TotalMilliseconds >= AnimMs) done.Add(kv.Key);
                foreach (int k in done) _rowT.Remove(k);
                if (_rowT.Count == 0) _anim.Stop();
                Invalidate();
            };

            // 增长动画：逐帧重绘，播完（或行被滚走后再没人画）就停表
            _growTick.Tick += (s, e) =>
            {
                DateTime now = DateTime.Now;
                var done = new List<int>();
                foreach (var kv in _grow)
                    if (kv.Value.Freeze < 0 && (now - kv.Value.T0).TotalMilliseconds >= GrowMs + 300) done.Add(kv.Key);
                foreach (int k in done) _grow.Remove(k);
                if (_grow.Count == 0) { _growTick.Stop(); return; }
                Invalidate();
            };
        }

        public virtual void SetTheme(Theme t)
        {
            T = t;
            BackColor = t.Bg2;
            Invalidate();
        }

        // ---- 攻略弹层（Coop 能力表 / 五维门槛），两个页面共用一套
        protected readonly DetailView Detail = new DetailView();

        /// <summary>这一点是否落在可点击的行上（子类实现，用来切手型光标）。</summary>
        protected virtual bool HitRow(Point p) => false;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Detail.IsOpen)
            {
                Detail.MouseDown(e.Location);   // 弹层自己决定是关还是吃掉
                Invalidate();
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!Detail.IsOpen)
                Cursor = HitRow(e.Location) ? Cursors.Hand : Cursors.Default;
            else
                Cursor = Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        /// <summary>子类 OnPaint 的最后一步：弹层永远盖在最上面。</summary>
        protected void PaintDetail(Graphics g)
        {
            if (Detail.IsOpen) Detail.Paint(g, ClientRectangle, T);
        }

        // ------------------------------------------------------------ 小键盘导航
        // 8/5 上下、4/6 左右（dx/dy 各给 ±1），7 确认，9 返回。
        // 返回 true 表示这一下被页面消费掉了（OverlayForm 不看返回值，但便于自检断言）。
        /// <summary>方向移动。dy: -1 上 / +1 下；dx: -1 左 / +1 右。</summary>
        public virtual bool NavMove(int dx, int dy) => false;

        /// <summary>确认键（小键盘 7）。</summary>
        public virtual bool NavConfirm() => false;

        /// <summary>返回 / 关闭键（小键盘 9）；没有可关的东西就返回 false，什么都不做。</summary>
        public virtual bool NavBack() => false;

        /// <summary>切到本页时重置光标（默认什么都不动）。</summary>
        public virtual void NavReset() { }

        /// <summary>小键盘 8/5：弹层开着就滚弹层内容，否则交给页面上下选。</summary>
        public bool NavVertical(int dy)
        {
            if (Detail.IsOpen) return Detail.ScrollBy(dy);
            return NavMove(0, dy);
        }

        /// <summary>小键盘 4/6：弹层开着就切「能力 / 礼物」页，否则交给页面横向移动。</summary>
        public bool NavSide(int dir)
        {
            if (Detail.IsOpen) return Detail.NextTab(dir);
            return NavMove(dir, 0);
        }

        /// <summary>画选中框；r 为稳定命中矩形（不要传带入场动画位移的矩形）。</summary>
        protected void DrawNavFocus(Graphics g, RectangleF r)
        {
            if (r.Width < 8 || r.Height < 8) return;
            P5R.FocusFrame(g, r, T.Accent);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _anim.Dispose(); _growTick.Dispose(); }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ 公共底
        /// <summary>整页底色：黑 + 右侧斜纹 + 半调网点，模拟 P5R 的墙面。</summary>
        protected void PaintBackdrop(Graphics g)
        {
            g.Clear(T.Bg);

            // 右侧两条出血红斜带
            var st = g.Save();
            g.SetClip(ClientRectangle);
            using (var p = new GraphicsPath())
            {
                p.AddPolygon(new[]
                {
                    new PointF(Width - 96, -20), new PointF(Width - 54, -20),
                    new PointF(Width - 154, Height + 20), new PointF(Width - 196, Height + 20),
                });
                using var br = new LinearGradientBrush(
                    new Rectangle(Width - 200, 0, 200, Math.Max(1, Height)),
                    Color.FromArgb(26, T.Accent), Color.FromArgb(0, T.Accent), 0f);
                g.FillPath(br, p);
            }
            g.Restore(st);

            // 底部网点（很淡，只作质感）
            P5R.Halftone(g, new RectangleF(0, Height - 90, Width, 90), Color.FromArgb(16, T.Text), 8, 1.0f);

            using (var pen = new Pen(Color.FromArgb(70, T.Accent), 2f))
                g.DrawLine(pen, 12, 1, Width - 12, 1);
            using (var pen = new Pen(Color.FromArgb(46, P5R.Wire)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>
        /// 顶部一行「音符换算」提示：斜切黑条 + 左侧红块。
        /// 返回占用的高度（调用方据此把正文往下挪）。
        /// </summary>
        protected int DrawNoteHint(Graphics g, string main, string suffix, float fs = 11.5f)
        {
            const int padX = 14;
            int h = (int)Math.Round(fs * 2.6f);        // 字号变高时横条一起长高，字不会被裁
            var bar = new RectangleF(padX + 6, 4, Math.Max(60, Width - padX * 2 - 12), h - 10);
            P5R.Slab(g, bar, Color.FromArgb(210, P5R.Ink), Color.FromArgb(50, P5R.Wire));
            P5R.Slab(g, new RectangleF(padX + 8, 6, 4, h - 14), T.Accent);

            using (var f = P5R.F(fs, FontStyle.Bold))
            using (var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            })
            using (var b = new SolidBrush(Theme.Blend(T.Text, T.Bg, 0.10)))
            {
                var box = new RectangleF(bar.X + 16, bar.Y, bar.Width - 24, bar.Height);
                g.DrawString(main, f, b, box, sf);
                if (!string.IsNullOrEmpty(suffix))
                {
                    var sz = g.MeasureString(main, f, 10000, sf);
                    float x = box.X + sz.Width + 18;
                    float w = box.Right - x;
                    if (w > 50)
                    {
                        using var sb = new SolidBrush(Theme.Blend(T.Text, T.Bg, 0.55));
                        g.DrawString(suffix, f, sb, new RectangleF(x, box.Y, w, box.Height), sf);
                    }
                }
            }
            return h;
        }

        /// <summary>
        /// P5R 式进度条：斜切轨道 + 填充 + 末端竖切。
        /// deltaFrom &gt;= 0 时，[deltaFrom, ratio] 这一段是本次涨出来的部分，
        /// 用 deltaFill 单独上色，并在分界处画一道竖线、推进端画一根游标。
        /// </summary>
        protected void DrawBar(Graphics g, RectangleF r, double ratio, Color fill, Color track,
                               double deltaFrom = -1, Color? deltaFill = null)
        {
            if (r.Width < 14 || r.Height < 5) return;
            ratio = Math.Max(0, Math.Min(1, ratio));

            using (var p = P5R.Skew(r, -5f))
            {
                P5R.Fill(g, p, track);
                P5R.Stroke(g, p, Color.FromArgb(90, P5R.Wire), 1f);
            }

            bool grow = deltaFill.HasValue && deltaFrom >= 0 && deltaFrom < ratio - 0.002;

            FillBarPart(g, r, 0, grow ? Math.Min(deltaFrom, ratio) : ratio, fill, true);

            if (grow)
            {
                FillBarPart(g, r, deltaFrom, ratio, deltaFill.Value, true);

                // 分界竖线：一眼看清「从哪开始是涨的」（细一条就够，粗了像把条切断）
                float x0 = r.X + (float)(r.Width * deltaFrom);
                using (var pen = new Pen(Color.FromArgb(120, P5R.Ink), 1f))
                    g.DrawLine(pen, x0, r.Y, x0, r.Bottom);

                // 推进端游标：跟着扫过去的亮色短竖条
                float x1 = r.X + (float)(r.Width * ratio);
                using (var pen = new Pen(deltaFill.Value, 2f))
                    g.DrawLine(pen, x1, r.Y - 3f, x1, r.Bottom + 3f);
            }
        }

        /// <summary>画进度条的一段（0~1 的区间），带顶端高光。</summary>
        private static void FillBarPart(Graphics g, RectangleF r, double from, double to, Color c, bool gloss)
        {
            if (to <= from) return;
            float x0 = r.X + (float)(r.Width * from);
            float w = (float)(r.Width * (to - from));
            if (w < 2f) return;

            var fr = new RectangleF(x0, r.Y, w, r.Height);
            using (var p = P5R.Skew(fr, -5f))
                P5R.Fill(g, p, c);
            if (!gloss) return;

            var hi = new RectangleF(fr.X + 1, fr.Y + 1, Math.Max(2f, fr.Width - 2), Math.Max(1f, fr.Height * 0.34f));
            using var hp = P5R.Skew(hi, -5f);
            P5R.Fill(g, hp, Color.FromArgb(64, Color.White));
        }

        /// <summary>
        /// 进度条上方的「+N」：斜切小牌 + 荧光黄字，headX 是推进端位置（牌尽量压在它头上）。
        /// fade 由 <see cref="GrowRatio"/> 给出，播完自然消失。maxRight 用来防止牌顶出右边界。
        /// </summary>
        protected RectangleF DrawGrowTag(Graphics g, float headX, float barTop, int delta, float fade, float maxRight)
        {
            var box = GrowTagBox(g, headX, barTop, delta, fade, maxRight, out var f, out var s);
            if (box.Width <= 0) return RectangleF.Empty;

            int a = (int)(255 * P5R.Clamp01(fade));
            using (var p = P5R.Skew(box, -5f))
            {
                P5R.Fill(g, p, Color.FromArgb((int)(210 * fade), P5R.Ink));
                P5R.Stroke(g, p, Color.FromArgb((int)(210 * fade), GrowTint), 1f);
            }
            P5R.Text(g, s, f, box, Color.FromArgb(a, GrowTint),
                     StringAlignment.Center, StringAlignment.Center, -0.12f, false);
            f.Dispose();
            return box;
        }

        /// <summary>
        /// 紧凑版「+N」：小深底牌 + 荧光黄字，压在进度条的推进端上。
        /// 好感行第二行只有一条窄缝，牌子放上面会压到角色名，所以直接骑在条头上。
        /// </summary>
        protected void DrawGrowText(Graphics g, float headX, float barTop, int delta, float fade,
                                    float minX, float maxRight)
        {
            if (delta <= 0 || fade <= 0.02f) return;

            using var f = P5R.F(12f, FontStyle.Bold);
            string s = "+" + delta;
            var sz = g.MeasureString(s, f);
            float h = 14f, w = Math.Max(24f, sz.Width + 8f);
            float x = Math.Max(minX, Math.Min(headX - w * 0.5f + 2f, Math.Max(minX, maxRight - w)));
            var box = new RectangleF(x, barTop - 3.5f, w, h);

            using (var p = P5R.Skew(box, -4f))
                P5R.Fill(g, p, Color.FromArgb((int)(215 * P5R.Clamp01(fade)), P5R.Ink));

            P5R.Text(g, s, f, box,
                     Color.FromArgb((int)(255 * P5R.Clamp01(fade)), GrowTint),
                     StringAlignment.Center, StringAlignment.Center, -0.10f, false);
        }

        /// <summary>只是算「+N」小牌的位置（不画），让调用方提前知道要给它让多少地方。</summary>
        protected RectangleF GrowTagBox(Graphics g, float headX, float barTop, int delta, float fade, float maxRight)
        {
            var box = GrowTagBox(g, headX, barTop, delta, fade, maxRight, out var f, out _);
            f?.Dispose();
            return box;
        }

        private RectangleF GrowTagBox(Graphics g, float headX, float barTop, int delta, float fade,
                                      float maxRight, out Font f, out string s)
        {
            f = null;
            s = "";
            if (delta <= 0 || fade <= 0.02f) return RectangleF.Empty;

            f = P5R.F(12f, FontStyle.Bold);
            s = "+" + delta;
            var sz = g.MeasureString(s, f);
            float h = 16f;
            float w = Math.Max(26f, sz.Width + 10f);
            float x = headX - w * 0.5f + 3f;
            x = Math.Max(2f, Math.Min(x, Math.Max(2f, maxRight - w)));
            float y = barTop - h - 2f;
            return new RectangleF(x, y, w, h);
        }
    }

    // ======================================================================= 五维页
    public sealed class StatPage : PageBase
    {
        private readonly short[] _stats = new short[5];
        private bool _hasPrev;
        private readonly RectangleF[] _rowHit = new RectangleF[5];   // 按内存下标存，供点击命中
        private int _navSel = -1;                                    // 小键盘光标（显示序 0~4）

        /// <summary>五维页字号整体放大倍数（原始尺寸 × 1.3 后取整，见 <see cref="FS"/>）。</summary>
        private const float FontScale = 1.3f;

        /// <summary>原始字号 → 放大后的整数字号。</summary>
        private static int FS(float px) => (int)Math.Round(px * FontScale);

        public StatPage()
        {
            SetStyle(ControlStyles.Selectable, true);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Detail.IsOpen) { Detail.Wheel(e.Delta); Invalidate(); return; }
            base.OnMouseWheel(e);
        }


        public void SetData(short[] s)
        {
            if (s == null) return;
            bool first = !_hasPrev;
            var changed = new List<int>();
            for (int i = 0; i < 5 && i < s.Length; i++)
            {
                short old = _stats[i];
                if (first)
                {
                    // 首次拿到数据：整页进场，不播增长
                    changed.Add(i);
                }
                else if (s[i] > old)
                {
                    // 涨了：只走增长动画，不再整行滑动，免得两种动画打架
                    int lvOld = GameTables.StatLevel(i, old);
                    int lvNew = GameTables.StatLevel(i, s[i]);
                    // 升级的话新一级的进度条本来就从空开始，也就从 0 扫起
                    PlayGrow(i, lvNew > lvOld ? 0f : (float)StatRatio(i, old), s[i] - old);
                }
                else if (s[i] != old)
                {
                    CancelGrow(i);                      // 掉点（读档/换存档）：别让旧动画把条往回扫
                    changed.Add(i);                     // 仍然走原来的入场动画
                }
                _stats[i] = s[i];
            }
            _hasPrev = true;
            PlayRows(changed.ToArray());   // 空数组内部直接返回，不会白启定时器
            Invalidate();
        }

        /// <summary>某一项在任意点数下的进度（0~1），用来算「涨之前」的起点。</summary>
        private static double StatRatio(int i, int pts)
        {
            int lv = GameTables.StatLevel(i, pts);
            if (lv >= 5) return 1.0;
            var th = GameTables.StatTh[i];
            int lo = th[lv - 1], hi = th[lv];
            return Math.Max(0, Math.Min(1, (pts - lo) / (double)Math.Max(1, hi - lo)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintBackdrop(g);

            int hintH = DrawNoteHint(g, "♪ = +2 点　♪♪ = +3~4 点　♪♪♪ = +5/7/10 点", "占卜 ×1.5", FS(11.5f));

            int usable = Math.Max(200, Height - hintH - 8);
            int rowH = Math.Min(104, Math.Max(58, usable / 5));
            int totalH = rowH * 5;
            int startY = hintH + Math.Max(2, (usable - totalH) / 2);

            int padX = 12;
            int fullW = Math.Max(120, Width - padX * 2);

            // 按游戏菜单顺序排列（知识/勇气/灵巧/体贴/魅力），数据仍按内存下标取
            var order = GameTables.StatDisplayOrder;
            for (int d = 0; d < 5; d++)
            {
                int i = (order != null && d < order.Length) ? order[d] : d;
                DrawStatRow(g, i, padX, startY + d * rowH, fullW, rowH - 6);
            }

            // 小键盘光标单独一轮画，免得被后面的行盖住
            if (!Detail.IsOpen && _navSel >= 0 && _navSel < 5)
            {
                int i = MemIdx(_navSel);
                DrawNavFocus(g, _rowHit[i]);
            }

            PaintDetail(g);
        }

        // ------------------------------------------------------------ 小键盘导航
        private static int MemIdx(int d)
        {
            var order = GameTables.StatDisplayOrder;
            return (order != null && d >= 0 && d < order.Length) ? order[d] : d;
        }

        private int DispIdx(int mem)
        {
            var order = GameTables.StatDisplayOrder;
            if (order == null) return mem;
            for (int d = 0; d < order.Length; d++) if (order[d] == mem) return d;
            return mem;
        }

        public override bool NavMove(int dx, int dy)
        {
            if (Detail.IsOpen || dy == 0) return false;
            if (_navSel < 0) _navSel = dy > 0 ? 0 : 4;
            else _navSel = Math.Max(0, Math.Min(4, _navSel + dy));
            Invalidate();
            return true;
        }

        public override bool NavConfirm()
        {
            if (Detail.IsOpen || _navSel < 0) return false;
            int i = MemIdx(_navSel);
            Detail.OpenStat(i, GameTables.StatLevel(i, _stats[i]), _stats[i]);
            Invalidate();
            return true;
        }

        public override bool NavBack()
        {
            if (!Detail.IsOpen) return false;
            Detail.Close();
            Invalidate();
            return true;
        }

        protected override bool HitRow(Point p)
        {
            for (int i = 0; i < 5; i++)
                if (_rowHit[i].Width > 0 && _rowHit[i].Contains(p)) return true;
            return false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Detail.IsOpen) { base.OnMouseDown(e); Invalidate(); return; }

            for (int i = 0; i < 5; i++)
            {
                if (_rowHit[i].Width <= 0 || !_rowHit[i].Contains(e.Location)) continue;
                int pts = _stats[i];
                _navSel = DispIdx(i);
                Detail.OpenStat(i, GameTables.StatLevel(i, pts), pts);
                Focus();          // 拿到焦点，滚轮才会送到这个 Panel 而不是窗口
                Invalidate();
                return;
            }
            base.OnMouseDown(e);
        }

        /// <summary>自检钩子：把某一项的增长动画定格在进度 p。</summary>
        public void DebugFreezeGrow(int i, float p) => FreezeGrow(i, p);

        /// <summary>自检钩子：解除所有定格。</summary>
        public void DebugClearGrow() => ClearGrow();

        /// <summary>自检钩子：直接点开某一项的攻略层。</summary>
        public void DebugOpenStat(int i)
        {
            if (i < 0 || i > 4) return;
            int pts = _stats[i];
            Detail.OpenStat(i, GameTables.StatLevel(i, pts), pts);
            Invalidate();
            Update();
        }

        private void DrawStatRow(Graphics g, int i, int x, int y, int w, int h)
        {
            int pts = _stats[i];
            int lv = GameTables.StatLevel(i, pts);
            var th = GameTables.StatTh[i];
            bool max = lv >= 5;

            // 进度
            double ratio;
            if (max) ratio = 1.0;
            else
            {
                int lo = th[lv - 1], hi = th[lv];
                ratio = (pts - lo) / (double)Math.Max(1, hi - lo);
            }

            // 动画：入场（整行滑入）与增长（进度条从旧值扫到新值）互斥
            //   - 点数涨了：行不滑、点数直接给最终值，只让进度条扫过去 + 标出涨了多少
            //   - 首次拿到数据 / 掉点：走原来的入场动画
            bool growing = GrowBusy(i);
            float t = RowIntro(i);
            float ease = growing ? 1f : P5R.EaseOut(t);
            int slide = (int)Math.Round((1f - ease) * 26);
            int shownPts = (int)Math.Round(pts * ease);

            // 条的显示进度：入场时按 ease 缩放，增长时从旧值插值到新值
            float shownRatio = GrowRatio(i, (float)(ratio * ease), out float growFrom, out int growDelta, out float growFade);

            var block = new RectangleF(x + 6 + slide, y + 2, w - 12, h - 4);
            if (block.Width < 60 || block.Height < 24) { _rowHit[i] = RectangleF.Empty; return; }

            // 命中区用不含动画位移的稳定矩形，行正在滑也能点到
            _rowHit[i] = new RectangleF(x + 6, y + 2, w - 12, h - 4);

            // --- 行底板（满级 = 红块，普通 = 深灰块 + 细白边）
            using (var p = P5R.Skew(block))
            {
                if (max)
                {
                    using var lg = new LinearGradientBrush(
                        new RectangleF(block.X, block.Y, Math.Max(2f, block.Width), Math.Max(2f, block.Height)),
                        P5R.RedHot, P5R.Red, 0f);
                    g.FillPath(lg, p);
                }
                else
                {
                    using var lg = new LinearGradientBrush(
                        new RectangleF(block.X, block.Y, Math.Max(2f, block.Width), Math.Max(2f, block.Height)),
                        Color.FromArgb(232, P5R.Slate), Color.FromArgb(214, P5R.SlabLo), 0f);
                    g.FillPath(lg, p);
                }
                P5R.Stroke(g, p, max ? Color.FromArgb(120, Color.White) : Color.FromArgb(96, P5R.Wire), 1f);
            }

            // 左侧色条（P5R 的图标位）
            P5R.Slab(g, new RectangleF(block.X + 8, block.Y + 10, 4, Math.Min(24, block.Height - 20)),
                     max ? P5R.Paper : T.Accent);

            // --- 名称（字号已按 FontScale 放大）
            using (var f = P5R.F(Math.Min(FS(19f), Math.Min((block.Height * 0.34f + 6f) * FontScale,
                                                            block.Height * 0.40f)), FontStyle.Bold))
            {
                P5R.Text(g, GameTables.StatNames[i], f,
                    new RectangleF(block.X + 20, block.Y + 2, Math.Min(160, block.Width * 0.46f), block.Height * 0.56f),
                    max ? P5R.Ink : T.Text, StringAlignment.Near, StringAlignment.Center, -0.16f, !max);
            }

            // --- 点数：平行四边形数值框（右端，P5R 的斜切标签）
            float chipW = Math.Min(82f, Math.Max(56f, block.Width * 0.17f));
            var chip = new RectangleF(block.Right - chipW - 10, block.Y + 4, chipW, Math.Min(34f, block.Height * 0.46f));
            using (var f = P5R.F(Math.Min(FS(15f), chip.Height * 0.62f), FontStyle.Bold))
                P5R.Chip(g, chip, P5R.Paper, Color.FromArgb(96, P5R.Ink), shownPts.ToString(), P5R.Ink, f);

            // --- 等级徽章
            float badgeH = Math.Min(24f, block.Height * 0.27f);
            var badge = new RectangleF(block.X + 20, block.Bottom - badgeH - 6, 50, badgeH);
            P5R.Slab(g, badge, max ? P5R.Paper : T.Accent);
            using (var f = P5R.F(Math.Max(11f, badgeH * 0.62f), FontStyle.Bold))
                TextRenderer.DrawText(g, "Lv" + lv, f, Rectangle.Round(badge), max ? P5R.Ink : P5R.Paper,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // --- 进度条（右侧原本留给小字的空间收回来，整条拉长）
            float barX = badge.Right + 8;
            float barW = block.Right - barX - 10;
            var bar = RectangleF.Empty;
            if (barW > 24)
            {
                bar = new RectangleF(barX, badge.Y + badge.Height * 0.5f - 3.5f, barW, 7);
                DrawBar(g, bar, shownRatio, max ? P5R.Paper : T.Accent, Color.FromArgb(190, P5R.Ink),
                        growDelta > 0 ? growFrom : -1, GrowTint);
            }

            // --- 涨了多少：同色小牌压在推进端上方（牌的位置先算出来，好让「还差」让位）
            var growTag = RectangleF.Empty;
            if (bar.Width > 0 && growDelta > 0)
                growTag = DrawGrowTag(g, bar.X + bar.Width * shownRatio, bar.Y, growDelta, growFade, bar.Right);

            // --- 进度条上方：「还差 N  ▶  Lv下一级」
            //     整组不再贴进度条左端，而是向右让出一段（约等于箭头原来的位置），箭头与 Lv 跟着右移
            if (!max && bar.Width > 60 && th[lv] > pts)
            {
                using var fNum = P5R.F(FS(12f), FontStyle.Bold);
                using var fTxt = P5R.F(FS(10.5f), FontStyle.Bold);

                string sNum = (th[lv] - pts).ToString();
                var szN = g.MeasureString(sNum, fNum);
                var szT = g.MeasureString("▶ Lv" + (lv + 1), fTxt);

                float gap = 6f;
                float totalW = szN.Width + 2f + gap + szT.Width;
                // 目标起点：bar.X 右移约 11% 条宽（夹在 28~76px），但保证整组不出条尾
                float shift = Math.Min(110f, Math.Max(40f, bar.Width * 0.16f));
                shift = Math.Min(shift, Math.Max(0f, bar.Width - totalW - 6f));
                float numX = bar.X + shift;

                // 「+N」在播的时候这一行不画「还差」：两组数字挤在一起谁也读不清，
                // 而且「还差」本来就在跟着涨点变小，0.9 秒后再出现不影响读数
                if (growTag.Width > 0) return;

                var boxNum = new RectangleF(numX, bar.Y - 24f, szN.Width + 2f, 20f);
                P5R.Text(g, sNum, fNum, boxNum, max ? P5R.Ink : P5R.RedHot,
                         StringAlignment.Near, StringAlignment.Center, -0.14f, true);

                var boxRest = new RectangleF(boxNum.Right + gap, boxNum.Y,
                                             Math.Max(20f, bar.Right - boxNum.Right - gap), boxNum.Height);
                P5R.Text(g, "▶ Lv" + (lv + 1), fTxt, boxRest, Theme.Blend(T.Text, T.Bg, 0.22),
                         StringAlignment.Near, StringAlignment.Center, -0.14f, true);
            }
        }
    }

    // ======================================================================= 好感页
    public sealed class CoopRow
    {
        public bool IsHeader;
        public string HeaderText = "";
        public Color HeaderColor = Color.Gray;
        public int Id, Rank, Points;
        public string Name = "", Arcana = "", State = "";
        public int? Need;
    }

    public sealed class CoopPage : PageBase
    {
        private readonly List<CoopRow> _rows = new List<CoopRow>();
        private int _scroll;
        private int _navSel = -1;                 // 小键盘光标：_rows 的下标（跳过分组标题）

        private struct Hit { public RectangleF Rect; public int Id, Rank; public string Name, Arcana; }
        private readonly List<Hit> _hit = new List<Hit>();   // 每帧重建，只含当前可见的行

        private const int HeaderH = 26;
        private const int RowH = 52;
        private const int HintH = 30;

        public CoopPage()
        {
            SetStyle(ControlStyles.Selectable, true);
        }

        public void SetData(List<CoopRow> rows)
        {
            // 和上一帧逐一对比：只有 Rank / 好感点数变了的角色才播动画，新出现的角色也播。
            // 之前是每次 SetData 都整页重播，5s 一刷就整片跳动，视觉上很吵。
            var before = new Dictionary<int, (int Rank, int Points)>();
            foreach (var r in _rows)
                if (!r.IsHeader && r.Id > 0) before[r.Id] = (r.Rank, r.Points);

            var changed = new List<int>();
            var grow = new List<(int Id, float From, int Delta)>();
            if (rows != null)
            {
                foreach (var r in rows)
                {
                    if (r.IsHeader || r.Id <= 0) continue;
                    if (!before.TryGetValue(r.Id, out var old)) { changed.Add(r.Id); continue; }   // 新出现的角色
                    if (old.Rank == r.Rank && old.Points == r.Points) continue;                   // 没变

                    // 同一 Rank 内点数涨了 = 涨好感，播增长；升 Rank / 掉点走入场动画
                    if (old.Rank == r.Rank && r.Points > old.Points && r.Need.HasValue && r.Need.Value > 0)
                        grow.Add((r.Id, old.Points / (float)r.Need.Value, r.Points - old.Points));
                    else
                    {
                        CancelGrow(r.Id);
                        changed.Add(r.Id);
                    }
                }
            }

            _rows.Clear();
            if (rows != null) _rows.AddRange(rows);
            _scroll = Math.Min(_scroll, MaxScroll);
            if (_scroll < 0) _scroll = 0;

            PlayRows(changed.ToArray());
            foreach (var g in grow) PlayGrow(g.Id, g.From, g.Delta);
            Invalidate();
        }

        /// <summary>可滚动的最大偏移（内容 + 顶部提示条 - 可视高度）。</summary>
        private int MaxScroll => Math.Max(0, HintH + ContentHeight - Height);

        private int ContentHeight
        {
            get
            {
                int h = 8;
                foreach (var r in _rows) h += r.IsHeader ? HeaderH : RowH;
                return h + 10;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Detail.IsOpen) { Detail.Wheel(e.Delta); Invalidate(); return; }
            _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll - e.Delta / 3));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool HitRow(Point p)
        {
            foreach (var h in _hit) if (h.Rect.Contains(p)) return true;
            return false;
        }

        /// <summary>自检钩子：把某个角色的增长动画定格在进度 p。</summary>
        public void DebugFreezeGrow(int id, float p) => FreezeGrow(id, p);

        /// <summary>自检钩子：解除所有定格。</summary>
        public void DebugClearGrow() => ClearGrow();

        /// <summary>自检钩子：直接点开第 i 个可见角色的能力表。</summary>
        public void DebugOpenCoop(int i)
        {
            Invalidate(); Update();                      // 先画一遍，_hit 才有值
            if (_hit.Count == 0) return;
            var h = _hit[Math.Max(0, Math.Min(i, _hit.Count - 1))];
            Detail.OpenCoop(h.Id, h.Name, h.Arcana, h.Rank);
            Invalidate(); Update();
        }

        /// <summary>自检钩子：把已打开的弹层切到「能力 / 礼物」页。</summary>
        public void DebugTab(int t)
        {
            Detail.DebugTab(t);
            Invalidate(); Update();
        }

        /// <summary>自检钩子：按坐标模拟点页签，返回结果描述。</summary>
        public string DebugClickTab(string which)
        {
            string r = Detail.DebugClickTab(which);
            Invalidate(); Update();
            return r;
        }

        /// <summary>自检钩子：按 Coop ID 直接打开弹层的指定页（不依赖列表里有没有这一行）。</summary>
        public void DebugOpenCoopId(int cid, int rank, int tab)
        {
            if (!GameTables.Coops.TryGetValue(cid, out var info)) return;
            Detail.OpenCoop(cid, info.Name, info.Arcana, rank);
            if (tab != 0) Detail.DebugTab(tab);
            Invalidate(); Update();
        }

        /// <summary>自检：把能力表弹层滚到底，方便截到 Max 那一行。</summary>
        public void DebugScrollDetail(int dy)
        {
            Detail.ScrollBy(dy);
            Invalidate(); Update();
        }

        // ------------------------------------------------------------ 小键盘导航
        private int RowTopOf(int index)
        {
            int y = HintH + 8;
            for (int k = 0; k < index && k < _rows.Count; k++)
                y += _rows[k].IsHeader ? HeaderH : RowH;
            return y;
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            int top = RowTopOf(index);
            int h = _rows[index].IsHeader ? HeaderH : RowH;
            int viewTop = HintH + 4, viewBot = Height - 6;
            int screenTop = top - _scroll;
            if (screenTop < viewTop) _scroll = Math.Max(0, top - viewTop);
            else if (screenTop + h > viewBot) _scroll = Math.Min(MaxScroll, top + h - viewBot);
        }

        public override bool NavMove(int dx, int dy)
        {
            if (Detail.IsOpen || dy == 0 || _rows.Count == 0) return false;

            int n = _rows.Count;
            int j = _navSel;
            if (j < 0 || j >= n) j = dy > 0 ? -1 : n;

            // 跳过分组标题，最多走一整轮
            for (int k = 0; k <= n; k++)
            {
                j += dy;
                if (j < 0) { j = 0; break; }
                if (j >= n) { j = n - 1; break; }
                if (!_rows[j].IsHeader) break;
            }
            while (j >= 0 && j < n && _rows[j].IsHeader) j += dy > 0 ? 1 : -1;
            if (j < 0 || j >= n || _rows[j].IsHeader) return false;

            _navSel = j;
            EnsureVisible(j);
            Invalidate();
            return true;
        }

        public override bool NavConfirm()
        {
            if (Detail.IsOpen || _navSel < 0 || _navSel >= _rows.Count) return false;
            var r = _rows[_navSel];
            if (r.IsHeader) return false;
            Detail.OpenCoop(r.Id, r.Name, r.Arcana, r.Rank);
            Invalidate();
            return true;
        }

        public override bool NavBack()
        {
            if (!Detail.IsOpen) return false;
            Detail.Close();
            Invalidate();
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Detail.IsOpen) { base.OnMouseDown(e); Invalidate(); return; }

            foreach (var h in _hit)
            {
                if (!h.Rect.Contains(e.Location)) continue;
                _navSel = _rows.FindIndex(r => !r.IsHeader && r.Id == h.Id);
                Detail.OpenCoop(h.Id, h.Name, h.Arcana, h.Rank);
                Focus();
                Invalidate();
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintBackdrop(g);
            _hit.Clear();

            if (_rows.Count == 0)
            {
                DrawNoteHint(g, "♪ = +5 点　♪♪ = +10 点　♪♪♪ = +15 点", "同塔罗 ×1.5");
                using var f = P5R.F(13f, FontStyle.Bold);
                P5R.Text(g, "还没读到 Coop 数据 —— 进入存档后会自动刷出（不用重启面板）", f,
                    new RectangleF(0, Height * 0.42f, Width, 40), Theme.Blend(T.Text, T.Bg, 0.35),
                    StringAlignment.Center, StringAlignment.Center, 0f, false);
                PaintDetail(g);
                return;
            }

            DrawNoteHint(g, "♪ = +5 点　♪♪ = +10 点　♪♪♪ = +15 点", "同塔罗 ×1.5");

            int padX = 12;
            int y = HintH + 8 - _scroll;
            var focusRect = RectangleF.Empty;

            for (int ri = 0; ri < _rows.Count; ri++)
            {
                var r = _rows[ri];
                if (r.IsHeader)
                {
                    if (y + HeaderH > HintH && y < Height)
                        DrawHeader(g, r, padX, y, Width - padX * 2);
                    y += HeaderH;
                    continue;
                }

                if (y + RowH > HintH && y < Height)
                {
                    DrawRow(g, r, padX, y, Width - padX * 2);
                    if (ri == _navSel)
                        focusRect = new RectangleF(padX + 6, y + 2, Width - padX * 2 - 12, RowH - 6);
                }
                y += RowH;
            }

            if (!Detail.IsOpen && focusRect.Width > 0) DrawNavFocus(g, focusRect);

            // 滚动条（P5R 那种白色竖条）
            if (MaxScroll > 0)
            {
                int trackTop = HintH + 6, trackH = Height - HintH - 16;
                if (trackH > 20)
                {
                    int total = HintH + ContentHeight;
                    int thumbH = Math.Max(28, (int)((double)Height / total * trackH));
                    int thumbY = trackTop + (int)((double)_scroll / MaxScroll * (trackH - thumbH));
                    var tb = new RectangleF(Width - 9, thumbY, 4, thumbH);
                    using var p = P5R.Skew(tb, -2f);
                    P5R.Fill(g, p, Color.FromArgb(210, T.Text));
                }
            }

            PaintDetail(g);
        }

        private void DrawHeader(Graphics g, CoopRow r, int x, int y, int w)
        {
            var bar = new RectangleF(x + 8, y + 5, Math.Min(w * 0.55f, 220), 17);
            P5R.Slab(g, bar, Color.FromArgb(220, P5R.Ink), Color.FromArgb(70, P5R.Wire));
            P5R.Slab(g, new RectangleF(bar.X + 6, bar.Y + 4, 4, 9), r.HeaderColor);
            using var f = P5R.F(11.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, r.HeaderText, f,
                new Rectangle((int)bar.X + 16, (int)bar.Y, (int)bar.Width - 20, (int)bar.Height),
                r.HeaderColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawRow(Graphics g, CoopRow r, int x, int y, int w)
        {
            bool max = r.Rank >= 10;
            bool ready = !max && r.Need.HasValue && r.Need.Value > 0 && r.Points >= r.Need.Value;

            // 增长时不再整行滑入，两种动画叠在一起会看不清条扫到哪
            bool growing = GrowBusy(r.Id);
            float t = RowIntro(r.Id);
            float ease = growing ? 1f : P5R.EaseOut(t);
            int slide = (int)Math.Round((1f - ease) * 22);

            var block = new RectangleF(x + 6 + slide, y + 3, w - 12, RowH - 7);
            if (block.Width < 80) return;

            _hit.Add(new Hit
            {
                Rect = new RectangleF(x + 6, y + 3, w - 12, RowH - 7),
                Id = r.Id, Rank = r.Rank, Name = r.Name, Arcana = r.Arcana,
            });

            // 底板：满级紫 / 可升级绿 / 普通深灰
            Color fillTop, fillBot, edge;
            if (max) { fillTop = P5R.RedHot; fillBot = P5R.Red; edge = Color.FromArgb(140, Color.White); }
            else if (ready) { fillTop = Color.FromArgb(24, 46, 40); fillBot = Color.FromArgb(15, 31, 27); edge = Color.FromArgb(150, 96, 226, 176); }
            else { fillTop = Color.FromArgb(230, P5R.Slate); fillBot = Color.FromArgb(210, P5R.SlabLo); edge = Color.FromArgb(92, P5R.Wire); }

            using (var p = P5R.Skew(block))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(block.X, block.Y, Math.Max(2f, block.Width), Math.Max(2f, block.Height)),
                    fillTop, fillBot, 0f);
                g.FillPath(lg, p);
                P5R.Stroke(g, p, edge, 1f);
            }
            P5R.Slab(g, new RectangleF(block.X + 8, block.Y + 8, 4, block.Height - 16),
                     max ? P5R.Paper : (ready ? Color.FromArgb(120, 240, 184) : T.Accent));

            int innerX = (int)block.X + 20;
            int rowW = (int)block.Width - 32;

            // 名称
            using (var f = P5R.F(13.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, r.Name, f, new Rectangle(innerX, (int)block.Y + 4, 108, 20), T.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // 塔罗
            using (var f = P5R.F(11f))
                TextRenderer.DrawText(g, r.Arcana, f, new Rectangle(innerX + 110, (int)block.Y + 4, 58, 20),
                    Theme.Blend(T.Text, T.Bg, 0.45),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // R 徽章
            var rb = new RectangleF(innerX + 168, block.Y + 5, 34, 18);
            P5R.Slab(g, rb, max ? P5R.Paper : T.Accent);
            using (var f = P5R.F(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, "R" + r.Rank, f, Rectangle.Round(rb), max ? P5R.Ink : P5R.Paper,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // 数值平行四边形框（第一行右端）
            string num = max ? "MAX" : (r.Need.HasValue ? $"{r.Points}/{r.Need.Value}" : "—");
            float chipW = 64;
            var chip = new RectangleF(block.Right - chipW - 10, block.Y + 5, chipW, 20);
            using (var f = P5R.F(11.5f, FontStyle.Bold))
                P5R.Chip(g, chip, P5R.Paper, Color.FromArgb(96, P5R.Ink), num, P5R.Ink, f);

            // --- R当前 → 还差N → R下一级（紧跟在 R 徽章后面，一块斜切标签）
            //     「还差 N」白字；「»R 下一级」黑底绿字，和「可以升级」组里的写法一致
            //     » 在雅黑里是小号标点、贴基线，单独用大一号的字画，才像个箭头
            if (!max && r.Need.HasValue && r.Need.Value > 0)
            {
                int gapPt = Math.Max(0, r.Need.Value - r.Points);
                string sGap = gapPt > 0 ? "+" + gapPt : "";
                string sArrow = "»";
                string sNext = "R" + (r.Rank + 1);
                using (var f = P5R.F(10.5f, FontStyle.Bold))
                using (var fa = P5R.F(14f, FontStyle.Bold))
                {
                    const TextFormatFlags mf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                               TextFormatFlags.SingleLine;
                    var szG = TextRenderer.MeasureText(g, sGap, f, new Size(int.MaxValue, int.MaxValue), mf);
                    var szA = TextRenderer.MeasureText(g, sArrow, fa, new Size(int.MaxValue, int.MaxValue), mf);
                    var szN = TextRenderer.MeasureText(g, sNext, f, new Size(int.MaxValue, int.MaxValue), mf);

                    float padL = sGap.Length > 0 ? 8f : 10f;
                    float padR = 9f;
                    float gapA = 2.5f;          // 数字 → 箭头、箭头 → R 的呼吸位
                    var cb = new RectangleF(rb.Right + 6, rb.Y,
                        padL + szG.Width + gapA + szA.Width + gapA + szN.Width + padR, rb.Height);
                    if (cb.Right < chip.X - 6)      // 窄面板放不下就不画，右侧数值框还有完整信息
                    {
                        P5R.Slab(g, cb, Color.FromArgb(215, P5R.Ink), Color.FromArgb(84, P5R.Wire));

                        var green = Color.FromArgb(150, 240, 184);
                        float tx = cb.X + padL;
                        if (sGap.Length > 0)
                        {
                            TextRenderer.DrawText(g, sGap, f,
                                Rectangle.Round(new RectangleF(tx, cb.Y, szG.Width, cb.Height)), P5R.Paper,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            tx += szG.Width + gapA;
                        }
                        TextRenderer.DrawText(g, sArrow, fa,
                            Rectangle.Round(new RectangleF(tx, cb.Y - 0.5f, szA.Width, cb.Height)), green,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        tx += szA.Width + gapA;
                        TextRenderer.DrawText(g, sNext, f,
                            Rectangle.Round(new RectangleF(tx, cb.Y, szN.Width, cb.Height)), green,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                }
            }

            // 第二行：进度条 + 状态
            double ratio;
            Color barFill;
            if (max) { ratio = 1.0; barFill = P5R.Paper; }
            else if (r.Need.HasValue && r.Need.Value > 0)
            {
                ratio = (double)r.Points / r.Need.Value;
                barFill = ready ? Color.FromArgb(126, 236, 178) : T.Accent;
            }
            else { ratio = 0; barFill = Theme.Blend(T.Text, T.Bg, 0.5); }

            // 好感涨了：条从旧值扫到新值，多出来那段用荧光黄标出，头上飘「+N」
            float shown = GrowRatio(r.Id, (float)(ratio * ease), out float growFrom, out int growDelta, out float growFade);

            // 「还差 N」已经挪到上面的 R 链条里了，这里不再重复；其余状态照旧
            bool showState = !string.IsNullOrEmpty(r.State) && !r.State.StartsWith("还差");
            var bar = new RectangleF(innerX, block.Bottom - 11,
                                     Math.Max(40f, showState ? rowW - 120 : rowW - 10), 6);
            DrawBar(g, bar, shown, barFill, Color.FromArgb(190, P5R.Ink),
                    growDelta > 0 ? growFrom : -1, GrowTint);

            if (growDelta > 0)
                DrawGrowText(g, bar.X + bar.Width * shown, bar.Y, growDelta, growFade, bar.X, bar.Right - 4);

            if (showState)
            {
                using var f = P5R.F(11f, ready ? FontStyle.Bold : FontStyle.Regular);
                TextRenderer.DrawText(g, r.State, f,
                    new Rectangle((int)bar.Right + 8, (int)bar.Y - 8, Math.Max(30, (int)(block.Right - bar.Right - 18)), 20),
                    ready ? Color.FromArgb(170, 255, 216) : Theme.Blend(T.Text, T.Bg, 0.40),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
