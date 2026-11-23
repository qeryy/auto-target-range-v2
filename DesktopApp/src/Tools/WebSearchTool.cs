// 灵枢桌面版 —— 联网搜索工具
//
// web 版有 web_search（445 行），桌面版一直没有。模型遇到需要查资料的任务
// （查 CVE 编号、查某个函数怎么用、查错误信息什么意思）只能自己拼 URL 去抓页面，
// 经常抓不到正题。
//
// 这里用一个**不需要 API Key** 的路子：DuckDuckGo 的 HTML 端点。
//
// 为什么不接 Bing / Google / Serper：
//   · 都要 key，而本应用的定位是「填个模型端点就能用」，不该再要求注册搜索服务
//   · key 要存在 secrets.dat 里，多一处泄露面
//
// HTML 端点的缺点：返回的是页面不是 JSON，得自己抠。但结构很稳定
// （`result__a` 是标题链接、`result__snippet` 是摘要），抠起来不复杂。
//
// 隐私：查什么会发给 DuckDuckGo。这一点在工具的 description 里写清楚，
// 让模型自己判断该不该用（比如查内网主机名就不该走这里）。

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class WebSearchTool : ITool
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // .NET Framework 4.8 的默认 SecurityProtocol 在不少机器上是 `Ssl3, Tls`
            // ——**没有 TLS 1.2**。Bing / 大多数现代站点只接受 1.2+，
            // 于是连接会在 130ms 左右被掐断（HttpRequestException「基础连接已关闭」）。
            //
            // 实测：不设 → 136ms 失败；设成 Tls12 → HTTP 200 / 326ms / 101KB。
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12;
            }
            catch { }

            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpiritScout/2.0");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            return c;
        }

        public string Name { get { return "web_search"; } }

        public string Description
        {
            get
            {
                return "联网搜索，返回标题、链接和摘要。用于查 CVE、查报错含义、查工具用法。"
                     + "注意：查询词会发送到公共搜索引擎（Bing / DuckDuckGo），**不要**用它搜内网主机名、口令、flag 原文这类敏感内容。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""搜索词"" },
    ""count"": { ""type"": ""integer"", ""description"": ""要几条结果，默认 6，最多 15"" }
  },
  ""required"": [""query""]
}";
            }
        }

        public async Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string query = Arg.Str(args, "query");
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query 不能为空");

            int count = Arg.Int(args, "count", 6);
            if (count <= 0) count = 6;
            if (count > 15) count = 15;

            try
            {
                var hits = await SearchAsync(query, count, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return new ToolResult
                    {
                        Ok = true,
                        Output = "（没搜到结果。换个说法，或者直接用 http_request 抓具体页面）",
                        Summary = "搜「" + Core.Text.OneLine(query, 24) + "」无结果",
                    };
                }

                var sb = new StringBuilder();
                sb.Append("搜「").Append(query).Append("」共 ").Append(hits.Count).Append(" 条：\n");
                int i = 1;
                foreach (var h in hits)
                {
                    sb.Append("\n").Append(i++).Append(". ").Append(h.Title).Append('\n');
                    sb.Append("   ").Append(h.Url).Append('\n');
                    if (!string.IsNullOrEmpty(h.Snippet))
                        sb.Append("   ").Append(h.Snippet).Append('\n');
                }
                sb.Append("\n（来源：Bing / DuckDuckGo 自动选择。要读全文就用 http_request 或 web_fetch 抓那个链接。）");

                return new ToolResult
                {
                    Ok = true,
                    Output = sb.ToString(),
                    Summary = "搜「" + Core.Text.OneLine(query, 24) + "」得到 " + hits.Count + " 条",
                };
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("搜索失败：" + ex.Message);
            }
        }

        private sealed class Hit
        {
            public string Title = "";
            public string Url = "";
            public string Snippet = "";
        }

        private static async Task<List<Hit>> SearchAsync(string query, int count, CancellationToken ct)
        {
            // 搜索源顺序：Bing → DuckDuckGo。
            //
            // 为什么 Bing 在前：**国内 DuckDuckGo 的 DNS 直接解析不了**
            // （实测 html.duckduckgo.com 报「未能解析此远程名称」），
            // 而 cn.bing.com 是 546ms / 101KB，稳定可用。
            //
            // 留 DuckDuckGo 做备选：国外网络下它更干净（没那么多推广位）。
            Exception last = null;

            foreach (var engine in new[] { "bing", "ddg" })
            {
                try
                {
                    var hits = engine == "bing"
                        ? await SearchBingAsync(query, count, ct).ConfigureAwait(false)
                        : await SearchDdgAsync(query, count, ct).ConfigureAwait(false);
                    if (hits.Count > 0) return hits;
                }
                catch (Exception ex) { last = ex; }
            }

            if (last != null) throw last;
            return new List<Hit>();
        }

        private static async Task<string> FetchAsync(string url, CancellationToken ct)
        {
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
                return await ToolIo.ReadCappedAsync(resp, 1024 * 1024).ConfigureAwait(false);
            }
        }

        /// <summary>Bing：结果在 li.b_algo 里，标题是 h2>a，摘要在 .b_caption。</summary>
        private static async Task<List<Hit>> SearchBingAsync(string query, int count, CancellationToken ct)
        {
            string html = await FetchAsync(
                "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);

            var hits = new List<Hit>();

            // 每个结果块以 li class="b_algo" 开头
            var blocks = Regex.Split(html, @"<li[^>]*class=""[^""]*b_algo[^""]*""", RegexOptions.IgnoreCase);

            var linkRe = new Regex(@"<h2[^>]*>\s*<a[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snipRe = new Regex(@"<p[^>]*class=""[^""]*b_lineclamp[^""]*""[^>]*>(?<text>.*?)</p>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var capRe = new Regex(@"(?<text>[^<>]{20,300})",
                RegexOptions.Singleline);

            for (int i = 1; i < blocks.Length && hits.Count < count; i++)   // i=0 是 b_algo 之前的部分
            {
                string block = blocks[i];
                var lm = linkRe.Match(block);
                if (!lm.Success) continue;

                string url = System.Net.WebUtility.HtmlDecode(lm.Groups["url"].Value);
                string title = StripTags(lm.Groups["title"].Value);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
                if (url.StartsWith("/") || url.StartsWith("#")) continue;

                var hit = new Hit { Title = title, Url = url };

                var sm = snipRe.Match(block);
                if (sm.Success) hit.Snippet = StripTags(sm.Groups["text"].Value);
                if (string.IsNullOrEmpty(hit.Snippet))
                {
                    // 退一步：块里找一段像摘要的连续文本
                    var cm = capRe.Match(block);
                    if (cm.Success) hit.Snippet = StripTags(cm.Groups["text"].Value);
                }
                if (hit.Snippet.Length > 300) hit.Snippet = hit.Snippet.Substring(0, 300) + "…";

                hits.Add(hit);
            }
            return hits;
        }

        /// <summary>DuckDuckGo：结果在 a.result__a。</summary>
        private static async Task<List<Hit>> SearchDdgAsync(string query, int count, CancellationToken ct)
        {
            string html = await FetchAsync(
                "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);

            var hits = new List<Hit>();
            var blockRe = new Regex(
                @"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippetRe = new Regex(
                @"<a[^>]*class=""[^""]*result__snippet[^""]*""[^>]*>(?<text>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var matches = blockRe.Matches(html);
            var snippets = snippetRe.Matches(html);

            for (int i = 0; i < matches.Count && hits.Count < count; i++)
            {
                var m = matches[i];
                string rawUrl = System.Net.WebUtility.HtmlDecode(m.Groups["url"].Value);
                string title = StripTags(m.Groups["title"].Value);
                if (string.IsNullOrWhiteSpace(title)) continue;

                string realUrl = UnwrapDdg(rawUrl);
                if (string.IsNullOrEmpty(realUrl)) continue;

                var hit = new Hit { Title = title, Url = realUrl };
                if (i < snippets.Count)
                    hit.Snippet = StripTags(snippets[i].Groups["text"].Value);
                if (hit.Snippet.Length > 300) hit.Snippet = hit.Snippet.Substring(0, 300) + "…";
                hits.Add(hit);
            }
            return hits;
        }

        /// <summary>把 DuckDuckGo 的跳转链接还原成真实地址。</summary>
        private static string UnwrapDdg(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.StartsWith("//")) url = "https:" + url;
            int idx = url.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string enc = url.Substring(idx + 5);
                int amp = enc.IndexOf('&');
                if (amp >= 0) enc = enc.Substring(0, amp);
                try { return Uri.UnescapeDataString(enc); } catch { return ""; }
            }
            return url;
        }

        private static string StripTags(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, @"<[^>]+>", "");
            s = System.Net.WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim();
        }

    }
}
