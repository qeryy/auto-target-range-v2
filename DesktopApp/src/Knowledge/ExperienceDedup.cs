// 灵枢桌面版 —— 经验查重（纯函数）
//
// ## 为什么要做
//
// 「经验」是这套系统越用越聪明的地方：一场任务跑完提炼几条洞察，下次同类场景
// 召回进提示词。但提炼是模型生成的 —— **同一件事可能被提炼成三条说法不同的条目**。
// 经验库越堆越大，召回时挤占上下文，实际有效信号反而变少。
//
// web 版有「相似条目合并建议」（`resources.css` 里的 `.exp-merge-*`）。
// 桌面版一直没有。这里补上核心的**相似度计算与分组**，UI 只负责展示。
//
// ## 为什么用字符二元组（bigram）而不是分词
//
// 中文没有空格，要分词得引词库或第三方库（本项目的第三方依赖是零）。
// 而「字符二元组 Jaccard」不需要分词器：
//
//   "SQL 注入用 union 绕过"  →  {sq, ql, l注, 注入, 入用, ...}
//
// 对中英混排一样有效，对措辞微调也够宽容（"SQL注入" 和 "sql 注入" 共享大部分二元组）。
//
// 代价：**字序颠倒时相似度也高**（"绕过 WAF" vs "WAF 绕过"），
// 但那种情况恰恰是该合并的，不算误判。
//
// ## 分组用并查集
//
// A~B、B~C 相似但 A 和 C 不直接相似时，三条应该在同一组 —— 所以做**传递闭包**，
// 而不是简单两两配对。用并查集实现，O(n²) 次比较 + 近线性合并。
//
// 经验库通常几十到几百条，O(n²) 完全够用（1000 条 = 50 万次比较，纯字符串操作，
// 百毫秒级）。真到几万条再换索引方案。

using System;
using System.Collections.Generic;

namespace SpiritScout.Knowledge
{
    /// <summary>一组被认为可能重复的经验。</summary>
    public sealed class SimilarGroup
    {
        /// <summary>组内条目（按 CreatedAt 升序，最旧的在前）。</summary>
        public List<Core.Experience> Items { get; private set; }

        /// <summary>组内**任意两条之间**的最低相似度（保守值，不是平均）。</summary>
        public double MinScore { get; set; }

        /// <summary>组内最高相似度（用来说明「这两条几乎一样」）。</summary>
        public double MaxScore { get; set; }

        public SimilarGroup()
        {
            Items = new List<Core.Experience>();
        }

        /// <summary>建议保留的那条：组内最旧的（先提炼的通常更完整）。</summary>
        public Core.Experience Keep
        {
            get { return Items.Count > 0 ? Items[0] : null; }
        }

        /// <summary>建议删除的那些：除保留项之外的全部。</summary>
        public List<Core.Experience> Duplicates
        {
            get
            {
                var list = new List<Core.Experience>();
                for (int i = 1; i < Items.Count; i++) list.Add(Items[i]);
                return list;
            }
        }
    }

    public static class ExperienceDedup
    {
        /// <summary>默认相似度阈值。0.62 是在「措辞微调」和「不同话题」之间取的折中。</summary>
        public const double DefaultThreshold = 0.62;

        /// <summary>
        /// 两条经验的相似度，0..1。用 `Insight + Tags` 的字符二元组 Jaccard。
        /// 任一条为空 → 0（空内容不该和谁相似）。
        /// </summary>
        public static double Similarity(Core.Experience a, Core.Experience b)
        {
            if (a == null || b == null) return 0;
            string ta = Normalize((a.Insight ?? "") + " " + (a.Tags ?? ""));
            string tb = Normalize((b.Insight ?? "") + " " + (b.Tags ?? ""));
            if (ta.Length < 2 || tb.Length < 2) return 0;

            var sa = Bigrams(ta);
            var sb = Bigrams(tb);
            if (sa.Count == 0 || sb.Count == 0) return 0;

            int inter = 0;
            foreach (var g in sa) if (sb.Contains(g)) inter++;
            int union = sa.Count + sb.Count - inter;
            if (union <= 0) return 0;
            return (double)inter / union;
        }

        /// <summary>
        /// 找出所有相似组（**至少 2 条**才算）。
        ///
        /// 用并查集做传递闭包：A~B、B~C 会让三条归到同一组。
        /// 返回的组按 `Items.Count` 降序（重复最多的排前面）。
        /// </summary>
        public static List<SimilarGroup> FindSimilar(
            List<Core.Experience> items, double threshold)
        {
            var groups = new List<SimilarGroup>();
            if (items == null || items.Count < 2) return groups;
            if (threshold <= 0) threshold = DefaultThreshold;

            int n = items.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            Func<int, int> find = null;
            find = delegate(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            };

            // 只做并查集合并，不在这里攒分数 —— 分数在分组之后按组内两两重算，
            // 那样才是「组内最低相似度」的准确值（合并过程中算不出来）。
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (Similarity(items[i], items[j]) < threshold) continue;
                    int ri = find(i), rj = find(j);
                    if (ri != rj) parent[rj] = ri;
                }
            }

            // 按根聚合成组
            var map = new Dictionary<int, SimilarGroup>();
            var order = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                SimilarGroup g;
                if (!map.TryGetValue(r, out g))
                {
                    g = new SimilarGroup();
                    map[r] = g;
                    order.Add(r);
                }
                g.Items.Add(items[i]);
            }

            foreach (var r in order)
            {
                var g = map[r];
                if (g.Items.Count < 2) continue;      // 单条不算重复

                // 组内按创建时间升序，最旧的作为「建议保留」
                g.Items.Sort(delegate(Core.Experience x, Core.Experience y)
                {
                    return x.CreatedAt.CompareTo(y.CreatedAt);
                });

                g.MinScore = 1.0;
                g.MaxScore = 0.0;
                for (int i = 0; i < g.Items.Count; i++)
                {
                    for (int j = i + 1; j < g.Items.Count; j++)
                    {
                        double s = Similarity(g.Items[i], g.Items[j]);
                        if (s < g.MinScore) g.MinScore = s;
                        if (s > g.MaxScore) g.MaxScore = s;
                    }
                }
                groups.Add(g);
            }

            groups.Sort(delegate(SimilarGroup x, SimilarGroup y)
            {
                return y.Items.Count.CompareTo(x.Items.Count);
            });
            return groups;
        }

        /// <summary>
        /// 归一化：只保留字母、数字、汉字，其余（标点/空白/符号）一律当分隔。
        /// 小写化。这样 "SQL 注入" 和 "sql注入" 归一化后相同。
        /// </summary>
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                // 中日韩统一表意文字本来就在 Letter 范围内，不用单独判断
            }
            return sb.ToString();
        }

        /// <summary>字符二元组集合。长度不足 2 时返回空集。</summary>
        private static HashSet<string> Bigrams(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < s.Length; i++)
                set.Add(s.Substring(i, 2));
            return set;
        }
    }
}
