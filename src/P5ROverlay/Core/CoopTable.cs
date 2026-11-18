using System.Collections.Generic;

namespace P5ROverlay
{
    /// <summary>五维阈值 / Coop 名称 / Coop 每级好感需求（转录自 bilibili 游戏 Wiki 各 COOP 页「触发条件」）。</summary>
    internal static class GameTables
    {
        // 内存/存档里五项的顺序是 [知识, 魅力, 灵巧, 勇气, 体贴]（P5/P5R 通用），
        // 名字必须跟这个顺序一一对应，否则会把魅力读成体贴。
        public static readonly string[] StatNames = { "知识", "魅力", "灵巧", "勇气", "体贴" };
        public static readonly string[] StatEn = { "Knowledge", "Charm", "Proficiency", "Guts", "Kindness" };

        // 游戏菜单（STATUS / 五维画面）里的显示顺序是 [知识, 勇气, 灵巧, 体贴, 魅力]，
        // 与内存顺序不同。这个数组存的是「显示位 → StatNames 下标」，
        // 只作用于渲染时的排列，读数据仍按 StatNames 的下标走。
        public static readonly int[] StatDisplayOrder = { 0, 3, 2, 4, 1 };

        /// <summary>Lv1~Lv5 的累计点数门槛（与 StatNames 同序）。</summary>
        public static readonly int[][] StatTh =
        {
            new[] { 0, 34, 82, 126, 192 },     // 知识
            new[] { 0, 6, 52, 92, 132 },       // 魅力（Lv2 只要 6 点，五项里最低）
            new[] { 0, 12, 34, 60, 87 },       // 灵巧
            new[] { 0, 11, 38, 68, 113 },      // 勇气
            new[] { 0, 14, 44, 91, 136 },      // 体贴
        };

        public sealed class CoopInfo
        {
            public int Id;
            public string Name;
            public string Arcana;
            public CoopInfo(int id, string name, string arcana) { Id = id; Name = name; Arcana = arcana; }
        }

        /// <summary>游戏内部 Confidant ID（Amicitia Wiki）。33 = 芳泽霞、35 = 丸喜拓人是 P5R 新增高位 ID。</summary>
        public static readonly Dictionary<int, CoopInfo> Coops = new Dictionary<int, CoopInfo>
        {
            { 1,  new CoopInfo(1,  "伊戈尔",       "愚者") },
            { 2,  new CoopInfo(2,  "摩尔加纳",     "魔术师") },
            { 3,  new CoopInfo(3,  "新岛真",       "女教皇") },
            { 4,  new CoopInfo(4,  "奥村春",       "女帝") },
            { 5,  new CoopInfo(5,  "喜多川祐介",   "皇帝") },
            { 6,  new CoopInfo(6,  "佐仓惣治郎",   "教皇") },
            { 7,  new CoopInfo(7,  "高卷杏",       "恋爱") },
            { 8,  new CoopInfo(8,  "坂本龙司",     "战车") },
            { 9,  new CoopInfo(9,  "明智吾郎",     "正义") },
            { 10, new CoopInfo(10, "佐仓双叶",     "隐者") },
            { 11, new CoopInfo(11, "御船千早",     "命运") },
            { 12, new CoopInfo(12, "卡萝琳&贾斯汀", "力量") },
            { 13, new CoopInfo(13, "岩井宗久",     "刑死者") },
            { 14, new CoopInfo(14, "武见妙",       "死神") },
            { 15, new CoopInfo(15, "川上贞代",     "节制") },
            { 16, new CoopInfo(16, "大宅一子",     "恶魔") },
            { 17, new CoopInfo(17, "织田信也",     "塔") },
            { 18, new CoopInfo(18, "东乡一二三",   "星") },
            { 19, new CoopInfo(19, "三岛由辉",     "月") },
            { 20, new CoopInfo(20, "吉田寅之助",   "太阳") },
            { 21, new CoopInfo(21, "新岛冴",       "审判") },
            { 33, new CoopInfo(33, "芳泽霞",       "信念") },
            { 35, new CoopInfo(35, "丸喜拓人",     "顾问官") },
        };

        public struct Req
        {
            public int? Need;      // null = 该级不看点数
            public string Extra;   // 附加条件
            public Req(int? need, string extra) { Need = need; Extra = extra; }
        }

        private static Req R(int? n, string e = "") => new Req(n, e);

