# -*- coding: utf-8 -*-
"""
P5R 存档读取器 —— 解密存档，显示五维和全部已解锁 Coop 的等级与好感度。

用法：
    python p5r_stats_coop.py            # 读最新存档
    python p5r_stats_coop.py 10         # 只读 DATA10
    python p5r_stats_coop.py all        # 列出全部 16 格
"""
import base64, zlib, os, struct, sys, time, glob
from Crypto.Cipher import AES

try:
    from coop_need import requirement
except ImportError:          # 从其他目录运行时兜底
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from coop_need import requirement

KEY = base64.b64decode("3lOZS0kYSoOOtkC4c7IDfvNXnxIprUPTlUGVC3yBJF0=")

STAT_OFFSET = 0x139E0     # 五维 short[5]
COOP_OFFSET = 0x136A0     # Coop 数组，每条 16 字节
COOP_COUNT = 26

# 内存/存档顺序：[知识, 魅力, 灵巧, 勇气, 体贴]
NAMES = ["知识", "魅力", "灵巧", "勇气", "体贴"]
NAMES_EN = ["Knowledge", "Charm", "Proficiency", "Guts", "Kindness"]
# 游戏菜单里的显示顺序（知识/勇气/灵巧/体贴/魅力），值为NAMES的下标。
# 只影响打印排列，读数据仍按内存下标。
ORDER = [0, 3, 2, 4, 1]
THRESHOLDS = [
    [0, 34, 82, 126, 192],
    [0, 6, 52, 92, 132],
    [0, 12, 34, 60, 87],
    [0, 11, 38, 68, 113],
    [0, 14, 44, 91, 136],
]

# 游戏内部 Confidant ID（来源：Amicitia Wiki — Persona 5 Royal/Confidants）
COOP_NAMES = {
    1:  ("伊戈尔",     "愚者",   "Igor"),
    2:  ("摩尔加纳",   "魔术师", "Morgana"),
    3:  ("新岛真",     "女教皇", "Makoto"),
    4:  ("奥村春",     "女帝",   "Haru"),
    5:  ("喜多川祐介", "皇帝",   "Yusuke"),
    6:  ("佐仓惣治郎", "教皇",   "Sojiro"),
    7:  ("高卷杏",     "恋爱",   "Ann"),
    8:  ("坂本龙司",   "战车",   "Ryuji"),
    9:  ("明智吾郎",   "正义",   "Akechi"),
    10: ("佐仓双叶",   "隐者",   "Futaba"),
    11: ("御船千早",   "命运",   "Chihaya"),
    12: ("卡萝琳&贾斯汀", "力量", "Justine&Caroline"),
    13: ("岩井宗久",   "刑死者", "Iwai"),
    14: ("武见妙",     "死神",   "Takemi"),
    15: ("川上贞代",   "节制",   "Kawakami"),
    16: ("大宅一子",   "恶魔",   "Ohya"),
    17: ("织田信也",   "塔",     "Shinya"),
    18: ("一二三",     "星",     "Hifumi"),
    19: ("三岛由辉",   "月",     "Mishima"),
    20: ("吉田寅之助", "太阳",   "Yoshida"),
    21: ("新岛冴",     "审判",   "Sae"),
    33: ("芳泽霞",     "信念",   "Kasumi"),
    35: ("丸喜拓人",   "顾问官", "Maruki"),
}


def default_root():
    p = os.path.join(os.environ.get("APPDATA", ""), "SEGA", "P5R", "Steam")
    if not os.path.isdir(p):
        return None
    subs = [d for d in glob.glob(os.path.join(p, "*", "savedata")) if os.path.isdir(d)]
    return subs[0] if subs else None


def decrypt_save(path):
    d = open(path, "rb").read()
    if d[:4] != b"DATA":
        raise ValueError("不是 P5R 存档")
    pt = AES.new(KEY, AES.MODE_CBC, d[0x10:0x20]).decrypt(d[0x20:])
    o = zlib.decompressobj()
    preview = o.decompress(pt[0x20:])
    u = o.unused_data
    i = u.find(b"\x78\xda")
    main = zlib.decompressobj().decompress(u[i:])
    return preview, main


def level_of(idx, points):
    t = THRESHOLDS[idx]
    for k in range(4, -1, -1):
        if points >= t[k]:
            return k + 1
    return 1


def gap_of(idx, points):
    t = THRESHOLDS[idx]
    lv = level_of(idx, points)
    if lv >= 5:
        return f"满级+{points - t[4]}"
    return f"还差{t[lv] - points}"


def read_coop(main):
    """返回 [(id, rank, aff), ...]，只含已解锁"""
    out = []
    for k in range(COOP_COUNT):
        b = COOP_OFFSET + k * 16
        if b + 10 > len(main):
            break
        cid, rank, aff = struct.unpack_from("<3h", main, b + 6)
        if cid == 0:
            continue
        out.append((cid, rank, aff))
    return out


def load_slot(root, n):
    p = os.path.join(root, f"DATA{n:02d}", "DATA.DAT")
    if not os.path.exists(p):
        return None
    pv, mn = decrypt_save(p)
    return {
        "slot": f"DATA{n:02d}",
        "mtime": os.path.getmtime(p),
        "stats": struct.unpack_from("<5h", mn, STAT_OFFSET),
        "coop": read_coop(mn),
    }


