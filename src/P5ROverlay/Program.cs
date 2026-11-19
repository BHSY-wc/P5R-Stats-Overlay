using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace P5ROverlay
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Log.Init(AppContext.BaseDirectory);

            // 高 DPI 感知必须在创建任何窗口之前设置：否则系统会把 96dpi 渲染的整窗位图拉伸，
            // 字就像"截图放大"一样糊。csproj 里的 ApplicationHighDpiMode 只在用
            // ApplicationConfiguration.Initialize() 时生效，这里是手写 Main，必须自己调。
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 自绘代码出问题时不能静默退出：把异常写进 log，方便定位
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.W("[UI异常] " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.W("[致命异常] " + e.ExceptionObject);

            if (args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
            {
                RunTest();
                return;
            }

            // 无游戏时开一个带演示数据的窗口，方便外部脚本点按钮做交互自检
            if (args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            {
                var d = new OverlayForm();
                d.Location = new System.Drawing.Point(40, 40);
                d.Show();
                Pump(1500);
                if (!d.HasData) { d.DebugDemoData(); Pump(200); }
                d.DebugSetup(2, true);
                Application.Run(d);
                return;
            }

            if (args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
            {
                RunUiTest(AppContext.BaseDirectory);
                Application.Exit();
                return;
            }

            if (args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase)))
            {
                RunShot(AppContext.BaseDirectory);
                return;
            }

            if (args.Any(a => a.Equals("--grab", StringComparison.OrdinalIgnoreCase)))
            {
                RunGrab(AppContext.BaseDirectory);
                return;
            }

            Application.Run(new OverlayForm());
        }

        /// <summary>命令行自检：不启动界面，只验证内存/存档读取与阈值计算。结果写到 selftest.txt。</summary>
        private static void RunTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== P5R 面板自检 ===");

            using (var reader = new GameReader())
            {
                if (reader.Attach(out string note))
                {
                    var snap = reader.Poll();
                    if (snap != null)
                    {
                        sb.AppendLine($"内存：{string.Join(" / ", snap.Stats)}");
                        foreach (var c in snap.Coops)
                        {
                            var info = GameTables.Coops[c.Id];
                            var need = GameTables.Requirement(c.Id, c.Rank);
                            sb.AppendLine($"  {info.Name,-12}{info.Arcana,-8}R{c.Rank}  {c.Points}" +
                                          (need.Need.HasValue ? $"/{need.Need}" : $"  {need.Extra}"));
                        }
                    }
                }
                else
                {
                    sb.AppendLine("内存：" + note);
                }
            }

            var save = SaveReader.LoadNewest(out string sn);
            if (save != null)
            {
                sb.AppendLine($"存档：{save.Source}  {string.Join(" / ", save.Stats)}  Coop {save.Coops.Count}");
            }
            else
            {
                sb.AppendLine("存档：" + sn);
            }

            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"),
                    sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>交互自检：不开真鼠标，直接把点击喂给日历页，看状态切换对不对。</summary>
        private static void RunUiTest(string dir)
        {
            var sb = new System.Text.StringBuilder();
            var f = new OverlayForm();
            f.Location = new System.Drawing.Point(40, 40);
            f.Show();
            Pump(1200);
            if (!f.HasData) { f.DebugDemoData(); Pump(300); }
            f.DebugSetup(2, true);
            Pump(500);

            void Step(string what, string key)
            {
                string r = f.DebugCalendarPress(key);
                sb.AppendLine($"{what,-22} -> {r}");
                Pump(120);
            }

            Step("初始（月视图）", "state");
            Step("点「今」", "today");
            Step("点「‹」前一天", "prev");
            Step("点「›」两天", "next");
            Step("点「›」", "next");
            Step("点日期标题", "title");
            Step("选 10 号", "dpday10");
            Step("再开选日→下月", "title");
            Step("选日浮层→下月", "dpnext");
            Step("选 5 号", "dpday5");
            Step("点「月历」返回", "back");
            Step("月历点第 6 格", "cell5");
            Step("弹层点面板外", "outside");
            Step("月历再点「今」", "today");
            Step("日视图点「月历」", "back");

            try
            {
                File.WriteAllText(Path.Combine(dir, "uitest.txt"),
                    sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
            Log.W("交互自检完成");
            f.DebugExit();
        }

        /// <summary>自检截图：把四个页面各拍一张，存到 exe 同目录 shots\。</summary>
        private static void RunShot(string dir)
        {
            string outDir = Path.Combine(dir, "shots");
            try { Directory.CreateDirectory(outDir); } catch { }

            var f = new OverlayForm();
            f.Location = new System.Drawing.Point(40, 40);
            f.Show();
            Pump(2200);                        // 等第一轮数据
            if (!f.HasData)                    // 游戏没开：灌演示数据，保证截图能看出排版
            {
                f.DebugDemoData();
                Pump(300);
            }

            var pages = new[] { (0, "1_五维"), (1, "2_好感"), (3, "4_日志"), (4, "5_设置") };
            foreach (var (page, name) in pages)
            {
                f.DebugSetup(page, true);
                f.Location = new System.Drawing.Point(40, 40);
                Pump(700);
                f.ShotTo(Path.Combine(outDir, name + ".png"));
            }

            // 日历三种形态：月视图（格子自动换行）/ 日详情弹层 / 整页日视图
            f.DebugSetup(2, true);
            f.DebugCalendar(0, 6, 21);
            f.Location = new System.Drawing.Point(40, 40);
            Pump(700);
            f.ShotTo(Path.Combine(outDir, "3_日历_月.png"));

            f.DebugCalendar(2, 6, 21);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "3b_日历_日详情.png"));

            f.DebugCalendar(1, 6, 21);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "3c_日历_日视图.png"));

            f.DebugCalendar(2, 6, 23);        // 一天 7 条，看大字下会不会溢出
            Pump(400);
            f.ShotTo(Path.Combine(outDir, "3d_日历_日详情_多条.png"));

            f.DebugCalendar(1, 6, 23);
            Pump(400);
            f.ShotTo(Path.Combine(outDir, "3e_日历_日视图_多条.png"));

            // 攻略弹层：五维门槛（2 倍字）/ 好感能力表（1.5 倍字）
            f.DebugDetail(0, 3);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "1b_五维_解锁项.png"));

            f.DebugDetail(1, 2);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "2b_好感_能力表.png"));

            f.DebugDetail(1, 8);      // 长描述，看 1.5 倍字下折行是否正常
            Pump(400);
            f.ShotTo(Path.Combine(outDir, "2c_好感_能力表_长文本.png"));

            // 选日浮层：看月份文字有没有被 ‹ › 按钮压住
            f.DebugSetup(2, true);
            f.DebugCalendar(3, 6, 21);
            f.Location = new System.Drawing.Point(40, 40);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "3f_日历_选日.png"));

            f.DebugSetup(0, false);
            f.Location = new System.Drawing.Point(40, 40);
            Pump(600);
            f.ShotTo(Path.Combine(outDir, "0_收起.png"));

            Log.W("自检截图完成 → " + outDir);
            f.DebugExit();
            Application.Exit();
        }

        /// <summary>画质对照实验：同一帧分别用 100% 不透明 / 90% 不透明 / 强挂 LAYERED 抓桌面真实像素。</summary>
        private static void RunGrab(string dir)
        {
            var f = new OverlayForm();
            f.Location = new System.Drawing.Point(40, 40);
            f.Show();
            Pump(1800);

            f.DebugForceTheme(System.Drawing.Color.FromArgb(18, 22, 28),
                              System.Drawing.Color.FromArgb(226, 233, 242),
                              System.Drawing.Color.FromArgb(240, 196, 96));
            f.DebugSetup(3, true);                 // 设置页文字最多，最能看出是否发虚
            f.Location = new System.Drawing.Point(40, 40);
            Pump(900);

            f.DebugSetOpacity(1.0);
            Pump(400);
            f.ShotScreenTo(Path.Combine(dir, "_grab_a_op100.png"));

            f.DebugSetOpacity(0.90);
            Pump(400);
            f.ShotScreenTo(Path.Combine(dir, "_grab_b_op090.png"));

            f.DebugSetOpacity(1.0);
            f.DebugAddLayered();                   // 模拟「鼠标穿透」态
            Pump(400);
            f.ShotScreenTo(Path.Combine(dir, "_grab_c_layered.png"));

            Log.W("画质对照完成 → " + dir);
            f.DebugExit();
            Application.Exit();
        }

        private static void Pump(int ms)
        {
            int step = 30;
            for (int t = 0; t < ms; t += step)
            {
                Application.DoEvents();
                Thread.Sleep(step);
            }
        }

    }
}
