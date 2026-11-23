using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace P5ROverlay
{
    public sealed class CoopEntry
    {
        public int Id;
        public int Rank;
        public int Points;
    }

    public sealed class Snapshot
    {
        public bool Live;                      // true = 内存实时；false = 存档
        public short[] Stats = new short[5];
        public List<CoopEntry> Coops = new List<CoopEntry>();
        public string Source = "";             // 状态行文案
        public string SaveSlot = "";           // 存档参考（DATA11 · 22:46）
        public DateTime Stamp = DateTime.Now;

        // ---- 游戏内日期：dayIdx 从 2016-04-01 起算（P5R 的日历就是这么存的）
        public int GameDayIdx = -1;
        public int TimeZone = -1;              // 0~4 白天 / 5 放学后 / 6 夜晚 / 7 深夜
        public bool HasDate => GameDayIdx >= 0;

        public static readonly DateTime CalendarOrigin = new DateTime(2016, 4, 1);
        public DateTime GameDate => CalendarOrigin.AddDays(GameDayIdx);

        /// <summary>5 及以前算白天（上午/午休/放学后），6 起算夜晚。</summary>
        public bool IsNight => TimeZone >= 6;

        public string TimeZoneName => TimeZoneText(TimeZone);

        /// <summary>0~2 上午 / 3~4 白天 / 5 放学后 / 6 夜晚 / 7 深夜。</summary>
        public static string TimeZoneText(int tz)
        {
            if (tz < 0) return "";
            if (tz <= 2) return "上午";
            if (tz <= 4) return "白天";
            if (tz == 5) return "放学后";
            if (tz == 6) return "夜晚";
            return "深夜";
        }
    }

    /// <summary>
    /// 读 P5R 进程内存：五维 quick 数组 + 紧跟其前的 Coop 定长数组。
    /// 签名 4C 8D 35 ?? ?? ?? ?? 0F B7 FD（lea r14,[rip+d32] / movzx edi,bpl），1.0.4 命中 1 处。
    /// </summary>
    internal sealed class GameReader : IDisposable
    {
        private static readonly byte[] SigHead = { 0x4C, 0x8D, 0x35 };
        private static readonly byte[] SigTail = { 0x0F, 0xB7, 0xFD };

        private IntPtr _h = IntPtr.Zero;
        private int _pid;
        private long _moduleBase, _moduleSize;
        private long _statsAddr;
        private long _coopAddr;
        private long _dateAddr;
        private int _dateMiss;                      // 日期块连续解析失败的次数
        private long _lastLocate;                   // 上次尝试定位 Coop/日期的时间戳（TickCount64）
        private const int LocateRetryMs = 2000;     // 补定位的最小间隔，防止每轮都去翻内存
        private const int DateMissLimit = 5;        // 连续 5 帧读不出日期才认为地址失效

        public bool Attached => _h != IntPtr.Zero && _statsAddr != 0;
        public int Pid => _pid;

        /// <summary>自检：把 Coop / 日期地址清成「未定位」，用来模拟「面板比存档先打开」的状态。</summary>
        public void DebugForgetAux()
        {
            _coopAddr = 0;
            _dateAddr = 0;
            _lastLocate = 0;
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { try { Win32.CloseHandle(_h); } catch { } _h = IntPtr.Zero; }
        }

        /// <summary>找进程 → 打开 → 扫签名 → 定位 Coop。成功返回 true。</summary>
        public bool Attach(out string note)
        {
            note = "";
            Dispose();
            _statsAddr = 0;
            _coopAddr = 0;
            _dateAddr = 0;

            var procs = Process.GetProcessesByName("P5R");
            if (procs.Length == 0)
            {
                note = "等待游戏启动…";
                return false;
            }
            _pid = procs[0].Id;
            _h = Win32.OpenProcess(Win32.PROCESS_VM_READ | Win32.PROCESS_QUERY_INFORMATION, false, _pid);
            if (_h == IntPtr.Zero)
            {
                note = "无法打开 P5R.exe，请以管理员身份运行面板";
                return false;
            }

            if (!GetModuleRange())
            {
                note = "取不到 P5R.exe 模块范围";
                Dispose();
                return false;
            }

            var sw = Stopwatch.StartNew();
            var hits = ScanSignature();
            if (hits.Count == 0)
            {
                note = "扫描不到五维签名（游戏版本需 1.0.4）";
                Dispose();
                return false;
            }

            // 多个命中时取"数值看起来像五维"的那个（另一处是字符串表，值是乱码）
            long best = hits[0];
            foreach (var a in hits)
            {
                var v = ReadBytes(a, 10);
                if (v == null) continue;
                bool sane = true;
                int nz = 0;
                for (int i = 0; i < 5; i++)
                {
                    short x = BitConverter.ToInt16(v, i * 2);
                    if (x < 0 || x > 3000) { sane = false; break; }
                    if (x != 0) nz++;
                }
                if (sane && nz > 0) { best = a; break; }
            }
            _statsAddr = best;
            _coopAddr = LocateCoop(_statsAddr);
            _dateAddr = LocateDate(_statsAddr);
            _lastLocate = Environment.TickCount64;

            Log.W($"已附加 P5R.exe（PID {_pid}），基址 0x{_moduleBase:X}，" +
                  $"签名 {hits.Count} 处，五维 0x{_statsAddr:X}，" +
                  $"Coop {(_coopAddr != 0 ? "0x" + _coopAddr.ToString("X") : "未定位")}，" +
                  $"日期 {(_dateAddr != 0 ? "0x" + _dateAddr.ToString("X") + $"（五维-0x{_statsAddr - _dateAddr:X}）" : "未定位")}" +
                  $"（{sw.ElapsedMilliseconds} ms）");
            note = "";
            return true;
        }

        private bool GetModuleRange()
        {
            IntPtr snap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPMODULE | Win32.TH32CS_SNAPMODULE32, _pid);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return false;
            try
            {
                var me = new Win32.MODULEENTRY32W();
                me.dwSize = Marshal.SizeOf(me);
                if (!Win32.Module32FirstW(snap, ref me)) return false;
                do
                {
                    if (me.szModule.StartsWith("P5R", StringComparison.OrdinalIgnoreCase))
                    {
                        _moduleBase = me.modBaseAddr.ToInt64();
                        _moduleSize = me.modBaseSize;
                        return _moduleSize > 0;
                    }
                } while (Win32.Module32NextW(snap, ref me));
            }
            finally { Win32.CloseHandle(snap); }
            return false;
        }

        private byte[] ReadBytes(long addr, int size)
        {
            if (_h == IntPtr.Zero) return null;
            var buf = new byte[size];
            if (!Win32.ReadProcessMemory(_h, (IntPtr)addr, buf, size, out IntPtr read)) return null;
            if (read.ToInt64() < size) return null;
            return buf;
        }

        private List<long> ScanSignature()
        {
            var hits = new List<long>();
            long addr = _moduleBase, end = _moduleBase + _moduleSize;
            const int CHUNK = 4 * 1024 * 1024;

            while (addr < end)
            {
                if (Win32.VirtualQueryEx(_h, (IntPtr)addr, out var mbi, Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) == 0)
                    break;
                long regionSize = mbi.RegionSize.ToInt64();
                if (regionSize <= 0) break;

                bool readable = mbi.State == Win32.MEM_COMMIT &&
                                (mbi.Protect & Win32.PAGE_NOACCESS) == 0 &&
                                (mbi.Protect & Win32.PAGE_GUARD) == 0;

                if (readable)
                {
                    for (long off = 0; off < regionSize; off += CHUNK)
                    {
                        int n = (int)Math.Min(CHUNK, regionSize - off);
                        var data = ReadBytes(mbi.BaseAddress.ToInt64() + off, n);
                        if (data == null) continue;
                        int i = 0;
                        while ((i = IndexOf(data, SigHead, i)) >= 0)
                        {
                            if (i + 10 <= data.Length &&
                                data[i + 7] == SigTail[0] && data[i + 8] == SigTail[1] && data[i + 9] == SigTail[2])
                            {
                                long insn = mbi.BaseAddress.ToInt64() + off + i;
                                int disp = BitConverter.ToInt32(data, i + 3);
                                long target = insn + 7 + disp;
                                var probe = ReadBytes(target, 10);
                                if (probe != null && target > 0x10000)
                                {
                                    bool ok = true;
                                    for (int k = 0; k < 5; k++)
                                    {
                                        short v = BitConverter.ToInt16(probe, k * 2);
                                        if (v < -1 || v > 3000) { ok = false; break; }
                                    }
                                    if (ok) hits.Add(target);
                                }
                            }
                            i++;
                        }
                    }
                }
                addr = mbi.BaseAddress.ToInt64() + regionSize;
            }
            return hits;
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            int last = hay.Length - needle.Length;
            for (int i = from; i <= last; i++)
            {
                if (hay[i] != needle[0]) continue;
                int k = 1;
                for (; k < needle.Length; k++) if (hay[i + k] != needle[k]) break;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>
        /// Coop 数组紧挨在五维数组前面（实测相距 0x382，但不写死）。
        /// 每条 16 字节：id(2) / rank(2) / 点数(2) / 后 10 字节补 0，动态长度、末尾之后全 0。
        /// </summary>
        private long LocateCoop(long statsAddr)
        {
            const int SPAN = 0x600;
            var blob = ReadBytes(statsAddr - SPAN, SPAN);
            if (blob == null) return 0;

            long bestAddr = 0;
            int bestCount = 0;
            for (int p = 0; p + 16 <= blob.Length; p++)
            {
                int count = 0;
                bool ok = true;
                for (int k = 0; k < 40; k++)
                {
                    int q = p + k * 16;
                    if (q + 16 > blob.Length) break;
                    short id = BitConverter.ToInt16(blob, q);
                    short rk = BitConverter.ToInt16(blob, q + 2);
                    short pt = BitConverter.ToInt16(blob, q + 4);
                    if (id == 0 && rk == 0 && pt == 0) break;
                    if (!GameTables.Coops.ContainsKey(id)) { ok = false; break; }
                    if (rk < 0 || rk > 10 || pt < 0 || pt > 3000) { ok = false; break; }
                    bool padZero = true;
                    for (int z = q + 6; z < q + 16; z++) if (blob[z] != 0) { padZero = false; break; }
                    if (!padZero) { ok = false; break; }
                    count++;
                }
                if (ok && count >= 4 && count > bestCount)
                {
                    bestCount = count;
                    bestAddr = statsAddr - SPAN + p;
                }
            }
            return bestAddr;
        }

        /// <summary>
        /// 日期块：8 字节 = (dayIdx u16, 标签 u16, dayIdx u16, 时段 u16)，
        /// dayIdx 从 2016-04-01 起算，时段 5 = 放学后、6 = 夜晚。
        /// 实测与五维数组同属 GlobalWork，相隔 -0x9190。
        ///
        /// 以前找不到就在 stats 往上 0x6000 里线性搜「第一个合法块」，而那段内存里全是
        /// 0x0101 填充串（257 天 = 12/14），一掉进兜底就把日期钉死在错值上。
        /// 现在只在固定偏移 ±0x40 内微调；找不到就返回 0，下一轮再试 ——
        /// 宁可这帧没有日期，也不能显示一个错的。
        /// </summary>
        private long LocateDate(long statsAddr)
        {
            long fixedAddr = statsAddr - 0x9190;
            if (IsDateBlock(fixedAddr)) return fixedAddr;

            for (long a = fixedAddr - 0x40; a <= fixedAddr + 0x40; a += 2)
                if (a != fixedAddr && IsDateBlock(a)) return a;

            return 0;       // 交给调用方决定要不要写日志（这里每 2 秒被问一次，别刷屏）
        }

        private bool IsDateBlock(long a)
        {
            if (a <= 0x10000) return false;
            var b = ReadBytes(a, 12);
            if (b == null) return false;

            int d1 = BitConverter.ToUInt16(b, 0);
            int tag = BitConverter.ToUInt16(b, 2);
            int d2 = BitConverter.ToUInt16(b, 4);
            int tz = BitConverter.ToUInt16(b, 6);
            int flag = BitConverter.ToUInt16(b, 8);

            // 0x0101 填充：内存里成片出现 "01 01 01 01 01 01 00 00"，
            // 按日期结构解就是两个 257（= 2016-12-14），必须先排掉。
            if (d1 == 0x0101 && tag == 0x0101) return false;

            // d1 == d2 是这块结构最强的特征；dayIdx 从 1 起（0 是没读到的空块）
            return d1 == d2 && d1 >= 1 && d1 <= 400 && tz <= 8 &&
                   tag >= 0x0100 && tag <= 0x0110 && flag <= 1;
        }

        /// <summary>
        /// 解析日期块。刻意不再要求两个 day 槽相等：切日 / 切时段的那一瞬间它们会短暂不同步，
        /// 以前正是这时判定「不像日期块」、掉进兜底搜错块。这里取第一个槽（当前日）即可。
        /// </summary>
        private bool TryParseDate(long a, out int dayIdx, out int tz)
        {
            dayIdx = -1;
            tz = -1;

            var b = ReadBytes(a, 8);
            if (b == null) return false;

            int d1 = BitConverter.ToUInt16(b, 0);
            int tag = BitConverter.ToUInt16(b, 2);
            int t = BitConverter.ToUInt16(b, 6);

            if (d1 < 1 || d1 > 400) return false;
            if (tag < 0x0100 || tag > 0x0110) return false;
            if (d1 == 0x0101 && tag == 0x0101) return false;   // 填充串
            if (t > 8) return false;

            dayIdx = d1;
            tz = t;
            return true;
        }

        /// <summary>
        /// 只读一次游戏内日期，给日历的 10 秒心跳用。
        /// 不定位、不改任何字段，可以在 UI 线程直接调用（ReadProcessMemory 本身是线程安全的）。
        /// 读不到（没附加 / 日期块还没定位 / 内容不像日期块）返回 false。
        /// </summary>
        public bool TryReadGameDate(out int dayIdx, out int tz)
        {
            dayIdx = -1;
            tz = -1;

            long a = _dateAddr;                 // long 读是原子的，不用加锁
            if (!Attached || a == 0) return false;

            return TryParseDate(a, out dayIdx, out tz);
        }

        /// <summary>读一帧。读不到返回 null（游戏已退出）。</summary>
        public Snapshot Poll()
        {
            if (!Attached) return null;
            var raw = ReadBytes(_statsAddr, 10);
            if (raw == null) return null;

            var snap = new Snapshot { Live = true, Stamp = DateTime.Now };
            bool sane = true;
            for (int i = 0; i < 5; i++)
            {
                short v = BitConverter.ToInt16(raw, i * 2);
                snap.Stats[i] = v;
                if (v < 0 || v > 3000) sane = false;
            }
            // 五维读出乱码：多半是附加时挑错了签名命中，返回 null 让外层重新附加
            if (!sane) return null;

            // 面板可能比「进存档」更早打开：那时五维、Coop、日期块全是 0，定位必然失败。
            // 所以不把「定位不到」当成终态 —— 一旦五维出现非 0（存档载入），就限速补定位，
            // 不用重启面板。日期块（日历页）同一个毛病，一起补。
            bool hasSave = false;
            for (int i = 0; i < 5; i++) if (snap.Stats[i] != 0) { hasSave = true; break; }

            if (hasSave)
            {
                long now = Environment.TickCount64;
                if (now - _lastLocate >= LocateRetryMs)
                {
                    _lastLocate = now;
                    if (_coopAddr == 0)
                    {
                        _coopAddr = LocateCoop(_statsAddr);
                        if (_coopAddr != 0) Log.W($"存档载入后补定位 Coop：0x{_coopAddr:X}");
                    }
                    if (_dateAddr == 0)
                    {
                        _dateAddr = LocateDate(_statsAddr);
                        if (_dateAddr != 0) Log.W($"存档载入后补定位日期：0x{_dateAddr:X}");
                    }
                }
            }

            if (_coopAddr != 0)
            {
                var blob = ReadBytes(_coopAddr, 16 * 40);
                if (blob != null)
                {
                    for (int k = 0; k < 40; k++)
                    {
                        int q = k * 16;
                        if (q + 16 > blob.Length) break;
                        int id = BitConverter.ToInt16(blob, q);
                        int rk = BitConverter.ToInt16(blob, q + 2);
                        int pt = BitConverter.ToInt16(blob, q + 4);
                        if (id == 0 && rk == 0 && pt == 0) break;
                        if (GameTables.Coops.ContainsKey(id) && rk >= 0 && rk <= 10)
                            snap.Coops.Add(new CoopEntry { Id = id, Rank = rk, Points = pt });
                    }

                    // 地址在、内容却是空的：进存档前误命中了别的块，清掉下轮重找
                    if (snap.Coops.Count == 0) _coopAddr = 0;
                }
                else
                {
                    // 地址失效（游戏重启），下次轮询重新定位
                    _coopAddr = 0;
                }
            }

            if (_dateAddr != 0)
            {
                if (TryParseDate(_dateAddr, out int dIdx, out int dTz))
                {
                    snap.GameDayIdx = dIdx;
                    snap.TimeZone = dTz;
                    _dateMiss = 0;
                }
                else if (++_dateMiss >= DateMissLimit)
                {
                    // 单帧读不出来（正在切日 / 切时段）先不动地址；连续失败才认定地址失效
                    _dateMiss = 0;
                    long old = _dateAddr;
                    _dateAddr = LocateDate(_statsAddr);
                    Log.W("日期块连续读失败，重定位：0x" + old.ToString("X") + " → " +
                          (_dateAddr != 0
                               ? "0x" + _dateAddr.ToString("X") + $"（五维-0x{_statsAddr - _dateAddr:X}）"
                               : "未定位"));
                }
            }
            return snap;
        }
    }
}
