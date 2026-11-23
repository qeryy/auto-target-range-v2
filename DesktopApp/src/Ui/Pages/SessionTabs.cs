// 灵枢桌面版 —— 会话详情的数据标签
//
// web 端的会话页有 9 个标签。桌面版做**有真实数据源**的五个：
//   · 事件：从消息里 role=tool 的条目推导（工具调用轨迹）
//   · 拓扑：从 `[delegate] 方向：摘要` 推导子任务派发链
//   · 文件：从 `[file] 动作 目标` 推导本会话动过的文件/目录
//   · Flag：从消息正文按 flag 格式正则提取
//   · 经验：按 source_session 过滤本会话提炼出的条目
//
// **所有工具消息的 Content 都是 `"[" + 工具名 + "] " + ToolResult.Summary`**
// （见 `AgentLoop.cs` 的 `_store.Append(...)`），不是 `Output`。
// 写解析前先看这一层 —— 跳过它会写出永远匹配不上的正则。
//
// **刻意不做**：
//   · 漏洞 —— SRC 场景才有数据，CTF 会话永远是空的
//   · 报告 / WP —— 需要生成管线
// 做空壳比不做更糟：用户点进去看到一片空白，会以为程序坏了。
//
// ## 关于「拓扑」
//
// 早先这里写着「拓扑需要图可视化，WinForms 画力导向图是另一个工程」。
// 去读 web 版的 `.tnode` 才发现它**本来就不是力导向图**：
//
//   .tnode .tact { display: flex; align-items: center; gap: 8px; }
//   .tnode .tact .dot { width: 8px; height: 8px; border-radius: 50%; }
//   .tnode .tact .dot.active { background: var(--accent); animation: blink; }
//   .tnode .tact .dot.done { background: var(--ok); }
//
// 一个圆点 + 加粗标题 + 等宽小字，是**带状态点的列表**。
// 列表用 `DataTable` 就够，不需要图引擎。之前的判断是看了名字想当然。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    /// <summary>会话详情的标签构建工具。</summary>
    public static class SessionTabs
    {
        /// <summary>事件标签：工具调用轨迹。</summary>
        public static Control BuildEvents(Core.SessionStore store, string sessionId)
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 10, 12, 10) };

            var grid = new DataTable { Dock = DockStyle.Fill };
            grid.AddColumn("seq", "#", 0.4f, DataGridViewContentAlignment.MiddleRight);
            grid.AddColumn("time", "时间", 1.1f);
            grid.AddColumn("tool", "工具", 1.2f);
            grid.AddColumn("content", "输出摘要", 5f);

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            root.Controls.Add(grid);
            root.Controls.Add(hint);

            Action refresh = () =>
            {
                var sf = store.Load(sessionId);
                var rows = new List<Dictionary<string, object>>();
                int seq = 0;
                if (sf != null)
                {
                    foreach (var m in sf.Messages)
                    {
                        if (m.Role != "tool") continue;
                        seq++;
                        rows.Add(new Dictionary<string, object>
                        {
                            { "seq", seq },
                            { "time", m.CreatedAt.ToString("HH:mm:ss") },
                            { "tool", m.Name },
                            // 列名就是「输出摘要」，但原来塞的是完整 Content ——
                            // 一次 `shell cat big.log` 的输出可能有几百 KB，
                            // 整串挂在一个单元格里既占内存也没人看得完。
                            { "content", Core.Text.OneLine(m.Content, 200) },
                        });
                    }
                }
                grid.Fill(rows);
                hint.Text = rows.Count == 0
                    ? "还没有工具调用。让模型做点需要动手的事（读文件、跑命令）就会出现。"
                    : "共 " + rows.Count + " 次工具调用";
            };

            refresh();
            root.Tag = new Action(refresh);
            return root;
        }

        /// <summary>
        /// 拓扑标签：主 Agent 通过 `delegate` 工具派出去的子任务。
        ///
        /// ## 数据源（这里踩过两次错，写清楚）
        ///
        /// `AgentLoop` 落库工具消息时写的是：
        ///
        /// ```csharp
        /// _store.Append(sessionId, "tool",
        ///     "[" + call.Name + "] " + (result.Summary ?? ""),   // ← Content
        ///     call.Name);
        /// ```
        ///
        /// 也就是说 **`Message.Content` = `"[工具名] " + ToolResult.Summary`，
        /// 不是 `Output`**。而 `Orchestrator` 里 delegate 的 `Output` 是
        /// `"[方向名 的结论]\n正文"` —— 那个字符串**从来没落过库**。
        ///
        /// 所以方向名要从 `Summary` 的形态里取：
        ///
        ///     Summary = profile.Name + "：" + Short(summary, 100)
        ///     Content = "[delegate] 密码分析：RSA 小模数分解拿到明文"
        ///
        /// 异常分支（`ToolResult.Fail`）的 Summary 是
        /// `profile.Name + " 执行失败：" + 消息`，可以据此标「执行失败」。
        ///
        /// 但**成功与失败在正常分支里分不出来**：`ToolResult.Ok` 不落库，
        /// 失败时 Summary 也只是「方向：错误文本」，和成功长得一样。
        /// 所以常态标「已返回」，不冒充「已完成」。
        ///
        /// 为什么不是「真拓扑图」：web 版的 `.tnode` 本身就是带状态点的**列表**，
        /// 不是力导向图。这里用表格对齐它，不引入图引擎。
        /// </summary>
        public static Control BuildTopology(Core.SessionStore store, string sessionId)
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 10, 12, 10) };

            var grid = new DataTable { Dock = DockStyle.Fill };
            grid.AddColumn("seq", "#", 0.4f, DataGridViewContentAlignment.MiddleRight);
            grid.AddColumn("agent", "方向", 1.3f);
            grid.AddColumn("state", "状态", 0.9f);
            grid.AddColumn("result", "结论摘要", 5f);
            grid.MakeChipColumn("state");
            grid.Colorize("state", delegate(string v)
            {
                if (v == "已返回") return Theme.Ok;
                if (v == "执行失败") return Theme.Danger;
                return Theme.Muted;
            });

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            root.Controls.Add(grid);
            root.Controls.Add(hint);

            Action refresh = delegate
            {
                var sf = store.Load(sessionId);
                var rows = new List<Dictionary<string, object>>();
                int seq = 0;
                if (sf != null)
                {
                    foreach (var m in sf.Messages)
                    {
                        if (m.Role != "tool" || m.Name != "delegate") continue;

                        // 解析抽到 `Session.SessionToolMessage`（纯函数，可单测）
                        var rec = Session.SessionToolMessage.ParseDelegate(m.Content);
                        if (rec == null) continue;
                        seq++;

                        rows.Add(new Dictionary<string, object>
                        {
                            { "seq", seq },
                            { "agent", rec.Agent },
                            { "state", rec.State },
                            { "result", Core.Text.OneLine(rec.Summary) },
                        });
                    }
                }
                grid.Fill(rows);
                if (rows.Count == 0)
                {
                    hint.Text = "还没有派发子任务。「编排模式」下主 Agent 会把工作拆给方向 agent。";
                }
                else
                {
                    // 「已返回」只说明子 agent 交了结论，不说结论对不对 ——
                    // 会话消息里没有成功/失败标记（ToolResult.Ok 不落库）。
                    hint.Text = "共 " + rows.Count + " 个子任务 · 由主 Agent 通过 delegate 派发"
                        + " ·「已返回」只表示交了结论，成败要看结论内容";
                }
            };

            refresh();
            root.Tag = new Action(refresh);
            return root;
        }

        /// <summary>
        /// 文件标签：本会话里 `file` 工具动过的文件 / 目录。
        ///
        /// ## 数据源（先看写入端，别猜）
        ///
        /// `AgentLoop` 落库时：`Content = "[file] " + ToolResult.Summary`。
        /// `FileTool` 的五种 Summary 精确形态：
        ///
        ///     "读取（二进制）" + GetFileName(path)           // read 二进制
        ///     "读取 " + GetFileName(path) + " (" + n + " 字节)"   // read 文本
        ///     "写入 " + GetFileName(path)                   // write
        ///     "列出 " + path + "（" + count + " 项）"        // list（完整路径）
        ///     "哈希 " + fi.Name                             // hash
        ///
        /// **只有「列出」带完整路径**，其余三种只有文件名——因为 `FileTool`
        /// 对这几种用的是 `Path.GetFileName()`。完整路径不落库，这里也不假装有。
        ///
        /// ## 动作词识别
        ///
        /// 只切动作词，**目标保留原样**（含「(123 字节)」「（12 项）」这种尾巴），
        /// 这样不丢信息，也不用为每种动作写一套剥离逻辑。
        /// `读取（二进制）` 和 `读取 ` 不会混淆：前者第四个字是「（」，不是空格。
        /// </summary>
        public static Control BuildFiles(Core.SessionStore store, string sessionId)
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 10, 12, 10) };

            var grid = new DataTable { Dock = DockStyle.Fill };
            grid.AddColumn("seq", "#", 0.4f, DataGridViewContentAlignment.MiddleRight);
            grid.AddColumn("time", "时间", 0.9f);
            grid.AddColumn("act", "动作", 0.8f);
            grid.AddColumn("target", "文件 / 目录", 5f);
            grid.MakeChipColumn("act");
            grid.Colorize("act", delegate(string v)
            {
                // 写入是会改磁盘的动作，单独标黄；其余是只读
                if (v == "写入") return Theme.Warn;
                if (v == "读取") return Theme.Ok;
                return Theme.Muted;
            });

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            root.Controls.Add(grid);
            root.Controls.Add(hint);

            Action refresh = delegate
            {
                var sf = store.Load(sessionId);
                var rows = new List<Dictionary<string, object>>();
                int seq = 0;
                if (sf != null)
                {
                    foreach (var m in sf.Messages)
                    {
                        if (m.Role != "tool" || m.Name != "file") continue;

                        // 解析抽到 `Session.SessionToolMessage`（纯函数，可单测）
                        var rec = Session.SessionToolMessage.ParseFile(m.Content);
                        if (rec == null) continue;
                        seq++;

                        rows.Add(new Dictionary<string, object>
                        {
                            { "seq", seq },
                            { "time", m.CreatedAt.ToString("HH:mm:ss") },
                            { "act", rec.Action },
                            { "target", rec.Target },
                        });
                    }
                }
                grid.Fill(rows);
                hint.Text = rows.Count == 0
                    ? "这个会话还没有文件操作。让模型读文件或写文件就会出现。"
                    : "共 " + rows.Count + " 次文件操作 · 「读取 / 写入 / 哈希」只有文件名，完整路径没有落库";
            };

            refresh();
            root.Tag = new Action(refresh);
            return root;
        }

        /// <summary>Flag 标签：从消息正文里提。</summary>
        public static Control BuildFlags(Core.SessionStore store, string sessionId)
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 10, 12, 10) };

            var grid = new DataTable { Dock = DockStyle.Fill };
            grid.AddColumn("seq", "#", 0.4f, DataGridViewContentAlignment.MiddleRight);
            grid.AddColumn("flag", "Flag", 3f);
            grid.AddColumn("source", "出现位置", 1.4f);
            grid.AddColumn("time", "时间", 1.1f);

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            root.Controls.Add(grid);
            root.Controls.Add(hint);

            Action refresh = () =>
            {
                var sf = store.Load(sessionId);
                var rows = new List<Dictionary<string, object>>();
                var seen = new HashSet<string>();

                // 优先用会话里声明的格式；没声明就用常见的三种
                string pattern = sf != null && sf.Session != null
                    && !string.IsNullOrWhiteSpace(sf.Session.FlagFormat)
                    ? sf.Session.FlagFormat
                    : @"(?:flag|FLAG|ctf|CTF)\{[^}]{1,200}\}";

                Regex re;
                try { re = new Regex(pattern, RegexOptions.IgnoreCase); }
                catch { re = new Regex(@"(?:flag|FLAG|ctf|CTF)\{[^}]{1,200}\}"); }

                int seq = 0;
                if (sf != null)
                {
                    foreach (var m in sf.Messages)
                    {
                        string text = m.Content ?? "";
                        foreach (Match hit in re.Matches(text))
                        {
                            string val = hit.Value.Trim();
                            if (!seen.Add(val)) continue;
                            seq++;
                            string where = m.Role == "tool" ? ("工具 " + m.Name)
                                : (m.Role == "assistant" ? "助手回复" : "用户输入");
                            rows.Add(new Dictionary<string, object>
                            {
                                { "seq", seq },
                                { "flag", val },
                                { "source", where },
                                { "time", m.CreatedAt.ToString("HH:mm:ss") },
                            });
                        }
                    }
                }
                grid.Fill(rows);
                hint.Text = rows.Count == 0
                    ? "这个会话里还没出现 flag。"
                    : "共发现 " + rows.Count + " 个不同的 flag（按会话声明的格式匹配）";
            };

            refresh();
            root.Tag = new Action(refresh);
            return root;
        }

        /// <summary>经验标签：本会话提炼出的条目。</summary>
        public static Control BuildExperience(Core.SessionStore store, string sessionId)
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 10, 12, 10) };

            var grid = new DataTable { Dock = DockStyle.Fill };
            grid.AddColumn("insight", "洞察", 5f);
            grid.AddColumn("tags", "标签", 1.4f);
            grid.AddColumn("time", "时间", 1.1f);

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            root.Controls.Add(grid);
            root.Controls.Add(hint);

            Action refresh = () =>
            {
                var expStore = new Knowledge.ExperienceStore(Core.AppPaths.ExperienceFile);
                var rows = new List<Dictionary<string, object>>();
                foreach (var e in expStore.All())
                {
                    if (!string.Equals(e.SourceSession, sessionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        // 同上：只防「模型写了五千字」把单元格撑爆，正常内容不动
                        { "insight", Core.Text.OneLine(e.Insight, 2000) },
                        { "tags", e.Tags },
                        { "time", e.CreatedAt.ToString("MM-dd HH:mm") },
                    });
                }
                grid.Fill(rows);
                hint.Text = rows.Count == 0
                    ? "这个会话还没提炼过经验。去「经验」页点「从会话提炼」。"
                    : "从本会话提炼出 " + rows.Count + " 条";
            };

            refresh();
            root.Tag = new Action(refresh);
            return root;
        }
    }
}
