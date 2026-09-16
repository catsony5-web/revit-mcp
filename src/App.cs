using System;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    public class App : IExternalApplication
    {
        internal static HttpServer Server;

        public Result OnStartup(UIControlledApplication a)
        {
            try
            {
                Config.Load();
                Log.Info("=== Revit MCP 애드인 시작 (Revit " + a.ControlledApplication.VersionNumber + ") ===");

                Dispatcher.Initialize(a);
                Tools.AllTools.RegisterAll();
                Log.Info("도구 " + ToolRegistry.Count + "개 등록 완료");

                BuildRibbon(a);

                if (Config.AutoStart)
                {
                    Server = new HttpServer();
                    Server.Start(Config.Port);
                }
                else
                {
                    Log.Info("autoStart 가 꺼져 있어 서버를 띄우지 않았습니다. 리본에서 직접 시작하십시오.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("애드인 시작 실패", ex);
                TaskDialog.Show("Revit MCP", "애드인 시작에 실패했습니다.\n\n" + ex.Message +
                                "\n\n로그: " + Log.Directory);
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            Dispatcher.Shutdown(a);
            try { if (Server != null) Server.Stop(); }
            catch (Exception ex) { Log.Error("서버 정지 실패", ex); }
            return Result.Succeeded;
        }

        static void BuildRibbon(UIControlledApplication a)
        {
            RibbonPanel panel;
            try { panel = a.CreateRibbonPanel("MCP"); }
            catch { return; }

            string dll = Assembly.GetExecutingAssembly().Location;

            PushButtonData status = new PushButtonData(
                "McpStatus", "MCP\n상태", dll, "RevitMcp.CmdStatus");
            status.ToolTip = "MCP 서버 상태와 접속 주소를 확인합니다.";
            panel.AddItem(status);

            PushButtonData toggle = new PushButtonData(
                "McpToggle", "서버\n시작·정지", dll, "RevitMcp.CmdToggle");
            toggle.ToolTip = "MCP HTTP 서버를 시작하거나 정지합니다.";
            panel.AddItem(toggle);
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdStatus : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string message, ElementSet elements)
        {
            bool running = App.Server != null && App.Server.IsRunning;
            int port = App.Server != null ? App.Server.Port : Config.Port;

            string text =
                "상태: " + (running ? "실행 중" : "정지됨") + "\n" +
                "주소: http://127.0.0.1:" + port + "/mcp\n" +
                "등록된 도구: " + ToolRegistry.Count + "개\n" +
                "코드 컴파일러: " + CodeExecutor.ActiveCompiler + "\n\n" +
                "로그 폴더:\n" + Log.Directory;

            TaskDialog dlg = new TaskDialog("Revit MCP 상태");
            dlg.MainInstruction = running ? "MCP 서버가 돌고 있습니다." : "MCP 서버가 멈춰 있습니다.";
            dlg.MainContent = text;
            dlg.CommonButtons = TaskDialogCommonButtons.Close;
            dlg.Show();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdToggle : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string message, ElementSet elements)
        {
            try
            {
                if (App.Server != null && App.Server.IsRunning)
                {
                    App.Server.Stop();
                    TaskDialog.Show("Revit MCP", "서버를 정지했습니다.");
                }
                else
                {
                    if (App.Server == null) App.Server = new HttpServer();
                    App.Server.Start(Config.Port);
                    TaskDialog.Show("Revit MCP",
                        "서버를 시작했습니다.\n\nhttp://127.0.0.1:" + Config.Port + "/mcp");
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Log.Error("서버 토글 실패", ex);
                return Result.Failed;
            }
            return Result.Succeeded;
        }
    }
}
