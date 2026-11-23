// 灵枢桌面版 —— 工具调用 + 模型对话的粘合层
//
// 职责：把「模型说要用某个工具」这件事变成「真的执行并回灌结果」，
// 直到模型给出最终答复。
//
// 循环结构：
//   问模型 → 有 tool_calls？
//     ├─ 有：逐个执行 → 把 (assistant.tool_calls + tool 结果) 追加进消息 →
//     │       再来一轮（最多 MaxToolRounds 轮，防止模型死循环）
//     └─ 没有：这就是最终答复，收工
//
// 工具调用的状态通过回调抛给界面（ToolStarted / ToolFinished），
// 这样聊天窗口里能看到「正在执行 shell ...」这类实时反馈。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpiritScout.Llm;
using SpiritScout.Tools;

namespace SpiritScout.Session
{
    /// <summary>工具调用事件。</summary>
    public sealed class ToolEvent
    {
        public string Name { get; set; }
        public string Arguments { get; set; }
        public bool Ok { get; set; }
        public string Summary { get; set; }
        public ToolEvent() { Name = ""; Arguments = ""; Summary = ""; }
    }

    /// <summary>一轮完整交互（可能包含多次工具调用）的结果。</summary>
    public sealed class AgentTurnResult
    {
        public bool Ok { get; set; }
        public string Reply { get; set; }
        public string Error { get; set; }
        public int Tokens { get; set; }
        public int ToolCalls { get; set; }
        public bool HitRoundLimit { get; set; }

        public AgentTurnResult() { Reply = ""; Error = ""; }
    }

    public sealed class AgentLoop
    {
        private readonly Core.SessionStore _store;
        private readonly ModelRegistry _registry;
        private readonly ToolRegistry _tools;
        private readonly string _modelName;

        /// <summary>最多几轮工具调用（超过就强制收尾，避免模型反复试同一个工具）。</summary>
        public int MaxToolRounds { get; set; }

        /// <summary>最多带多少条历史消息。</summary>
        public int MaxHistoryMessages { get; set; }

        /// <summary>这一轮发给模型的历史总字符上限（约 token × 3）。</summary>
        public int MaxSliceChars { get; set; }

        /// <summary>单条消息的字符上限，超了截断并留标记。</summary>
        public int MaxSingleMessageChars { get; set; }

        public string SystemPrompt { get; set; }

        /// <summary>经验库（为 null 则不召回）。</summary>
        public Knowledge.ExperienceStore Experience { get; set; }

        /// <summary>本轮任务模式（src/ctf/competition），用于按类召回经验。</summary>
        public string Mode { get; set; }

        /// <summary>每轮召回几条经验（0 = 关闭召回）。</summary>
        public int RecallLimit { get; set; }

        /// <summary>工具开始执行（界面可显示"正在运行 xxx"）。</summary>
        public event Action<ToolEvent> ToolStarted;

        /// <summary>工具执行结束。</summary>
        public event Action<ToolEvent> ToolFinished;

        /// <summary>
        /// 界面所在的同步上下文。**事件必须回到这个上下文再触发。**
        ///
        /// 2026-11-20 审计发现：`SendAsync` 里有 74 处 `ConfigureAwait(false)`，
        /// 从第 170 行那次 await 之后，**整段代码都在线程池**。而 `ToolStarted`
        /// 就在那之后触发，界面处理器里直接 `Controls.Add` —— 必然抛
        /// 「线程间操作无效」。e2e 测的是 AgentLoop 本身（无 UI），所以没暴露。
        ///
        /// 由 Orchestrator 在 UI 线程构造时捕获并下发；子 loop 在后台创建,
        /// 那时 SynchronizationContext.Current 是 null，只能靠父级传。
        /// </summary>
        public System.Threading.SynchronizationContext UiContext { get; set; }

        /// <summary>
        /// 把事件送回 UI 线程再触发。没有上下文（命令行 / 测试）时直接触发。
        ///
        /// 用 Post 不用 Send：Send 会等 UI 线程处理完，而后台线程正被 UI 线程
        /// 用 await 等着 —— 那是死锁的经典形态。
        /// </summary>
        private void Raise(Action<ToolEvent> handler, ToolEvent ev)
        {
            if (handler == null) return;
            var ctx = UiContext;
            if (ctx != null && ctx != System.Threading.SynchronizationContext.Current)
                ctx.Post(delegate { handler(ev); }, null);
            else
                handler(ev);
        }

