using System.Drawing;

namespace P5ROverlay
{
    /// <summary>
    /// 「关键」页的清单：会真正改变玩法节奏、且必须主动投入时间才拿得到的东西。
    /// 刻意不收「宫殿截止日期」这类日历每天都会提示、随剧情自然推进的内容。
    /// 能力名与 Rank 对照 GuideData（bilibili Wiki 转录）；书籍 / 设施条目对照流程攻略。
    /// </summary>
    internal static class KeyData
    {
        public sealed class Item
        {
            public int Cid;            // 关联 Coop ID；0 = 书籍 / 设施，没有进度可读
            public int NeedRank;       // 需要达到的 Rank；0 = 不看 Rank
            public string Title = "";  // 主标题（能力名 / 书名）
            public string Source = ""; // 副标题（角色名 / 获取地点）
            public string Desc = "";   // 一句话作用
            public string Deadline = ""; // 「11/17」这种硬日期；空 = 没有截止
            public bool Hard;          // 错过就整条线没了
        }

        public sealed class Group
        {
            public string Name = "";
            public Color Tint;
            public Item[] Items = new Item[0];
        }

        private static Item I(int cid, int need, string title, string source, string desc,
                              string deadline = "", bool hard = false)
            => new Item
            {
                Cid = cid, NeedRank = need, Title = title, Source = source,
                Desc = desc, Deadline = deadline, Hard = hard,
            };

        public static readonly Color TintStory = Color.FromArgb(255, 64, 96);
        public static readonly Color TintBattle = Color.FromArgb(168, 132, 255);
        public static readonly Color TintTime = Color.FromArgb(86, 206, 214);
        public static readonly Color TintTalk = Color.FromArgb(236, 190, 86);
        public static readonly Color TintStat = Color.FromArgb(150, 240, 184);

        public static readonly Group[] Groups =
        {
            new Group
            {
                Name = "结局 · 剧情", Tint = TintStory,
                Items = new[]
                {
                    I(35, 9, "顾问官 R9：第三学期开关", "丸喜拓人", "唯一的硬条件。没到 R9，第三学期整段不会出现。", "11/17", true),
                    I(9, 8, "正义 R8：加长结局", "明智吾郎", "不影响第三学期开启，但少了真结局后那段额外场景。", "11/17", false),
                    I(33, 5, "信念 R5：霞的后续", "芳泽霞", "不是真结局条件；没到 R5 她的社群本周目直接锁死。", "12/22", false),
                },
            },
            new Group
            {
                Name = "战斗 · 迷宫", Tint = TintBattle,
                Items = new[]
                {
                    I(8, 7, "瞬杀", "坂本龙司", "奔跑撞上比自己低 9 级以上的阴影，直接消灭。练级与刷素材的核心。", "", false),
                    I(33, 4, "锁链勾绳", "芳泽霞", "在殿堂里从敌人背后远距离勾过去，稳定先制遇敌。", "", false),
                    I(3, 7, "阴影因数分解", "新岛真", "光标停在敌人身上即可看到「无效 / 反弹 / 吸收」，不用试错。", "", false),
                    I(10, 2, "印象空间搜寻", "佐仓双叶", "切换楼层时有几率把整层地图直接开出来。", "", false),
                    I(5, 7, "即兴彩绘", "喜多川祐介", "随时随地进行技能卡复制与制作，不用回学校。", "", false),
                    I(2, 5, "强夺秘宝", "摩尔加纳", "近战命中后有机会从敌人身上掏道具。", "", false),
                },
            },
            new Group
            {
                Name = "时间 · 行动", Tint = TintTime,
                Items = new[]
                {
                    I(15, 10, "特别按摩★", "川上贞代", "从异世界回来的当晚，按摩后照样能出门。注意是 R10，不是 R5。", "", true),
                    I(15, 1, "混水摸鱼★", "川上贞代", "指定日期的上课时间可以拿来练自己，不占行动。", "", false),
                    I(15, 5, "彻底混水摸鱼", "川上贞代", "能摸鱼的课变多（6/16、6/25、7/6、9/30、10/31、11/11、11/16、1/20、1/25）。", "", false),
                    I(15, 3, "家事代理", "川上贞代", "泡咖啡、投币洗衣交给她做，不占时间段。R9 后免费。", "", false),
                    I(15, 6, "超级家事代理", "川上贞代", "潜入道具与咖喱也能外包，等于白送一个时间段。", "", false),
                },
            },
            new Group
            {
                Name = "交涉 · 金钱", Tint = TintTalk,
                Items = new[]
                {
                    I(20, 2, "交涉术", "吉田寅之助", "要钱、要道具之后有机会再追加一次——反复要钱的基础。", "", false),
                    I(20, 3, "募集经费", "吉田寅之助", "有几率要到更多钱。", "", false),
                    I(20, 8, "人心掌握术", "吉田寅之助", "交涉时必定说服阴影成为面具。", "", false),
                    I(20, 10, "魅力演讲术", "吉田寅之助", "连比自己等级高的阴影也能收。", "", false),
                    I(9, 7, "打折优惠", "明智吾郎", "诊所药品打折，长期省一大笔。", "", false),
                },
            },
            new Group
            {
                Name = "五维 · 效率", Tint = TintStat,
                Items = new[]
                {
                    I(0, 0, "《图解·顿悟速读术》", "学校图书馆 · 7/1 起", "读完后看书一次顶两次进度，等于把地铁时间翻倍。", "", false),
                    I(0, 0, "《因数分解学习法》", "吉祥寺 · 7/26~30 物物交换", "读完后「学习」额外加知识。", "", false),
                    I(0, 0, "《震撼灵魂的观影技巧》", "新宿书店 · 看过一次电影后", "看 DVD / 电影额外 +1♪。", "", false),
                    I(0, 0, "《游戏秘技宝典》", "新宿书店 · 玩过一次游戏后", "游戏难度下降，通关更快，通关还能去秋叶原换饰品。", "", false),
                    I(0, 0, "DVD 播放机 + 怀旧游戏", "四轩茶屋二手店 / 杂货店", "买齐才会开启「在家看 DVD」「玩游戏」两条五维来源。", "", false),
                },
            },
        };
    }
}
