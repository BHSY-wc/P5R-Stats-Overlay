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
        }

        public bool IsOpen { get; private set; }

        private string _title = "";
        private string _sub = "";
        private string _empty = "";
        private bool _statMode;
        private int _cur;

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<float> _h = new List<float>();

        private int _scroll;
        private float _contentH;
        private int _layoutW = -1;

        private Rectangle _panel, _listArea, _closeRect;

        // ---------------------------------------------------------------- 打开
        public void OpenCoop(int cid, string name, string arcana, int rank)
        {
            _statMode = false;
            _title = name;
            _sub = arcana + " · Rank " + rank;
            _cur = rank;

            _rows.Clear();
            var ab = GuideData.Coop(cid);
            foreach (var a in ab)
            {
                bool story = a.Rank > 10;
                _rows.Add(new Row
                {
                    Lv = a.Rank,
                    Name = a.Name,
                    Desc = a.Desc,
                    On = story ? rank >= 10 : a.Rank <= rank,
                    Story = story,
                });
            }
            _empty = ab.Length == 0 ? "这个 Coop 没有战斗能力，随剧情推进" : "";
            Reset();
        }

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

            int headH = 56, footH = 22;
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
                using var f = P5R.F(12.5f);
                P5R.Text(g, _empty, f, new RectangleF(_listArea.X, _listArea.Y + _listArea.Height * 0.4f,
                    _listArea.Width, 30), Theme.Blend(T.Text, T.Bg, 0.40),
                    StringAlignment.Center, StringAlignment.Center, 0f, false);
            }

            DrawScroll(g, T);

            using (var f = P5R.F(9.5f))
                TextRenderer.DrawText(g, _statMode ? "滚轮滚动　·　点空白处关闭" : "滚轮滚动　·　点空白处关闭",
                    f, new Rectangle(_panel.X + 14, _panel.Bottom - footH + 3, _panel.Width - 28, footH - 4),
                    Theme.Blend(T.Text, T.Bg, 0.52),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawHead(Graphics g, Theme T)
        {
            var head = new RectangleF(_panel.X, _panel.Y, _panel.Width, 56);

            // 出血红斜条
            using (var p = P5R.Skew(new RectangleF(head.X, head.Y, head.Width, 50), -18f))
            {
                using var lg = new LinearGradientBrush(
                    new RectangleF(head.X, head.Y, Math.Max(2f, head.Width), 50f),
                    P5R.RedHot, P5R.Red, 0f);
                g.FillPath(lg, p);
            }
            P5R.Halftone(g, new RectangleF(head.X, head.Y, head.Width, 50), Color.FromArgb(40, Color.White), 6, 1.1f);

            using (var f = P5R.F(Math.Min(21f, _panel.Width * 0.085f + 9f), FontStyle.Bold))
                P5R.Text(g, _title, f, new RectangleF(head.X + 20, head.Y + 4, _panel.Width - 120, 42),
                    P5R.Paper, StringAlignment.Near, StringAlignment.Center, -0.16f, true);

            if (_sub.Length > 0)
            {
                using var f2 = P5R.F(10.5f, FontStyle.Bold);
                var sz = g.MeasureString(_title, P5R.F(21f, FontStyle.Bold), 1000);
                float x = Math.Min(head.X + 24 + sz.Width, _panel.Right - 120);
                TextRenderer.DrawText(g, _sub, f2,
                    new Rectangle((int)x + 6, _panel.Y + 16, 104, 20), Color.FromArgb(236, 232, 228),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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

            // 分隔线
            using (var pen = new Pen(Color.FromArgb(70, T.Accent), 2f))
                g.DrawLine(pen, _panel.X + 8, _panel.Y + 52, _panel.Right - 8, _panel.Y + 52);
        }

        private void EnsureLayout(Graphics g)
        {
            int w = _listArea.Width - 26;
            if (_layoutW == w && _h.Count == _rows.Count) return;

            _layoutW = w;
            _h.Clear();

            using var fName = P5R.F(13.5f, FontStyle.Bold);
            using var fDesc = P5R.F(11.2f);

            float total = 8;
            foreach (var r in _rows)
            {
                float h;
                if (r.Desc.Length == 0) h = 28f;
                else
                {
                    // 不设 NoWrap：按可用宽度折行，长描述不裁切
                    float dh = g.MeasureString(r.Desc, fDesc, Math.Max(60, w - 62)).Height;
                    dh = Math.Max(15f, dh);
                    h = 25f + dh + 9f;
                }
                _h.Add(h);
                total += h;
            }
            _contentH = total;
            ClampScroll();
        }

        private void DrawRow(Graphics g, Theme T, Row r, float x, float y, float w, float h)
        {
            bool on = r.On;

            // 左侧状态竖条
            var tick = new RectangleF(x, y + 5, 4, h - 12);
            if (h > 18)
                P5R.Slab(g, tick, r.Story ? Color.FromArgb(190, 226, 176, 64) : (on ? T.Accent : Color.FromArgb(80, P5R.Wire)));

            // 等级徽章
            float bw = _statMode ? 44f : 40f;
            var badge = new RectangleF(x + 10, y + 4, bw, 17);
            string bt = _statMode ? ("Lv" + r.Lv) : (r.Story ? "剧情" : "R" + r.Lv);

            Color badgeFill, badgeText;
            if (r.Story) { badgeFill = Color.FromArgb(226, 176, 64); badgeText = P5R.Ink; }
            else if (on) { badgeFill = T.Accent; badgeText = P5R.Paper; }
            else { badgeFill = Color.FromArgb(150, P5R.SlabLo); badgeText = Color.FromArgb(150, P5R.Ash); }

            P5R.Slab(g, badge, badgeFill, Color.FromArgb(60, P5R.Wire));
            using (var f = P5R.F(Math.Min(10.5f, badge.Height * 0.66f), FontStyle.Bold))
                TextRenderer.DrawText(g, bt, f, Rectangle.Round(badge), badgeText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            float tx = badge.Right + 10;
            float tw = Math.Max(40f, x + w - tx - 4);

            // 名称
            Color nameColor = on ? T.Text : Theme.Blend(T.Text, T.Bg, 0.66);
            if (r.Story) nameColor = Theme.Blend(T.Text, P5R.Ink, 0.35);

            using (var f = P5R.F(13.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, r.Name, f,
                    new Rectangle((int)tx, (int)y + 2, (int)tw, 22), nameColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // 描述
            if (r.Desc.Length > 0)
            {
                Color dc = on ? Theme.Blend(T.Text, T.Bg, 0.22) : Theme.Blend(T.Text, T.Bg, 0.68);
                using var f = P5R.F(11.2f);
                var box = new RectangleF(tx, y + 25, tw, h - 31);
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
