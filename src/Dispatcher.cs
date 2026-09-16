using System;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    // Revit API 는 UI 스레드에서만 호출할 수 있다.
    // HTTP 스레드가 넘긴 작업을 ExternalEvent 로 UI 스레드에 태우고 결과를 되돌려준다.
    internal sealed class Dispatcher : IExternalEventHandler
    {
        sealed class Job
        {
            public Func<UIApplication, object> Work;
            public object Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static Dispatcher _instance;
        static ExternalEvent _event;
        readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();

        // OnStartup(UI 스레드)에서 한 번만 호출한다.
        public static void Initialize()
        {
            _instance = new Dispatcher();
            _event = ExternalEvent.Create(_instance);
            Log.Info("Dispatcher 초기화 완료");
        }

        public static bool IsReady { get { return _instance != null && _event != null; } }

        public static object Invoke(Func<UIApplication, object> work, int timeoutMs)
        {
            if (!IsReady) throw new InvalidOperationException("Dispatcher 가 아직 초기화되지 않았습니다.");

            Job job = new Job { Work = work };
            _instance._jobs.Enqueue(job);

            ExternalEventRequest req = _event.Raise();
            if (req == ExternalEventRequest.Denied)
                throw new InvalidOperationException("Revit 이 요청을 거부했습니다 (ExternalEventRequest.Denied).");

            if (!job.Done.Wait(timeoutMs))
            {
                // 큐에는 남아 있지만 호출자는 포기한다. 나중에 실행되더라도 결과는 버려진다.
                throw new TimeoutException(
                    "Revit 응답이 " + timeoutMs + "ms 안에 오지 않았습니다. " +
                    "Revit 이 모달 대화상자를 띄우고 있거나 작업이 너무 깁니다. " +
                    "대화상자를 닫거나 timeoutMs 를 늘려 다시 시도하십시오.");
            }

            if (job.Error != null) throw job.Error;
            return job.Result;
        }

        public void Execute(UIApplication app)
        {
            Job job;
            while (_instance._jobs.TryDequeue(out job))
            {
                try { job.Result = job.Work(app); }
                catch (Exception ex) { job.Error = ex; }
                finally { job.Done.Set(); }
            }
        }

        public string GetName() { return "Revit MCP Dispatcher"; }
    }
}
