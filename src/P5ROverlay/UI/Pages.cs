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

        // ---- 入场动画（数据变化时播一次，播完停表，不常驻吃 CPU）
        private readonly Timer _anim = new Timer { Interval = 30 };
        private float _intro = 1f;
        private DateTime _animStart;
        private const int AnimMs = 700;

        /// <summary>0→1 的入场进度，绘制时乘到位移/长度上。</summary>
        protected float Intro => _intro;

        protected PageBase()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BorderStyle = BorderStyle.None;
            _anim.Tick += (s, e) =>
            {
                float ms = (float)(DateTime.Now - _animStart).TotalMilliseconds;
                _intro = P5R.Clamp01(ms / AnimMs);
                if (_intro >= 1f) _anim.Stop();
                Invalidate();
            };
        }

        /// <summary>播放入场动画。</summary>
        protected void PlayIntro()
        {
            _intro = 0f;
            _animStart = DateTime.Now;
            _anim.Start();
        }

        public virtual void SetTheme(Theme t)
        {
            T = t;
            BackColor = t.Bg2;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ 公共底
        /// <summary>整页底色：黑 + 右侧斜纹 + 半调网点，模拟 P5R 的墙面。</summary>
        protected void PaintBackdrop(Graphics g)
        {
            using (var b = new SolidBrush(T.Bg)) g.FillRectangle(b, ClientRectangle);

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
        protected int DrawNoteHint(Graphics g, string main, string suffix)
        {
            const int padX = 14, h = 30;
            var bar = new RectangleF(padX + 6, 4, Math.Max(60, Width - padX * 2 - 12), h - 10);
            P5R.Slab(g, bar, Color.FromArgb(210, P5R.Ink), Color.FromArgb(50, P5R.Wire));
            P5R.Slab(g, new RectangleF(padX + 8, 6, 4, h - 14), T.Accent);

            using (var f = P5R.F(11.5f, FontStyle.Bold))
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
                    float x = box.X + sz.Width + 8;
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

        /// <summary>P5R 式进度条：斜切轨道 + 红色填充 + 末端竖切。</summary>
        protected void DrawBar(Graphics g, RectangleF r, double ratio, Color fill, Color track)
        {
            if (r.Width < 14 || r.Height < 5) return;
            ratio = Math.Max(0, Math.Min(1, ratio));

            using (var p = P5R.Skew(r, -5f))
            {
                P5R.Fill(g, p, track);
                P5R.Stroke(g, p, Color.FromArgb(90, P5R.Wire), 1f);
            }
            float fw = (float)Math.Round(r.Width * ratio);
            if (fw >= 5)
            {
                var fr = new RectangleF(r.X, r.Y, Math.Max(5f, fw), r.Height);
                using var p = P5R.Skew(fr, -5f);
                P5R.Fill(g, p, fill);
                // 顶端一条高光，避免大片纯色
                var hi = new RectangleF(fr.X + 1, fr.Y + 1, Math.Max(2f, fr.Width - 2), Math.Max(1f, fr.Height * 0.34f));
                using var hp = P5R.Skew(hi, -5f);
                P5R.Fill(g, hp, Color.FromArgb(64, Color.White));
            }
        }
    }

    // ======================================================================= 五维页
    public sealed class StatPage : PageBase
    {
        private readonly short[] _stats = new short[5];
        private readonly short[] _prev = new short[5];
        private bool _hasPrev;

        public void SetData(short[] s)
        {
            if (s == null) return;
            bool changed = !_hasPrev;
            for (int i = 0; i < 5 && i < s.Length; i++)
            {
                if (_stats[i] != s[i]) changed = true;
                _prev[i] = _hasPrev ? _stats[i] : (short)0;
                _stats[i] = s[i];
            }
            _hasPrev = true;
            if (changed) PlayIntro();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintBackdrop(g);

            int hintH = DrawNoteHint(g, "♪ = +2 点　♪♪ = +3~4 点　♪♪♪ = +5/7/10 点", "占卜 ×1.5");

            int usable = Math.Max(200, Height - hintH - 8);
            int rowH = Math.Min(104, Math.Max(58, usable / 5));
            int totalH = rowH * 5;
            int startY = hintH + Math.Max(2, (usable - totalH) / 2);

            int padX = 12;
            int fullW = Math.Max(120, Width - padX * 2);

            for (int i = 0; i < 5; i++)
            {
                DrawStatRow(g, i, padX, startY + i * rowH, fullW, rowH - 6, i * 0.06f);
            }
        }

        private void DrawStatRow(Graphics g, int i, int x, int y, int w, int h, float delay)
        {
            int pts = _stats[i];
            int lv = GameTables.StatLevel(i, pts);
            var th = GameTables.StatTh[i];
            bool max = lv >= 5;

            // 进度与文案
            double ratio;
            string next;
            if (max)
            {
                ratio = 1.0;
                next = "MAX";
            }
            else
            {
                int lo = th[lv - 1], hi = th[lv];
                ratio = (pts - lo) / (double)Math.Max(1, hi - lo);
                next = $"距 Lv{lv + 1} 还差 {hi - pts}";
            }

            // 动画：错峰滑入 + 数值滚动 + 进度缓动
            float t = P5R.Clamp01((Intro - delay) / (1f - delay));
            float ease = P5R.EaseOut(t);
            int slide = (int)Math.Round((1f - ease) * 26);
            int shownPts = (int)Math.Round(pts * ease);
            double shownRatio = ratio * ease;

            var block = new RectangleF(x + 6 + slide, y + 2, w - 12, h - 4);
            if (block.Width < 60 || block.Height < 24) return;

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

            // --- 名称
            using (var f = P5R.F(Math.Min(19f, block.Height * 0.34f + 6f), FontStyle.Bold))
            {
                P5R.Text(g, GameTables.StatNames[i], f,
                    new RectangleF(block.X + 20, block.Y + 4, Math.Min(130, block.Width * 0.42f), block.Height * 0.52f),
                    max ? P5R.Ink : T.Text, StringAlignment.Near, StringAlignment.Center, -0.16f, !max);
            }

            // --- 点数胶囊（右端）
            float pillW = Math.Min(64f, Math.Max(46f, block.Width * 0.16f));
            var pill = new RectangleF(block.Right - pillW - 10, block.Y + 6, pillW, Math.Min(26f, block.Height * 0.40f));
            using (var f = P5R.F(Math.Min(15f, pill.Height * 0.62f), FontStyle.Bold))
                P5R.Pill(g, pill, max ? P5R.Paper : P5R.Paper, shownPts.ToString(), P5R.Ink, f);

            // --- 等级徽章
            float badgeH = Math.Min(18f, block.Height * 0.26f);
            var badge = new RectangleF(block.X + 20, block.Bottom - badgeH - 7, 44, badgeH);
            P5R.Slab(g, badge, max ? P5R.Paper : T.Accent);
            using (var f = P5R.F(Math.Max(9f, badgeH * 0.62f), FontStyle.Bold))
                TextRenderer.DrawText(g, "Lv" + lv, f, Rectangle.Round(badge), max ? P5R.Ink : P5R.Paper,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // --- 进度条
            float barX = badge.Right + 8;
            float barW = block.Right - barX - Math.Min(126f, block.Width * 0.30f);
            if (barW > 24)
            {
                var bar = new RectangleF(barX, badge.Y + badge.Height * 0.5f - 3.5f, barW, 7);
                DrawBar(g, bar, shownRatio, max ? P5R.Paper : T.Accent, Color.FromArgb(190, P5R.Ink));
            }

            // --- 距下一级
            float tipW = Math.Min(126f, block.Width * 0.30f) - 4;
            if (tipW > 40)
            {
                using var f = P5R.F(10.5f);
                TextRenderer.DrawText(g, next, f,
                    new Rectangle((int)(block.Right - tipW - 8), (int)(badge.Y - 1), (int)tipW, (int)badge.Height + 2),
                    max ? P5R.Ink : Theme.Blend(T.Text, T.Bg, 0.42),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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

        private const int HeaderH = 26;
        private const int RowH = 52;
        private const int HintH = 30;

        public CoopPage()
        {
            SetStyle(ControlStyles.Selectable, true);
        }

        public void SetData(List<CoopRow> rows)
        {
            _rows.Clear();
            if (rows != null) _rows.AddRange(rows);
            _scroll = Math.Min(_scroll, MaxScroll);
            if (_scroll < 0) _scroll = 0;
            PlayIntro();
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
            _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll - e.Delta / 3));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintBackdrop(g);

            if (_rows.Count == 0)
            {
                DrawNoteHint(g, "♪ = +5 点　♪♪ = +10 点　♪♪♪ = +15 点", "同塔罗 ×1.5");
                using var f = P5R.F(13f, FontStyle.Bold);
                P5R.Text(g, "还没有已解锁的 Coop —— 进游戏后会自动刷新", f,
                    new RectangleF(0, Height * 0.42f, Width, 40), Theme.Blend(T.Text, T.Bg, 0.35),
                    StringAlignment.Center, StringAlignment.Center, 0f, false);
                return;
            }

            DrawNoteHint(g, "♪ = +5 点　♪♪ = +10 点　♪♪♪ = +15 点", "同塔罗 ×1.5");

            int padX = 12;
            int y = HintH + 8 - _scroll;
            int idx = 0;

            foreach (var r in _rows)
            {
                if (r.IsHeader)
                {
                    if (y + HeaderH > HintH && y < Height)
                        DrawHeader(g, r, padX, y, Width - padX * 2);
                    y += HeaderH;
                    continue;
                }

                if (y + RowH > HintH && y < Height)
                    DrawRow(g, r, padX, y, Width - padX * 2, idx);
                y += RowH;
                idx++;
            }

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

        private void DrawRow(Graphics g, CoopRow r, int x, int y, int w, int index)
        {
            bool max = r.Rank >= 10;
            bool ready = !max && r.Need.HasValue && r.Need.Value > 0 && r.Points >= r.Need.Value;

            float t = P5R.Clamp01((Intro - index * 0.035f) / 0.7f);
            float ease = P5R.EaseOut(t);
            int slide = (int)Math.Round((1f - ease) * 22);

            var block = new RectangleF(x + 6 + slide, y + 3, w - 12, RowH - 7);
            if (block.Width < 80) return;

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

            // 数值胶囊（第一行右端）
            string num = max ? "MAX" : (r.Need.HasValue ? $"{r.Points}/{r.Need.Value}" : "—");
            float pillW = 62;
            var pill = new RectangleF(block.Right - pillW - 10, block.Y + 5, pillW, 20);
            using (var f = P5R.F(11.5f, FontStyle.Bold))
                P5R.Pill(g, pill, P5R.Paper, num, P5R.Ink, f);

            // 第二行：进度条 + 状态
            double ratio;
            Color barFill;
            if (max) { ratio = 1.0; barFill = P5R.Paper; }
            else if (r.Need.HasValue && r.Need.Value > 0)
            {
                ratio = (double)r.Points / r.Need.Value * ease;
                barFill = ready ? Color.FromArgb(126, 236, 178) : T.Accent;
            }
            else { ratio = 0; barFill = Theme.Blend(T.Text, T.Bg, 0.5); }

            var bar = new RectangleF(innerX, block.Bottom - 13, Math.Max(40f, rowW - 120), 6);
            DrawBar(g, bar, ratio, barFill, Color.FromArgb(190, P5R.Ink));

            if (!string.IsNullOrEmpty(r.State))
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
