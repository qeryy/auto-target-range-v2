// 灵枢桌面版 —— 会话渲染性能测试
//
// ## 为什么要测这个
//
// 182 轮给气泡加了 Markdown 分块渲染。每条消息现在要：
//   1. `MarkdownLite.Parse` 切成块
//   2. 每块 `TextRenderer.MeasureText` 量一次
//   3. 绘制时每块再画一次
//
// 一条典型的模型回复有 8–12 块。会话页默认渲染 `InitialBubbles = 150` 个气泡，
// 那就是 **1200–1800 次 MeasureText**。`MeasureText` 在 GDI+ 里不便宜。
//
// 不测的话，用户打开一个大会话会发现卡三五秒——而这类问题在开发时
// 用自己的两三条消息的测试会话根本看不出来。
//
// ## 三个指标
//
//   1. **单次 Parse** —— 纯字符串逻辑，应该远低于 1ms
//   2. **打开 5000 条消息的会话** —— 构造 + Show，这里包含 150 个气泡的全部测量
//   3. **全窗口重绘** —— DrawToBitmap 走一遍所有 OnPaint
//
// ## 阈值怎么定的
//
// 都是「明显能感觉到卡」之上的宽松值：打开 < 4s、重绘 < 2s。
// 目标不是卡着线过，而是**一旦有个数量级的变化能被立刻发现**
// （比如某次改动让 Parse 变成 O(n²)）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SpiritScout.Core;
using SpiritScout.Ui;
using SpiritScout.Ui.Pages;
using SpiritScout.Ui.Widgets;
using Msg = SpiritScout.Core.Message;

class SessionPerf
{
    private static int _pass;
    private static int _fail;
    private static long _mid = 1;

    /// <summary>一条典型的模型回复：标题 + 段落 + 列表 + 代码块 + 引用 + 分隔线。</summary>
    private const string Sample =
        "### 先排除标准解码\n\n" +
        "标准字母表解出来是 Hello World，不像 flag。\n\n" +
        "- 原文 16 字符，末尾有 = 补位\n" +
        "- 解出 12 字节，长度正常\n" +
        "- 内容是可读英文，说明不是加密\n\n" +
        "### 下一步\n\n" +
        "> Base64 的字母表可以整体偏移。\n\n" +
        "```python\nimport base64\ns = \"SGVsbG8gV29ybGQ=\"\nfor i in range(1, 26):\n    std = \"ABC\"\n    rot = std[i:] + std[:i]\n    print(i)\n```\n\n" +
        "跑完就能出结果。\n\n---\n\n1. 标准解码\n2. 左移一位\n3. 左移两位 → 命中";

    [STAThread]
    private static int Main(string[] args)
    {
        string dataRoot = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ssperf");
        AppPaths.Override(dataRoot, "perf");
        Directory.CreateDirectory(dataRoot);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---------- 1) 纯解析 ----------
        var blocks = MarkdownLite.Parse(Sample);
        Console.WriteLine("  样本分块数: " + blocks.Count);

