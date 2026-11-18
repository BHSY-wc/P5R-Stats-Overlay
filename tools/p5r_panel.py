# -*- coding: utf-8 -*-
"""
P5R 实时状态面板
================
一个脚本同时显示「五维」和「Coop 羁绊」，全部直接读运行中游戏的内存 ——
不需要安装 Mod，不需要重启游戏，数值变化即时可见。

用法：
    python p5r_panel.py            实时面板（游戏需运行）
    python p5r_panel.py --save     游戏没开时，改读最新存档
    python p5r_panel.py --no-color 关闭颜色（管道/重定向时自动关闭）

面板内按键：
    S  立即导出 P5R状态.txt
    Q  退出（Esc 同）

数据来源：
    五维  —— 内存 signature 4C 8D 35 ?? ?? ?? ?? 0F B7 FD，指向 short[5]
    Coop  —— 内存中五维数组前面的定长数组，每条 16 字节：id / rank / 好感点
    阈值  —— coop_need.py（转录自 bilibili 游戏 Wiki 各 COOP 页「触发条件」）
"""
import base64
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import subprocess
import sys
import time
import unicodedata
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
try:
    from coop_need import requirement
except Exception:                                    # pragma: no cover
    def requirement(cid, rank):
        return None, None

# ────────────────────────────────────────────────────────── 基础数据 ──
# 内存/存档顺序：[知识, 魅力, 灵巧, 勇气, 体贴]
STAT_NAMES = ["知识", "魅力", "灵巧", "勇气", "体贴"]
STAT_EN = ["Knowledge", "Charm", "Proficiency", "Guts", "Kindness"]

# 每级起点（0 级起点固定为 0），Lv1~Lv5
STAT_TH = [
    [0, 34, 82, 126, 192],
    [0, 6, 52, 92, 132],
    [0, 12, 34, 60, 87],
    [0, 11, 38, 68, 113],
    [0, 14, 44, 91, 136],
]

# 游戏内部 Confidant ID（Amicitia Wiki — Persona 5 Royal/Confidants）
COOP = {
    1:  ("伊戈尔", "愚者"),
    2:  ("摩尔加纳", "魔术师"),
    3:  ("新岛真", "女教皇"),
    4:  ("奥村春", "女帝"),
    5:  ("喜多川祐介", "皇帝"),
    6:  ("佐仓惣治郎", "教皇"),
    7:  ("高卷杏", "恋爱"),
    8:  ("坂本龙司", "战车"),
    9:  ("明智吾郎", "正义"),
    10: ("佐仓双叶", "隐者"),
    11: ("御船千早", "命运"),
    12: ("卡萝琳&贾斯汀", "力量"),
    13: ("岩井宗久", "刑死者"),
    14: ("武见妙", "死神"),
    15: ("川上贞代", "节制"),
    16: ("大宅一子", "恶魔"),
    17: ("织田信也", "塔"),
    18: ("东乡一二三", "星"),
    19: ("三岛由辉", "月"),
    20: ("吉田寅之助", "太阳"),
    21: ("新岛冴", "审判"),
    33: ("芳泽霞", "信念"),
    35: ("丸喜拓人", "顾问官"),
}

SAVE_KEY = base64.b64decode("3lOZS0kYSoOOtkC4c7IDfvNXnxIprUPTlUGVC3yBJF0=")
SAVE_STAT_OFF = 0x139E0
SAVE_COOP_OFF = 0x136A0
SAVE_COOP_N = 26

# ────────────────────────────────────────────────────────── ANSI 颜色 ──
_C = {"reset": "\033[0m", "dim": "\033[2m", "b": "\033[1m",
      "cyan": "\033[96m", "green": "\033[92m", "yellow": "\033[93m",
      "red": "\033[91m", "magenta": "\033[95m", "grey": "\033[90m", "blue": "\033[94m"}
USE_COLOR = False


def paint(text, key):
    if not USE_COLOR or not key:
        return text
    return _C.get(key, "") + text + _C["reset"]


# ────────────────────────────────────────────────────────── 排版工具 ──
def dwidth(s):
    """按终端列宽计算字符串宽度（东亚全角算 2）"""
    w = 0
    for ch in s:
        w += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return w


