using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>
    /// 日历页：左边日历、右边（点开）当天日程。
    /// 两种密度 —— 缩起来只看日期，放开来每格带当天事项，像攻略表那样。
    /// </summary>
    public sealed class CalendarPage : PageBase
    {
        private const int BarH = 30;
        private const int InfoH = 18;
        private const int WeekH = 21;

        // ---- 视图状态
        private int _year = CalendarData.Year0;
        private int _month = CalendarData.FirstMonth;
        private bool _expand = true;          // true = 每格带事项
        private bool _follow = true;          // 自动跟随游戏内月份

        // ---- 游戏内时间
        private bool _hasDate;
        private DateTime _today;
        private int _tz = -1;

        // ---- 命中区（每帧重建）
        private struct Cell
        {
            public RectangleF R;
            public DateTime D;
            public bool InMonth;
        }
        private readonly List<Cell> _cells = new List<Cell>();

        private RectangleF _btnPrev, _btnNext, _btnToday, _btnMinus, _btnPlus, _btnTitle;

        // ---- 覆盖层
        private enum Overlay { None, Picker, Day }
        private Overlay _ov = Overlay.None;

        private Rectangle _ovPanel;
        private Rectangle _ovClose;
        private readonly RectangleF[] _pickRects = new RectangleF[12];
        private int _pickHover = -1;

        private DayPlan _ovPlan;
        private DateTime _ovDate;
        private int _ovScroll;
        private float _ovContentH;
        private int _ovLayoutW = -1;
        private readonly List<(string Text, bool Night, bool Title)> _ovItems = new List<(string, bool, bool)>();
        private readonly List<float> _ovHeights = new List<float>();

        private static readonly string[] WeekCn = { "日", "一", "二", "三", "四", "五", "六" };

        public CalendarPage()
        {
            SetStyle(ControlStyles.Selectable, true);
            BackColor = P5R.Ink;   // 必须实色：窗口 90% 不透明，透明面板会把游戏画面透上来
        }

        // ================================================================ 数据
        /// <summary>每次轮询喂进来；dayIdx &lt; 0 表示这一帧没读到。</summary>
        public void SetGameDate(int dayIdx, int tz)
        {
            if (dayIdx < 0) return;

            var d = Snapshot.CalendarOrigin.AddDays(dayIdx);
            bool first = !_hasDate;
            bool monthChanged = _hasDate && (d.Year != _today.Year || d.Month != _today.Month);

            _today = d;
            _tz = tz;
            _hasDate = true;

            // 第一次拿到 / 游戏跨月且用户没手动翻过 → 把视图带过去
            if (first || (_follow && monthChanged))
            {
                _year = d.Year;
                _month = d.Month;
                _follow = true;
            }
            Invalidate();
        }

        private DateTime First => new DateTime(2016, 4, 1);
        private DateTime Last => new DateTime(2017, 3, 1);

        private void Shift(int months)
        {
            var dt = new DateTime(_year, _month, 1).AddMonths(months);
            if (dt < First || dt > Last) return;
            _year = dt.Year;
            _month = dt.Month;
            _follow = false;      // 手动翻过就不再自动跳
            _ov = Overlay.None;
            Invalidate();
        }

        private void GoToday()
        {
            if (!_hasDate) return;
            _year = _today.Year;
            _month = _today.Month;
            _follow = true;
            _ov = Overlay.None;
            Invalidate();
        }

        // ================================================================ 交互
        protected override bool HitRow(Point p)
        {
            if (_ov != Overlay.None) return true;
            foreach (var c in _cells) if (c.R.Contains(p)) return true;
            return _btnPrev.Contains(p) || _btnNext.Contains(p) || _btnToday.Contains(p) ||
                   _btnMinus.Contains(p) || _btnPlus.Contains(p) || _btnTitle.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_ov == Overlay.Picker)
            {
                int h = -1;
                for (int i = 0; i < 12; i++) if (_pickRects[i].Contains(e.Location)) { h = i; break; }
                if (h != _pickHover) { _pickHover = h; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            Focus();

            if (_ov != Overlay.None)
            {
                HandleOverlayClick(e.Location);
                Invalidate();
                return;
            }

            if (_btnPrev.Contains(e.Location)) { Shift(-1); return; }
            if (_btnNext.Contains(e.Location)) { Shift(1); return; }
            if (_btnToday.Contains(e.Location)) { GoToday(); return; }
            if (_btnTitle.Contains(e.Location)) { OpenPicker(); return; }
            if (_btnMinus.Contains(e.Location)) { SetExpand(false); return; }
            if (_btnPlus.Contains(e.Location)) { SetExpand(true); return; }

            foreach (var c in _cells)
            {
                if (!c.InMonth || !c.R.Contains(e.Location)) continue;
                OpenDay(c.D);
                return;
            }
            base.OnMouseDown(e);
        }

        private void SetExpand(bool on)
        {
            if (_expand == on) return;
            _expand = on;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_ov == Overlay.Day)
            {
                _ovScroll -= e.Delta / 3;
                ClampOv();
                Invalidate();
                return;
            }
            if (_ov == Overlay.Picker) return;

            // 日历没有可滚动内容，滚轮直接翻月（和手机日历一致）
            Shift(e.Delta > 0 ? -1 : 1);
        }

        // ================================================================ 覆盖层
        private void HandleOverlayClick(Point p)
        {
            if (_ov == Overlay.Picker)
            {
                if (!_ovPanel.Contains(p)) { _ov = Overlay.None; return; }
                for (int i = 0; i < 12; i++)
                {
                    if (!_pickRects[i].Contains(p)) continue;
                    int m = i + 4 <= 12 ? i + 4 : i - 8;
                    _year = m >= 4 ? 2016 : 2017;
                    _month = m;
                    _follow = false;
                    _ov = Overlay.None;
                    return;
                }
                return;
            }

            // 日详情
            if (_ovClose.Contains(p) || !_ovPanel.Contains(p)) { _ov = Overlay.None; return; }
        }

        private void OpenPicker()
        {
            _ov = Overlay.Picker;
            _pickHover = -1;
            Invalidate();
        }

        private void OpenDay(DateTime d)
        {
            _ov = Overlay.Day;
            _ovDate = d;
            _ovPlan = CalendarData.Get(d);
            _ovScroll = 0;
            _ovLayoutW = -1;
            _ovItems.Clear();
            _ovHeights.Clear();

            if (_ovPlan != null)
            {
                _ovItems.Add(("白天", false, true));
                foreach (var s in _ovPlan.DayItems) _ovItems.Add((s, false, false));
                _ovItems.Add(("夜晚", true, true));
                foreach (var s in _ovPlan.NightItems) _ovItems.Add((s, true, false));
            }
            Invalidate();
        }

        private void ClampOv()
        {
            float max = Math.Max(0f, _ovContentH - (_ovPanel.Height - 92));
            if (_ovScroll < 0) _ovScroll = 0;
            if (_ovScroll > max) _ovScroll = (int)max;
        }

        // ================================================================ 绘制
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            PaintBackdrop(g);
            _cells.Clear();

            int infoH = _hasDate ? InfoH : 0;
            LayoutBar(g);

            if (_hasDate) DrawInfoLine(g, BarH);

            int gridTop = BarH + infoH;
            DrawWeekHeader(g, gridTop);
            DrawGrid(g, gridTop + WeekH, Height - gridTop - WeekH - 6);

            if (_ov == Overlay.Picker) PaintPicker(g);
            else if (_ov == Overlay.Day) PaintDay(g);
        }

        private void LayoutBar(Graphics g)
        {
            int w = Width;
            _btnPrev = new RectangleF(6, 3, 24, 24);
            _btnTitle = new RectangleF(32, 3, Math.Min(126, Math.Max(80, w * 0.30f)), 24);
            _btnNext = new RectangleF(_btnTitle.Right + 2, 3, 24, 24);

            _btnPlus = new RectangleF(w - 34, 3, 24, 24);
            _btnMinus = new RectangleF(w - 62, 3, 24, 24);
            _btnToday = new RectangleF(_btnMinus.X - 40, 3, 34, 24);

            var first = new DateTime(2016, 4, 1);
            var last = new DateTime(2017, 3, 1);
            var cur = new DateTime(_year, _month, 1);
            DrawNavBtn(g, _btnPrev, "‹", cur > first);
            DrawNavBtn(g, _btnNext, "›", cur < last);

            // 年月标题：黑条 + 红块，点开了选月份
            P5R.Slab(g, _btnTitle, Color.FromArgb(210, P5R.Ink), Color.FromArgb(60, P5R.Wire));
            P5R.Slab(g, new RectangleF(_btnTitle.X + 5, _btnTitle.Y + 5, 3, _btnTitle.Height - 10), T.Accent);
            using (var f = P5R.F(13.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, $"{_year} 年 {_month} 月", f,
                    new Rectangle((int)_btnTitle.X + 12, (int)_btnTitle.Y, (int)_btnTitle.Width - 26, (int)_btnTitle.Height),
                    T.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            using (var f = P5R.F(9f))
                TextRenderer.DrawText(g, "▾", f,
                    new Rectangle((int)_btnTitle.Right - 15, (int)_btnTitle.Y, 12, (int)_btnTitle.Height),
                    Theme.Blend(T.Text, T.Bg, 0.45), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            DrawNavBtn(g, _btnToday, "今", !_hasDate);
            DrawNavBtn(g, _btnMinus, "−", _expand);
            DrawNavBtn(g, _btnPlus, "＋", !_expand);
        }

        private void DrawNavBtn(Graphics g, RectangleF r, string text, bool enabled)
        {
            var bg = enabled ? Color.FromArgb(200, P5R.Slate) : Color.FromArgb(90, P5R.Slate);
            P5R.Slab(g, r, bg, Color.FromArgb(enabled ? 90 : 34, P5R.Wire));
            using var f = P5R.F(13f, FontStyle.Bold);
            TextRenderer.DrawText(g, text, f, Rectangle.Round(r),
                enabled ? T.Text : Theme.Blend(T.Text, T.Bg, 0.62),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawInfoLine(Graphics g, int y)
        {
            string wd = "日一二三四五六"[(int)_today.DayOfWeek].ToString();
            string tzn = _tz < 0 ? "" : Snapshot.TimeZoneText(_tz);
            string s = $"游戏内 {_today.Month} 月 {_today.Day} 日（{wd}）"
                     + (tzn.Length > 0 ? " · " + tzn : "")
                     + (_follow ? "" : " · 已暂停跟随");

            using var f = P5R.F(10.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, s, f,
                new Rectangle(8, y + 1, Width - 16, InfoH - 2),
                _follow ? Color.FromArgb(214, 255, 226, 232) : Theme.Blend(T.Text, T.Bg, 0.55),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private void DrawWeekHeader(Graphics g, int y)
        {
            float cw = (Width - 8) / 7f;

            for (int i = 0; i < 7; i++)
            {
                var r = new RectangleF(4 + i * cw, y, cw, WeekH - 2);
                // 周日红、周六蓝，其余灰白 —— 和纸质日历的习惯一致
                Color c = i == 0 ? Color.FromArgb(236, 96, 118)
                        : i == 6 ? Color.FromArgb(120, 170, 226)
                        : Theme.Blend(T.Text, T.Bg, 0.22);
                using var f = P5R.F(11.5f, FontStyle.Bold);
                TextRenderer.DrawText(g, WeekCn[i], f, Rectangle.Round(r), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            using var pen = new Pen(Color.FromArgb(46, P5R.Wire), 1f);
            g.DrawLine(pen, 4, y + WeekH - 1, Width - 4, y + WeekH - 1);
        }

        private void DrawGrid(Graphics g, int top, int availH)
        {
            if (availH < 80) return;

            var first = new DateTime(_year, _month, 1);
            int lead = (int)first.DayOfWeek;                      // 周日 = 0，和表头一致
            int days = DateTime.DaysInMonth(_year, _month);
            int rows = (int)Math.Ceiling((lead + days) / 7.0);
            if (rows < 5) rows = 5;

            float cw = (Width - 8) / 7f;
            float ch = availH / (float)rows;

            for (int i = 0; i < days; i++)
            {
                int slot = lead + i;
                int col = slot % 7, row = slot / 7;
                var d = first.AddDays(i);
                var r = new RectangleF(4 + col * cw + 1, top + row * ch + 1, cw - 2, ch - 2);
                _cells.Add(new Cell { R = r, D = d, InMonth = true });
                DrawDayCell(g, r, d);
            }
        }

        private void DrawDayCell(Graphics g, RectangleF r, DateTime d)
        {
            if (r.Width < 16 || r.Height < 14) return;

            bool isToday = _hasDate && d.Date == _today.Date;
            bool has = CalendarData.Has(d.Month, d.Day);
            var plan = CalendarData.Get(d.Month, d.Day);

            // 底
            if (isToday)
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y, Math.Max(2f, r.Width), Math.Max(2f, r.Height)),
                    P5R.RedHot, Color.FromArgb(176, 0, 34), 90f);
                g.FillRectangle(lg, r);
            }
            else
            {
                using var b = new SolidBrush(has ? Theme.Blend(T.Bg, Color.White, 0.085)
                                                 : Theme.Blend(T.Bg, Color.White, 0.028));
                g.FillRectangle(b, r);
            }

            using (var pen = new Pen(Color.FromArgb(isToday ? 150 : 40, isToday ? Color.White : P5R.Wire), 1f))
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);

            // 日期数字
            if (_expand && r.Height > 42)
            {
                using var f = P5R.F(Math.Min(13.5f, 9.5f + r.Width * 0.035f), FontStyle.Bold);
                TextRenderer.DrawText(g, d.Day.ToString(), f,
                    new Rectangle((int)r.X + 4, (int)r.Y + 1, 30, 16),
                    isToday ? Color.White : Theme.Blend(T.Text, T.Bg, has ? 0.05 : 0.42),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                if (isToday)
                {
                    using var ft = P5R.F(8.5f, FontStyle.Bold);
                    TextRenderer.DrawText(g, "今天", ft,
                        new Rectangle((int)r.Right - 30, (int)r.Y + 2, 26, 13), Color.FromArgb(255, 232, 236),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                if (plan != null) DrawCellItems(g, r, plan, isToday);
            }
            else
            {
                bool hasRoom = r.Height > 26;
                using var f = P5R.F(hasRoom ? Math.Min(26f, r.Height * 0.42f) : 11f,
                                    isToday ? FontStyle.Bold : FontStyle.Regular);
                var box = new RectangleF(r.X, r.Y + (hasRoom ? 0 : 1), r.Width, r.Height - (hasRoom ? 0 : 2));
                P5R.Text(g, d.Day.ToString(), f, box,
                    isToday ? Color.White : Theme.Blend(T.Text, T.Bg, has ? 0.10 : 0.48),
                    StringAlignment.Center, StringAlignment.Center, 0f, false);

                // 有安排的小红点
                if (has && !isToday && r.Height > 30)
                {
                    using var b = new SolidBrush(Color.FromArgb(190, T.Accent));
                    float dy = hasRoom ? r.Y + r.Height * 0.5f + Math.Min(30f, r.Height * 0.22f)
                                       : r.Bottom - 6;
                    g.FillEllipse(b, r.X + r.Width * 0.5f - 2, dy, 4, 4);
                }
            }
        }

        /// <summary>展开态：格子里塞当天的条目，白天在前、夜晚在后，放不下就截。</summary>
        private void DrawCellItems(Graphics g, RectangleF r, DayPlan plan, bool isToday)
        {
            float x = r.X + 4;
            float y = r.Y + 17;
            float w = r.Width - 8;
            float bottom = r.Bottom - 3;
            if (w < 20 || y > bottom) return;

            float lineH = Math.Max(11f, Math.Min(15f, (bottom - y) / 6f));
            using var f = P5R.F(Math.Max(8.5f, Math.Min(10.5f, lineH * 0.80f)));

            var st = g.Save();
            g.SetClip(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2));

            int drawn = 0;
            var all = new List<(string s, bool night)>();
            foreach (var s in plan.DayItems) all.Add((s, false));
            foreach (var s in plan.NightItems) all.Add((s, true));

            int maxLines = Math.Max(1, (int)((bottom - y) / lineH));
            foreach (var (s, night) in all)
            {
                if (drawn >= maxLines) break;

                // 条目前缀：白天实心、夜晚空心，比写「夜」字省地方
                using (var br = new SolidBrush(night ? Color.FromArgb(120, 176, 230) : Color.FromArgb(226, 0, 44)))
                    g.FillEllipse(br, x, y + lineH * 0.5f - 1.5f, 3, 3);

                var rect = new Rectangle((int)(x + 6), (int)y, (int)(w - 6), (int)Math.Ceiling(lineH));
                Color fc = isToday
                    ? Color.FromArgb(255, 250, 250, 252)
                    : night ? Color.FromArgb(150, 186, 226) : Theme.Blend(T.Text, T.Bg, 0.14);
                TextRenderer.DrawText(g, s, f, rect, fc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

                y += lineH;
                drawn++;
            }

            // 还有没画完的，末尾点三个点
            if (drawn < all.Count && y + lineH <= bottom + 2)
            {
                using var br = new SolidBrush(Theme.Blend(T.Text, T.Bg, 0.55));
                g.DrawString("…", f, br, new PointF(x + 6, y));
            }

            g.Restore(st);
        }

        // ================================================================ 日详情
        private void EnsureOvLayout(Graphics g)
        {
            if (_ovLayoutW == _ovPanel.Width && _ovHeights.Count == _ovItems.Count) return;
            _ovLayoutW = _ovPanel.Width;
            _ovHeights.Clear();

            float w = _ovPanel.Width - 34;
            using var fBody = P5R.F(11.5f);
            using var fHead = P5R.F(10.5f, FontStyle.Bold);

            foreach (var it in _ovItems)
            {
                if (it.Title) { _ovHeights.Add(24f); continue; }
                float h;
                if (it.Night && it.Text.Length == 0) h = 16f;
                else
                {
                    var sz = g.MeasureString(it.Text, fBody, (int)Math.Max(40, w), StringFormat.GenericTypographic);
                    h = Math.Max(18f, sz.Height + 4f);
                }
                _ovHeights.Add(h);
            }

            float total = 6;
            foreach (var h in _ovHeights) total += h;
            _ovContentH = total + 20;
            ClampOv();
        }

        private void PaintDay(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                g.FillRectangle(b, ClientRectangle);

            // 先定宽度，按内容算高度 —— 只有一两条时不要撑满整页
            _ovPanel = new Rectangle(8, 8, Math.Max(120, ClientRectangle.Width - 16), 200);
            EnsureOvLayout(g);

            int wantH = 58 + (int)_ovContentH + 20;
            int maxH = Math.Max(120, ClientRectangle.Height - 16);
            _ovPanel.Height = Math.Min(maxH, Math.Max(132, wantH));
            _ovPanel.Y = Math.Max(8, (ClientRectangle.Height - _ovPanel.Height) / 2);
            _ovLayoutW = -1;                 // 高度变了，重排一次
            EnsureOvLayout(g);

            if (_ovPanel.Width < 150 || _ovPanel.Height < 120) { _ov = Overlay.None; return; }

            using (var lg = new LinearGradientBrush(
                new Rectangle(_ovPanel.X, _ovPanel.Y, Math.Max(2, _ovPanel.Width), Math.Max(2, _ovPanel.Height)),
                Theme.Blend(T.Bg, P5R.Ink, 0.50), Theme.Blend(T.Bg, P5R.Ink, 0.88), 90f))
                g.FillRectangle(lg, _ovPanel);

            P5R.Halftone(g, new RectangleF(_ovPanel.X, _ovPanel.Bottom - 80, _ovPanel.Width, 80),
                         Color.FromArgb(16, T.Text), 7, 1f);
            using (var pen = new Pen(Color.FromArgb(120, T.Accent), 1.5f))
                g.DrawRectangle(pen, _ovPanel.X, _ovPanel.Y, _ovPanel.Width, _ovPanel.Height);

            // 头部
            var head = new RectangleF(_ovPanel.X, _ovPanel.Y, _ovPanel.Width, 54);
            using (var p = P5R.Skew(new RectangleF(head.X, head.Y, head.Width, 48), -18f))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(head.X, head.Y, Math.Max(2f, head.Width), 48f), P5R.RedHot, P5R.Red, 0f);
                g.FillPath(lg, p);
            }
            P5R.Halftone(g, new RectangleF(head.X, head.Y, head.Width, 48), Color.FromArgb(38, Color.White), 6, 1.1f);

            string wd = "日一二三四五六"[(int)_ovDate.DayOfWeek].ToString();
            using (var f = P5R.F(19f, FontStyle.Bold))
                P5R.Text(g, $"{_ovDate.Month} 月 {_ovDate.Day} 日", f,
                    new RectangleF(head.X + 18, head.Y + 2, _ovPanel.Width - 90, 40),
                    P5R.Paper, StringAlignment.Near, StringAlignment.Center, -0.16f, true);

            string sub = "星期" + wd;
            bool isToday = _hasDate && _ovDate.Date == _today.Date;
            if (isToday && _tz >= 0) sub += " · 现在 " + Snapshot.TimeZoneText(_tz);
            using (var f = P5R.F(10.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, sub, f,
                    new Rectangle((int)head.X + 20, (int)head.Y + 30, _ovPanel.Width - 70, 18),
                    Color.FromArgb(238, 232, 228),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            _ovClose = new Rectangle(_ovPanel.Right - 32, _ovPanel.Y + 10, 21, 21);
            P5R.Slab(g, _ovClose, Color.FromArgb(230, P5R.Ink), Color.FromArgb(120, P5R.Wire));
            using (var pen = new Pen(P5R.Paper, 1.7f))
            {
                g.DrawLine(pen, _ovClose.X + 6, _ovClose.Y + 6, _ovClose.Right - 6, _ovClose.Bottom - 6);
                g.DrawLine(pen, _ovClose.Right - 6, _ovClose.Y + 6, _ovClose.X + 6, _ovClose.Bottom - 6);
            }

            // 内容
            var list = new Rectangle(_ovPanel.X + 2, _ovPanel.Y + 58, _ovPanel.Width - 4, _ovPanel.Height - 58 - 22);
            EnsureOvLayout(g);

            var st = g.Save();
            g.SetClip(list, CombineMode.Intersect);

            float y = list.Y + 4 - _ovScroll;
            using var fBody = P5R.F(11.5f);
            using var fTitle = P5R.F(10.5f, FontStyle.Bold);

            for (int i = 0; i < _ovItems.Count && i < _ovHeights.Count; i++)
            {
                var it = _ovItems[i];
                float h = _ovHeights[i];
                if (y + h > list.Y && y < list.Bottom)
                {
                    if (it.Title)
                    {
                        var bar = new RectangleF(list.X + 8, y + 4, Math.Min(120, list.Width * 0.45f), 16);
                        P5R.Slab(g, bar, Color.FromArgb(220, P5R.Ink), Color.FromArgb(70, P5R.Wire));
                        P5R.Slab(g, new RectangleF(bar.X + 5, bar.Y + 4, 3, 8),
                                 it.Night ? Color.FromArgb(120, 176, 230) : T.Accent);
                        using var f = fTitle;
                        TextRenderer.DrawText(g, it.Text, f,
                            new Rectangle((int)bar.X + 13, (int)bar.Y, (int)bar.Width - 16, (int)bar.Height),
                            it.Night ? Color.FromArgb(176, 208, 236) : Theme.Blend(T.Text, T.Bg, 0.10),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    else if (it.Text.Length > 0)
                    {
                        using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
                        using var b = new SolidBrush(it.Night ? Color.FromArgb(196, 216, 238) : Theme.Blend(T.Text, T.Bg, 0.08));
                        g.DrawString(it.Text, fBody, b, new RectangleF(list.X + 18, y + 1, list.Width - 30, h), sf);
                    }
                }
                y += h;
            }
            g.Restore(st);

            if (_ovPlan == null || _ovPlan.Empty)
            {
                using var f = P5R.F(12f);
                P5R.Text(g, "这一天没有安排（剧情 / 休息）", f,
                    new RectangleF(list.X, list.Y + list.Height * 0.38f, list.Width, 30),
                    Theme.Blend(T.Text, T.Bg, 0.42), StringAlignment.Center, StringAlignment.Center, 0f, false);
            }

            DrawOvScroll(g, list);

            using (var f = P5R.F(9.5f))
                TextRenderer.DrawText(g, "滚轮滚动　·　点面板外或 ✕ 关闭", f,
                    new Rectangle(_ovPanel.X + 14, _ovPanel.Bottom - 20, _ovPanel.Width - 28, 16),
                    Theme.Blend(T.Text, T.Bg, 0.52),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawOvScroll(Graphics g, Rectangle list)
        {
            float max = _ovContentH - list.Height;
            if (max <= 2) return;
            int trackH = list.Height - 8;
            int thumbH = Math.Max(26, (int)(list.Height / _ovContentH * trackH));
            int y = list.Y + 4 + (int)(_ovScroll / max * (trackH - thumbH));
            var tb = new RectangleF(list.Right - 6, y, 3, thumbH);
            using var p = P5R.Skew(tb, -2f);
            P5R.Fill(g, p, Color.FromArgb(200, T.Text));
        }

        // ================================================================ 月份选择
        private void PaintPicker(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                g.FillRectangle(b, ClientRectangle);

            int pw = Math.Min(Width - 20, 330);
            int ph = Math.Min(Height - 20, 260);
            _ovPanel = new Rectangle((Width - pw) / 2, (Height - ph) / 2, pw, ph);

            using (var lg = new LinearGradientBrush(
                new Rectangle(_ovPanel.X, _ovPanel.Y, Math.Max(2, _ovPanel.Width), Math.Max(2, _ovPanel.Height)),
                Theme.Blend(T.Bg, P5R.Ink, 0.50), Theme.Blend(T.Bg, P5R.Ink, 0.90), 90f))
                g.FillRectangle(lg, _ovPanel);
            using (var pen = new Pen(Color.FromArgb(120, T.Accent), 1.5f))
                g.DrawRectangle(pen, _ovPanel.X, _ovPanel.Y, _ovPanel.Width, _ovPanel.Height);

            var head = new RectangleF(_ovPanel.X, _ovPanel.Y, _ovPanel.Width, 34);
            using (var p = P5R.Skew(new RectangleF(head.X, head.Y, head.Width - 10, 30), -16f))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(head.X, head.Y, Math.Max(2f, head.Width), 30f), P5R.RedHot, P5R.Red, 0f);
                g.FillPath(lg, p);
            }
            using (var f = P5R.F(13f, FontStyle.Bold))
                P5R.Text(g, "选择月份", f, new RectangleF(head.X + 16, head.Y, 160, 30),
                    P5R.Paper, StringAlignment.Near, StringAlignment.Center, -0.16f, true);

            int cols = 4, rows = 3;
            float padX = 12, padY = 42;
            float cw = (_ovPanel.Width - padX * 2) / cols;
            float ch = (_ovPanel.Height - padY - 10) / rows;

            for (int i = 0; i < 12; i++)
            {
                int m = i + 4 <= 12 ? i + 4 : i - 8;
                int y0 = m >= 4 ? 2016 : 2017;
                var r = new RectangleF(_ovPanel.X + padX + (i % cols) * cw + 3,
                                       _ovPanel.Y + padY + (i / cols) * ch + 3,
                                       cw - 6, ch - 6);
                _pickRects[i] = r;

                bool cur = (m == _month && y0 == _year);
                bool hov = i == _pickHover;
                var bg = cur ? Color.FromArgb(230, P5R.Red)
                        : hov ? Color.FromArgb(240, P5R.Slate)
                        : Color.FromArgb(180, P5R.SlabLo);
                P5R.Slab(g, r, bg, Color.FromArgb(cur ? 160 : 70, cur ? Color.White : P5R.Wire));

                using var f = P5R.F(Math.Min(15f, ch * 0.34f), FontStyle.Bold);
                TextRenderer.DrawText(g, m + " 月", f, Rectangle.Round(r),
                    cur ? P5R.Paper : Theme.Blend(T.Text, T.Bg, 0.15),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                using var f2 = P5R.F(8.5f);
                TextRenderer.DrawText(g, y0.ToString(), f2,
                    new Rectangle((int)r.X, (int)r.Bottom - 14, (int)r.Width, 12),
                    cur ? Color.FromArgb(240, 230, 232) : Theme.Blend(T.Text, T.Bg, 0.60),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
