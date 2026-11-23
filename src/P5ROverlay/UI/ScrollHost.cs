using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace P5ROverlay
{
    /// <summary>
    /// P5R 风滚动容器：内容全放进 Content，滚动条自己画。
    /// 系统那根白底灰滑条没法着色，和整套红黑 UI 打架，所以整根换掉——
    /// 深色斜切轨道 + 强调色滑块 + 三道抓握纹，滚轮 / 拖滑块 / 点轨道都能滚。
    /// 内容不够高时不画条（但宽度仍预留，免得布局跟着抖）。
    /// </summary>
    public sealed class ScrollHost : Panel
    {
        public const int BarW = 11;         // 滚动条总宽
        private const int MinThumb = 26;    // 滑块最短，太短不好抓
        private const int WheelStep = 56;   // 滚轮一格滚多少像素

        /// <summary>真正承载页面控件的容器（往这儿 Add，不要往 Host 里 Add）。</summary>
        public readonly Panel Content;

        private Theme _t;
        private int _offset;
        private int _contentH;
        private int _thumbTop = -1, _thumbLen;
        private bool _dragging;
        private int _dragY0, _dragOff0;

        public ScrollHost()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.UserMouse, true);

            Content = new Panel
            {
                Left = 0,
                Top = 0,
                AutoScroll = false,
                BorderStyle = BorderStyle.None,
            };
            Controls.Add(Content);
        }

        /// <summary>内容可用的宽度（已经扣掉滚动条那一列）。</summary>
        public int ViewportWidth => Math.Max(60, Width - BarW);

        /// <summary>内容总高；每次布局完由调用方写入。</summary>
        public int ContentHeight
        {
            get => _contentH;
            set
            {
                _contentH = Math.Max(0, value);
                Content.Width = ViewportWidth;
                Content.Height = Math.Max(Height, _contentH);
                SetOffset(_offset);
                Invalidate();
            }
        }

        public bool NeedsBar => _contentH > Height + 1;

        public int ScrollOffset => _offset;

        public void SetTheme(Theme t)
        {
            _t = t;
            BackColor = t.Bg2;
            Content.BackColor = t.Bg2;
            Invalidate();
        }

        public void ScrollBy(int dy) => SetOffset(_offset + dy);

        public void SetOffset(int v)
        {
            int max = Math.Max(0, _contentH - Height);
            if (v < 0) v = 0;
            if (v > max) v = max;
            if (v == _offset) return;
            _offset = v;
            Content.Top = -_offset;
            Invalidate();
        }

        /// <summary>这个点是不是落在滚动条那一列上（拖窗口时要避开）。</summary>
        public bool HitBar(Point p) => NeedsBar && p.X >= Width - BarW;

        /// <summary>
        /// 滚轮只在这一层处理：子控件没处理滚轮时，WM_MOUSEWHEEL 会沿父链冒泡上来，
        /// 逐级都挂处理函数就会一次滚好几格。TrackBar 自己吃滚轮（改变数值），那也是我们要的。
        /// </summary>
        private void OnWheel(MouseEventArgs e)
        {
            if (!NeedsBar) return;
            ScrollBy(-(e.Delta / 120) * WheelStep);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Content.Width = ViewportWidth;
            Content.Height = Math.Max(Height, _contentH);
            SetOffset(_offset);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            OnWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !HitBar(e.Location)) return;

            if (e.Y >= _thumbTop && e.Y <= _thumbTop + _thumbLen)
            {
                _dragging = true;
                _dragY0 = e.Y;
                _dragOff0 = _offset;
                Capture = true;
            }
            else
            {
                // 点空白轨道：翻一屏（和 P5R 的列表一样，不是一下跳到底）
                ScrollBy(e.Y < _thumbTop ? -Height * 4 / 5 : Height * 4 / 5);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;

            int travel = Height - 4 - _thumbLen;
            if (travel <= 0) return;
            int max = Math.Max(0, _contentH - Height);
            SetOffset(_dragOff0 + (int)Math.Round((e.Y - _dragY0) * (double)max / travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(_t != null ? _t.Bg2 : BackColor))
                g.FillRectangle(b, ClientRectangle);

            if (!NeedsBar) { _thumbTop = -1; return; }

            Color accent = _t != null ? _t.Accent : Color.FromArgb(226, 0, 44);
            float x = Width - BarW;

            // 和内容区之间的分隔线
            using (var pen = new Pen(Color.FromArgb(70, P5R.Wire)))
                g.DrawLine(pen, x - 0.5f, 1f, x - 0.5f, Height - 2f);

            // 轨道：深色斜切长条
            using (var p = P5R.Skew(new RectangleF(x + 2f, 2f, BarW - 4f, Height - 4f), -4f))
                P5R.Fill(g, p, Color.FromArgb(160, P5R.Ink));

            // 滑块：强调色斜切块，长度按可视比例
            float len = Math.Min(Height - 4f, Math.Max(MinThumb, Height * (float)Height / Math.Max(1, _contentH)));
            int max = Math.Max(1, _contentH - Height);
            float top = 2f + (Height - 4f - len) * (_offset / (float)max);

            var thumb = new RectangleF(x + 1.5f, top, BarW - 3f, len);
            using (var p = P5R.Skew(thumb, -5f))
            {
                P5R.Fill(g, p, accent);
                // 描边用重色而不是白：白边在深底上会跳出来像系统控件
                P5R.Stroke(g, p, Theme.Blend(accent, P5R.Ink, 0.45), 1f);
            }

            // 左沿一条高光，让滑块有厚度（和进度条同一套做法）
            using (var pen = new Pen(Color.FromArgb(90, Color.White), 1f))
                g.DrawLine(pen, x + 2.5f, thumb.Y + 3f, x + 2.5f, thumb.Bottom - 3f);

            // 抓握纹：三道硬横线
            using (var pen = new Pen(Color.FromArgb(170, P5R.Ink), 1.4f))
            {
                float cy = top + len * 0.5f;
                for (int i = -1; i <= 1; i++)
                    g.DrawLine(pen, x + 3.2f, cy + i * 3.4f, x + BarW - 3.2f, cy + i * 3.4f);
            }

            _thumbTop = (int)Math.Round(top);
            _thumbLen = (int)Math.Round(len);
        }
    }
}
