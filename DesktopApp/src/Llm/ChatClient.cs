// 灵枢桌面版 —— 模型调用
//
// 只实现两种协议，覆盖绝大多数端点：
//   * openai_compatible  → POST {base}/chat/completions   （方舟 / DeepSeek / Qwen / 本地 LM Studio…）
//   * anthropic_compatible → POST {base}/v1/messages       （Claude 系列）
//
// 刻意不做的事：
//   * 不做流式（第一版先跑通非流式；流式留到后面加 SSE 解析）
//   * 不内置任何厂商的默认端点或预置 key —— 用户自己填
//
// 错误一律转成可读中文：连不上、401、429、超时、返回体不是 JSON。

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpiritScout.Llm
{
    public enum ApiProtocol
    {
        OpenAiCompatible,
        AnthropicCompatible,
    }

    /// <summary>一个供应商（一个端点 + 一把密钥）。</summary>
    public sealed class ProviderSpec
    {
        public string Name { get; set; }          // 显示名，也是密钥的键
        public ApiProtocol Protocol { get; set; }
        public string BaseUrl { get; set; }       // 例：https://api.deepseek.com/v1

        public ProviderSpec()
        {
            Name = "";
            Protocol = ApiProtocol.OpenAiCompatible;
            BaseUrl = "";
        }
    }

    /// <summary>一次对话请求。</summary>
    public sealed class ChatRequest
    {
        public string Model { get; set; }
        public List<ChatMessage> Messages { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }
        public string SystemPrompt { get; set; }

        /// <summary>工具清单 JSON（OpenAI function calling 的 tools 数组）。空则不下发。</summary>
        public string ToolsJson { get; set; }

        public ChatRequest()
        {
            Model = "";
            Messages = new List<ChatMessage>();
            Temperature = 0.3;
            MaxTokens = 0;
            SystemPrompt = "";
        }
    }

    /// <summary>模型发起的一次工具调用。</summary>
    public sealed class ToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Arguments { get; set; }   // 原始 JSON 文本

        public ToolCall() { Id = ""; Name = ""; Arguments = ""; }
    }

    public sealed class ChatMessage
    {
        public string Role { get; set; }      // system / user / assistant / tool
        public string Content { get; set; }

        /// <summary>assistant 消息里携带的工具调用（回传给端点用）。</summary>
        public List<ToolCall> ToolCalls { get; set; }

        /// <summary>role=tool 时，对应哪一次调用。</summary>
        public string ToolCallId { get; set; }

        /// <summary>role=tool 时，工具名。</summary>
        public string ToolName { get; set; }

        public ChatMessage()
        {
            Role = "user";
            Content = "";
            ToolCalls = null;
            ToolCallId = "";
            ToolName = "";
        }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content ?? "";
            ToolCalls = null;
            ToolCallId = "";
            ToolName = "";
        }
    }

    /// <summary>一次对话结果。</summary>
    public sealed class ChatReply
    {
        public bool Ok { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public string RawJson { get; set; }
        public List<ToolCall> ToolCalls { get; set; }

        public int TotalTokens { get { return PromptTokens + CompletionTokens; } }

        public ChatReply() { Text = ""; Error = ""; RawJson = ""; }
    }

    /// <summary>模型调用客户端。</summary>
    public sealed class ChatClient : IDisposable
    {
        private readonly HttpClient _http;

        public ChatClient()
        {
            // 关键：.NET Framework 4.8 的默认协议在老系统上可能仍协商 TLS 1.0/1.1，
            // 而现在绝大多数 API（含 newapi）要求 TLS 1.2+，不设会直接
            // HttpRequestException（表现为「无法连接端点」）。
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#if NET48
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // Tls12 位
#endif
            }
            catch { }

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(180),
            };
            // 有些网关按 UA 拦截：默认的空 UA 会被 403（实测 newapi.zerohh.cn 就是这样）。
            // 带上产品标识既避免被挡，也方便对方在日志里认出调用方。
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SpiritScout/2.0");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }

        /// <summary>
        /// 发一个 GET 请求，返回原始响应文本。拿不到就返回空串（不抛）。
        ///
        /// 目前只用于 `GET /models` 拉模型列表。复用同一个 HttpClient 是为了
        /// 继承它已经设好的 TLS 1.2 与 User-Agent——有些网关按 UA 拦截，
        /// 新建一个 HttpClient 会重新踩一遍坑。
        /// </summary>
        public async Task<string> GetRawAsync(ProviderSpec provider, string apiKey,
            string path, CancellationToken ct = default(CancellationToken))
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.BaseUrl)) return "";
            try
            {
                string url = TrimEnd(provider.BaseUrl) + path;
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    if (provider.Protocol == ApiProtocol.AnthropicCompatible)
                    {
                        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    }

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            Core.ConfigManager.Log("GET " + url + " -> " + (int)resp.StatusCode
                                + "：" + Core.Json.Clip(body, 200));
                            return "";
                        }
                        return body;
                    }
                }
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("GET " + path + " 失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// 发一次对话。不会抛异常——把失败折进 ChatReply.Error，界面直接显示即可。
        /// </summary>
        public async Task<ChatReply> CompleteAsync(ProviderSpec provider, string apiKey,
            ChatRequest req, CancellationToken ct = default(CancellationToken))
        {
            if (provider == null) return Fail("供应商为空");
            if (string.IsNullOrWhiteSpace(provider.BaseUrl)) return Fail("供应商端点地址为空");
            if (string.IsNullOrWhiteSpace(apiKey)) return Fail("还没有填写这个供应商的 API Key");
            if (req == null || string.IsNullOrWhiteSpace(req.Model)) return Fail("没有指定模型");

            try
            {
                if (provider.Protocol == ApiProtocol.AnthropicCompatible)
                    return await CallAnthropicAsync(provider, apiKey, req, ct).ConfigureAwait(false);
                return await CallOpenAiAsync(provider, apiKey, req, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return Fail("请求超时（180 秒）。端点可能不可达，或模型响应太慢。");
            }
            catch (HttpRequestException ex)
            {
                return Fail("无法连接端点：" + ex.Message
                    + "\n\n检查：① 地址是否写全（通常以 /v1 结尾）② 是否需要代理 ③ 网络是否通");
            }
            catch (Exception ex)
            {
                return Fail("调用失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------- OpenAI 兼容 ----------

        private async Task<ChatReply> CallOpenAiAsync(ProviderSpec provider, string apiKey,
            ChatRequest req, CancellationToken ct)
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(req.SystemPrompt))
                messages.Add(new Dictionary<string, object> { { "role", "system" }, { "content", req.SystemPrompt } });
            foreach (var m in req.Messages)
            {
                var d = new Dictionary<string, object> { { "role", m.Role } };

                if (m.Role == "tool")
                {
                    // 工具结果：OpenAI 要求 role=tool + tool_call_id，content 是字符串
                    d["content"] = m.Content ?? "";
                    d["tool_call_id"] = m.ToolCallId ?? "";
                }
                else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    // assistant 发起工具调用：content 可为 null，但 tool_calls 必须原样带回去
                    d["content"] = string.IsNullOrEmpty(m.Content) ? null : m.Content;
                    var calls = new List<object>();
                    foreach (var c in m.ToolCalls)
                    {
                        calls.Add(new Dictionary<string, object>
                        {
                            { "id", c.Id },
                            { "type", "function" },
                            { "function", new Dictionary<string, object>
                                { { "name", c.Name }, { "arguments", c.Arguments } } },
                        });
                    }
                    d["tool_calls"] = calls;
                }
                else
                {
                    d["content"] = m.Content ?? "";
                }
                messages.Add(d);
            }

            var body = new Dictionary<string, object>
            {
                { "model", req.Model },
                { "messages", messages },
                { "temperature", req.Temperature },
            };
            if (req.MaxTokens > 0) body["max_tokens"] = req.MaxTokens;

            // 工具清单：只在调用方给了才下发，避免端点不认识这个字段时报错
            if (!string.IsNullOrWhiteSpace(req.ToolsJson))
            {
                try
                {
                    body["tools"] = new JavaScriptSerializer()
                        .DeserializeObject(req.ToolsJson);
                    body["tool_choice"] = "auto";
                }
                catch (Exception ex)
                {
                    // 工具定义序列化失败时**不能静默跳过**：模型会拿不到任何工具，
                    // 表现为「它只会空口说话」，而且没有任何提示。
                    // MCP 工具的 schema 来自外部，格式未必合法，这条路径真会走到。
                    Core.ConfigManager.Log("工具定义无效，本次请求不附带 tools："
                        + ex.GetType().Name + ": " + ex.Message
                        + " | 片段: " + Core.Json.Clip(req.ToolsJson, 300));
                }
            }

            string url = TrimEnd(provider.BaseUrl) + "/chat/completions";
            var reply = await PostJsonAsync(url, apiKey, body, false, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;

            var root = Parse(reply.RawJson);
            if (root == null) return Fail("端点返回的不是 JSON：\n" + Preview(reply.RawJson));

            // 标准结构：choices[0].message.content
            var list = GetArray(root, "choices");
            if (list != null && list.Count > 0)
            {
                var first = list[0] as Dictionary<string, object>;
                var msg = first == null ? null : GetDict(first, "message");
                if (msg != null)
                {
                    reply.Text = GetStr(msg, "content");

                    // 工具调用
                    var tcs = GetArray(msg, "tool_calls");
                    if (tcs != null && tcs.Count > 0)
                    {
                        reply.ToolCalls = new List<ToolCall>();
                        foreach (var item in tcs)
                        {
                            var td = item as Dictionary<string, object>;
                            if (td == null) continue;
                            var fn = GetDict(td, "function");
                            reply.ToolCalls.Add(new ToolCall
                            {
                                Id = GetStr(td, "id"),
                                Name = fn == null ? "" : GetStr(fn, "name"),
                                Arguments = fn == null ? "{}" : GetStr(fn, "arguments"),
                            });
                        }
                    }

                    if (string.IsNullOrEmpty(reply.Text) && reply.ToolCalls == null)
                    {
                        var alt = GetStr(msg, "reasoning_content");
                        if (!string.IsNullOrEmpty(alt)) reply.Text = alt;
                    }
                }

                // 有工具调用就算成功（正文可以为空）
                bool hasTools = reply.ToolCalls != null && reply.ToolCalls.Count > 0;
                if (string.IsNullOrEmpty(reply.Text) && !hasTools)
                {
                    reply.Error = "端点返回了空内容。原始响应：\n" + Preview(reply.RawJson);
                    reply.Ok = false;
                }
            }
            else
            {
                // 有些端点出错时仍返回 200，错误放在 error 字段
                var err = GetDict(root, "error");
                reply.Ok = false;
                reply.Error = err != null
                    ? "端点报错：" + GetStr(err, "message")
                    : "响应里没有 choices。原始响应：\n" + Preview(reply.RawJson);
            }

            var usage = GetDict(root, "usage");
            if (usage != null)
            {
                reply.PromptTokens = GetInt(usage, "prompt_tokens");
                reply.CompletionTokens = GetInt(usage, "completion_tokens");
            }
            return reply;
        }

        // ---------- Anthropic ----------

        private async Task<ChatReply> CallAnthropicAsync(ProviderSpec provider, string apiKey,
            ChatRequest req, CancellationToken ct)
        {
            var messages = new List<object>();
            foreach (var m in req.Messages)
            {
                if (m.Role == "system") continue;   // Anthropic 的系统提示走独立字段
                messages.Add(new Dictionary<string, object> { { "role", m.Role }, { "content", m.Content } });
            }

            var body = new Dictionary<string, object>
            {
                { "model", req.Model },
                { "messages", messages },
                // Anthropic 必填 max_tokens
                { "max_tokens", req.MaxTokens > 0 ? req.MaxTokens : 4096 },
                { "temperature", req.Temperature },
            };
            if (!string.IsNullOrWhiteSpace(req.SystemPrompt)) body["system"] = req.SystemPrompt;

            string url = TrimEnd(provider.BaseUrl) + "/v1/messages";
            var reply = await PostJsonAsync(url, apiKey, body, true, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;

            var root = Parse(reply.RawJson);
            if (root == null) return Fail("端点返回的不是 JSON：\n" + Preview(reply.RawJson));

            var content = GetArray(root, "content");
            if (content != null)
            {
                var sb = new StringBuilder();
                foreach (var block in content)
                {
                    var d = block as Dictionary<string, object>;
                    if (d == null) continue;
                    if (GetStr(d, "type") == "text") sb.Append(GetStr(d, "text"));
                }
                reply.Text = sb.ToString();
            }
            if (string.IsNullOrEmpty(reply.Text))
            {
                var err = GetDict(root, "error");
                reply.Ok = false;
                reply.Error = err != null
                    ? "端点报错：" + GetStr(err, "message")
                    : "响应里没有 text 块。原始响应：\n" + Preview(reply.RawJson);
            }

            var usage = GetDict(root, "usage");
            if (usage != null)
            {
                reply.PromptTokens = GetInt(usage, "input_tokens");
                reply.CompletionTokens = GetInt(usage, "output_tokens");
            }
            return reply;
        }

        // ---------- HTTP ----------

        private async Task<ChatReply> PostJsonAsync(string url, string apiKey,
            Dictionary<string, object> body, bool anthropic, CancellationToken ct)
        {
            string json = new JavaScriptSerializer().Serialize(body);
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (anthropic)
                {
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                }
                else
                {
                    req.Headers.Add("Authorization", "Bearer " + apiKey);
                }

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        int code = (int)resp.StatusCode;
                        string hint;
                        switch (code)
                        {
                            case 401: hint = "密钥无效或已过期（401）"; break;
                            case 403: hint = "密钥没有该模型的权限（403）"; break;
                            case 404: hint = "端点路径不对（404）——检查 BaseUrl 是否漏了 /v1"; break;
                            case 429: hint = "触发限流或额度用尽（429）"; break;
                            case 500:
                            case 502:
                            case 503: hint = "端点侧故障（" + code + "），稍后再试"; break;
                            default: hint = "HTTP " + code; break;
                        }
                        return Fail(hint + "\n\n端点原文：\n" + Preview(text));
                    }
                    return new ChatReply { Ok = true, RawJson = text };
                }
            }
        }

        // ---------- 小工具 ----------

        private static string TrimEnd(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.TrimEnd('/');
        }

        private static ChatReply Fail(string message)
        {
            return new ChatReply { Ok = false, Error = message ?? "未知错误" };
        }

        private static Dictionary<string, object> Parse(string json)
        {
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }
                    .Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string Preview(string s)
        {
            if (string.IsNullOrEmpty(s)) return "（空）";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 400 ? s : s.Substring(0, 400) + "…";
        }

        private static T Get<T>(Dictionary<string, object> d, string key) where T : class
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as T : null;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            return Get<Dictionary<string, object>>(d, key);
        }

        /// <summary>
        /// 取一个数组字段。
        /// **注意**：JavaScriptSerializer 把 JSON 数组反序列化成 `object[]`，
        /// 不是 `List&lt;object&gt;`。只判 IList 会永远是 null（踩过：响应明明正确，
        /// 却报「没有 choices」）。这里统一成 List。
        /// </summary>
        private static List<object> GetArray(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            var list = v as System.Collections.IEnumerable;
            if (list == null || v is string) return null;
            var result = new List<object>();
            foreach (var item in list) result.Add(item);
            return result;
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null) return "";
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            return v.ToString();
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            int n;
            return int.TryParse(v.ToString(), out n) ? n : 0;
        }

        public void Dispose()
        {
            try { _http.Dispose(); } catch { }
        }
    }
}
