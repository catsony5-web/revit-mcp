using System;
using System.IO;
using System.Text;

namespace RevitMcp
{
    // 로그는 애드인 폴더 아래 Logs\mcp_YYYYMMDD.log 로 누적한다.
    internal static class Log
    {
        static readonly object _gate = new object();
        static string _dir;

        public static string Directory
        {
            get
            {
                if (_dir == null)
                {
                    string baseDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    _dir = Path.Combine(baseDir, "RevitMcpLogs");
                    try { System.IO.Directory.CreateDirectory(_dir); } catch { }
                }
                return _dir;
            }
        }

        public static void Info(string msg) { Write("Info", msg); }
        public static void Warn(string msg) { Write("Warn", msg); }

        public static void Error(string msg, Exception ex)
        {
            Write("Error", ex == null ? msg : msg + " :: " + ex.GetType().Name + ": " + ex.Message
                + Environment.NewLine + ex.StackTrace);
        }

        static void Write(string level, string msg)
        {
            try
            {
                string path = Path.Combine(Directory, "mcp_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg;
                lock (_gate) File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(true));
            }
            catch { }
        }
    }
}
