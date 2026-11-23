// 灵枢桌面版（原生）—— API 客户端（stdio 管道版）
//
// 与后端通信**不走任何 TCP 端口**：请求写成「长度前缀 + HTTP 报文」，投进子进程
// 的 stdin；响应从子进程的 stdout 读回来，同样是「长度前缀 + HTTP 报文」。
//
// 为什么这么绕：用户要的是「双击就开、像微信/QQ 那样的桌面软件」，后台开着
// 8010 端口本质上还是「跑了个 web 服务」。改成管道后，系统里没有任何 socket
// 在监听，任务管理器里只有这一个应用。
//
// 协议与 src/spiritscout/web/stdio_bridge.py 一一对应，改一边必须改另一边。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Native
{
    /// <summary>一次 API 调用失败时抛这个。</summary>
    public sealed class ApiException : Exception
    {
        private readonly int _status;
        private readonly string _body;

        public int Status { get { return _status; } }
        public string Body { get { return _body; } }

        public ApiException(int status, string body)
            : base(status == 0 ? body : string.Format("HTTP {0}: {1}", status, Trim(body)))
        {
            _status = status;
            _body = body ?? string.Empty;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "（空响应）";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }

    /// <summary>
    /// 通过子进程 stdin/stdout 与后端通信。
    /// 同一时刻只允许一个请求（桌面单用户场景足够，且避免帧交错）。
    /// </summary>
    public sealed class SpiritApi : IDisposable
    {
        private Stream _stdin;
        private Stream _stdout;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        /// <summary>后端 Host 头必须是这个——别的值会被后端的 Host 白名单拒掉（403）。</summary>
        private const string HostHeader = "localhost";

        public SpiritApi(Stream stdin, Stream stdout)
        {
            _stdin = stdin;
            _stdout = stdout;
        }

        /// <summary>服务真正起来之后，把管道接上（构造时还不知道流）。</summary>
        public void Rebind(Stream stdin, Stream stdout)
        {
            _stdin = stdin;
            _stdout = stdout;
        }

        // ---------- 帧读写 ----------

        private void WriteFrame(byte[] payload)
        {
            byte[] len = Encoding.ASCII.GetBytes(payload.Length.ToString() + "\n");
            _stdin.Write(len, 0, len.Length);
            _stdin.Write(payload, 0, payload.Length);
            _stdin.Flush();
        }

        private byte[] ReadFrame()
        {
            // 读长度行
            var sb = new StringBuilder();
            while (true)
            {
                int b = _stdout.ReadByte();
                if (b < 0) throw new EndOfStreamException("后端已退出（管道关闭）");
                if (b == '\n') break;
                sb.Append((char)b);
            }
            int size;
            if (!int.TryParse(sb.ToString().Trim(), out size) || size < 0 || size > 256 * 1024 * 1024)
                throw new InvalidDataException("帧长度异常：" + sb.ToString());

            var buf = new byte[size];
            int got = 0;
            while (got < size)
            {
                int n = _stdout.Read(buf, got, size - got);
                if (n <= 0) throw new EndOfStreamException("后端响应不完整");
                got += n;
            }
            return buf;
        }

        // ---------- 发一个 HTTP 请求，拿回状态码与 body ----------

        private void Exchange(string method, string path, string json,
            out int status, out string body)
        {
            byte[] bodyBytes = json == null ? new byte[0] : Encoding.UTF8.GetBytes(json);
            var head = new StringBuilder();
            head.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(HostHeader).Append("\r\n");
            head.Append("Accept: application/json\r\n");
            head.Append("Connection: close\r\n");
            if (bodyBytes.Length > 0)
            {
                head.Append("Content-Type: application/json\r\n");
                head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            }
            head.Append("\r\n");

            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            var payload = new byte[headBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headBytes, 0, payload, 0, headBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, payload, headBytes.Length, bodyBytes.Length);

            WriteFrame(payload);
            byte[] raw = ReadFrame();
            ParseResponse(raw, out status, out body);
        }

        private static void ParseResponse(byte[] raw, out int status, out string body)
        {
            string text = Encoding.UTF8.GetString(raw);
            int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string head = split >= 0 ? text.Substring(0, split) : text;
            body = split >= 0 ? text.Substring(split + 4) : "";

            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            status = 0;
            if (lines.Length > 0)
            {
                string[] parts = lines[0].Split(' ');
                if (parts.Length >= 2)
                    int.TryParse(parts[1], out status);
            }
        }

        private async Task<string> SendAsync(string method, string path, string json,
            CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    int status;
                    string body;
                    Exchange(method, path, json, out status, out body);
                    if (status < 200 || status >= 300)
                        throw new ApiException(status, body);
                    return body;
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<string> GetAsync(string path, CancellationToken ct = default(CancellationToken))
        {
            return SendAsync("GET", path, null, ct);
        }

        public Task<string> PostAsync(string path, string json,
            CancellationToken ct = default(CancellationToken))
        {
            return SendAsync("POST", path, json ?? "{}", ct);
        }

        public Task<string> PutAsync(string path, string json,
            CancellationToken ct = default(CancellationToken))
        {
            return SendAsync("PUT", path, json ?? "{}", ct);
        }

        public Task<string> DeleteAsync(string path,
            CancellationToken ct = default(CancellationToken))
        {
            return SendAsync("DELETE", path, null, ct);
        }

        /// <summary>探一下后端是否已经能应答（首次调用兼做就绪检测）。</summary>
        public bool Ping(int timeoutMs = 5000)
        {
            try
            {
                var t = Task.Run(() => SendAsync("GET", "/api/sessions", null, CancellationToken.None));
                return t.Wait(timeoutMs);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>只释放自己的信号量；**不关流**——流的生命周期归 BackendService。</summary>
        public void Dispose()
        {
            try { _gate.Dispose(); } catch { }
        }
    }
}
