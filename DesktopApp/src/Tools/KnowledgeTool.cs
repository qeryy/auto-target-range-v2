// 灵枢桌面版 —— 工具：知识检索
//
// 对应 web 端的 `kb_search`：agent 解题时按关键词查技能库，把命中的技能正文
// 拿进上下文当参考。
//
// 这里同时查两个来源：
//   · **技能库**（288 条，打包在 exe 里的 skills.json）
//   · **经验库**（用户自己跑任务提炼出来的洞察）
//
// 两个来源性质不同：技能是「通用方法论」，经验是「这台机器上踩过的坑」。
// 混在一次检索里给模型，它自己会判断该信哪个。
//
// 打分很简单：关键词在 description + body 里命中几次就是几分。
// 不引全文索引——技能库只有 288 条，线性扫一遍比维护索引便宜。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class KnowledgeTool : ITool
    {

        public string Name { get { return "kb_search"; } }

        public string Description
        {
            get
            {
                return "按关键词检索知识库：安全技能的方法论 + 以往任务提炼的经验。"
                     + "遇到不认识的技术名词、不确定的手法、想确认某个信号的含义时用它。"
                     + "返回的是参考资料，不是本题结论。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""检索词，可以是技术名词或场景描述"" },
    ""limit"": { ""type"": ""integer"", ""description"": ""每个来源最多返回几条，默认 2"" }
  },
  ""required"": [""query""]
}";
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string query = Arg.Str(args, "query");
            if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(ToolResult.Fail("query 不能为空"));

            int limit = Arg.Int(args, "limit", 2);
            if (limit <= 0 || limit > 6) limit = 2;

            var words = query.ToLowerInvariant().Split(
                new[] { ' ', '\t', '\n', ',', '，', '/', '、' },
                StringSplitOptions.RemoveEmptyEntries);

            var sb = new StringBuilder();
            int totalHits = 0;

            // ---- 技能库 ----
            var skills = ScoreSkills(Knowledge.SkillLibrary.All(), words);
            if (skills.Count > 0)
            {
                sb.Append("=== 技能库命中 ===\n");
                int n = 0;
                foreach (var sk in skills)
                {
                    if (n >= limit) break;
                    n++;
                    totalHits++;
                    sb.Append('[').Append(sk.Id).Append("] [").Append(sk.Domain).Append("] ")
                      .Append(sk.Description).Append('\n');
                    // 正文只给前 1500 字：模型需要的是要点，不是整篇
                    string body = sk.Body ?? "";
                    if (body.Length > 1500) body = body.Substring(0, 1500) + "\n…（正文截断）";
                    sb.Append(body).Append("\n---\n");
                }
            }

            // ---- 经验库 ----
            try
            {
                var expStore = new Knowledge.ExperienceStore(Core.AppPaths.ExperienceFile);
                var expHits = new List<Core.Experience>();
                var scored = new List<KeyValuePair<int, Core.Experience>>();
                foreach (var e in expStore.All())
                {
                    string hay = ((e.Insight ?? "") + " " + (e.Tags ?? "")).ToLowerInvariant();
                    int score = 0;
                    foreach (var w in words)
                    {
                        if (w.Length >= 2 && hay.Contains(w)) score++;
                    }
                    if (score > 0) scored.Add(new KeyValuePair<int, Core.Experience>(score, e));
                }
                scored.Sort((a, b) => b.Key.CompareTo(a.Key));
                foreach (var kv in scored)
                {
                    if (expHits.Count >= limit) break;
                    expHits.Add(kv.Value);
                }

                if (expHits.Count > 0)
                {
                    sb.Append("\n=== 经验库命中（以往任务提炼）===\n");
                    int n = 0;
                    foreach (var e in expHits)
                    {
                        n++;
                        totalHits++;
                        sb.Append(n).Append(". ").Append(e.Insight);
                        if (!string.IsNullOrEmpty(e.Tags)) sb.Append("　[").Append(e.Tags).Append(']');
                        sb.Append('\n');
                    }
                }
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("kb_search 查经验库失败: " + ex.Message);
            }

            if (totalHits == 0)
            {
                return Task.FromResult(new ToolResult
                {
                    Ok = true,
                    Output = "知识库没有命中「" + query + "」。按你掌握的信息自行分析。",
                    Summary = "检索「" + Short(query, 30) + "」无命中",
                });
            }

            sb.Append("\n（以上是参考资料，不是本次任务的结论；与当前观测冲突时以后者为准。）");
            return Task.FromResult(new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "检索「" + Short(query, 30) + "」命中 " + totalHits + " 条",
            });
        }

        /// <summary>按关键词命中数给技能打分。</summary>
        private static List<Knowledge.SkillEntry> ScoreSkills(List<Knowledge.SkillEntry> all, string[] words)
        {
            var scored = new List<KeyValuePair<int, Knowledge.SkillEntry>>();
            foreach (var sk in all)
            {
                string hay = ((sk.Id ?? "") + " " + (sk.Domain ?? "") + " "
                            + (sk.Description ?? "") + " " + Short(sk.Body ?? "", 8000))
                            .ToLowerInvariant();
                int score = 0;
                foreach (var w in words)
                {
                    if (w.Length < 2) continue;
                    // 命中一次加一分，同一词多出现也加分（最多 +3）
                    int idx = 0, cnt = 0;
                    while (cnt < 3)
                    {
                        int at = hay.IndexOf(w, idx, StringComparison.Ordinal);
                        if (at < 0) break;
                        cnt++;
                        idx = at + w.Length;
                    }
                    score += cnt;
                }
                if (score > 0) scored.Add(new KeyValuePair<int, Knowledge.SkillEntry>(score, sk));
            }
            scored.Sort((a, b) => b.Key.CompareTo(a.Key));
            var result = new List<Knowledge.SkillEntry>();
            foreach (var kv in scored) result.Add(kv.Value);
            return result;
        }

        private static string Short(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
