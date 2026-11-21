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
                d.DebugSetup(3, true);
                Application.Run(d);
                return;
            }

            if (args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
            {
                RunUiTest(AppContext.BaseDirectory);
                Application.Exit();
                return;
            }

            // 读取自检：不开窗口，验证「面板比存档先打开」时能不能自动补定位
            if (args.Any(a => a.Equals("--readtest", StringComparison.OrdinalIgnoreCase)))
            {
                RunReadTest(AppContext.BaseDirectory);
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

        /// <summary>
        /// 读取自检：附加游戏 → 读一帧 → 清空 Coop/日期地址（模拟「面板先于存档打开」）→
        /// 再读一帧，看是否自动补定位回来。结果写 readtest.txt。
        /// </summary>
        private static void RunReadTest(string dir)
        {
            var sb = new System.Text.StringBuilder();
            using var r = new GameReader();
            sb.AppendLine(r.Attach(out string note) ? "附加成功，PID " + r.Pid : "附加失败：" + note);

            string Fmt(Snapshot x) => x == null
                ? "null"
                : $"五维 [{string.Join(",", x.Stats)}]　Coop {x.Coops.Count} 条　" +
                  $"日期 {(x.GameDayIdx > 0 ? x.GameDayIdx + "（tz " + x.TimeZone + "）" : "无")}";

            sb.AppendLine("第一帧　　　：" + Fmt(r.Poll()));

            r.DebugForgetAux();                 // 清成未定位
            var again = r.Poll();
            sb.AppendLine("清空地址后　：" + Fmt(again));
            sb.AppendLine("补定位　　　：" + (again != null && again.Coops.Count > 0 && again.GameDayIdx > 0 ? "成功" : "失败"));

            try { File.WriteAllText(Path.Combine(dir, "readtest.txt"), sb.ToString(), System.Text.Encoding.UTF8); }
            catch { }
            Log.W("读取自检完成");
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
            f.DebugSetup(3, true);
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

            // 10 秒心跳：游戏里过日子 → 日历自己跟上（不点「今」也要跟）
            sb.AppendLine();
            sb.AppendLine("== 日历 10 秒心跳 ==");
            f.DebugStopWorker();        // 否则数据线程会把手喂进来的日期冲掉
            f.DebugDemoData();          // 回到确定的起点：2016-06-21（dayIdx 81）
            Pump(200);
            void Tick(string what, int day, int tz)
            {
                string r = f.DebugDateTick(day, tz);
                sb.AppendLine($"{what,-22} -> {r}");
                Pump(150);
            }
            Tick("心跳·同一天", 81, 5);              // 什么都不该变
            Tick("心跳·次日(本月内)", 82, 5);        // 月视图跟到 06-22
            Tick("心跳·跨月到 07-01", 91, 3);        // 月视图跟到 2016-07
            Step("手动翻回上个月", "prev");           // follow 变 false
            Tick("心跳·跨月(已暂停跟随)", 100, 5);   // 视图不动，只更新「今天」
            Step("点「今」回今天", "today");          // 进入 07-10 的日视图，并恢复跟随
            Tick("心跳·日视图+1 天", 101, 5);        // 日视图本身跟到 07-11

            // 好感弹层的「能力 / 礼物」页签：走真实命中判定
            sb.AppendLine();
            f.DebugCoopDetail(7, 6, 0);
            Pump(300);
            sb.AppendLine($"打开好感弹层(能力页)     -> {f.DebugClickTab("state")}");
            string r1 = f.DebugClickTab("gift");  sb.AppendLine($"点「礼物」页签          -> {r1}");
            string r2 = f.DebugClickTab("ab");    sb.AppendLine($"点「能力」页签          -> {r2}");
            string r3 = f.DebugClickTab("gift");  sb.AppendLine($"再点「礼物」页签        -> {r3}");

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

            var pages = new[] { (0, "1_五维"), (1, "2_好感"), (2, "3_关键"), (4, "4_日志"), (5, "5_设置") };
            foreach (var (page, name) in pages)
            {
                f.DebugSetup(page, true);
                f.Location = new System.Drawing.Point(40, 40);
                Pump(700);
                f.ShotTo(Path.Combine(outDir, name + ".png"));
            }

            // 日历三种形态：月视图（格子自动换行）/ 日详情弹层 / 整页日视图
            // 先停掉数据线程和 10 秒心跳：否则真游戏的日期/状态行会把手喂的日期和文案冲掉
            f.DebugStopWorker();
            f.DebugSetup(3, true);
            f.DebugCalendar(0, 6, 21);
            f.DebugSetFollow(true);            // DebugCalendar 会关掉跟随，截图要拍跟随态
            f.DebugSetStatus("内存实时 · 每 5s · " + DateTime.Now.ToString("HH:mm:ss"));
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

            // 手动翻走 + 游戏已经过日子：顶部要写清「已暂停跟随（游戏 6/21）」
            f.DebugStopWorker();               // 游戏在跑时也要拍出确定画面
            f.DebugSetup(3, true);
            f.DebugCalendar(0, 6, 1);          // 先回到月视图
            f.DebugDateTick(81, 5);            // 游戏内 2016-06-21
            f.DebugCalendarPress("prev");      // 翻到 5 月 → 暂停跟随
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "3g_日历_暂停跟随.png"));

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

            // 礼物页：看「能力 / 礼物」两页签、+50 徽章、字级是否与能力页一致
            f.DebugCoopDetail(7, 6, 1);      // 高卷杏，六件礼物
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "2d_好感_礼物.png"));

            f.DebugCoopDetail(17, 8, 1);     // 织田信也，七件礼物 + 长店名
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "2e_好感_礼物_多件.png"));

            f.DebugCoopDetail(7, 6, 0);      // 切回能力页，确认页签状态跟着变
            Pump(400);
            f.ShotTo(Path.Combine(outDir, "2f_好感_切回能力页.png"));

            f.DebugCoopDetail(2, 4, 1);      // 摩尔加纳：没有礼物记录，看空态文案
            Pump(400);
            f.ShotTo(Path.Combine(outDir, "2g_好感_礼物_空态.png"));

            // 最小尺寸下页签会不会把标题挤没（DebugCoopDetail 会复原尺寸，所以先开再缩）
            f.DebugCoopDetail(12, 5, 1);
            f.Size = new System.Drawing.Size(432, 470);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "2h_好感_礼物_最小宽度.png"));

            // 恋爱线标记：武见妙（三个前置条件 + Rank 8 的确认项都看得到）
            f.DebugCoopDetail(14, 8, 0);
            f.Size = new System.Drawing.Size(600, 760);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "2i_好感_恋爱标记.png"));

            // 恋爱线标记：大宅一子（三段式确认），拉高看 Rank 8 那条
            f.DebugCoopDetail(16, 9, 0);
            f.Size = new System.Drawing.Size(600, 760);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "2j_好感_恋爱标记_大宅一子.png"));

            // 关键页：滚到下半部分看分组切换
            f.DebugSetup(2, true);
            f.DebugKeyScroll(520);
            f.Location = new System.Drawing.Point(40, 40);
            Pump(400);
            f.ShotTo(Path.Combine(outDir, "3a_关键_滚动.png"));

            // 关键页：最小宽度下标题 / 截止标签 / R 徽章会不会打架
            f.DebugSetup(2, true);
            f.DebugKeyScroll(0);
            f.Size = new System.Drawing.Size(432, 520);
            Pump(500);
            f.ShotTo(Path.Combine(outDir, "3b_关键_最小宽度.png"));

            // 选日浮层：看月份文字有没有被 ‹ › 按钮压住
            f.DebugSetup(3, true);
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
            f.DebugSetup(4, true);                 // 设置页文字最多，最能看出是否发虚
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
