// 灵枢桌面版 —— 经验库与提炼
//
// 「经验」是这套系统自我进化的载体：一场任务结束，从战报里提炼几条**可复用的
// 洞察**（不是解题步骤），下次遇到同类场景时召回进提示词。
//
// 提炼纪律（与 web 端一致）：
//   · 洞察必须脱离本题仍然成立 —— 单题发现要抽象成同类场景的信号；
//   * 抽象不出来的（具体路径、参数、口令、端口、命令、flag 原文）一律不写；
//   · 不写操作序列与处方，写「这类场景下什么信号意味着什么」。
//
// 存储：experience.json（整个库一个文件，条数在几千以内）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpiritScout.Llm;

namespace SpiritScout.Knowledge
{
    /// <summary>经验库读写。</summary>
    public sealed class ExperienceStore
    {
        private readonly string _path;

        public ExperienceStore(string path) { _path = path; }

        public List<Core.Experience> All()
        {
            var list = Core.JsonStore.Read<List<Core.Experience>>(_path, null);
            return list ?? new List<Core.Experience>();
        }

        /// <summary>
        /// 「读-改-写」整体加跨进程锁。
        ///
        /// 实测：两个窗口各 Add 20 条，**都报成功**，最终只剩 17 条（丢 23 条）。
        /// 因为 Add 是「读全部 → 插一条 → 写全部」，两个进程各读各的、后写的覆盖前写的。
        ///
        /// 注意读也要在锁里 —— 光让 Write 加锁只能保证文件不坏，救不了丢数据。
        /// </summary>
        private IDisposable LockFile()
        {
            return Core.CrossProcessLock.Acquire(_path);
        }

        /// <summary>绕过任何缓存（当前实现每次都读盘，留着这个方法是为了语义清晰）。</summary>
        private List<Core.Experience> Reload() { return All(); }

        public void Save(List<Core.Experience> items)
        {
            Core.JsonStore.Write(_path, items ?? new List<Core.Experience>());
        }

        public void Add(Core.Experience e)
        {
            if (e == null) return;
            AddRange(new[] { e });
        }

        /// <summary>
        /// 批量加入，只读写一次文件。
        ///
        /// `Add` 是「读全部 → 插一条 → 写全部」。从会话提炼一次往往产出 3-6 条，
        /// 逐条调 Add 就是 3-6 次完整读写——实测 2 万条经验（4MB）时
        /// **单次 Add 401ms**，提炼一次要两秒。
        ///
        /// 这里合成一次：读一次、插完、写一次。
        /// </summary>
        public void AddRange(IEnumerable<Core.Experience> items)
        {
            if (items == null) return;
            var batch = new List<Core.Experience>();
            foreach (var e in items)
            {
                if (e == null) continue;
                if (string.IsNullOrEmpty(e.Id))
                    e.Id = "exp-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-"
                         + Guid.NewGuid().ToString("N").Substring(0, 4);
                batch.Add(e);
            }
            if (batch.Count == 0) return;

            using (LockFile())
            {
                var all = Reload();   // 锁内重读，不能用锁外的快照
                all.InsertRange(0, batch);
                Save(all);
            }
        }

        public void Delete(string id)
        {
            using (LockFile())
            {
                var all = Reload();
                all.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                Save(all);
            }
        }

        /// <summary>按模式筛选（空 = 全部）。</summary>
        public List<Core.Experience> ByMode(string mode)
        {
            var all = All();
            if (string.IsNullOrEmpty(mode)) return all;
            // 按**解析后的枚举**比较，而不是比字符串。
            //
            // 原因有两个：
            //   1. 经验可以从 JSON 导入（`ExperiencePage` 的「导入」接受任意文件），
            //      手写 `"Mode": "CTF "` 也能进得来 —— 比字符串就永远匹配不上，
            //      那条经验在按模式筛选时会凭空消失。
            //   2. 空 Mode 原来**不会被** `ByMode("ctf")` 选中，
            //      但 `CountByMode()` 又把空 Mode 算成 ctf —— 两处口径不一致。
            //      用 Parse 统一之后，空 Mode 等价于 ctf，两边对上了。
            var want = Core.RunModeText.Parse(mode);
            return all.Where(x => Core.RunModeText.Parse(x.Mode) == want).ToList();
        }

