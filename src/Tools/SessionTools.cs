using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class SessionTools
    {
        public static void Register()
        {
            ToolRegistry.Register("get_document_context", "현재 문서 토큰과 변경 번호, 열린 문서 목록을 확인합니다. 변경 작업 전에 이 값을 사용합니다.", S.Obj(), DocumentGuard.Context);
            ToolRegistry.Register("get_request_status", "요청 ID로 대기/실행/완료/실패/재시작 후 불확실 상태와 저장된 결과를 조회합니다. 모델에 접근하지 않습니다.", S.Obj().StrReq("requestId", "원래 변경 요청의 ID"), delegate(UIApplication ui, JObject args) { return Dispatcher.Status(A.StrReq(args, "requestId")); }, false);
            ToolRegistry.Register("cancel_request", "아직 실행되지 않은 요청만 취소합니다. 이미 실행 중인 Revit 작업을 강제로 중단하지 않습니다.", S.Obj().StrReq("requestId", "취소할 요청 ID"), delegate(UIApplication ui, JObject args) { return Dispatcher.Cancel(A.StrReq(args, "requestId")); }, false);
        }
    }
}
