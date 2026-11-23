// 灵枢桌面版 —— 会话导出
//
// 把一个会话导成 Markdown 报告。分两部分：
//
//   **概览部分**（给不想读全部过程的人看）
//     · 基本信息：目标、模式、状态、耗时、token、消息数
//     · 工具调用统计：各工具调用几次、成功几次
//     · 工具调用时间线：按顺序列出每一步做了什么、结果如何
//     · 发现的 flag
//     · 关键结论（最后一条助手回复）
//
//   **完整过程**（给要复核的人看）
//     · 逐条消息原文，工具输出保留前 4000 字
//
// 为什么要分两部分：审阅一份几十轮的解题记录时，前面那半页就够判断
// 「这活干成了没、走的什么路子」；只有要复核细节时才往下翻。
//
// web 版的 report agent 靠「黑板」里的结构化数据（challenge / asset / severity）
// 排正式报告。桌面版没有那些模型，所以这里从**消息流本身**提取结构——
// 工具调用天然就是一条时间线，够用。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpiritScout.Session
{
    public static class SessionExporter
    {
        /// <summary>单条工具输出最多写多少字（超出标注截断）。</summary>
        private const int ToolClip = 4000;

        /// <summary>概览里时间线最多列多少条（太长就折叠）。</summary>
        private const int TimelineCap = 40;

        /// <summary>一条工具调用的记录。</summary>
        private sealed class ToolStep
        {
            public int Index;
            public string Name = "";
            public string Summary = "";
            public bool Ok;
            public DateTime At;
        }

        public static string ToMarkdown(Core.SessionFile sf)
        {
            if (sf == null || sf.Session == null) return "";
            var s = sf.Session;
            var sb = new StringBuilder();

            var steps = CollectToolSteps(sf);
            var flags = ExtractFlags(sf);

            sb.Append("# ").Append(string.IsNullOrEmpty(s.Title) ? s.Target : s.Title).Append("\n\n");

            // ---------- 概览 ----------
            sb.Append("## 概览\n\n");
            sb.Append("| 项 | 值 |\n|---|---|\n");
            sb.Append("| 目标 | ").Append(Esc(s.Target)).Append(" |\n");
            sb.Append("| 模式 | ").Append(Core.RunModeText.Label(s.Mode)).Append(" |\n");
            sb.Append("| 状态 | ").Append(Core.PhaseText.Label(s.Phase)).Append(" |\n");
            sb.Append("| 创建 | ").Append(s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")).Append(" |\n");
            sb.Append("| 更新 | ").Append(s.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")).Append(" |\n");

            // 耗时：从创建到最后一条消息
            var span = Span(sf);
            if (span.HasValue) sb.Append("| 耗时 | ").Append(Core.Text.Duration(span.Value)).Append(" |\n");

            sb.Append("| 消息 | ").Append(sf.Messages.Count).Append(" 条 |\n");
            sb.Append("| 工具调用 | ").Append(steps.Count).Append(" 次").Append(StepsStat(steps)).Append(" |\n");
            sb.Append("| Token | ").Append(s.Tokens.ToString("N0")).Append(" |\n");
            sb.Append("| 会话 ID | `").Append(s.Id).Append("` |\n\n");

            if (!string.IsNullOrWhiteSpace(s.Mission))
            {
                sb.Append("## 任务描述\n\n").Append(s.Mission).Append("\n\n");
            }

            // ---------- Flag ----------
            if (flags.Count > 0)
            {
                sb.Append("## 发现的 Flag\n\n");
                foreach (var f in flags) sb.Append("- `").Append(f).Append("`\n");
                sb.Append('\n');
            }

            // ---------- 工具调用时间线 ----------
            if (steps.Count > 0)
            {
                sb.Append("## 工具调用时间线\n\n");
                sb.Append("| # | 工具 | 做了什么 | 结果 |\n|---|---|---|---|\n");
                int n = 0;
                foreach (var st in steps)
                {
                    if (++n > TimelineCap)
                    {
                        sb.Append("| … | （还有 ").Append(steps.Count - TimelineCap).Append(" 次，见下面的完整过程） | | |\n");
                        break;
                    }
                    sb.Append("| ").Append(st.Index).Append(" | `").Append(st.Name).Append("` | ")
                      .Append(Esc(Core.Text.OneLine(st.Summary, 80))).Append(" | ")
                      .Append(st.Ok ? "✓" : "✗").Append(" |\n");
                }
                sb.Append('\n');
            }

            // ---------- 关键结论 ----------
            string conclusion = LastAssistantText(sf);
            if (!string.IsNullOrWhiteSpace(conclusion))
            {
                sb.Append("## 关键结论\n\n");
                sb.Append("> 最后一条助手回复：\n\n");
                sb.Append(conclusion.Trim()).Append("\n\n");
            }

            // ---------- 完整过程 ----------
            sb.Append("## 完整过程\n\n");
            foreach (var m in sf.Messages)
            {
                string role = m.Role;
                if (role == "user")
                {
                    sb.Append("### 用户\n\n").Append(m.Content).Append("\n\n");
                }
                else if (role == "assistant")
                {
                    sb.Append("### 助手\n\n").Append(m.Content).Append("\n\n");
                }
                else if (role == "tool")
                {
                    sb.Append("### 工具 `").Append(m.Name).Append("`\n\n");
                    string body = m.Content ?? "";
                    if (body.Length > ToolClip)
                    {
                        sb.Append("```text\n").Append(body.Substring(0, ToolClip))
                          .Append("\n…（截断，原文 ").Append(body.Length).Append(" 字符）\n```\n\n");
                    }
                    else
                    {
                        sb.Append("```text\n").Append(body).Append("\n```\n\n");
                    }
                }
                else
                {
                    sb.Append("> ").Append(m.Content).Append("\n\n");
                }
            }

            sb.Append("---\n\n");
            sb.Append("*由灵枢 · SpiritScout 桌面版导出 · ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("*\n");

            // 导出前整体脱敏：报告是**给别人看的**，而模型可能在命令或输出里带了凭据。
            // 这一步在最后做，前面按原文组织内容不受影响。
            return Core.Redact.Clean(sb.ToString());
        }

        /// <summary>把所有工具调用按顺序收集起来。</summary>
        private static List<ToolStep> CollectToolSteps(Core.SessionFile sf)
        {
            var list = new List<ToolStep>();
            if (sf == null || sf.Messages == null) return list;

            int idx = 0;
            foreach (var m in sf.Messages)
            {
                if (m.Role != "tool") continue;
                idx++;

                string content = m.Content ?? "";
                // 工具消息的格式是 "[name] summary"，提取 summary
                string summary = content;
                if (summary.StartsWith("["))
                {
                    int close = summary.IndexOf(']');
                    if (close > 0) summary = summary.Substring(close + 1).Trim();
                }

                list.Add(new ToolStep
                {
                    Index = idx,
                    Name = string.IsNullOrEmpty(m.Name) ? "?" : m.Name,
                    Summary = summary,
                    // 约定：成功的工具输出不会以「失败」「错误」开头（ToolResult.Fail 的约定）
                    Ok = !(summary.StartsWith("失败") || summary.StartsWith("错误")
                        || summary.IndexOf("exit 1", StringComparison.OrdinalIgnoreCase) >= 0),
                    At = m.CreatedAt,
                });
            }
            return list;
        }

        /// <summary>「共 12 次（成功 11 / 失败 1）」这样的统计尾巴。</summary>
        private static string StepsStat(List<ToolStep> steps)
        {
            if (steps.Count == 0) return "";
            int ok = 0;
            foreach (var s in steps) if (s.Ok) ok++;
            int bad = steps.Count - ok;
            if (bad == 0) return "（全部成功）";
            return "（成功 " + ok + " / 失败 " + bad + "）";
        }

        /// <summary>整个会话的时间跨度（创建 → 最后一条消息）。</summary>
        private static TimeSpan? Span(Core.SessionFile sf)
        {
            if (sf == null || sf.Session == null) return null;
            DateTime start = sf.Session.CreatedAt;
            DateTime end = sf.Session.UpdatedAt;
            if (sf.Messages != null && sf.Messages.Count > 0)
            {
                var last = sf.Messages[sf.Messages.Count - 1];
                if (last.CreatedAt > end) end = last.CreatedAt;
            }
            if (end <= start) return null;
            return end - start;
        }


        /// <summary>最后一条有内容的助手回复（当作「结论」）。</summary>
        private static string LastAssistantText(Core.SessionFile sf)
        {
            if (sf == null || sf.Messages == null) return "";
            for (int i = sf.Messages.Count - 1; i >= 0; i--)
            {
                var m = sf.Messages[i];
                if (m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))
                {
                    // 注意：这里**不能**用 Clip —— 它会把换行换成空格，
                    // 结论里的列表和分段就塌成一行了。只按长度截断，保留结构。
                    string t = m.Content.Trim();
                    if (t.Length > 800)
                        t = t.Substring(0, 800) + "\n\n…（完整回复见下方「完整过程」）";
                    return t;
                }
            }
            return "";
        }

        /// <summary>从消息里提取 flag（与会话页同一套规则）。</summary>
        /// <summary>
        /// 把「flag 格式提示」转成匹配用的正则。
        ///
        /// 用户填的是 `flag{...}` / `flag{xxx}` / `flag{????}` 这类**模板**，
        /// 占位符表示「这里是一段内容」。直接当正则会去匹配字面量 `flag{...}`。
        ///
        /// 认得的占位符：`...` `xxx` `XXX` `???` `***`
        /// 认不出的就整条转义当字面量（宁可提不到，也别乱匹配）。
        /// </summary>
        private static string FlagFormatToRegex(string format)
        {
            if (string.IsNullOrWhiteSpace(format)) return @"(?:flag|FLAG|ctf|CTF)\{[^}]{1,200}\}";
            string f = format.Trim();
            // 占位符 → 通配
            f = f.Replace("...", "\u0001").Replace("???", "\u0001")
                 .Replace("xxx", "\u0001").Replace("XXX", "\u0001")
                 .Replace("***", "\u0001");
            // 转义 { } 和正则元字符
            var sb = new System.Text.StringBuilder();
            foreach (char c in f)
            {
                if (c == '\u0001') { sb.Append("[^}]{1,200}"); continue; }
                if ("\\.$^+()[]{}|".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            string rx = sb.ToString();
            // 一个占位符都没认出来（比如用户填的是 "flag"）→ 补一个默认的后缀匹配
            if (rx.IndexOf("[^}]", StringComparison.Ordinal) < 0)
                rx = rx + @"\{[^}]{1,200}\}";
            return rx;
        }

        public static List<string> ExtractFlags(Core.SessionFile sf)
        {
            var result = new List<string>();
            if (sf == null) return result;

            // `FlagFormat` 是**给人看的格式提示**（如 `flag{...}`），不是正则。
            // 直接当正则会去匹配字面量 `flag{...}`，实际一个 flag 都提不到
            // （165 轮测出来）。这里把它转成正则：把 ... / xxx / ??? 换成
            // 「1-200 个非 } 字符」，并把 { } 转义。
            string pattern = sf.Session != null && !string.IsNullOrWhiteSpace(sf.Session.FlagFormat)
                ? FlagFormatToRegex(sf.Session.FlagFormat)
                : @"(?:flag|FLAG|ctf|CTF)\{[^}]{1,200}\}";

            Regex re;
            try { re = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch { re = new Regex(@"(?:flag|FLAG|ctf|CTF)\{[^}]{1,200}\}"); }

            var seen = new HashSet<string>();
            foreach (var m in sf.Messages)
            {
                foreach (Match hit in re.Matches(m.Content ?? ""))
                {
                    string v = hit.Value.Trim();
                    if (seen.Add(v)) result.Add(v);
                }
            }
            return result;
        }

        /// <summary>导出到文件，返回实际写入的路径。</summary>
        public static string ExportToFile(Core.SessionFile sf, string path)
        {
            string md = ToMarkdown(sf);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, md, new UTF8Encoding(false));
            return path;
        }

        /// <summary>建议的文件名（去掉路径非法字符）。</summary>
        public static string SuggestFileName(Core.SessionFile sf)
        {
            string name = sf != null && sf.Session != null
                ? (string.IsNullOrEmpty(sf.Session.Title) ? sf.Session.Target : sf.Session.Title)
                : "session";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 0x7F) sb.Append(c);
                else sb.Append('_');
            }
            string safe = sb.ToString();
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            return safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".md";
        }


        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
