// 灵枢桌面版 —— 正则工具
//
// ## 为什么需要这个
//
// CTF 里「从这坨输出里把 flag 找出来」出现的频率，和「这串是什么编码」差不多。
//
// 之前的做法是 `shell` 里调 grep：
//     grep -oP 'flag\{[^}]+\}' output.txt
//
// 问题：**Windows 上没有 grep**。桌面版跑在用户自己的机器上，那台机器很可能是
// Windows，`grep` / `sed` / `awk` 一概没有。模型写了 `grep -oP`，得到
// 「'grep' 不是内部或外部命令」，然后开始试 `findstr`（语法完全不同），
// 来回几轮就浪费了。
//
// 这个工具把「正则提取」做成一等能力，跨平台，不用记 grep 的参数。
//
// ## 三个动作
//
//   match    —— 列出所有匹配（带行号和上下文）
//   extract  —— 只输出匹配到的内容，一行一个（`grep -o` 的等价物）
//   replace  —— 替换并输出结果
//
// ## 输入来源
//
//   `text` 直接给字符串，或者 `path` 读文件（大文件也能处理）。
//
// ## 内置模式
//
// `preset` 参数可以用常见模式，不用每次写正则：
//   flag / ipv4 / url / email / hash / base64 / hex / path

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class RegexTool : ITool
    {
        public string Name { get { return "regex"; } }

        public string Description
        {
            get
            {
                return "正则匹配与提取，替代 grep/sed（Windows 上没有 grep）。"
                     + "actions: match（列匹配+行号）| extract（只输出匹配内容）| replace（替换）。"
                     + "输入用 text 或 path（读文件）。"
                     + "preset 可用内置模式：flag / ipv4 / url / email / hash / base64 / hex / path。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""match"", ""extract"", ""replace""], ""description"": ""做什么"" },
    ""pattern"": { ""type"": ""string"", ""description"": ""正则表达式；用 preset 时可省略"" },
    ""preset"": { ""type"": ""string"", ""enum"": [""flag"", ""flagonly"", ""ipv4"", ""url"", ""email"", ""hash"", ""base64"", ""hex"", ""path""], ""description"": ""内置模式，优先于 pattern。flag = 任何 word{...}；flagonly = 只认带 flag 字样的"" },
    ""text"": { ""type"": ""string"", ""description"": ""直接给输入内容"" },
    ""path"": { ""type"": ""string"", ""description"": ""从文件读输入（和 text 二选一）"" },
    ""replacement"": { ""type"": ""string"", ""description"": ""replace 时替换成什么，支持 $1 $2"" },
    ""ignore_case"": { ""type"": ""boolean"", ""description"": ""忽略大小写，默认 false"" },
    ""multiline"": { ""type"": ""boolean"", ""description"": ""^ $ 匹配每行首尾，默认 true"" },
    ""dotall"": { ""type"": ""boolean"", ""description"": "". 匹配换行，默认 false"" },
    ""context"": { ""type"": ""integer"", ""description"": ""match 时显示几行上下文，默认 0"" },
    ""max_hits"": { ""type"": ""integer"", ""description"": ""最多返回多少条，默认 200"" },
    ""unique"": { ""type"": ""boolean"", ""description"": ""extract 时去重，默认 false"" }
  },
  ""required"": [""action""]
}";
            }
        }

        /// <summary>内置模式。都是 CTF 里最常找的东西。</summary>
        private static string PresetPattern(string preset)
        {
            switch ((preset ?? "").Trim().ToLowerInvariant())
            {
                // 各种 flag 格式都要认：
                //   flag{...}  ctf{...}  FLAG{...}  hgame{...}  DASCTF{...}
                //   有些用 [] 或 () 当括号
                //
                // 一开始写的是 `[A-Za-z0-9_]*flag\s*[...]`，硬绑了 "flag" 这个词，
                // 于是 `ctf{second_one}` 匹配不到。改成「任意 2-20 个字母数字做前缀」，
                // 但要求括号里至少 4 个字符 —— 否则 `if(x)` 这种代码也会被当成 flag。
                case "flag":   return @"\b[A-Za-z0-9_]{2,20}\s*[\{\(\[](?<f>[^\}\)\]]{4,200})[\}\)\]]";

                // 只想匹配「明确写着 flag 的」
                case "flagonly": return @"[A-Za-z0-9_]*flag\s*[\{\(\[](?<f>[^\}\)\]]+)[\}\)\]]";
                case "ipv4":   return @"\b(?:(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\b";
                case "url":    return @"https?://[^\s""'<>\)\]]+";
                case "email":  return @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}";
                case "hash":   return @"\b(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\b";
                case "base64": return @"\b[A-Za-z0-9+/]{16,}={0,2}";
                case "hex":    return @"\b[a-fA-F0-9]{16,}\b";
                case "path":   return @"(?:[A-Za-z]:\\|/)[^\s""'<>|*?\r\n]+";
                default:       return null;
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string preset = Arg.Str(args, "preset");
            string pattern = Arg.Str(args, "pattern");
            string text = Arg.Str(args, "text");
            string path = Arg.Str(args, "path");

            RegexOptions opts = RegexOptions.None;
            if (Arg.Bool(args, "ignore_case", false)) opts |= RegexOptions.IgnoreCase;
            if (Arg.Bool(args, "multiline", true)) opts |= RegexOptions.Multiline;
            if (Arg.Bool(args, "dotall", false)) opts |= RegexOptions.Singleline;

            int context = Arg.Int(args, "context", 0);
            int maxHits = Arg.Int(args, "max_hits", 200);
            if (maxHits < 1) maxHits = 1;
            if (maxHits > 5000) maxHits = 5000;
            if (context < 0) context = 0;
            if (context > 5) context = 5;
            bool unique = Arg.Bool(args, "unique", false);

            // 预置模式优先
            if (!string.IsNullOrEmpty(preset))
            {
                string p = PresetPattern(preset);
                if (p == null) return Task.FromResult(ToolResult.Fail("不认识的内置模式：" + preset));
                pattern = p;
            }
            if (string.IsNullOrEmpty(pattern))
                return Task.FromResult(ToolResult.Fail("pattern 或 preset 至少给一个"));

            // 输入来源
            string input;
            string sourceName;
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) return Task.FromResult(ToolResult.Fail("文件不存在：" + path));
                    const long cap = 16L * 1024 * 1024;   // 16MB，够大但不会把内存吃穿
                    if (fi.Length > cap)
                        return Task.FromResult(ToolResult.Fail("文件太大（" + (fi.Length / 1024 / 1024) + " MB），"
                            + "先用 shell 截取一段再喂进来"));
                    using (var sr = new StreamReader(path, Encoding.UTF8, true))
                        input = sr.ReadToEnd();
                    sourceName = Path.GetFileName(path);
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail("读文件失败：" + ex.Message));
                }
            }
            else if (!string.IsNullOrEmpty(text))
            {
                input = text;
                sourceName = "(text)";
            }
            else
            {
                return Task.FromResult(ToolResult.Fail("要给 text 或 path"));
            }

            Regex re;
            try
            {
                re = new Regex(pattern, opts, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("正则写错了：" + ex.Message));
            }

            try
            {
                if (action == "match") return Task.FromResult(DoMatch(re, input, sourceName, context, maxHits));
                if (action == "extract") return Task.FromResult(DoExtract(re, input, sourceName, maxHits, unique));
                if (action == "replace")
                {
                    string repl = Arg.Str(args, "replacement") ?? "";
                    return Task.FromResult(DoReplace(re, input, repl));
                }
                return Task.FromResult(ToolResult.Fail("action 只能是 match / extract / replace"));
            }
            catch (RegexMatchTimeoutException)
            {
                return Task.FromResult(ToolResult.Fail("正则回溯超时（10 秒）——大概率是 (a+)+ 这种灾难性回溯，"
                    + "把模式写具体一些"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("匹配失败：" + ex.Message));
            }
        }

        private static ToolResult DoMatch(Regex re, string input, string source, int context, int maxHits)
        {
            var lines = input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var hits = new List<string>();
            int total = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                if (!re.IsMatch(lines[i])) continue;
                total++;
                if (hits.Count >= maxHits) continue;

                var sb = new StringBuilder();
                int from = Math.Max(0, i - context);
                int to = Math.Min(lines.Length - 1, i + context);
                for (int j = from; j <= to; j++)
                {
                    string mark = j == i ? ">" : " ";
                    sb.Append(mark).Append(' ').Append((j + 1).ToString().PadLeft(5)).Append(" │ ")
                      .Append(Clip(lines[j], 240)).Append('\n');
                }
                if (context > 0) sb.Append('\n');
                hits.Add(sb.ToString());
            }

            var outSb = new StringBuilder();
            outSb.Append(source).Append(" · 共 ").Append(lines.Length).Append(" 行 · ")
                 .Append(total).Append(" 行命中");
            if (total > hits.Count) outSb.Append("（只显示前 ").Append(hits.Count).Append(" 条）");
            outSb.Append("\n\n");
            if (total == 0) outSb.Append("没有匹配。");
            else foreach (var h in hits) outSb.Append(h);

            return new ToolResult
            {
                Ok = true,
                Output = outSb.ToString(),
                Summary = "match · " + total + " 行命中",
            };
        }

        private static ToolResult DoExtract(Regex re, string input, string source, int maxHits, bool unique)
        {
            var hits = new List<string>();
            var seen = unique ? new HashSet<string>(StringComparer.Ordinal) : null;
            int total = 0;

            foreach (Match m in re.Matches(input))
            {
                // 有命名组 f 就取它（内置 flag 模式用这个把括号内容剥出来）
                string v = m.Groups["f"].Success ? m.Groups["f"].Value : m.Value;
                total++;
                if (hits.Count >= maxHits) continue;
                if (seen != null && !seen.Add(v)) continue;
                hits.Add(v);
            }

            var sb = new StringBuilder();
            sb.Append(source).Append(" · ").Append(total).Append(" 个匹配");
            if (unique) sb.Append("（去重后 ").Append(hits.Count).Append(" 个）");
            if (total > hits.Count && !unique) sb.Append("（只显示前 ").Append(hits.Count).Append(" 个）");
            sb.Append("\n\n");
            if (hits.Count == 0) sb.Append("没有匹配。");
            else foreach (var h in hits) sb.Append(h).Append('\n');

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "extract · " + (unique ? hits.Count + " 个唯一" : total + " 个匹配"),
            };
        }

        private static ToolResult DoReplace(Regex re, string input, string replacement)
        {
            int count = 0;
            string result = re.Replace(input, m => { count++; return m.Result(replacement); });
            return new ToolResult
            {
                Ok = true,
                Output = result,
                Summary = "replace · 替换了 " + count + " 处",
            };
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
