// 灵枢桌面版 —— 智能体定义
//
// 每个 agent 是一份「角色卡」：代号、方向、什么时候用、提示词、能用哪些工具。
// 主 Agent（灵枢）拿着一份花名册做分派；编队页负责给它们指派模型。
//
// 加一个 agent = 在 AgentLibrary.Default() 里加一行；删一个 = 删那一行。
// 与 web 端的 profiles.py 对应，代号保持一致，方便对照。

using System;
using System.Collections.Generic;
using System.Linq;

namespace SpiritScout.Agents
{
    /// <summary>一个 agent 的角色定义。</summary>
    public sealed class AgentProfile
    {
        /// <summary>稳定键（master / ctf_web / …）。</summary>
        public string Key { get; set; }

        /// <summary>中文代号（灵枢 / 蛛丝 / 破壳 …）。</summary>
        public string Name { get; set; }

        /// <summary>一句话方向。</summary>
        public string Direction { get; set; }

        /// <summary>什么时候该派它上（主 Agent 据此选择）。</summary>
        public string WhenToUse { get; set; }

        /// <summary>系统提示词。</summary>
        public string SystemPrompt { get; set; }

        /// <summary>适用模式：src / ctf / competition / all。</summary>
        public string[] Modes { get; set; }

        /// <summary>可用工具名。空数组表示全部。</summary>
        public string[] Tools { get; set; }

        /// <summary>默认指派到哪个模型（空 = 用全局默认）。</summary>
        public string Model { get; set; }

        public AgentProfile()
        {
            Key = "";
            Name = "";
            Direction = "";
            WhenToUse = "";
            SystemPrompt = "";
            Modes = new[] { "all" };
            Tools = new string[0];
            Model = "";
        }

