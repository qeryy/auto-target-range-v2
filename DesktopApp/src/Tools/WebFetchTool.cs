// 灵枢桌面版 —— 工具：抓取网页正文
//
// 与 http_request 的区别：那个返回原始响应（含头、HTML 源码），适合探测；
// 这个把 HTML 洗成可读正文，适合「看一眼这个页面说了什么」。
//
// 洗法（不引第三方 HTML 解析库，保持零依赖）：
//   1. 去掉 script / style / noscript / svg 整块
//   2. 块级标签转换行，br 转换行
//   3. 剥掉所有剩余标签
//   4. 解 HTML 实体
//   5. 合并连续空白
//
// 这个工具同时也是「可扩展性」的实证：新增一个工具只需要写这个文件 +
// 在 ToolRegistry.CreateDefault() 里加一行，其它地方一行不用改。

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class WebFetchTool : ITool
    {
        public string Name { get { return "web_fetch"; } }

        public string Description
        {
            get
            {
                return "抓取一个网页并返回它的可读正文（去掉标签、脚本、样式）。"
                     + "适合快速了解页面内容；要看原始响应头和 HTML 源码请用 http_request。";
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
    ""max_chars"": { ""type"": ""integer"", ""description"": ""最多返回多少字符，默认 8000"" },
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

            int maxChars = Arg.Int(args, "max_chars", 8000);
            if (maxChars <= 0 || maxChars > 40000) maxChars = 8000;
            int timeout = Arg.Int(args, "timeout", 20);
            if (timeout <= 0 || timeout > 120) timeout = 20;

            try
            {
                using (var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true,
                })
                using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) })
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpiritScout/2.0");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                        "text/html,application/xhtml+xml,*/*;q=0.8");

                    // 同 http_request：ResponseHeadersRead 才不会把整个响应体先缓冲进内存
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
                    {
                        int code = (int)resp.StatusCode;
                        // 别用 ReadAsStringAsync：它是全量读。上限 256KB——
                        // 抽出来的正文最多也就 maxChars（默认 8000）字符，
                        // 而且 StripHtml 那串正则要在原文上跑，原文越大峰值越高。
                        string raw = await ToolIo.ReadCappedAsync(resp, 256 * 1024)
                            .ConfigureAwait(false);
                        string contentType = resp.Content.Headers.ContentType == null
                            ? "" : resp.Content.Headers.ContentType.MediaType ?? "";

                        // 非 HTML（JSON / 纯文本 / 二进制）原样给一段
                        if (contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0
                            && contentType.IndexOf("xml", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            string body = raw.Length <= maxChars
                                ? raw : raw.Substring(0, maxChars) + "\n…（截断）";
                            return new ToolResult
                            {
                                Ok = code < 400,
                                Output = "HTTP " + code + " · " + contentType + "\n\n" + body,
                                Summary = "抓取 " + Core.Redact.Clean(url) + " → " + code + "（非 HTML，原样返回）",
                            };
                        }

                        string title = ExtractTitle(raw);
                        string text = HtmlToText(raw);
                        bool truncated = text.Length > maxChars;
                        if (truncated) text = text.Substring(0, maxChars) + "\n…（截断，原文 "
                            + (raw.Length) + " 字符 HTML）";

                        var sb = new StringBuilder();
                        sb.Append("HTTP ").Append(code).Append(" · ").Append(url).Append('\n');
                        if (!string.IsNullOrEmpty(title)) sb.Append("标题：").Append(title).Append('\n');
                        sb.Append("正文长度：").Append(text.Length)
                          .Append(truncated ? "（已截断）" : "").Append("\n\n");
                        sb.Append(text);

                        return new ToolResult
                        {
                            Ok = code < 400,
                            Output = sb.ToString(),
                            Summary = "抓取 " + Core.Redact.Clean(string.IsNullOrEmpty(title) ? url : title)
                                + " → " + code + " · " + text.Length + " 字",
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

        // ---------- HTML 洗成正文 ----------

        private static readonly Regex ReBlock = new Regex(
            @"</?(p|div|section|article|header|footer|nav|aside|main|h[1-6]|li|tr|blockquote|pre)\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReBr = new Regex(@"<br\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReDrop = new Regex(
            @"<(script|style|noscript|svg|iframe|template)\b[^>]*>.*?</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ReTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex ReBlank = new Regex(@"[ \t\u00A0]+", RegexOptions.Compiled);
        private static readonly Regex ReLines = new Regex(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>提取 &lt;title&gt;。</summary>
        public static string ExtractTitle(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? HtmlDecode(m.Groups[1].Value).Trim() : "";
        }

        /// <summary>HTML → 可读正文。</summary>
        public static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            string s = ReDrop.Replace(html, " ");
            s = ReBr.Replace(s, "\n");
            s = ReBlock.Replace(s, "\n");
            s = ReTag.Replace(s, " ");
            s = HtmlDecode(s);
            s = ReBlank.Replace(s, " ");
            // 行首行尾空白清掉，再把连续空行压成一个
            var lines = s.Split('\n');
            var sb = new StringBuilder();
            foreach (var ln in lines)
            {
                string t = ln.Trim();
                if (t.Length == 0) { sb.Append('\n'); continue; }
                sb.Append(t).Append('\n');
            }
            return ReLines.Replace(sb.ToString(), "\n\n").Trim();
        }

        /// <summary>常用 HTML 实体解码（不引 WebUtility 以兼容老框架路径）。</summary>
        public static string HtmlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
                 .Replace("&amp;", "&");
            // 数字实体
            s = Regex.Replace(s, @"&#(\d+);", m =>
            {
                int code;
                if (int.TryParse(m.Groups[1].Value, out code) && code > 0 && code < 0x10000)
                    return ((char)code).ToString();
                return m.Value;
            });
            s = Regex.Replace(s, @"&#x([0-9a-fA-F]+);", m =>
            {
                int code;
                if (int.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.HexNumber, null, out code)
                    && code > 0 && code < 0x10000)
                    return ((char)code).ToString();
                return m.Value;
            });
            return s;
        }
    }
}
