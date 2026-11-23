// 灵枢桌面版 —— 记事工具
//
// 同一会话内的临时线索。存进会话文件，跟着会话走。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class NoteTool : ITool
    {
        private readonly List<string> _notes = new List<string>();

        public string Name { get { return "note"; } }

        public string Description
        {
            get
            {
                return "记一条笔记或读回已记的内容。用于在多轮操作之间保留关键线索"
                     + "（发现的路径、口令、flag 片段等），避免上下文被裁掉后丢失。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""add 记一条 / list 读回全部"" },
    ""text"": { ""type"": ""string"", ""description"": ""add 时要记的内容"" }
  },
  ""required"": [""action""]
}";
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "list").ToLowerInvariant();
            if (action == "add")
            {
                string text = Arg.Str(args, "text");
                if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(ToolResult.Fail("text 不能为空"));
                _notes.Add(text.Trim());
                return Task.FromResult(new ToolResult
                {
                    Ok = true,
                    Output = "已记录（共 " + _notes.Count + " 条）",
                    Summary = "记笔记：" + (text.Length > 30 ? text.Substring(0, 30) + "…" : text),
                });
            }

            var sb = new StringBuilder();
            if (_notes.Count == 0) sb.Append("（还没有笔记）");
            else
            {
                for (int i = 0; i < _notes.Count; i++)
                    sb.Append(i + 1).Append(". ").Append(_notes[i]).Append('\n');
            }
            return Task.FromResult(new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "读笔记（" + _notes.Count + " 条）",
            });
        }
    }

    // ===================== 参数读取 =====================
}
