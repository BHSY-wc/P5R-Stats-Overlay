using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>
    /// 五维的「这一级解锁了什么」。
    /// 数据不是另抄一份，而是从 GameTables.Need 的附加条件里反解出来的
    /// （「勇气2级」「魅力MAX」「灵巧4级」…），所以和好感需求表永远一致。
    /// </summary>
    internal static class StatGateTable
    {
        public struct Gate
        {
            public int Lv;
            public string Text;
            public Gate(int lv, string text) { Lv = lv; Text = text; }
        }

        // 必须与 GameTables.StatNames 同序：[知识, 魅力, 灵巧, 勇气, 体贴]
        private static readonly string[] Keys = { "知识", "魅力", "灵巧", "勇气", "体贴" };

        // 必须声明在 _gates 之前：静态字段按声明顺序初始化，Build() 里要用到它
        /// <summary>
        /// 写在 Wiki「事件基础信息 → 出现条件」里、但没进好感需求表的门槛。
        /// 好感需求表存的是「升级要多少点」，而 Coop 能不能开启另有一套五维条件，两边互补。
        /// </summary>
        private static readonly (int Stat, int Lv, string Text)[] Extra =
        {
            (0, 3, "新岛真 Coop 开启"),     // 金城悔改后，知识 3 级以上
            (3, 4, "岩井宗久 Coop 开启"),   // 5/6 以后，勇气 4 级以上（「大胆无畏」）
        };

        private static readonly List<Gate>[] _gates = Build();

        public static List<Gate> Get(int statIdx)
            => (statIdx >= 0 && statIdx < _gates.Length) ? _gates[statIdx] : new List<Gate>();

        private static List<Gate>[] Build()
        {
            var res = new List<Gate>[5];
            for (int i = 0; i < 5; i++) res[i] = new List<Gate>();

            foreach (var kv in GameTables.Need)
            {
                int cid = kv.Key;
                if (!GameTables.Coops.TryGetValue(cid, out var info)) continue;

                var arr = kv.Value;
                for (int i = 0; i < arr.Length; i++)
                {
                    string extra = arr[i].Extra;
                    if (string.IsNullOrEmpty(extra)) continue;
                    int rank = i + 1;

                    foreach (Match m in Regex.Matches(extra, "(知识|魅力|灵巧|勇气|体贴)(MAX|\\d+)"))
                    {
                        int idx = Array.IndexOf(Keys, m.Groups[1].Value);
                        if (idx < 0) continue;

                        string lvS = m.Groups[2].Value;
                        int lv = lvS == "MAX" ? 5 : int.Parse(lvS);
                        if (lv < 1 || lv > 5) continue;

                        string txt = rank == 1
                            ? info.Name + " Coop 开启"
                            : info.Name + " · R" + rank + "→R" + (rank + 1);
                        res[idx].Add(new Gate(lv, txt));
                    }
                }
            }

            // Wiki「出现条件」里另有、但没写进好感需求表的门槛
            foreach (var e in Extra)
                if (e.Stat >= 0 && e.Stat < 5)
                    res[e.Stat].Add(new Gate(e.Lv, e.Text));

            for (int i = 0; i < 5; i++)
            {
                res[i].Sort((a, b) => a.Lv != b.Lv ? a.Lv.CompareTo(b.Lv) : string.CompareOrdinal(a.Text, b.Text));
                var seen = new HashSet<string>();
                res[i].RemoveAll(x => !seen.Add(x.Lv + "|" + x.Text));
            }
            return res;
        }
    }

    /// <summary>
    /// 页内弹层：点 Coop 行看它在各级解锁了什么能力，点五维项看这一级放开了哪些行动。
    /// 不用新窗口 —— 跟随页面尺寸、跟随主题、跟随缩放，关掉就是原地消失。
    /// </summary>
    public sealed class DetailView
    {
        private struct Row
        {
            public int Lv;
            public string Name;
            public string Desc;
            public bool On;      // 已解锁
            public bool Story;   // 剧情触发（二觉 / 三觉），不看好感
            public bool Love;    // 恋爱线的关键选项
            public bool LoveMust;// 恋人确认项（选错就断在友人线）
            public bool Growth;  // 升级事件后拿到的五维点数
            public bool Daily;   // 特定日期 / 日常活动的五维，排在表尾
            public bool Info;    // 作息页：一行「标签 + 正文」
            public bool Place;   // 景点页：景点标题行
            public bool Best;    // 景点页：该景点好感最高的选项
            public bool Indent;  // 景点页：选项缩进，和景点标题分开层次
            public string Badge;// 徽章上的字；空 = 按类型自动算
            public float NameH;  // 布局时算出的单行/多行名称高度
            public float DescH;  // 布局时算出的描述高度
        }

        public bool IsOpen { get; private set; }

        private string _title = "";
        private string _sub = "";
        private string _empty = "";
        private bool _statMode;
        private int _cur;

        // ---- 好感页的四页：能力 / 礼物 / 作息 / 景点（按钮两行，上排能力·礼物，下排作息·景点）
        private int _coopId;
        private int _tab;                       // 0 能力，1 礼物，2 作息，3 景点
        private RectangleF _btnAb, _btnGift, _btnRoutine, _btnSpot;

        private static readonly string[] TabNames = { "能力", "礼物", "作息", "景点" };

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<float> _h = new List<float>();

        private int _scroll;
        private float _contentH;
        private int _layoutW = -1;

        private Rectangle _panel, _listArea, _closeRect;

        // ---- 字号缩放：好感能力表放大 1.5 倍，五维门槛放大 2 倍（原来一律 11~13px，看着太吃力）
        private float S => _statMode ? 2f : 1.5f;
        private float NamePx => 13.5f * S;
        private float DescPx => 11.2f * S;
        private float BadgeW => (_statMode ? 44f : 40f) * S;
        private float BadgeH => 17f * S;
        private float NameH => NamePx * 1.55f;
        private float TextX => 10f + BadgeW + 8f * S;      // 行内文字起点（相对行左边）

        // ---------------------------------------------------------------- 打开
        public void OpenCoop(int cid, string name, string arcana, int rank)
        {
            _statMode = false;
            _title = name;
            _sub = arcana + " · Rank " + rank;
            _cur = rank;
            _coopId = cid;
            _tab = 0;
            BuildAbilityRows();
            Reset();
        }

        private void BuildAbilityRows()
        {
            _rows.Clear();
            var ab = GuideData.Coop(_coopId);
            var tips = RomanceData.Get(_coopId);

            // 能力和恋爱选项按 Rank 混排：翻到第 N 级时，那一级的「别选错」就在眼前
            var merged = new List<Row>();
            foreach (var a in ab)
            {
                bool story = a.Rank > 10;
                merged.Add(new Row
                {
                    Lv = a.Rank,
                    Name = a.Name,
                    Desc = a.Desc,
                    On = story ? _cur >= 10 : a.Rank <= _cur,
                    Story = story,
                });
            }
            foreach (var t in tips)
            {
                merged.Add(new Row
                {
                    Lv = t.Rank,
                    Name = t.Text,
                    Desc = t.Note,
                    On = t.Rank <= _cur,
                    Love = true,
                    LoveMust = t.Must,
                });
            }

            // 升级事件后拿到的五维：紧跟在该级能力下面（Rank 记的是「升到的那一级」）
            foreach (var s in GuideData.StatUps(_coopId))
            {
                merged.Add(new Row
                {
                    Lv = s.Rank,
                    Name = "♪ " + s.Text,
                    Desc = "",
                    On = s.Rank <= _cur,
                    Growth = true,
                });
            }

            // 不靠升级的（特定日期 / 日常活动）统一排在最后
            foreach (var s in GuideData.Daily(_coopId))
            {
                merged.Add(new Row
                {
                    Lv = 90,
                    Name = s,
                    Desc = "",
                    On = true,
                    Daily = true,
                });
            }

            // 稳定排序：同 Rank 时 能力 → 五维 → 恋爱提示
            var order = new List<int>();
            for (int i = 0; i < merged.Count; i++) order.Add(i);
            order.Sort((i, j) =>
            {
                int c = merged[i].Lv.CompareTo(merged[j].Lv);
                if (c != 0) return c;
                c = Kind(merged[i]).CompareTo(Kind(merged[j]));
                if (c != 0) return c;
                return i.CompareTo(j);   // 同一 Rank 有多条能力时保持数据表里的先后
            });
            foreach (var i in order) _rows.Add(merged[i]);

            if (ab.Length == 0 && tips.Length == 0)
                _empty = "这个 Coop 没有战斗能力，随剧情推进";
            else if (ab.Length == 0)
                _empty = "";
            else
                _empty = "";
        }

        /// <summary>同一 Rank 内的排列顺序：能力 → 五维成长 → 恋爱提示（日常活动恒在表尾）。</summary>
        private static int Kind(Row r) => r.Daily ? 3 : r.Love ? 2 : r.Growth ? 1 : 0;

        /// <summary>礼物页：只列「好感 +50」那一档（普通 5 / 较好 10 / 高级 50）。</summary>
        private void BuildGiftRows()
        {
            _rows.Clear();
            var gs = GiftData.Get(_coopId);
            foreach (var g in gs)
                _rows.Add(new Row
                {
                    Lv = 50,
                    Name = g.Name,
                    Desc = "¥" + g.Price.ToString("N0") + "　·　" + g.Shop,
                    On = true,
                    Story = false,
                });
            _empty = gs.Length == 0 ? "这个 Coop 没有 +50 档的礼物记录" : "";
        }

        private void SetTab(int t)
        {
            if (_statMode || t == _tab) return;
            _tab = t;
            switch (t)
            {
                case 1: BuildGiftRows(); break;
                case 2: BuildRoutineRows(); break;
                case 3: BuildSpotRows(); break;
                default: BuildAbilityRows(); break;
            }
            Reset();
        }

        /// <summary>作息页：什么时候、在哪、什么天气能触发（wiki「事件基础信息」表）。</summary>
        private void BuildRoutineRows()
        {
            _rows.Clear();
            foreach (var it in CoopExtra.Info(_coopId))
                _rows.Add(new Row
                {
                    Lv = 0,
                    Name = it.Value,
                    Desc = "",
                    On = true,
                    Info = true,
                    Badge = it.Label,
                });
            _empty = _rows.Count == 0
                ? "wiki 这个角色的页面没有收录「事件基础信息」"
                : "";
        }

        /// <summary>景点页：约会地点 + 每个选项的好感变化（wiki「景点事件」表）。</summary>
        private void BuildSpotRows()
        {
            _rows.Clear();
            var sp = CoopExtra.Spots(_coopId);

            string curPlace = null, common = "";
            for (int i = 0; i < sp.Length; i++)
            {
                string place = ShortPlace(sp[i].Place);
                bool head = place != curPlace;
                if (head)
                {
                    curPlace = place;
                    // 同一景点的备注全都一样时，只写在标题行，选项行就不用重复三遍
                    common = CommonNote(sp, i, place);
                }

                if (head)
                    _rows.Add(new Row
                    {
                        Lv = 0,
                        Name = place,
                        Desc = common,
                        On = true,
                        Place = true,
                        Badge = sp[i].Place.StartsWith("通用") ? "通用" : "景点",
                    });

                string opt = sp[i].Option;
                if (opt.Length == 0 || opt == "－" || opt == "-") continue;   // 没有选项的分支不单列

                string note = sp[i].Note;
                if (note == "-" || note == "－") note = "";
                if (note == common) note = "";

                _rows.Add(new Row
                {
                    Lv = 0,
                    Name = opt,
                    Desc = note,
                    On = true,
                    Best = sp[i].Best,
                    Indent = true,
                    Badge = sp[i].Gain,
                });
            }

            _empty = sp.Length == 0 ? "这个 Coop 没有景点约会" : "";
        }

        private static string ShortPlace(string p)
        {
            if (p.StartsWith("通用景点：")) p = p.Substring(5);
            else if (p.StartsWith("景点：")) p = p.Substring(3);
            return p;
        }

        /// <summary>该景点所有行的备注完全一致时返回它，否则返回空（备注各不相同就逐行显示）。</summary>
        private static string CommonNote(CoopExtra.Spot[] sp, int from, string place)
        {
            string note = null;
            for (int i = from; i < sp.Length && ShortPlace(sp[i].Place) == place; i++)
            {
                string n = sp[i].Note;
                if (n == "-" || n == "－") n = "";
                if (note == null) note = n;
                else if (note != n) return "";
            }
            return note ?? "";
        }

        /// <summary>自检钩子：切到指定页（0 能力 / 1 礼物）。</summary>
        public void DebugTab(int t) => SetTab(t);

        public void OpenStat(int idx, int lv, int pts)
        {
            _statMode = true;
            _title = GameTables.StatNames[idx];
            _sub = "Lv" + lv + " · " + pts + " 点";
            _cur = lv;

            _rows.Clear();
            foreach (var g in StatGateTable.Get(idx))
                _rows.Add(new Row { Lv = g.Lv, Name = g.Text, Desc = "", On = g.Lv <= lv, Story = false });

            _empty = _rows.Count == 0 ? "这一项目前没有硬性门槛" : "";
            Reset();
        }

        private void Reset()
        {
            _scroll = 0;
            _layoutW = -1;
            _h.Clear();
            IsOpen = true;
        }

        public void Close() { IsOpen = false; }

        // ---------------------------------------------------------------- 交互
        /// <summary>返回 true 表示这次点击被弹层消费掉了（不要继续传给下面的控件）。</summary>
        public bool MouseDown(Point p)
        {
            if (!IsOpen) return false;

            if (!_statMode)
            {
                if (_btnAb.Contains(p)) { SetTab(0); return true; }
                if (_btnGift.Contains(p)) { SetTab(1); return true; }
                if (_btnRoutine.Contains(p)) { SetTab(2); return true; }
                if (_btnSpot.Contains(p)) { SetTab(3); return true; }
            }

            if (_closeRect.Contains(p)) { Close(); return true; }
            if (!_panel.Contains(p)) { Close(); return true; }   // 点面板外 = 关

            return true;   // 点在面板里：吃掉，避免误触到后面的行
        }

        public void Wheel(int delta)
        {
            if (!IsOpen) return;
            _scroll -= delta / 3;
            ClampScroll();
        }

        /// <summary>小键盘 4/6：在「能力 / 礼物 / 作息 / 景点」四页之间切；五维弹层没有页签，返回 false。</summary>
        public bool NextTab(int dir)
        {
            if (!IsOpen || _statMode) return false;
            int t = _tab + dir;
            if (t < 0 || t > 3) return false;
            SetTab(t);
            return true;
        }

        /// <summary>小键盘 8/5：弹层内容上下滚；dy 为 -1 上 / +1 下。</summary>
        public bool ScrollBy(int dy)
        {
            if (!IsOpen || dy == 0) return false;
            _scroll += dy * 46;
            ClampScroll();
            return true;
        }

        /// <summary>自检：按坐标模拟点击页签，走真实命中判定（顺带验证不会误触关闭）。</summary>
        public string DebugClickTab(string which)
        {
            if (!IsOpen) return "弹层未打开";
            if (which == "state")
                return $"打开={IsOpen} 当前页={TabNames[_tab]} 行数={_rows.Count}" +
                       $" 页签=({_btnAb.X:0},{_btnAb.Y:0},{_btnAb.Width:0}x{_btnAb.Height:0})/" +
                       $"({_btnGift.X:0},{_btnGift.Y:0})/({_btnRoutine.X:0},{_btnRoutine.Y:0})/({_btnSpot.X:0},{_btnSpot.Y:0})";

            RectangleF r;
            switch (which)
            {
                case "gift": r = _btnGift; break;
                case "routine": r = _btnRoutine; break;
                case "spot": r = _btnSpot; break;
                default: r = _btnAb; break;
            }
            if (r.Width <= 0 || r.Height <= 0) return "页签矩形还没算出来（没画过）";

            var p = Point.Round(new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f));
            bool hit = MouseDown(p);
            return $"{(hit ? "已消费" : "未消费")} x={p.X} y={p.Y} → 仍打开={IsOpen} 当前页={TabNames[_tab]}";
        }

        private void ClampScroll()
        {
            float max = Math.Max(0f, _contentH - _listArea.Height);
            if (_scroll < 0) _scroll = 0;
            if (_scroll > max) _scroll = (int)max;
        }

        // ---------------------------------------------------------------- 绘制
        public void Paint(Graphics g, Rectangle area, Theme T)
        {
            if (!IsOpen) return;

            using (var b = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                g.FillRectangle(b, area);

            _panel = Rectangle.Inflate(area, -9, -9);
            if (_panel.Width < 140 || _panel.Height < 120) { IsOpen = false; return; }

            // 面板底
            using (var lg = new LinearGradientBrush(
                new Rectangle(_panel.X, _panel.Y, Math.Max(2, _panel.Width), Math.Max(2, _panel.Height)),
                Theme.Blend(T.Bg, P5R.Ink, 0.52), Theme.Blend(T.Bg, P5R.Ink, 0.88), 90f))
                g.FillRectangle(lg, _panel);

            P5R.Halftone(g, new RectangleF(_panel.X, _panel.Bottom - 96, _panel.Width, 96),
                         Color.FromArgb(18, T.Text), 7, 1f);
            using (var pen = new Pen(Color.FromArgb(120, T.Accent), 1.5f))
                g.DrawRectangle(pen, _panel.X, _panel.Y, _panel.Width, _panel.Height);

            DrawHead(g, T);

            int headH = _statMode ? 56 : 80, footH = 22;
            _listArea = new Rectangle(_panel.X + 2, _panel.Y + headH,
                                      _panel.Width - 4, _panel.Height - headH - footH);
            if (_listArea.Height < 40) { IsOpen = false; return; }

            EnsureLayout(g);

            var st = g.Save();
            g.SetClip(_listArea, CombineMode.Intersect);

            float y = _listArea.Y - _scroll + 4;
            for (int i = 0; i < _rows.Count && i < _h.Count; i++)
            {
                float hh = _h[i];
                if (y + hh > _listArea.Y && y < _listArea.Bottom)
                    DrawRow(g, T, _rows[i], _listArea.X + 6, y, _listArea.Width - 20, hh);
                y += hh;
            }

            g.Restore(st);

            if (_empty.Length > 0 && _rows.Count == 0)
            {
                using var f = P5R.F(12.5f * S);
                P5R.Text(g, _empty, f, new RectangleF(_listArea.X, _listArea.Y + _listArea.Height * 0.4f,
                    _listArea.Width, 30), Theme.Blend(T.Text, T.Bg, 0.40),
                    StringAlignment.Center, StringAlignment.Center, 0f, false);
            }

            DrawScroll(g, T);

            // 页脚压一条底色：列表最后一行文字不会和提示糊在一起
            using (var b = new SolidBrush(Theme.Blend(T.Bg, P5R.Ink, 0.9)))
                g.FillRectangle(b, _panel.X + 1, _panel.Bottom - footH, _panel.Width - 2, footH - 1);
            using (var pen = new Pen(Color.FromArgb(50, P5R.Wire)))
                g.DrawLine(pen, _panel.X + 8, _panel.Bottom - footH, _panel.Right - 8, _panel.Bottom - footH);

            using (var f = P5R.F(10f))
                TextRenderer.DrawText(g, _statMode ? "滚轮滚动　·　点空白处关闭"
                                                   : "滚轮滚动　·　4/6 切页　·　点空白处关闭",
                    f, new Rectangle(_panel.X + 14, _panel.Bottom - footH + 3, _panel.Width - 28, footH - 4),
                    Theme.Blend(T.Text, T.Bg, 0.52),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawHead(Graphics g, Theme T)
        {
            // 好感页有四个页签（两行两列），出血条要高一些才装得下
            float bandH = _statMode ? 50f : 74f;
            var head = new RectangleF(_panel.X, _panel.Y, _panel.Width, bandH + 6f);

            // 出血红斜条
            using (var p = P5R.Skew(new RectangleF(head.X, head.Y, head.Width, bandH), -18f))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(head.X, head.Y, Math.Max(2f, head.Width), bandH),
                    P5R.RedHot, P5R.Red, 0f);
                g.FillPath(lg, p);
            }
            P5R.Halftone(g, new RectangleF(head.X, head.Y, head.Width, bandH), Color.FromArgb(40, Color.White), 6, 1.1f);

            // 页签宽度跟着面板走：窄到 MinimumSize 时也得给标题留出字位
            float tabW = _panel.Width >= 420f ? 66f : 52f;
            float tabSpace = _statMode ? 0f : tabW * 2f + 6f + 16f;   // 右上角两列页签占位

            // 标题可用宽度 = 面板宽 − 左缩进 − ✕/页签占位 − 右留白
            float titleMaxW = _panel.Width - 24f - (34f + 12f + tabSpace + 14f);
            if (titleMaxW < 70f) titleMaxW = 70f;

            // P5R.Text 是 NoWrap+NoClip 的，放不下不会自己换行 —— 只能缩字号
            float titlePx = Math.Min(21f + (S - 1f) * 7f, _panel.Width * 0.10f + 9f);
            Font titleFont = P5R.F(titlePx, FontStyle.Bold);
            float titleW = g.MeasureString(_title, titleFont, 4096).Width;
            while (titleW > titleMaxW && titlePx > 12f)
            {
                titlePx -= 0.5f;
                titleFont.Dispose();
                titleFont = P5R.F(titlePx, FontStyle.Bold);
                titleW = g.MeasureString(_title, titleFont, 4096).Width;
            }
            using (titleFont)
                P5R.Text(g, _title, titleFont, new RectangleF(head.X + 20, head.Y + 3, titleMaxW, 44),
                    P5R.Paper, StringAlignment.Near, StringAlignment.Center, -0.16f, true);

            // 副标题（阿卡纳 · Rank）：好感页放到标题下方，五维页靠右贴页签
            if (_sub.Length > 0)
            {
                using var f2 = P5R.F(10.5f + (S - 1f) * 4f, FontStyle.Bold);
                float sw = TextRenderer.MeasureText(g, _sub, f2, new Size(1000, 100),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                if (_statMode)
                {
                    float sx = _panel.Right - 46f - sw - 4f;   // 右对齐到 ✕ 左侧
                    float titleRight = head.X + 24 + titleW * 1.08f;
                    if (sx > titleRight + 10f)
                        TextRenderer.DrawText(g, _sub, f2,
                            new Rectangle((int)sx, _panel.Y + 16, (int)(sw + 4f), 20), Color.FromArgb(236, 232, 228),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
                else
                {
                    float room = _panel.Width - 40f - tabSpace;
                    if (room > 40f)
                        TextRenderer.DrawText(g, _sub, f2,
                            new Rectangle((int)(head.X + 22), (int)(head.Y + 46), (int)Math.Min(room, sw + 6f), 18),
                            Color.FromArgb(236, 232, 228),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }

            // 关闭
            _closeRect = new Rectangle(_panel.Right - 34, _panel.Y + 12, 22, 22);
            P5R.Slab(g, _closeRect, Color.FromArgb(230, P5R.Ink), Color.FromArgb(120, P5R.Wire));
            using (var pen = new Pen(P5R.Paper, 1.8f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(pen, _closeRect.X + 6, _closeRect.Y + 6, _closeRect.Right - 6, _closeRect.Bottom - 6);
                g.DrawLine(pen, _closeRect.Right - 6, _closeRect.Y + 6, _closeRect.X + 6, _closeRect.Bottom - 6);
            }

            // 四个页签：上排「能力 · 礼物」，下排「作息 · 景点」（只有好感页有）
            if (!_statMode)
            {
                float th = 26f, gap = 6f, vgap = 5f;
                float x2 = _closeRect.X - 12 - tabW;          // 右列
                float x1 = x2 - gap - tabW;                    // 左列
                float y1 = _panel.Y + 8, y2 = y1 + th + vgap;

                _btnGift    = new RectangleF(x2, y1, tabW, th);
                _btnAb      = new RectangleF(x1, y1, tabW, th);
                _btnSpot    = new RectangleF(x2, y2, tabW, th);
                _btnRoutine = new RectangleF(x1, y2, tabW, th);

                DrawTab(g, T, _btnAb, TabNames[0], _tab == 0);
                DrawTab(g, T, _btnGift, TabNames[1], _tab == 1);
                DrawTab(g, T, _btnRoutine, TabNames[2], _tab == 2);
                DrawTab(g, T, _btnSpot, TabNames[3], _tab == 3);
            }
            else _btnAb = _btnGift = _btnRoutine = _btnSpot = RectangleF.Empty;

            // 分隔线
            using (var pen = new Pen(Color.FromArgb(70, T.Accent), 2f))
                g.DrawLine(pen, _panel.X + 8, _panel.Y + bandH + 2, _panel.Right - 8, _panel.Y + bandH + 2);
        }

        private static void DrawTab(Graphics g, Theme T, RectangleF r, string text, bool active)
        {
            // 页签压在红色出血条上，激活态若也用红色会跟底色糊在一起 —— 改成纸白色块，
            // 未激活是半透明墨色，一深一亮，一眼能看出现在在哪一页。
            var fill = active ? P5R.Paper : Color.FromArgb(215, P5R.Ink);
            P5R.Slab(g, r, fill, Color.FromArgb(active ? 230 : 90, P5R.Wire));
            using var f = P5R.F(13f, FontStyle.Bold);
            TextRenderer.DrawText(g, text, f, Rectangle.Round(r),
                active ? P5R.Ink : Theme.Blend(P5R.Paper, P5R.Ink, 0.42),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void EnsureLayout(Graphics g)
        {
            int w = _listArea.Width - 26;
            if (_layoutW == w && _h.Count == _rows.Count) return;

            _layoutW = w;
            _h.Clear();

            using var fDesc = P5R.F(DescPx);
            using var fName = P5R.F(NamePx, FontStyle.Bold);
            float wrapW = Math.Max(60f, w - TextX - 4f);

            float total = 8;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                // 名称允许折行：恋爱线的选项是好几个「」串起来的，比一行长
                float nh = Math.Max(NameH, g.MeasureString(r.Name, fName, (int)wrapW).Height);
                float dh = 0f;
                if (r.Desc.Length > 0)
                {
                    dh = g.MeasureString(r.Desc, fDesc, (int)wrapW).Height;
                    dh = Math.Max(DescPx * 1.4f, dh);
                }
                r.NameH = nh;
                r.DescH = dh;
                _rows[i] = r;

                float h = nh + (dh > 0 ? dh + 4f * S : 0f) + 8f * S;
                h = Math.Max(h, BadgeH + 10f * S);
                _h.Add(h);
                total += h;
            }
            _contentH = total;
            ClampScroll();
        }

        private void DrawRow(Graphics g, Theme T, Row r, float x, float y, float w, float h)
        {
            bool on = r.On;

            // 景点页的选项往右缩一点，和景点标题拉出层次
            float ox = r.Indent ? 14f * S : 0f;

            // 左侧状态竖条
            var tick = new RectangleF(x + ox, y + 5 * S, Math.Max(4f, 3f * S), Math.Max(6f, h - 10 * S));
            if (h > 18)
                P5R.Slab(g, tick,
                    r.Love ? Color.FromArgb(r.LoveMust ? 235 : 170, 226, 84, 132)
                           : r.Story ? Color.FromArgb(190, 226, 176, 64)
                                     : r.Daily ? Color.FromArgb(150, 110, 138, 168)
                                               : r.Info ? Color.FromArgb(170, InfoColor(r.Badge))
                                                        : r.Place ? Color.FromArgb(210, 226, 176, 64)
                                                                  : r.Indent ? (r.Best ? Color.FromArgb(230, T.Accent)
                                                                                       : Color.FromArgb(150, 118, 146, 178))
                                                                             : r.Growth ? Color.FromArgb(200, 72, 186, 168)
                                                                                        : (on ? T.Accent : Color.FromArgb(80, P5R.Wire)));

            // 等级徽章
            var badge = new RectangleF(x + 10 + ox, y + 4 * S, BadgeW, BadgeH);
            string bt = !string.IsNullOrEmpty(r.Badge) ? r.Badge
                      : _statMode ? ("Lv" + r.Lv)
                      : (_tab == 1 ? "+50"
                      : (r.Daily ? "活动" : (r.Story ? "剧情" : "R" + r.Lv)));

            if (r.Love)
            {
                // 恋爱线：粉红徽章 + 手绘爱心（不依赖字体里有没有 ♥）
                var loveBg = r.LoveMust
                    ? Color.FromArgb(226, 66, 112)          // 确认项：深粉，选错就断
                    : Color.FromArgb(244, 132, 168);        // 前置条件：浅粉
                P5R.Slab(g, badge, loveBg, Color.FromArgb(60, P5R.Wire));
                float hs = badge.Height * 0.66f;
                DrawHeart(g, new RectangleF(
                    badge.X + (badge.Width - hs) / 2f,
                    badge.Y + (badge.Height - hs) / 2f, hs, hs), P5R.Paper);
            }
            else
            {
                Color badgeFill, badgeText;
                if (r.Info) { badgeFill = Color.FromArgb(225, InfoColor(r.Badge)); badgeText = P5R.Paper; }
                else if (r.Place) { badgeFill = Color.FromArgb(235, 226, 176, 64); badgeText = P5R.Ink; }
                else if (r.Indent) { badgeFill = r.Best ? T.Accent : Color.FromArgb(205, 118, 146, 178); badgeText = P5R.Paper; }
                else if (r.Daily) { badgeFill = Color.FromArgb(215, 118, 146, 178); badgeText = P5R.Paper; }
                else if (r.Growth) { badgeFill = Color.FromArgb(226, 72, 186, 168); badgeText = P5R.Ink; }
                else if (r.Story) { badgeFill = Color.FromArgb(226, 176, 64); badgeText = P5R.Ink; }
                else if (on) { badgeFill = T.Accent; badgeText = P5R.Paper; }
                else { badgeFill = Color.FromArgb(150, P5R.SlabLo); badgeText = Color.FromArgb(150, P5R.Ash); }

                P5R.Slab(g, badge, badgeFill, Color.FromArgb(60, P5R.Wire));
                using (var f = P5R.F(Math.Min(10.5f * S, badge.Height * 0.62f), FontStyle.Bold))
                    TextRenderer.DrawText(g, bt, f, Rectangle.Round(badge), badgeText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            float tx = x + TextX + ox;
            float tw = Math.Max(40f, x + w - tx - 4);
            float nameTop = y + 3f * S;

            // 名称
            Color nameColor = on ? T.Text : Theme.Blend(T.Text, T.Bg, 0.66);
            if (r.Story) nameColor = Theme.Blend(T.Text, P5R.Ink, 0.35);
            if (r.Love) nameColor = Color.FromArgb(r.LoveMust ? 214 : 198, 62, 108);
            if (r.Place) nameColor = Theme.Blend(Color.FromArgb(226, 176, 64), T.Text, 0.18f);
            if (r.Indent && r.Best) nameColor = Theme.Blend(T.Accent, T.Text, 0.22f);
            if (r.Info) nameColor = T.Text;
            if (r.Growth) nameColor = on ? Theme.Blend(Color.FromArgb(56, 176, 158), T.Text, 0.28f)
                                         : Theme.Blend(Theme.Blend(Color.FromArgb(56, 176, 158), T.Text, 0.28f), T.Bg, 0.55f);
            if (r.Daily) nameColor = Theme.Blend(Color.FromArgb(98, 128, 162), T.Text, 0.28f);

            // 作息页的正文是整段说明文字，加粗反而糊，用常规字重
            using (var f = P5R.F(r.Info ? NamePx * 0.96f : NamePx, r.Info ? FontStyle.Regular : FontStyle.Bold))
                TextRenderer.DrawText(g, r.Name, f,
                    new Rectangle((int)tx, (int)nameTop, (int)tw, (int)(r.NameH > 0 ? r.NameH : NameH)), nameColor,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // 描述
            if (r.Desc.Length > 0)
            {
                Color dc = on ? Theme.Blend(T.Text, T.Bg, 0.22) : Theme.Blend(T.Text, T.Bg, 0.68);
                using var f = P5R.F(DescPx);
                float boxH = r.DescH > 0 ? r.DescH : Math.Max(DescPx, h - NameH - 8f * S);
                var box = new RectangleF(tx, nameTop + (r.NameH > 0 ? r.NameH : NameH), tw, boxH);
                using var sf = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near,
                    FormatFlags = StringFormatFlags.LineLimit,
                };
                using var br = new SolidBrush(dc);
                g.DrawString(r.Desc, f, br, box, sf);
            }
        }

        /// <summary>作息页左侧短标签的配色：开启 / 面具偏金，地点 / 时间用主色，天气 / UP / 备注偏蓝灰。</summary>
        private static Color InfoColor(string label)
        {
            switch (label)
            {
                case "开启":
                case "面具": return Color.FromArgb(196, 152, 52);
                case "地点":
                case "时间": return Color.FromArgb(196, 48, 44);
                default: return Color.FromArgb(96, 122, 152);
            }
        }

        /// <summary>
        /// 手绘爱心。用参数方程铺多边形，避免依赖字体里有没有 U+2665：
        /// x = 16sin³t，y = 13cos t − 5cos2t − 2cos3t − cos4t。
        /// </summary>
        private static void DrawHeart(Graphics g, RectangleF r, Color c)
        {
            const int N = 48;
            var pts = new PointF[N];
            for (int i = 0; i < N; i++)
            {
                double t = System.Math.PI * 2 * i / N;
                double x = 16 * System.Math.Pow(System.Math.Sin(t), 3);
                double y = -(13 * System.Math.Cos(t) - 5 * System.Math.Cos(2 * t)
                             - 2 * System.Math.Cos(3 * t) - System.Math.Cos(4 * t));
                pts[i] = new PointF(
                    r.X + (float)((x + 16) / 32) * r.Width,
                    r.Y + (float)((y + 17) / 29) * r.Height);
            }
            using var br = new SolidBrush(c);
            g.FillPolygon(br, pts);
        }

        private void DrawScroll(Graphics g, Theme T)
        {
            float max = _contentH - _listArea.Height;
            if (max <= 1f) return;

            int trackTop = _listArea.Y + 4, trackH = _listArea.Height - 8;
            if (trackH < 24) return;

            float thumbH = Math.Max(30f, trackH * (_listArea.Height / Math.Max(1f, _contentH)));
            float thumbY = trackTop + (_scroll / max) * (trackH - thumbH);

            var tb = new RectangleF(_listArea.Right - 6, thumbY, 3.5f, thumbH);
            using var p = P5R.Skew(tb, -2f);
            P5R.Fill(g, p, Color.FromArgb(200, T.Text));
        }
    }
}
