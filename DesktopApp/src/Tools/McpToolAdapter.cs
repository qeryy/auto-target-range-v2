// 灵枢桌面版 —— MCP 工具接入
//
// MCP 页能配能测，但配好之后 agent 还调不到它的工具。这里补上那一环：
// 把 MCP 服务器暴露的工具包装成普通的 ITool，注册进 ToolRegistry。
//
// ## 为什么要长连接
//
// MCP 的 stdio 传输是「拉起一个子进程，用 stdin/stdout 说 JSON-RPC」。
// 如果每次工具调用都重启进程，一次会话里可能重启几十次——慢，而且有状态
// 的服务器（比如已打开某个数据库的）每次都要重新初始化。
//
// 所以 McpConnection 持有一个长连接：第一次调用时启动，会话结束才关。
//
// ## 与 web 版的差别
//
// web 版把 MCP 工具名暴露成 `mcp__<server>__<tool>`，因为它有命名空间冲突要处理。
// 桌面版工具本来就少（6 个内置），直接叫 `<server>_<tool>` 也不会撞，
// 而且模型读起来更清楚。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpiritScout.Tools
{
    /// <summary>
    /// 一台 MCP 服务器的长连接。首次调用启动子进程，之后复用。
    /// </summary>
    public sealed class McpConnection : IDisposable
    {
        private readonly McpServer _server;
        private Process _proc;
        private StreamWriter _stdin;
        private readonly object _lock = new object();
        private int _nextId = 1;
        private bool _broken;

        public string ServerName { get { return _server.Name; } }
        public bool Broken { get { return _broken; } }

        public McpConnection(McpServer server)
        {
            _server = server;
        }

        /// <summary>确保进程活着并完成握手。返回是否可用。</summary>
        public async Task<bool> EnsureStartedAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_broken) return false;
                if (_proc != null && !_proc.HasExited) return true;
            }

            if (!string.Equals(_server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _server.Command,
                    Arguments = _server.Args ?? "",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                // 应用用户配的环境变量（每行 KEY=VALUE）
                if (!string.IsNullOrWhiteSpace(_server.Env))
                {
                    foreach (var raw in _server.Env.Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.Length > 0) psi.EnvironmentVariables[k] = v;
                    }
                }

                var proc = Process.Start(psi);
                if (proc == null) return false;

                // .NET Framework 4.8 的 ProcessStartInfo 没有 StandardInputEncoding
                // （那是 .NET Core 才有的），所以自己包一个 UTF-8 写器。
                // 不包的话 Windows 会按系统代码页（中文系统 GBK）写，
                // 而 MCP 服务器按 UTF-8 读——带中文的参数会被对端解码失败、
                // 整行丢弃，表现为「这个工具调用没响应」。
                // 实测踩过：echo("第1次") 一直超时，add(100,23) 秒回。
                var writer = new StreamWriter(proc.StandardInput.BaseStream,
                    new UTF8Encoding(false)) { AutoFlush = true };
                lock (_lock) { _proc = proc; _stdin = writer; }

                // 握手
                var initReply = await SendAsync(new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", NextId() },
                    { "method", "initialize" },
                    { "params", new Dictionary<string, object>
                        {
                            { "protocolVersion", "2024-11-05" },
                            { "capabilities", new Dictionary<string, object>() },
                            { "clientInfo", new Dictionary<string, object>
                                { { "name", Core.AppInfo.McpClientName }, { "version", Core.AppInfo.Version } } },
                        }
                    },
                }, ct).ConfigureAwait(false);

                if (initReply == null) { MarkBroken(); return false; }

                SendNotification(new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "method", "notifications/initialized" },
                });

                return true;
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("MCP[" + _server.Name + "] 启动失败: " + ex.Message);
                MarkBroken();
                return false;
            }
        }

        /// <summary>列工具。</summary>
        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
        {
            var list = new List<McpTool>();
            var reply = await SendAsync(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", NextId() },
                { "method", "tools/list" },
                { "params", new Dictionary<string, object>() },
            }, ct).ConfigureAwait(false);
            if (reply == null) return list;

            var root = Core.Json.AsDict(Core.Json.Parse(reply));
            var res = Core.Json.Dict(root, "result");
            var arr = Core.Json.Array(res, "tools");
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    var d = Core.Json.AsDict(item);
                    if (d == null) continue;
                    list.Add(new McpTool
                    {
                        Name = Core.Json.Str(d, "name"),
                        Description = Core.Json.Str(d, "description"),
                        InputSchemaJson = d.ContainsKey("inputSchema")
                            ? new JavaScriptSerializer().Serialize(d["inputSchema"]) : "",
                    });
                }
            }
            return list;
        }

        /// <summary>调用一个工具，返回文本结果。</summary>
        public async Task<ToolResult> CallAsync(string tool, string argumentsJson,
            CancellationToken ct)
        {
            if (!await EnsureStartedAsync(ct).ConfigureAwait(false))
                return ToolResult.Fail("MCP 服务器 " + _server.Name + " 不可用");

            object args;
            try
            {
                args = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new Dictionary<string, object>()
                    : new JavaScriptSerializer().DeserializeObject(argumentsJson);
            }
            catch
            {
                args = new Dictionary<string, object>();
            }

            var reply = await SendAsync(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", NextId() },
                { "method", "tools/call" },
                { "params", new Dictionary<string, object>
                    {
                        { "name", tool },
                        { "arguments", args },
                    }
                },
            }, ct).ConfigureAwait(false);

            if (reply == null) return ToolResult.Fail("MCP 调用无响应（" + _server.Name + "）");

            var root = Core.Json.AsDict(Core.Json.Parse(reply));
            if (root == null) return ToolResult.Fail("MCP 返回的不是 JSON");

            // 错误
            var err = Core.Json.Dict(root, "error");
            if (err != null)
                return ToolResult.Fail("MCP 报错：" + Core.Json.Str(err, "message"));

            var res = Core.Json.Dict(root, "result");
            if (res == null) return ToolResult.Fail("MCP 响应里没有 result");

            bool isError = Core.Json.Bool(res, "isError");
            var content = Core.Json.Array(res, "content");
            var sb = new StringBuilder();
            if (content != null)
            {
                foreach (var block in content)
                {
                    var d = Core.Json.AsDict(block);
                    if (d == null) continue;
                    if (Core.Json.Str(d, "type") == "text")
                        sb.Append(Core.Json.Str(d, "text"));
                }
            }
            string text = sb.ToString();
            if (text.Length == 0) text = "（工具没有返回文本内容）";

            return new ToolResult
            {
                Ok = !isError,
                Output = text.Length > 16000 ? text.Substring(0, 16000) + "\n…（截断）" : text,
                Summary = _server.Name + "/" + tool + (isError ? " 返回错误" : " 完成"),
            };
        }

        // ---------- 底层 ----------

        private int NextId()
        {
            lock (_lock) { return _nextId++; }
        }

        private void SendNotification(Dictionary<string, object> msg)
        {
            try
            {
                var proc = _proc;
                if (proc == null || proc.HasExited) return;
                string json = new JavaScriptSerializer().Serialize(msg);
                var w = _stdin;
                if (w != null) { w.WriteLine(json); w.Flush(); }
            }
            catch { }
        }

        /// <summary>
        /// 发一条请求并等**对应 id** 的响应。
        ///
        /// 只读一行是不够的：MCP 服务器可能先发通知、也可能因为我们发了
        /// `notifications/initialized`（无 id）而回一条空响应，那样读到的就是
        /// 前一条的残留，表现为「第一次调用失败、第二次却成功」。
        /// 正确做法是按 JSON-RPC 的 id 匹配，不匹配的行丢掉继续读。
        /// </summary>
        private async Task<string> SendAsync(Dictionary<string, object> msg, CancellationToken ct)
        {
            var proc = _proc;
            if (proc == null || proc.HasExited) return null;
            try
            {
                object wantId = msg.ContainsKey("id") ? msg["id"] : null;
                string json = new JavaScriptSerializer().Serialize(msg);
                var w = _stdin;
                if (w == null) return null;
                w.WriteLine(json);
                w.Flush();

                int timeout = _server.TimeoutSeconds <= 0 ? 60 : _server.TimeoutSeconds;
                var deadline = DateTime.UtcNow.AddSeconds(timeout);
                var buffer = new StringBuilder();

                while (DateTime.UtcNow < deadline)
                {
                    var remain = deadline - DateTime.UtcNow;
                    if (remain <= TimeSpan.Zero) break;

                    var readTask = proc.StandardOutput.ReadLineAsync();
                    var done = await Task.WhenAny(readTask,
                        Task.Delay((int)Math.Max(1, remain.TotalMilliseconds), ct))
                        .ConfigureAwait(false);
                    if (done != readTask) break;

                    string line = readTask.Result;
                    if (line == null) break;
                    if (line.Trim().Length == 0) continue;

                    // 没有 id 的是通知，跳过
                    if (wantId == null) return line;

                    try
                    {
                        var obj = Core.Json.AsDict(Core.Json.Parse(line));
                        if (obj == null) continue;
                        object gotId;
                        if (!obj.TryGetValue("id", out gotId) || gotId == null) continue;
                        // id 可能是数字也可能是字符串，统一按字符串比
                        if (gotId.ToString() == wantId.ToString()) return line;
                        // 不匹配：留个记录继续等
                        buffer.AppendLine("（跳过 id=" + gotId + " 的响应）");
                    }
                    catch
                    {
                        continue;
                    }
                }

                Core.ConfigManager.Log("MCP[" + _server.Name + "] 等 id=" + wantId + " 超时"
                    + (buffer.Length > 0 ? "；" + buffer.ToString().Trim() : ""));
                return null;
            }
            catch
            {
                MarkBroken();
                return null;
            }
        }

        private void MarkBroken()
        {
            lock (_lock) { _broken = true; }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                var proc = _proc;
                _proc = null;
                var w = _stdin;
                _stdin = null;
                if (w != null) { try { w.Dispose(); } catch { } }
                if (proc != null)
                {
                    try { proc.StandardInput.Close(); } catch { }
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                }
            }
        }
    }

    /// <summary>把一个 MCP 工具包装成普通 ITool。</summary>
    public sealed class McpToolAdapter : ITool
    {
        private readonly McpConnection _conn;
        private readonly McpTool _tool;
        private readonly string _exposedName;

        public string Name { get { return _exposedName; } }

        public string Description
        {
            get
            {
                string desc = string.IsNullOrEmpty(_tool.Description)
                    ? _tool.Name : _tool.Description;
                return desc + "（来自 MCP 服务器 " + _conn.ServerName + "）";
            }
        }

        public string ParametersJson
        {
            get
            {
                const string empty = @"{
  ""type"": ""object"",
  ""properties"": {}
}";
                if (string.IsNullOrWhiteSpace(_tool.InputSchemaJson)) return empty;

                // 外部 schema 未必合法（JSON Schema 片段、带 $ref、空串都有）。
                // 先自己解一遍：解不开就退化成"无参数"——宁可少一个参数说明，
                // 也不能让整份 tools 定义失效，导致模型连别的工具都用不了。
                try
                {
                    if (Core.Json.AsDict(Core.Json.Parse(_tool.InputSchemaJson)) == null)
                    {
                        Core.ConfigManager.Log("MCP[" + _conn.ServerName + "/" + _tool.Name
                            + "] inputSchema 不是对象，已退化为空参数");
                        return empty;
                    }
                    return _tool.InputSchemaJson;
                }
                catch
                {
                    Core.ConfigManager.Log("MCP[" + _conn.ServerName + "/" + _tool.Name
                        + "] inputSchema 无法解析，已退化为空参数");
                    return empty;
                }
            }
        }

        public McpToolAdapter(McpConnection conn, McpTool tool, string exposedName)
        {
            _conn = conn;
            _tool = tool;
            _exposedName = exposedName;
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string json = new JavaScriptSerializer().Serialize(args ?? new Dictionary<string, object>());
            return _conn.CallAsync(_tool.Name, json, ct);
        }
    }

    /// <summary>
    /// 把「已启用的 MCP 服务器」装配进工具注册表。
    /// 会话开始时调用一次；返回的连接列表由调用方负责 Dispose。
    /// </summary>
    public static class McpToolLoader
    {
        /// <summary>单台服务器最多接入多少个工具（防止某个服务器塞几百个把提示词撑爆）。</summary>
        private const int MaxToolsPerServer = 24;

        public static async Task<List<McpConnection>> MountAsync(McpRepository repo,
            ToolRegistry registry, CancellationToken ct = default(CancellationToken))
        {
            var connections = new List<McpConnection>();
            var servers = repo.List();

            foreach (var server in servers)
            {
                if (!server.Enabled) continue;
                // 目前只支持 stdio 长连接；http/sse 需要各自的会话管理，留待后续
                if (!string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                    continue;

                var conn = new McpConnection(server);
                try
                {
                    if (!await conn.EnsureStartedAsync(ct).ConfigureAwait(false))
                    {
                        conn.Dispose();
                        continue;
                    }

                    var tools = await conn.ListToolsAsync(ct).ConfigureAwait(false);
                    if (tools.Count == 0) { conn.Dispose(); continue; }

                    int n = 0;
                    foreach (var t in tools)
                    {
                        if (n >= MaxToolsPerServer) break;
                        string exposed = SafeName(server.Name) + "_" + SafeName(t.Name);
                        registry.Register(new McpToolAdapter(conn, t, exposed));
                        n++;
                    }
                    connections.Add(conn);
                    Core.ConfigManager.Log("MCP[" + server.Name + "] 接入 " + n + " 个工具");
                }
                catch (Exception ex)
                {
                    Core.ConfigManager.Log("MCP[" + server.Name + "] 装配失败: " + ex.Message);
                    conn.Dispose();
                }
            }
            return connections;
        }

        /// <summary>工具名只能有字母数字下划线，否则模型调用时容易写错。</summary>
        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mcp";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }
}