def collect_history(root):
    """按时间顺序扫描全部存档，统计每个角色「升级前达到过的最高好感点」，
    用它作为该角色升一级大概需要多少点的参考。"""
    slots = []
    for n in range(1, 17):
        try:
            s = load_slot(root, n)
        except Exception:
            continue
        if s:
            slots.append(s)
    slots.sort(key=lambda s: s["mtime"])

    last = {}   # cid -> (rank, aff)
    ref = {}    # cid -> 升级前的最大好感点
    for s in slots:
        for cid, rank, aff in s["coop"]:
            if cid in last:
                pr, pa = last[cid]
                if rank > pr:
                    ref[cid] = max(ref.get(cid, 0), pa)
            last[cid] = (rank, aff)
    return ref


def main():
    root = default_root()
    mode = sys.argv[1] if len(sys.argv) > 1 else None
    if mode and not mode.isdigit() and mode != "all":
        root = mode

    if not root:
        print("找不到存档目录，可把 savedata 路径作为参数传入")
        return

    slots = []
    for n in range(1, 17):
        try:
            s = load_slot(root, n)
            if s:
                slots.append(s)
        except Exception as e:
            print(f"DATA{n:02d} 读取失败：{e}")

    if not slots:
        print("没有可读的存档")
        return

    slots.sort(key=lambda s: -s["mtime"])

    if mode == "all":
        print()
        print("全部存档格   " + root)
        print("=" * 86)
        print(f"{'存档':<8}{'保存时间':<14}{'知识':<7}{'魅力':<7}{'灵巧':<7}{'勇气':<7}{'体贴':<7}{'Coop数':<6}")
        print("-" * 86)
        for s in slots:
            t = time.strftime("%m-%d %H:%M", time.localtime(s["mtime"]))
            v = s["stats"]
            print(f"{s['slot']:<8}{t:<14}" + "".join(f"{x:<7}" for x in v) + f"{len(s['coop'])}")
        return

    if mode and mode.isdigit():
        want = f"DATA{int(mode):02d}"
        slots = [s for s in slots if s["slot"] == want] or slots[:1]

    cur = slots[0]

    print()
    print(f"存档 {cur['slot']}   保存于 {time.strftime('%Y-%m-%d %H:%M', time.localtime(cur['mtime']))}")
    print("=" * 78)

    print("\n【五维】")
    for i in ORDER:
        v = cur["stats"][i]
        print(f"  {NAMES[i]} {NAMES_EN[i]:<12} {v:4d} 点   Lv{level_of(i, v)}   {gap_of(i, v)}")

    print("\n【Coop 羁绊】")
    print(f"  {'角色':<13}{'塔罗':<7}{'等级':<7}{'好感':<6}{'下一级需要':<10}{'状态'}")
    print("  " + "-" * 78)

    coop = sorted(cur["coop"], key=lambda c: (-c[1], -c[2]))
    for cid, rank, aff in coop:
        nm, arcana, _ = COOP_NAMES.get(cid, (f"未知{cid}", "?", "?"))
        need, extra = requirement(cid, rank)
        if rank >= 10:
            need_s, state = "-", "已满级"
        elif need is None:
            need_s = "不看点数"
            state = extra or "条件升级"
        else:
            need_s = str(need)
            if aff >= need:
                state = "已达标，下次见面即可升"
            else:
                state = f"还差 {need - aff} 点"
            if extra:
                state = f"{state}｜{extra}"
        print(f"  {nm:<13}{arcana:<7}Rank {rank:<3}{aff:<6}{need_s:<10}{state}")

    print("\n  ※ 「好感」是游戏内部羁绊点数，界面上的音符＝基础 5 点/个。")
    print("    带同阿尔卡那面具 ×1.5，考试前十 ×1.2 / 第一 ×1.5，立刻回应邀请 ×1.2。")
    print("    「下一级需要」取自 bilibili 游戏 Wiki 各 COOP 页的「触发条件」，")
    print("    详细到每一级的完整表格见同目录《Coop好感度需求表.md》。")

    # 写报告
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Coop报告.txt")
    with open(out, "w", encoding="utf-8") as f:
        f.write(f"P5R 存档 {cur['slot']}  " +
                time.strftime("%Y-%m-%d %H:%M", time.localtime(cur["mtime"])) + "\n")
        f.write("=" * 78 + "\n\n【五维】\n")
        for i in ORDER:
            v = cur["stats"][i]
            f.write(f"  {NAMES[i]} {NAMES_EN[i]:<12} {v:4d} 点   Lv{level_of(i, v)}   {gap_of(i, v)}\n")
        f.write("\n【Coop 羁绊】\n")
        for cid, rank, aff in coop:
            nm, arcana, _ = COOP_NAMES.get(cid, (f"未知{cid}", "?", "?"))
            need, extra = requirement(cid, rank)
            if need is None:
                need_s = extra or "-"
            elif rank >= 10:
                need_s = "已满级"
            else:
                need_s = f"需要{need}/还差{max(0, need - aff)}"
            f.write(f"  {nm:<12}{arcana:<8}Rank {rank:<3}好感 {aff:<5}{need_s}\n")
    print(f"\n报告已写入：{out}")


if __name__ == "__main__":
    main()
