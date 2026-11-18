# -*- coding: utf-8 -*-
"""
P5R 五维实时监视器 —— 直接读运行中游戏的内存，无需安装任何 Mod、无需重启游戏。

用法：
    python P5R五维实时监视.py

首次启动会在 P5R.exe 里扫描五维数组地址（约 2~5 秒），之后每秒刷新一次。
按 Ctrl+C 退出。
"""
import ctypes, ctypes.wintypes as wt, struct, subprocess, sys, time, os

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400

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

PREFIX = b'\x4C\x8D\x35'
SUFFIX = b'\x0F\xB7\xFD'
READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wt.DWORD), ("__a1", wt.DWORD),
                ("RegionSize", ctypes.c_ulonglong), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__a2", wt.DWORD)]


class MODULEENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("GlblcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD), ("modBaseAddr", ctypes.c_void_p),
                ("modBaseSize", wt.DWORD), ("hModule", wt.HMODULE),
                ("szModule", ctypes.c_char * 256), ("szExePath", ctypes.c_char * 260)]


def find_pid(name='p5r.exe'):
    try:
        out = subprocess.check_output(['tasklist', '/FO', 'CSV', '/NH'], text=True, errors='ignore')
    except Exception:
        return None
    for line in out.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if p and p[0].lower() == name:
            return int(p[1])
    return None


def module_base(pid):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    me = MODULEENTRY32(); me.dwSize = ctypes.sizeof(me)
    if k32.Module32First(snap, ctypes.byref(me)):
        while True:
            if me.szModule.lower().startswith(b'p5r'):
                k32.CloseHandle(snap)
                return me.modBaseAddr, me.modBaseSize
            if not k32.Module32Next(snap, ctypes.byref(me)):
                break
    k32.CloseHandle(snap)
    return None, 0


def read(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[:n.value]


def scan(h, base, msize):
    """返回所有命中的数组地址"""
    hits = []
    addr, end = base, base + msize
    mbi = MBI()
    while addr < end:
        if k32.VirtualQueryEx(ctypes.c_void_p(h), ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            break
        rsize = mbi.RegionSize
        if mbi.State == 0x1000 and (mbi.Protect & READABLE) and not (mbi.Protect & 0x100):
            chunk, off = 4 * 1024 * 1024, 0
            while off < rsize:
                n = min(chunk, rsize - off)
                data = read(h, mbi.BaseAddress + off, n)
                if data:
                    i = 0
                    while True:
                        i = data.find(PREFIX, i)
                        if i < 0:
                            break
                        if i + 10 <= len(data) and data[i + 7:i + 10] == SUFFIX:
                            insn = mbi.BaseAddress + off + i
                            d = read(h, insn + 3, 4)
                            if d:
                                tgt = insn + 7 + struct.unpack('<i', d)[0]
                                v = read(h, tgt, 10)
                                if v:
                                    vals = struct.unpack('<5h', v)
                                    if all(-1 <= x <= 3000 for x in vals):
                                        hits.append((tgt, vals))
                        i += 1
                off += n
        addr = mbi.BaseAddress + rsize
    return hits


def level_of(idx, pts):
    t = THRESHOLDS[idx]
    for k in range(4, -1, -1):
        if pts >= t[k]:
            return k + 1
    return 1


def gap_of(idx, pts):
    t = THRESHOLDS[idx]
    lv = level_of(idx, pts)
    if lv >= 5:
        return f"满级+{pts - t[4]}"
    return f"还差{t[lv] - pts}"


def bar(pts, idx, width=20):
    t = THRESHOLDS[idx]
    lv = level_of(idx, pts)
    if lv >= 5:
        filled = width
    else:
        lo = t[lv - 1]
        hi = t[lv]
        filled = int(width * (pts - lo) / max(1, hi - lo))
    return "█" * filled + "·" * (width - filled)


def main():
    pid = find_pid()
    if not pid:
        print("没有找到运行中的 P5R.exe，请先启动游戏（通过 Reloaded-II 或 Steam 都可以）")
        input("按回车退出...")
        return

    h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
    if not h:
        print(f"无法打开游戏进程（PID {pid}），请用管理员身份运行本程序")
        input("按回车退出...")
        return

    base, msize = module_base(pid)
    print(f"游戏 PID {pid}，模块基址 0x{base:X}")
    print("正在扫描五维数组地址...")
    t0 = time.time()
    hits = scan(h, base, msize)
    if not hits:
        print("没找到五维数组签名。游戏版本可能不是 1.0.4。")
        input("按回车退出...")
        return

    # 选第一个值不是全 0 的命中（全 0 可能是未初始化）
    target, vals = hits[0]
    for a, v in hits:
        if any(x != 0 for x in v):
            target, vals = a, v
            break
    print(f"找到 {len(hits)} 处候选，使用 0x{target:X}（扫描耗时 {time.time() - t0:.1f}s）")
    time.sleep(1)

    os.system('')  # 启用 Windows 终端 ANSI 支持
    last = None
    try:
        while True:
            raw = read(h, target, 10)
            if not raw:
                print("读取失败，游戏可能已退出")
                break
            v = struct.unpack('<5h', raw)
            if v != last:
                lines = []
                lines.append("=" * 62)
                lines.append(f" P5R 五维实时监视    {time.strftime('%H:%M:%S')}   Ctrl+C 退出")
                lines.append("=" * 62)
                for i in ORDER:
                    lines.append(f" {NAMES[i]} {NAMES_EN[i]:<12} {v[i]:4d}  Lv{level_of(i, v[i])}  "
                                 f"[{bar(v[i], i)}]  {gap_of(i, v[i])}")
                lines.append("=" * 62)
                if all(x == 0 for x in v):
                    lines.append(" 当前全 0：还在标题画面或没载入存档")
                out = "\n".join(lines)
                sys.stdout.write("\033[2J\033[H" + out + "\n")
                sys.stdout.flush()
                last = v
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n已退出")


if __name__ == '__main__':
    main()
