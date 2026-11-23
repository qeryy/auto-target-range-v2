// 灵枢桌面版 —— http 请求工具
//
// 发一个 HTTP 请求，返回状态码、响应头与正文。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class HttpTool : ITool
    {
        public string Name { get { return "http_request"; } }

        public string Description
        {
            get
            {
                return "发一个 HTTP 请求并返回状态码、响应头与正文。"
                     + "用于探测接口、看页面、验证 SSRF 之类的场景。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""url"": { ""type"": ""string"", ""description"": ""完整 URL"" },
    ""method"": { ""type"": ""string"", ""description"": ""GET/POST/PUT/DELETE，默认 GET"" },
    ""headers"": { ""type"": ""object"", ""description"": ""请求头键值对"" },
    ""body"": { ""type"": ""string"", ""description"": ""请求体（POST/PUT 用）"" },
    ""timeout"": { ""type"": ""integer"", ""description"": ""超时秒数，默认 20"" }
  },
  ""required"": [""url""]
}";
            }
        }

        public async Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            // .NET Framework 的默认 SecurityProtocol 在不少机器上是 `Ssl3, Tls`，
            // 不含 TLS 1.2——连现代站点会在百毫秒内被掐断。这里显式设一次。
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12;
            }
            catch { }
            string url = Arg.Str(args, "url");
            if (string.IsNullOrWhiteSpace(url)) return ToolResult.Fail("url 不能为空");
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            string method = (Arg.Str(args, "method", "GET") ?? "GET").ToUpperInvariant();
            int timeout = Arg.Int(args, "timeout", 20);
            if (timeout <= 0 || timeout > 300) timeout = 20;

            try
            {
                using (var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,   // 探测时不自动跟随，能看清 3xx
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true,
                })
                using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) })
                {
                    var req = new HttpRequestMessage(new HttpMethod(method), url);
                    var headers = Arg.Dict(args, "headers");
                    if (headers != null)
                    {
                        foreach (var kv in headers)
                        {
                            if (!req.Headers.TryAddWithoutValidation(kv.Key, kv.Value)) { }
                        }
                    }
                    string body = Arg.Str(args, "body");
                    if (!string.IsNullOrEmpty(body) && method != "GET" && method != "HEAD")
                    {
                        var contentType = "application/x-www-form-urlencoded";
                        if (headers != null)
                        {
                            foreach (var kv in headers)
                            {
                                if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                                    contentType = kv.Value;
                            }
                        }
                        req.Content = new StringContent(body, Encoding.UTF8, contentType);
                    }

                    // 关键：HttpCompletionOption.ResponseHeadersRead
                    //
                    // 默认的 ResponseContentRead 会**先把整个响应体读进内存**再把
                    // HttpResponseMessage 交给你。所以哪怕下游只用 512KB，
                    // 20MB 的响应照样先吃掉 20MB（实测峰值涨了 26MB）。
                    //
                    // ResponseHeadersRead 拿到响应头就返回，body 留着让你自己流式读。
                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false))
                    {
                        // 别用 ReadAsStringAsync()：它是全量读。
                        string text = await ToolIo.ReadCappedAsync(resp, 512 * 1024).ConfigureAwait(false);
                        int code = (int)resp.StatusCode;

                        var sb = new StringBuilder();
                        sb.Append("HTTP ").Append(code).Append(' ').Append(resp.ReasonPhrase).Append('\n');
                        sb.Append("--- 响应头 ---\n");
                        foreach (var h in resp.Headers) sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');
                        foreach (var h in resp.Content.Headers) sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');
                        sb.Append("\n--- 响应体（前 8000 字符）---\n");
                        sb.Append(text.Length <= 8000 ? text : text.Substring(0, 8000) + "\n…（共 " + text.Length + " 字符）");

                        return new ToolResult
                        {
                            // **拿到响应就算成功**，哪怕状态码是 404/403/500。
                            //
                            // 原来写的是 `Ok = code < 400`，于是探测到 404 时模型看到
                            // 「工具失败」，很可能就不会去读 Output 里的响应内容——
                            // 而 404（路径不存在）、403（存在但没权限）、500（触发了错误）
                            // **正是渗透测试最想知道的信息**。
                            //
                            // 真正的失败只有两种：连不上、超时，走下面的 catch。
                            // 状态码在 Output 第一行写着（`HTTP 404 Not Found`），
                            // Summary 也带着（`GET /x → 404`），不需要靠 Ok 表达。
                            Ok = true,
                            Output = sb.ToString(),
                            // URL 里可能带 ?token=xxx，脱敏后再落盘
                            Summary = Core.Redact.Clean(method + " " + url) + " → " + code,
                        };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return ToolResult.Fail("请求超时（" + timeout + " 秒）");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    // ===================== file =====================
}
