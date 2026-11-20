using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>
    /// P5R 菜单风格的绘制原语：斜切条、尖角标签、胶囊、半调网点、倾斜文字。
    /// 参考游戏内「饰品 / ACCESSORY」一览：纯黑底 + 出血红 + 硬白，所有方块都是平行四边形。
    /// </summary>
    internal static class P5R
    {
        // ---------------------------------------------------------------- 调色板
        public static readonly Color Ink    = Color.FromArgb(7, 7, 9);
        public static readonly Color Slate  = Color.FromArgb(18, 18, 22);
        public static readonly Color SlabLo = Color.FromArgb(12, 12, 15);
        public static readonly Color Paper  = Color.FromArgb(246, 246, 249);
        public static readonly Color Red    = Color.FromArgb(226, 0, 44);
        public static readonly Color RedHot = Color.FromArgb(255, 44, 80);
        public static readonly Color Ash    = Color.FromArgb(134, 136, 146);
        public static readonly Color Wire   = Color.FromArgb(56, 56, 64);

        /// <summary>所有方块的横向倾角（像素）。负值 = 顶边右移，即 P5R 的「右倒」。</summary>
        public const float Lean = -13f;

        // ---------------------------------------------------------------- 字体
        private static readonly string Fam = PickFamily();

        private static string PickFamily()
        {
            foreach (var n in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans SC", "MS Gothic" })
            {
                try { using (var ff = new FontFamily(n)) { if (ff.Name != null) return n; } }
                catch { }
            }
            return "Arial";
        }

        public static Font F(float px, FontStyle st = FontStyle.Regular)
            => new Font(Fam, px, st, GraphicsUnit.Pixel);

        // ---------------------------------------------------------------- 路径
        /// <summary>平行四边形：整体右倾 lean。</summary>
        public static GraphicsPath Skew(RectangleF r, float lean = Lean)
        {
            float d = Math.Abs(lean) * 0.5f;
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(r.X + d, r.Y),
                new PointF(r.Right + d, r.Y),
                new PointF(r.Right - d, r.Bottom),
                new PointF(r.X - d, r.Bottom),
            });
            p.CloseFigure();
            return p;
        }

        /// <summary>左端带尖角的斜切标签（P5R 选中行的形状）。tip 为尖角伸出量。</summary>
        public static GraphicsPath Tag(RectangleF r, float tip = 9f, float lean = Lean)
        {
            float d = Math.Abs(lean) * 0.5f;
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(r.X + d, r.Y),
                new PointF(r.Right + d, r.Y),
                new PointF(r.Right - d, r.Bottom),
                new PointF(r.X - d + tip, r.Bottom),
                new PointF(r.X - d, r.Y + r.Height * 0.5f),
                new PointF(r.X - d + tip, r.Y),
            });
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(2f, Math.Min(rad * 2f, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------------------------------------------------------- 填充
        public static void Fill(Graphics g, GraphicsPath p, Color c)
        {
            using var b = new SolidBrush(c);
            g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, GraphicsPath p, Color c, float w = 1f)
        {
            using var pen = new Pen(c, w);
            pen.LineJoin = LineJoin.Miter;
            g.DrawPath(pen, p);
        }

        /// <summary>斜切块：填充 + 细边。</summary>
        public static void Slab(Graphics g, RectangleF r, Color fill, Color? edge = null, float lean = Lean)
        {
            using var p = Skew(r, lean);
            Fill(g, p, fill);
            if (edge.HasValue) Stroke(g, p, edge.Value, 1f);
        }

        /// <summary>胶囊（P5R 右上角的白色数量标签）。</summary>
        public static void Pill(Graphics g, RectangleF r, Color fill, string text, Color ink, Font f)
        {
            using (var p = Round(r, r.Height * 0.5f))
            {
                Fill(g, p, fill);
                if (r.Width > 24 && r.Height > 12)
                    Stroke(g, p, Color.FromArgb(70, Ink), 1f);
            }
            TextRenderer.DrawText(g, text, f, Rectangle.Round(r), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        /// <summary>
        /// 平行四边形数值框：P5R 里所有数字标签都是斜切块，没有圆角。
        /// edge 传 Color.Transparent 就不描边。
        /// </summary>
        public static void Chip(Graphics g, RectangleF r, Color fill, Color edge, string text, Color ink, Font f,
                                float lean = Lean)
        {
            using var p = Skew(r, lean);
            Fill(g, p, fill);
            if (edge.A > 0) Stroke(g, p, edge, 1f);
            TextRenderer.DrawText(g, text, f, Rectangle.Round(r), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        /// <summary>小号「▸」箭头，用于「还差 N ▸ 下一级」这种串联标签。</summary>
        public static void Arrow(Graphics g, RectangleF r, Color c)
        {
            float h = r.Height * 0.62f, w = Math.Min(r.Width, h * 0.85f);
            float cx = r.X + r.Width * 0.5f, cy = r.Y + r.Height * 0.5f;
            using var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(cx - w * 0.5f, cy - h * 0.5f),
                new PointF(cx + w * 0.5f, cy),
                new PointF(cx - w * 0.5f, cy + h * 0.5f),
            });
            p.CloseFigure();
            Fill(g, p, c);
        }

        // ---------------------------------------------------------------- 半调网点
        private static readonly Dictionary<int, TextureBrush> Dots = new Dictionary<int, TextureBrush>();

        public static TextureBrush DotBrush(Color c, int step = 7, float rad = 1.15f)
        {
            int key = c.ToArgb();
            if (Dots.TryGetValue(key, out var hit)) return hit;

            var bmp = new Bitmap(step, step);
            using (var gg = Graphics.FromImage(bmp))
            {
                gg.SmoothingMode = SmoothingMode.AntiAlias;
                gg.Clear(Color.Transparent);
                using var b = new SolidBrush(c);
                gg.FillEllipse(b, step * 0.5f - rad, step * 0.5f - rad, rad * 2f, rad * 2f);
            }
            var tb = new TextureBrush(bmp) { WrapMode = WrapMode.Tile };
            Dots[key] = tb;
            return tb;
        }

        /// <summary>在指定矩形里铺半调网点（会裁剪，不越界）。</summary>
        public static void Halftone(Graphics g, RectangleF r, Color c, int step = 7, float rad = 1.15f)
        {
            if (r.Width < 2 || r.Height < 2) return;
            var st = g.Save();
            g.SetClip(r, CombineMode.Intersect);
            g.FillRectangle(DotBrush(c, step, rad), r);
            g.Restore(st);
        }

        // ---------------------------------------------------------------- 倾斜文字
        /// <summary>
        /// 右倒文字：P5R 的标题/大字都是斜的。用 shear 矩阵而不是斜体字库，中文字形更稳。
        /// </summary>
        public static void Text(Graphics g, string s, Font f, RectangleF box, Color c,
                                StringAlignment h = StringAlignment.Near,
                                StringAlignment v = StringAlignment.Center,
                                float shear = -0.16f, bool shadow = true)
        {
            var st = g.Save();
            var old = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // 以盒子左中为锚点做 shear，避免整体位移过大
            g.TranslateTransform(box.X, box.Y + box.Height * 0.5f);
            g.MultiplyTransform(new Matrix(1, 0, shear, 1, 0, 0));
            g.TranslateTransform(-box.X, -(box.Y + box.Height * 0.5f));

            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = h,
                LineAlignment = v,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };

            if (shadow)
            {
                using var sb = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                g.DrawString(s, f, sb, new RectangleF(box.X + 1.5f, box.Y + 2f, box.Width, box.Height), sf);
            }
            using (var b = new SolidBrush(c))
                g.DrawString(s, f, b, box, sf);

            g.TextRenderingHint = old;
            g.Restore(st);
        }

        // ---------------------------------------------------------------- 缓动
        public static float EaseOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float u = 1f - t;
            return 1f - u * u * u;
        }

        public static float EaseInOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f;
        }

        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>给文字挑一个能在给定底色上读得清的前景色。</summary>
        public static Color On(Color bg)
        {
            int l = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000;
            return l > 140 ? Ink : Paper;
        }
    }
}