        /// <summary>NEED[cid] = [1→2, 2→3, ... , 9→10] 共 9 项。</summary>
        public static readonly Dictionary<int, Req[]> Need = new Dictionary<int, Req[]>
        {
            [1] = new[] { R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"),
                          R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动") },
            [2] = new[] { R(null, "随剧情推进"), R(null, "随剧情推进"), R(null, "随剧情推进"), R(null, "随剧情推进"),
                          R(null, "随剧情推进"), R(null, "随剧情推进"), R(null, "随剧情推进"), R(null, "随剧情推进"), R(null, "随剧情推进") },
            [3] = new[] { R(null, "雨天以外"), R(null, "无"), R(15), R(20),
                          R(20, "魅力MAX"), R(20), R(30), R(20, "先在新宿书店前与拉客牛郎对话"), R(55) },
            [4] = new[] { R(null, "无"), R(null, "无"), R(14, "灵巧MAX"), R(28),
                          R(15), R(20), R(40), R(22), R(20) },
            [5] = new[] { R(null, "无"), R(null, "1→2 事件 3 天后"), R(25), R(15),
                          R(25, "灵巧4级"), R(20), R(26), R(22), R(35) },
            [6] = new[] { R(null, "无"), R(30, "泡咖啡至少 1 次"), R(40), R(43, "8/22 以后"),
                          R(20), R(20, "体贴MAX"), R(14), R(null, "完成任务「盯上遗产的坏舅舅」"), R(40) },
            [7] = new[] { R(null, "5/6 以后 + 体贴2级"), R(35), R(35), R(20),
                          R(35), R(55), R(30), R(67), R(35) },
            [8] = new[] { R(null, "雨天以外"), R(20), R(30), R(20, "5/6 以后"),
                          R(30), R(45, "等龙司短信邀请（夜间事件）"), R(45), R(60), R(60) },
            [9] = new[] { R(null, "无"), R(null, "知识3级 + 魅力3级"), R(23), R(40),
                          R(null, "8/28 以后接受他的邀请"), R(55, "知识4级 + 进入新岛冴殿堂后"),
                          R(null, "印象空间对决，不能输"), R(null, "剧情自动"), R(null, "剧情自动") },
            [10] = new[] { R(null, "体贴4级"), R(null, "无"), R(10), R(15),
                           R(26), R(21), R(null, "完成任务「腐蚀少女的残忍双亲」"), R(30), R(35) },
            [11] = new[] { R(null, "无"), R(null, "无"), R(15), R(15),
                           R(15), R(30), R(20, "还需完成任务「揭穿！灵能者的花招」"), R(46), R(21) },
            [12] = new[] { R(null, "合成任务"), R(null, "合成任务"), R(null, "合成任务"), R(null, "合成任务"),
                           R(null, "合成任务"), R(null, "合成任务"), R(null, "合成任务"), R(null, "合成任务"), R(null, "合成任务") },
            [13] = new[] { R(null, "无"), R(5), R(15), R(25),
                           R(40), R(40), R(null, "勇气MAX；还需完成任务「地下交易」"), R(25), R(40) },
            [14] = new[] { R(null, "勇气2级"), R(null, "无"), R(11), R(20),
                           R(11), R(11), R(null, "魅力4级；还需完成任务「医生的抗争」"), R(42), R(36) },
            [15] = new[] { R(null, "支付 5000 元"), R(20, "支付 5000 元"), R(37, "支付 5000 元"), R(null, "支付 5000 元"),
                           R(11, "支付 5000 元"), R(37), R(null, "支付 5000 元"),
                           R(null, "完成任务「苦恼的恩师来自地狱」"), R(null, "先触发「川上振作」事件") },
            [16] = new[] { R(null, "无"), R(null, "无"), R(12), R(15),
                           R(25), R(22), R(null, "之后需完成任务「曝光！蛮横的恶劣上司」"), R(21), R(42) },
            [17] = new[] { R(null, "无"), R(null, "无"), R(11), R(14),
                           R(20), R(25), R(null, "雨天/寒流以外"), R(null, "完成任务「I Shot My Mother」"), R(30) },
            [18] = new[] { R(null, "无"), R(null, "无"), R(10), R(14),
                           R(14), R(22), R(null, "知识MAX；还需完成任务「击败未来星妈的妙招」"), R(30), R(30) },
            [19] = new[] { R(null, "完成 1 个三岛委托"), R(null, "完成 2 个三岛委托"), R(null, "完成 3 个三岛委托"),
                           R(null, "完成 4 个三岛委托"), R(null, "完成 5 个三岛委托"), R(null, "完成 6 个三岛委托"),
                           R(null, "完成 7 个三岛委托"), R(null, "完成 8 个三岛委托"), R(null, "完成 10 个三岛委托") },
            [20] = new[] { R(null, "无好感要求"), R(null, "无好感要求"), R(null, "无好感要求"), R(null, "无好感要求"),
                           R(null, "无好感要求"), R(null, "无好感要求"), R(null, "无好感要求"), R(null, "无好感要求"), R(null, "无好感要求") },
            [21] = new[] { R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"),
                           R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动"), R(null, "剧情自动") },
            [33] = new[] { R(null, "无"), R(15), R(51), R(20),
                           R(null, "1/13 以后"), R(null, "无"), R(55), R(40), R(80) },
            [35] = new[] { R(null, "无"), R(null, "雨天以外"), R(28), R(42),
                           R(32, "9/20 以后"), R(28), R(60), R(30), R(null, "11/18 自动触发") },
        };

        public static Req Requirement(int cid, int rank)
        {
            if (rank >= 10) return new Req(null, "已满级");
            if (Need.TryGetValue(cid, out var row) && rank >= 1 && rank <= row.Length) return row[rank - 1];
            return new Req(null, "");
        }

        public static int StatLevel(int idx, int pts)
        {
            var t = StatTh[idx];
            for (int k = 4; k >= 0; k--) if (pts >= t[k]) return k + 1;
            return 1;
        }
    }
}
