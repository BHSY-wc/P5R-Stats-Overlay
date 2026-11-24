using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace P5ROverlay
{
    public sealed class OverlayForm : Form
    {
        public const string AppVersion = "V1.0";
        public const string AppAuthor = "冰寒石英制作";

        // ---- 页面
        private const int PAGE_STAT = 0, PAGE_COOP = 1, PAGE_KEY = 2, PAGE_CAL = 3, PAGE_LOG = 4, PAGE_SET = 5;
        private const int PageCount = 6;
        private static readonly string[] TabNames = { "五维", "好感", "关键", "日历", "日志", "设置" };

        // ---- 尺寸
        private static readonly Size DefaultCompact = new Size(300, 56);
        private static readonly Size DefaultExpand = new Size(600, 660);
        private static readonly Size MinCompact = new Size(220, 44);
        private static readonly Size MinExpand = new Size(430, 430);   // 高度要够「提示行 + 五行五维」
        private Size _compactSize = DefaultCompact;
        private Size _expandSize = DefaultExpand;

        private const int TAB_H = 28;
        private const int TITLE_H = 32;
        private const int RESIZE_BORDER = 6;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTBOTTOM = 15,
                          HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        // ---- 主题默认值（P5R：纯黑底 + 出血红 + 硬白）
        private static readonly Color DefBg = Color.FromArgb(7, 7, 9);
        private static readonly Color DefText = Color.FromArgb(246, 246, 249);
        private static readonly Color DefAccent = Color.FromArgb(226, 0, 44);

        private Color _cBg = DefBg, _cText = DefText, _cAccent = DefAccent;
        private double _opacity = 1.00;
        private Theme _theme;

        private bool _expanded;
        private int _page = PAGE_STAT;
        private bool _clickThrough;
        private bool _passWantedAtStartup;

        // ---- 数据
        private readonly GameReader _reader = new GameReader();
        private Snapshot _snap;
        private volatile int _refreshSec = 5;
        private Thread _worker;
        private volatile bool _stop;
        private string _status = "启动中…";

        // ---- 控件
        private Label _title, _btnExpand, _btnHide;
        private Label[] _tabs;
        private readonly Rectangle[] _tabRects = new Rectangle[PageCount];
        private StatPage _statPage;
        private CoopPage _coopPage;
        private KeyPage _keyPage;
        private CalendarPage _calPage;
        private RichTextBox _logBox;
        private ScrollHost _logHost;      // 日志也套同一个自绘滚动条，不然一根白条子戳在那儿
        private ScrollHost _settings;
        private Label _statusLabel;
        private NotifyIcon _tray;

        // 设置页控件
        private TrackBar _opacityBar;
        private Label _opacityVal, _capOpacity, _capBg, _capText, _capAccent, _capMouse, _capRefresh, _capKeys, _capNav, _tipLabel, _verLabel, _byLabel;
        private Label[] _bgSw, _fgSw, _acSw;
        private Label _btnCustomBg, _btnCustomText, _btnCustomAcc, _btnReset, _btnMouseMode;
        private Label _hintMouse, _hintFreq;
        private Label[] _refreshBtns;
        private Panel _preview;
        private Label _pvLine1, _pvLine2, _capPreview;

        // 涨点动画（时长 + 高亮色）
        private TrackBar _growBar;
        private Label _growVal, _capAnim, _capGrowColor;
        private Label[] _growSw;
        private Label _btnCustomGrow;

        // ---- 热键
        private const int HK_TOGGLE = 1, HK_EXPAND = 2, HK_PREV = 3, HK_NEXT = 4, HK_PASS = 5, HK_REFRESH = 6;

        // ---- 小键盘导航（面板展开且可交互时才注册，收起后立刻还给游戏）
        private const int HK_NAV_UP = 11, HK_NAV_DOWN = 12, HK_NAV_LEFT = 13,
                          HK_NAV_RIGHT = 14, HK_NAV_OK = 15, HK_NAV_BACK = 16;

        private bool _navRegistered;

        // ---- 方向键长按连发：热键用 MOD_NOREPEAT，系统只报一次按下，
        //      所以按下后自己开表，按「先等 350ms、之后每 70ms 一步」轮询按键是否还按着。
        private const int NavRepeatDelay = 350, NavRepeatStep = 70;
        private readonly System.Windows.Forms.Timer _navRepeat = new System.Windows.Forms.Timer { Interval = 40 };
        private int _navRepeatId, _navRepeatVk, _navRepeatT0, _navRepeatNext;
        private bool _navRepeating;

        private sealed class HotkeySlot
        {
            public int Id;
            public string Caption = "", IniKey = "";
            public uint Mods, Vk;
            public string Text = "";
            public uint DefMods, DefVk;
            public string DefText;
            public Label Btn, Cap;
            public Action OnFire;
            public bool Ok = true;
            /// <summary>小键盘导航键：同样可改键、同样存 ini，但只在「展开 + 可交互」时注册。</summary>
            public bool Nav;
        }

        private readonly List<HotkeySlot> _slots = new List<HotkeySlot>();
        private readonly Dictionary<int, HotkeySlot> _slotById = new Dictionary<int, HotkeySlot>();
        private HotkeySlot _passSlot, _captureSlot;

        // ---- 拖动
        private bool _dragging;
        private Point _dragScreen, _dragForm;
        private bool _applyingMode;

        public static string SettingsPath = Path.Combine(AppContext.BaseDirectory, "p5r_overlay.ini");

        // ================================================================== 构造
        public OverlayForm()
        {
            Text = "P5R 五维人物好感面板 " + AppVersion;
            Icon = LoadAppIcon();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = true;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            MinimumSize = MinCompact;

            BuildHotkeySlots();
            LoadSettings();
            BuildControls();
            BuildTray();
            ApplyTheme();
            ApplyModeSize(false);

            MouseDown += DragStart;
            MouseMove += DragMove;
            MouseUp += DragEnd;

            Log.OnLine += OnLogLine;
            foreach (var ln in Log.Snapshot()) AppendLog(ln);

            StartWorker();
            StartTopMostKeeper();
            StartDateTicker();
            _navRepeat.Tick += NavRepeatTick;
        }

        private const int WS_EX_COMPOSITED = 0x02000000;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_COMPOSITED;
                // 穿透位写进 CreateParams，避免 WinForms 重算窗口样式时把手工塞的样式冲掉
                if (_clickThrough) cp.ExStyle |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED;
                return cp;
            }
        }

        // ================================================================== 颜色工具
        private static Color Blend(Color a, Color b, double t) => Theme.Blend(a, b, t);
        private static string ColToStr(Color c) => $"{c.R},{c.G},{c.B}";
        private static Color ParseCol(string s, Color fb)
        {
            try
            {
                var p = s.Split(',');
                if (p.Length >= 3) return Color.FromArgb(Clamp255(int.Parse(p[0].Trim())),
                    Clamp255(int.Parse(p[1].Trim())), Clamp255(int.Parse(p[2].Trim())));
            }
            catch { }
            return fb;
        }
        private static int Clamp255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        // ================================================================== 控件
        private void BuildControls()
        {
            var fTitle = P5R.F(13.5f, FontStyle.Bold);

            _title = new Label
            {
                Text = "",                       // 文字由 OnPaint 画，避免 GDI ClearType 在白牌上出彩边
                Font = fTitle,
                ForeColor = P5R.Ink,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            HookDrag(_title);
            _title.DoubleClick += (s, e) => ToggleExpand();

            _btnExpand = MakeButton("▾", "展开 / 收起", (s, e) => ToggleExpand());
            _btnHide = MakeButton("✕", "最小化到托盘", (s, e) => HideToTray());

            _tabs = new Label[PageCount];
            for (int i = 0; i < PageCount; i++)
            {
                int idx = i;
                var t = new Label
                {
                    Text = TabNames[i],
                    Font = new Font("Microsoft YaHei UI", 10f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    AutoSize = false,
                    BackColor = Color.Transparent,
                };
                t.MouseEnter += (s, e) => { if (idx != _page) t.ForeColor = _cAccent; };
                t.MouseLeave += (s, e) => UpdateTabStyles();
                t.Click += (s, e) => GoPage(idx);
                _tabs[i] = t;
            }

            _statPage = new StatPage { Visible = false };
            _coopPage = new CoopPage { Visible = false };
            _keyPage = new KeyPage { Visible = false };
            _calPage = new CalendarPage { Visible = false };

            _logBox = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                DetectUrls = false,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.None,   // 滚动交给外面的 ScrollHost
                Font = new Font("Consolas", 9.5f),
                Visible = false,
            };

            // 日志区：RichTextBox 拉成和内容一样高，滚动条由 ScrollHost 自绘
            _logHost = new ScrollHost { Visible = false };
            _logBox.Visible = true;
            _logHost.Content.Controls.Add(_logBox);

            _statusLabel = new Label
            {
                Text = "启动中…",
                Font = new Font("Microsoft YaHei UI", 8.5f),
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            HookDrag(_statusLabel);

            BuildSettingsPanel();

            Controls.AddRange(new Control[] { _statPage, _coopPage, _keyPage, _calPage, _logHost, _settings, _statusLabel, _title, _btnExpand, _btnHide });
            Controls.AddRange(_tabs);
            _settings.BringToFront();
            _statPage.BringToFront();
            _coopPage.BringToFront();
            _keyPage.BringToFront();
            _calPage.BringToFront();
            _logHost.BringToFront();
        }

        private Label MakeButton(string text, string tip, EventHandler onClick)
        {
            var b = new Label
            {
                Text = text,
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                AutoSize = false,
            };
            b.MouseEnter += (s, e) => b.ForeColor = _cAccent;
            b.MouseLeave += (s, e) => b.ForeColor = Blend(_cText, _cBg, 0.32);
            b.Click += onClick;
            var t = new ToolTip();
            t.SetToolTip(b, tip);
            return b;
        }

        private void HookDrag(Control c)
        {
            c.MouseDown += DragStart;
            c.MouseMove += DragMove;
            c.MouseUp += DragEnd;
        }

        // ================================================================== 设置页
        private static readonly Color[] BgPalette =
        {
            Color.FromArgb(18, 22, 28), Color.FromArgb(28, 34, 44), Color.FromArgb(12, 13, 16),
            Color.FromArgb(24, 18, 30), Color.FromArgb(14, 28, 26), Color.FromArgb(38, 30, 22),
            Color.FromArgb(244, 245, 248), Color.FromArgb(226, 232, 240),
        };
        private static readonly Color[] FgPalette =
        {
            Color.FromArgb(226, 233, 242), Color.FromArgb(255, 255, 255), Color.FromArgb(240, 240, 232),
            Color.FromArgb(255, 214, 102), Color.FromArgb(150, 220, 255), Color.FromArgb(255, 180, 200),
            Color.FromArgb(200, 235, 200), Color.FromArgb(170, 175, 185),
        };
        private static readonly Color[] AccentPalette =
        {
            Color.FromArgb(240, 196, 96), Color.FromArgb(255, 120, 96), Color.FromArgb(120, 200, 255),
            Color.FromArgb(140, 230, 170), Color.FromArgb(220, 150, 255), Color.FromArgb(255, 160, 200),
            Color.FromArgb(210, 210, 220), Color.FromArgb(255, 255, 255),
        };

        // 涨点高亮色：要能从红色（普通）/ 绿色（可升级）两种进度条上跳出来，
        // 所以默认给荧光黄，其余也偏高亮；最后一个纯白给喜欢素一点的。
        private static readonly Color[] GrowPalette =
        {
            PageBase.DefGrowTint,
            Color.FromArgb(255, 176, 64), Color.FromArgb(120, 244, 176), Color.FromArgb(96, 216, 255),
            Color.FromArgb(168, 152, 255), Color.FromArgb(255, 128, 168), Color.FromArgb(255, 96, 96),
            Color.FromArgb(255, 255, 255),
        };

        private void BuildSettingsPanel()
        {
            // 自绘滚动条：系统的白条子跟整页配色打架，见 ScrollHost
            _settings = new ScrollHost { Visible = false };
            _settings.MouseDown += DragStart;
            _settings.MouseMove += DragMove;
            _settings.MouseUp += DragEnd;
            _settings.Content.MouseDown += DragStart;
            _settings.Content.MouseMove += DragMove;
            _settings.Content.MouseUp += DragEnd;

            _capOpacity = MakeCap("窗口透明度");
            _capBg = MakeCap("背景颜色");
            _capText = MakeCap("文字颜色");
            _capAccent = MakeCap("强调颜色");
            _capMouse = MakeCap("鼠标交互");
            _capRefresh = MakeCap("刷新频率");
            _capAnim = MakeCap("涨点动画时长");
            _capGrowColor = MakeCap("涨点高亮色");
            _capKeys = MakeCap("快捷键");
            _capNav = MakeCap("小键盘导航（仅展开且可交互时接管）");

            _opacityBar = new TrackBar
            {
                Minimum = 25,
                Maximum = 100,
                Value = (int)Math.Round(_opacity * 100),
                TickStyle = TickStyle.None,
                SmallChange = 1,
                LargeChange = 5,
            };
            _opacityVal = new Label
            {
                Text = _opacityBar.Value + "%",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            };
            _opacityBar.ValueChanged += (s, e) =>
            {
                _opacity = _opacityBar.Value / 100.0;
                Opacity = _opacity;
                _opacityVal.Text = _opacityBar.Value + "%";
                SaveSettings();
            };

            _bgSw = MakeSwatches(BgPalette, c => { _cBg = c; });
            _fgSw = MakeSwatches(FgPalette, c => { _cText = c; });
            _acSw = MakeSwatches(AccentPalette, c => { _cAccent = c; });

            _btnCustomBg = MakeSmallButton("自定义…", () => PickColor(_cBg, c => { _cBg = c; }));
            _btnCustomText = MakeSmallButton("自定义…", () => PickColor(_cText, c => { _cText = c; }));
            _btnCustomAcc = MakeSmallButton("自定义…", () => PickColor(_cAccent, c => { _cAccent = c; }));
            _btnReset = MakeSmallButton("恢复默认外观", ResetLook);

            _btnMouseMode = new Label
            {
                Text = "可交互",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                Tag = "btn",
            };
            _btnMouseMode.Click += (s, e) => SetClickThrough(!_clickThrough, true);

            _hintMouse = MakeHint("关掉后点击会穿透到游戏里");
            _hintFreq = MakeHint("越短越实时，越长越省资源");

            // 刷新频率
            int[] secs = { 1, 2, 5, 10 };
            _refreshBtns = new Label[secs.Length];
            for (int i = 0; i < secs.Length; i++)
            {
                int v = secs[i];
                var b = new Label
                {
                    Text = v + "s",
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                    Tag = "btn",
                };
                b.Click += (s, e) => { SetRefreshSec(v); };
                _refreshBtns[i] = b;
            }

            // 涨点动画：时长滑块 + 高亮色（改完立刻生效，下一次涨点就按新值播）
            _growBar = new TrackBar
            {
                Minimum = 200,
                Maximum = 2000,
                Value = Math.Max(200, Math.Min(2000, PageBase.GrowMs)),
                TickStyle = TickStyle.None,
                SmallChange = 50,
                LargeChange = 200,
            };
            _growVal = new Label
            {
                Text = GrowMsText(_growBar.Value),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            };
            _growBar.ValueChanged += (s, e) =>
            {
                PageBase.GrowMs = _growBar.Value;
                _growVal.Text = GrowMsText(_growBar.Value);
                _statPage?.Invalidate();
                _coopPage?.Invalidate();
                SaveSettings();
            };

            _growSw = MakeSwatches(GrowPalette, c => { PageBase.GrowTint = c; });
            _btnCustomGrow = MakeSmallButton("自定义…",
                () => PickColor(PageBase.GrowTint, c => { PageBase.GrowTint = c; }));

            foreach (var slot in _slots)
            {
                var s = slot;
                s.Cap = MakeCap(s.Caption);
                s.Btn = new Label
                {
                    Text = s.Text,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                    Tag = "btn",
                };
                s.Btn.Click += (x, y) =>
                {
                    if (_captureSlot == s) EndHotkeyCapture(null);
                    else BeginHotkeyCapture(s);
                };
                var tip = new ToolTip();
                tip.SetToolTip(s.Btn, $"「{s.Caption}」的快捷键，点一下再按新键即可更换");
            }

            _tipLabel = new Label
            {
                Text = "改动立即生效，并自动保存到 p5r_overlay.ini。\r\n" +
                       "窗口透明度 100% 最清晰；调低会透出游戏画面，文字发虚。\r\n" +
                       "涨点动画 0.2～2.0 秒，越长越看得清；高亮色只染新涨出来那一段和「+N」。\r\n" +
                       "点键位改键（Esc 取消）；「鼠标交互」关掉后鼠标穿透到游戏里。\r\n" +
                       "小键盘导航只在面板展开且可交互时接管，收起后立刻还给游戏。",
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 9f),
                Tag = "cap",
            };

            _capPreview = MakeCap("颜色预览");
            _preview = new Panel { BorderStyle = BorderStyle.FixedSingle };
            _pvLine1 = new Label
            {
                AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                Text = "知识 90   Lv3     勇气 43   Lv3",
            };
            _pvLine2 = new Label
            {
                AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                Text = "坂本龙司  R5   30/30   下次见面即可升级",
            };
            _preview.Controls.Add(_pvLine1);
            _preview.Controls.Add(_pvLine2);

            _verLabel = new Label
            {
                Text = "五维人物好感面板 " + AppVersion,
                AutoSize = false, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                Tag = "ver",
            };
            _byLabel = new Label
            {
                Text = AppAuthor,
                AutoSize = false, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                Tag = "by",
            };

            _settings.Content.Controls.AddRange(new Control[]
            {
                _capOpacity, _opacityBar, _opacityVal,
                _capBg, _capText, _capAccent,
                _btnCustomBg, _btnCustomText, _btnCustomAcc, _btnReset,
                _capMouse, _btnMouseMode, _hintMouse,
                _capRefresh, _hintFreq,
                _capAnim, _growBar, _growVal,
                _capGrowColor, _btnCustomGrow,
                _capKeys, _capNav,
                _capPreview, _preview, _tipLabel,
                _verLabel, _byLabel,
            });
            _settings.Content.Controls.AddRange(_refreshBtns);
            foreach (var slot in _slots) { _settings.Content.Controls.Add(slot.Cap); _settings.Content.Controls.Add(slot.Btn); }
            foreach (var arr in new[] { _bgSw, _fgSw, _acSw, _growSw })
                foreach (var s in arr) _settings.Content.Controls.Add(s);
        }

        private static Label MakeCap(string text) => new Label
        {
            Text = text,
            AutoSize = false,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Tag = "cap",
        };

        private Label MakeHint(string text) => new Label
        {
            Text = text,
            AutoSize = false,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            Tag = "hint",
        };

        private Label[] MakeSwatches(Color[] palette, Action<Color> pick)
        {
            var arr = new Label[palette.Length];
            for (int i = 0; i < palette.Length; i++)
            {
                var c = palette[i];
                var l = new Label
                {
                    BackColor = c,
                    AutoSize = false,
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                    Tag = c,
                };
                l.Click += (s, e) => { pick(c); ApplyTheme(); SaveSettings(); };
                arr[i] = l;
            }
            return arr;
        }

        private Label MakeSmallButton(string text, Action action)
        {
            var b = new Label
            {
                Text = text,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9f),
                Tag = "btn",
            };
            b.Click += (s, e) => { action(); ApplyTheme(); SaveSettings(); };
            return b;
        }

        private void PickColor(Color cur, Action<Color> set)
        {
            using var dlg = new ColorDialog { Color = cur, FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) { set(dlg.Color); ApplyTheme(); SaveSettings(); }
        }

        private void ResetLook()
        {
            _cBg = DefBg; _cText = DefText; _cAccent = DefAccent;
            _opacity = 1.00;
            _opacityBar.Value = 100;
            Opacity = _opacity;

            // 涨点动画一并回默认（同一页的「外观」里，用户按一次恢复就该全回去）
            PageBase.GrowTint = PageBase.DefGrowTint;
            if (_growBar != null) _growBar.Value = PageBase.DefGrowMs;   // 触发 ValueChanged → GrowMs 同步
            if (_growVal != null) _growVal.Text = GrowMsText(PageBase.GrowMs);
            _statPage?.Invalidate();
            _coopPage?.Invalidate();
        }

        private void SetRefreshSec(int sec)
        {
            _refreshSec = Math.Max(1, Math.Min(60, sec));
            UpdateRefreshButtons();
            SaveSettings();
            Log.W($"刷新频率改为 {_refreshSec}s");
        }

        private static string GrowMsText(int ms) => (ms / 1000.0).ToString("0.00") + " 秒";

        // ================================================================== 主题
        private void ApplyTheme()
        {
            _theme = Theme.Make(_cBg, _cText, _cAccent);
            Opacity = _opacity;

            BackColor = _cBg;
            _title.ForeColor = P5R.Ink;          // 标题压在白色斜牌上，必须是黑字
            _btnExpand.ForeColor = Blend(_cText, _cBg, 0.32);
            _btnHide.ForeColor = Blend(_cText, _cBg, 0.32);
            _statusLabel.ForeColor = Theme.Blend(_cText, _cBg, 0.32);

            _statPage?.SetTheme(_theme);
            _coopPage?.SetTheme(_theme);
            _calPage?.SetTheme(_theme);

            if (_logBox != null)
            {
                _logBox.BackColor = _theme.Bg2;
                _logBox.ForeColor = _theme.Text;
            }
            _logHost?.SetTheme(_theme);
            if (_settings != null) _settings.SetTheme(_theme);

            foreach (var c in Controls.OfType<Label>())
            {
                if (c.Tag as string == "btn" || c.Parent == _settings) { }
            }
            StyleSettingsControls();
            UpdateTabStyles();
            UpdateSwatchMarks();
            Invalidate(true);
        }

        private void StyleSettingsControls()
        {
            if (_settings == null) return;
            foreach (var c in _settings.Content.Controls)
            {
                switch (c)
                {
                    case Label l:
                        string tag = l.Tag as string;
                        if (tag == "cap")
                        {
                            // P5R 的分组小牌：深灰斜感底 + 白字，比纯文字清楚
                            l.BackColor = Color.FromArgb(150, P5R.Slate);
                            l.ForeColor = _theme.Text;
                            l.Padding = new Padding(7, 0, 0, 0);
                        }
                        else if (tag == "btn")
                        {
                            l.BackColor = Theme.Blend(_theme.Bg, Color.Black, 0.18);
                            l.ForeColor = _theme.Text;
                            if (l == _captureSlot?.Btn) l.ForeColor = _theme.Accent;
                        }
                        else if (tag == "hint") { l.ForeColor = Theme.Blend(_theme.Text, _theme.Bg, 0.42); }
                        else if (tag == "ver") { l.ForeColor = _theme.Accent; }
                        else if (tag == "by") { l.ForeColor = _theme.Dim; }
                        else if (l.Parent == _preview) { l.ForeColor = _theme.Text; }
                        else l.ForeColor = _theme.Text;
                        break;
                    case TrackBar tb:
                        tb.BackColor = _theme.Bg2;
                        break;
                }
            }
            if (_opacityVal != null) _opacityVal.ForeColor = _theme.Accent;
            if (_growVal != null) _growVal.ForeColor = _theme.Accent;
            if (_preview != null) _preview.BackColor = _theme.Bg2;
            if (_pvLine1 != null) _pvLine1.ForeColor = _theme.Dim;
            if (_pvLine2 != null) _pvLine2.ForeColor = _theme.Accent;
            UpdateRefreshButtons();
        }

        private void UpdateRefreshButtons()
        {
            if (_refreshBtns == null) return;
            foreach (var b in _refreshBtns)
            {
                bool on = b.Text == _refreshSec + "s";
                b.BackColor = on ? Theme.Blend(_theme.Accent, _theme.Bg, 0.65) : Theme.Blend(_theme.Bg, Color.Black, 0.18);
                b.ForeColor = on ? _theme.Accent : _theme.Text;
            }
        }

        private void UpdateSwatchMarks()
        {
            MarkSwatches(_bgSw, BgPalette, _cBg);
            MarkSwatches(_fgSw, FgPalette, _cText);
            MarkSwatches(_acSw, AccentPalette, _cAccent);
            MarkSwatches(_growSw, GrowPalette, PageBase.GrowTint);
        }

        private static void MarkSwatches(Label[] sw, Color[] pal, Color cur)
        {
            if (sw == null) return;
            for (int i = 0; i < sw.Length; i++)
            {
                bool on = pal[i].ToArgb() == cur.ToArgb();
                sw[i].Text = on ? "✓" : "";
                sw[i].ForeColor = Theme.Luma(pal[i]) > 140 ? Color.Black : Color.White;
                sw[i].Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
                sw[i].TextAlign = ContentAlignment.MiddleCenter;
            }
        }

        private void UpdateTabStyles()
        {
            if (_tabs == null) return;
            for (int i = 0; i < _tabs.Length; i++)
            {
                bool on = i == _page;
                _tabs[i].ForeColor = on ? P5R.Paper : Theme.Blend(_cText, _cBg, 0.42);
                _tabs[i].BackColor = Color.Transparent;
                _tabs[i].Font = P5R.F(on ? 13f : 12.5f, on ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        // ================================================================== 布局
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_applyingMode) return;
            LayoutControls();
            ApplyRoundRegion();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (_expanded) { _expandSize = Size; } else { _compactSize = Size; }
            SaveSettings();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyRoundRegion();
            EnsureClickThroughStyle();
            if (_passWantedAtStartup) SetClickThrough(true, false);
        }

        private void LayoutControls()
        {
            if (_title == null) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            _title.SetBounds(16, 4, Math.Max(60, w - 92), 24);
            _btnHide.SetBounds(w - 30, 5, 20, 22);
            _btnExpand.SetBounds(w - 54, 5, 20, 22);

            int tabTop = TITLE_H + 2;
            int tabLeft = 14, avail = w - 28;
            int tw = Math.Max(40, avail / PageCount);
            for (int i = 0; i < _tabs.Length; i++)
            {
                _tabs[i].Visible = _expanded;
                _tabs[i].SetBounds(tabLeft + i * tw, tabTop, tw - 4, TAB_H);
                _tabRects[i] = _expanded ? new Rectangle(tabLeft + i * tw, tabTop, tw - 4, TAB_H) : Rectangle.Empty;
            }

            if (!_expanded)
            {
                // 收起态：只有标题一行，别的一概不显示
                _statPage.Visible = _coopPage.Visible = _keyPage.Visible = _calPage.Visible = _logHost.Visible = _settings.Visible = false;
                _statusLabel.Visible = false;
                _title.SetBounds(16, 4, Math.Max(60, w - 92), Math.Max(20, h - 8));
                return;
            }

            _title.SetBounds(16, 4, Math.Max(60, w - 92), 24);

            int bodyTop = tabTop + TAB_H + 8;
            int statusH = 20;
            int bodyH = Math.Max(60, h - bodyTop - statusH - 8);
            var body = new Rectangle(14, bodyTop, Math.Max(60, w - 28), bodyH);

            _statPage.Bounds = body;
            _coopPage.Bounds = body;
            _keyPage.Bounds = body;
            _calPage.Bounds = body;
            _logHost.Bounds = body;
            _logBox.Bounds = new Rectangle(8, 6, Math.Max(60, _logHost.ViewportWidth - 16), Math.Max(40, body.Height - 12));

            _statPage.Visible = _page == PAGE_STAT;
            _coopPage.Visible = _page == PAGE_COOP;
            _keyPage.Visible = _page == PAGE_KEY;
            _calPage.Visible = _page == PAGE_CAL;
            _logHost.Visible = _page == PAGE_LOG;
            _settings.Visible = _page == PAGE_SET;
            if (_page == PAGE_SET)
            {
                _settings.Bounds = body;
                LayoutSettings();
            }
            if (_page == PAGE_LOG) SyncLogHeight();

            _statusLabel.Visible = true;
            _statusLabel.SetBounds(16, h - statusH - 2, w - 32, statusH);
        }

        private void LayoutSettings()
        {
            if (_settings == null) return;
            // 视口宽 = 扣掉右侧自绘滚动条那一列，布局按这个宽度算，条出不出现都不会抖
            int w = _settings.ViewportWidth;
            if (w < 200) return;

            int y = 8;
            int capW = 122, colX = 126;     // 「涨点动画时长」六个字要留够，否则末字被裁

            // 右侧留 22px，滑块和数值不贴到滚动条上
            const int sbW = 22;
            _opacityBar.SetBounds(colX, y, Math.Max(60, w - colX - 104), 42);
            int th = Math.Max(30, _opacityBar.Height);
            _capOpacity.SetBounds(4, y + (th - 20) / 2, capW, 20);
            _opacityVal.SetBounds(w - sbW - 78, y + (th - 20) / 2, 78, 20);
            y += th + 4;

            LayoutRow(_capBg, _bgSw, _btnCustomBg, ref y, w, capW, colX);
            LayoutRow(_capText, _fgSw, _btnCustomText, ref y, w, capW, colX);
            LayoutRow(_capAccent, _acSw, _btnCustomAcc, ref y, w, capW, colX);

            // 鼠标交互
            _capMouse.SetBounds(4, y + 3, capW, 20);
            _btnMouseMode.SetBounds(colX, y, 86, 24);
            _hintMouse.SetBounds(colX + 96, y + 4, Math.Max(40, w - colX - 104), 20);
            y += 30;

            // 刷新频率
            _capRefresh.SetBounds(4, y + 3, capW, 20);
            for (int i = 0; i < _refreshBtns.Length; i++)
                _refreshBtns[i].SetBounds(colX + i * 52, y, 46, 24);
            _hintFreq.SetBounds(colX + _refreshBtns.Length * 52 + 8, y + 4, Math.Max(40, w - colX - _refreshBtns.Length * 52 - 16), 20);
            y += 32;

            // 涨点动画：时长滑块（右边跟一个当前值），再一行高亮色
            _growBar.SetBounds(colX, y, Math.Max(60, w - colX - 104), 42);
            int gh = Math.Max(30, _growBar.Height);
            _capAnim.SetBounds(4, y + (gh - 20) / 2, capW, 20);
            _growVal.SetBounds(w - sbW - 78, y + (gh - 20) / 2, 78, 20);
            y += gh + 2;

            LayoutRow(_capGrowColor, _growSw, _btnCustomGrow, ref y, w, capW, colX);

            // 快捷键：两列，先全局热键，再小键盘导航（都能改键）
            _capKeys.SetBounds(4, y, capW, 20);
            y += 22;
            LayoutSlots(_slots.Where(s => !s.Nav).ToArray(), ref y, w);

            _capNav.SetBounds(4, y, Math.Max(140, w - 16), 20);
            y += 22;
            LayoutSlots(_slots.Where(s => s.Nav).ToArray(), ref y, w);

            _btnReset.SetBounds(4, y, 118, 24);
            y += 30;

            _capPreview.SetBounds(4, y, capW, 20);
            y += 22;
            _preview.SetBounds(4, y, Math.Max(120, w - 16), 52);
            _pvLine1.SetBounds(10, 5, Math.Max(80, w - 40), 22);
            _pvLine2.SetBounds(10, 26, Math.Max(80, w - 40), 22);
            y += 58;

            _tipLabel.SetBounds(4, y, Math.Max(120, w - 16), 102);
            y += 106;

            _verLabel.SetBounds(4, y, Math.Max(80, w - 130), 22);
            _byLabel.SetBounds(w - 136, y, 128, 22);
            y += 22;

            _settings.ContentHeight = y + 10;
        }

        /// <summary>快捷键槽位：两列铺开，返回占用的行数。</summary>
        private void LayoutSlots(HotkeySlot[] arr, ref int y, int w)
        {
            int colW = (w - 16) / 2;
            for (int i = 0; i < arr.Length; i++)
            {
                int col = i % 2, row = i / 2;
                int x = 4 + col * colW;
                int yy = y + row * 30;
                arr[i].Cap.SetBounds(x, yy + 3, 90, 20);
                arr[i].Btn.SetBounds(x + 94, yy, Math.Max(50, colW - 106), 24);
            }
            y += ((arr.Length + 1) / 2) * 30 + 4;
        }

        private void LayoutRow(Label cap, Label[] sw, Label custom, ref int y, int w, int capW, int colX)
        {
            cap.SetBounds(4, y + 4, capW, 20);

            int right = w - 24;                 // 右侧留竖滚动条
            int n = sw?.Length ?? 0;

            // 窄窗（432 甚至更窄）时 8 个 20px 色块 + 70px 按钮会顶出去，
            // 先压间距，再压方块，最后让按钮自适应 —— 保证「自定义…」永远不被裁在窗外。
            int gap = 24, box = 20;
            int need = n * gap + (custom != null ? 6 + 70 : 0);
            if (colX + need > right)
            {
                gap = 18;
                need = n * gap + (custom != null ? 6 + 70 : 0);
                if (colX + need > right) { box = 18; gap = 16; }
            }

            int x = colX;
            if (sw != null)
            {
                foreach (var s in sw)
                {
                    s.SetBounds(x, y + 3 + (20 - box) / 2, box, box);
                    x += gap;
                }
            }
            if (custom != null)
            {
                int cw = 70;
                if (x + 6 + cw > right) cw = Math.Max(48, right - x - 6);
                custom.SetBounds(x + 6, y + 3, cw, 21);
            }
            y += 32;
        }

        /// <summary>量字宽（和 P5R.Chip 用的 GDI 排版一致，去掉左右 padding）。</summary>
        private static float MeasureText(string s, Font f)
            => TextRenderer.MeasureText(s, f, Size.Empty,
                   TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

        private string _clockStamp = "";

        /// <summary>标题旁的时钟：秒变了才重画顶部那一条，不整窗重绘。</summary>
        private void TickClock()
        {
            string now = DateTime.Now.ToString("HH:mm:ss");
            if (now == _clockStamp) return;
            _clockStamp = now;
            if (!Visible || IsDisposed) return;
            Invalidate(new Rectangle(0, 0, Width, _expanded ? TITLE_H + 6 : Height));
        }

        private void ApplyRoundRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            try
            {
                using var path = new GraphicsPath();
                int d = 12;
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(Width - d, 0, d, d, 270, 90);
                path.AddArc(Width - d, Height - d, d, d, 0, 90);
                path.AddArc(0, Height - d, d, d, 90, 90);
                path.CloseAllFigures();
                Region = new Region(path);
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // ---- 底色
            using (var solid = new SolidBrush(_theme.Bg)) g.FillRectangle(solid, ClientRectangle);

            // ---- 右侧出血红斜带（P5R 的墙面装饰）
            var st0 = g.Save();
            g.SetClip(ClientRectangle);
            using (var p = new GraphicsPath())
            {
                p.AddPolygon(new[]
                {
                    new PointF(Width - 78, -16), new PointF(Width - 40, -16),
                    new PointF(Width - 128, Height + 16), new PointF(Width - 166, Height + 16),
                });
                using var br = new LinearGradientBrush(
                    new Rectangle(Math.Max(0, Width - 180), 0, 180, Math.Max(1, Height)),
                    Color.FromArgb(34, _cAccent), Color.FromArgb(0, _cAccent), 0f);
                g.FillPath(br, p);
            }
            g.Restore(st0);

            // ---- 底部半调网点（只作质感，非常淡）
            if (Height > 120)
                P5R.Halftone(g, new RectangleF(0, Height - 64, Width, 64), Color.FromArgb(13, _cText), 8, 1.0f);

            // ---- 外框
            using (var p = Round(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            {
                using (var pen = new Pen(Color.FromArgb(120, _cAccent), 1.6f)) g.DrawPath(pen, p);
            }

            // ---- 标题：白色斜切牌（P5R 的 ACCESSORY 牌）。收起态牌面铺满整条。
            //      标题右边挂一枚同字号的小钟牌，收起 / 展开都跟着走。
            using var fTitle = P5R.F(_expanded ? 13.5f : 15f, FontStyle.Bold);
            const string Title = "五维人物好感面板";
            const float PadL = 15f, PadR = 9f, Gap = 7f, ChipPadX = 7f;

            float textW = MeasureText(Title, fTitle);
            string clock = DateTime.Now.ToString("HH:mm:ss");
            float chipW = MeasureText(clock, fTitle) + ChipPadX * 2;

            // 展开态牌面最宽 268；收起态顶到窗口按钮前面（按钮吃 54，留 16 的呼吸）
            float capW = _expanded ? 268f : Math.Max(120f, Width - 70f);
            float need = PadL + textW + Gap + chipW + PadR;
            if (need > capW)
            {
                clock = DateTime.Now.ToString("HH:mm");      // 先砍秒
                chipW = MeasureText(clock, fTitle) + ChipPadX * 2;
                need = PadL + textW + Gap + chipW + PadR;
            }
            bool withClock = need <= capW;
            float plateW = Math.Min(capW, Math.Max(PadL + textW + PadR, need));

            float plateH = _expanded ? (TITLE_H - 5) : Math.Max(28, Height - 7);
            var plate = new RectangleF(6, 2, plateW, plateH);
            using (var p = P5R.Skew(plate, -9f))
            {
                P5R.Fill(g, p, P5R.Paper);
            }
            P5R.Slab(g, new RectangleF(plate.X + 7, plate.Y + 6, 4, plate.Height - 12), _cAccent, null, -9f);

            // 牌上的黑字自己画：Label 用 GDI ClearType，白底上会出彩色描边
            P5R.Text(g, Title, fTitle,
                new RectangleF(plate.X + PadL, plate.Y, textW + 3, plate.Height),
                P5R.Ink, StringAlignment.Near, StringAlignment.Center, -0.10f, false);

            // 时钟：金底黑字的斜切小牌，字号与标题完全一致
            if (withClock)
            {
                float chipH = Math.Min(_expanded ? 19f : 21f, plate.Height - 8);
                var chip = new RectangleF(plate.X + PadL + textW + Gap,
                                          plate.Y + (plate.Height - chipH) * 0.5f,
                                          chipW, chipH);
                P5R.Chip(g, chip, _cAccent, Color.FromArgb(95, P5R.Ink), clock, P5R.On(_cAccent), fTitle, -8f);
            }

            // ---- 顶部标签：斜切块
            if (_expanded)
            {
                for (int i = 0; i < PageCount; i++)
                {
                    var r = _tabRects[i];
                    if (r.Width <= 6 || r.Height <= 6) continue;
                    bool on = i == _page;
                    var tr = new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                    using (var p = P5R.Skew(tr, -7f))
                    {
                        if (on)
                        {
                            using var lg = new LinearGradientBrush(
                                new RectangleF(tr.X, tr.Y, Math.Max(2f, tr.Width), Math.Max(2f, tr.Height)),
                                P5R.RedHot, _cAccent, 0f);
                            g.FillPath(lg, p);
                        }
                        else
                        {
                            P5R.Fill(g, p, Color.FromArgb(186, P5R.SlabLo));
                        }
                        P5R.Stroke(g, p, on ? Color.FromArgb(150, Color.White) : Color.FromArgb(74, P5R.Wire), 1f);
                    }
                    // 选中项底下补一根白线，增强"被压下"的感觉
                    if (on)
                    {
                        using var pen = new Pen(Color.FromArgb(220, P5R.Paper), 2f);
                        g.DrawLine(pen, tr.X + 6, tr.Bottom - 1, tr.Right - 8, tr.Bottom - 1);
                    }
                }

                using (var pen = new Pen(Color.FromArgb(60, _theme.Line), 1f))
                    g.DrawLine(pen, 12, TITLE_H, Width - 12, TITLE_H);
            }

            base.OnPaint(e);
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ================================================================== 展开 / 翻页
        private void ToggleExpand()
        {
            _expanded = !_expanded;
            ApplyModeSize(true);
        }

        private void ApplyModeSize(bool reposition)
        {
            _applyingMode = true;
            try
            {
                int right = Right, top = Top;
                MinimumSize = _expanded ? MinExpand : MinCompact;
                Size = _expanded ? _expandSize : _compactSize;
                if (reposition) Location = new Point(Math.Max(0, right - Width), top);
                _btnExpand.Text = _expanded ? "▴" : "▾";
                LayoutControls();
                ApplyRoundRegion();
                UpdateTabStyles();
                Invalidate();
            }
            finally { _applyingMode = false; }
            SyncNavHotkeys();
            SaveSettings();
        }

        private void GoPage(int idx)
        {
            if (idx < 0 || idx >= PageCount) return;
            _page = idx;
            if (!_expanded) { _expanded = true; ApplyModeSize(true); }
            LayoutControls();
            UpdateTabStyles();
            if (idx == PAGE_LOG) ScrollLogToEnd();
            Invalidate();
        }

        private void NextPage(int dir)
        {
            int n = (_page + dir + PageCount) % PageCount;
            GoPage(n);
        }

        // ================================================================== 拖动
        private void DragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _clickThrough) return;
            // 落在自绘滚动条上就交给它拖滑块，别顺手把窗口也拖走
            if (sender == _settings && _settings != null && _settings.HitBar(e.Location)) return;
            _dragging = true;
            var c = sender as Control;
            _dragScreen = c != null ? c.PointToScreen(e.Location) : PointToScreen(e.Location);
            _dragForm = Location;
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var c = sender as Control;
            var now = c != null ? c.PointToScreen(e.Location) : PointToScreen(e.Location);
            Location = new Point(_dragForm.X + (now.X - _dragScreen.X), _dragForm.Y + (now.Y - _dragScreen.Y));
        }

        private void DragEnd(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            SaveSettings();
        }

        // ================================================================== 热键
        private void BuildHotkeySlots()
        {
            _slots.Clear();
            _slotById.Clear();
            AddSlot(new HotkeySlot
            {
                Id = HK_TOGGLE, Caption = "唤出/收起", IniKey = "HotkeyToggle",
                DefMods = 0, DefVk = 0x78, DefText = "F9",
                OnFire = () => { if (Visible) HideToTray(); else ShowPanel(); }
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_EXPAND, Caption = "展开/收起", IniKey = "HotkeyExpand",
                DefMods = 0, DefVk = 0x60, DefText = "Num0",
                OnFire = () => ToggleExpand()
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_PREV, Caption = "上一页", IniKey = "HotkeyPrev",
                // 原来是小键盘 8，那个键已经让给「上选」，切页挪到 PgUp/PgDn
                DefMods = 0, DefVk = 0x21, DefText = "PgUp",
                OnFire = () => NextPage(-1)
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_NEXT, Caption = "下一页", IniKey = "HotkeyNext",
                DefMods = 0, DefVk = 0x22, DefText = "PgDn",
                OnFire = () => NextPage(1)
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_PASS, Caption = "鼠标穿透", IniKey = "HotkeyPass",
                DefMods = 0, DefVk = 0x79, DefText = "F10",
                OnFire = () => SetClickThrough(!_clickThrough, true)
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_REFRESH, Caption = "立即刷新", IniKey = "HotkeyRefresh",
                DefMods = 0, DefVk = 0x77, DefText = "F8",
                OnFire = () => { KickWorker(); }
            });
            _passSlot = _slotById[HK_PASS];
            AddNavSlots();
        }

        /// <summary>
        /// 小键盘导航键：和全局热键同一套槽位（可改键、可存 ini、可与全局键查重），
        /// 区别只是「只在面板展开且可交互时注册」，收起 / 穿透 / 进托盘立刻还给游戏。
        /// </summary>
        private void AddNavSlots()
        {
            AddSlot(new HotkeySlot
            {
                Id = HK_NAV_UP, Nav = true, Caption = "上选", IniKey = "NavUp",
                DefMods = 0, DefVk = 0x68, DefText = "Num8",
                OnFire = () => DispatchNav(HK_NAV_UP),
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_NAV_DOWN, Nav = true, Caption = "下选", IniKey = "NavDown",
                DefMods = 0, DefVk = 0x65, DefText = "Num5",
                OnFire = () => DispatchNav(HK_NAV_DOWN),
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_NAV_LEFT, Nav = true, Caption = "左选", IniKey = "NavLeft",
                DefMods = 0, DefVk = 0x64, DefText = "Num4",
                OnFire = () => DispatchNav(HK_NAV_LEFT),
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_NAV_RIGHT, Nav = true, Caption = "右选", IniKey = "NavRight",
                DefMods = 0, DefVk = 0x66, DefText = "Num6",
                OnFire = () => DispatchNav(HK_NAV_RIGHT),
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_NAV_OK, Nav = true, Caption = "确认", IniKey = "NavOk",
                DefMods = 0, DefVk = 0x67, DefText = "Num7",
                OnFire = () => DispatchNav(HK_NAV_OK),
            });
            AddSlot(new HotkeySlot
            {
                Id = HK_NAV_BACK, Nav = true, Caption = "关闭/返回", IniKey = "NavBack",
                DefMods = 0, DefVk = 0x69, DefText = "Num9",
                OnFire = () => DispatchNav(HK_NAV_BACK),
            });
        }

        private void AddSlot(HotkeySlot s)
        {
            s.Mods = s.DefMods;
            s.Vk = s.DefVk;
            s.Text = s.DefText;
            _slots.Add(s);
            _slotById[s.Id] = s;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterAllHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            foreach (var s in _slots) try { Win32.UnregisterHotKey(Handle, s.Id); } catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncNavHotkeys();
        }

        /// <summary>导航键允许注册的条件：看得见、展开、没穿透、也没在改键。</summary>
        private bool NavWanted()
            => IsHandleCreated && !IsDisposed && Visible && _expanded && !_clickThrough && _captureSlot == null;

        /// <summary>导航键的注册状态跟着「展开 + 可见 + 可交互」走，条件不满足就注销。</summary>
        private void SyncNavHotkeys()
        {
            if (!IsHandleCreated || IsDisposed) return;

            StopNavRepeat();       // 键都换了 / 要收起了，连发必须停

            bool want = NavWanted();
            if (want == _navRegistered) return;

            var names = new List<string>();
            foreach (var s in _slots)
            {
                if (!s.Nav) continue;
                try { Win32.UnregisterHotKey(Handle, s.Id); } catch { }
                if (!want) continue;

                s.Ok = false;
                try { s.Ok = Win32.RegisterHotKey(Handle, s.Id, s.Mods | Win32.MOD_NOREPEAT, s.Vk); }
                catch { }
                if (s.Ok) names.Add(s.Text);
                else Log.W($"[nav] {s.Caption} 的 {s.Text} 注册失败（可能被其它程序占用）");
            }
            _navRegistered = want;
            Log.W(want
                ? $"[nav] 小键盘导航已接管 {string.Join(" ", names)}"
                : "[nav] 已释放，小键盘还给游戏");
        }

        private PageBase CurrentPage()
        {
            switch (_page)
            {
                case PAGE_STAT: return _statPage;
                case PAGE_COOP: return _coopPage;
                case PAGE_KEY: return _keyPage;
                case PAGE_CAL: return _calPage;
            }
            return null;
        }

        /// <summary>小键盘导航分发：8/5 上下、4/6 左右、7 确认、9 关闭。</summary>
        private void DispatchNav(int id)
        {
            NavFires++;
            var page = CurrentPage();
            if (page == null) return;      // 日志 / 设置页没有可导航的东西

            switch (id)
            {
                case HK_NAV_UP: page.NavVertical(-1); break;
                case HK_NAV_DOWN: page.NavVertical(1); break;
                case HK_NAV_LEFT: page.NavSide(-1); break;
                case HK_NAV_RIGHT: page.NavSide(1); break;
                case HK_NAV_OK: page.NavConfirm(); break;
                case HK_NAV_BACK: page.NavBack(); break;
            }
            page.Invalidate();
        }

        // ------------------------------------------------------------ 方向键长按
        private static bool IsNavDir(int id)
            => id == HK_NAV_UP || id == HK_NAV_DOWN || id == HK_NAV_LEFT || id == HK_NAV_RIGHT;

        /// <summary>方向键按下：先立刻走一步（短按就是这一下），之后按住不放再连发。</summary>
        private void BeginNavRepeat(HotkeySlot s)
        {
            _navRepeatId = s.Id;
            _navRepeatVk = (int)s.Vk;
            _navRepeatT0 = Environment.TickCount;
            _navRepeatNext = _navRepeatT0 + NavRepeatDelay;
            _navRepeating = true;
            if (!_navRepeat.Enabled) _navRepeat.Start();
        }

        private void StopNavRepeat()
        {
            _navRepeating = false;
            if (_navRepeat.Enabled) _navRepeat.Stop();
        }

        private void NavRepeatTick(object sender, EventArgs e)
        {
            if (!_navRepeating) { _navRepeat.Stop(); return; }
            if (!NavWanted()) { StopNavRepeat(); return; }
            if ((Win32.GetAsyncKeyState(_navRepeatVk) & 0x8000) == 0) { StopNavRepeat(); return; }

            int now = Environment.TickCount;
            if (now - _navRepeatT0 < NavRepeatDelay) return;   // 没按住满 350ms：就当短按，不连发
            if (now < _navRepeatNext) return;

            _navRepeatNext = now + NavRepeatStep;
            NavTimerFires++;
            DispatchNav(_navRepeatId);
        }

        private void RegisterAllHotkeys()
        {
            if (!IsHandleCreated) return;
            foreach (var s in _slots)
                if (!s.Nav) { try { Win32.UnregisterHotKey(Handle, s.Id); } catch { } }

            foreach (var s in _slots)
            {
                if (s.Nav) continue;       // 导航键走 SyncNavHotkeys，只在展开时注册
                if (!TryRegister(s))
                {
                    Log.W($"[hk] {s.Caption} 的 {s.Text} 被占用，回退默认键 {s.DefText}");
                    s.Mods = s.DefMods; s.Vk = s.DefVk; s.Text = s.DefText;
                    s.Ok = TryRegister(s);
                }
            }
        }

        private bool TryRegister(HotkeySlot s)
        {
            // 导航键此刻不该注册（收起 / 穿透 / 正在改键），等条件满足由 SyncNavHotkeys 注册
            if (s.Nav && !NavWanted()) { s.Ok = true; return true; }

            bool ok = false;
            try { ok = Win32.RegisterHotKey(Handle, s.Id, s.Mods | Win32.MOD_NOREPEAT, s.Vk); } catch { }
            s.Ok = ok;
            return ok;
        }

        private bool RebindSlot(HotkeySlot s, uint mods, uint vk, string text, out string err)
        {
            err = null;
            foreach (var o in _slots)
                if (o != s && o.Mods == mods && o.Vk == vk) { err = $"这个组合已用于「{o.Caption}」"; return false; }

            try { Win32.UnregisterHotKey(Handle, s.Id); } catch { }
            uint oldMods = s.Mods, oldVk = s.Vk;
            string oldText = s.Text;
            s.Mods = mods; s.Vk = vk; s.Text = text;

            if (TryRegister(s)) { s.Btn.Text = s.Text; SaveSettings(); return true; }

            s.Mods = oldMods; s.Vk = oldVk; s.Text = oldText;
            TryRegister(s);
            s.Btn.Text = s.Text;
            err = "该组合被其它程序占用，已恢复原键";
            return false;
        }

        private void BeginHotkeyCapture(HotkeySlot s)
        {
            if (_captureSlot == s) { EndHotkeyCapture(null); return; }
            _captureSlot = s;
            StopNavRepeat();
            // 捕获期间摘掉全局热键，否则按 F9 会被自己抢走
            foreach (var o in _slots) try { Win32.UnregisterHotKey(Handle, o.Id); } catch { }
            SyncNavHotkeys();     // 导航键也一起摘掉，否则按小键盘会被导航吃掉
            s.Btn.Text = "按新键…";
            s.Btn.ForeColor = _theme.Accent;
        }

        private void EndHotkeyCapture(string msg)
        {
            if (_captureSlot == null) return;
            var s = _captureSlot;
            _captureSlot = null;
            s.Btn.Text = s.Text;
            RegisterAllHotkeys();
            SyncNavHotkeys();
            StyleSettingsControls();
            if (!string.IsNullOrEmpty(msg)) Log.W(msg);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_captureSlot != null)
            {
                if (keyData == Keys.Escape) { EndHotkeyCapture("已取消改键"); return true; }

                uint mods = 0;
                if ((keyData & Keys.Control) != 0) mods |= Win32.MOD_CONTROL;
                if ((keyData & Keys.Alt) != 0) mods |= Win32.MOD_ALT;
                if ((keyData & Keys.Shift) != 0) mods |= Win32.MOD_SHIFT;
                uint vk = (uint)(keyData & Keys.KeyCode);

                if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C) return true;  // 纯修饰键

                if (mods == 0 && !IsSafeBareKey(vk))
                {
                    Log.W("改键失败：单独一个字母/数字会抢系统输入，请配 Ctrl、Alt 或 Shift");
                    return true;
                }

                string text = FormatHotkey(mods, vk);
                if (RebindSlot(_captureSlot, mods, vk, text, out string err))
                    EndHotkeyCapture($"已将「{_captureSlot.Caption}」改为 {text}");
                else
                {
                    Log.W("改键失败：" + err);
                    _captureSlot.Btn.Text = "按新键…";
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();

                if (_slotById.TryGetValue(id, out var slot))
                {
                    // 同一个方向键的连发已经在跑：这次是系统自己的按键重复，节奏交给我们自己的表，
                    // 免得「系统重复 + 定时器」两路一起推，一次长按走出两倍步数
                    if (slot.Nav && IsNavDir(id)) NavHotkeyFires++;
                    bool sameRepeat = slot.Nav && IsNavDir(id) && _navRepeating && _navRepeatId == id;
                    if (sameRepeat) return;

                    try { slot.OnFire?.Invoke(); }
                    catch (Exception ex) { Log.W($"[hk] {slot.Caption} 执行异常: {ex.Message}"); }
                    // 方向键：这一下已经生效，按住不放就接着连发
                    if (slot.Nav && IsNavDir(id)) BeginNavRepeat(slot);
                    return;
                }
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT && !_clickThrough)
                {
                    long lp = m.LParam.ToInt64();
                    var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    bool l = pt.X <= RESIZE_BORDER, r = pt.X >= Width - RESIZE_BORDER;
                    bool t = pt.Y <= RESIZE_BORDER, b = pt.Y >= Height - RESIZE_BORDER;
                    if (b && r) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (b && l) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (t && l) m.Result = (IntPtr)HTTOPLEFT;
                    else if (t && r) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (l) m.Result = (IntPtr)HTLEFT;
                    else if (r) m.Result = (IntPtr)HTRIGHT;
                    else if (t) m.Result = (IntPtr)HTTOP;
                    else if (b) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            base.WndProc(ref m);
        }

        private static string FormatHotkey(uint mods, uint vk)
        {
            var sb = new System.Text.StringBuilder();
            if ((mods & Win32.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Win32.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Win32.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Win32.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        private static string KeyName(uint vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);          // F1..F24
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);        // 小键盘 0..9
            switch (vk)
            {
                case 0x6A: return "Multiply";
                case 0x6B: return "Add";
                case 0x6D: return "Subtract";
                case 0x6E: return "Decimal";
                case 0x6F: return "Divide";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x2C: return "PrintScreen";
                case 0x13: return "Pause";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
            }
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            return "0x" + vk.ToString("X2");
        }

        private static bool IsSafeBareKey(uint vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return true;                       // F1..F24
            if (vk >= 0x60 && vk <= 0x69) return true;                       // 小键盘数字
            if (vk >= 0x6A && vk <= 0x6F) return true;                       // 小键盘运算
            switch (vk)
            {
                case 0x25: case 0x26: case 0x27: case 0x28:                  // 方向键
                case 0x21: case 0x22: case 0x24: case 0x23:                  // PgUp/PgDn/Home/End
                case 0x2D: case 0x2E: case 0x2C: case 0x13:                  // Insert/Delete/PrtSc/Pause
                case 0x90: case 0x91:                                        // NumLock/ScrollLock
                    return true;
            }
            return false;
        }

        private static bool ParseHotkey(string s, out uint mods, out uint vk, out string text)
        {
            mods = 0; vk = 0; text = s;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split('+');
            string key = parts[parts.Length - 1].Trim();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= Win32.MOD_CONTROL; break;
                    case "alt": mods |= Win32.MOD_ALT; break;
                    case "shift": mods |= Win32.MOD_SHIFT; break;
                    case "win": mods |= Win32.MOD_WIN; break;
                    default: return false;
                }
            }
            if (!TryParseKeyName(key, out vk)) return false;
            text = FormatHotkey(mods, vk);
            return true;
        }

        private static bool TryParseKeyName(string name, out uint vk)
        {
            vk = 0;
            string n = name.Trim();
            if (n.Length == 1)
            {
                char c = char.ToUpperInvariant(n[0]);
                if (c >= 'A' && c <= 'Z') { vk = c; return true; }
                if (c >= '0' && c <= '9') { vk = c; return true; }
            }
            if (n.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(n.Substring(1), out int f)
                && f >= 1 && f <= 24) { vk = (uint)(0x6F + f); return true; }
            if (n.StartsWith("Num", StringComparison.OrdinalIgnoreCase))
            {
                string d = n.Substring(3);
                if (int.TryParse(d, out int k) && k >= 0 && k <= 9) { vk = (uint)(0x60 + k); return true; }
            }
            switch (n.ToLowerInvariant())
            {
                case "add": vk = 0x6B; return true;
                case "subtract": vk = 0x6D; return true;
                case "multiply": vk = 0x6A; return true;
                case "divide": vk = 0x6F; return true;
                case "decimal": vk = 0x6E; return true;
                case "left": vk = 0x25; return true;
                case "up": vk = 0x26; return true;
                case "right": vk = 0x27; return true;
                case "down": vk = 0x28; return true;
                case "pgup": vk = 0x21; return true;
                case "pgdn": vk = 0x22; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "insert": vk = 0x2D; return true;
                case "delete": vk = 0x2E; return true;
                case "space": vk = 0x20; return true;
            }
            return false;
        }

        // ================================================================== 穿透
        private void SetClickThrough(bool on, bool persist)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<bool, bool>(SetClickThrough), on, persist); } catch { } return; }
            if (!IsHandleCreated) { _clickThrough = on; return; }

            _clickThrough = on;
            ApplyClickThroughStyle(on);
            if (!on) ReassertTopMost();
            if (_btnMouseMode != null)
                _btnMouseMode.Text = on ? "已穿透" : "可交互";
            Log.W("鼠标交互 = " + (!on) + "（穿透=" + on + "）");
            SyncNavHotkeys();
            if (persist) SaveSettings();
        }

        private void ApplyClickThroughStyle(bool on)
        {
            int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
            int want = on ? (ex | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED)
                          : (ex & ~Win32.WS_EX_TRANSPARENT);
            if (want != ex) Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, want);
            Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER |
                Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        }

        private void EnsureClickThroughStyle()
        {
            if (!_clickThrough || !IsHandleCreated) return;
            if ((Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) != 0) return;
            ApplyClickThroughStyle(true);
        }

        // ================================================================== 日历心跳
        /// <summary>
        /// 日历专用心跳。不管「刷新频率」被设成 3s 还是 60s，都单独每 10 秒读一次游戏内日期：
        /// 面板还停在前一天（或者根本没对上过）就直接带到游戏里的今天。
        /// 只读 8 字节，成本可以忽略；不加锁是因为 ReadProcessMemory 本身线程安全。
        /// </summary>
        private const int DateTickMs = 10000;
        private System.Windows.Forms.Timer _dateTick;

        private void StartDateTicker()
        {
            _dateTick = new System.Windows.Forms.Timer { Interval = DateTickMs };
            _dateTick.Tick += (s, e) => DateTick();
            _dateTick.Start();
        }

        private void DateTick()
        {
            try
            {
                if (!_reader.TryReadGameDate(out int day, out int tz)) return;
                if (_calPage.SyncToday(day, tz))
                {
                    var d = Snapshot.CalendarOrigin.AddDays(day);
                    Log.W($"[日历] 已对齐游戏内 {d.Month} 月 {d.Day} 日（{(tz >= 0 ? Snapshot.TimeZoneText(tz) : "时段未知")}）");
                }
            }
            catch (Exception ex) { Log.W("[日历] " + ex.Message); }
        }

        // ================================================================== 置顶 / 托盘
        private System.Windows.Forms.Timer _keeper;

        private void StartTopMostKeeper()
        {
            _keeper = new System.Windows.Forms.Timer { Interval = 1000 };
            _keeper.Tick += (s, e) => { ReassertTopMost(); EnsureClickThroughStyle(); TickClock(); };
            _keeper.Start();
        }

        private void ReassertTopMost()
        {
            if (!Visible || IsDisposed) return;
            try
            {
                if (!TopMost) TopMost = true;
                Win32.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>
        /// 程序图标：优先用内嵌的多尺寸 ico（16/24/32/48/256 各有独立帧，
        /// 托盘和任务栏都能拿到对应尺寸，不会把小图拉伸）。
        /// </summary>
        internal static Icon LoadAppIcon()
        {
            try
            {
                using (var s = typeof(OverlayForm).Assembly.GetManifestResourceStream("app.ico"))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch { }

            try
            {
                var exe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exe != null) return exe;
            }
            catch { }

            return SystemIcons.Application;
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("打开面板") { Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
            miOpen.Click += (s, e) => ShowPanel();
            var miClick = new ToolStripMenuItem("鼠标交互");
            miClick.Click += (s, e) => SetClickThrough(!_clickThrough, true);
            var miExit = new ToolStripMenuItem("关闭面板");
            miExit.Click += (s, e) => ExitApp();
            menu.Items.Add(miOpen);
            menu.Items.Add(miClick);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            _tray = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "P5R 五维人物好感面板",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (s, e) => ShowPanel();
        }

        private void ShowPanel()
        {
            Show();
            ShowInTaskbar = true;
            if (_clickThrough) SetClickThrough(false, true);
            TopMost = true;
            ReassertTopMost();
            SyncNavHotkeys();
            try { Win32.SetForegroundWindow(Handle); } catch { }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            SyncNavHotkeys();
            Log.W("面板已收进托盘（F9 或托盘右键可唤回）");
        }

        private bool _reallyExit;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        private void ExitApp()
        {
            _reallyExit = true;
            _stop = true;
            try { StopNavRepeat(); _navRepeat.Dispose(); } catch { }
            try { if (_dateTick != null) { _dateTick.Stop(); _dateTick.Dispose(); _dateTick = null; } } catch { }
            try { SaveSettings(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { _reader.Dispose(); } catch { }
            Close();
            Application.Exit();
        }

        // ================================================================== 数据线程
        private void StartWorker()
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "p5r-reader" };
            _worker.Start();
        }

        private void KickWorker()
        {
            Log.W("手动刷新一次");
            try { _worker?.Interrupt(); } catch { }
        }

        private void WorkerLoop()
        {
            int failCount = 0;

            while (!_stop)
            {
                try
                {
                    Snapshot snap = null;

                    if (!_reader.Attached)
                    {
                        if (_reader.Attach(out string note))
                        {
                            failCount = 0;
                            _readerDisposedReset();
                        }
                        else if (!string.IsNullOrEmpty(note))
                        {
                            PostStatus(note);
                        }
                    }

                    if (_reader.Attached)
                    {
                        snap = _reader.Poll();
                        if (snap == null)
                        {
                            // 游戏退出或地址失效 → 下次重新附加
                            _reader.Dispose();
                            failCount++;
                        }
                    }

                    if (snap == null)
                    {
                        // 没游戏就读最新存档（数值是最后一次保存时的状态）
                        string root = SaveReader.Root;
                        if (root != null)
                        {
                            snap = SaveReader.LoadNewest(out string sn);
                            if (snap != null) PostStatus(sn);
                            else PostStatus(sn);
                        }
                        else PostStatus("等待游戏启动…");
                    }

                    if (snap != null) PostSnapshot(snap);

                    int sleep = Math.Max(1, _refreshSec) * 1000;
                    if (!_reader.Attached) sleep = Math.Min(sleep, 4000);
                    Thread.Sleep(sleep);
                }
                catch (ThreadInterruptedException)
                {
                    // 手动刷新：立刻进入下一轮
                }
                catch (Exception ex)
                {
                    Log.W("[worker] " + ex.Message);
                    Thread.Sleep(2000);
                }
            }
        }

        private void _readerDisposedReset()
        {
            // 附加成功后清掉"无游戏"时的旧文案
        }

        private void PostStatus(string text)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    _status = text;
                    UpdateStatusLabel();
                }));
            }
            catch { }
        }

        private void PostSnapshot(Snapshot s)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() => ApplySnapshot(s)));
            }
            catch { }
        }

        private void ApplySnapshot(Snapshot s)
        {
            _snap = s;
            _statPage.SetData(s.Stats);
            _coopPage.SetData(BuildCoopRows(s.Coops));
            _keyPage.SetRanks(s.Coops);
            _keyPage.SetStats(s.Stats);
            _calPage.SetGameDate(s.GameDayIdx, s.TimeZone);

            double avg = s.Stats.Average(v => v);
            bool empty = s.Stats.All(v => v == 0);
            if (empty && s.Live)
            {
                _status = "已连接游戏，但还没载入存档（标题画面 / 读档中）";
            }
            else if (s.Live)
            {
                _status = $"内存实时 · PID {_reader.Pid} · 每 {_refreshSec}s · {TimeText()}";
            }
            else
            {
                _status = $"存档 {s.SaveSlot} · 每 {_refreshSec}s · {TimeText()}";
            }
            UpdateStatusLabel();
        }

        private static string TimeText() => DateTime.Now.ToString("HH:mm:ss");

        private void UpdateStatusLabel()
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = _status + (_clickThrough ? "   ｜ 鼠标已穿透" : "");
        }

        private List<CoopRow> BuildCoopRows(List<CoopEntry> coops)
        {
            var rows = new List<CoopRow>();
            if (coops == null || coops.Count == 0) return rows;

            var ready = new List<CoopRow>();
            var accum = new List<CoopRow>();
            var special = new List<CoopRow>();
            var maxed = new List<CoopRow>();

            foreach (var c in coops)
            {
                var info = GameTables.Coops[c.Id];
                var need = GameTables.Requirement(c.Id, c.Rank);
                var r = new CoopRow
                {
                    Id = c.Id, Name = info.Name, Arcana = info.Arcana,
                    Rank = c.Rank, Points = c.Points, Need = need.Need,
                };
                if (c.Rank >= 10) { r.State = "已满级"; maxed.Add(r); }
                else if (!need.Need.HasValue)
                {
                    string e = need.Extra ?? "";
                    r.State = (e == "无" || e == "") ? "事件触发" : e;
                    special.Add(r);
                }
                else if (c.Points >= need.Need.Value) { r.State = "下次见面可升"; ready.Add(r); }
                else
                {
                    r.State = $"还差 {need.Need.Value - c.Points}";
                    accum.Add(r);
                }
            }

            ready.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            accum.Sort((a, b) => (a.Need.Value - a.Points).CompareTo(b.Need.Value - b.Points));
            special.Sort((a, b) => b.Rank.CompareTo(a.Rank));
            maxed.Sort((a, b) => b.Rank.CompareTo(a.Rank));

            void AddGroup(string title, Color col, List<CoopRow> list)
            {
                if (list.Count == 0) return;
                rows.Add(new CoopRow { IsHeader = true, HeaderText = title, HeaderColor = col });
                rows.AddRange(list);
            }

            AddGroup("可以升级", Color.FromArgb(110, 220, 140), ready);
            AddGroup("继续积累", Color.FromArgb(240, 196, 96), accum);
            AddGroup("不看点数（任务 / 剧情 / 人格指数）", Color.FromArgb(150, 158, 170), special);
            AddGroup("已满级", Color.FromArgb(234, 190, 76), maxed);
            return rows;
        }

        // ================================================================== 日志页
        private void OnLogLine(string line)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() => AppendLog(line)));
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            if (_logBox == null || _logBox.IsDisposed) return;
            _logBox.AppendText(line + Environment.NewLine);
            if (_logBox.Lines.Length > 500)
            {
                var keep = _logBox.Lines.Skip(_logBox.Lines.Length - 400).ToArray();
                _logBox.Lines = keep;
            }
            SyncLogHeight();
            if (_page == PAGE_LOG) ScrollLogToEnd();
        }

        /// <summary>
        /// 日志框拉成和文字一样高，滚动交给外面的 ScrollHost（RichTextBox 自带的条没法着色）。
        /// 先按视口宽设宽度，WordWrap 的换行位置才和实际显示一致，量出来的高度才准。
        /// </summary>
        private void SyncLogHeight()
        {
            if (_logHost == null || _logBox == null || _logBox.IsDisposed) return;

            int w = Math.Max(60, _logHost.ViewportWidth - 16);
            if (_logBox.Width != w) _logBox.Width = w;

            int h = Math.Max(40, _logHost.Height - 12);
            if (_logBox.TextLength > 0)
            {
                // 最后一个字符的逻辑坐标就是文本末尾；再加一行行高的余量
                var last = _logBox.GetPositionFromCharIndex(_logBox.TextLength - 1);
                if (last.Y > 0) h = Math.Max(h, last.Y + _logBox.Font.Height + 12);
            }
            if (_logBox.Height != h) _logBox.Height = h;
            _logHost.ContentHeight = h + 12;
        }

        private void ScrollLogToEnd()
        {
            if (_logHost == null) return;
            _logHost.SetOffset(int.MaxValue);   // SetOffset 内部 clamp 到最大值
        }

        /// <summary>自检：当前日志区的滚动偏移 / 内容高度，用来确认没被裁掉。</summary>
        public (int Offset, int ContentH, int ViewH) DebugLogScroll()
            => _logHost == null ? (-1, -1, -1) : (_logHost.ScrollOffset, _logHost.ContentHeight, _logHost.Height);

        /// <summary>自检：灌假日志把日志区撑过一屏（只进内存，不写 log 文件）。</summary>
        public void DebugFillLog(int n)
        {
            for (int i = 0; i < n; i++)
                AppendLog($"{DateTime.Now:HH:mm:ss}  [test] 第 {i + 1} 行：这一行只是把日志撑长，用来看自绘滚动条");
            if (_page == PAGE_LOG) ScrollLogToEnd();
            Application.DoEvents();
        }

        // ================================================================== 设置读写
        private void LoadSettings()
        {
            int x = Screen.PrimaryScreen.WorkingArea.Right - DefaultCompact.Width - 24;
            int y = 120;
            bool exp = false;
            int refresh = 5;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (var ln in File.ReadAllLines(SettingsPath))
                    {
                        int eq = ln.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = ln.Substring(0, eq).Trim();
                        string val = ln.Substring(eq + 1).Trim();
                        switch (key)
                        {
                            case "X": int.TryParse(val, out x); break;
                            case "Y": int.TryParse(val, out y); break;
                            case "Expanded": bool.TryParse(val, out exp); break;
                            case "W": if (int.TryParse(val, out int w1) && w1 > 100) _expandSize.Width = w1; break;
                            case "H": if (int.TryParse(val, out int h1) && h1 > 80) _expandSize.Height = h1; break;
                            case "CW": if (int.TryParse(val, out int w2) && w2 > 100) _compactSize.Width = w2; break;
                            case "CH": if (int.TryParse(val, out int h2) && h2 > 30) _compactSize.Height = h2; break;
                            case "Opacity":
                                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double op))
                                    _opacity = Math.Min(1.0, Math.Max(0.25, op));
                                break;
                            case "Bg": _cBg = ParseCol(val, _cBg); break;
                            case "Text": _cText = ParseCol(val, _cText); break;
                            case "Accent": _cAccent = ParseCol(val, _cAccent); break;
                            case "ClickThrough": bool.TryParse(val, out _passWantedAtStartup); break;
                            case "RefreshSec":
                                if (int.TryParse(val, out int rs)) refresh = Math.Max(1, Math.Min(60, rs));
                                break;
                            case "GrowMs":
                                if (int.TryParse(val, out int gm))
                                    PageBase.GrowMs = Math.Max(200, Math.Min(2000, gm));
                                break;
                            case "GrowColor": PageBase.GrowTint = ParseCol(val, PageBase.GrowTint); break;
                            default:
                                var slot = _slots.FirstOrDefault(s => s.IniKey == key);
                                if (slot != null && ParseHotkey(val, out uint hm, out uint hv, out string ht))
                                {
                                    slot.Mods = hm; slot.Vk = hv; slot.Text = ht;
                                }
                                break;
                        }
                    }
                }
            }
            catch { }

            _refreshSec = refresh;
            _expanded = exp;

            // 老版本的「上一页 / 下一页」默认是小键盘 8 / 2，这两个键现在归导航用；
            // 老 ini 里存着的旧默认值就地迁到 PgUp / PgDn，否则会和导航键抢注册。
            if (_slotById.TryGetValue(HK_PREV, out var prevSlot) && prevSlot.Mods == 0 && prevSlot.Vk == 0x68)
            { prevSlot.Vk = 0x21; prevSlot.Text = "PgUp"; }
            if (_slotById.TryGetValue(HK_NEXT, out var nextSlot) && nextSlot.Mods == 0 && nextSlot.Vk == 0x62)
            { nextSlot.Vk = 0x22; nextSlot.Text = "PgDn"; }

            _compactSize = new Size(Math.Max(MinCompact.Width, _compactSize.Width), Math.Max(MinCompact.Height, _compactSize.Height));
            _expandSize = new Size(Math.Max(MinExpand.Width, _expandSize.Width), Math.Max(MinExpand.Height, _expandSize.Height));

            var wa = Screen.PrimaryScreen.WorkingArea;
            if (x < wa.Left - 50 || x > wa.Right - 60) x = wa.Right - DefaultCompact.Width - 24;
            if (y < wa.Top - 20 || y > wa.Bottom - 60) y = 120;
            Location = new Point(x, y);
            Size = _expanded ? _expandSize : _compactSize;
            Opacity = _opacity;
        }

        private void SaveSettings()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("X=" + Left);
                sb.AppendLine("Y=" + Top);
                sb.AppendLine("Expanded=" + _expanded);
                sb.AppendLine("W=" + _expandSize.Width);
                sb.AppendLine("H=" + _expandSize.Height);
                sb.AppendLine("CW=" + _compactSize.Width);
                sb.AppendLine("CH=" + _compactSize.Height);
                sb.AppendLine("Opacity=" + _opacity.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("Bg=" + ColToStr(_cBg));
                sb.AppendLine("Text=" + ColToStr(_cText));
                sb.AppendLine("Accent=" + ColToStr(_cAccent));
                sb.AppendLine("ClickThrough=" + _clickThrough);
                sb.AppendLine("RefreshSec=" + _refreshSec);
                sb.AppendLine("GrowMs=" + PageBase.GrowMs);
                sb.AppendLine("GrowColor=" + ColToStr(PageBase.GrowTint));
                foreach (var s in _slots) sb.AppendLine(s.IniKey + "=" + s.Text);
                File.WriteAllText(SettingsPath, sb.ToString());
            }
            catch { }
        }

        // ================================================================== 自检截图
        public void DebugSetup(int page, bool expanded)
        {
            _page = page;
            _expanded = expanded;
            Opacity = 1.0;
            ApplyModeSize(false);
            LayoutControls();
            UpdateTabStyles();
            if (page == PAGE_LOG) ScrollLogToEnd();
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：当前是否已经读到真实数据。</summary>
        public bool HasData => _snap != null && _snap.Stats != null && _snap.Stats.Any(v => v > 0);

        /// <summary>自检计数：导航分发被调用了几次（含长按连发），给 --navhold 用。</summary>
        public static int NavFires, NavTimerFires, NavHotkeyFires;

        /// <summary>自检：模拟一次小键盘导航。0 上 / 1 下 / 2 左 / 3 右 / 4 确认 / 5 返回。</summary>
        public void DebugNav(int key)
        {
            var page = CurrentPage();
            if (page == null) return;

            switch (key)
            {
                case 0: page.NavVertical(-1); break;
                case 1: page.NavVertical(1); break;
                case 2: page.NavSide(-1); break;
                case 3: page.NavSide(1); break;
                case 4: page.NavConfirm(); break;
                case 5: page.NavBack(); break;
            }
            page.Invalidate();
            page.Update();
            Application.DoEvents();
        }

        /// <summary>自检：当前页有没有东西处在「可确认」状态（只用于断言日志）。</summary>
        public string DebugNavState()
        {
            var page = CurrentPage();
            return page == null ? "无页面" : (page.GetType().Name + " 可交互");
        }

        /// <summary>自检：没有游戏在跑时灌一份演示数据，用来检查排版。</summary>
        public void DebugDemoData()
        {
            var snap = new Snapshot { Live = false, SaveSlot = "（演示数据 · 仅用于排版自检）" };
            snap.Stats = new short[] { 90, 41, 41, 43, 33 };
            snap.GameDayIdx = 81;      // 2016-06-21
            snap.TimeZone = 5;         // 放学后
            int[,] demo =
            {
                { 1, 5, 30 }, { 2, 5, 30 }, { 3, 5, 22 },   // 可升
                { 4, 5, 21 }, { 5, 3, 15 },                 // 继续积累
                { 6, 4, 0 }, { 7, 4, 0 }, { 8, 3, 0 }, { 9, 3, 0 }, { 10, 1, 0 }, { 11, 1, 0 },
            };
            for (int i = 0; i < demo.GetLength(0); i++)
                snap.Coops.Add(new CoopEntry { Id = demo[i, 0], Rank = demo[i, 1], Points = demo[i, 2] });
            ApplySnapshot(snap);
            LayoutControls();
            Application.DoEvents();
        }

        /// <summary>自检：在演示数据上给某一项五维加点数，触发增长动画（idx 是内存下标）。</summary>
        public void DebugStatGrow(int idx, int amount)
        {
            if (_snap == null) return;
            var snap = CloneSnap(_snap);
            if (idx >= 0 && idx < snap.Stats.Length) snap.Stats[idx] = (short)(snap.Stats[idx] + amount);
            ApplySnapshot(snap);
            Invalidate(true);
        }

        /// <summary>自检：把某项五维的增长动画定格在某进度（0~1），逐帧截图用。</summary>
        public void DebugFreezeStatGrow(int idx, float p)
        {
            _statPage.DebugFreezeGrow(idx, p);
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：把某个角色好感增长动画定格，0 传 -1 之外的负数解冻请用 DebugClearGrow。</summary>
        public void DebugFreezeCoopGrow(int cid, float p)
        {
            _coopPage.DebugFreezeGrow(cid, p);
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：解除五维 / 好感两页的增长定格。</summary>
        public void DebugClearGrow()
        {
            _statPage.DebugClearGrow();
            _coopPage.DebugClearGrow();
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：好感页滚到指定偏移（拍「已满级」金块）。</summary>
        public void DebugCoopPageScroll(int y)
        {
            _coopPage.DebugScrollTo(y);
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：给某个角色的好感点数加分，触发好感页增长动画。</summary>
        public void DebugCoopGrow(int cid, int amount)
        {
            if (_snap == null) return;
            var snap = CloneSnap(_snap);
            foreach (var c in snap.Coops) if (c.Id == cid) c.Points += amount;
            ApplySnapshot(snap);
            Invalidate(true);
        }

        private static Snapshot CloneSnap(Snapshot s)
        {
            var n = new Snapshot
            {
                Live = s.Live, Source = s.Source, SaveSlot = s.SaveSlot,
                Stats = (short[])s.Stats.Clone(),
                GameDayIdx = s.GameDayIdx, TimeZone = s.TimeZone,
            };
            foreach (var c in s.Coops) n.Coops.Add(new CoopEntry { Id = c.Id, Rank = c.Rank, Points = c.Points });
            return n;
        }

        /// <summary>自检：把设置页滚到指定像素位置（看自绘滚动条的滑块位置）。</summary>
        public void DebugScrollSettings(int offset)
        {
            _settings?.SetOffset(offset);
            Application.DoEvents();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 自检：把滚轮消息发给设置页里的一个普通 Label，返回滚完的偏移。
        /// 真机上滚轮时鼠标就压在 Label 上，靠的是 WM_MOUSEWHEEL 沿父链冒泡到 ScrollHost。
        /// </summary>
        public int DebugWheelSettings()
        {
            if (_settings == null) return -1;
            var c = _settings.Content.Controls.OfType<Label>().FirstOrDefault();
            if (c == null) return -1;

            const int WM_MOUSEWHEEL = 0x020A;
            int delta = -120;                                  // 往下一格
            IntPtr wp = (IntPtr)(delta << 16);
            IntPtr lp = (IntPtr)((40 << 16) | 20);
            SendMessage(c.Handle, WM_MOUSEWHEEL, wp, lp);
            Application.DoEvents();
            return _settings.ScrollOffset;
        }

        /// <summary>自检：直接摆好日历页的状态（无游戏时截排版用）。</summary>
        public void DebugCalendar(int mode, int month, int day)
        {
            _calPage.DebugView(mode, month, day);
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：直接打开攻略弹层（0 = 五维门槛，1 = 好感能力表）。</summary>
        public void DebugDetail(int kind, int idx, int tab = 0)
        {
            _page = kind == 0 ? PAGE_STAT : PAGE_COOP;
            _expanded = true;
            Opacity = 1.0;
            ApplyModeSize(false);
            LayoutControls();
            UpdateTabStyles();
            Invalidate(true);
            Application.DoEvents();

            if (kind == 0) _statPage.DebugOpenStat(idx);
            else
            {
                _coopPage.DebugOpenCoop(idx);
                if (tab != 0) _coopPage.DebugTab(tab);
            }

            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：按 Coop ID 打开好感弹层并切到指定页（tab 0 能力 / 1 礼物）。</summary>
        public void DebugCoopDetail(int cid, int rank, int tab)
        {
            _page = PAGE_COOP;
            _expanded = true;
            Opacity = 1.0;
            ApplyModeSize(false);
            LayoutControls();
            UpdateTabStyles();
            Invalidate(true);
            Application.DoEvents();

            _coopPage.DebugOpenCoopId(cid, rank, tab);

            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：能力表弹层滚动指定像素。</summary>
        public void DebugCoopScroll(int dy)
        {
            _coopPage.DebugScrollDetail(dy);
            Invalidate(true);
            Application.DoEvents();
        }

        /// <summary>自检：停掉数据线程和日历心跳，避免它们把手喂进来的日期冲掉。</summary>
        public void DebugStopWorker()
        {
            _stop = true;
            try { if (_dateTick != null) _dateTick.Stop(); } catch { }
            try { _worker?.Interrupt(); } catch { }
            try { _worker?.Join(1500); } catch { }
            Application.DoEvents();
        }

        /// <summary>自检：改状态行文案（截图用，避免拍到「读档中」这类临时状态）。</summary>
        public void DebugSetStatus(string text)
        {
            _status = text;
            UpdateStatusLabel();
            Application.DoEvents();
        }

        /// <summary>自检：强制日历的「自动跟随」状态。</summary>
        public void DebugSetFollow(bool on)
        {
            _calPage.DebugSetFollow(on);
            Invalidate(true);
            Update();
        }

        /// <summary>自检：把点击转给日历页，返回它的状态描述。</summary>
        public string DebugCalendarPress(string name) => _calPage.DebugPress(name);

        /// <summary>
        /// 自检：手动跑一次 10 秒心跳 —— 直接喂游戏日期，不用真游戏。
        /// 返回（有变化?）+ 日历状态，用来验证「日子变了会自动跳到新的一天」。
        /// </summary>
        public string DebugDateTick(int dayIdx, int tz)
        {
            bool moved = _calPage.SyncToday(dayIdx, tz);
            Invalidate(true);
            Update();
            var d = Snapshot.CalendarOrigin.AddDays(dayIdx);
            return $"{(moved ? "已对齐" : "无变化")} 游戏={d:MM-dd} tz={tz} | {_calPage.DebugState()}";
        }

        /// <summary>自检：把点击转给好感弹层的「能力 / 礼物」页签。</summary>
        public string DebugClickTab(string which) => _coopPage.DebugClickTab(which);

        /// <summary>自检：滚动关键页，用来拍下半部分的分组。</summary>
        public void DebugKeyScroll(int y) => _keyPage.DebugScrollTo(y);

        /// <summary>自检：强制换一套配色并复位不透明度。</summary>
        public void DebugForceTheme(Color bg, Color text, Color accent)
        {
            _cBg = bg; _cText = text; _cAccent = accent;
            ApplyTheme();
            DebugSetOpacity(1.0);
        }

        /// <summary>自检：直接改窗口不透明度。</summary>
        public void DebugSetOpacity(double v)
        {
            _opacity = v;
            Opacity = v;
            _opacityBar.Value = (int)Math.Round(v * 100);
            Application.DoEvents();
        }

        /// <summary>自检：手工挂上 WS_EX_LAYERED，模拟穿透态。</summary>
        public void DebugAddLayered()
        {
            int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_LAYERED);
            Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER |
                Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
            Application.DoEvents();
        }

        /// <summary>自检：抓桌面真实像素（DWM 合成后的样子，等同于肉眼所见）。</summary>
        public bool ShotScreenTo(string path)
        {
            try
            {
                if (!Win32.GetWindowRect(Handle, out var r)) return false;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return false;
                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(new Point(r.Left, r.Top), Point.Empty, new Size(w, h));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return true;
            }
            catch (Exception ex) { Log.W("桌面截图失败：" + ex.Message); return false; }
        }

        public void DebugExit()
        {
            _reallyExit = true;
            try { _stop = true; } catch { }
            Close();
            Application.Exit();
        }

        public void ShotTo(string path)
        {
            try
            {
                // 用 PrintWindow 直接渲染窗口自身，避免屏幕抓取受位置 / 遮挡 / 缩放影响
                using var bmp = new Bitmap(Width, Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok = false;
                    try { ok = Win32.PrintWindow(Handle, hdc, 2); } catch { }
                    g.ReleaseHdc(hdc);
                    if (!ok)
                    {
                        if (Win32.GetWindowRect(Handle, out var r))
                            g.CopyFromScreen(new Point(r.Left, r.Top), Point.Empty, bmp.Size);
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { Log.W("截图失败：" + ex.Message); }
        }
    }
}
