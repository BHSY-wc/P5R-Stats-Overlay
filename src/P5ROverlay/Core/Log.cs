using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace P5ROverlay
{
    /// <summary>面板日志：内存里留最近 400 条给「日志」页显示，同时追加到 exe 同目录的 p5r_overlay.log。</summary>
    internal static class Log
    {
        private const int Max = 400;
        private static readonly List<string> _lines = new List<string>();
        private static readonly object _lock = new object();
        private static string _path;

        /// <summary>新日志（在 UI 线程外触发，订阅方自行 Invoke）。</summary>
        public static event Action<string> OnLine;

        public static void Init(string dir)
        {
            try { _path = Path.Combine(dir, "p5r_overlay.log"); } catch { }
        }

        public static void W(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (_lock)
            {
                _lines.Add(line);
                if (_lines.Count > Max) _lines.RemoveRange(0, _lines.Count - Max);
            }
            try
            {
                if (_path != null)
                {
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(_path);
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
            try { OnLine?.Invoke(line); } catch { }
        }

        public static string[] Snapshot()
        {
            lock (_lock) return _lines.ToArray();
        }

        public static void Clear()
        {
            lock (_lock) _lines.Clear();
        }
    }
}
