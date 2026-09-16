using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // Revit 과 무관한 로컬 저장소.
    // 원래 서버는 Node 쪽 SQLite 를 썼지만, 애드인 단독 구성에서는 네이티브 의존성을 만들지 않으려고
    // 컬렉션마다 JSON 파일 하나를 쓴다. 규모가 이 용도에는 충분하다.
    internal static class DataStore
    {
        static readonly object _gate = new object();
        static string _root;

        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    string baseDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    _root = Path.Combine(baseDir, "RevitMcpData");
                    Directory.CreateDirectory(_root);
                }
                return _root;
            }
        }

        static string PathFor(string collection)
        {
            if (string.IsNullOrEmpty(collection)) collection = "default";
            foreach (char c in Path.GetInvalidFileNameChars())
                collection = collection.Replace(c, '_');
            return Path.Combine(Root, collection + ".json");
        }

        public static JArray Load(string collection)
        {
            lock (_gate)
            {
                string path = PathFor(collection);
                if (!File.Exists(path)) return new JArray();
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(text)) throw new IOException("Stored data is empty or truncated.");
                    JArray arr = JArray.Parse(text);
                    return arr;
                }
                catch (Exception ex)
                {
                    Log.Error("저장소 읽기 실패: " + collection, ex);
                    throw new IOException("Stored data is unreadable; refusing to replace it with an empty collection: " + collection, ex);
                }
            }
        }

        static void Save(string collection, JArray arr)
        {
            lock (_gate)
            {
                string path = PathFor(collection);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(arr.ToString(Formatting.Indented));
                    using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        // 같은 key 가 있으면 덮어쓰고, 없으면 추가한다. 돌려주는 값은 (추가됨 여부).
        public static bool Put(string collection, string key, JToken value, JObject meta)
        {
            lock (_gate)
            {
            JArray arr = Load(collection);
            JObject record = new JObject();
            record["key"] = key;
            record["value"] = value;
            record["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (meta != null) record["meta"] = meta;

            for (int i = 0; i < arr.Count; i++)
            {
                JObject o = arr[i] as JObject;
                if (o == null) continue;
                if (string.Equals((string)o["key"], key, StringComparison.Ordinal))
                {
                    record["createdAt"] = o["createdAt"] != null ? o["createdAt"] : record["updatedAt"];
                    arr[i] = record;
                    Save(collection, arr);
                    return false;
                }
            }

            record["createdAt"] = record["updatedAt"];
            arr.Add(record);
            Save(collection, arr);
            return true;
            }
        }

        public static int Delete(string collection, string key)
        {
            lock (_gate)
            {
            JArray arr = Load(collection);
            int removed = 0;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                JObject o = arr[i] as JObject;
                if (o == null) continue;
                if (string.Equals((string)o["key"], key, StringComparison.Ordinal))
                {
                    arr.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0) Save(collection, arr);
            return removed;
            }
        }

        public static List<string> Collections()
        {
            List<string> names = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(Root, "*.json"))
                    names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            names.Sort();
            return names;
        }
    }
}