def pad(s, width, align="left"):
    gap = width - dwidth(s)
    if gap <= 0:
        return s
    if align == "right":
        return " " * gap + s
    if align == "center":
        left = gap // 2
        return " " * left + s + " " * (gap - left)
    return s + " " * gap


RULE_W = 74


def rule(ch="─"):
    return "  " + ch * RULE_W


def section(title, note="", color="cyan"):
    """分节标题：── 五 维 ────────────────────"""
    head = "  " + title + " "
    tail = ""
    if note:
        tail = "  " + note
    fill = RULE_W + 2 - dwidth(head) - dwidth(tail)
    if fill < 3:
        fill = 3
    return paint(head, color) + paint("─" * fill, "grey") + paint(tail, "dim")


# ────────────────────────────────────────────────────────── 数值计算 ──
def stat_level(idx, pts):
    t = STAT_TH[idx]
    for k in range(4, -1, -1):
        if pts >= t[k]:
            return k + 1
    return 1


def stat_bar(idx, pts, width=10):
    t = STAT_TH[idx]
    lv = stat_level(idx, pts)
    if lv >= 5:
        return "█" * width
    lo, hi = t[lv - 1], t[lv]
    filled = int(round(width * (pts - lo) / max(1, hi - lo)))
    filled = max(0, min(width, filled))
    return "█" * filled + "░" * (width - filled)


def stat_next(idx, pts):
    """返回 (下一级文案, 还差点数 or None)"""
    t = STAT_TH[idx]
    lv = stat_level(idx, pts)
    if lv >= 5:
        return "MAX", None
    return f"Lv{lv}→{lv + 1}", t[lv] - pts


def need_bar(aff, need, width=10):
    if not need or need <= 0:
        return "█" * width
    filled = int(round(width * min(1.0, aff / need)))
    filled = max(0, min(width, filled))
    return "█" * filled + "░" * (width - filled)


# ────────────────────────────────────────────────────────── 内存读取 ──
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
PREFIX = b"\x4C\x8D\x35"
SUFFIX = b"\x0F\xB7\xFD"
READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80
CACHE_PATH = os.path.join(HERE, "_mem_addr.json")


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wt.DWORD), ("__a1", wt.DWORD),
                ("RegionSize", ctypes.c_ulonglong), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__a2", wt.DWORD)]


class MODULEENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("GlblcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD),
                ("modBaseAddr", ctypes.c_void_p), ("modBaseSize", wt.DWORD),
                ("hModule", wt.HMODULE), ("szModule", ctypes.c_char * 256),
                ("szExePath", ctypes.c_char * 260)]


def find_pid(name="p5r.exe"):
    try:
        out = subprocess.check_output(["tasklist", "/FO", "CSV", "/NH"],
                                      text=True, errors="ignore", timeout=10)
    except Exception:
        return None
    for line in out.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if p and p[0].lower() == name:
            try:
                return int(p[1])
            except ValueError:
                return None
    return None


def module_base(pid):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    me = MODULEENTRY32()
    me.dwSize = ctypes.sizeof(me)
    if k32.Module32First(snap, ctypes.byref(me)):
        while True:
            if me.szModule.lower().startswith(b"p5r"):
                k32.CloseHandle(snap)
                return me.modBaseAddr, me.modBaseSize
            if not k32.Module32Next(snap, ctypes.byref(me)):
                break
    k32.CloseHandle(snap)
    return None, 0


