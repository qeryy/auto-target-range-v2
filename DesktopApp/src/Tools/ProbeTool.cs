// 灵枢桌面版 —— 探测工具
//
// 渗透测试里最常重复的三个手工动作：**探端口**、**认技术栈**、**试常见路径**。
//
// 之前模型只能 `shell` 里拼 nmap / curl 命令，问题有三个：
//   · 本机不一定装了 nmap（Windows 上大概率没装）
//   · 命令输出是给人看的表格，模型还得从里面抠数据
//   · 每条命令都是新的进程，几十个端口就是几十次启动开销
//
// 这个工具把三件事做成原生实现：
//   · `port`         —— TCP connect 探测，纯 .NET，不需要外部工具
//   · `fingerprint`  —— 抓一次 HTTP，从响应头/标题/Cookie 里认技术栈
//   · `paths`        —— 并发试一批常见敏感路径
//
// 都受同一个 Rate 限制（每秒最多几个连接），免得把目标打挂——
// 这是**探测**工具，不是压力测试工具。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class ProbeTool : ITool
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            }
            catch { }

            var handler = new HttpClientHandler
            {
                // 探测时证书多半是自签的，不该因为证书问题就看不到指纹
                ServerCertificateCustomValidationCallback = delegate { return true; },
                AllowAutoRedirect = true,
                UseCookies = true,
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpiritScout/2.0");
            return c;
        }

        public string Name { get { return "probe"; } }

        public string Description
        {
            get
            {
                return "探测目标：端口开放情况、HTTP 技术栈指纹、常见敏感路径。"
                     + "actions: port | fingerprint | paths。"
                     + "**只做合法授权的测试**；并发已限制，别拿它当压测工具。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""port"", ""fingerprint"", ""paths""], ""description"": ""探测类型"" },
    ""host"": { ""type"": ""string"", ""description"": ""主机名或 IP；port 用"" },
    ""url"": { ""type"": ""string"", ""description"": ""完整 URL；fingerprint / paths 用"" },
    ""ports"": { ""type"": ""string"", ""description"": ""端口列表，如 22,80,443 或 1-1024；默认常用 30 个"" },
    ""timeout_ms"": { ""type"": ""integer"", ""description"": ""单端口超时，默认 800"" },
    ""concurrency"": { ""type"": ""integer"", ""description"": ""并发数，默认 50，最多 120"" }
  },
  ""required"": [""action""]
}";
            }
        }

        /// <summary>常见的 30 个端口。不写全 65535 是有意的——扫全量在真实目标上太重。</summary>
        private static readonly int[] CommonPorts =
        {
            21, 22, 23, 25, 53, 80, 110, 111, 135, 139,
            143, 443, 445, 993, 995, 1433, 1521, 1723, 2049, 2375,
            3306, 3389, 5432, 5900, 6379, 8000, 8080, 8443, 9000, 27017
        };

        /// <summary>常见敏感路径。</summary>
        private static readonly string[] CommonPaths =
        {
            "/robots.txt", "/sitemap.xml", "/.git/HEAD", "/.git/config", "/.svn/entries",
            "/.env", "/.DS_Store", "/web.config", "/phpinfo.php", "/info.php",
            "/admin", "/admin/", "/login", "/wp-admin/", "/wp-login.php",
            "/server-status", "/actuator", "/actuator/env", "/api", "/api/v1",
            "/swagger-ui.html", "/swagger/index.html", "/v2/api-docs", "/openapi.json",
            "/backup.zip", "/backup.sql", "/config.php.bak", "/index.php.bak",
            "/console", "/druid/index.html", "/nacos/", "/jenkins/",
        };

        public async Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            try
            {
                if (action == "port") return await ProbePortsAsync(args, ct).ConfigureAwait(false);
                if (action == "fingerprint") return await FingerprintAsync(args, ct).ConfigureAwait(false);
                if (action == "paths") return await ProbePathsAsync(args, ct).ConfigureAwait(false);
                return ToolResult.Fail("action 只能是 port / fingerprint / paths");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("探测失败：" + ex.Message);
            }
        }

        // ---------- 端口 ----------

        private async Task<ToolResult> ProbePortsAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string host = (Arg.Str(args, "host") ?? "").Trim();
            if (host.Length == 0) return ToolResult.Fail("host 不能为空");

            int timeoutMs = Arg.Int(args, "timeout_ms", 800);
            if (timeoutMs < 100) timeoutMs = 100;
            if (timeoutMs > 5000) timeoutMs = 5000;

            int conc = Arg.Int(args, "concurrency", 50);
            if (conc < 1) conc = 1;
            if (conc > 120) conc = 120;

            int[] ports = ParsePorts(Arg.Str(args, "ports"));
            if (ports.Length == 0) ports = CommonPorts;
            if (ports.Length > 4096) return ToolResult.Fail("一次最多 4096 个端口");

            var open = new List<int>();
            var sw = Stopwatch.StartNew();

            using (var gate = new SemaphoreSlim(conc, conc))
            {
                var tasks = new List<Task>();
                foreach (int p in ports)
                {
                    int port = p;
                    tasks.Add(Task.Run(async delegate
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (await IsOpenAsync(host, port, timeoutMs, ct).ConfigureAwait(false))
                                lock (open) { open.Add(port); }
                        }
                        catch { }
                        finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            sw.Stop();
            open.Sort();

            var sb = new StringBuilder();
            sb.Append("探测 ").Append(host).Append(" · ").Append(ports.Length).Append(" 个端口 · ")
              .Append(sw.ElapsedMilliseconds).Append("ms\n\n");
            if (open.Count == 0)
            {
                sb.Append("没有发现开放端口。\n");
                sb.Append("（如果是内网主机，可能被防火墙拦；也可能是主机不通。）");
            }
            else
            {
                sb.Append("开放 ").Append(open.Count).Append(" 个：\n\n");
                foreach (int p in open)
                    sb.Append("  ").Append(p).Append("  ").Append(GuessService(p)).Append('\n');
                sb.Append("\n（下一步可以对这些端口抓 banner 或试 HTTP）");
                // 把「没开的那些」也交代一句。
                // 只列开放端口是对的（跟 nmap 默认一致），但用户指定了 N 个却只看到 M 个，
                // 得知道剩下的是「关了」还是「还在测」。
                if (open.Count < ports.Length)
                {
                    sb.Append("\n另外 ").Append(ports.Length - open.Count)
                      .Append(" 个端口关闭或超时（超时通常是防火墙丢包，不一定是关着）。");
                }
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = host + " 开放 " + open.Count + "/" + ports.Length + " 个端口"
                    + (open.Count > 0 && open.Count <= 6 ? "：" + Join(open) : ""),
            };
        }

        private static async Task<bool> IsOpenAsync(string host, int port, int timeoutMs, CancellationToken ct)
        {
            using (var tcp = new TcpClient())
            {
                var connect = tcp.ConnectAsync(host, port);
                var done = await Task.WhenAny(connect,
                    Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
                if (done != connect) return false;
                try { await connect.ConfigureAwait(false); return tcp.Connected; }
                catch { return false; }
            }
        }

        private static int[] ParsePorts(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return new int[0];
            var list = new List<int>();
            foreach (var part in spec.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    int a, b;
                    if (int.TryParse(p.Substring(0, dash), out a) && int.TryParse(p.Substring(dash + 1), out b))
                    {
                        if (a < 1) a = 1;
                        if (b > 65535) b = 65535;
                        for (int i = a; i <= b && list.Count < 4096; i++) list.Add(i);
                    }
                }
                else
                {
                    int v;
                    if (int.TryParse(p, out v) && v >= 1 && v <= 65535) list.Add(v);
                }
            }
            return list.ToArray();
        }

        private static string GuessService(int port)
        {
            switch (port)
            {
                case 21: return "ftp";
                case 22: return "ssh";
                case 23: return "telnet";
                case 25: return "smtp";
                case 53: return "dns";
                case 80: return "http";
                case 110: return "pop3";
                case 111: return "rpcbind";
                case 135: return "msrpc";
                case 139: return "netbios";
                case 143: return "imap";
                case 443: return "https";
                case 445: return "smb";
                case 993: return "imaps";
                case 995: return "pop3s";
                case 1433: return "mssql";
                case 1521: return "oracle";
                case 1723: return "pptp";
                case 2049: return "nfs";
                case 2375: return "docker";
                case 3306: return "mysql";
                case 3389: return "rdp";
                case 5432: return "postgres";
                case 5900: return "vnc";
                case 6379: return "redis";
                case 8000: return "http-alt";
                case 8080: return "http-proxy";
                case 8443: return "https-alt";
                case 9000: return "php-fpm/http";
                case 27017: return "mongodb";
                default: return "";
            }
        }

        private static string Join(List<int> ports)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ports.Count && i < 6; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ports[i]);
            }
            return sb.ToString();
        }

        // ---------- 指纹 ----------

        private async Task<ToolResult> FingerprintAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string url = (Arg.Str(args, "url") ?? "").Trim();
            if (url.Length == 0) return ToolResult.Fail("url 不能为空");
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;

            var sb = new StringBuilder();
            sb.Append("指纹 ").Append(url).Append("\n\n");

            HttpResponseMessage resp;
            try
            {
                resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("请求失败：" + ex.Message);
            }

            using (resp)
            {
                sb.Append("HTTP ").Append((int)resp.StatusCode).Append(' ').Append(resp.ReasonPhrase).Append("\n\n");

                // 关键响应头
                sb.Append("--- 响应头（节选）---\n");
                foreach (var name in new[] { "Server", "X-Powered-By", "X-AspNet-Version",
                                             "X-Generator", "Via", "CF-RAY", "X-Cache" })
                {
                    var vals = HeaderValues(resp, name);
                    if (vals.Length > 0) sb.Append(name).Append(": ").Append(vals).Append('\n');
                }
                var setCookie = HeaderValues(resp, "Set-Cookie");
                if (setCookie.Length > 0) sb.Append("Set-Cookie: ").Append(setCookie).Append('\n');
                sb.Append('\n');

                // 正文取样
                string body = "";
                try { body = await ToolIo.ReadCappedAsync(resp, 256 * 1024).ConfigureAwait(false); }
                catch { }

                string title = Regex.Match(body, @"<title[^>]*>(?<t>.*?)</title>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["t"].Value;
                title = Regex.Replace(title ?? "", @"\s+", " ").Trim();
                if (title.Length > 0) sb.Append("标题: ").Append(title).Append("\n\n");

                // 技术栈推断
                var findings = InferStack(body, setCookie, resp);
                if (findings.Count > 0)
                {
                    sb.Append("--- 识别到的技术栈 ---\n");
                    foreach (var t in findings) sb.Append("  · ").Append(t).Append('\n');
                    sb.Append('\n');
                }
                else
                {
                    sb.Append("（没有识别出明显特征。可以试试 paths 看看有没有已知入口。）\n\n");
                }

                return new ToolResult
                {
                    Ok = true,
                    Output = sb.ToString(),
                    Summary = "指纹 " + HostOf(url),
                };
            }
        }

        /// <summary>从 URL 里取主机名（C# 5 没有 inline out，这里包一层）。</summary>
        private static string HostOf(string url)
        {
            Uri u;
            if (Uri.TryCreate(url, UriKind.Absolute, out u) && u != null) return u.Host;
            return url;
        }

        private static string HeaderValues(HttpResponseMessage resp, string name)
        {
            IEnumerable<string> vals;
            if (resp.Headers.TryGetValues(name, out vals))
                return string.Join("; ", vals);
            if (resp.Content != null && resp.Content.Headers.TryGetValues(name, out vals))
                return string.Join("; ", vals);
            return "";
        }

        /// <summary>从响应特征里推断技术栈。规则不多，够覆盖常见目标。</summary>
        private static List<string> InferStack(string body, string cookies, HttpResponseMessage resp)
        {
            var found = new List<string>();
            string lower = (body ?? "").ToLowerInvariant();
            string ck = (cookies ?? "").ToLowerInvariant();

            string server = HeaderValues(resp, "Server").ToLowerInvariant();
            if (server.Contains("nginx")) found.Add("Nginx");
            if (server.Contains("apache")) found.Add("Apache");
            if (server.Contains("iis") || server.Contains("microsoft-iis")) found.Add("IIS");
            if (server.Contains("openresty")) found.Add("OpenResty");
            if (server.Contains("tomcat")) found.Add("Tomcat");
            if (server.Contains("jetty")) found.Add("Jetty");

            string powered = HeaderValues(resp, "X-Powered-By").ToLowerInvariant();
            if (powered.Contains("php")) found.Add("PHP " + PoweredVersion(powered, "php"));
            if (powered.Contains("asp.net")) found.Add("ASP.NET");
            if (powered.Contains("express")) found.Add("Express (Node.js)");

            if (ck.Contains("phpsessid")) found.Add("PHP");
            if (ck.Contains("jsessionid")) found.Add("Java (Servlet)");
            if (ck.Contains("asp.net_sessionid")) found.Add("ASP.NET");
            if (ck.Contains("laravel_session")) found.Add("Laravel");
            if (ck.Contains("ci_session")) found.Add("CodeIgniter");

            if (lower.Contains("wp-content") || lower.Contains("wp-includes")) found.Add("WordPress");
            if (lower.Contains("/_next/")) found.Add("Next.js");
            if (lower.Contains("__nuxt")) found.Add("Nuxt");
            if (lower.Contains("ng-version") || lower.Contains("ng-app")) found.Add("Angular");
            if (lower.Contains("data-reactroot") || lower.Contains("react.")) found.Add("React");
            if (lower.Contains("vue.js") || lower.Contains("v-cloak")) found.Add("Vue");
            if (lower.Contains("jquery")) found.Add("jQuery");
            if (lower.Contains("bootstrap")) found.Add("Bootstrap");

            if (lower.Contains("spring") || lower.Contains("whitelabel error page")) found.Add("Spring");
            if (lower.Contains("django")) found.Add("Django");
            if (lower.Contains("csrfmiddlewaretoken")) found.Add("Django");
            if (lower.Contains("shiro")) found.Add("Apache Shiro");
            if (lower.Contains("thinkphp")) found.Add("ThinkPHP");

            return found;
        }

        private static string PoweredVersion(string powered, string product)
        {
            var m = Regex.Match(powered, product + @"/([\d.]+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ---------- 路径 ----------

        private async Task<ToolResult> ProbePathsAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string url = (Arg.Str(args, "url") ?? "").Trim();
            if (url.Length == 0) return ToolResult.Fail("url 不能为空");
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            url = url.TrimEnd('/');

            int conc = Arg.Int(args, "concurrency", 12);
            if (conc < 1) conc = 1;
            if (conc > 40) conc = 40;

            // 允许自定义路径列表
            var paths = new List<string>();
            string custom = (Arg.Str(args, "paths") ?? "").Trim();
            if (custom.Length > 0 && custom.IndexOf('/') >= 0)
            {
                foreach (var p in custom.Split(new[] { ',', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    paths.Add(p.StartsWith("/") ? p : "/" + p);
            }
            else
            {
                paths.AddRange(CommonPaths);
            }

            var hits = new List<string>();
            var sw = Stopwatch.StartNew();

            using (var gate = new SemaphoreSlim(conc, conc))
            {
                var tasks = new List<Task>();
                foreach (var path in paths)
                {
                    string p = path;
                    tasks.Add(Task.Run(async delegate
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            using (var req = new HttpRequestMessage(HttpMethod.Get, url + p))
                            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                .ConfigureAwait(false))
                            {
                                int code = (int)resp.StatusCode;
                                // 404 / 400 一般是「没有」；其余都可能有意思
                                if (code == 404 || code == 400 || code == 410) return;
                                long len = resp.Content.Headers.ContentLength ?? -1;
                                lock (hits)
                                    hits.Add(code + "  " + p + (len >= 0 ? "  (" + len + " B)" : ""));
                            }
                        }
                        catch { }
                        finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            sw.Stop();
            hits.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.Append("路径探测 ").Append(url).Append(" · 试了 ").Append(paths.Count)
              .Append(" 条 · ").Append(sw.ElapsedMilliseconds).Append("ms\n\n");
            if (hits.Count == 0)
            {
                sb.Append("都返回 404/400，没发现。\n");
                sb.Append("（可能站点对所有路径都返 200，那样这个结果也不可信——先手动看一眼。）");
            }
            else
            {
                sb.Append("命中 ").Append(hits.Count).Append(" 条：\n");
                foreach (var h in hits) sb.Append("  ").Append(h).Append('\n');
                sb.Append("\n注意：403 也算命中（说明路径存在但被拦），值得跟进。");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "路径探测 " + hits.Count + "/" + paths.Count + " 命中",
            };
        }
    }
}
