# -*- coding: utf-8 -*-
"""
P5R 五维查看器 —— 直接解密读取存档，显示全部 16 个存档格的五维数值。

用法：
    python P5R五维查看器.py            # 读取全部存档格
    python P5R五维查看器.py 10         # 只读 DATA10
"""
import base64, zlib, os, struct, sys, time, glob
from Crypto.Cipher import AES

KEY = base64.b64decode("3lOZS0kYSoOOtkC4c7IDfvNXnxIprUPTlUGVC3yBJF0=")

# 五维在解密后主数据里的偏移
STAT_OFFSET = 0x139E0

# 内存/存档顺序：[知识, 魅力, 灵巧, 勇气, 体贴]
NAMES = ["知识", "魅力", "灵巧", "勇气", "体贴"]
NAMES_EN = ["Knowledge", "Kindness", "Proficiency", "Guts", "Charm"]
THRESHOLDS = [
    [0, 34, 82, 126, 192],
    [0, 6, 52, 92, 132],
    [0, 12, 34, 60, 87],
    [0, 11, 38, 68, 113],
    [0, 14, 44, 91, 136],
]


def default_root():
    p = os.path.join(os.environ.get("APPDATA", ""), "SEGA", "P5R", "Steam")
    if not os.path.isdir(p):
        return None
    subs = [d for d in glob.glob(os.path.join(p, "*", "savedata")) if os.path.isdir(d)]
    return subs[0] if subs else None


def decrypt_save(path):
    """返回 (预览段, 主数据段)"""
    d = open(path, "rb").read()
    if d[:4] != b"DATA":
        raise ValueError("不是 P5R 存档")
    pt = AES.new(KEY, AES.MODE_CBC, d[0x10:0x20]).decrypt(d[0x20:])
    o = zlib.decompressobj()
    preview = o.decompress(pt[0x20:])
    u = o.unused_data
    i = u.find(b"\x78\xda")
    o2 = zlib.decompressobj()
    main = o2.decompress(u[i:])
    return preview, main


def level_of(idx, points):
    t = THRESHOLDS[idx]
    for k in range(4, -1, -1):
        if points >= t[k]:
            return k + 1
    return 1


def next_gap(idx, points):
    t = THRESHOLDS[idx]
    lv = level_of(idx, points)
    if lv >= 5:
        return f"满级+{points - t[4]}"
    return f"还差{t[lv] - points}"


def read_slot(root, n):
    path = os.path.join(root, f"DATA{n:02d}", "DATA.DAT")
    if not os.path.exists(path):
        return None
    preview, main = decrypt_save(path)
    vals = struct.unpack_from("<5h", main, STAT_OFFSET)
    return {
        "slot": f"DATA{n:02d}",
        "mtime": os.path.getmtime(path),
        "values": vals,
        "path": path,
    }


def main():
    root = default_root()
    if len(sys.argv) > 1:
        arg = sys.argv[1]
        if arg.isdigit():
            slots = [int(arg)]
        else:
            root = arg
            slots = range(1, 17)
    else:
        slots = range(1, 17)

    if not root:
        print("找不到存档目录，请把 savedata 路径作为参数传入")
        return

    rows = []
    for n in slots:
        try:
            r = read_slot(root, n)
            if r:
                rows.append(r)
        except Exception as e:
            print(f"DATA{n:02d} 读取失败：{e}")

    if not rows:
        print("没有可读的存档")
        return

    rows.sort(key=lambda r: -r["mtime"])

    print()
    print("P5R 五维查看器   存档目录：" + root)
    print("=" * 96)
    header = f"{'存档':<8}{'保存时间':<14}" + "".join(f"{c:<14}" for c in NAMES)
    print(header)
    print("-" * 96)
    for r in rows:
        t = time.strftime("%m-%d %H:%M", time.localtime(r["mtime"]))
        line = f"{r['slot']:<8}{t:<14}"
        for i, v in enumerate(r["values"]):
            lv = level_of(i, v)
            line += f"{v}(Lv{lv})".ljust(14)
        print(line)
    print("-" * 96)

    # 最新存档的详细信息
    latest = rows[0]
    print()
    print(f"最新存档 {latest['slot']} 详细：")
    for i, v in enumerate(latest["values"]):
        print(f"  {NAMES[i]}({NAMES_EN[i]})  {v:3d} 点   Lv{level_of(i, v)}   {next_gap(i, v)}")
    print()

    # 同时写入 txt
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "五维报告.txt")
    with open(out, "w", encoding="utf-8") as f:
        f.write("P5R 五维报告   " + time.strftime("%Y-%m-%d %H:%M:%S") + "\n")
        f.write("=" * 96 + "\n")
        f.write(header + "\n" + "-" * 96 + "\n")
        for r in rows:
            t = time.strftime("%Y-%m-%d %H:%M", time.localtime(r["mtime"]))
            line = f"{r['slot']:<8}{t:<18}"
            for i, v in enumerate(r["values"]):
                line += f"{v}(Lv{level_of(i, v)})".ljust(14)
            f.write(line + "\n")
        f.write("-" * 96 + "\n\n")
        f.write(f"最新存档 {latest['slot']} 详细：\n")
        for i, v in enumerate(latest["values"]):
            f.write(f"  {NAMES[i]}({NAMES_EN[i]})  {v:3d} 点   Lv{level_of(i, v)}   {next_gap(i, v)}\n")
    print(f"报告已写入：{out}")


if __name__ == "__main__":
    main()
