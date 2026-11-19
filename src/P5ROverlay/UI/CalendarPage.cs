using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>
    /// 日历页：
    ///   月视图 —— 整月格子，展开时每格带当天事项（文字自动换行），点某天弹大号详情层。
    ///   日视图 —— 整页只显示某一天，字号更大，‹ › 切前后一天，「月历」退回月视图。
    /// </summary>
    public sealed class CalendarPage : PageBase
    {
        private const int BarH = 30;
        private const int InfoH = 18;
        private const int WeekH = 21;

        // ---- 日详情弹层（月视图里点某一天）
        //      上一版正文 11.5px，实在太小，这里整体放到 2 倍左右，面板随之撑大
        private const float PopBody = 21f;
        private const float PopHead = 17.5f;
        private const float PopTitleH = 31f;
        private const int PopHeadH = 80;
        private const int PopFootH = 30;

        // ---- 整页日视图
        private const float DayBody = 20f;
        private const float DayHead = 15f;
        private const float DayHeadH = 36f;

        private enum View { Month, Day }
        private enum Ov { None, MonthPick, DayPopup, DayPick }

        // ---- 月视图状态
        private int _year = CalendarData.Year0;
        private int _month = CalendarData.FirstMonth;
        private bool _expand = true;          // true = 每格带事项
        private bool _follow = true;          // 自动跟随游戏内月份

        // ---- 日视图状态
        private View _view = View.Month;
        private DateTime _dayDate = new DateTime(2016, 4, 1);
        private int _dayScroll;
        private float _dayContentH;
        private int _dayLayoutW = -1;
        private int _dayLayoutN = -1;
        private readonly List<(string Text, bool Night, bool Title)> _dayItems = new List<(string, bool, bool)>();
        private readonly List<float> _dayHeights = new List<float>();

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

        private RectangleF _btnPrev, _btnNext, _btnToday, _btnMinus, _btnPlus, _btnTitle, _btnBack;

        // ---- 覆盖层
        private Ov _ov = Ov.None;

        private Rectangle _ovPanel;
        private Rectangle _ovClose;
        private readonly RectangleF[] _pickRects = new RectangleF[12];
        private int _pickHover = -1;

        private DayPlan _ovPlan;
        private DateTime _ovDate;
        private int _ovScroll;
        private float _ovContentH;
        private int _ovLayoutW = -1;
        private int _ovLayoutN = -1;
        private readonly List<(string Text, bool Night, bool Title)> _ovItems = new List<(string, bool, bool)>();
        private readonly List<float> _ovHeights = new List<float>();

        // ---- 日选择（日视图里点日期标题）
        private int _dpYear, _dpMonth;
        private readonly RectangleF[] _dpDayRects = new RectangleF[42];
        private readonly int[] _dpDayNums = new int[42];
        private RectangleF _dpPrev, _dpNext;
        private int _dpHover = -1;

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

        private static DateTime First => new DateTime(2016, 4, 1);
        private static DateTime Last => new DateTime(2017, 3, 31);

        private void Shift(int months)
        {
            var dt = new DateTime(_year, _month, 1).AddMonths(months);
            if (dt < new DateTime(2016, 4, 1) || dt > new DateTime(2017, 3, 1)) return;
            _year = dt.Year;
            _month = dt.Month;
            _follow = false;      // 手动翻过就不再自动跳
            _ov = Ov.None;
            Invalidate();
        }

        // ================================================================ 视图切换
        /// <summary>「今」：整页切到游戏内当天的日程（不是弹小窗）。</summary>
        private void ShowToday()
        {
            if (!_hasDate) return;
            OpenDayPage(_today);
        }

        private void OpenDayPage(DateTime d)
        {
            if (d < First) d = First;
            if (d > Last) d = Last;
            _dayDate = d;
            _view = View.Day;
            _dayScroll = 0;
            _dayLayoutN = -1;
            _ov = Ov.None;
            BuildDayItems();
            Invalidate();
        }

        /// <summary>从日视图退回月视图（回到正在看的那一月的日历）。</summary>
        private void BackToMonth()
        {
            _view = View.Month;
            _year = _dayDate.Year;
            _month = _dayDate.Month;
            _follow = _hasDate && _dayDate.Date == _today.Date;
            _ov = Ov.None;
            Invalidate();
        }

        private void ShiftDay(int days)
        {
            var d = _dayDate.AddDays(days);
            if (d < First || d > Last) return;
            _dayDate = d;
            _dayScroll = 0;
            _dayLayoutN = -1;
            _ov = Ov.None;
            BuildDayItems();
            Invalidate();
        }

        /// <summary>自检钩子：--shot 无游戏时直接指定日历的显示状态。mode 0=月视图 1=日视图 2=日详情弹层。</summary>
        public void DebugView(int mode, int month, int day)
        {
            var d = new DateTime(month >= 4 ? 2016 : 2017, month, day);
            _follow = false;
            _year = d.Year;
            _month = d.Month;
            _ov = Ov.None;
            if (mode == 1) { OpenDayPage(d); return; }
            if (mode == 3) { OpenDayPage(d); OpenDayPick(); return; }
            _view = View.Month;
            if (mode == 2) OpenDay(d);
            Invalidate();
        }

        // ---- 自检：按名字点某个命中区，用来验证按钮几何与状态切换（不需要真鼠标）
        public string DebugPress(string name)
        {
            if (name == "state") return DebugState();

            Invalidate(true);
            Update();                    // 先画一遍，命中区才有值

            Point p;
            if (name == "prev") p = Mid(_btnPrev);
            else if (name == "next") p = Mid(_btnNext);
            else if (name == "today") p = Mid(_btnToday);
            else if (name == "title") p = Mid(_btnTitle);
            else if (name == "back") p = Mid(_btnBack);
            else if (name == "minus") p = Mid(_btnMinus);
            else if (name == "plus") p = Mid(_btnPlus);
            else if (name == "pick4") p = Mid(_pickRects[0]);
            else if (name == "pick7") p = Mid(_pickRects[3]);
            else if (name == "dpnext") p = Mid(_dpNext);
            else if (name == "dpprev") p = Mid(_dpPrev);
            else if (name.StartsWith("dpday"))
            {
                int want = int.Parse(name.Substring(5));
                p = Point.Empty;
                for (int i = 0; i < 42; i++)
                    if (_dpDayNums[i] == want) { p = Mid(_dpDayRects[i]); break; }
            }
            else if (name.StartsWith("cell"))
            {
                int i = int.Parse(name.Substring(4));
                p = Mid(_cells[Math.Min(i, _cells.Count - 1)].R);
            }
            else if (name == "outside") p = new Point(3, Height - 3);
            else return "unknown:" + name;

            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
            Invalidate(true);
            Update();
            return DebugState() + $"  (clicked {p.X},{p.Y})";
        }

        private static Point Mid(RectangleF r) =>
            new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));

        public string DebugState() =>
            $"view={_view} ov={_ov} date={_dayDate:yyyy-MM-dd} month={_year}-{_month:00} " +
            $"expand={_expand} follow={_follow} scroll={_dayScroll} cells={_cells.Count}";

        private void BuildDayItems()
        {
            _dayItems.Clear();
            _dayHeights.Clear();
            _dayLayoutN = -1;

            var plan = CalendarData.Get(_dayDate);
            if (plan == null) return;

            _dayItems.Add(("白天", false, true));
            int n = 0;
            foreach (var s in plan.DayItems) if (s.Length > 0) { _dayItems.Add((s, false, false)); n++; }
            if (n == 0) _dayItems.Add(("（这段没有安排）", false, false));
            _dayItems.Add(("夜晚", true, true));
            n = 0;
            foreach (var s in plan.NightItems) if (s.Length > 0) { _dayItems.Add((s, true, false)); n++; }
            if (n == 0) _dayItems.Add(("（这段没有安排）", true, false));
        }

        // ================================================================ 交互
        protected override bool HitRow(Point p)
        {
            if (_ov != Ov.None) return true;

            if (_view == View.Day)
                return _btnPrev.Contains(p) || _btnNext.Contains(p) || _btnToday.Contains(p) ||
                       _btnTitle.Contains(p) || _btnBack.Contains(p);

            foreach (var c in _cells) if (c.R.Contains(p)) return true;
            return _btnPrev.Contains(p) || _btnNext.Contains(p) || _btnToday.Contains(p) ||
                   _btnMinus.Contains(p) || _btnPlus.Contains(p) || _btnTitle.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_ov == Ov.MonthPick)
            {
                int h = -1;
                for (int i = 0; i < 12; i++) if (_pickRects[i].Contains(e.Location)) { h = i; break; }
                if (h != _pickHover) { _pickHover = h; Invalidate(); }
            }
            else if (_ov == Ov.DayPick)
            {
                int h = -1;
                for (int i = 0; i < 42; i++)
                    if (_dpDayRects[i].Width > 0 && _dpDayRects[i].Contains(e.Location)) { h = i; break; }
                if (h != _dpHover) { _dpHover = h; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            Focus();

            if (_ov != Ov.None)
            {
                HandleOverlayClick(e.Location);
                Invalidate();
                return;
            }

            if (_view == View.Day)
            {
                if (_btnPrev.Contains(e.Location)) { ShiftDay(-1); return; }
                if (_btnNext.Contains(e.Location)) { ShiftDay(1); return; }
                if (_btnToday.Contains(e.Location)) { ShowToday(); return; }
                if (_btnBack.Contains(e.Location)) { BackToMonth(); return; }
                if (_btnTitle.Contains(e.Location)) { OpenDayPick(); return; }
                base.OnMouseDown(e);
                return;
            }

            if (_btnPrev.Contains(e.Location)) { Shift(-1); return; }
            if (_btnNext.Contains(e.Location)) { Shift(1); return; }
            if (_btnToday.Contains(e.Location)) { ShowToday(); return; }
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
            if (_ov == Ov.DayPopup)
            {
                _ovScroll -= e.Delta / 3;
                ClampOv();
                Invalidate();
                return;
            }
            if (_ov == Ov.MonthPick || _ov == Ov.DayPick) return;

            if (_view == View.Day)
            {
                float max = DayScrollMax();
                if (max > 4f)
                {
                    _dayScroll = (int)Math.Max(0, Math.Min(max, _dayScroll - e.Delta / 3));
                    Invalidate();
                }
                else ShiftDay(e.Delta > 0 ? -1 : 1);
                return;
            }

            // 月视图没有可滚动内容，滚轮直接翻月（和手机日历一致）
            Shift(e.Delta > 0 ? -1 : 1);
        }

        // ================================================================ 覆盖层
        private void HandleOverlayClick(Point p)
        {
            if (_ov == Ov.MonthPick)
            {
                if (!_ovPanel.Contains(p)) { _ov = Ov.None; return; }
                for (int i = 0; i < 12; i++)
                {
                    if (!_pickRects[i].Contains(p)) continue;
                    int m = i + 4 <= 12 ? i + 4 : i - 8;
                    _year = m >= 4 ? 2016 : 2017;
                    _month = m;
                    _follow = false;
                    _ov = Ov.None;
                    return;
                }
                return;
            }

            if (_ov == Ov.DayPick)
            {
                if (!_ovPanel.Contains(p)) { _ov = Ov.None; return; }
                if (_dpPrev.Contains(p)) { ShiftPickMonth(-1); return; }
                if (_dpNext.Contains(p)) { ShiftPickMonth(1); return; }
                for (int i = 0; i < 42; i++)
                {
                    if (_dpDayNums[i] <= 0 || !_dpDayRects[i].Contains(p)) continue;
                    var d = new DateTime(_dpYear, _dpMonth, _dpDayNums[i]);
                    OpenDayPage(d);
                    return;
                }
                return;
            }

            // 日详情弹层
            if (_ovClose.Contains(p) || !_ovPanel.Contains(p)) { _ov = Ov.None; return; }
        }

        private void OpenPicker()
        {
            _ov = Ov.MonthPick;
            _pickHover = -1;
            Invalidate();
        }

        private void OpenDayPick()
        {
            _ov = Ov.DayPick;
            _dpYear = _dayDate.Year;
            _dpMonth = _dayDate.Month;
            _dpHover = -1;
            Invalidate();
        }

        private void ShiftPickMonth(int months)
        {
            var dt = new DateTime(_dpYear, _dpMonth, 1).AddMonths(months);
            if (dt < new DateTime(2016, 4, 1) || dt > new DateTime(2017, 3, 1)) return;
            _dpYear = dt.Year;
            _dpMonth = dt.Month;
            _dpHover = -1;
            Invalidate();
        }

        private void OpenDay(DateTime d)
        {
            _ov = Ov.DayPopup;
            _ovDate = d;
            _ovPlan = CalendarData.Get(d);
            _ovScroll = 0;
            _ovLayoutW = -1;
            _ovLayoutN = -1;
            _ovItems.Clear();
            _ovHeights.Clear();

            if (_ovPlan != null)
            {
                _ovItems.Add(("白天", false, true));
                int n = 0;
                foreach (var s in _ovPlan.DayItems) if (s.Length > 0) { _ovItems.Add((s, false, false)); n++; }
                if (n == 0) _ovItems.Add(("（这段没有安排）", false, false));
                _ovItems.Add(("夜晚", true, true));
                n = 0;
                foreach (var s in _ovPlan.NightItems) if (s.Length > 0) { _ovItems.Add((s, true, false)); n++; }
                if (n == 0) _ovItems.Add(("（这段没有安排）", true, false));
            }
            Invalidate();
        }

        private void ClampOv()
        {
            float max = Math.Max(0f, _ovContentH - Math.Max(20, _ovPanel.Height - PopHeadH - PopFootH));
            if (_ovScroll < 0) _ovScroll = 0;
            if (_ovScroll > max) _ovScroll = (int)max;
        }

        private float DayScrollMax()
        {
            int top = BarH + InfoH + 2;
            int listH = Height - 6 - top;
            return Math.Max(0f, _dayContentH - listH);
        }

        // ================================================================ 绘制
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            PaintBackdrop(g);
            _cells.Clear();

            if (_view == View.Day) PaintDayPage(g);
            else PaintMonthPage(g);

            if (_ov == Ov.MonthPick) PaintPicker(g);
            else if (_ov == Ov.DayPopup) PaintDay(g);
            else if (_ov == Ov.DayPick) PaintDayPick(g);
        }

        // ---------------------------------------------------------------- 月视图
        private void PaintMonthPage(Graphics g)
        {
            int infoH = _hasDate ? InfoH : 0;
            LayoutBar(g);

            if (_hasDate) DrawInfoLine(g, BarH, "");

            int gridTop = BarH + infoH;
            DrawWeekHeader(g, gridTop);
            DrawGrid(g, gridTop + WeekH, Height - gridTop - WeekH - 6);
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
            _btnBack = RectangleF.Empty;

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

        private void DrawInfoLine(Graphics g, int y, string right)
        {
            string wd = "日一二三四五六"[(int)_today.DayOfWeek].ToString();
            string tzn = _tz < 0 ? "" : Snapshot.TimeZoneText(_tz);
            string s = _view == View.Day
                ? "现在：" + (tzn.Length > 0 ? tzn : "—")
                : $"游戏内 {_today.Month} 月 {_today.Day} 日（{wd}）"
                  + (tzn.Length > 0 ? " · " + tzn : "")
                  + (_follow ? "" : " · 已暂停跟随");

            using (var f = P5R.F(10.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, s, f,
                    new Rectangle(8, y + 1, Width - (right.Length > 0 ? 236 : 16), InfoH - 2),
                    _follow || _view == View.Day ? Color.FromArgb(214, 255, 226, 232) : Theme.Blend(T.Text, T.Bg, 0.55),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (right.Length > 0)
            {
                using var f2 = P5R.F(10f);
                TextRenderer.DrawText(g, right, f2, new Rectangle(Width - 226, y + 1, 214, InfoH - 2),
                    Theme.Blend(T.Text, T.Bg, 0.58),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
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

        /// <summary>
        /// 展开态：格子里塞当天的条目，白天在前、夜晚在后。
        /// 条目按格子宽度自动换行（以前是单行 EndEllipsis，长条目后半截全被吃掉）；
        /// 格子放不下时只画到边缘，剩下的靠弹层看。
        /// </summary>
        private void DrawCellItems(Graphics g, RectangleF r, DayPlan plan, bool isToday)
        {
            float x = r.X + 4;
            float y = r.Y + 17;
            float w = r.Width - 8;
            float bottom = r.Bottom - 3;
            if (w < 20 || y > bottom) return;

            float lineH = Math.Max(12f, Math.Min(15f, (bottom - y) / 5f));
            using var f = P5R.F(Math.Max(9f, Math.Min(11f, lineH * 0.82f)));

            var all = new List<(string s, bool night)>();
            foreach (var s in plan.DayItems) if (s.Length > 0) all.Add((s, false));
            foreach (var s in plan.NightItems) if (s.Length > 0) all.Add((s, true));
            if (all.Count == 0) return;

            const float indent = 7f;
            float tw = w - indent;
            if (tw < 10) return;

            // 先量一遍：条目换行后各占几行，好决定哪些放得下
            var hs = new float[all.Count];
            float total = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var sz = TextRenderer.MeasureText(g, all[i].s, f, new Size((int)tw, 4000),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                hs[i] = Math.Max(lineH, sz.Height);
                total += hs[i];
            }

            // GDI 量出来的行高普遍比实际画出来的高一点，留半行误差，别为了一两像素就报「还有」
            bool cut = total > bottom - y + lineH * 0.55f;
            float limit = cut ? bottom - lineH - 1f : bottom;   // 放不下就预留一行给省略号

            var st = g.Save();
            g.SetClip(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2));

            float yy = y;
            for (int i = 0; i < all.Count; i++)
            {
                if (yy + hs[i] > limit + 0.5f) { cut = true; break; }

                Color fc = isToday
                    ? Color.FromArgb(255, 250, 250, 252)
                    : all[i].night ? Color.FromArgb(150, 186, 226) : Theme.Blend(T.Text, T.Bg, 0.14);
                Color dot = all[i].night ? Color.FromArgb(130, 180, 232) : Color.FromArgb(226, 0, 44);

                using (var br = new SolidBrush(dot))
                    g.FillEllipse(br, x, yy + lineH * 0.5f - 1.5f, 3f, 3f);

                var rect = new Rectangle((int)(x + indent), (int)yy, (int)tw, (int)Math.Ceiling(hs[i]) + 1);
                TextRenderer.DrawText(g, all[i].s, f, rect, fc,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                yy += hs[i];
            }

            // 还有没放下的：末尾单独一行点个省略号（点开当天可看全）
            if (cut)
            {
                using var fb = P5R.F(Math.Max(9f, lineH * 0.9f), FontStyle.Bold);
                TextRenderer.DrawText(g, "…", fb,
                    new Rectangle((int)(x + indent), (int)(bottom - lineH), (int)tw, (int)lineH + 1),
                    isToday ? Color.FromArgb(235, 255, 240, 244) : Theme.Blend(T.Text, T.Bg, 0.34),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }

            g.Restore(st);
        }

        // ---------------------------------------------------------------- 日视图（整页）
        private void PaintDayPage(Graphics g)
        {
            int w = Width;

            _btnBack = new RectangleF(w - 52, 3, 46, 24);
            _btnToday = new RectangleF(_btnBack.X - 40, 3, 34, 24);
            string wd = WeekCn[(int)_dayDate.DayOfWeek];
            string title = $"{_dayDate.Month} 月 {_dayDate.Day} 日（{wd}）";

            float titleW;
            using (var ft = P5R.F(14f, FontStyle.Bold))
                titleW = TextRenderer.MeasureText(title, ft, new Size(1000, 40),
                                                  TextFormatFlags.NoPadding).Width;

            _btnPrev = new RectangleF(6, 3, 24, 24);
            _btnTitle = new RectangleF(32, 3, Math.Min(Math.Max(110, w - 200), titleW + 36), 24);
            _btnNext = new RectangleF(_btnTitle.Right + 2, 3, 24, 24);
            _btnMinus = _btnPlus = RectangleF.Empty;

            DrawNavBtn(g, _btnPrev, "‹", _dayDate > First);
            DrawNavBtn(g, _btnNext, "›", _dayDate < Last);

            // 日期标题：点开了选「这个月的某一天」
            P5R.Slab(g, _btnTitle, Color.FromArgb(216, P5R.Ink), Color.FromArgb(70, P5R.Wire));
            P5R.Slab(g, new RectangleF(_btnTitle.X + 5, _btnTitle.Y + 5, 3, _btnTitle.Height - 10), T.Accent);
            using (var f = P5R.F(14f, FontStyle.Bold))
                TextRenderer.DrawText(g, title, f,
                    new Rectangle((int)_btnTitle.X + 12, (int)_btnTitle.Y, (int)_btnTitle.Width - 26, (int)_btnTitle.Height),
                    T.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            using (var f = P5R.F(11f))
                TextRenderer.DrawText(g, "▾", f,
                    new Rectangle((int)_btnTitle.Right - 16, (int)_btnTitle.Y, 13, (int)_btnTitle.Height),
                    Theme.Blend(T.Text, T.Bg, 0.45), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            DrawNavBtn(g, _btnToday, "今", _hasDate);
            DrawNavBtn(g, _btnBack, "月历", true);

            if (_hasDate) DrawInfoLine(g, BarH, "滚轮切日 · 点日期选日");

            PaintDayBody(g, BarH + (_hasDate ? InfoH : 0) + 2);
        }

        private void EnsureDayLayout(Graphics g)
        {
            float w = Width - 34;
            if (_dayLayoutW == (int)w && _dayLayoutN == _dayItems.Count) return;
            _dayLayoutW = (int)w;
            _dayLayoutN = _dayItems.Count;
            _dayHeights.Clear();

            using var fBody = P5R.F(DayBody);

            foreach (var it in _dayItems)
            {
                if (it.Title) { _dayHeights.Add(DayHeadH); continue; }
                var sz = g.MeasureString(it.Text, fBody, (int)Math.Max(40, w - 12), StringFormat.GenericTypographic);
                _dayHeights.Add(Math.Max(DayBody + 10f, sz.Height + 8f));
            }

            float total = 8;
            foreach (var h in _dayHeights) total += h;
            _dayContentH = total + 14;
        }

        private void PaintDayBody(Graphics g, int top)
        {
            EnsureDayLayout(g);

            var list = new Rectangle(6, top + 2, Width - 12, Math.Max(20, Height - 6 - top - 2));
            float max = Math.Max(0f, _dayContentH - list.Height);
            if (_dayScroll > max) _dayScroll = (int)max;
            if (_dayScroll < 0) _dayScroll = 0;

            var st = g.Save();
            g.SetClip(list, CombineMode.Intersect);

            float y = list.Y + 4 - _dayScroll;
            using var fBody = P5R.F(DayBody);
            using var fHead = P5R.F(DayHead, FontStyle.Bold);

            for (int i = 0; i < _dayItems.Count && i < _dayHeights.Count; i++)
            {
                var it = _dayItems[i];
                float h = _dayHeights[i];
                if (y + h > list.Y - 4 && y < list.Bottom + 4)
                {
                    if (it.Title)
                    {
                        var bar = new RectangleF(list.X + 4, y + 5, list.Width - 8, DayHeadH - 10);
                        P5R.Slab(g, bar, Color.FromArgb(238, 26, 26, 33), Color.FromArgb(80, P5R.Wire));
                        P5R.Slab(g, new RectangleF(bar.X + 6, bar.Y + 5, 4, bar.Height - 10),
                                 it.Night ? Color.FromArgb(120, 176, 230) : T.Accent);
                        TextRenderer.DrawText(g, it.Text, fHead,
                            new Rectangle((int)bar.X + 18, (int)bar.Y, (int)bar.Width - 24, (int)bar.Height),
                            it.Night ? Color.FromArgb(176, 208, 236) : Theme.Blend(T.Text, T.Bg, 0.08),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    else
                    {
                        // 左侧竖线 + 正文，正文按宽度自动换行
                        using (var b = new SolidBrush(Color.FromArgb(it.Night ? 90 : 60,
                                                                     it.Night ? Color.FromArgb(120, 176, 230) : T.Accent)))
                            g.FillRectangle(b, list.X + 12, y + 5, 3, Math.Max(6f, h - 8f));

                        using var sf = new StringFormat(StringFormat.GenericTypographic)
                        {
                            FormatFlags = StringFormatFlags.LineLimit,
                        };
                        using var br = new SolidBrush(it.Night ? Color.FromArgb(200, 220, 240)
                                                               : Theme.Blend(T.Text, T.Bg, 0.05));
                        g.DrawString(it.Text, fBody, br,
                            new RectangleF(list.X + 24, y + 3, list.Width - 40, h), sf);
                    }
                }
                y += h;
            }
            g.Restore(st);

            // 没有安排
            if (_dayItems.Count == 0)
            {
                using var f = P5R.F(15f);
                P5R.Text(g, "这一天没有安排（剧情 / 休息）", f,
                    new RectangleF(list.X, list.Y + list.Height * 0.34f, list.Width, 40),
                    Theme.Blend(T.Text, T.Bg, 0.42), StringAlignment.Center, StringAlignment.Center, 0f, false);
            }

            DrawDayScroll(g, list);
        }

        private void DrawDayScroll(Graphics g, Rectangle list)
        {
            float max = _dayContentH - list.Height;
            if (max <= 2) return;
            int trackH = list.Height - 8;
            int thumbH = Math.Max(28, (int)(list.Height / _dayContentH * trackH));
            int y = list.Y + 4 + (int)(_dayScroll / max * (trackH - thumbH));
            var tb = new RectangleF(list.Right - 5, y, 3, thumbH);
            using var p = P5R.Skew(tb, -2f);
            P5R.Fill(g, p, Color.FromArgb(200, T.Text));
        }

        // ================================================================ 日详情弹层
        private void EnsureOvLayout(Graphics g)
        {
            if (_ovLayoutW == _ovPanel.Width && _ovLayoutN == _ovItems.Count) return;
            _ovLayoutW = _ovPanel.Width;
            _ovLayoutN = _ovItems.Count;
            _ovHeights.Clear();

            float w = _ovPanel.Width - 46;
            using var fBody = P5R.F(PopBody);

            foreach (var it in _ovItems)
            {
                if (it.Title) { _ovHeights.Add(PopTitleH + 8f); continue; }
                float h;
                if (it.Text.Length == 0) h = PopBody + 6f;
                else
                {
                    var sz = g.MeasureString(it.Text, fBody, (int)Math.Max(40, w), StringFormat.GenericTypographic);
                    h = Math.Max(PopBody + 10f, sz.Height + 8f);
                }
                _ovHeights.Add(h);
            }

            float total = 10;
            foreach (var h in _ovHeights) total += h;
            _ovContentH = total + 18;
            ClampOv();
        }

        private void PaintDay(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                g.FillRectangle(b, ClientRectangle);

            // 先定宽度，按内容算高度 —— 只有一两条时不要撑满整页
            _ovPanel = new Rectangle(8, 8, Math.Max(120, ClientRectangle.Width - 16), 220);
            EnsureOvLayout(g);

            int wantH = PopHeadH + (int)_ovContentH + PopFootH;
            int maxH = Math.Max(160, ClientRectangle.Height - 16);
            _ovPanel.Height = Math.Min(maxH, Math.Max(180, wantH));
            _ovPanel.Y = Math.Max(8, (ClientRectangle.Height - _ovPanel.Height) / 2);
            _ovLayoutW = -1;                 // 高度变了，重排一次
            EnsureOvLayout(g);

            if (_ovPanel.Width < 150 || _ovPanel.Height < 140) { _ov = Ov.None; return; }

            using (var lg = new LinearGradientBrush(
                new Rectangle(_ovPanel.X, _ovPanel.Y, Math.Max(2, _ovPanel.Width), Math.Max(2, _ovPanel.Height)),
                Theme.Blend(T.Bg, P5R.Ink, 0.50), Theme.Blend(T.Bg, P5R.Ink, 0.88), 90f))
                g.FillRectangle(lg, _ovPanel);

            P5R.Halftone(g, new RectangleF(_ovPanel.X, _ovPanel.Bottom - 80, _ovPanel.Width, 80),
                         Color.FromArgb(16, T.Text), 7, 1f);
            using (var pen = new Pen(Color.FromArgb(120, T.Accent), 1.5f))
                g.DrawRectangle(pen, _ovPanel.X, _ovPanel.Y, _ovPanel.Width, _ovPanel.Height);

            // 头部
            var head = new RectangleF(_ovPanel.X, _ovPanel.Y, _ovPanel.Width, PopHeadH);
            using (var p = P5R.Skew(new RectangleF(head.X, head.Y, head.Width, PopHeadH - 6), -18f))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(head.X, head.Y, Math.Max(2f, head.Width), PopHeadH - 6), P5R.RedHot, P5R.Red, 0f);
                g.FillPath(lg, p);
            }
            P5R.Halftone(g, new RectangleF(head.X, head.Y, head.Width, PopHeadH - 6), Color.FromArgb(38, Color.White), 6, 1.1f);

            string wd = WeekCn[(int)_ovDate.DayOfWeek];
            using (var f = P5R.F(27f, FontStyle.Bold))
                P5R.Text(g, $"{_ovDate.Month} 月 {_ovDate.Day} 日", f,
                    new RectangleF(head.X + 20, head.Y + 5, _ovPanel.Width - 110, 54),
                    P5R.Paper, StringAlignment.Near, StringAlignment.Center, -0.16f, true);

            string sub = "星期" + wd;
            bool isToday = _hasDate && _ovDate.Date == _today.Date;
            if (isToday && _tz >= 0) sub += " · 现在 " + Snapshot.TimeZoneText(_tz);
            using (var f = P5R.F(16f, FontStyle.Bold))
                TextRenderer.DrawText(g, sub, f,
                    new Rectangle((int)head.X + 23, (int)head.Y + 48, _ovPanel.Width - 96, 24),
                    Color.FromArgb(238, 232, 228),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            _ovClose = new Rectangle(_ovPanel.Right - 38, _ovPanel.Y + 12, 27, 27);
            P5R.Slab(g, _ovClose, Color.FromArgb(230, P5R.Ink), Color.FromArgb(120, P5R.Wire));
            using (var pen = new Pen(P5R.Paper, 2.2f))
            {
                g.DrawLine(pen, _ovClose.X + 8, _ovClose.Y + 8, _ovClose.Right - 8, _ovClose.Bottom - 8);
                g.DrawLine(pen, _ovClose.Right - 8, _ovClose.Y + 8, _ovClose.X + 8, _ovClose.Bottom - 8);
            }

            // 内容
            var list = new Rectangle(_ovPanel.X + 2, _ovPanel.Y + PopHeadH + 4,
                                     _ovPanel.Width - 4, _ovPanel.Height - PopHeadH - PopFootH);
            EnsureOvLayout(g);

            var st = g.Save();
            g.SetClip(list, CombineMode.Intersect);

            float y = list.Y + 4 - _ovScroll;
            using var fBody = P5R.F(PopBody);
            using var fTitle = P5R.F(PopHead, FontStyle.Bold);

            for (int i = 0; i < _ovItems.Count && i < _ovHeights.Count; i++)
            {
                var it = _ovItems[i];
                float h = _ovHeights[i];
                if (y + h > list.Y && y < list.Bottom)
                {
                    if (it.Title)
                    {
                        var bar = new RectangleF(list.X + 10, y + 6, Math.Min(160, list.Width * 0.5f), PopTitleH - 6);
                        P5R.Slab(g, bar, Color.FromArgb(224, P5R.Ink), Color.FromArgb(70, P5R.Wire));
                        P5R.Slab(g, new RectangleF(bar.X + 6, bar.Y + 6, 4, bar.Height - 12),
                                 it.Night ? Color.FromArgb(120, 176, 230) : T.Accent);
                        TextRenderer.DrawText(g, it.Text, fTitle,
                            new Rectangle((int)bar.X + 18, (int)bar.Y, (int)bar.Width - 22, (int)bar.Height),
                            it.Night ? Color.FromArgb(176, 208, 236) : Theme.Blend(T.Text, T.Bg, 0.08),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    else if (it.Text.Length > 0)
                    {
                        using var sf = new StringFormat(StringFormat.GenericTypographic)
                        {
                            FormatFlags = StringFormatFlags.LineLimit,
                        };
                        using var b = new SolidBrush(it.Night ? Color.FromArgb(196, 216, 238)
                                                              : Theme.Blend(T.Text, T.Bg, 0.06));
                        g.DrawString(it.Text, fBody, b,
                            new RectangleF(list.X + 26, y + 2, list.Width - 46, h), sf);
                    }
                }
                y += h;
            }
            g.Restore(st);

            if (_ovPlan == null || _ovPlan.Empty)
            {
                using var f = P5R.F(17f);
                P5R.Text(g, "这一天没有安排（剧情 / 休息）", f,
                    new RectangleF(list.X, list.Y + list.Height * 0.34f, list.Width, 40),
                    Theme.Blend(T.Text, T.Bg, 0.42), StringAlignment.Center, StringAlignment.Center, 0f, false);
            }

            DrawOvScroll(g, list);

            using (var f = P5R.F(11f))
                TextRenderer.DrawText(g, "滚轮滚动　·　点面板外或 ✕ 关闭", f,
                    new Rectangle(_ovPanel.X + 16, _ovPanel.Bottom - PopFootH + 8, _ovPanel.Width - 32, 18),
                    Theme.Blend(T.Text, T.Bg, 0.52),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawOvScroll(Graphics g, Rectangle list)
        {
            float max = _ovContentH - list.Height;
            if (max <= 2) return;
            int trackH = list.Height - 8;
            int thumbH = Math.Max(28, (int)(list.Height / _ovContentH * trackH));
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

        // ================================================================ 日选择（日视图）
        private void PaintDayPick(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                g.FillRectangle(b, ClientRectangle);

            int pw = Math.Min(Width - 20, 350);
            int ph = Math.Min(Height - 20, 340);
            _ovPanel = new Rectangle((Width - pw) / 2, (Height - ph) / 2, pw, ph);

            using (var lg = new LinearGradientBrush(
                new Rectangle(_ovPanel.X, _ovPanel.Y, Math.Max(2, _ovPanel.Width), Math.Max(2, _ovPanel.Height)),
                Theme.Blend(T.Bg, P5R.Ink, 0.50), Theme.Blend(T.Bg, P5R.Ink, 0.90), 90f))
                g.FillRectangle(lg, _ovPanel);
            using (var pen = new Pen(Color.FromArgb(120, T.Accent), 1.5f))
                g.DrawRectangle(pen, _ovPanel.X, _ovPanel.Y, _ovPanel.Width, _ovPanel.Height);

            // 头部：‹ 2016 年 6 月 ›
            var head = new RectangleF(_ovPanel.X, _ovPanel.Y, _ovPanel.Width, 38);
            using (var p = P5R.Skew(new RectangleF(head.X, head.Y, head.Width, 34), -16f))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(head.X, head.Y, Math.Max(2f, head.Width), 34f), P5R.RedHot, P5R.Red, 0f);
                g.FillPath(lg, p);
            }
            using (var f = P5R.F(14f, FontStyle.Bold))
                P5R.Text(g, "选择日期", f, new RectangleF(head.X + 18, head.Y, 100, 32),
                    P5R.Paper, StringAlignment.Near, StringAlignment.Center, -0.16f, true);

            // 月份文字按实际宽度摆位：以前写死 74px 的框，字一长就往左溢出，被 ‹ 按钮盖掉半个「2」
            string mlab = $"{_dpYear} 年 {_dpMonth} 月";
            float mw;
            using (var fm = P5R.F(13f, FontStyle.Bold))
                mw = TextRenderer.MeasureText(mlab, fm, new Size(2000, 40), TextFormatFlags.NoPadding).Width;
            mw = Math.Min(mw, Math.Max(40f, _ovPanel.Width - 190f));

            _dpNext = new RectangleF(_ovPanel.Right - 12 - 24, _ovPanel.Y + 6, 24, 24);
            _dpPrev = new RectangleF(_dpNext.X - 8 - mw - 8 - 24, _ovPanel.Y + 6, 24, 24);

            using (var f = P5R.F(13f, FontStyle.Bold))
                TextRenderer.DrawText(g, mlab, f,
                    new Rectangle((int)_dpPrev.Right + 6, (int)_dpPrev.Y,
                                  (int)(_dpNext.X - _dpPrev.Right - 12), 24),
                    P5R.Paper, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            var first = new DateTime(2016, 4, 1);
            var last = new DateTime(2017, 3, 1);
            DrawPickNav(g, _dpPrev, "‹", new DateTime(_dpYear, _dpMonth, 1) > first);
            DrawPickNav(g, _dpNext, "›", new DateTime(_dpYear, _dpMonth, 1) < last);

            // 星期行
            int padX = 10;
            float cw = (_ovPanel.Width - padX * 2) / 7f;
            int top = _ovPanel.Y + 46;

            for (int i = 0; i < 7; i++)
            {
                Color c = i == 0 ? Color.FromArgb(236, 96, 118)
                        : i == 6 ? Color.FromArgb(120, 170, 226)
                        : Theme.Blend(T.Text, T.Bg, 0.25);
                using var f = P5R.F(10.5f, FontStyle.Bold);
                TextRenderer.DrawText(g, WeekCn[i], f,
                    new Rectangle((int)(_ovPanel.X + padX + i * cw), top, (int)cw, 16), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            int lead = (int)new DateTime(_dpYear, _dpMonth, 1).DayOfWeek;
            int days = DateTime.DaysInMonth(_dpYear, _dpMonth);
            int rows = (int)Math.Ceiling((lead + days) / 7.0);
            if (rows < 5) rows = 5;

            float gridTop = top + 18;
            float gridH = _ovPanel.Bottom - 10 - gridTop;
            float chh = gridH / rows;

            Array.Clear(_dpDayRects, 0, _dpDayRects.Length);
            Array.Clear(_dpDayNums, 0, _dpDayNums.Length);

            for (int i = 0; i < days; i++)
            {
                int slot = lead + i;
                int col = slot % 7, row = slot / 7;
                var d = new DateTime(_dpYear, _dpMonth, i + 1);
                var r = new RectangleF(_ovPanel.X + padX + col * cw + 2, gridTop + row * chh + 2,
                                       cw - 4, Math.Min(chh - 4, 34));
                int idx = slot;
                if (idx < 42) { _dpDayRects[idx] = r; _dpDayNums[idx] = i + 1; }

                bool sel = d.Date == _dayDate.Date;
                bool today = _hasDate && d.Date == _today.Date;
                bool hov = idx == _dpHover;
                bool has = CalendarData.Has(d.Month, d.Day);

                var bg = sel ? Color.FromArgb(235, P5R.Red)
                        : hov ? Color.FromArgb(240, P5R.Slate)
                        : Color.FromArgb(150, P5R.SlabLo);
                P5R.Slab(g, r, bg, Color.FromArgb(sel ? 160 : today ? 130 : 50,
                                                  sel || today ? Color.White : P5R.Wire));

                using (var f = P5R.F(12f, (sel || today) ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, (i + 1).ToString(), f, Rectangle.Round(r),
                        sel ? P5R.Paper : Theme.Blend(T.Text, T.Bg, has ? 0.10 : 0.42),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawPickNav(Graphics g, RectangleF r, string text, bool enabled)
        {
            var bg = enabled ? Color.FromArgb(220, P5R.Ink) : Color.FromArgb(110, P5R.Ink);
            P5R.Slab(g, r, bg, Color.FromArgb(enabled ? 140 : 50, P5R.Wire));
            using var f = P5R.F(13f, FontStyle.Bold);
            TextRenderer.DrawText(g, text, f, Rectangle.Round(r),
                enabled ? P5R.Paper : Color.FromArgb(120, P5R.Paper),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}