        public AgentLoop(Core.SessionStore store, ModelRegistry registry,
            ToolRegistry tools, string modelName)
        {
            _store = store;
            _registry = registry;
            _tools = tools;
            _modelName = modelName;
            MaxToolRounds = 8;
            MaxHistoryMessages = 40;
            MaxSliceChars = 60000;          // 约 2 万 token，给响应和工具定义留足空间
            MaxSingleMessageChars = 16000;  // 单条约 5 千 token
            SystemPrompt = DefaultSystemPrompt();
            Mode = "ctf";
            RecallLimit = 5;
        }

        public static string DefaultSystemPrompt()
        {
            return "你是「灵枢」，一个安全测试与 CTF 解题助手，运行在用户自己的 Windows 电脑上。\n\n"
                 + "工作方式：\n"
                 + "· 先想清楚要验证什么，再用最少的步骤去验证；能一条命令说清的就不要拆成三条。\n"
                 + "· 需要看文件、跑命令、发请求时，直接用提供的工具去做，不要只描述「你可以运行…」。\n"
                 + "· 每次工具返回后先判断结果说明了什么，再决定下一步；"
                 + "同一条路走不通就换思路，不要连试几条同类命令（轮数有限）。\n"
                 + "· 能一步做完的就别拆成三步——比如解码直接用 python 一行，不要反复试命令行工具。\n"
                 + "· 拿到结论再回答，回答里给出关键证据（命令输出、响应片段），不要空泛地说「已完成」。\n\n"
                 + "注意：shell 工具在用户本机真实执行，权限就是当前用户权限；"
                 + "不要执行会破坏系统或删除用户数据的命令，除非用户明确要求。";
        }

        /// <summary>发一轮（含工具循环）。</summary>
        public async Task<AgentTurnResult> SendAsync(string sessionId, string userText,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(userText))
                return new AgentTurnResult { Ok = false, Error = "输入为空" };

            var sf = _store.Load(sessionId);
            if (sf == null) return new AgentTurnResult { Ok = false, Error = "会话不存在" };

            // 用户消息落盘
            _store.Append(sessionId, "user", userText);

            int totalTokens = 0;
            int toolCallCount = 0;

            // 取消检查。ChatClient 会把 OperationCanceledException 当成普通的
            // 「调用失败」吞掉（返回 Ok=false），那样 UI 只会显示「异常：」，
            // 也分不清是用户主动停的还是网络出错。这里在每轮开头显式检查，
            // 让取消能以 OperationCanceledException 的形式传到界面上。
            Action checkCancel = () =>
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            };
            checkCancel();

            // 组装上下文
            var messages = BuildContext(sessionId);
            string toolsJson = _tools.Count > 0 ? _tools.BuildToolsJson() : null;

            // 经验召回：按这一轮的输入挑几条同类场景的信号，拼进系统提示词。
            // 这是「越用越聪明」真正生效的地方——提炼进库的经验只有被喂回来才有用。
            string systemPrompt = SystemPrompt;
            Knowledge.RecallResult recalled = null;
            if (Experience != null && RecallLimit > 0)
            {
                recalled = Knowledge.ExperienceRecall.Build(
                    Experience, userText, Mode, RecallLimit);
                if (recalled.HasAny)
                {
                    systemPrompt = SystemPrompt + recalled.Block;
                    Core.ConfigManager.Log("召回经验 " + recalled.Items.Count
                        + " 条 / " + recalled.Chars + " 字（会话 " + sessionId + "）");
                }
            }

            for (int round = 0; round < MaxToolRounds; round++)
            {
                checkCancel();

                var req = new ChatRequest
                {
                    Messages = messages,
                    SystemPrompt = systemPrompt,
                    Temperature = 0.3,
                    ToolsJson = toolsJson,
                };
                var reply = await _registry.ChatAsync(_modelName, req, ct).ConfigureAwait(false);

                if (!reply.Ok)
                {
                    // 取消导致的失败要抛出去，不能当成普通错误
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);

                    _store.Append(sessionId, "system", "调用失败：" + Core.Text.OneLine(reply.Error), "turn");
                    return new AgentTurnResult
                    {
                        Ok = false,
                        Error = reply.Error,
                        Tokens = totalTokens,
                        ToolCalls = toolCallCount,
                    };
                }

                totalTokens += reply.TotalTokens;

                // 没有工具调用 → 这就是最终答复
                if (reply.ToolCalls == null || reply.ToolCalls.Count == 0)
                {
                    string text = reply.Text ?? "";
                    // 一次写盘：追加消息 + 累加 token。
                    // 拆成 Append + AddTokens 是两次完整读写（1000 条会话时各 17ms）。
                    _store.AppendWithTokens(sessionId, "assistant", text, totalTokens);
                    // 跑完了把 Phase 写回 Completed —— 任务坞靠它判断「哪些还在后台」。
                    // 之前只在开始时置 Executing，成功路径没置回，会一直挂在「执行中」。
                    try
                    {
                        var sf2 = _store.Load(sessionId);
                        if (sf2 != null && sf2.Session != null
                            && sf2.Session.Phase == Core.SessionPhase.Executing)
                        {
                            sf2.Session.Phase = Core.SessionPhase.Completed;
                            _store.Save(sf2);
                        }
                    }
                    catch { }
                    return new AgentTurnResult
                    {
                        Ok = true,
                        Reply = text,
                        Tokens = totalTokens,
                        ToolCalls = toolCallCount,
                    };
                }

