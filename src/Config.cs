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
        public const int HardMaxTimeoutMs = 600000;

        public static void Load()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(baseDir, "revitmcp.config.json");
                if (!File.Exists(path)) return;

                Apply(JObject.Parse(File.ReadAllText(path)));
                Log.Info("설정 파일 적용: " + path);
            }
            catch (Exception ex) { Log.Error("설정 파일 읽기/검증 실패; 기존 유효 설정을 유지합니다", ex); }
        }

        internal static void Apply(JObject o)
        {
            int port = ReadInteger(o, "port", Port);
            int defaultTimeout = ReadInteger(o, "defaultTimeoutMs", DefaultTimeoutMs);
            int maxTimeout = ReadInteger(o, "maxTimeoutMs", MaxTimeoutMs);
            bool autoStart = AutoStart;
            if (o["autoStart"] != null)
            {
                if (o["autoStart"].Type != JTokenType.Boolean) throw new ArgumentException("autoStart must be a Boolean.");
                autoStart = (bool)o["autoStart"];
            }
            if (port < 1 || port > 65535) throw new ArgumentException("port must be 1..65535.");
            ValidateTimeout(defaultTimeout); ValidateTimeout(maxTimeout);
            if (defaultTimeout > maxTimeout) throw new ArgumentException("defaultTimeoutMs must not exceed maxTimeoutMs.");
            // Assign only after the entire configuration passes: no partial invalid updates.
            Port = port; DefaultTimeoutMs = defaultTimeout; MaxTimeoutMs = maxTimeout; AutoStart = autoStart;
        }

        static int ReadInteger(JObject o, string name, int fallback)
        {
            JToken value = o[name];
            if (value == null) return fallback;
            if (value.Type != JTokenType.Integer) throw new ArgumentException(name + " must be an integer.");
            return value.Value<int>();
        }

        public static void ValidateTimeout(int value)
        {
            if (value < 1 || value > HardMaxTimeoutMs)
                throw new ArgumentOutOfRangeException("timeoutMs", "Wait duration must be 1..600000 milliseconds.");
        }

        public static int ClampTimeout(int? requested)
        {
            ValidateTimeout(DefaultTimeoutMs); ValidateTimeout(MaxTimeoutMs);
            if (DefaultTimeoutMs > MaxTimeoutMs) throw new ArgumentException("defaultTimeoutMs must not exceed maxTimeoutMs.");
            if (requested.HasValue && requested.Value <= 0) throw new ArgumentOutOfRangeException("timeoutMs", "Requested wait duration must be positive.");
            int v = requested.HasValue ? requested.Value : DefaultTimeoutMs;
            return Math.Min(v, MaxTimeoutMs);
        }
    }
}