        /// <summary>
        /// 召回：按关键词挑几条最相关的经验喂给 agent。
        /// 打分只看标签与正文的关键词命中数，简单但够用——经验条数本来就不多。
        /// </summary>
        public List<Core.Experience> Recall(string query, string mode, int limit)
        {
            var pool = ByMode(mode);
            if (pool.Count == 0) return pool;
            if (string.IsNullOrWhiteSpace(query)) return pool.Take(limit).ToList();

            var words = query.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', ',', '，', '。', ';', '；' },
                       StringSplitOptions.RemoveEmptyEntries);

            var scored = new List<KeyValuePair<int, Core.Experience>>();
            foreach (var e in pool)
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
            var result = new List<Core.Experience>();
            foreach (var kv in scored.Take(limit)) result.Add(kv.Value);
            // 不够就用最近的补满
            foreach (var e in pool)
            {
                if (result.Count >= limit) break;
                if (!result.Contains(e)) result.Add(e);
            }
            return result;
        }

        /// <summary>统计各模式条数。</summary>
        public Dictionary<string, int> CountByMode()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in All())
            {
                string m = string.IsNullOrEmpty(e.Mode) ? "ctf" : e.Mode;
                counts[m] = (counts.ContainsKey(m) ? counts[m] : 0) + 1;
            }
            return counts;
        }
    }

    /// <summary>从会话提炼经验。</summary>
    public sealed class ExperienceDistiller
    {
        private readonly Core.SessionStore _sessions;
        private readonly ModelRegistry _registry;
        private readonly ExperienceStore _store;

        /// <summary>喂给模型的战报最多多少字符（太长会挤掉提示词）。</summary>
        public int MaxDigestChars { get; set; }

        public ExperienceDistiller(Core.SessionStore sessions, ModelRegistry registry,
            ExperienceStore store)
        {
            _sessions = sessions;
            _registry = registry;
            _store = store;
            MaxDigestChars = 12000;
        }

        /// <summary>
        /// 从会话提炼。返回新增的条数；失败返回 -1 并把原因写进 message。
        /// </summary>
        public async Task<KeyValuePair<int, string>> DistillAsync(string sessionId,
            string modelName, string mode)
        {
            var sf = _sessions.Load(sessionId);
            if (sf == null) return new KeyValuePair<int, string>(-1, "会话不存在");
            if (sf.Messages.Count < 4)
                return new KeyValuePair<int, string>(-1, "消息太少（不到 4 条），提炼不出东西");

            string digest = BuildDigest(sf);
            if (digest.Length < 200)
                return new KeyValuePair<int, string>(-1, "战报内容太短，提炼不出东西");

            string prompt =
                "你是灵枢。一场安全测试刚结束。\n"
                + "从以下战报中提炼 3~5 条可复用的实战洞察——不是解题步骤，"
                + "而是「这类场景下我学到了什么」：目标/平台特性、手法适用条件、"
                + "容易踩的坑、判读信号、中断或失败的教训。\n\n"
                + "纪律：\n"
                + "① 洞察必须脱离本题仍然成立——单题发现要抽象成同类场景的信号与事实；"
                + "抽象不出来的（具体路径、参数、口令、端口、命令、flag 原文）一律不写；\n"
                + "② 不写操作序列与处方（「先…再…」「应优先…」），写「什么信号意味着什么」；\n"
                + "③ 每条一句话，20~80 字，能独立读懂。\n\n"
                + "只输出 JSON 数组，形如：\n"
                + "[{\"insight\":\"……\",\"tags\":[\"web\",\"认证\"]},{\"insight\":\"……\",\"tags\":[\"reverse\"]}]\n\n"
                + "=== 战报 ===\n" + digest;

            var reply = await _registry.ChatAsync(modelName, new ChatRequest
            {
                Messages = new List<ChatMessage> { new ChatMessage("user", prompt) },
                Temperature = 0.3,
                MaxTokens = 1200,
            }).ConfigureAwait(false);

            if (!reply.Ok)
                return new KeyValuePair<int, string>(-1, "模型调用失败：" + Core.Text.OneLine(reply.Error));

            var items = ParseInsights(reply.Text);
            if (items.Count == 0)
                return new KeyValuePair<int, string>(-1, "模型没有返回可解析的洞察：" + Core.Text.OneLine(reply.Text));

            // 先攒着，最后一次写盘。
            //
            // 原来是在循环里逐条 `_store.Add(...)`——而 Add 是「读全部 → 插一条 → 写全部」。
            // 提炼一次产出 3~5 条就是 3~5 次完整读写：实测 2 万条经验（4MB）时
            // 单次 Add 401ms，提炼一次要两秒。
            var fresh = new List<Core.Experience>();
            foreach (var it in items)
            {
                // 去重：与已有条目正文高度重合就跳过
                if (IsDuplicate(it.Key)) continue;
                fresh.Add(new Core.Experience
                {
                    Id = "",
                    Insight = it.Key,
                    Tags = string.Join(",", it.Value),
                    Mode = string.IsNullOrEmpty(mode) ? "ctf" : mode,
                    SourceSession = sessionId,
                    CreatedAt = DateTime.Now,
                });
            }
            if (fresh.Count > 0) _store.AddRange(fresh);
            int added = fresh.Count;
            return new KeyValuePair<int, string>(added, added + " 条新洞察");
        }

        // ---------- 内部 ----------

        private string BuildDigest(Core.SessionFile sf)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("目标：").Append(sf.Session.Target).Append('\n');
            sb.Append("模式：").Append(Core.RunModeText.Label(sf.Session.Mode)).Append('\n');
            if (!string.IsNullOrEmpty(sf.Session.Mission))
                sb.Append("任务：").Append(sf.Session.Mission).Append('\n');
            sb.Append('\n');

            // 只取每条消息的前若干字，保留结构而不被细节淹没
            foreach (var m in sf.Messages)
            {
                if (m.Role == "system") continue;
                string who = m.Role == "user" ? "用户" : (m.Role == "tool" ? "工具" : "助手");
                string text = m.Content ?? "";
                if (text.Length > 600) text = text.Substring(0, 600) + "…";
                sb.Append('[').Append(who).Append("] ").Append(text.Replace("\n", " ")).Append('\n');
                if (sb.Length > MaxDigestChars) break;
            }
            return sb.ToString();
        }

        private List<KeyValuePair<string, List<string>>> ParseInsights(string text)
        {
            var result = new List<KeyValuePair<string, List<string>>>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            // 模型可能带 ```json 围栏，先剥掉
            int start = text.IndexOf('[');
            int end = text.LastIndexOf(']');
            if (start < 0 || end <= start) return result;
            string json = text.Substring(start, end - start + 1);

            var arr = Core.Json.AsList(Core.Json.Parse(json));
            if (arr == null) return result;

            foreach (var item in arr)
            {
                string insight;
                var tags = new List<string>();

                var d = Core.Json.AsDict(item);
                if (d != null)
                {
                    insight = Core.Json.Str(d, "insight");
                    var tagArr = Core.Json.Array(d, "tags");
                    if (tagArr != null)
                    {
                        foreach (var t in tagArr)
                        {
                            if (t != null) tags.Add(t.ToString());
                        }
                    }
                }
                else
                {
                    insight = item == null ? "" : item.ToString();
                }

                insight = (insight ?? "").Trim();
                if (insight.Length < 8) continue;
                result.Add(new KeyValuePair<string, List<string>>(insight, tags));
            }
            return result;
        }

        private bool IsDuplicate(string insight)
        {
            if (string.IsNullOrEmpty(insight)) return true;
            string a = insight.ToLowerInvariant();
            foreach (var e in _store.All())
            {
                string b = (e.Insight ?? "").ToLowerInvariant();
                if (b.Length == 0) continue;
                if (b == a) return true;
                // 长度接近且前 40% 相同，视为重复
                if (Math.Abs(a.Length - b.Length) < 10)
                {
                    int n = Math.Min(a.Length, b.Length) * 2 / 5;
                    if (n > 6 && a.Substring(0, n) == b.Substring(0, n)) return true;
                }
            }
            return false;
        }

    }
}