def rpm(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[:n.value]


def scan_stats(h, base, msize, progress=None):
    """扫描五维数组地址，返回候选 [(addr, vals)]"""
    hits = []
    addr, end = base, base + msize
    mbi = MBI()
    while addr < end:
        if k32.VirtualQueryEx(ctypes.c_void_p(h), ctypes.c_void_p(addr),
                              ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            break
        if mbi.State == 0x1000 and (mbi.Protect & READABLE) and not (mbi.Protect & 0x100):
            data = rpm(h, mbi.BaseAddress, mbi.RegionSize)
            if data:
                i = 0
                while True:
                    i = data.find(PREFIX, i)
                    if i < 0:
                        break
                    if i + 10 <= len(data) and data[i + 7:i + 10] == SUFFIX:
                        insn = mbi.BaseAddress + i
                        d = rpm(h, insn + 3, 4)
                        if d:
                            tgt = insn + 7 + struct.unpack("<i", d)[0]
                            v = rpm(h, tgt, 10)
                            if v:
                                vals = struct.unpack("<5h", v)
                                if all(-1 <= x <= 3000 for x in vals):
                                    hits.append((tgt, vals))
                    i += 1
        addr = mbi.BaseAddress + mbi.RegionSize
    return hits


def locate_coop(h, stats_addr):
    """五维数组前面就是 Coop 定长数组：16 字节/条，id/rank/好感 各 2 字节，后 10 字节补 0。
    返回 (起始地址, [(id, rank, aff), ...]) 或 (None, [])"""
    span = 0x600
    lo = stats_addr - span
    blob = rpm(h, lo, span)
    if not blob:
        return None, []
    best = (None, [])
    limit = len(blob) - 16
    for p in range(0, limit):
        recs = []
        ok = True
        for k in range(40):
            q = p + k * 16
            if q + 16 > len(blob):
                break
            cid, rk, af = struct.unpack_from("<3h", blob, q)
            if cid == 0 and rk == 0 and af == 0:
                break
            if cid not in COOP or not (0 <= rk <= 10) or not (0 <= af <= 3000):
                ok = False
                break
            if any(blob[q + 6:q + 16]):
                ok = False
                break
            recs.append((cid, rk, af))
        if ok and len(recs) >= 4 and len(recs) > len(best[1]):
            best = (lo + p, recs)
    return best


# ────────────────────────────────────────────────────────── 存档读取 ──
def save_root():
    p = os.path.join(os.environ.get("APPDATA", ""), "SEGA", "P5R", "Steam")
    if not os.path.isdir(p):
        return None
    for d in os.listdir(p):
        cand = os.path.join(p, d, "savedata")
        if os.path.isdir(cand):
            return cand
    return None


def read_save(root, slot):
    path = os.path.join(root, f"DATA{slot:02d}", "DATA.DAT")
    with open(path, "rb") as f:
        d = f.read()
    if d[:4] != b"DATA":
        raise ValueError("不是 P5R 存档")
    from Crypto.Cipher import AES
    pt = AES.new(SAVE_KEY, AES.MODE_CBC, d[0x10:0x20]).decrypt(d[0x20:])
    o = zlib.decompressobj()
    o.decompress(pt[0x20:])
    u = o.unused_data
    main = zlib.decompressobj().decompress(u[u.find(b"\x78\xda"):])
    stats = struct.unpack_from("<5h", main, SAVE_STAT_OFF)
    coop = []
    for k in range(SAVE_COOP_N):
        b = SAVE_COOP_OFF + k * 16
        cid, rk, af = struct.unpack_from("<3h", main, b + 6)
        if cid:
            coop.append((cid, rk, af))
    return {"stats": stats, "coop": coop, "mtime": os.path.getmtime(path),
            "slot": f"DATA{slot:02d}"}


def newest_save(root):
    best = None
    for n in range(1, 17):
        try:
            s = read_save(root, n)
        except Exception:
            continue
        if best is None or s["mtime"] > best["mtime"]:
            best = s
    return best


# ────────────────────────────────────────────────────────── 面板渲染 ──
def render(stats, coop, headline, subnote, footer_lines, deltas=None, coop_deltas=None):
    """返回整屏文本。
    deltas      : {维度索引: 增量}，用于在行尾显示 ↑+2（仅终端，导出时不传）
    coop_deltas : {confidant id: 增量}
    """
    deltas = deltas or {}
    coop_deltas = coop_deltas or {}
    W = RULE_W + 2
    out = []
    top = "+" + "-" * (W - 2) + "+"
    out.append("  " + top)
    out.append("  |" + pad(" P5R 状态面板", W - 2 - dwidth(headline) - 1) + headline + " |")
    out.append("  " + top)

    # ── 副标题（数据来源）
    out.append("   " + paint(subnote, "dim"))
    out.append("")

    # ── 五 维 ──
    total_lv = sum(stat_level(i, stats[i]) for i in range(5))
    out.append(section("五 维", f"总等级 {total_lv}/25"))
    for i in range(5):
        pts = stats[i]
        lv = stat_level(i, pts)
        nxt, gap = stat_next(i, pts)
        if gap is None:
            tail, col = "已满级", "magenta"
        elif gap <= 5:
            tail, col = f"距 Lv{lv + 1} 还差 {gap}", "green"
        elif gap <= 20:
            tail, col = f"距 Lv{lv + 1} 还差 {gap}", "yellow"
        else:
            tail, col = f"距 Lv{lv + 1} 还差 {gap}", "grey"
        line = ("   " + paint(pad(STAT_NAMES[i], 6), "b")
                + paint(pad(str(pts), 4, "right"), col)
                + paint(f"  Lv{lv}", "dim")
                + "  " + paint(stat_bar(i, pts), col)
                + "   " + paint(tail, col))
        d = deltas.get(i)
        if d:
            line += paint(f"   ↑+{d}", "green")
        out.append(line.rstrip())
    out.append("")

    # ── Coop 羁绊 ──
    ready, accum, special, maxed = [], [], [], []
    for cid, rank, aff in coop:
        nm, arcana = COOP.get(cid, (f"未知{cid}", "?"))
        need, extra = requirement(cid, rank)
        item = {"cid": cid, "name": nm, "arcana": arcana, "rank": rank,
                "aff": aff, "need": need, "extra": extra}
        if rank >= 10:
            maxed.append(item)
        elif need is None:
            special.append(item)
        elif aff >= need:
            ready.append(item)
        else:
            accum.append(item)

    ready.sort(key=lambda x: x["name"])
    accum.sort(key=lambda x: (x["need"] - x["aff"], -x["rank"]))
    special.sort(key=lambda x: -x["rank"])
    maxed.sort(key=lambda x: -x["rank"])

    n_all = len(coop)
    out.append(section("Coop 羁绊",
                       f"{n_all}/23 已解锁 · 可升 {len(ready)} · 满级 {len(maxed)}"))

    def row(it, tag_col):
        nm = pad(it["name"], 14)
        arc = pad(it["arcana"], 7)
        rk = pad(f"R{it['rank']}", 4)
        need = it["need"]
        if it["rank"] >= 10:
            b, num, tail = "█" * 10, pad(f"{it['aff']}", 8), "已满级"
        elif need is None:
            b, num = "░" * 10, pad("—", 8)
            extra = it["extra"] or ""
            tail = "事件触发" if extra in ("无", "") else extra
        else:
            b = need_bar(it["aff"], need)
            num = pad(f"{it['aff']}/{need}", 8)
            gap = need - it["aff"]
            tail = "下次见面即可升级" if gap <= 0 else f"还差 {gap} 点"
        cd = coop_deltas.get(it["cid"])
        return ("   " + nm + paint(arc, "dim") + paint(rk, "dim")
                + paint(b, tag_col) + "  " + num + paint(tail, tag_col)
                + (paint(f"   ↑+{cd}", "green") if cd else "")).rstrip()

    def group(title, items, col, blank_before):
        if not items:
            return
        if blank_before and out:
            out.append("")
        out.append("   " + paint("● " + title, col))
        for it in items:
            out.append(row(it, col))

    group("可以升级", ready, "green", False)
    group("继续积累", accum, "yellow", bool(ready))
    group("不看点数（任务 / 剧情 / 人格指数）", special, "grey", bool(ready or accum))
    group("已满级", maxed, "magenta", bool(ready or accum or special))

    out.append("")
    out.append("  " + paint("─" * RULE_W, "grey"))
    for fl in footer_lines:
        out.append("   " + paint(fl, "dim"))
    return "\n".join(out)


def footer_for_file():
    return [
        "五维进度条＝当前等级内的完成度；「距 Lv4 还差 36」＝再攒 36 点升级。",
        "好感＝内部羁绊点数（界面上 1 个音符 ＝ 5 点）；「21/35」＝当前 / 升级所需。",
        "带同阿尔卡那面具 ×1.5，考试前十 ×1.2 / 第一 ×1.5，立刻回应邀请 ×1.2。",
        "升级瞬间好感清零并重新累计；完整逐级需求表见《Coop好感度需求表.md》。",
    ]


FOOTER_LIVE = [
    "[S] 导出 P5R状态.txt    [Q] 退出    ↑+N = 最近 8 秒内的增长",
    "好感＝内部羁绊点数（界面音符 ＝ 5 点/个）；升级后清零重算。",
]


# ────────────────────────────────────────────────────────── 存档模式 ──
def run_save_mode(color):
    global USE_COLOR
    USE_COLOR = color
    try:
        from Crypto.Cipher import AES  # noqa: F401
    except ImportError:
        print("缺少解密库 pycryptodome，请先执行：  python -m pip install pycryptodome")
        return 1
    root = save_root()
    if not root:
        print("找不到存档目录：%APPDATA%\\SEGA\\P5R\\Steam")
        return 1
    s = newest_save(root)
    if not s:
        print("没有可读的存档")
        return 1
    when = time.strftime("%Y-%m-%d %H:%M", time.localtime(s["mtime"]))
    text = render(s["stats"], s["coop"],
                  time.strftime("%Y-%m-%d %H:%M:%S"),
                  f"数据来源 · 存档 {s['slot']}（{when} 保存，游戏未运行）",
                  ["存档模式：数值为最后一次保存时的状态。启动游戏后可读实时数据。"])
    print(text)
    out = os.path.join(HERE, "P5R状态.txt")
    with open(out, "w", encoding="utf-8-sig", newline="\n") as f:
        f.write(render(s["stats"], s["coop"], when,
                       f"数据来源 · 存档 {s['slot']}（{when} 保存）",
                       footer_for_file()) + "\n")
    print(f"\n  已导出：{out}")
    return 0


# ────────────────────────────────────────────────────────── 实时模式 ──
def main():
    global USE_COLOR
    args = sys.argv[1:]
    color = ("--no-color" not in args) and not os.environ.get("NO_COLOR") \
        and sys.stdout.isatty()
    if "--save" in args:
        return run_save_mode(color)
    USE_COLOR = color

    pid = find_pid()
    if not pid:
        print("  没有检测到运行中的 P5R.exe —— 已切换为「存档模式」。")
        print()
        return run_save_mode(color)

    h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
    if not h:
        print(f"无法打开游戏进程（PID {pid}），请以管理员身份运行。")
        return 1

    base, msize = module_base(pid)
    stats_addr = None
    print("正在定位五维数组（首次约 2 秒，之后用缓存）...")

    if os.path.exists(CACHE_PATH):
        try:
            c = json.load(open(CACHE_PATH, encoding="utf-8"))
            if c.get("pid") == pid and rpm(h, c["addr"], 10):
                stats_addr = c["addr"]
        except Exception:
            pass

    if stats_addr is None:
        hits = scan_stats(h, base, msize)
        if not hits:
            print("没找到五维数组签名，游戏版本可能不是 1.0.4。")
            return 1
        stats_addr = hits[0][0]
        for a, v in hits:
            if any(x != 0 for x in v):
                stats_addr = a
                break
        try:
            json.dump({"pid": pid, "addr": stats_addr}, open(CACHE_PATH, "w", encoding="utf-8"))
        except Exception:
            pass

    coop_addr, coop_recs = locate_coop(h, stats_addr)
    save_info = newest_save(save_root()) if save_root() else None
    save_tag = ""
    if save_info:
        save_tag = f"{save_info['slot']} · " + \
            time.strftime("%m-%d %H:%M", time.localtime(save_info["mtime"]))

    os.system("")                                    # 打开 ANSI 支持
    last_text = None
    last_sig = None
    last_write = 0.0
    prev_stats = None
    prev_coop = {}
    stat_changes = {}
    coop_changes = {}
    HOLD = 8.0                                       # 增量提示保留秒数
    txt_path = os.path.join(HERE, "P5R状态.txt")
    file_sub = "数据源 · 内存实时读（游戏运行中）"
    if save_tag:
        file_sub += f"      存档参考 · {save_tag}"

    def dump_txt(now, stats, coop):
        try:
            with open(txt_path, "w", encoding="utf-8-sig", newline="\n") as f:
                f.write(render(stats, coop, now, file_sub,
                               footer_for_file()) + "\n")
            return True
        except Exception:
            return False

    try:
        miss = 0
        rescanned = False
        while True:
            raw = rpm(h, stats_addr, 10)
            if not raw:
                miss += 1
                if miss == 1 and not rescanned:
                    # 地址失效（游戏重启/换档），重扫一次
                    rescanned = True
                    try:
                        os.remove(CACHE_PATH)
                    except OSError:
                        pass
                    hits = scan_stats(h, base, msize)
                    if hits:
                        stats_addr = hits[0][0]
                        for a, v in hits:
                            if any(x != 0 for x in v):
                                stats_addr = a
                                break
                        coop_addr, _ = locate_coop(h, stats_addr)
                        miss = 0
                        continue
                if miss > 6:
                    print("\n读取失败，游戏可能已退出。")
                    break
                time.sleep(0.5)
                continue
            miss = 0
            stats = struct.unpack("<5h", raw)

            coop = []
            coop_live = True
            if coop_addr:
                blob = rpm(h, coop_addr, 16 * 40)
                if blob:
                    for k in range(40):
                        q = k * 16
                        if q + 16 > len(blob):
                            break
                        cid, rk, af = struct.unpack_from("<3h", blob, q)
                        if cid == 0 and rk == 0 and af == 0:
                            break
                        if cid in COOP and 0 <= rk <= 10:
                            coop.append((cid, rk, af))
            if not coop:
                # 内存里读不到就退回存档（只用于显示，不影响五维实时）
                coop_live = False
                if save_info:
                    coop = save_info["coop"]

            now = time.strftime("%Y-%m-%d %H:%M:%S")

            # 记录增量（仅内存数据，存档回退不参与）
            t_now = time.time()
            if prev_stats is not None:
                for i in range(5):
                    if stats[i] != prev_stats[i]:
                        stat_changes[i] = (stats[i] - prev_stats[i], t_now)
            prev_stats = stats
            if coop_live:
                cur = {cid: af for cid, rk, af in coop}
                for cid, af in cur.items():
                    if cid in prev_coop and af != prev_coop[cid]:
                        coop_changes[cid] = (af - prev_coop[cid], t_now)
                prev_coop = cur
            live_stats = {i: v[0] for i, v in stat_changes.items()
                          if v[0] > 0 and t_now - v[1] < HOLD}
            live_coop = {c: v[0] for c, v in coop_changes.items()
                         if v[0] > 0 and t_now - v[1] < HOLD}

            if coop_live:
                sub = f"数据源 · 内存实时读（PID {pid}）"
                if save_tag:
                    sub += f"      存档参考 · {save_tag}"
            else:
                sub = f"数据源 · 五维内存实时 · Coop 取自存档 {save_tag or '（无）'}"
            text = render(stats, coop, now, sub, FOOTER_LIVE, live_stats, live_coop)

            if text != last_text:
                sys.stdout.write("\033[H\033[J" + text + "\n")
                sys.stdout.flush()
                last_text = text

            # 报告文件：数值有变化时写（最短间隔 5 秒）
            sig = (tuple(stats), tuple(coop))
            if sig != last_sig and time.time() - last_write > 5:
                if dump_txt(now, stats, coop):
                    last_sig = sig
                    last_write = time.time()

            # 非阻塞按键
            if os.name == "nt":
                import msvcrt
                if msvcrt.kbhit():
                    k = msvcrt.getwch().lower()
                    if k in ("q", "\x1b"):
                        break
                    if k == "s":
                        dump_txt(now, stats, coop)
                        last_sig = sig
                        last_write = time.time()
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass

    print(f"\n  面板已退出，报告保存在：{txt_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
