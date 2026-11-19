using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace P5ROverlay
{
    /// <summary>
    /// 游戏没开时的兜底：解密最新存档，读五维与 Coop。
    /// 存档 = "DATA" + CRC + 时间戳 + flags + IV(0x10) + AES-256-CBC 密文；
    /// 明文 0x20 起是两段 zlib（第一段是 400 字节预览，第二段才是 198432 字节主数据）。
    /// </summary>
    internal static class SaveReader
    {
        private static readonly byte[] Key =
            Convert.FromBase64String("3lOZS0kYSoOOtkC4c7IDfvNXnxIprUPTlUGVC3yBJF0=");

        private const int STAT_OFF = 0x139E0;
        private const int COOP_OFF = 0x136A0;
        private const int DATE_OFF = 0xA670;   // (dayIdx, 标签, dayIdx, 时段)，dayIdx 从 2016-04-01 起
        private const int COOP_N = 26;

        public static string Root
        {
            get
            {
                try
                {
                    string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "SEGA", "P5R", "Steam");
                    if (!Directory.Exists(baseDir)) return null;
                    foreach (var d in Directory.GetDirectories(baseDir))
                    {
                        string sd = Path.Combine(d, "savedata");
                        if (Directory.Exists(sd)) return sd;
                    }
                }
                catch { }
                return null;
            }
        }

        public static Snapshot LoadNewest(out string note)
        {
            note = "";
            string root = Root;
            if (root == null) { note = "找不到存档目录"; return null; }

            string bestPath = null;
            DateTime bestTime = DateTime.MinValue;
            int bestSlot = 0;
            for (int n = 1; n <= 16; n++)
            {
                string p = Path.Combine(root, $"DATA{n:00}", "DATA.DAT");
                if (!File.Exists(p)) continue;
                var t = File.GetLastWriteTime(p);
                if (t > bestTime) { bestTime = t; bestPath = p; bestSlot = n; }
            }
            if (bestPath == null) { note = "没有可读的存档"; return null; }

            try
            {
                byte[] main = DecryptMain(File.ReadAllBytes(bestPath));
                if (main == null) { note = "存档解压失败"; return null; }
                if (main.Length < STAT_OFF + 10) { note = "存档结构不符（版本不同？）"; return null; }

                var snap = new Snapshot
                {
                    Live = false,
                    Stamp = bestTime,
                    SaveSlot = $"DATA{bestSlot:00} · {bestTime:MM-dd HH:mm}",
                    Source = $"存档 {bestSlot:00}（{bestTime:MM-dd HH:mm} 保存）",
                };
                for (int i = 0; i < 5; i++) snap.Stats[i] = BitConverter.ToInt16(main, STAT_OFF + i * 2);

                // 存档里的日期：保存那一刻的游戏内时间（日历页在没有游戏进程时用它兜底）
                if (main.Length >= DATE_OFF + 8)
                {
                    int d1 = BitConverter.ToUInt16(main, DATE_OFF);
                    int d2 = BitConverter.ToUInt16(main, DATE_OFF + 4);
                    if (d1 == d2 && d1 <= 400)
                    {
                        snap.GameDayIdx = d1;
                        snap.TimeZone = BitConverter.ToUInt16(main, DATE_OFF + 6);
                    }
                }

                for (int k = 0; k < COOP_N; k++)
                {
                    int b = COOP_OFF + k * 16;
                    if (b + 10 > main.Length) break;
                    int id = BitConverter.ToInt16(main, b + 6);
                    int rk = BitConverter.ToInt16(main, b + 8);
                    int pt = BitConverter.ToInt16(main, b + 10);
                    if (id == 0) continue;
                    if (!GameTables.Coops.ContainsKey(id)) continue;
                    snap.Coops.Add(new CoopEntry { Id = id, Rank = rk, Points = pt });
                }
                note = $"已读存档 {bestSlot:00}（{bestTime:HH:mm:ss}）";
                return snap;
            }
            catch (Exception ex)
            {
                note = "存档读取失败：" + ex.Message;
                return null;
            }
        }

        private static byte[] DecryptMain(byte[] data)
        {
            if (data.Length < 0x40 || data[0] != (byte)'D' || data[1] != (byte)'A') return null;

            byte[] iv = new byte[16];
            Buffer.BlockCopy(data, 0x10, iv, 0, 16);

            using var aes = Aes.Create();
            aes.Key = Key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.IV = iv;
            int len = data.Length - 0x20;
            len -= len % 16;
            byte[] plain = aes.CreateDecryptor().TransformFinalBlock(data, 0x20, len);

            // 逐段找 zlib 流：第一段是预览，第二段（长度 >10 万）才是主数据
            for (int i = 0x20; i + 1 < plain.Length; i++)
            {
                if (plain[i] != 0x78 || (plain[i + 1] != 0x9C && plain[i + 1] != 0xDA &&
                                          plain[i + 1] != 0x01 && plain[i + 1] != 0x5E)) continue;
                try
                {
                    using var ms = new MemoryStream(plain, i, plain.Length - i);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    z.CopyTo(outMs);
                    var res = outMs.ToArray();
                    if (res.Length > 100000) return res;     // 主数据段
                }
                catch { }
            }
            return null;
        }
    }
}
