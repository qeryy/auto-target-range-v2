// 灵枢桌面版 —— 工具消息的解析（纯函数）
//
// ## 为什么单独拎出来
//
// 「拓扑」和「文件」两个标签各要解析一种 `Content` 格式。原来这两段解析
// 直接写在 `SessionTabs.BuildTopology` / `BuildFiles` 的闭包里 ——
//
//   · 没法单独测（要造一个 Panel、挂一个 DataTable 才跑得到）
//   · 两段逻辑混在 UI 代码里，改格式要翻 UI 文件
//
// 抽成纯函数之后：输入一个字符串，输出几个字段，**不碰任何 WinForms 类型**。
//
// ## 数据源（写解析前必须先确认这一层）
//
// `AgentLoop` 落库工具消息时：
//
// ```csharp
// _store.Append(sessionId, "tool",
//     "[" + call.Name + "] " + (result.Summary ?? ""),   // ← Content
//     call.Name);
// ```
//
// 所以 **`Message.Content` = `"[工具名] " + ToolResult.Summary`**，
// **不是 `Output`**。这个区别让 187 轮做出来的拓扑标签整个不可用（正则匹配不到）。
//
// ## 两种格式
//
// **delegate**（`Orchestrator`）
//
//     Content = "[delegate] " + profile.Name + "：" + Short(summary, 100)
//     异常时  = "[delegate] " + profile.Name + " 执行失败：" + 消息
//
// **file**（`FileTool`，五种 Summary）
//
//     "[file] 读取（二进制）" + GetFileName(path)
//     "[file] 读取 " + GetFileName(path) + " (" + n + " 字节)"
//     "[file] 写入 " + GetFileName(path)
//     "[file] 列出 " + path + "（" + count + " 项）"
//     "[file] 哈希 " + fi.Name
//
// 注意 `读取 / 写入 / 哈希` 只有**文件名**（`Path.GetFileName`），
// 只有 `列出` 带完整路径。完整路径不落库。

using System;

namespace SpiritScout.Session
{
    /// <summary>一条 `delegate` 工具消息的解析结果。</summary>
    public sealed class DelegateRecord
    {
        /// <summary>方向名（取不到时是「（未记录方向）」）。</summary>
        public string Agent { get; set; }
        /// <summary>结论摘要。</summary>
        public string Summary { get; set; }
        /// <summary>「已返回」或「执行失败」。</summary>
        public string State { get; set; }

        public DelegateRecord()
        {
            Agent = "";
            Summary = "";
            State = "已返回";
        }
    }

    /// <summary>一条 `file` 工具消息的解析结果。</summary>
    public sealed class FileRecord
    {
        /// <summary>读取 / 写入 / 列出 / 哈希 / 其它。</summary>
        public string Action { get; set; }
        /// <summary>目标（文件名或目录，**含**「(123 字节)」这类尾巴）。</summary>
        public string Target { get; set; }

        public FileRecord()
        {
            Action = "其它";
            Target = "";
        }
    }

    public static class SessionToolMessage
    {
        private const string DelegatePrefix = "[delegate]";
        private const string FilePrefix = "[file]";

        /// <summary>
        /// 解析 `delegate` 的落库文本。**不是 delegate 消息就返回 null**
        /// （调用方据此过滤，不用自己先判前缀）。
        /// </summary>
        public static DelegateRecord ParseDelegate(string content)
        {
            string body = (content ?? "").Trim();
            if (!body.StartsWith(DelegatePrefix, StringComparison.Ordinal)) return null;

            string rest = body.Substring(DelegatePrefix.Length).Trim();
            var rec = new DelegateRecord();

            // 切**第一个**全角冒号：摘要里再有冒号也不会切错
            int colon = rest.IndexOf('：');
            string head = colon > 0 ? rest.Substring(0, colon).Trim() : rest;
            if (colon > 0) rec.Summary = rest.Substring(colon + 1).Trim();

            // 异常分支的 Summary 是「方向 执行失败：消息」
            const string FailSuffix = " 执行失败";
            if (head.EndsWith(FailSuffix, StringComparison.Ordinal))
            {
                rec.Agent = head.Substring(0, head.Length - FailSuffix.Length).Trim();
                rec.State = "执行失败";
            }
            else
            {
                rec.Agent = head;
            }

            // 成功/失败在正常分支里分不出来（ToolResult.Ok 不落库），
            // 所以只标「已返回」，不冒充「已完成」。
            if (rec.Agent.Length == 0) rec.Agent = "（未记录方向）";
            return rec;
        }

        /// <summary>
        /// 解析 `file` 的落库文本。**不是 file 消息就返回 null**。
        ///
        /// 目标**保留原样**（含「(123 字节)」「（12 项）」），不剥离：
        /// 剥了要丢信息，而且每种动作要写一套剥离逻辑。
        /// </summary>
        public static FileRecord ParseFile(string content)
        {
            string body = (content ?? "").Trim();
            if (!body.StartsWith(FilePrefix, StringComparison.Ordinal)) return null;

            string rest = body.Substring(FilePrefix.Length).Trim();
            if (rest.Length == 0) return null;

            var rec = new FileRecord { Target = rest };

            // **长的前缀先判**：`读取（二进制）xxx` 的第四个字是「（」不是空格，
            // 两条本来不会误匹配；但把长的放前面是防御性写法，
            // 将来加「读取（文本）」这类前缀时不会踩到。
            if (rest.StartsWith("读取（二进制）", StringComparison.Ordinal))
            {
                rec.Action = "读取";
                rec.Target = rest.Substring("读取（二进制）".Length).Trim();
            }
            else if (rest.StartsWith("读取 ", StringComparison.Ordinal))
            {
                rec.Action = "读取";
                rec.Target = rest.Substring("读取 ".Length).Trim();
            }
            else if (rest.StartsWith("写入 ", StringComparison.Ordinal))
            {
                rec.Action = "写入";
                rec.Target = rest.Substring("写入 ".Length).Trim();
            }
            else if (rest.StartsWith("列出 ", StringComparison.Ordinal))
            {
                rec.Action = "列出";
                rec.Target = rest.Substring("列出 ".Length).Trim();
            }
            else if (rest.StartsWith("哈希 ", StringComparison.Ordinal))
            {
                rec.Action = "哈希";
                rec.Target = rest.Substring("哈希 ".Length).Trim();
            }
            // 都不匹配 → 保持 Action = "其它"、Target = 原文（比如工具报错）

            return rec;
        }
    }
}
