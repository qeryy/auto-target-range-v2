// 灵枢桌面版 —— 经验召回（闭环的最后一环）
//
// 前面已经能把经验提炼进库、能在页面上看到。但**只有把经验喂回提示词，
// 它才会真的影响后续表现**——否则经验库只是个收藏夹。
//
// 召回策略：
//   · 按用户这一轮的输入做关键词匹配（不是全库塞进去——那会挤掉正文）；
//   · 只召回当前模式下的条目（CTF 的经验不该出现在 SRC 会话里）；
//   · 条数可配（默认 5），每条截断到合理长度；
//   · 召回到什么会记进审计日志，方便判断「这次答得好是不是经验起作用」。
//
// 注入位置：追加在系统提示词末尾，用分隔线隔开，模型能分清哪些是规则、
// 哪些是历史经验。

using System;
using System.Collections.Generic;
using System.Text;

namespace SpiritScout.Knowledge
{
    /// <summary>一次召回的产物（便于日志与调试）。</summary>
    public sealed class RecallResult
    {
        public List<Core.Experience> Items { get; set; }
        public string Block { get; set; }
        public int Chars { get; set; }

        public RecallResult()
        {
            Items = new List<Core.Experience>();
            Block = "";
        }

        public bool HasAny { get { return Items.Count > 0; } }
    }

    public static class ExperienceRecall
    {
        /// <summary>每条经验最多写多少字进提示词。</summary>
        private const int PerItemChars = 200;

        /// <summary>
        /// 按这一轮的输入召回经验，拼成可注入提示词的一段。
        /// 没有经验或没配库时返回空结果（调用方直接跳过即可）。
        /// </summary>
        public static RecallResult Build(ExperienceStore store, string query,
            string mode, int limit)
        {
            var result = new RecallResult();
            if (store == null || limit <= 0) return result;

            List<Core.Experience> items;
            try
            {
                items = store.Recall(query, mode, limit);
            }
            catch
            {
                return result;
            }
            if (items == null || items.Count == 0) return result;

            var sb = new StringBuilder();
            sb.Append("\n\n=== 相关经验（以往同类场景提炼出的信号，供参考）===\n");
            int n = 0;
            foreach (var e in items)
            {
                string text = (e.Insight ?? "").Trim();
                if (text.Length == 0) continue;
                if (text.Length > PerItemChars) text = text.Substring(0, PerItemChars) + "…";

                n++;
                sb.Append(n).Append(". ").Append(text);
                if (!string.IsNullOrEmpty(e.Tags)) sb.Append("　[").Append(e.Tags).Append(']');
                sb.Append('\n');
                result.Items.Add(e);
            }
            sb.Append("（这些是历史信号，不是本题结论；与当前观察冲突时以当前观察为准。）\n");

            result.Block = sb.ToString();
            result.Chars = result.Block.Length;
            return result;
        }
    }
}
