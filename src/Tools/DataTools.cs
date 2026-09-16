using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    // Revit 을 건드리지 않는 도구들. 애드인 폴더 아래 RevitMcpData\*.json 에 저장한다.
    internal static class DataTools
    {
        const string ProjectsCollection = "projects";
        const string ModulesCollection = "modules";

        public static void Register()
        {
            ToolRegistry.Register("store_project_data",
                "프로젝트 정보를 로컬 저장소에 기록한다. 같은 이름이 있으면 갱신한다. Revit 모델을 바꾸지 않는다.",
                S.Obj()
                    .StrReq("project_name", "프로젝트 이름. 이 값이 식별자가 된다.")
                    .Str("project_path", "프로젝트 파일 경로")
                    .Str("project_number", "프로젝트 번호")
                    .Str("project_address", "프로젝트 주소")
                    .Str("client_name", "발주처 이름")
                    .Str("project_status", "진행 상태. 예: 진행중, 완료, 보류")
                    .Str("author", "작성자")
                    .Any("metadata", "그 밖에 남길 값들. 자유 형식 객체."),
                StoreProjectData, false);

            ToolRegistry.Register("query_stored_data",
                "로컬 저장소에 쌓아둔 프로젝트·방 정보를 조회한다. Revit 모델을 읽지 않는다.",
                S.Obj()
                    .Enum("query_type", "조회 종류",
                          new string[] { "all_projects", "project_by_id", "project_by_name",
                                         "rooms_by_project_id", "rooms_by_project_name", "all_rooms", "stats" },
                          "all_projects")
                    .Int("project_id", "프로젝트 ID. project_by_id, rooms_by_project_id 에 필요.")
                    .Str("project_name", "프로젝트 이름. project_by_name, rooms_by_project_name 에 필요."),
                QueryStoredData, false);

            ToolRegistry.Register("search_modules",
                "저장해 둔 코드 모듈을 찾는다. 모듈은 자주 쓰는 C# 조각에 이름을 붙여 보관한 것이다. " +
                "이름, 설명, 태그, 코드 본문에서 찾는다. 검색어를 비우면 전체 목록을 준다.",
                S.Obj()
                    .Str("query", "찾을 문자열. 비우면 전체 목록.")
                    .Str("tag", "이 태그가 붙은 모듈만")
                    .Bool("includeCode", "코드 본문까지 함께 줄지", false)
                    .IntDef("limit", "돌려줄 최대 개수", 50),
                SearchModules, false);

            ToolRegistry.Register("use_module",
                "저장해 둔 코드 모듈을 실행하거나, 새 모듈을 저장하거나 지운다.\n" +
                "action=run 이면 모듈의 C# 코드를 Revit 에서 실행한다 (send_code_to_revit 과 같은 실행 환경).\n" +
                "action=save 면 code 를 name 으로 저장한다. action=delete 면 지운다.",
                S.Obj()
                    .StrReq("name", "모듈 이름")
                    .Enum("action", "할 일", new string[] { "run", "save", "delete" }, "run")
                    .Str("code", "저장할 C# 코드. action=save 에 필요하다.")
                    .Str("description", "모듈 설명. action=save 에서만 쓰인다.")
                    .StrArr("tags", "모듈에 붙일 태그. action=save 에서만 쓰인다.", false)
                    .AnyArr("parameters", "실행할 때 parameters[0] 처럼 넘길 인자 배열")
                    .Enum("transactionMode", "실행할 때의 트랜잭션 처리 방식",
                          new string[] { "auto", "manual", "none" }, "auto")
                    .Int("timeoutMs", "실행 제한 시간(밀리초)"),
                UseModule);

            // 모듈 실행만 Revit 이 필요하므로 NeedsRevit 은 true 로 둔다.
            // save/delete 는 Revit 을 쓰지 않지만 UI 스레드에서 돌아도 문제없다.
        }

        // ---- 프로젝트 ----

        static object StoreProjectData(UIApplication uiapp, JObject args)
        {
            string name = A.StrReq(args, "project_name");

            JArray existing = DataStore.Load(ProjectsCollection);
            int maxId = 0;
            JObject prev = null;
            foreach (JToken t in existing)
            {
                JObject o = t as JObject;
                if (o == null) continue;
                JObject val = o["value"] as JObject;
                if (val != null)
                {
                    int id = val["id"] != null ? (int)val["id"] : 0;
                    if (id > maxId) maxId = id;
                }
                if (string.Equals((string)o["key"], name, StringComparison.Ordinal)) prev = val;
            }

            JObject project = new JObject();
            project["id"] = prev != null && prev["id"] != null ? (int)prev["id"] : maxId + 1;
            project["project_name"] = name;
            project["project_path"] = A.Str(args, "project_path", null);
            project["project_number"] = A.Str(args, "project_number", null);
            project["project_address"] = A.Str(args, "project_address", null);
            project["client_name"] = A.Str(args, "client_name", null);
            project["project_status"] = A.Str(args, "project_status", null);
            project["author"] = A.Str(args, "author", null);
            if (A.Has(args, "metadata")) project["metadata"] = args["metadata"];

            bool added = DataStore.Put(ProjectsCollection, name, project, null);

            JObject res = new JObject();
            res["success"] = true;
            res["message"] = added ? "프로젝트를 새로 저장했습니다." : "기존 프로젝트를 갱신했습니다.";
            res["project_id"] = project["id"];
            res["project"] = project;
            res["storagePath"] = DataStore.Root;
            return res;
        }

        static JObject FindProjectByName(string name)
        {
            foreach (JToken t in DataStore.Load(ProjectsCollection))
            {
                JObject o = t as JObject;
                if (o == null) continue;
                if (string.Equals((string)o["key"], name, StringComparison.OrdinalIgnoreCase))
                    return o["value"] as JObject;
            }
            return null;
        }

        static JObject FindProjectById(int id)
        {
            foreach (JToken t in DataStore.Load(ProjectsCollection))
            {
                JObject o = t as JObject;
                if (o == null) continue;
                JObject v = o["value"] as JObject;
                if (v != null && v["id"] != null && (int)v["id"] == id) return v;
            }
            return null;
        }

        internal static JArray RoomsOfProject(int projectId)
        {
            JArray result = new JArray();
            foreach (JToken t in DataStore.Load(RoomTools.RoomsCollection))
            {
                JObject o = t as JObject;
                if (o == null) continue;
                JObject v = o["value"] as JObject;
                if (v == null) continue;
                if (v["project_id"] != null && (int)v["project_id"] == projectId) result.Add(v);
            }
            return result;
        }

        static object QueryStoredData(UIApplication uiapp, JObject args)
        {
            string type = A.Str(args, "query_type", "all_projects");
            JObject res = new JObject();
            res["query_type"] = type;

            switch (type)
            {
                case "all_projects":
                    {
                        JArray arr = new JArray();
                        foreach (JToken t in DataStore.Load(ProjectsCollection))
                        {
                            JObject o = t as JObject;
                            if (o != null && o["value"] != null) arr.Add(o["value"]);
                        }
                        res["count"] = arr.Count;
                        res["projects"] = arr;
                        break;
                    }
                case "project_by_id":
                    {
                        int id = A.IntReq(args, "project_id");
                        JObject p = FindProjectById(id);
                        if (p == null) throw new ArgumentException("ID " + id + " 인 프로젝트가 없습니다.");
                        res["project"] = p;
                        break;
                    }
                case "project_by_name":
                    {
                        string name = A.StrReq(args, "project_name");
                        JObject p = FindProjectByName(name);
                        if (p == null) throw new ArgumentException("'" + name + "' 프로젝트가 없습니다.");
                        res["project"] = p;
                        break;
                    }
                case "rooms_by_project_id":
                    {
                        int id = A.IntReq(args, "project_id");
                        JArray rooms = RoomsOfProject(id);
                        res["project_id"] = id;
                        res["count"] = rooms.Count;
                        res["rooms"] = rooms;
                        break;
                    }
                case "rooms_by_project_name":
                    {
                        string name = A.StrReq(args, "project_name");
                        JObject p = FindProjectByName(name);
                        if (p == null) throw new ArgumentException("'" + name + "' 프로젝트가 없습니다.");
                        JArray rooms = RoomsOfProject((int)p["id"]);
                        res["project_name"] = name;
                        res["count"] = rooms.Count;
                        res["rooms"] = rooms;
                        break;
                    }
                case "all_rooms":
                    {
                        JArray arr = new JArray();
                        foreach (JToken t in DataStore.Load(RoomTools.RoomsCollection))
                        {
                            JObject o = t as JObject;
                            if (o != null && o["value"] != null) arr.Add(o["value"]);
                        }
                        res["count"] = arr.Count;
                        res["rooms"] = arr;
                        break;
                    }
                case "stats":
                    {
                        int projectCount = DataStore.Load(ProjectsCollection).Count;
                        int roomCount = DataStore.Load(RoomTools.RoomsCollection).Count;
                        int moduleCount = DataStore.Load(ModulesCollection).Count;
                        res["projects"] = projectCount;
                        res["rooms"] = roomCount;
                        res["modules"] = moduleCount;
                        res["collections"] = new JArray(DataStore.Collections().ToArray());
                        res["storagePath"] = DataStore.Root;
                        break;
                    }
                default:
                    throw new ArgumentException("모르는 query_type 입니다: " + type);
            }
            return res;
        }

        // ---- 모듈 ----

        static object SearchModules(UIApplication uiapp, JObject args)
        {
            string query = A.Str(args, "query", null);
            string tag = A.Str(args, "tag", null);
            bool includeCode = A.Bool(args, "includeCode", false);
            int limit = A.Int(args, "limit", 50);

            JArray found = new JArray();
            int total = 0;

            foreach (JToken t in DataStore.Load(ModulesCollection))
            {
                JObject rec = t as JObject;
                if (rec == null) continue;
                JObject m = rec["value"] as JObject;
                if (m == null) continue;

                string name = (string)m["name"];
                string desc = (string)m["description"];
                string code = (string)m["code"];

                if (!string.IsNullOrEmpty(tag))
                {
                    bool hasTag = false;
                    JArray tags = m["tags"] as JArray;
                    if (tags != null)
                    {
                        foreach (JToken tt in tags)
                            if (string.Equals(tt.ToString(), tag, StringComparison.OrdinalIgnoreCase)) { hasTag = true; break; }
                    }
                    if (!hasTag) continue;
                }

                if (!string.IsNullOrEmpty(query))
                {
                    string hay = (name + " " + desc + " " + code).ToLowerInvariant();
                    if (hay.IndexOf(query.ToLowerInvariant(), StringComparison.Ordinal) < 0) continue;
                }

                total++;
                if (found.Count >= limit) continue;

                JObject o = new JObject();
                o["name"] = name;
                o["description"] = desc;
                o["tags"] = m["tags"];
                o["updatedAt"] = rec["updatedAt"];
                if (includeCode) o["code"] = code;
                else o["codeLength"] = code != null ? code.Length : 0;
                found.Add(o);
            }

            JObject res = new JObject();
            res["totalMatched"] = total;
            res["returned"] = found.Count;
            res["modules"] = found;
            if (total == 0)
                res["message"] = "저장된 모듈이 없습니다. use_module 에 action=save 로 먼저 저장하십시오.";
            return res;
        }

        static object UseModule(UIApplication uiapp, JObject args)
        {
            string name = A.StrReq(args, "name");
            string action = A.Str(args, "action", "run").ToLowerInvariant();

            if (action == "save")
            {
                string code = A.Str(args, "code", null);
                if (string.IsNullOrWhiteSpace(code))
                    throw new ArgumentException("action=save 에는 code 가 필요합니다.");

                JObject m = new JObject();
                m["name"] = name;
                m["description"] = A.Str(args, "description", null);
                m["code"] = code;
                JArray tags = A.Arr(args, "tags");
                m["tags"] = tags != null ? tags : new JArray();

                bool added = DataStore.Put(ModulesCollection, name, m, null);

                JObject r = new JObject();
                r["success"] = true;
                r["action"] = "save";
                r["message"] = added ? "모듈을 새로 저장했습니다." : "기존 모듈을 덮어썼습니다.";
                r["name"] = name;
                r["storagePath"] = DataStore.Root;
                return r;
            }

            if (action == "delete")
            {
                int n = DataStore.Delete(ModulesCollection, name);
                if (n == 0) throw new ArgumentException("'" + name + "' 모듈이 없습니다.");
                JObject r = new JObject();
                r["success"] = true;
                r["action"] = "delete";
                r["message"] = "모듈을 지웠습니다.";
                r["name"] = name;
                return r;
            }

            // run
            JObject module = null;
            foreach (JToken t in DataStore.Load(ModulesCollection))
            {
                JObject rec = t as JObject;
                if (rec == null) continue;
                if (string.Equals((string)rec["key"], name, StringComparison.OrdinalIgnoreCase))
                {
                    module = rec["value"] as JObject;
                    break;
                }
            }
            if (module == null)
                throw new ArgumentException("'" + name + "' 모듈이 없습니다. search_modules 로 목록을 확인하십시오.");

            string src = (string)module["code"];
            if (string.IsNullOrWhiteSpace(src))
                throw new InvalidOperationException("'" + name + "' 모듈에 코드가 비어 있습니다.");

            DateTime t0 = DateTime.Now;
            object result = CodeExecutor.Run(
                uiapp, src, A.Arr(args, "parameters"),
                A.Str(args, "transactionMode", "auto"),
                "모듈 실행: " + name);
            double ms = (DateTime.Now - t0).TotalMilliseconds;

            JObject res = new JObject();
            res["success"] = true;
            res["action"] = "run";
            res["module"] = name;
            res["elapsedMs"] = Math.Round(ms, 1);
            res["result"] = result == null ? JValue.CreateNull() : (result as JToken) ?? JToken.FromObject(result);
            return res;
        }
    }
}
