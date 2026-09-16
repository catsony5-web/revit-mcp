using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RevitMcp
{
    // HttpListener 는 URL 예약(netsh urlacl) 때문에 관리자 권한이 필요할 수 있어
    // TcpListener 위에 최소한의 HTTP/1.1 만 직접 구현한다. 권한 문제가 없다.
    internal sealed class HttpServer
    {
        TcpListener _listener;
        Thread _accept;
        volatile bool _running;
        readonly object lifecycleGate = new object();
        readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        long generation;

        public int Port { get; private set; }
        public bool IsRunning { get { return _running; } }

        public void Start(int port)
        {
            lock (lifecycleGate)
            {
                if (_running) return;
                if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port", "Port must be 1..65535.");
                TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                try
                {
                    long currentGeneration = Dispatcher.Resume();
                    _listener = listener; Port = port; generation = currentGeneration; _running = true;
                    // Capture this listener; a previous accept thread must never use a new one.
                    _accept = new Thread(delegate() { AcceptLoop(listener, currentGeneration); });
                    _accept.IsBackground = true; _accept.Name = "RevitMcp-Accept"; _accept.Start();
                }
                catch { listener.Stop(); _running = false; Dispatcher.Pause(); throw; }
            }

            Log.Info("MCP HTTP 서버 시작: http://127.0.0.1:" + port + "/mcp");
        }

        public void Stop()
        {
            lock (lifecycleGate)
            {
                if (!_running) return;
                _running = false;
                Dispatcher.Pause();
                try { _listener.Stop(); } catch { }
                // Unblock old partial reads and release connection slots immediately.
                foreach (TcpClient client in clients) { try { client.Close(); } catch { } }
                clients.Clear();
            }
            Log.Info("MCP HTTP 서버 정지");
        }

        bool IsCurrent(long expectedGeneration)
        {
            lock (lifecycleGate) return _running && generation == expectedGeneration;
        }

        void AcceptLoop(TcpListener listener, long expectedGeneration)
        {
            while (IsCurrent(expectedGeneration))
            {
                TcpClient client = null;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { if (!IsCurrent(expectedGeneration)) break; continue; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log.Error("Accept 실패", ex); continue; }

                TcpClient c = client;
                lock (lifecycleGate)
                {
                    if (!_running || generation != expectedGeneration || clients.Count >= 32) { c.Close(); continue; }
                    clients.Add(c);
                }
                ThreadPool.QueueUserWorkItem(delegate { try { HandleClient(c, expectedGeneration); } finally { lock (lifecycleGate) clients.Remove(c); } });
            }
        }

        void HandleClient(TcpClient client, long expectedGeneration)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (NetworkStream stream = client.GetStream())
                {
                    // keep-alive: 같은 연결로 여러 요청이 올 수 있다.
                    while (IsCurrent(expectedGeneration) && client.Connected)
                    {
                        Request req = ReadRequest(stream);
                        if (req == null || !IsCurrent(expectedGeneration)) break;

                        Response res = Dispatcher.InServerRequest(expectedGeneration, delegate { return Route(req); });
                        if (!IsCurrent(expectedGeneration)) break;
                        string connection;
                        if (req.Headers.TryGetValue("Connection", out connection) && string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)) res.KeepAlive = false;
                        WriteResponse(stream, res);
                        if (!res.KeepAlive) break;
                    }
                }
            }
            catch (IOException) { }
            catch (Exception ex) { Log.Error("연결 처리 실패", ex); }
            finally { try { client.Close(); } catch { } }
        }

        sealed class Request
        {
            public string Method;
            public string Path;
            public readonly Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body = "";
        }

        sealed class Response
        {
            public int Status = 200;
            public string StatusText = "OK";
            public string ContentType = "application/json";
            public string Body = "";
            public bool KeepAlive = true;
        }

        static Request ReadRequest(NetworkStream stream)
        {
            // 헤더는 ASCII 라서 한 바이트씩 읽어도 안전하다. CRLF CRLF 까지 읽는다.
            MemoryStream head = new MemoryStream();
            int state = 0;
            while (state < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                if (head.Length > 32768) throw new IOException("HTTP header exceeds 32 KiB.");
                if ((state == 0 || state == 2) && b == 13) state++;
                else if ((state == 1 || state == 3) && b == 10) state++;
                else state = (b == 13) ? 1 : 0;
            }

            string headText = Encoding.ASCII.GetString(head.ToArray());
            string[] lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            Request req = new Request();
            string[] parts = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            req.Method = parts[0];
            req.Path = parts[1];

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            string cl;
            if (req.Headers.ContainsKey("Transfer-Encoding")) throw new IOException("Chunked request bodies are not supported. Send Content-Length.");
            if (req.Headers.TryGetValue("Content-Length", out cl))
            {
                int len;
                if (!int.TryParse(cl, out len) || len < 0 || len > 8388608) throw new IOException("Invalid Content-Length; maximum 8 MiB.");
                if (len > 0)
                {
                    byte[] buf = new byte[len];
                    int got = 0;
                    while (got < len)
                    {
                        int n = stream.Read(buf, got, len - got);
                        if (n <= 0) return null;
                        got += n;
                    }
                    req.Body = Encoding.UTF8.GetString(buf);
                }
            }
            return req;
        }

        Response Route(Request req)
        {
            // DNS rebinding 방지: Origin 이 붙어 있으면 localhost 계열만 허용한다.
            string origin;
            if (req.Headers.TryGetValue("Origin", out origin) && !string.IsNullOrEmpty(origin))
            {
                Uri parsedOrigin;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out parsedOrigin) ||
                    (parsedOrigin.Scheme != "http" && parsedOrigin.Scheme != "https") ||
                    (parsedOrigin.Host != "localhost" && parsedOrigin.Host != "127.0.0.1" && parsedOrigin.Host != "[::1]"))
                {
                    return new Response { Status = 403, StatusText = "Forbidden", Body = "{\"error\":\"origin not allowed\"}" };
                }
            }

            string path = req.Path;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            path = path.TrimEnd('/');
            if (path.Length == 0) path = "/";

            if (req.Method == "GET" && (path == "/health" || path == "/"))
            {
                return new Response
                {
                    Body = "{\"status\":\"ok\",\"server\":\"revit-mcp\",\"port\":" + Port +
                           ",\"tools\":" + ToolRegistry.Count + "}"
                };
            }

            if (path != "/mcp")
                return new Response { Status = 404, StatusText = "Not Found", Body = "{\"error\":\"not found\"}" };

            // Streamable HTTP: 서버 개시 SSE 스트림은 지원하지 않는다. 응답은 항상 단일 JSON.
            if (req.Method == "GET")
                return new Response { Status = 405, StatusText = "Method Not Allowed", Body = "{\"error\":\"SSE stream not supported\"}" };

            if (req.Method == "DELETE")
                return new Response { Body = "{}" };

            if (req.Method != "POST")
                return new Response { Status = 405, StatusText = "Method Not Allowed", Body = "{\"error\":\"method not allowed\"}" };

            string responseJson = McpProtocol.Handle(req.Body);

            if (responseJson == null)
            {
                // 알림(notification) 은 본문 없이 202 로 답한다.
                return new Response { Status = 202, StatusText = "Accepted", ContentType = null, Body = "" };
            }
            return new Response { Body = responseJson };
        }

        static void WriteResponse(NetworkStream stream, Response res)
        {
            byte[] body = Encoding.UTF8.GetBytes(res.Body ?? "");
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(res.Status).Append(" ").Append(res.StatusText).Append("\r\n");
            if (res.ContentType != null)
                sb.Append("Content-Type: ").Append(res.ContentType).Append("; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: ").Append(res.KeepAlive ? "keep-alive" : "close").Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("\r\n");

            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(header, 0, header.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