        const int N = 2000;
        var swP = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) MarkdownLite.Parse(Sample);
        swP.Stop();
        double perParse = swP.Elapsed.TotalMilliseconds / N;
        Console.WriteLine("  Parse " + N + " 次 = " + swP.ElapsedMilliseconds + "ms（" +
            perParse.ToString("F3") + " ms/次）");
        Report("单次 Parse < 1ms", perParse < 1.0, perParse.ToString("F3") + " ms");

        // ---------- 造 5000 条消息 ----------
        var store = new SessionStore();
        var sf = new SessionFile();
        sf.Session.Id = "big";
        sf.Session.Target = "5000 条消息压力测试";
        sf.Session.Mission = "跑一遍看打开要多久";
        sf.Session.Mode = RunMode.Ctf;
        sf.Session.Phase = SessionPhase.Executing;
        sf.Session.Tokens = 1234567;
        sf.Session.CreatedAt = DateTime.Now.AddHours(-20);
        sf.Session.UpdatedAt = DateTime.Now;
        sf.Messages = new List<Msg>();
        DateTime now = DateTime.Now;
        for (int i = 0; i < 5000; i++)
        {
            var m = new Msg();
            m.Id = _mid++;
            m.SessionId = "big";
            m.Role = (i % 3 == 0) ? "user" : (i % 3 == 1 ? "assistant" : "tool");
            m.Content = (i % 3 == 1)
                ? Sample
                : ("第 " + i + " 条消息的正文，用来把内容撑起来。");
            if (i % 3 == 2) m.Name = "file_read";
            m.CreatedAt = now.AddMinutes(-i);
            sf.Messages.Add(m);
        }
        sf.Session.MessageCount = sf.Messages.Count;
        store.Save(sf);
        Console.WriteLine("  已造 " + sf.Session.MessageCount + " 条消息的会话");

        var reg = new SpiritScout.Llm.ModelRegistry(AppPaths.ModelsFile,
            new SpiritScout.Llm.SecretStore(AppPaths.SecretsFile));

        // ---------- 2) 打开会话 ----------
        var sw = Stopwatch.StartNew();
        var view = new SessionView(store, reg, "big");
        var form = new Form();
        form.Size = new Size(1180, 900);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-3000, -3000);
        form.ShowInTaskbar = false;
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        Pump(300);
        sw.Stop();
        Console.WriteLine("  构造 + Show（5000 条）= " + sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
        Report("打开 5000 条消息的会话 < 4000ms", sw.Elapsed.TotalMilliseconds < 4000,
            sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms");

        int bubbles = 0;
        CountBubbles(view, ref bubbles);
        Console.WriteLine("  实际渲染气泡数: " + bubbles);
        Report("气泡数受 InitialBubbles 限制（<= 160）", bubbles <= 160, bubbles + "");
        Report("气泡数不为 0", bubbles > 0, bubbles + "");

        // ---------- 3) 全窗口重绘 ----------
        var sw2 = Stopwatch.StartNew();
        using (var bmp = new Bitmap(form.Width, form.Height))
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        sw2.Stop();
        Console.WriteLine("  全窗口重绘 = " + sw2.ElapsedMilliseconds + " ms");
        Report("全窗口重绘 < 2000ms", sw2.ElapsedMilliseconds < 2000, sw2.ElapsedMilliseconds + " ms");

        // ---------- 4) 滚动 30 次（重绘压力） ----------
        var sw3 = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
        {
            var sc = FindScrollable(view);
            if (sc != null && sc.VerticalScroll != null)
            {
                // 直接调整滚动位置，触发重绘
                try { sc.VerticalScroll.Value = Math.Min(sc.VerticalScroll.Maximum, i * 20); }
                catch { }
            }
            Pump(6);
        }
        sw3.Stop();
        Console.WriteLine("  30 次滚动 = " + sw3.ElapsedMilliseconds + " ms");
        Report("30 次滚动 < 3000ms", sw3.ElapsedMilliseconds < 3000, sw3.ElapsedMilliseconds + " ms");

        form.Close();
        form.Dispose();
        Pump(150);

        Console.WriteLine();
        Console.WriteLine("  合计: " + _pass + " PASS / " + _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    private static ScrollableControl FindScrollable(Control r)
    {
        foreach (Control c in r.Controls)
        {
            var sc = c as ScrollableControl;
            if (sc != null && sc.AutoScroll) return sc;
            var inner = FindScrollable(c);
            if (inner != null) return inner;
        }
        return null;
    }

    private static void CountBubbles(Control r, ref int n)
    {
        foreach (Control c in r.Controls)
        {
            if (c is ChatBubble) n++;
            if (c.HasChildren) CountBubbles(c, ref n);
        }
    }

    private static void Pump(int ms)
    {
        int end = Environment.TickCount + ms;
        while (Environment.TickCount < end)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(6);
        }
    }

    private static void Report(string label, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + label
            + (ok ? "" : "  (" + detail + ")"));
    }
}
