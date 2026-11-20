using System.Collections.Generic;

namespace P5ROverlay
{
    /// <summary>
    /// 恋爱线的关键对话选项。
    ///
    /// 来源：bilibili 游戏 Wiki「P5R/十艘跳指南」+ 各角色 COOP 对话页（2026-11 抓取）。
    /// P5R 的恋人关系没有菜单开关，全靠 Rank 8→9 前后的几个选项，选错就永久走友人线
    /// （而且 Rank 10 的约会事件会直接消失），所以面板单独标出来。
    ///
    /// Must = 最后确认项（Rank 8→9），选错必定友人；
    /// 非 Must 的是「恋人前置条件」，多个条件满足任意一个即可让确认项出现。
    /// </summary>
    internal static class RomanceData
    {
        public sealed class Tip
        {
            /// <summary>该选项出现在 Rank N → N+1 的那次事件里。</summary>
            public int Rank;
            /// <summary>选项原文，多句用「→」串起来。</summary>
            public string Text;
            /// <summary>第几个选项 / 是第几个前置条件。</summary>
            public string Note;
            /// <summary>最后确认项：选错直接走友人线。</summary>
            public bool Must;

            public Tip(int rank, string text, string note, bool must = false)
            { Rank = rank; Text = text; Note = note; Must = must; }
        }

        private static Tip T(int r, string t, string n, bool must = false) => new Tip(r, t, n, must);

        private static readonly Dictionary<int, Tip[]> _d = new Dictionary<int, Tip[]>
        {
            // 3 新岛真（女教皇）
            [3] = new[]
            {
                T(8, "「那个人就在这里」→「我喜欢你」", "Rank 8→9　第五、第六个选项・选错走友人线（Rank 10 约会消失）", true),
            },

            // 4 奥村春（女帝）
            [4] = new[]
            {
                T(8, "「我也很喜欢妳」", "Rank 8→9　第四个选项・选错走友人线（Rank 10 约会消失）", true),
            },

            // 7 高卷杏（恋爱）
            [7] = new[]
            {
                T(8, "「还有我在」", "Rank 8→9　之后两个选项都能确认恋人・不选「还有我在」就走友人线", true),
            },

            // 10 佐仓双叶（隐者）
            [10] = new[]
            {
                T(8, "「因为我喜欢双叶」", "Rank 8→9　第四个选项・选错走友人线（Rank 10 约会消失）", true),
            },

            // 11 御船千早（命运）
            [11] = new[]
            {
                T(5, "「千早就是千早」", "Rank 5→6　第四个选项・恋人条件 1/3"),
                T(7, "「可以」", "交任务「揭穿！灵能者的花招」时・恋人条件 2/3"),
                T(8, "「想见千早」", "Rank 8→9　第三个选项・选错走友人线（Rank 10 约会消失）", true),
            },

            // 14 武见妙（死神）
            [14] = new[]
            {
                T(6, "「要我看几次都行」", "Rank 6→7　最后一个选项・恋人条件 1/3"),
                T(7, "「我在这」", "Rank 7→8　第二个选项・恋人条件 2/3"),
                T(7, "「如果是为了美和的话」", "交任务「医生的抗争·失传的疗法」时第三个选项・恋人条件 3/3"),
                T(8, "「因为我想见妳」→「就是喜欢妳」→「别岔开话题」",
                     "Rank 8→9　连着选；选「真的是为了考试」或「妳觉得呢?」会断在友人线", true),
            },

            // 15 川上贞代（节制）
            [15] = new[]
            {
                T(6, "「给我打消念头」", "Rank 6→7　第二个选项"),
                T(8, "「希望你能再来」→「你没有误会」→「是男人和女人」",
                     "交任务「苦恼的恩师来自地狱」时连着选・选错走友人线", true),
            },

            // 16 大宅一子（恶魔）
            [16] = new[]
            {
                T(6, "「这是大宅的魅力」", "Rank 6→7　第三个选项"),
                T(7, "「一起去见她吧」", "交任务「曝光！蛮横的恶劣上司」时最后一个选项"),
                T(8, "「我不能让你一个人」→「我当真了」→「我喜欢一子」",
                     "Rank 8→9　第三、五、六个选项・选错走友人线（Rank 10 约会消失）", true),
            },

            // 18 东乡一二三（星）
            [18] = new[]
            {
                T(8, "「我想永远陪在你身边」→「请你和我交往」", "Rank 8→9　连着选・选错走友人线（Rank 10 约会消失）", true),
            },

            // 33 芳泽霞（信念）
            [33] = new[]
            {
                T(8, "「我也喜欢堇」", "Rank 8→9　第七个选项・选错走友人线（Rank 10 约会消失）", true),
            },
        };

        public static Tip[] Get(int coopId) => _d.TryGetValue(coopId, out var v) ? v : System.Array.Empty<Tip>();

        /// <summary>这个 Coop 有没有恋爱线（女性可攻略角色才有）。</summary>
        public static bool Has(int coopId) => _d.ContainsKey(coopId);
    }
}
