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

        public bool Attached => _h != IntPtr.Zero && _statsAddr != 0;
        public int Pid => _pid;

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

            Log.W($"已附加 P5R.exe（PID {_pid}），基址 0x{_moduleBase:X}，" +
                  $"签名 {hits.Count} 处，五维 0x{_statsAddr:X}，" +
                  $"Coop {( _coopAddr != 0 ? "0x" + _coopAddr.ToString("X") : "未定位")}" +
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

        /// <summary>读一帧。读不到返回 null（游戏已退出）。</summary>
        public Snapshot Poll()
        {
            if (!Attached) return null;
            var raw = ReadBytes(_statsAddr, 10);
            if (raw == null) return null;

            var snap = new Snapshot { Live = true, Stamp = DateTime.Now };
            for (int i = 0; i < 5; i++) snap.Stats[i] = BitConverter.ToInt16(raw, i * 2);

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
                }
                else
                {
                    // 地址失效（游戏重启），下次轮询重新定位
                    _coopAddr = LocateCoop(_statsAddr);
                }
            }
            return snap;
        }
    }
}
