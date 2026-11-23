// 灵枢桌面版 —— 编排器
//
// 主 Agent（灵枢）通过一个 `delegate` 工具把子任务派给某个方向 agent。
// 被派出去的子 agent 拿到自己的提示词与工具白名单，跑完把结论回给主 Agent。
//
// 为什么用「工具」而不是「硬编码流程」：
//   · 派谁、派几次、要不要派，交给模型判断——不同题目需要的人不一样；
//   · 主 Agent 的上下文里只留子 agent 的**结论**，不塞它跑工具的全过程，
//     否则主 Agent 很快就被日志淹掉；
//   · 加一个新方向 agent 只需要往花名册加一行，编排器不用改。
//
// 深度限制：子 agent 不能再派子 agent（Delegate 工具只给主 agent），
// 避免无限递归。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpiritScout.Agents;
using SpiritScout.Llm;
using SpiritScout.Tools;

namespace SpiritScout.Session
{
    /// <summary>delegate 工具：让主 Agent 指派子任务。</summary>
    public sealed class DelegateTool : ITool
    {
        private readonly Func<string, string, Task<ToolResult>> _dispatch;
        private readonly List<AgentProfile> _roster;

        public string Name { get { return "delegate"; } }

        public string Description
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("把子任务派给某个方向 agent，它跑完会把结论回给你。");
                sb.Append("可用方向：");
                foreach (var a in _roster)
                {
                    sb.Append("\n· ").Append(a.Key).Append("（").Append(a.Name).Append("）")
                      .Append("：").Append(a.Direction);
                }
                return sb.ToString();
            }
        }

        public string ParametersJson
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(@"{
  ""type"": ""object"",
  ""properties"": {
    ""agent"": { ""type"": ""string"", ""enum"": [");
                for (int i = 0; i < _roster.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('"').Append(_roster[i].Key).Append('"');
                }
                sb.Append(@"], ""description"": ""派给哪个方向"" },
    ""task"": { ""type"": ""string"", ""description"": ""给它的具体任务，写清要它验证什么、产出什么"" }
  },
  ""required"": [""agent"", ""task""]
}");
                return sb.ToString();
            }
        }

        public DelegateTool(List<AgentProfile> roster,
            Func<string, string, Task<ToolResult>> dispatch)
        {
            _roster = roster;
            _dispatch = dispatch;
        }

        public async Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string agent = Arg.Str(args, "agent");
            string task = Arg.Str(args, "task");
            if (string.IsNullOrWhiteSpace(agent)) return ToolResult.Fail("agent 不能为空");
            if (string.IsNullOrWhiteSpace(task)) return ToolResult.Fail("task 不能为空");
            return await _dispatch(agent, task).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 编排：给主 Agent 装上 delegate 工具，子 Agent 用自己的提示词与工具跑。
    /// </summary>
    public sealed class Orchestrator
    {
        private readonly Core.SessionStore _store;
        private readonly ModelRegistry _registry;
        private readonly ToolRegistry _baseTools;
        private readonly AgentRepository _agents;
        private readonly string _defaultModel;

        /// <summary>经验库（为 null 则不召回）。</summary>
        public Knowledge.ExperienceStore Experience { get; set; }

        /// <summary>每轮召回几条经验。</summary>
        public int RecallLimit { get; set; }

        /// <summary>单个子 agent 最多跑几轮（含工具调用）。</summary>
        public int SubAgentMaxRounds { get; set; }

        /// <summary>一次编排最多派几次子任务（防止主 Agent 无限分派）。</summary>
        public int MaxDelegations { get; set; }

        /// <summary>子 agent 开始时触发（界面可显示"蛛丝接手…"）。</summary>
        public event Action<string, string> SubAgentStarted;

        /// <summary>子 agent 结束时触发（agentKey, 是否成功, 摘要）。</summary>
        public event Action<string, bool, string> SubAgentFinished;

        private int _delegated;

        public Orchestrator(Core.SessionStore store, ModelRegistry registry,
            ToolRegistry baseTools, AgentRepository agents, string defaultModel)
        {
            _store = store;
            _registry = registry;
            _baseTools = baseTools;
            _agents = agents;
            _defaultModel = defaultModel;
            SubAgentMaxRounds = 8;
            MaxDelegations = 4;
            RecallLimit = 5;
            _uiContext = System.Threading.SynchronizationContext.Current;
        }

        /// <summary>
        /// 界面所在的同步上下文，构造时（UI 线程）捕获。
        ///
        /// 子 loop 是在**后台线程**创建的（`ConfigureAwait(false)` 之后），
        /// 那时 `SynchronizationContext.Current` 为 null —— 所以必须由这里
        /// 捕获再下发给每个 loop，不能指望 loop 自己拿到。
        /// </summary>
        private readonly System.Threading.SynchronizationContext _uiContext;

        /// <summary>
        /// 把事件送回 UI 线程再触发（同上：Post 不是 Send，避免死锁）。
        /// </summary>
        private void RaiseStart(string name, string task)
        {
            var h = SubAgentStarted;
            if (h == null) return;
            var ctx = _uiContext;
            if (ctx != null && ctx != System.Threading.SynchronizationContext.Current)
                ctx.Post(delegate { h(name, task); }, null);
            else
                h(name, task);
        }

        private void RaiseFinish(string name, bool ok, string summary)
        {
            var h = SubAgentFinished;
            if (h == null) return;
            var ctx = _uiContext;
            if (ctx != null && ctx != System.Threading.SynchronizationContext.Current)
                ctx.Post(delegate { h(name, ok, summary); }, null);
            else
                h(name, ok, summary);
        }

        /// <summary>为一次任务建主 Agent 的循环。</summary>
        public AgentLoop BuildMasterLoop(string sessionId, string mode)
        {
            var roster = _agents.WithAssignments();
            var master = FindByKey(roster, "master");
            var subs = new List<AgentProfile>();
            foreach (var a in roster)
            {
                if (a.Key == "master" || a.Key == "verifier") continue;
                if (a.SupportsMode(mode)) subs.Add(a);
            }

            // 主 Agent 的工具集 = 基础工具 + delegate
            var tools = new ToolRegistry();
            foreach (var t in _baseTools.All) tools.Register(t);
            tools.Register(new DelegateTool(subs, (key, task) => DispatchAsync(sessionId, key, task, roster, mode)));

            string model = ResolveModel(master, roster);
            var loop = new AgentLoop(_store, _registry, tools, model);
            // 子 loop 在后台线程建（父级 ConfigureAwait(false) 之后），
            // 这时 SynchronizationContext.Current 是 null —— 必须显式传下去，
            // 否则子 agent 的 ToolStarted 仍会在后台线程摸界面。
            loop.UiContext = _uiContext;
            loop.UiContext = _uiContext;     // 事件回到 UI 线程再触发
            loop.MaxToolRounds = 10;
            loop.Experience = Experience;
            loop.Mode = mode;
            loop.RecallLimit = RecallLimit;
            if (master != null && !string.IsNullOrEmpty(master.SystemPrompt))
                loop.SystemPrompt = master.SystemPrompt;

            return loop;
        }

        private async Task<ToolResult> DispatchAsync(string sessionId, string agentKey,
            string task, List<AgentProfile> roster, string mode)
        {
            if (_delegated >= MaxDelegations)
            {
                return ToolResult.Fail("本次任务已经派了 " + MaxDelegations
                    + " 次子任务，请根据已有结论自己收尾。");
            }

            var profile = FindByKey(roster, agentKey);
            if (profile == null) return ToolResult.Fail("没有这个方向：" + agentKey);

            _delegated++;
            RaiseStart(profile.Name, task);

            // 子 agent 用自己的工具白名单
            var tools = new ToolRegistry();
            if (profile.Tools == null || profile.Tools.Length == 0)
            {
                foreach (var t in _baseTools.All) tools.Register(t);
            }
            else
            {
                foreach (var name in profile.Tools)
                {
                    var t = _baseTools.Find(name);
                    if (t != null) tools.Register(t);
                }
            }

            string model = ResolveModel(profile, roster);
            var loop = new AgentLoop(_store, _registry, tools, model);
            loop.MaxToolRounds = SubAgentMaxRounds;
            loop.SystemPrompt = profile.SystemPrompt;
            loop.Experience = Experience;
            loop.Mode = mode;
            loop.RecallLimit = RecallLimit;

            // 子 agent 在独立会话文件里跑，避免污染主会话的上下文
            string subSessionId = sessionId + "-sub-" + profile.Key;
            EnsureSession(subSessionId, profile);

            try
            {
                var result = await loop.SendAsync(subSessionId, task).ConfigureAwait(false);
                bool ok = result.Ok && !result.HitRoundLimit;
                string summary = ok ? (result.Reply ?? "") : (result.Error ?? "失败");
                RaiseFinish(profile.Name, ok, Short(summary, 120));
                return new ToolResult
                {
                    Ok = ok,
                    Output = "[" + profile.Name + " 的结论]\n" + (result.Reply ?? result.Error ?? ""),
                    Summary = profile.Name + "：" + Short(summary, 100),
                };
            }
            catch (Exception ex)
            {
                var onEnd = SubAgentFinished;
                if (onEnd != null) onEnd(profile.Name, false, ex.Message);
                return ToolResult.Fail(profile.Name + " 执行失败：" + ex.Message);
            }
        }

        private void EnsureSession(string sessionId, AgentProfile profile)
        {
            var sf = _store.Load(sessionId);
            if (sf != null) return;
            sf = new Core.SessionFile();
            sf.Session.Id = sessionId;
            sf.Session.Target = "子任务 · " + profile.Name;
            sf.Session.Mode = Core.RunMode.Ctf;
            sf.Session.Phase = Core.SessionPhase.Executing;
            sf.Session.Title = profile.Name;
            sf.Session.CreatedAt = DateTime.Now;
            _store.Save(sf);
        }

        private string ResolveModel(AgentProfile profile, List<AgentProfile> roster)
        {
            if (profile != null && !string.IsNullOrEmpty(profile.Model)) return profile.Model;
            if (!string.IsNullOrEmpty(_defaultModel)) return _defaultModel;
            // 兜底：第一个启用的模型
            try
            {
                foreach (var m in _registry.Models())
                {
                    if (m.Enabled) return m.Name;
                }
            }
            catch { }
            return "";
        }

        private static AgentProfile FindByKey(List<AgentProfile> roster, string key)
        {
            foreach (var a in roster)
            {
                if (string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        private static string Short(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
