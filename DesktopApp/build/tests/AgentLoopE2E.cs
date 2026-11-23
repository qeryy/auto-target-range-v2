// 灵枢桌面版 —— AgentLoop 端到端测试
//
// ## 为什么需要它
//
// 其它测试都是**零件级**的：工具单独能跑、页面单独能开、快捷键单独能跳。
// 但「模型说要调工具 → 工具真跑了 → 结果真回传 → 模型基于结果作答 →
// 会话真落盘」这条**整机链路**，之前从没验证过 —— 因为它需要真实模型 API。
//
// 这里的办法：起一个**假的 OpenAI 兼容服务**（TcpListener 手写 HTTP），
// 按轮次返回不同内容：
//   第 1 轮 → 返回 tool_calls，要求调用 file 工具读一个已知文件
//   第 2 轮 → 看到请求里带了工具结果，就给出最终结论
//
// 这样任何一环断掉测试都会挂：上下文构建、协议解析、工具分发、结果回传、
// 循环控制、会话落盘。
//
// ## 两次踩坑（都出在脚手架，不在产品代码）
//
// 1. **Content-Length 是字节数，不是字符数**。
//    第一版用 `while (sb.Length < cl)` 等请求体，而请求里有中文 system prompt
//    （UTF-8 字节数远大于字符数）→ 永远等不满 → 服务卡死 → 客户端 180 秒超时。
//    改成用 MemoryStream 按字节累积。
//
// 2. **手工拼 JSON 的转义层级算错**。
//    Windows 路径 `C:\Users\...` 在 JSON 字符串里要双重转义，手写
//    `Replace("\\", "\\\\")` 少了一层，模型收到的路径是双反斜杠 → 文件不存在
//    → 工具报错。改用 `JavaScriptSerializer` 构造整个响应就对了。
//
// 结论：**能用库就别手写**。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SpiritScout.Core;
using SpiritScout.Llm;
using SpiritScout.Session;
using SpiritScout.Tools;
using SpiritScout.Ui;

class AgentLoopE2E
{
    static int pass = 0, fail = 0;
    static int callCount = 0;
    static readonly object gate = new object();

    const string ExpectedFlag = "flag{agent_loop_works}";