                // 有工具调用：先把 assistant 的 tool_calls 记进上下文
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = reply.Text ?? "",
                    ToolCalls = reply.ToolCalls,
                });

                // 逐个执行
                foreach (var call in reply.ToolCalls)
                {
                    toolCallCount++;
                    var tool = _tools.Find(call.Name);
                    var started = new ToolEvent { Name = call.Name, Arguments = call.Arguments };
                    Raise(ToolStarted, started);

                    ToolResult result;
                    if (tool == null)
                    {
                        result = ToolResult.Fail("没有这个工具：" + call.Name);
                    }
                    else
                    {
                        var args = ParseArgs(call.Arguments);
                        try
                        {
                            result = await tool.InvokeAsync(args, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            result = ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
                        }
                    }

                    var finished = new ToolEvent
                    {
                        Name = call.Name,
                        Arguments = call.Arguments,
                        Ok = result.Ok,
                        Summary = result.Summary,
                    };
                    Raise(ToolFinished, finished);

                    // 工具结果回灌
                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        Content = result.Output ?? "",
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                    });

                    // 落盘（界面上能看到工具做了什么）
                    _store.Append(sessionId, "tool",
                        "[" + call.Name + "] " + (result.Summary ?? ""),
                        call.Name);
                }
            }

            // 轮数用完还没收敛
            _store.AddTokens(sessionId, totalTokens);
            return new AgentTurnResult
            {
                Ok = false,
                Error = "工具调用轮数已达上限（" + MaxToolRounds + " 轮），可能存在循环。",
                Tokens = totalTokens,
                ToolCalls = toolCallCount,
                HitRoundLimit = true,
            };
        }

        // ---------- 上下文 ----------

        /// <summary>
        /// 拼出这一轮要发给模型的历史。
        ///
        /// 两个闸门，缺一不可：
        ///
        /// **条数**（MaxHistoryMessages）：挡掉太老的对话，省 token。
        ///
        /// **字符预算**（MaxSliceChars）：挡掉太长的内容。
        /// 实测：用户粘一段 100KB 日志之后又聊了 39 轮，那 100KB 落进最后 40 条里，
        /// 每轮请求都把它整个发出去 —— 8.3 万字符约 2.8 万 token，32k 上下文的
        /// 模型直接贴到上限。条数限制对这个完全无效，因为**它只是一条消息**。
        ///
        /// 做法是从**最新往最旧**累加，超预算就停。这样丢掉的永远是最老的，
        /// 最近几轮的完整上下文一定保留。
        ///
        /// 单条也设了上限（MaxSingleMessageChars）：一条 500KB 的粘贴光它自己
        /// 就超预算，那不管怎么切都放不进去，只能截断并留个标记告诉模型。
        /// </summary>
        private List<ChatMessage> BuildContext(string sessionId)
        {
            var sf = _store.Load(sessionId);
            var messages = new List<ChatMessage>();
            if (sf == null) return messages;

            int tail = Math.Max(2, MaxHistoryMessages);
            int from = Math.Max(0, sf.Messages.Count - tail);

            long budget = Math.Max(2000, MaxSliceChars);
            long used = 0;

            // 从最新往最旧走
            for (int i = sf.Messages.Count - 1; i >= from; i--)
            {
                var m = sf.Messages[i];
                if (m.Role != "user" && m.Role != "assistant") continue;

                string text = m.Content ?? "";
                if (text.Length > MaxSingleMessageChars)
                {
                    text = text.Substring(0, MaxSingleMessageChars)
                         + "\n\n…（内容过长已截断，原文 " + (m.Content ?? "").Length + " 字符）";
                }

                // 预算用完就停（但至少留最近一条，免得空历史）
                if (messages.Count > 0 && used + text.Length > budget) break;

                messages.Add(new ChatMessage(m.Role, text));
                used += text.Length;
            }

            messages.Reverse();   // 恢复时间顺序
            return messages;
        }

        private static IDictionary<string, object> ParseArgs(string json)
        {
            var empty = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(json)) return empty;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
                return d ?? empty;
            }
            catch
            {
                return empty;
            }
        }

    }
}
