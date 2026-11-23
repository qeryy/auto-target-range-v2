// 灵枢桌面版 —— 轻量 Markdown 分段器
//
// ## 为什么需要
//
// 模型的回复**基本都是 Markdown**：`### 结论`、`- 第一步`、```bash 命令```。
// 桌面版原来把这些原文直接画进气泡——用户看到的是三个反引号和一堆星号，
// 而 web 版渲染成了标题、列表和代码块。
//
// ## 为什么不是完整 Markdown
//
// 完整解析器要处理嵌套列表、表格、脚注、内联 HTML……对一个气泡来说太重了。
// 这里只认**模型最常用的六种块**，一块一行逻辑，够用且不会出错：
//
//   1. 围栏代码块   ```lang … ```
//   2. 标题         # / ## / ###
//   3. 列表项       - / * / + / 1.
//   4. 引用         >
//   5. 分隔线       --- / ***
//   6. 普通段落（连续非空行合成一段）
//
// 内联样式（**粗体**、`代码`）**不解析**——WinForms 的 TextRenderer 不支持
// 同一行混排不同字体，要做得用 RichTextBox 并放弃自绘圆角。收益不值。
// 代码块是例外：它单独成块，能整块换字体，效果最明显。

using System;
using System.Collections.Generic;

namespace SpiritScout.Ui.Widgets
{
    public enum MdKind
    {
        Paragraph,
        Heading,
        ListItem,
        Code,
        Quote,
        Rule,
    }

    public sealed class MdBlock
    {
        public MdKind Kind { get; set; }
        /// <summary>块内容（代码块是代码本身，已去掉围栏行）。</summary>
        public string Text { get; set; }
        /// <summary>标题级别 1–3；列表项存序号（有序列表）或 0（无序）。</summary>
        public int Level { get; set; }
        /// <summary>代码块的语言标记（可能为空）。</summary>
        public string Lang { get; set; }

        public MdBlock()
        {
            Text = "";
            Lang = "";
        }
    }

    public static class MarkdownLite
    {
        public static List<MdBlock> Parse(string src)
        {
            var blocks = new List<MdBlock>();
            if (string.IsNullOrEmpty(src)) return blocks;

            string[] lines = src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var para = new List<string>();

            Action flushPara = delegate
            {
                if (para.Count == 0) return;
                blocks.Add(new MdBlock { Kind = MdKind.Paragraph, Text = string.Join("\n", para.ToArray()) });
                para.Clear();
            };

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                // ---- 围栏代码块 ----
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    flushPara();
                    string lang = trimmed.Substring(3).Trim();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length)
                    {
                        string inner = lines[i];
                        if (inner.Trim().StartsWith("```", StringComparison.Ordinal)) break;
                        code.Add(inner);
                        i++;
                    }
                    blocks.Add(new MdBlock
                    {
                        Kind = MdKind.Code,
                        Text = string.Join("\n", code.ToArray()),
                        Lang = lang,
                    });
                    continue;
                }

                // ---- 空行 ----
                if (trimmed.Length == 0)
                {
                    flushPara();
                    continue;
                }

                // ---- 分隔线 ----
                if (trimmed == "---" || trimmed == "***" || trimmed == "___")
                {
                    flushPara();
                    blocks.Add(new MdBlock { Kind = MdKind.Rule });
                    continue;
                }

                // ---- 标题 ----
                if (trimmed[0] == '#')
                {
                    int lv = 0;
                    while (lv < trimmed.Length && lv < 6 && trimmed[lv] == '#') lv++;
                    if (lv >= 1 && lv <= 6 && lv < trimmed.Length && trimmed[lv] == ' ')
                    {
                        flushPara();
                        blocks.Add(new MdBlock
                        {
                            Kind = MdKind.Heading,
                            Level = Math.Min(lv, 3),
                            Text = trimmed.Substring(lv + 1).Trim(),
                        });
                        continue;
                    }
                }

                // ---- 引用 ----
                if (trimmed[0] == '>')
                {
                    flushPara();
                    string q = trimmed.Substring(1).TrimStart();
                    // 连续的 > 行合成一个引用块
                    if (blocks.Count > 0 && blocks[blocks.Count - 1].Kind == MdKind.Quote)
                        blocks[blocks.Count - 1].Text += "\n" + q;
                    else
                        blocks.Add(new MdBlock { Kind = MdKind.Quote, Text = q });
                    continue;
                }

                // ---- 列表项 ----
                string bullet = "";
                string rest = null;
                if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+')
                    && trimmed[1] == ' ')
                {
                    bullet = "·";
                    rest = trimmed.Substring(2).Trim();
                }
                else
                {
                    // 有序列表：1. / 12. 之类
                    int d = 0;
                    while (d < trimmed.Length && char.IsDigit(trimmed[d])) d++;
                    if (d > 0 && d + 1 < trimmed.Length && trimmed[d] == '.' && trimmed[d + 1] == ' ')
                    {
                        bullet = trimmed.Substring(0, d) + ".";
                        rest = trimmed.Substring(d + 2).Trim();
                    }
                }
                if (rest != null)
                {
                    flushPara();
                    blocks.Add(new MdBlock { Kind = MdKind.ListItem, Text = rest, Lang = bullet });
                    continue;
                }

                // ---- 普通行 ----
                para.Add(line);
            }

            flushPara();
            return blocks;
        }
    }
}
