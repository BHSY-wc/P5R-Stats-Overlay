using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>
    /// 关键页：把「必须主动去拿、拿到以后玩法会变」的能力与书列成一张可滚动清单，
    /// 并按当前 Coop Rank 实时标出达成状态。点带 Coop 的行直接弹能力表。
    /// </summary>
    public sealed class KeyPage : PageBase
    {
        private readonly Dictionary<int, int> _ranks = new Dictionary<int, int>();
        private readonly short[] _stats = new short[5];
        private int _scroll;

        private struct Hit { public RectangleF Rect; public int Cid, Rank; public string Name, Arcana; }
        private readonly List<Hit> _hit = new List<Hit>();

        private const int HintH = 30;
        private const int GroupH = 30;
        private const int RowH = 62;

        public KeyPage()
        {
            SetStyle(ControlStyles.Selectable, true);
        }

        /// <summary>Coop 等级变化时刷新达成状态；没变就什么都不做，避免每 5 秒重画一次。</summary>
        public void SetRanks(List<CoopEntry> coops)
        {
            var d = new Dictionary<int, int>();
            if (coops != null)
                foreach (var c in coops)
                    if (c != null && c.Id > 0) d[c.Id] = c.Rank;

            bool same = d.Count == _ranks.Count;
            if (same)
            {
                foreach (var kv in d)
                    if (!_ranks.TryGetValue(kv.Key, out int r) || r != kv.Value) { same = false; break; }
            }
            if (same) return;

            _ranks.Clear();
            foreach (var kv in d) _ranks[kv.Key] = kv.Value;
            Invalidate();
        }

        private int Rank(int cid) => _ranks.TryGetValue(cid, out int r) ? r : 0;

        /// <summary>五维点数变化时刷新汉堡挑战的达成状态；点数没变就不重画。</summary>
        public void SetStats(short[] stats)
        {
            if (stats == null || stats.Length < 5) return;

            bool same = true;
            for (int i = 0; i < 5; i++)
                if (_stats[i] != stats[i]) { same = false; break; }
            if (same) return;

            for (int i = 0; i < 5; i++) _stats[i] = stats[i];
            Invalidate();
        }

        private int StatLv(int i) => GameTables.StatLevel(i, _stats[i]);

        /// <summary>五维条件是否全部达到（StatNeed 里 0 表示该项不要求）。</summary>
        private bool StatOk(KeyData.Item it)
        {
            if (it.StatNeed == null) return false;
            for (int i = 0; i < 5 && i < it.StatNeed.Length; i++)
                if (it.StatNeed[i] > 0 && StatLv(i) < it.StatNeed[i]) return false;
            return true;
        }

        private int ContentHeight
        {
            get
            {
                int h = 8;
                foreach (var g in KeyData.Groups) h += GroupH + g.Items.Length * RowH;
                return h + 12;
            }
        }

        private int MaxScroll => Math.Max(0, HintH + ContentHeight - Height);

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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Detail.IsOpen) { base.OnMouseDown(e); Invalidate(); return; }

            foreach (var h in _hit)
            {
                if (!h.Rect.Contains(e.Location)) continue;
                Detail.OpenCoop(h.Cid, h.Name, h.Arcana, h.Rank);
                Focus();
                Invalidate();
                return;
            }
            base.OnMouseDown(e);
        }

        /// <summary>自检钩子：滚到指定位置，方便截图覆盖到下半部分。</summary>
        public void DebugScrollTo(int y)
        {
            _scroll = Math.Max(0, Math.Min(MaxScroll, y));
            Invalidate(); Update();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintBackdrop(g);
            _hit.Clear();

            _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll));

            int padX = 12;
            int w = Math.Max(120, Width - padX * 2);

            // 列表整体裁在提示条以下，滚上来时不会把提示条顶乱
            var st = g.Save();
            g.SetClip(new Rectangle(0, HintH, Width, Math.Max(0, Height - HintH)));

            int y = HintH + 8 - _scroll;
            foreach (var grp in KeyData.Groups)
            {
                if (y + GroupH > HintH && y < Height) DrawGroup(g, grp, padX, y, w);
                y += GroupH;

                foreach (var it in grp.Items)
                {
                    if (y + RowH > HintH && y < Height) DrawItem(g, grp, it, padX, y, w);
                    y += RowH;
                }
            }
            g.Restore(st);

            // 提示条最后画，永远悬在列表之上
            DrawNoteHint(g, "★ 硬截止　✓ 已达成　徽章＝现级/需求", "点能力行看表");

            if (MaxScroll > 0)
            {
                int trackTop = HintH + 6, trackH = Height - HintH - 16;
                if (trackH > 20)
                {
                    int total = HintH + ContentHeight;
                    int thumbH = Math.Max(28, (int)((double)Height / total * trackH));
                    int thumbY = trackTop + (int)((double)_scroll / MaxScroll * (trackH - thumbH));
                    using var p = P5R.Skew(new RectangleF(Width - 9, thumbY, 4, thumbH), -2f);
                    P5R.Fill(g, p, Color.FromArgb(210, T.Text));
                }
            }

            PaintDetail(g);
        }

        private void DrawGroup(Graphics g, KeyData.Group grp, int x, int y, int w)
        {
            var bar = new RectangleF(x + 8, y + 6, Math.Min(w * 0.62f, 260), 18);
            P5R.Slab(g, bar, Color.FromArgb(222, P5R.Ink), Color.FromArgb(70, P5R.Wire));
            P5R.Slab(g, new RectangleF(bar.X + 7, bar.Y + 4, 4, 10), grp.Tint);

            using (var f = P5R.F(11.5f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, grp.Name, f,
                    new Rectangle((int)bar.X + 17, (int)bar.Y, (int)bar.Width - 60, (int)bar.Height),
                    grp.Tint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                TextRenderer.DrawText(g, "×" + grp.Items.Length, f,
                    new Rectangle((int)bar.Right - 40, (int)bar.Y, 32, (int)bar.Height),
                    Theme.Blend(T.Text, T.Bg, 0.55),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            // 一条细线拉到右侧，让分组之间有阻断感
            using var pen = new Pen(Color.FromArgb(46, P5R.Wire), 1f);
            g.DrawLine(pen, bar.Right + 8, bar.Y + bar.Height * 0.5f, x + w - 4, bar.Y + bar.Height * 0.5f);
        }

        private void DrawItem(Graphics g, KeyData.Group grp, KeyData.Item it, int x, int y, int w)
        {
            bool hasCoop = it.Cid > 0 && it.NeedRank > 0;
            bool hasStat = it.StatNeed != null;
            int cur = hasCoop ? Rank(it.Cid) : -1;
            bool done = hasCoop ? cur >= it.NeedRank : (hasStat && StatOk(it));
            bool near = hasCoop && !done && cur > 0 && it.NeedRank - cur <= 2;

            var block = new RectangleF(x + 6, y + 3, w - 12, RowH - 8);
            if (block.Width < 100) return;

            if (hasCoop)
            {
                string nm = GameTables.Coops.TryGetValue(it.Cid, out var info) ? info.Name : it.Source;
                string arc = GameTables.Coops.TryGetValue(it.Cid, out var info2) ? info2.Arcana : "";
                _hit.Add(new Hit
                {
                    Rect = new RectangleF(x + 6, y + 3, w - 12, RowH - 8),
                    Cid = it.Cid, Rank = Math.Max(0, cur), Name = nm, Arcana = arc,
                });
            }

            // 底板：已达成深绿，其余常规深灰
            using (var p = P5R.Skew(block))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(block.X, block.Y, Math.Max(2f, block.Width), Math.Max(2f, block.Height)),
                    done ? Color.FromArgb(24, 48, 39) : Color.FromArgb(232, P5R.Slate),
                    done ? Color.FromArgb(14, 30, 24) : Color.FromArgb(212, P5R.SlabLo), 0f);
                g.FillPath(lg, p);
                P5R.Stroke(g, p, done ? Color.FromArgb(120, 96, 226, 176) : Color.FromArgb(88, P5R.Wire), 1f);
            }

            // 左侧竖条：分组色；已达成转绿
            P5R.Slab(g, new RectangleF(block.X + 8, block.Y + 9, 4, block.Height - 18),
                     done ? Color.FromArgb(150, 240, 184) : grp.Tint);

            // ---- 右上：截止日期（红块）
            float rightLimit = block.Right - 10;
            if (!string.IsNullOrEmpty(it.Deadline))
            {
                string s = it.Deadline + " 前";
                using var f = P5R.F(10f, FontStyle.Bold);
                var sz = TextRenderer.MeasureText(g, s, f, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                float bw = sz.Width + 14;
                var chip = new RectangleF(block.Right - bw - 8, block.Y + 6, bw, 18);
                P5R.Slab(g, chip, it.Hard ? P5R.Red : Color.FromArgb(196, 150, 60));
                TextRenderer.DrawText(g, s, f, Rectangle.Round(chip), P5R.Paper,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                rightLimit = chip.X - 8;
            }

            // ---- 第一行：标题
            using (var f = P5R.F(13.5f, FontStyle.Bold))
            {
                var box = new RectangleF(block.X + 20, block.Y + 5, Math.Max(40f, rightLimit - block.X - 20), 20);
                P5R.Text(g, it.Title, f, box, done ? Color.FromArgb(198, 246, 220) : T.Text,
                         StringAlignment.Near, StringAlignment.Center, -0.10f, !done);
            }

            // ---- 第二行：来源 + 描述（左），Rank / 五维徽章（右）
            float badgeW = hasCoop ? 78f : (hasStat ? StatBadgeWidth(it) : 0f);
            float descRight = block.Right - 10 - (badgeW > 0 ? badgeW + 8 : 0);

            string sub = string.IsNullOrEmpty(it.Source) ? it.Desc : it.Source + " · " + it.Desc;
            using (var f = P5R.F(10.5f))
            {
                var box = new RectangleF(block.X + 20, block.Y + 30, Math.Max(40f, descRight - block.X - 20), 18);
                TextRenderer.DrawText(g, sub, f, Rectangle.Round(box),
                    Theme.Blend(T.Text, T.Bg, done ? 0.30 : 0.42),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }

            if (hasCoop)
            {
                string txt = done ? "R" + it.NeedRank + " ✓" : "R" + Math.Max(0, cur) + "/" + it.NeedRank;
                Color fill = done ? Color.FromArgb(150, 240, 184)
                                  : (near ? Color.FromArgb(236, 190, 86) : Color.FromArgb(206, P5R.Slate));
                Color ink = done || near ? P5R.Ink : Theme.Blend(T.Text, T.Bg, 0.24);

                var chip = new RectangleF(block.Right - badgeW - 10, block.Y + 30, badgeW, 18);
                using (var f = P5R.F(10.5f, FontStyle.Bold))
                    P5R.Chip(g, chip, fill, Color.FromArgb(96, P5R.Ink), txt, ink, f);
            }
            else if (hasStat)
            {
                DrawStatChips(g, it, block.Right - 10, block.Y + 30);
            }
        }

        /// <summary>五维徽章区宽度：勇 / 知 / 灵 三枚，每枚 44px、间距 5px。</summary>
        private static float StatBadgeWidth(KeyData.Item it)
        {
            int n = 0;
            foreach (int idx in StatShow) if (it.StatNeed[idx] > 0) n++;
            return n <= 0 ? 0f : n * StatChipW + (n - 1) * StatGap;
        }

        // 汉堡挑战只看勇气 / 知识 / 灵巧，按这个顺序排（体贴、魅力通常不要求）
        private static readonly int[] StatShow = { 3, 0, 2, 4, 1 };
        private const float StatChipW = 44f, StatGap = 5f;

        private void DrawStatChips(Graphics g, KeyData.Item it, float right, float top)
        {
            float x = right - StatBadgeWidth(it);
            using var f = P5R.F(9.5f, FontStyle.Bold);

            foreach (int idx in StatShow)
            {
                int need = it.StatNeed[idx];
                if (need <= 0) continue;

                int lv = StatLv(idx);
                bool ok = lv >= need;
                bool almost = !ok && need - lv == 1;

                Color fill = ok ? Color.FromArgb(150, 240, 184)
                                : (almost ? Color.FromArgb(236, 190, 86) : Color.FromArgb(206, P5R.Slate));
                Color ink = ok || almost ? P5R.Ink : Theme.Blend(T.Text, T.Bg, 0.24);

                string txt = GameTables.StatNames[idx].Substring(0, 1) + lv + "/" + need;
                P5R.Chip(g, new RectangleF(x, top, StatChipW, 18), fill, Color.FromArgb(96, P5R.Ink), txt, ink, f);
                x += StatChipW + StatGap;
            }
        }
    }
}