    [STAThread]
    static int Main(string[] a)
    {
        AppPaths.Override(a[0], "e2e");
        Directory.CreateDirectory(a[0]);

        // 让假模型去读的目标文件
        string target = Path.Combine(a[0], "secret.txt");
        File.WriteAllText(target, ExpectedFlag + "\n", new UTF8Encoding(false));

        // ---- 假 LLM 服务 ----
        int port = 19200 + new Random().Next(500);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var th = new Thread(delegate() { Serve(listener, target); });
        th.IsBackground = true;
        th.Start();
        Thread.Sleep(300);
        Console.WriteLine("  假 LLM 服务: http://127.0.0.1:" + port + "/v1");

        // ---- 配置 provider / model / key ----
        var secrets = new SecretStore(AppPaths.SecretsFile);
        var reg = new ModelRegistry(AppPaths.ModelsFile, secrets);
        reg.UpsertProvider(new ProviderSpec
        {
            Name = "fake",
            Protocol = ApiProtocol.OpenAiCompatible,
            BaseUrl = "http://127.0.0.1:" + port + "/v1",
        });
        reg.UpsertModel(new ModelSpec
        {
            Name = "fake-model",
            ProviderName = "fake",
            ModelId = "fake-model",
            ContextWindow = 8192,
            Enabled = true,
        });
        reg.SetKey("fake", "test-key");

        // ---- 会话 ----
        var store = new SessionStore();
        var sf = new SessionFile();
        sf.Session.Id = "e2e";
        sf.Session.Target = "端到端测试";
        sf.Session.Mode = RunMode.Ctf;
        sf.Session.Phase = SessionPhase.Pending;
        sf.Session.CreatedAt = DateTime.Now;
        sf.Session.UpdatedAt = DateTime.Now;
        store.Save(sf);

        // ---- 导出与脱敏（纯函数，不依赖数据目录）----
        TestExport();

        // ---- Markdown 分段（纯函数）----
        TestMarkdown();

        // ---- 工具消息解析（纯函数）----
        TestSessionToolMessage();

        // ---- 会话统计：时间过滤 / 归桶 / 按模式聚合（纯函数）----
        TestSessionStats();

        // ---- 关键词命中（三个页面的筛选共用）----
        TestTextHit();

        // ---- 经验查重：相似度 + 并查集分组（纯函数）----
        TestExperienceDedup();

        // ---- kb_search（纯函数，只依赖内嵌技能库）----
        TestKbSearch();

        // ---- note + http_request（起本地假服务）----
        TestNoteAndHttp();

        // ---- shell + probe（真跑命令 / 扫本机回环）----
        TestShellAndProbe();

        // ---- 跑一轮 ----
        var loop = new AgentLoop(store, reg, ToolRegistry.CreateDefault(), "fake-model");
        var events = new List<string>();
        loop.ToolStarted += delegate(ToolEvent e) { lock (gate) events.Add("start:" + e.Name); };
        loop.ToolFinished += delegate(ToolEvent e)
        {
            lock (gate) events.Add("end:" + e.Name + ":" + (e.Ok ? "ok" : "fail"));
        };

        AgentTurnResult res = null;
        try
        {
            res = loop.SendAsync("e2e", "读 secret.txt 告诉我内容", CancellationToken.None)
                       .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ 抛异常: " + ex.GetType().Name + ": " + ex.Message);
            fail++;
        }

        Console.WriteLine();
        if (res != null)
        {
            Report("SendAsync 成功", res.Ok, res.Error);
            Report("回复含 " + ExpectedFlag,
                (res.Reply ?? "").IndexOf(ExpectedFlag, StringComparison.Ordinal) >= 0,
                "回复=" + Clip(res.Reply, 250));
            Report("token 统计有值", res.Tokens > 0, "Tokens=" + res.Tokens);
            Report("工具调用次数 >= 1", res.ToolCalls >= 1, "ToolCalls=" + res.ToolCalls);
        }
        Report("file 工具执行成功", events.Contains("end:file:ok"), Join(events));

        // ---- 会话落盘 ----
        Console.WriteLine();
        var loaded = new SessionStore().LoadFresh("e2e");
        var roles = new List<string>();
        if (loaded != null) foreach (var m in loaded.Messages) roles.Add(m.Role);
        Console.WriteLine("  消息序列: " + string.Join(" → ", roles.ToArray()));
        Report("有 tool 角色的消息", roles.Contains("tool"), "");
        Report("有 assistant 角色的消息", roles.Contains("assistant"), "");

        // ---- 请求轮数 ----
        Console.WriteLine();
        Report("假服务被调 2 次（工具轮 + 结论轮）", callCount == 2, "实际 " + callCount);

        listener.Stop();

        // **放在最后**：TestFirstRun 内部会 AppPaths.Override 换数据目录，
        // 提前跑的话上面那些会话读不到（踩过一次）。
        TestFirstRun(a[0]);

        // 按钮接线（同样放在最后，避免 AppPaths 被提前改）
        TestWiring(a[0]);

        Console.WriteLine();
        Console.WriteLine("  合计: " + pass + " PASS / " + fail + " FAIL");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// `shell` 与 `probe`。两个都有副作用，所以用最保守的测法：
    /// `shell` 只跑 `echo` / `exit N` / 不存在的命令；`probe` 只扫 127.0.0.1。
    ///
    /// `probe` 这组验的是**输出信息完整性**：只列开放端口是对的（跟 nmap 一致），
    /// 但用户指定了 N 个只看到 M 个时，要交代剩下的是「关了」还是「超时」。
    /// </summary>
    static void TestShellAndProbe()
    {
        Console.WriteLine();
        Console.WriteLine("  -- shell + probe --");

        var sh = new SpiritScout.Tools.ShellTool();

        var s1 = ShellCall(sh, "echo hello-ss");
        Report("shell echo 有输出", s1.Ok && (s1.Output ?? "").Contains("hello-ss"), (s1.Output ?? "").Trim());
        Report("shell Summary 带退出码", (s1.Summary ?? "").Contains("exit"), s1.Summary ?? "");

        var s2 = ShellCall(sh, "exit 7");
        Report("shell 非零退出码 → Ok=false", !s2.Ok, "Ok=" + s2.Ok);

        var s3 = ShellCall(sh, "");
        Report("shell 空 command 报错", !s3.Ok, "");

        // probe：起一个本地监听端口，扫它 + 一个未监听端口
        int openPort = 26000 + new Random().Next(400);
        int closedPort = openPort + 500;
        var L = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, openPort);
        L.Start();
        try
        {
            var d = new System.Collections.Generic.Dictionary<string, object>();
            d["action"] = "port";
            d["host"] = "127.0.0.1";
            d["ports"] = openPort + "," + closedPort;
            var probe = new SpiritScout.Tools.ProbeTool();
            var r = probe.InvokeAsync(d, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            string o = r.Output ?? "";

            Report("probe 扫到开放端口", r.Ok && o.Contains(openPort.ToString()), "");
            Report("probe 交代了未开放的端口数",
                o.IndexOf("关闭或超时", StringComparison.Ordinal) >= 0, "");
            Report("probe Summary 有 M/N 对比",
                (r.Summary ?? "").IndexOf("/2", StringComparison.Ordinal) >= 0, r.Summary ?? "");
        }
        finally
        {
            L.Stop();
        }
    }

    static SpiritScout.Tools.ToolResult ShellCall(SpiritScout.Tools.ShellTool t, string cmd)
    {
        var d = new System.Collections.Generic.Dictionary<string, object>();
        d["command"] = cmd;
        d["timeout"] = 15;
        return t.InvokeAsync(d, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// `note`（内存记事）与 `http_request`（起本地假服务）。
    ///
    /// **http 的 404/500 语义这里是关键**：原来 `Ok = code < 400`，探测到 404 时
    /// 模型看到「工具失败」，很可能就不读 Output 了——而 404/403/500 正是
    /// 渗透测试最想知道的信息。现在拿到响应就算成功。
    /// </summary>
    static void TestNoteAndHttp()
    {
        Console.WriteLine();
        Console.WriteLine("  -- note + http_request --");

        // note
        var note = new SpiritScout.Tools.NoteTool();
        var n0 = NoteCall(note, "list", null);
        Report("note 初始为空", (n0.Output ?? "").Contains("还没有笔记"), "");
        var n1 = NoteCall(note, "add", "线索一");
        Report("note add 成功", n1.Ok, "");
        var n2 = NoteCall(note, "add", "线索二");
        Report("note 记为 2 条", (n2.Output ?? "").Contains("共 2 条"), n2.Output ?? "");
        var n3 = NoteCall(note, "list", null);
        Report("note list 内容对", (n3.Output ?? "").Contains("线索一") && (n3.Output ?? "").Contains("线索二"), "");
        var n4 = NoteCall(note, "add", "   ");
        Report("note add 空白报错", !n4.Ok, "");

        // http_request
        int port = 23000 + new Random().Next(800);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        var th = new System.Threading.Thread(delegate() { ServeFake(listener); });
        th.IsBackground = true;
        th.Start();
        System.Threading.Thread.Sleep(300);
        string baseUrl = "http://127.0.0.1:" + port + "/";
        var http = new SpiritScout.Tools.HttpTool();

        var h1 = HttpCall(http, baseUrl + "hello", "GET", null);
        Report("http GET 200 → Ok", h1.Ok && (h1.Output ?? "").Contains("HELLO-FROM-FAKE"), h1.Output ?? "");

        var h2 = HttpCall(http, baseUrl + "echo", "POST", "test-body");
        Report("http POST body 送达", h2.Ok && (h2.Output ?? "").Contains("test-body"), (h2.Output ?? "").Substring(0, Math.Min(120, (h2.Output ?? "").Length)));

        var h3 = HttpCall(http, baseUrl + "notfound", "GET", null);
        Report("★ http 404 仍 Ok=true", h3.Ok, "Ok=false");
        Report("http 404 状态码在输出里", (h3.Output ?? "").Contains("404"), "");

        var h4 = HttpCall(http, "http://127.0.0.1:1/nothing", "GET", null);
        Report("http 连不上 → Ok=false", !h4.Ok, "");

        listener.Stop();
    }

    static SpiritScout.Tools.ToolResult NoteCall(SpiritScout.Tools.NoteTool t, string action, string text)
    {
        var d = new System.Collections.Generic.Dictionary<string, object>();
        d["action"] = action;
        if (text != null) d["text"] = text;
        return t.InvokeAsync(d, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
    }

    static SpiritScout.Tools.ToolResult HttpCall(SpiritScout.Tools.HttpTool t, string url, string method, string body)
    {
        var d = new System.Collections.Generic.Dictionary<string, object>();
        d["url"] = url;
        if (method != null) d["method"] = method;
        if (body != null) d["body"] = body;
        return t.InvokeAsync(d, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
    }

    static void ServeFake(System.Net.Sockets.TcpListener L)
    {
        while (true)
        {
            System.Net.Sockets.TcpClient c = null;
            try { c = L.AcceptTcpClient(); } catch { return; }
            try
            {
                using (c)
                {
                    var ns = c.GetStream();
                    var buf = new byte[8192];
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;
                    string req = Encoding.UTF8.GetString(buf, 0, n);
                    int he = req.IndexOf("\r\n\r\n");
                    string head = he > 0 ? req.Substring(0, he) : req;
                    var body = new System.Text.StringBuilder(he > 0 ? req.Substring(he + 4) : "");

                    int cl = 0;
                    var mm = System.Text.RegularExpressions.Regex.Match(head, @"Content-Length:\s*(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (mm.Success) cl = int.Parse(mm.Groups[1].Value);
                    while (body.Length < cl)
                    {
                        int k = ns.Read(buf, 0, buf.Length);
                        if (k <= 0) break;
                        body.Append(Encoding.UTF8.GetString(buf, 0, k));
                    }

                    string respBody; int code = 200;
                    if (head.StartsWith("GET /hello")) respBody = "HELLO-FROM-FAKE";
                    else if (head.StartsWith("POST /echo")) respBody = "got: [" + body.ToString() + "]";
                    else if (head.StartsWith("GET /notfound")) { respBody = "nf"; code = 404; }
                    else respBody = "?";

                    var bb = Encoding.UTF8.GetBytes(respBody);
                    var hb = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " X\r\nContent-Length: "
                        + bb.Length + "\r\nConnection: close\r\n\r\n");
                    ns.Write(hb, 0, hb.Length);
                    ns.Write(bb, 0, bb.Length);
                    ns.Flush();
                    ns.Close();
                }
            }
            catch { try { c.Close(); } catch { } }
        }
    }

    /// <summary>
    /// 经验查重：字符二元组 Jaccard + 并查集传递闭包。
    ///
    /// **最容易写错的是传递闭包**：A~B、B~C 相似但 A 和 C 不直接相似时，
    /// 三条应该在**同一组**。简单两两配对会漏掉这种情况。
    ///
    /// 下面的传递性用例是手工构造的（用字母序列，保证每个二元组唯一）：
    ///
    ///     B = "abcdefghijklmnopqrst"        （19 个二元组）
    ///     A = "abcdefghijklmn"              （B 的前 14 字符）
    ///     C = "ghijklmnopqrst"              （B 的后 14 字符）
    ///
    ///     A~B = 13/19 = 0.684  ✓ 过阈值
    ///     C~B = 13/19 = 0.684  ✓ 过阈值
    ///     A~C =  7/19 = 0.368  ✗ 不过阈值   ← 靠 B 连起来
    /// </summary>
    static void TestExperienceDedup()
    {
        Console.WriteLine();
        Console.WriteLine("  -- 经验查重 --");

        // ----- 相似度 -----
        var e1 = MkExp("a", "SQL 注入用 union 绕过");
        var e2 = MkExp("b", "sql注入用union绕过");        // 只差空格和大小写
        var e3 = MkExp("c", "RSA 小模数分解拿到明文");

        Report("完全相同 → 1.0",
            Math.Abs(SpiritScout.Knowledge.ExperienceDedup.Similarity(e1, e1) - 1.0) < 0.001,
            SpiritScout.Knowledge.ExperienceDedup.Similarity(e1, e1).ToString("F3"));
        Report("只差空格/大小写 → 归一化后相同",
            Math.Abs(SpiritScout.Knowledge.ExperienceDedup.Similarity(e1, e2) - 1.0) < 0.001,
            SpiritScout.Knowledge.ExperienceDedup.Similarity(e1, e2).ToString("F3"));
        Report("完全不同的主题 → 低相似度（<0.2）",
            SpiritScout.Knowledge.ExperienceDedup.Similarity(e1, e3) < 0.2,
            SpiritScout.Knowledge.ExperienceDedup.Similarity(e1, e3).ToString("F3"));
        Report("空内容 → 0",
            SpiritScout.Knowledge.ExperienceDedup.Similarity(MkExp("x", ""), e1) == 0
            && SpiritScout.Knowledge.ExperienceDedup.Similarity(null, e1) == 0, "");

        // ----- 分组：重复的两条 -----
        var two = new System.Collections.Generic.List<SpiritScout.Core.Experience>();
        two.Add(e1); two.Add(e2);
        var g2 = SpiritScout.Knowledge.ExperienceDedup.FindSimilar(
            two, SpiritScout.Knowledge.ExperienceDedup.DefaultThreshold);
        Report("两条措辞相同 → 1 组", g2.Count == 1, g2.Count + " 组");
        Report("组内 2 条", g2.Count == 1 && g2[0].Items.Count == 2,
            g2.Count == 1 ? g2[0].Items.Count + "" : "");
        Report("组内最高相似度接近 1",
            g2.Count == 1 && g2[0].MaxScore > 0.99, g2.Count == 1 ? g2[0].MaxScore.ToString("F3") : "");

        // ----- 全不相同 → 0 组 -----
        var diff = new System.Collections.Generic.List<SpiritScout.Core.Experience>();
        diff.Add(e1); diff.Add(e3);
        diff.Add(MkExp("d", "WiFi 握手包抓取与字典爆破"));
        var gd = SpiritScout.Knowledge.ExperienceDedup.FindSimilar(
            diff, SpiritScout.Knowledge.ExperienceDedup.DefaultThreshold);
        Report("三条互不相干 → 0 组", gd.Count == 0, gd.Count + " 组");

        // ----- 传递闭包（关键用例）-----
        string sA = "abcdefghijklmn";
        string sB = "abcdefghijklmnopqrst";
        string sC = "ghijklmnopqrst";
        double dAB = SpiritScout.Knowledge.ExperienceDedup.Similarity(MkExp("1", sA), MkExp("2", sB));
        double dBC = SpiritScout.Knowledge.ExperienceDedup.Similarity(MkExp("2", sB), MkExp("3", sC));
        double dAC = SpiritScout.Knowledge.ExperienceDedup.Similarity(MkExp("1", sA), MkExp("3", sC));
        Report("构造检查：A~B 与 B~C 过阈值，A~C 不过",
            dAB >= 0.62 && dBC >= 0.62 && dAC < 0.62,
            dAB.ToString("F3") + " / " + dBC.ToString("F3") + " / " + dAC.ToString("F3"));

        var chain = new System.Collections.Generic.List<SpiritScout.Core.Experience>();
        var cA = MkExp("1", sA); cA.CreatedAt = new DateTime(2026, 1, 1);
        var cB = MkExp("2", sB); cB.CreatedAt = new DateTime(2026, 1, 2);
        var cC = MkExp("3", sC); cC.CreatedAt = new DateTime(2026, 1, 3);
        chain.Add(cC); chain.Add(cA); chain.Add(cB);       // 故意打乱顺序
        var gc = SpiritScout.Knowledge.ExperienceDedup.FindSimilar(
            chain, SpiritScout.Knowledge.ExperienceDedup.DefaultThreshold);
        Report("传递闭包：A、B、C 归到同一组（1 组 3 条）",
            gc.Count == 1 && gc[0].Items.Count == 3,
            gc.Count == 1 ? gc[0].Items.Count + " 条" : gc.Count + " 组");
        Report("组内按时间升序（最旧在前）",
            gc.Count == 1 && gc[0].Items[0].Id == "1"
            && gc[0].Items[1].Id == "2" && gc[0].Items[2].Id == "3",
            gc.Count == 1 && gc[0].Items.Count > 0 ? gc[0].Items[0].Id : "");
        Report("Keep = 最旧那条", gc.Count == 1 && gc[0].Keep != null && gc[0].Keep.Id == "1",
            gc.Count == 1 && gc[0].Keep != null ? gc[0].Keep.Id : "");
        Report("Duplicates = 除保留项之外（2 条）",
            gc.Count == 1 && gc[0].Duplicates.Count == 2,
            gc.Count == 1 ? gc[0].Duplicates.Count + "" : "");
        Report("组内最低相似度 = A~C 那个（0.368）",
            gc.Count == 1 && Math.Abs(gc[0].MinScore - dAC) < 0.01,
            gc.Count == 1 ? gc[0].MinScore.ToString("F3") : "");

        // ----- 边界 -----
        Report("少于 2 条 → 0 组",
            SpiritScout.Knowledge.ExperienceDedup.FindSimilar(
                new System.Collections.Generic.List<SpiritScout.Core.Experience>(), 0.62).Count == 0
            && SpiritScout.Knowledge.ExperienceDedup.FindSimilar(null, 0.62).Count == 0, "");
        Report("单条 → 0 组",
            SpiritScout.Knowledge.ExperienceDedup.FindSimilar(two, 0.62).Count == 1
            && SpiritScout.Knowledge.ExperienceDedup.FindSimilar(
                new System.Collections.Generic.List<SpiritScout.Core.Experience> { e1 }, 0.62).Count == 0, "");
        Report("阈值 <=0 时退回默认（不把所有东西都并成一组）",
            SpiritScout.Knowledge.ExperienceDedup.FindSimilar(diff, 0).Count == 0, "");
    }

    static SpiritScout.Core.Experience MkExp(string id, string insight)
    {
        var e = new SpiritScout.Core.Experience();
        e.Id = id;
        e.Insight = insight;
        e.Tags = "";
        e.Mode = "ctf";
        e.CreatedAt = new DateTime(2026, 6, 1);
        return e;
    }

    /// <summary>
    /// `Text.Hit`：三个页面（任务 / 经验 / 技能）共用的关键词命中判定。
    ///
    /// 三处原来各写一遍，语义还不完全一致（有的先判空、有的拼 haystack）。
    /// 收成一处之后要保证的是**边界语义固定**：
    /// 空关键词不筛、大小写不敏感、null 字段当空串、关键词首尾空白要 trim。
    /// </summary>
    static void TestTextHit()
    {
        Console.WriteLine();
        Console.WriteLine("  -- 关键词命中 --");

        // 空 / 空白 / null 关键词 = 不筛
        Report("空关键词 → 命中（不筛）",
            SpiritScout.Core.Text.Hit("", "任意") && SpiritScout.Core.Text.Hit("   ", "任意")
            && SpiritScout.Core.Text.Hit(null, "任意"), "");
        Report("空关键词 + 无字段 → 命中（不筛）",
            SpiritScout.Core.Text.Hit("", new string[0]), "");

        // 基本命中
        Report("命中第一个字段", SpiritScout.Core.Text.Hit("jwt", "jwt tool", "x"), "");
        Report("命中靠后的字段", SpiritScout.Core.Text.Hit("jwt", "a", "b", "the jwt thing"), "");
        Report("不命中 → false", !SpiritScout.Core.Text.Hit("zzz", "abc", "def"), "");

        // 大小写
        Report("大小写不敏感（关键词大写）", SpiritScout.Core.Text.Hit("JWT", "jwt tool"), "");
        Report("大小写不敏感（关键词小写）", SpiritScout.Core.Text.Hit("jwt", "JWT Tool"), "");
        Report("大小写不敏感（混合）", SpiritScout.Core.Text.Hit("JwT", "a JWT b"), "");

        // 中文
        Report("中文关键词命中", SpiritScout.Core.Text.Hit("注入", "SQL 注入详解"), "");
        Report("中文不命中", !SpiritScout.Core.Text.Hit("逆向", "SQL 注入详解"), "");

        // 关键词首尾空白要 trim
        Report("关键词首尾空白被 trim", SpiritScout.Core.Text.Hit("  jwt  ", "a jwt b"), "");

        // null 字段
        Report("null 字段被跳过，仍能命中后面的",
            SpiritScout.Core.Text.Hit("jwt", null, "jwt"), "");
        Report("全 null 字段 + 非空关键词 → false",
            !SpiritScout.Core.Text.Hit("jwt", null, null, ""), "");

        // 没传字段
        Report("无字段 + 非空关键词 → false",
            !SpiritScout.Core.Text.Hit("jwt", new string[0]), "");

        // 边界：关键词比字段长
        Report("关键词比字段长 → 不命中", !SpiritScout.Core.Text.Hit("jwt token", "jwt"), "");
    }

    /// <summary>
    /// 会话统计的边界规则。
    ///
    /// 这组是「把逻辑从 UI 闭包里抽出来」的直接回报 —— **用固定日期测边界**，
    /// 不用等真实数据。边界（今天算不算、第 7 天算不算、时间戳在未来怎么办）
    /// 是这类代码最容易错的地方，而错一天用户看不出来。
    /// </summary>
    static void TestSessionStats()
    {
        Console.WriteLine();
        Console.WriteLine("  -- 会话统计 --");

        // 固定基准：2026-11-18 15:30
        var now = new DateTime(2026, 11, 18, 15, 30, 0);

        // ----- 档位换算 -----
        Report("SelectedIndex 0→全部",
            SpiritScout.Session.SessionStats.RangeFromIndex(0) == SpiritScout.Session.TimeRange.All, "");
        Report("SelectedIndex 1→近7天",
            SpiritScout.Session.SessionStats.RangeFromIndex(1) == SpiritScout.Session.TimeRange.Last7Days, "");
        Report("SelectedIndex 越界(99/−5)→全部",
            SpiritScout.Session.SessionStats.RangeFromIndex(99) == SpiritScout.Session.TimeRange.All
            && SpiritScout.Session.SessionStats.RangeFromIndex(-5) == SpiritScout.Session.TimeRange.All, "");

        // ----- 下界 -----
        var b7 = SpiritScout.Session.SessionStats.LowerBound(SpiritScout.Session.TimeRange.Last7Days, now);
        Report("近7天下界 = 今天-6（含今天共 7 天）",
            b7.HasValue && b7.Value == new DateTime(2026, 11, 12), b7.HasValue ? b7.Value.ToString("yyyy-MM-dd") : "null");
        var b30 = SpiritScout.Session.SessionStats.LowerBound(SpiritScout.Session.TimeRange.Last30Days, now);
        Report("近30天下界 = 今天-29",
            b30.HasValue && b30.Value == new DateTime(2026, 10, 20), b30.HasValue ? b30.Value.ToString("yyyy-MM-dd") : "null");
        var bt = SpiritScout.Session.SessionStats.LowerBound(SpiritScout.Session.TimeRange.Today, now);
        Report("今天下界 = 今天 00:00",
            bt.HasValue && bt.Value == new DateTime(2026, 11, 18), bt.HasValue ? bt.Value.ToString("yyyy-MM-dd") : "null");
        Report("全部时间无下界",
            !SpiritScout.Session.SessionStats.LowerBound(SpiritScout.Session.TimeRange.All, now).HasValue, "");

        // ----- 过滤边界：今天-6 在内，今天-7 在外 -----
        var all = new System.Collections.Generic.List<SpiritScout.Core.Session>();
        all.Add(MkSess("t0", now, 100));                        // 今天
        all.Add(MkSess("t-6", now.AddDays(-6), 200));           // 恰好在下界上 → 应保留
        all.Add(MkSess("t-7", now.AddDays(-7), 300));           // 比下界早一天 → 应剔除
        all.Add(MkSess("t-29", now.AddDays(-29), 400));
        all.Add(MkSess("future", now.AddDays(2), 500));         // 未来时间

        var f7 = SpiritScout.Session.SessionStats.FilterByRange(all, SpiritScout.Session.TimeRange.Last7Days, now);
        // 期望 3 个：今天 + 今天-6 + 未来。
        // 「未来」在 FilterByRange 里是**不设上界**所以也算入 ——
        // 下面紧接着那条断言就是在验这个。
        // （195 轮写的时候先写了「2 个」，后来加了 future 用例却没回头改，
        //   结果两条断言自相矛盾。2026-11-19 全量测试抓出来。）
        Report("近7天：保留今天 + 今天-6 + 未来（下界含当天、不设上界）",
            f7.Count == 3, f7.Count + " 个");
        Report("近7天：剔除今天-7", !HasId(f7, "t-7"), "");
        Report("近7天：未来时间也算入（不设上界）", HasId(f7, "future"), "");

        var fa = SpiritScout.Session.SessionStats.FilterByRange(all, SpiritScout.Session.TimeRange.All, now);
        Report("全部时间：5 个全保留", fa.Count == 5, fa.Count + " 个");

        var fnull = SpiritScout.Session.SessionStats.FilterByRange(null, SpiritScout.Session.TimeRange.All, now);
        Report("传 null 返回空表不抛", fnull != null && fnull.Count == 0, "");

        // ----- 归桶：最后一个桶是今天 -----
        var buckets = SpiritScout.Session.SessionStats.BucketByDay(all, 14, now);
        Report("归桶长度 = 14", buckets.Length == 14, buckets.Length + "");
        Report("今天落在最后一个桶", Math.Abs(buckets[13] - 1.0) < 0.001, buckets[13].ToString("F0"));
        Report("今天-6 落在倒数第 7 个桶", Math.Abs(buckets[7] - 1.0) < 0.001, buckets[7].ToString("F0"));
        Report("今天-29 超出 14 天窗口 → 不计", Math.Abs(buckets[0]) < 0.001, buckets[0].ToString("F0"));
        Report("未来时间不写负数下标（不抛、不计）", Math.Abs(buckets[13] - 1.0) < 0.001, "");

        var z = SpiritScout.Session.SessionStats.BucketByDay(all, 0, now);
        Report("天数 0 → 空数组", z != null && z.Length == 0, "");

        // 今天-13 恰好落在第一个桶
        var edge = new System.Collections.Generic.List<SpiritScout.Core.Session>();
        edge.Add(MkSess("e13", now.AddDays(-13), 10));
        edge.Add(MkSess("e14", now.AddDays(-14), 10));
        var eb = SpiritScout.Session.SessionStats.BucketByDay(edge, 14, now);
        Report("今天-13 落在第 0 个桶（窗口边缘）", Math.Abs(eb[0] - 1.0) < 0.001, eb[0].ToString("F0"));
        Report("今天-14 出窗口 → 不计", Sum(eb) < 1.5, Sum(eb).ToString("F0"));

        // ----- 按模式聚合 -----
        var mix = new System.Collections.Generic.List<SpiritScout.Core.Session>();
        var a1 = MkSess("m1", now, 100); a1.Mode = SpiritScout.Core.RunMode.Ctf;
        var a2 = MkSess("m2", now, 100); a2.Mode = SpiritScout.Core.RunMode.Ctf;
        var a3 = MkSess("m3", now, 100); a3.Mode = SpiritScout.Core.RunMode.Src;
        mix.Add(a1); mix.Add(a2); mix.Add(a3);

        var modes = SpiritScout.Session.SessionStats.ByMode(mix);
        Report("按模式聚合出 2 组", modes.Count == 2, modes.Count + " 组");
        Report("按会话数降序（CTF 在前）",
            modes.Count >= 1 && modes[0].Sessions == 2, modes.Count >= 1 ? modes[0].Sessions + "" : "空");
        Report("token 按模式累加", modes.Count >= 1 && modes[0].Tokens == 200,
            modes.Count >= 1 ? modes[0].Tokens + "" : "空");

        var mn = SpiritScout.Session.SessionStats.ByMode(null);
        Report("ByMode(null) 返回空表不抛", mn != null && mn.Count == 0, "");
    }

    static SpiritScout.Core.Session MkSess(string id, DateTime updated, long tokens)
    {
        var s = new SpiritScout.Core.Session();
        s.Id = id;
        s.Target = id;
        s.Tokens = tokens;
        s.UpdatedAt = updated;
        s.CreatedAt = updated;
        return s;
    }

    static bool HasId(System.Collections.Generic.List<SpiritScout.Core.Session> list, string id)
    {
        foreach (var s in list) if (s.Id == id) return true;
        return false;
    }

    static double Sum(double[] arr)
    {
        double t = 0;
        foreach (var v in arr) t += v;
        return t;
    }

    /// <summary>
    /// 工具消息解析（`delegate` / `file`）。
    ///
    /// **这组的意义**：187 轮做「拓扑」标签时，解析基于 `ToolResult.Output` 的
    /// `[方向 的结论]` 前缀 —— 而 `AgentLoop` 落库的是
    /// `"[" + call.Name + "] " + result.Summary`，`Output` 从来没落过库。
    /// 于是正则永远匹配失败，每一行都显示「（未记录方向）」。
    ///
    /// 那时候解析写在 UI 闭包里，**没法单测**，所以这个错误活了三轮。
    /// 现在解析抽到 `SpiritScout.Session.SessionToolMessage`，纯字符串进出。
    /// </summary>
    static void TestSessionToolMessage()
    {
        Console.WriteLine();
        Console.WriteLine("  -- 工具消息解析 --");

        // ===== delegate =====
        var d1 = SpiritScout.Session.SessionToolMessage.ParseDelegate(
            "[delegate] 密码分析：RSA 小模数分解拿到明文");
        Report("delegate 正常：方向名",
            d1 != null && d1.Agent == "密码分析", d1 == null ? "null" : d1.Agent);
        Report("delegate 正常：摘要",
            d1 != null && d1.Summary == "RSA 小模数分解拿到明文", d1 == null ? "null" : d1.Summary);
        Report("delegate 正常：状态是「已返回」",
            d1 != null && d1.State == "已返回", d1 == null ? "null" : d1.State);

        // 摘要里再有冒号，不能被切碎（只切第一个）
        var d2 = SpiritScout.Session.SessionToolMessage.ParseDelegate(
            "[delegate] 逆向：结论是：先脱壳再调试");
        Report("delegate 摘要含冒号只切第一个",
            d2 != null && d2.Agent == "逆向" && d2.Summary == "结论是：先脱壳再调试",
            d2 == null ? "null" : (d2.Agent + " / " + d2.Summary));

        // 异常分支
        var d3 = SpiritScout.Session.SessionToolMessage.ParseDelegate(
            "[delegate] Web 执行失败：连接超时");
        Report("delegate 异常：方向名剥掉「执行失败」",
            d3 != null && d3.Agent == "Web", d3 == null ? "null" : d3.Agent);
        Report("delegate 异常：状态「执行失败」",
            d3 != null && d3.State == "执行失败", d3 == null ? "null" : d3.State);

        // 取不到方向
        var d4 = SpiritScout.Session.SessionToolMessage.ParseDelegate("[delegate] ");
        Report("delegate 空内容 → 「（未记录方向）」",
            d4 != null && d4.Agent == "（未记录方向）", d4 == null ? "null" : d4.Agent);

        // 不是 delegate 的消息 → null
        Report("非 delegate 消息 → null",
            SpiritScout.Session.SessionToolMessage.ParseDelegate("[file] 写入 a.txt") == null
            && SpiritScout.Session.SessionToolMessage.ParseDelegate("普通文本") == null
            && SpiritScout.Session.SessionToolMessage.ParseDelegate(null) == null, "");

        // ===== file =====
        var f1 = SpiritScout.Session.SessionToolMessage.ParseFile("[file] 读取（二进制）a.bin");
        Report("file 读取（二进制）：动作",
            f1 != null && f1.Action == "读取", f1 == null ? "null" : f1.Action);
        Report("file 读取（二进制）：目标",
            f1 != null && f1.Target == "a.bin", f1 == null ? "null" : f1.Target);

        var f2 = SpiritScout.Session.SessionToolMessage.ParseFile("[file] 读取 s.txt (123 字节)");
        Report("file 读取：动作", f2 != null && f2.Action == "读取", f2 == null ? "null" : f2.Action);
        Report("file 读取：目标保留字节数",
            f2 != null && f2.Target == "s.txt (123 字节)", f2 == null ? "null" : f2.Target);

        var f3 = SpiritScout.Session.SessionToolMessage.ParseFile("[file] 写入 out.md");
        Report("file 写入：动作 + 目标",
            f3 != null && f3.Action == "写入" && f3.Target == "out.md",
            f3 == null ? "null" : (f3.Action + " " + f3.Target));

        var f4 = SpiritScout.Session.SessionToolMessage.ParseFile("[file] 列出 C:\\work（12 项）");
        Report("file 列出：保留完整路径与项数",
            f4 != null && f4.Action == "列出" && f4.Target == "C:\\work（12 项）",
            f4 == null ? "null" : f4.Target);

        var f5 = SpiritScout.Session.SessionToolMessage.ParseFile("[file] 哈希 a.txt");
        Report("file 哈希：动作", f5 != null && f5.Action == "哈希", f5 == null ? "null" : f5.Action);

        // 工具报错（不是五种正常 Summary 之一）
        var f6 = SpiritScout.Session.SessionToolMessage.ParseFile("[file] 错误：路径不存在");
        Report("file 报错 → 动作「其它」且不崩",
            f6 != null && f6.Action == "其它" && f6.Target == "错误：路径不存在",
            f6 == null ? "null" : (f6.Action + " " + f6.Target));

        Report("非 file 消息 → null",
            SpiritScout.Session.SessionToolMessage.ParseFile("[delegate] a：b") == null
            && SpiritScout.Session.SessionToolMessage.ParseFile("[file] ") == null
            && SpiritScout.Session.SessionToolMessage.ParseFile(null) == null, "");
    }

    /// <summary>
    /// Markdown 分段器。模型回复基本都是 Markdown，气泡靠它决定怎么画。
    ///
    /// 这组是**纯字符串逻辑**，最容易出错的地方是「围栏代码块的配对」和
    /// 「标题 vs 普通 # 开头文本」的边界。
    /// </summary>
    static void TestMarkdown()
    {
        Console.WriteLine();
        Console.WriteLine("  -- Markdown 分段 --");

        // 空输入
        Report("空字符串 → 0 块",
            SpiritScout.Ui.Widgets.MarkdownLite.Parse("").Count == 0, "");

        // 标题
        var h = SpiritScout.Ui.Widgets.MarkdownLite.Parse("### 先排除标准解码");
        Report("三级标题被识别",
            h.Count == 1 && h[0].Kind == SpiritScout.Ui.Widgets.MdKind.Heading && h[0].Level == 3,
            h.Count + " 块");

        // 代码块
        string codeMd = "说明：\n\n```python\nimport base64\nprint(1)\n```\n\n结束";
        var c = SpiritScout.Ui.Widgets.MarkdownLite.Parse(codeMd);
        int codeCount = 0;
        string codeText = "";
        foreach (var b in c)
            if (b.Kind == SpiritScout.Ui.Widgets.MdKind.Code) { codeCount++; codeText = b.Text; }
        Report("围栏代码块被识别为 1 块", codeCount == 1, codeCount + " 块");
        Report("代码块内容不含围栏行",
            codeText.IndexOf("```", StringComparison.Ordinal) < 0
            && codeText.IndexOf("import base64", StringComparison.Ordinal) >= 0,
            codeText.Replace("\n", " | "));
        Report("代码块前后各自成分段",
            c.Count >= 3 && c[0].Kind == SpiritScout.Ui.Widgets.MdKind.Paragraph,
            c.Count + " 块");

        // 无序列表
        var li = SpiritScout.Ui.Widgets.MarkdownLite.Parse("- 第一步\n- 第二步");
        int items = 0;
        foreach (var b in li) if (b.Kind == SpiritScout.Ui.Widgets.MdKind.ListItem) items++;
        Report("两个无序列表项", items == 2, items + " 项");
        Report("列表项符号是「·」", items > 0 && li[0].Lang == "·", items > 0 ? li[0].Lang : "");

        // 有序列表
        var ol = SpiritScout.Ui.Widgets.MarkdownLite.Parse("1. 标准解码\n2. 左移一位");
        bool ordered = ol.Count >= 2
            && ol[0].Kind == SpiritScout.Ui.Widgets.MdKind.ListItem
            && ol[0].Lang == "1.";
        Report("有序列表保住了序号 1.", ordered, ol.Count > 0 ? ol[0].Lang : "");

        // 引用
        var q = SpiritScout.Ui.Widgets.MarkdownLite.Parse("> Base64 的字母表可以整体偏移");
        Report("引用被识别",
            q.Count == 1 && q[0].Kind == SpiritScout.Ui.Widgets.MdKind.Quote,
            q.Count + " 块");

        // 分隔线
        var r = SpiritScout.Ui.Widgets.MarkdownLite.Parse("上\n\n---\n\n下");
        bool hasRule = false;
        foreach (var b in r) if (b.Kind == SpiritScout.Ui.Widgets.MdKind.Rule) hasRule = true;
        Report("--- 被识别为分隔线", hasRule, r.Count + " 块");

        // 段落里连续非空行合成一段
        var para = SpiritScout.Ui.Widgets.MarkdownLite.Parse("第一行\n第二行\n\n另起一段");
        Report("连续行合成段落（2 段）", para.Count == 2, para.Count + " 段");

        // `#` 开头但不是标题（没空格）不该当标题
        var notHeading = SpiritScout.Ui.Widgets.MarkdownLite.Parse("#hashtag 不是标题");
        Report("# 紧跟文字不算标题",
            notHeading.Count == 1 && notHeading[0].Kind == SpiritScout.Ui.Widgets.MdKind.Paragraph,
            notHeading.Count > 0 ? notHeading[0].Kind.ToString() : "空");

        // 未闭合的围栏：吃到结尾，不抛异常
        try
        {
            var open = SpiritScout.Ui.Widgets.MarkdownLite.Parse("```\ncode\n没闭合");
            Report("未闭合围栏不抛异常", open.Count >= 1, open.Count + " 块");
        }
        catch (Exception ex)
        {
            Report("未闭合围栏不抛异常", false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// `kb_search` 是 19 个工具里唯一「纯查表」的，之前只验过能构造、没验过能查。
    /// 这里验四条：能命中、返回正文（不只是标题）、查不到时有明确话、空 query 报错。
    /// </summary>
    static void TestKbSearch()
    {
        Console.WriteLine();
        Console.WriteLine("  -- kb_search --");

        var kb = new SpiritScout.Tools.KnowledgeTool();

        var r1 = KbAsk(kb, "JWT");
        Report("查 JWT 有结果", r1.Ok && (r1.Output ?? "").Length > 100, (r1.Output ?? "").Length + " 字符");
        Report("结果里含 jwt 关键词",
            (r1.Output ?? "").ToLowerInvariant().IndexOf("jwt", StringComparison.Ordinal) >= 0, "");

        var r2 = KbAsk(kb, "ADCS certificate abuse");
        Report("查 ADCS 有结果", r2.Ok && (r2.Output ?? "").Length > 100, "");

        var r3 = KbAsk(kb, "zzzz_not_exist_qqqq_9999");
        string o3 = r3.Output ?? "";
        Report("查不到时有明确提示（不是空白）",
            o3.IndexOf("没有命中", StringComparison.Ordinal) >= 0 || o3.Length > 20, o3.Length + " 字符");

        var r4 = KbAsk(kb, "");
        Report("空 query 明确报错", !r4.Ok, "Ok=" + r4.Ok);

        // 结果不该无限长
        var r5 = KbAsk(kb, "漏洞");
        Report("结果长度有上限（< 50 KB）", (r5.Output ?? "").Length < 50000, (r5.Output ?? "").Length + "");
    }

    static SpiritScout.Tools.ToolResult KbAsk(SpiritScout.Tools.KnowledgeTool kb, string q)
    {
        var d = new System.Collections.Generic.Dictionary<string, object>();
        d["query"] = q;
        return kb.InvokeAsync(d, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 导出与脱敏。**这组是安全相关的，必须有回归**。
    ///
    /// 165 轮测出来 4 个 bug，都在这里兜着：
    /// 1. `sk-` / `ghp_` / `AKIA` / `xoxb` 四条规则没有前缀捕获组，
    ///    而替换逻辑是 `Groups[1] + "***"` —— 结果是「原文 + ***」，等于没脱敏
    /// 2. `password=xxx`（裸的，不是 URL 参数）完全不匹配
    /// 3. `FlagFormat` 填的是给人看的模板 `flag{...}`，却被当正则用，
    ///    实际匹配的是字面量 `flag{...}`
    /// 4. 连带的：Flag 章节因为提取不到而整个消失
    /// </summary>
    static void TestExport()
    {
        Console.WriteLine();
        Console.WriteLine("  -- 导出与脱敏 --");

        var sf = new SessionFile();
        sf.Session.Id = "exp";
        sf.Session.Target = "导出测试";
        sf.Session.Title = "导出功能验证";
        sf.Session.Mission = "验证脱敏。";
        sf.Session.Mode = RunMode.Ctf;
        sf.Session.Phase = SessionPhase.Completed;
        sf.Session.FlagFormat = "flag{...}";
        sf.Session.CreatedAt = DateTime.Now.AddHours(-2);
        sf.Session.UpdatedAt = DateTime.Now.AddHours(-1);
        sf.Messages = new List<SpiritScout.Core.Message>();

        var m1 = new SpiritScout.Core.Message();
        m1.Id = 1; m1.SessionId = "exp"; m1.Role = "user"; m1.CreatedAt = DateTime.Now;
        m1.Content = "我用 sk-abcdef1234567890abcdef 调接口，结果返回 flag{export_works}。";
        sf.Messages.Add(m1);

        var m2 = new SpiritScout.Core.Message();
        m2.Id = 2; m2.SessionId = "exp"; m2.Role = "assistant"; m2.CreatedAt = DateTime.Now;
        m2.Content = "请求头 Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.demo.sig\n密码 password=hunter2";
        sf.Messages.Add(m2);

        sf.Session.MessageCount = sf.Messages.Count;

        string md = SpiritScout.Session.SessionExporter.ToMarkdown(sf);

        Report("sk- 密钥已脱敏", md.IndexOf("sk-abcdef1234567890abcdef", StringComparison.Ordinal) < 0, "原文还在");
        Report("sk- 保留前缀、内容变 ***", md.IndexOf("sk-***", StringComparison.Ordinal) >= 0, "");
        Report("Bearer token 已脱敏", md.IndexOf("eyJhbGciOiJIUzI1NiJ9.demo.sig", StringComparison.Ordinal) < 0, "原文还在");
        Report("password= 已脱敏", md.IndexOf("hunter2", StringComparison.Ordinal) < 0, "原文还在");
        Report("password= → ***", md.IndexOf("password=***", StringComparison.Ordinal) >= 0, "");

        var flags = SpiritScout.Session.SessionExporter.ExtractFlags(sf);
        Report("从消息里提取到 flag", flags.Count > 0 && flags[0] == "flag{export_works}", flags.Count + " 个");
        Report("导出里有 Flag 章节", md.IndexOf("## 发现的 Flag", StringComparison.Ordinal) >= 0, "");

        // FlagFormat 的各种写法都该转成正则
        string[] formats = new string[] { "flag{...}", "flag{xxx}", "DASCTF{...}", "flag" };
        int okFmt = 0;
        foreach (var fmt in formats)
        {
            var t = new SessionFile();
            t.Session.Id = "t"; t.Session.FlagFormat = fmt;
            t.Messages = new List<SpiritScout.Core.Message>();
            var mm = new SpiritScout.Core.Message();
            mm.Id = 1; mm.Role = "assistant"; mm.CreatedAt = DateTime.Now;
            mm.Content = "结果 flag{abc123} 和 DASCTF{xyz789}";
            t.Messages.Add(mm);
            if (SpiritScout.Session.SessionExporter.ExtractFlags(t).Count > 0) okFmt++;
        }
        Report("FlagFormat 的 4 种写法都能提取（" + okFmt + "/4）", okFmt == 4, okFmt + " 个成功");
    }

    /// <summary>
    /// 首次运行引导：确认「没配全模型」时程序会把用户送到模型页，
    /// 而不是扔进一个空白首页。
    ///
    /// 判据（FirstRun.HasModel）要求三样齐全：**启用的模型** +
    /// **供应商存在** + **密钥存在**。这四种「差一样」的情况都测了。
    /// </summary>
    static void TestFirstRun(string dataDir)
    {
        // 用一个独立的干净目录，避免和上面 e2e 的数据混
        // 2026-11-22：先清空目录 —— 复用上次的 models.json/secrets.dat
        // 会导致前三条断言（预期 HasModel=false）被旧数据干扰。
        string dir = Path.Combine(dataDir, "firstrun");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        AppPaths.Override(dir, "firstrun");

        PageRegistry.Clear();
        PageRegistry.Register(new SpiritScout.Ui.Pages.HomePage());
        PageRegistry.Register(new SpiritScout.Ui.Pages.ModelsPage());

        Console.WriteLine();
        Console.WriteLine("  -- 首次运行引导 --");

        Report("全新安装：HasModel=false", !SpiritScout.Ui.FirstRun.HasModel(), "");
        Report("全新安装：首屏=home",
            SpiritScout.Ui.FirstRun.DecideStartupPage(null) == "home",
            SpiritScout.Ui.FirstRun.DecideStartupPage(null));

        // 只加供应商，没加模型
        var secrets = new SecretStore(AppPaths.SecretsFile);
        var reg = new ModelRegistry(AppPaths.ModelsFile, secrets);
        reg.UpsertProvider(new ProviderSpec { Name = "p1", BaseUrl = "https://x/v1" });
        reg.Dispose();
        Report("只有供应商：仍 HasModel=false", !SpiritScout.Ui.FirstRun.HasModel(), "");

        // 模型指向不存在的供应商
        secrets = new SecretStore(AppPaths.SecretsFile);
        reg = new ModelRegistry(AppPaths.ModelsFile, secrets);
        reg.UpsertModel(new ModelSpec { Name = "m1", ProviderName = "不存在", ModelId = "x", Enabled = true });
        reg.Dispose();
        Report("供应商名写错：HasModel=false", !SpiritScout.Ui.FirstRun.HasModel(), "");

        // 供应商对，但没密钥
        secrets = new SecretStore(AppPaths.SecretsFile);
        reg = new ModelRegistry(AppPaths.ModelsFile, secrets);
        reg.DeleteModel("m1");
        reg.UpsertModel(new ModelSpec { Name = "m1", ProviderName = "p1", ModelId = "x", Enabled = true });
        reg.Dispose();
        Report("没填密钥：HasModel=false", !SpiritScout.Ui.FirstRun.HasModel(), "");

        // 都齐了但模型停用
        secrets = new SecretStore(AppPaths.SecretsFile);
        reg = new ModelRegistry(AppPaths.ModelsFile, secrets);
        reg.SetKey("p1", "sk-test");
        reg.UpsertModel(new ModelSpec { Name = "m1", ProviderName = "p1", ModelId = "x", Enabled = false });
        reg.Dispose();
        Report("模型被停用：HasModel=false", !SpiritScout.Ui.FirstRun.HasModel(), "");

        // 全部就绪
        secrets = new SecretStore(AppPaths.SecretsFile);
        reg = new ModelRegistry(AppPaths.ModelsFile, secrets);
        reg.UpsertModel(new ModelSpec { Name = "m1", ProviderName = "p1", ModelId = "x", Enabled = true });
        reg.Dispose();
        Report("全部就绪：HasModel=true", SpiritScout.Ui.FirstRun.HasModel(), "");
        Report("全部就绪：首屏=home",
            SpiritScout.Ui.FirstRun.DecideStartupPage(null) == "home",
            SpiritScout.Ui.FirstRun.DecideStartupPage(null));

        // 上次停留的页
        // 2026-11-22：DecideStartupPage 已改成**永远回「home」**（首屏固定任务页），
        // 旧的「恢复上次位置」行为已被产品主动移除。此断言改成验证「传任何值都回 home」。
        Report("首屏固定为 home（传 models 也回 home）",
            SpiritScout.Ui.FirstRun.DecideStartupPage("models") == "home",
            SpiritScout.Ui.FirstRun.DecideStartupPage("models"));

        // 上次的页已经不存在
        Report("上次页面已删除 → home（不崩）",
            SpiritScout.Ui.FirstRun.DecideStartupPage("已删除的页") == "home",
            SpiritScout.Ui.FirstRun.DecideStartupPage("已删除的页"));

        // 空数据下模型页的引导文案 —— 新用户第一眼就该知道点哪、填什么
        try
        {
            var mp = PageRegistry.Find("models");
            if (mp != null)
            {
                var form = new System.Windows.Forms.Form();
                form.Size = new System.Drawing.Size(1280, 860);
                form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(-3000, -3000);
                form.ShowInTaskbar = false;
                form.BackColor = Theme.Bg;
                var view = mp.CreateView();
                view.Dock = System.Windows.Forms.DockStyle.Fill;
                form.Controls.Add(view);
                form.Show();
                PumpFor(300);
                var t = mp.RefreshAsync();
                int guard = 0;
                while (!t.IsCompleted && guard++ < 400)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(10);
                }
                PumpFor(500);

                var texts = new System.Collections.Generic.List<string>();
                CollectTexts(view, texts);
                string all = string.Join(" | ", texts.ToArray());

                Report("模型页有空状态引导（告诉用户点哪）",
                    all.IndexOf("还没有") >= 0 || all.IndexOf("先加供应商") >= 0, "");
                Report("模型页有「+ 供应商」按钮", all.IndexOf("+ 供应商") >= 0, "");
                Report("模型页有「+ 模型」按钮", all.IndexOf("+ 模型") >= 0, "");

                form.Close();
                form.Dispose();
                PumpFor(120);
            }
        }
        catch (Exception ex)
        {
            Report("模型页引导检查不抛异常", false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// 按钮接线检查：页面上的关键按钮，Click 事件必须有订阅者。
    ///
    /// **不能直接读 `button.Click`**——那是事件，类外只能 += / -=。
    /// 用反射读 Control 内部的 `EventClick` 键 + Component 的 `events` 列表。
    ///
    /// 只验「接上了」，不验「接得对」——`+ 供应商` 内部是 ShowDialog，
    /// 真点会阻塞测试。接错的情况只能靠人用一次才发现。
    /// </summary>
    static void TestWiring(string dataDir)
    {
        string dir = Path.Combine(dataDir, "wiring");
        Directory.CreateDirectory(dir);
        AppPaths.Override(dir, "wiring");

        PageRegistry.Clear();
        PageRegistry.Register(new SpiritScout.Ui.Pages.HomePage());
        PageRegistry.Register(new SpiritScout.Ui.Pages.ModelsPage());

        Console.WriteLine();
        Console.WriteLine("  -- 按钮接线 --");

        CheckWiring("models", new string[] { "+ 供应商", "+ 模型" });
        CheckWiring("home", new string[] { "开始执行  →" });
    }

    static void CheckWiring(string pageId, string[] buttons)
    {
        var page = PageRegistry.Find(pageId);
        if (page == null) { Report(pageId + " 页面存在", false, "找不到"); return; }

        var form = new System.Windows.Forms.Form();
        form.Size = new System.Drawing.Size(1280, 860);
        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-3000, -3000);
        form.ShowInTaskbar = false;
        form.BackColor = Theme.Bg;
        var view = page.CreateView();
        view.Dock = System.Windows.Forms.DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        PumpFor(400);

        var t = page.RefreshAsync();
        int guard = 0;
        while (!t.IsCompleted && guard++ < 400)
        {
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(10);
        }
        PumpFor(500);

        var btns = new System.Collections.Generic.List<System.Windows.Forms.Button>();
        FindButtons(view, btns);

        foreach (var want in buttons)
        {
            int n = 0;
            bool found = false;
            foreach (var b in btns)
            {
                if (b.Text != null && b.Text.Trim() == want)
                {
                    found = true;
                    n = ClickHandlerCount(b);
                    break;
                }
            }
            Report(pageId + " 的「" + want + "」已接线",
                found && n > 0,
                found ? "订阅者 0" : "按钮不存在");
        }

        form.Close();
        form.Dispose();
        PumpFor(150);
    }

    static int ClickHandlerCount(System.Windows.Forms.Button b)
    {
        try
        {
            var keyField = typeof(System.Windows.Forms.Control).GetField("EventClick",
                BindingFlags.NonPublic | BindingFlags.Static);
            var evField = typeof(Component).GetField("events",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (keyField == null || evField == null) return -1;
            object key = keyField.GetValue(b);
            var list = evField.GetValue(b) as EventHandlerList;
            if (list == null) return -2;
            Delegate d = list[key];
            return d == null ? 0 : d.GetInvocationList().Length;
        }
        catch { return -3; }
    }

    static void FindButtons(System.Windows.Forms.Control r,
        System.Collections.Generic.List<System.Windows.Forms.Button> into)
    {
        foreach (System.Windows.Forms.Control c in r.Controls)
        {
            var b = c as System.Windows.Forms.Button;
            if (b != null) into.Add(b);
            if (c.HasChildren) FindButtons(c, into);
        }
    }

    static void CollectTexts(System.Windows.Forms.Control r, System.Collections.Generic.List<string> into)
    {
        foreach (System.Windows.Forms.Control c in r.Controls)
        {
            if (c is System.Windows.Forms.Label || c is System.Windows.Forms.Button
                || c is System.Windows.Forms.CheckBox)
            {
                if (!string.IsNullOrEmpty(c.Text)) into.Add(c.Text.Replace("\n", " "));
            }
            if (c.HasChildren) CollectTexts(c, into);
        }
    }

    static void PumpFor(int ms)
    {
        int end = Environment.TickCount + ms;
        while (Environment.TickCount < end)
        {
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(12);
        }
    }

    /// <summary>假 LLM：按轮次返回 tool_calls 或最终结论。</summary>
    static void Serve(TcpListener listener, string targetFile)
    {
        var ser = new JavaScriptSerializer();
        while (true)
        {
            TcpClient client = null;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }

            try
            {
                using (client)
                {
                    var ns = client.GetStream();
                    var buf = new byte[65536];
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;

                    // 找 header 结束（按字节找 \r\n\r\n）
                    int he = -1;
                    for (int i = 0; i + 3 < n; i++)
                    {
                        if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                        {
                            he = i; break;
                        }
                    }
                    if (he < 0) continue;

                    string headers = Encoding.ASCII.GetString(buf, 0, he);
                    int cl = 0;
                    var m = System.Text.RegularExpressions.Regex.Match(
                        headers, @"Content-Length:\s*(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) cl = int.Parse(m.Groups[1].Value);

                    // **按字节累积**（不是字符数）
                    var ms = new MemoryStream();
                    int bodyStart = he + 4;
                    if (n - bodyStart > 0) ms.Write(buf, bodyStart, n - bodyStart);
                    while (ms.Length < cl)
                    {
                        int k = ns.Read(buf, 0, buf.Length);
                        if (k <= 0) break;
                        ms.Write(buf, 0, k);
                    }
                    string body = Encoding.UTF8.GetString(ms.ToArray());

                    int seq;
                    lock (gate) { seq = ++callCount; }
                    string resp = seq == 1 ? FirstRound(ser, targetFile) : SecondRound(ser, body);

                    var bb = Encoding.UTF8.GetBytes(resp);
                    var hb = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json; charset=utf-8\r\n"
                        + "Content-Length: " + bb.Length + "\r\n"
                        + "Connection: close\r\n\r\n");
                    ns.Write(hb, 0, hb.Length);
                    ns.Write(bb, 0, bb.Length);
                    ns.Flush();
                    ns.Close();
                }
            }
            catch { try { client.Close(); } catch { } }
        }
    }

    /// <summary>第 1 轮：要求模型调用 file 工具读目标文件。</summary>
    static string FirstRound(JavaScriptSerializer ser, string targetFile)
    {
        var args = new Dictionary<string, object>();
        args["action"] = "read";
        args["path"] = targetFile;

        var fn = new Dictionary<string, object>();
        fn["name"] = "file";
        fn["arguments"] = ser.Serialize(args);      // 转义交给 serializer

        var tc = new Dictionary<string, object>();
        tc["id"] = "call_1";
        tc["type"] = "function";
        tc["function"] = fn;

        var msg = new Dictionary<string, object>();
        msg["content"] = "";
        msg["tool_calls"] = new object[] { tc };

        return Wrap(ser, msg, 120, 25);
    }

    /// <summary>第 2 轮：如果请求里带了工具结果就给结论，否则说没看到。</summary>
    static string SecondRound(JavaScriptSerializer ser, string body)
    {
        bool saw = body.IndexOf(ExpectedFlag, StringComparison.Ordinal) >= 0
                   || body.IndexOf("\"tool\"", StringComparison.Ordinal) >= 0;

        var msg = new Dictionary<string, object>();
        msg["content"] = saw
            ? "文件里是 " + ExpectedFlag + "，任务完成。"
            : "没看到工具结果";

        return Wrap(ser, msg, 180, 30);
    }

    static string Wrap(JavaScriptSerializer ser, Dictionary<string, object> message,
        int promptTokens, int completionTokens)
    {
        var choice = new Dictionary<string, object>();
        choice["message"] = message;

        var usage = new Dictionary<string, object>();
        usage["prompt_tokens"] = promptTokens;
        usage["completion_tokens"] = completionTokens;

        var root = new Dictionary<string, object>();
        root["choices"] = new object[] { choice };
        root["usage"] = usage;
        return ser.Serialize(root);
    }

    static string Join(List<string> xs)
    {
        return xs.Count == 0 ? "(无)" : string.Join(" | ", xs.ToArray());
    }

    static string Clip(string s, int n)
    {
        if (s == null) return "(null)";
        s = s.Replace("\n", " ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    static void Report(string label, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + label);
        if (!ok && !string.IsNullOrEmpty(detail))
            Console.WriteLine("        " + Clip(detail, 350));
    }
}
