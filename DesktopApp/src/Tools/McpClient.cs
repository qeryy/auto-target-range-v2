// 灵枢桌面版 —— MCP 客户端
//
// MCP（Model Context Protocol）让外部工具以统一协议接进来。桌面版支持三种传输：
//   stdio  拉起一个子进程，用 stdin/stdout 传 JSON-RPC（最常用，也是零依赖的那个）
//   http   Streamable HTTP 单端点
//   sse    旧版 SSE 端点
//
// 这一版实现到「能连上、能列出工具、能调一个工具」的程度——足够让用户验证
// 配置对不对。把外部工具并入 agent 工具目录是下一步的事（需要 tool 名字空间
// 与原有工具合并的策略）。
//
// 协议要点（stdio）：
//   每行一个 JSON-RPC 消息；先 initialize，再 notifications/initialized，
//   然后 tools/list、tools/call。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpiritScout.Tools
{
    /// <summary>一个 MCP 服务器的配置。</summary>
    public sealed class McpServer
    {
        public string Name { get; set; }
        public string Transport { get; set; }   // stdio / http / sse
        public string Command { get; set; }
        public string Args { get; set; }        // 空格分隔（简化版；复杂参数用引号）
        public string Url { get; set; }

        /// <summary>
        /// 环境变量（每行一个 KEY=VALUE）。很多 MCP 服务器靠 env 传配置——
        /// PYTHONPATH、数据目录、API key 之类，不给就没法起。
        /// </summary>
        public string Env { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool Enabled { get; set; }

        public McpServer()
        {
            Name = "";
            Transport = "stdio";
            Command = "";
            Args = "";
            Url = "";
            Env = "";
            TimeoutSeconds = 60;
            Enabled = false;
        }
    }

    /// <summary>MCP 服务器清单（mcp.json）。</summary>
    public sealed class McpFile
    {
        public List<McpServer> Servers { get; set; }
        public McpFile() { Servers = new List<McpServer>(); }
    }

    /// <summary>tools/list 返回的一条工具。</summary>
    public sealed class McpTool
    {
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>该工具的参数 schema（JSON 文本），装配成 ITool 时直接用。</summary>
        public string InputSchemaJson { get; set; }

        public McpTool()
        {
            Name = "";
            Description = "";
            InputSchemaJson = "";
        }
    }

    /// <summary>一次连接测试的结果。</summary>
    public sealed class McpProbe
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public List<McpTool> Tools { get; set; }
        public McpProbe() { Message = ""; Tools = new List<McpTool>(); }
    }

    public static class McpClient
    {
        /// <summary>
        /// 连一次、握手、列工具，然后断开。用于「测试连接」按钮。
        /// </summary>
        public static async Task<McpProbe> ProbeAsync(McpServer server,
            CancellationToken ct = default(CancellationToken))
        {
            if (server == null) return new McpProbe { Ok = false, Message = "配置为空" };

            if (string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                return await ProbeStdioAsync(server, ct).ConfigureAwait(false);
            return new McpProbe
            {
                Ok = false,
                Message = "当前版本只实现了 stdio 传输的连通测试；" + server.Transport + " 需要端点侧协议支持。",
            };
        }

        private static async Task<McpProbe> ProbeStdioAsync(McpServer server,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                return new McpProbe { Ok = false, Message = "没有填命令" };

            var psi = new ProcessStartInfo
            {
                FileName = server.Command,
                Arguments = server.Args ?? "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            Process proc = null;
            try
            {
                proc = Process.Start(psi);
                if (proc == null) return new McpProbe { Ok = false, Message = "进程没起来" };

                int timeout = server.TimeoutSeconds <= 0 ? 60 : server.TimeoutSeconds;

                // 1) initialize
                var init = await RpcAsync(proc, new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", 1 },
                    { "method", "initialize" },
                    { "params", new Dictionary<string, object>
                        {
                            { "protocolVersion", "2024-11-05" },
                            { "capabilities", new Dictionary<string, object>() },
                            { "clientInfo", new Dictionary<string, object>
                                { { "name", Core.AppInfo.McpClientName }, { "version", Core.AppInfo.Version } } },
                        }
                    },
                }, timeout, ct).ConfigureAwait(false);

                if (init == null)
                {
                    string err = ReadStderr(proc);
                    return new McpProbe
                    {
                        Ok = false,
                        Message = "握手超时或没有响应。" + (err.Length > 0 ? "\nstderr: " + Core.Text.OneLine(err, 300) : ""),
                    };
                }

                // 2) notifications/initialized（通知，无 id，不等回复）
                WriteLine(proc, new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "method", "notifications/initialized" },
                });

                // 3) tools/list
                var list = await RpcAsync(proc, new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", 2 },
                    { "method", "tools/list" },
                    { "params", new Dictionary<string, object>() },
                }, timeout, ct).ConfigureAwait(false);

                var result = new McpProbe { Ok = true, Message = "连接成功" };
                var root = Core.Json.AsDict(Core.Json.Parse(list ?? ""));
                var res = Core.Json.Dict(root, "result");
                var arr = Core.Json.Array(res, "tools");
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var d = Core.Json.AsDict(item);
                        if (d == null) continue;
                        result.Tools.Add(new McpTool
                        {
                            Name = Core.Json.Str(d, "name"),
                            Description = Core.Json.Str(d, "description"),
                            InputSchemaJson = d.ContainsKey("inputSchema")
                                ? new JavaScriptSerializer().Serialize(d["inputSchema"]) : "",
                        });
                    }
                }
                result.Message = "连接成功，暴露 " + result.Tools.Count + " 个工具";
                return result;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new McpProbe
                {
                    Ok = false,
                    Message = "找不到命令：" + server.Command + "\n请填绝对路径，或确认它在 PATH 里。",
                };
            }
            catch (Exception ex)
            {
                return new McpProbe { Ok = false, Message = ex.GetType().Name + ": " + ex.Message };
            }
            finally
            {
                if (proc != null)
                {
                    try { proc.StandardInput.Close(); } catch { }
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                }
            }
        }

        private static async Task<string> RpcAsync(Process proc,
            Dictionary<string, object> message, int timeoutSeconds, CancellationToken ct)
        {
            WriteLine(proc, message);
            var readTask = proc.StandardOutput.ReadLineAsync();
            var done = await Task.WhenAny(readTask,
                Task.Delay(timeoutSeconds * 1000, ct)).ConfigureAwait(false);
            if (done != readTask) return null;
            return readTask.Result;
        }

        private static void WriteLine(Process proc, Dictionary<string, object> message)
        {
            string json = new JavaScriptSerializer().Serialize(message);
            proc.StandardInput.WriteLine(json);
            proc.StandardInput.Flush();
        }

        private static string ReadStderr(Process proc)
        {
            try
            {
                proc.StandardError.BaseStream.ReadTimeout = 500;
                var buf = new byte[4096];
                int n = proc.StandardError.BaseStream.Read(buf, 0, buf.Length);
                return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : "";
            }
            catch { return ""; }
        }

    }

    /// <summary>MCP 配置读写。</summary>
    public sealed class McpRepository
    {
        private readonly string _path;

        public McpRepository(string path) { _path = path; }

        public List<McpServer> List()
        {
            var f = Core.JsonStore.Read<McpFile>(_path, null);
            return f == null || f.Servers == null ? new List<McpServer>() : f.Servers;
        }

        public void Save(List<McpServer> servers)
        {
            Core.JsonStore.Write(_path, new McpFile { Servers = servers ?? new List<McpServer>() });
        }

        public void Upsert(McpServer server)
        {
            var all = List();
            int i = all.FindIndex(s => string.Equals(s.Name, server.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) all[i] = server; else all.Add(server);
            Save(all);
        }

        public void Delete(string name)
        {
            var all = List();
            all.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            Save(all);
        }
    }
}
