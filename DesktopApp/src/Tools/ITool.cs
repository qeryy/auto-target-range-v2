// 灵枢桌面版 —— 工具契约与注册表
//
// 一个工具 = 名字 + 一句说明 + 参数 schema + 执行函数。
// 注册到 ToolRegistry 之后，模型就能通过 function calling 调用它。
//
// 加一个工具：写一个 ITool 实现，在 ToolRegistry.CreateDefault() 里加一行。
// 删一个工具：删掉那一行（文件留着不影响）。
//
// 参数 schema 用 OpenAI function calling 的 JSON Schema 子集：
//   {"type":"object","properties":{...},"required":[...]}
// 这样能直接被服务端识别，不用做转换。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    /// <summary>一次工具调用的结果。</summary>
    public sealed class ToolResult
    {
        public bool Ok { get; set; }

        /// <summary>回灌给模型看的文本（成败都写在这里）。</summary>
        public string Output { get; set; }

        /// <summary>给界面显示的一行摘要。</summary>
        public string Summary { get; set; }

        public ToolResult()
        {
            Output = "";
            Summary = "";
        }

        public static ToolResult Success(string output, string summary = "")
        {
            return new ToolResult { Ok = true, Output = output ?? "", Summary = summary ?? "" };
        }

        public static ToolResult Fail(string error)
        {
            return new ToolResult { Ok = false, Output = "错误：" + (error ?? "未知"), Summary = error ?? "失败" };
        }
    }

    /// <summary>一个工具。</summary>
    public interface ITool
    {
        /// <summary>模型看到的名字（snake_case）。</summary>
        string Name { get; }

        /// <summary>什么时候该用这个工具（模型据此判断）。</summary>
        string Description { get; }

        /// <summary>参数的 JSON Schema。</summary>
        string ParametersJson { get; }

        /// <summary>执行。args 是模型给的 JSON 解析后的字典。</summary>
        Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct);
    }

    /// <summary>工具注册表。</summary>
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools =
            new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);

        public void Register(ITool tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Name)) return;
            _tools[tool.Name] = tool;
        }

        public void Unregister(string name)
        {
            if (!string.IsNullOrEmpty(name)) _tools.Remove(name);
        }

        public ITool Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ITool t;
            return _tools.TryGetValue(name, out t) ? t : null;
        }

        public IList<ITool> All
        {
            get
            {
                var list = new List<ITool>(_tools.Values);
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return list;
            }
        }

        public int Count { get { return _tools.Count; } }

        /// <summary>
        /// 生成 OpenAI function calling 的 tools 数组（JSON 文本）。
        /// </summary>
        public string BuildToolsJson()
        {
            var arr = new List<object>();
            foreach (var t in All)
            {
                arr.Add(new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object>
                        {
                            { "name", t.Name },
                            { "description", t.Description },
                            { "parameters", ParseSchema(t.ParametersJson) },
                        }
                    },
                });
            }
            return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(arr);
        }

        private static object ParseSchema(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            try
            {
                return new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(json);
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// 默认工具集。**增删工具只改这里**。
        /// </summary>
        public static ToolRegistry CreateDefault()
        {
            var reg = new ToolRegistry();
            reg.Register(new ShellTool());
            reg.Register(new HttpTool());
            reg.Register(new WebFetchTool());
            reg.Register(new WebSearchTool());
            reg.Register(new ProbeTool());
            reg.Register(new CodecTool());
            reg.Register(new RegexTool());
            reg.Register(new CipherTool());
            reg.Register(new HashTool());
            reg.Register(new BaseTool());
            reg.Register(new JwtTool());
            reg.Register(new BinTool());
            reg.Register(new PayloadTool());
            reg.Register(new ImageTool());
            reg.Register(new CrackZipTool());
            reg.Register(new SqlTool());
            reg.Register(new FileTool());
            reg.Register(new NoteTool());
            reg.Register(new KnowledgeTool());
            return reg;
        }
    }
}