        public bool SupportsMode(string mode)
        {
            if (Modes == null || Modes.Length == 0) return true;
            foreach (var m in Modes)
            {
                if (string.Equals(m, "all", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 内置花名册。与 web 端的 profiles.py 对应。
    /// </summary>
    public static class AgentLibrary
    {
        private static List<AgentProfile> _default;

        /// <summary>基础提示词：所有 agent 共用这一段。</summary>
        private const string BasePrompt =
            "你在用户自己的 Windows 电脑上工作。\n"
            + "工作方式：先想清楚要验证什么，再用最少步骤去验证；"
            + "需要看文件、跑命令、发请求时直接用工具去做。\n"
            + "每次工具返回后先判断结果说明了什么再决定下一步；"
            + "同一条路走不通就换思路，不要连试几条同类命令（轮数有限）。\n"
            + "能一步做完的就别拆成三步——比如解码直接用 python 一行，不要反复试命令行工具。\n"
            + "给出结论时附上关键证据（命令输出、响应片段），不要空泛地说「已完成」。\n"
            + "shell 在用户本机真实执行，权限就是当前用户权限；"
            + "不要执行会破坏系统或删除用户数据的命令，除非用户明确要求。";

        public static List<AgentProfile> Default()
        {
            if (_default != null) return _default;

            var list = new List<AgentProfile>
            {
                new AgentProfile
                {
                    Key = "master",
                    Name = "灵枢",
                    Direction = "安全测试负责人：界定目标、编排任务、终审结论",
                    WhenToUse = "所有任务的总入口",
                    Modes = new[] { "all" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是总指挥。用户给一个目标，你要：\n"
                        + "1. 先把目标拆成几个可验证的小问题；\n"
                        + "2. 逐个验证，不要一次铺开所有方向；\n"
                        + "3. 拿到足够证据就收尾，不要无休止地试。\n"
                        + "如果需要专门的 agent（逆向、密码、Web）来做，就在回答里说明要派谁。",
                },
                new AgentProfile
                {
                    Key = "ctf_web",
                    Name = "蛛丝",
                    Direction = "CTF Web：从页面与流量证据推导解题路径",
                    WhenToUse = "目标是网站 / API / 前端应用",
                    Modes = new[] { "ctf", "competition" },
                    Tools = new[] { "http_request", "shell", "file", "note", "kb_search", "web_fetch", "web_search", "probe", "codec", "regex", "cipher", "hash", "base", "jwt", "bin", "payload", "img", "crackzip", "sql" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是 CTF Web 方向。原则：\n"
                        + "· 先把应用的正常功能完整走一遍，弄清它怎么用；\n"
                        + "· 从请求/响应里找异常，而不是从考点清单里猜；\n"
                        + "· 每个假设用最小对照实验验证（改一个变量，看响应差异）；\n"
                        + "· 拿到 flag 立刻提交，不要继续扩大攻击面。\n"
                        + "常见入口：参数注入、认证绕过、文件上传、模板注入、越权、反序列化。",
                },
                new AgentProfile
                {
                    Key = "ctf_reverse",
                    Name = "回溯",
                    Direction = "CTF 逆向：从文件结构、字符串、校验逻辑还原语义",
                    WhenToUse = "给了二进制 / APK / 字节码，要弄清它做什么",
                    Modes = new[] { "ctf", "competition" },
                    Tools = new[] { "shell", "file", "note", "kb_search", "bin", "hash", "base", "codec", "regex" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是逆向方向。原则：\n"
                        + "· 先看文件类型、架构、有没有加壳；\n"
                        + "· 从字符串和导入表找线索，再定位关键函数；\n"
                        + "· 静态看不懂就动态跑（用 shell 起调试/追踪）；\n"
                        + "· 不要在反汇编里逐行读，先找输入校验和比较逻辑。\n"
                        + "常用：file / strings / objdump / readelf / radare2 / python 模拟。",
                },
                new AgentProfile
                {
                    Key = "ctf_pwn",
                    Name = "破壳",
                    Direction = "CTF Pwn：保护机制、攻击面、利用原语",
                    WhenToUse = "给了可执行文件且有远程服务，要找内存破坏",
                    Modes = new[] { "ctf", "competition" },
                    Tools = new[] { "shell", "file", "note", "kb_search", "bin", "hash", "base", "codec", "regex" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是 Pwn 方向。原则：\n"
                        + "· 先查保护（checksec：canary / NX / PIE / RELRO）；\n"
                        + "· 找输入点和缓冲边界，确认能不能溢出；\n"
                        + "· 算清楚偏移，再决定用 ret2text / ret2libc / ROP；\n"
                        + "· 本地打通再打远程，注意 libc 版本差异。\n"
                        + "用 shell 跑 python 写 exploit（pwntools 语法即可）。",
                },
                new AgentProfile
                {
                    Key = "ctf_crypto",
                    Name = "译码",
                    Direction = "CTF 密码：编码层、算法结构、可验证的数学弱点",
                    WhenToUse = "给了密文 / 加密脚本 / 数学参数",
                    Modes = new[] { "ctf", "competition" },
                    Tools = new[] { "shell", "file", "note", "kb_search", "cipher", "hash", "base", "codec", "regex" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是密码方向。原则：\n"
                        + "· 先分清是编码、对称加密、非对称还是哈希；\n"
                        + "· 看参数选攻击（RSA 看 e/n 关系、AES 看模式和 IV 复用）；\n"
                        + "· 写代码验证，不要手算；\n"
                        + "· 古典密码先试频率分析和常见变形。\n"
                        + "用 shell 跑 python（sympy / pycryptodome 可用）。",
                },
                new AgentProfile
                {
                    Key = "ctf_misc",
                    Name = "拾遗",
                    Direction = "CTF Misc：文件、流量、图像、音频、多层容器",
                    WhenToUse = "附件是陌生格式，或题目说「杂项」",
                    Modes = new[] { "ctf", "competition" },
                    Tools = new[] { "shell", "file", "note", "kb_search", "img", "bin", "codec", "base", "crackzip", "cipher", "hash", "regex", "probe" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是 Misc 方向。原则：\n"
                        + "· 先 file 看真实类型，再 binwalk 看有没有嵌套；\n"
                        + "· 保留原始文件，每步解包都记下产物；\n"
                        + "· 隐写先看元数据、再看 LSB、再看附加数据；\n"
                        + "· 流量题先看协议分布，再追可疑会话。\n"
                        + "常用：file / binwalk / foremost / exiftool / tshark / python。",
                },
                new AgentProfile
                {
                    Key = "verifier",
                    Name = "鉴真",
                    Direction = "独立复测：排除误报，确认实际影响",
                    WhenToUse = "拿到一个结论但不确定真假时",
                    Modes = new[] { "all" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是复核员。你的职责是**证伪**而不是附和：\n"
                        + "· 假设上一个结论是错的，重新独立验证一遍；\n"
                        + "· 特别检查：是不是把正常行为当成了漏洞？"
                        + "是不是环境差异导致的假象？\n"
                        + "· 只保留你有独立证据支持的结论。",
                },
                new AgentProfile
                {
                    Key = "recon",
                    Name = "斥候",
                    Direction = "攻击面测绘：指纹、导航、表单、公开信息",
                    WhenToUse = "SRC 场景，先摸清目标有什么",
                    Modes = new[] { "src", "competition" },
                    Tools = new[] { "http_request", "shell", "note", "kb_search", "web_fetch", "web_search", "probe", "codec", "regex", "cipher", "hash", "base", "jwt", "bin", "payload", "img", "crackzip", "sql" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是侦察员。目标是把「目标有什么」摸清楚：\n"
                        + "· 首页、robots.txt、sitemap、常见备份路径；\n"
                        + "· 技术栈指纹（响应头、错误页、静态资源路径）；\n"
                        + "· 从页面里提取所有链接、表单、API 端点；\n"
                        + "· 记录资产，交给后续测试。\n"
                        + "不要在这个阶段就尝试攻击，先把面摸全。",
                },
                new AgentProfile
                {
                    Key = "web_vuln",
                    Name = "破壁",
                    Direction = "Web 漏洞验证：对已发现端点做最小对照实验",
                    WhenToUse = "侦察完成，要在具体端点上验证漏洞",
                    Modes = new[] { "src", "competition" },
                    Tools = new[] { "http_request", "shell", "note", "kb_search", "web_fetch", "web_search", "probe", "codec", "regex", "cipher", "hash", "base", "jwt", "bin", "payload", "img", "crackzip", "sql" },
                    SystemPrompt = BasePrompt
                        + "\n\n你是 Web 漏洞测试员。方法：\n"
                        + "· 每个漏洞当成待证伪的假设：先记基线，再单变量改动，再看差异；\n"
                        + "· 一次只改一个参数，否则分不清是哪个改动起的作用；\n"
                        + "· 确认能复现再下结论，附上请求与响应片段；\n"
                        + "· 不要拿扫描器结果当结论。",
                },
            };

            _default = list;
            return list;
        }
    }

    /// <summary>指派存储（agents.json）：各 agent 用哪个模型。</summary>
    public sealed class AgentAssignments
    {
        public Dictionary<string, string> Models { get; set; }

        public AgentAssignments()
        {
            Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>读取 / 保存指派。</summary>
    public sealed class AgentRepository
    {
        private readonly string _path;

        public AgentRepository(string path) { _path = path; }

        /// <summary>带指派信息的花名册。</summary>
        public List<AgentProfile> WithAssignments()
        {
            var list = AgentLibrary.Default();
            var saved = Core.JsonStore.Read<AgentAssignments>(_path, null);
            if (saved != null && saved.Models != null)
            {
                foreach (var a in list)
                {
                    string m;
                    if (saved.Models.TryGetValue(a.Key, out m)) a.Model = m ?? "";
                }
            }
            return list;
        }

        public void SetModel(string agentKey, string modelName)
        {
            var saved = Core.JsonStore.Read<AgentAssignments>(_path, null) ?? new AgentAssignments();
            if (saved.Models == null) saved.Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(modelName)) saved.Models.Remove(agentKey);
            else saved.Models[agentKey] = modelName;
            Core.JsonStore.Write(_path, saved);
        }

        public string ModelOf(string agentKey)
        {
            var saved = Core.JsonStore.Read<AgentAssignments>(_path, null);
            if (saved == null || saved.Models == null) return "";
            string m;
            return saved.Models.TryGetValue(agentKey, out m) ? (m ?? "") : "";
        }
    }
}
