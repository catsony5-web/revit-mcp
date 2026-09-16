using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // MCP(JSON-RPC 2.0) 요청을 해석하고 응답 JSON 문자열을 만든다.
    // 알림(notification)이면 null 을 돌려주고 호출자는 202 로 답한다.
    // 주의: 이 프로젝트는 C# 5 컴파일러로 빌드한다. 인덱스 초기화자와 문자열 보간을 쓸 수 없다.
    internal static class McpProtocol
    {
        public const string ServerName = "revit-mcp";
        public const string ServerVersion = "2.0.0-preview.1";
        public const string DefaultProtocolVersion = "2025-06-18";

        public static string Handle(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return ErrorObj(null, -32700, "빈 요청입니다.").ToString(Formatting.None);

            JToken parsed;
            try { parsed = JToken.Parse(body); }
            catch (Exception ex)
            {
                return ErrorObj(null, -32700, "JSON 파싱 실패: " + ex.Message).ToString(Formatting.None);
            }

            if (parsed.Type == JTokenType.Array)
            {
                JArray results = new JArray();
                foreach (JToken item in (JArray)parsed)
                {
                    JObject r = HandleOne(item as JObject);
                    if (r != null) results.Add(r);
                }
                return results.Count == 0 ? null : results.ToString(Formatting.None);
            }

            JObject single = HandleOne(parsed as JObject);
            return single == null ? null : single.ToString(Formatting.None);
        }

        static JObject HandleOne(JObject req)
        {
            if (req == null) return ErrorObj(null, -32600, "요청이 객체가 아닙니다.");

            string method = (string)req["method"];
            JToken id = req["id"];
            bool isNotification = (id == null || id.Type == JTokenType.Null);

            JObject args = req["params"] as JObject;
            if (args == null) args = new JObject();

            if (string.IsNullOrEmpty(method))
                return isNotification ? null : ErrorObj(id, -32600, "method 가 없습니다.");

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, Initialize(args));

                    case "notifications/initialized":
                    case "notifications/cancelled":
                    case "notifications/roots/list_changed":
                        return null;

                    case "ping":
                        return Result(id, new JObject());

                    case "tools/list":
                        return Result(id, new JObject(new JProperty("tools", ToolRegistry.ListSchema())));

                    case "tools/call":
                        return Result(id, ToolRegistry.Call(args));

                    case "resources/list":
                        return Result(id, new JObject(new JProperty("resources", new JArray())));

                    case "prompts/list":
                        return Result(id, new JObject(new JProperty("prompts", new JArray())));

                    default:
                        if (isNotification) return null;
                        return ErrorObj(id, -32601, "지원하지 않는 method 입니다: " + method);
                }
            }
            catch (Exception ex)
            {
                Log.Error("method 처리 실패: " + method, ex);
                if (isNotification) return null;
                return ErrorObj(id, -32603, ex.GetType().Name + ": " + ex.Message);
            }
        }

        static JObject Initialize(JObject args)
        {
            // 클라이언트가 요청한 프로토콜 버전을 그대로 되돌려준다. 없으면 기본값.
            string requested = (string)args["protocolVersion"];
            string version = requested == "2024-11-05" || requested == "2025-03-26" || requested == DefaultProtocolVersion ? requested : DefaultProtocolVersion;

            string client = args["clientInfo"] != null ? args["clientInfo"].ToString(Formatting.None) : "?";
            Log.Info("initialize 수신 (protocolVersion=" + version + ", client=" + client + ")");

            JObject caps = new JObject(
                new JProperty("tools", new JObject(new JProperty("listChanged", false))));

            JObject info = new JObject(
                new JProperty("name", ServerName),
                new JProperty("version", ServerVersion));

            return new JObject(
                new JProperty("protocolVersion", version),
                new JProperty("capabilities", caps),
                new JProperty("serverInfo", info),
                new JProperty("instructions",
                    "Revit 2024 서버입니다. 변경 전에 get_document_context로 expectedDocument/expectedRevision을 얻고 고유 requestId를 전달하십시오. " +
                    "시간초과 후 새 ID로 재실행하지 말고 get_request_status를 조회하십시오. 전용 도구의 좌표는 mm, send_code_to_revit 코드는 Revit API 내부 단위입니다. " +
                    "해석/CAD/간섭 기능은 API 구현 범위이며 모든 UI 명령을 지원하지 않습니다."));
        }

        static JObject Result(JToken id, JToken result)
        {
            return new JObject(
                new JProperty("jsonrpc", "2.0"),
                new JProperty("id", id != null ? id : JValue.CreateNull()),
                new JProperty("result", result));
        }

        static JObject ErrorObj(JToken id, int code, string message)
        {
            JObject err = new JObject(
                new JProperty("code", code),
                new JProperty("message", message));

            return new JObject(
                new JProperty("jsonrpc", "2.0"),
                new JProperty("id", id != null ? id : JValue.CreateNull()),
                new JProperty("error", err));
        }
    }
}
