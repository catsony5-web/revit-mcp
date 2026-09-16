using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // 애드인 폴더의 revitmcp.config.json 을 읽는다. 없으면 기본값.
    internal static class Config
    {
        public static int Port = 8090;
        public static int DefaultTimeoutMs = 60000;
        public static int MaxTimeoutMs = 600000;
        public static bool AutoStart = true;

        public static void Load()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(baseDir, "revitmcp.config.json");
                if (!File.Exists(path)) return;

                JObject o = JObject.Parse(File.ReadAllText(path));
                if (o["port"] != null) Port = (int)o["port"];
                if (o["defaultTimeoutMs"] != null) DefaultTimeoutMs = (int)o["defaultTimeoutMs"];
                if (o["maxTimeoutMs"] != null) MaxTimeoutMs = (int)o["maxTimeoutMs"];
                if (o["autoStart"] != null) AutoStart = (bool)o["autoStart"];
                Log.Info("설정 파일 적용: " + path);
            }
            catch (Exception ex) { Log.Error("설정 파일 읽기 실패", ex); }
        }

        public static int ClampTimeout(int? requested)
        {
            int v = requested.HasValue && requested.Value > 0 ? requested.Value : DefaultTimeoutMs;
            return Math.Min(v, MaxTimeoutMs);
        }
    }
}
