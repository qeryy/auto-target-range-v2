// 排版贴合检查（对话框 / 页面 / 会话气泡）
//
// 字号、DPI、缩放比例一变，最容易坏的是「文字和装它的盒子」的关系。
// UIA 那套检查（clip_check）只看得到有 Name 的控件，而且只量宽度；
// 这里用控件树自己量，补上三件它管不到的事：
//
//   1. 纵裁 —— 文字需要的高度 > 控件高度（AutoSize=false 的说明标签最常栽）
//   2. 压字 —— 同层两个控件的矩形互相盖住（标签顶进输入框、按钮压住说明文字）
//   3. 出窗 —— 控件底边掉到内容区外面（小屏或字号变大后按钮看不见）。
//      AutoScroll 容器里的内容超出去**能滚到**，不算问题，只有挂在不会滚的区域外才算。
//
// 另外单独量会话气泡：徽章（SealW）比首行还高时，气泡高度必须容得下它。
//
// 全程离屏构造，不 Show 窗口、不抢前台。
// 对话框能 DrawToBitmap 出像素（-Shots 目录）；**页面画出来是空白**
// —— 页面靠主窗体那套氛围底和真实宿主尺寸，离屏宿主没有。
// 页面的像素在 `build/_shots.ps1`（走 exe 的 --screenshot，真窗口渲染）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using SpiritScout.Ui;
using SpiritScout.Ui.Pages;
using SpiritScout.Ui.Widgets;

internal static class LayoutFit
{
    /// <summary>
    /// 页面**内容区**的设计尺寸，不是整窗客户区。
    /// 主窗体 972x661 设计，侧栏占 212（MainForm.LayoutSideWidth 的 17% 下限），
    /// 顶栏 40、状态栏 26 —— 页面实际只有这么大。用整窗量等于白给 200 多像素，
    /// 窄屏下真正会挤的地方全测不出来。
    /// 写物理值会被 ScaleTree 缩两次，页面量出来一片宽松，全是假绿。
    /// </summary>
    private static readonly Size HostClient = new Size(760, 595);

    private static int _problems;

    /// <summary>给了目录就把每个窗口离屏渲染成 png（DrawToBitmap，不显示、不抢前台）。</summary>
    private static string _shotDir;

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0) SpiritScout.Core.AppPaths.Override(args[0], "fit");
        if (args.Length > 1)
        {
            _shotDir = args[1];
            System.IO.Directory.CreateDirectory(_shotDir);
        }
        Theme.InitScale();

        Console.WriteLine("UiScale=" + Theme.UiScale.ToString("0.##") +
            " 正文=" + Theme.FontBody.Size.ToString("0.#") + "px" +
            " 字号增量=" + Theme.FontBump.ToString("0.#") + "pt");

        var dialogs = new List<string>();
        foreach (var name in new[]
        {
            "SpiritScout.Ui.Pages.InputDialog",
            "SpiritScout.Ui.Pages.LogViewer",
            "SpiritScout.Ui.Pages.AssignDialog",
            "SpiritScout.Ui.Pages.McpDialog",
            "SpiritScout.Ui.Pages.ProjectDialog",
            "SpiritScout.Ui.Pages.SkillDialog",
            "SpiritScout.Ui.Pages.ProviderDialog",
            "SpiritScout.Ui.Pages.ModelDialog",
            "SpiritScout.Ui.Pages.DistillDialog",
            "SpiritScout.Ui.Pages.DedupDialog",
        })
        {
            var t = typeof(MainForm).Assembly.GetType(name);
            if (t == null) { Console.WriteLine("[FAIL] 找不到对话框类型 " + name); _problems++; continue; }
            Form f = TryCreateDialog(t);
            if (f == null)
            {
                Console.WriteLine("[SKIP] " + Short(name) + " 需要真实数据才能构造");
                continue;
            }
            try
            {
                Theme.ScaleTree(f);   // 对话框的缩放在 Load 里做，离屏构造要手动补一次
                Audit(f, f, Short(name), dialogs);
                Shot(f, Short(name));
            }
            finally { f.Dispose(); }
        }

        foreach (var page in Pages())
        {
            Form host = null;
            try
            {
                host = new Form { ClientSize = HostClient, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None };
                var view = page.CreateView();
                view.Dock = DockStyle.Fill;
                host.Controls.Add(view);
                Theme.ScaleTree(host);
                // ScaleTree 期间 Theme.IsScaling=true，工具条的 Resize 回调会直接返回
                // （避免半缩放态算物理值）。所以缩完要**再逼一次真实 Resize**，
                // 否则右侧按钮还停在构建时的位置上，压字判据会报假案。
                var sz = host.ClientSize;   // 已是物理尺寸（ScaleTree 把窗体自己也缩了）
                host.ClientSize = new Size(sz.Width - 2, sz.Height);
                host.ClientSize = sz;
                NudgeLayout(host);          // 让右侧按钮回到真正靠右的位置（见方法注释）
                host.PerformLayout();
                Audit(host, view, "page:" + page.Id, dialogs);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SKIP] page:" + page.Id + " 离屏构建失败（" + ex.GetType().Name + "）");
            }
            finally { if (host != null) host.Dispose(); }
        }

        Bubbles();

        foreach (var d in dialogs) Console.WriteLine(d);
        Console.WriteLine(_problems == 0
            ? "[PASS] 排版贴合：对话框 + 13 页 + 会话气泡，0 处压字/裁切/出窗"
            : "[FAIL] 排版贴合：" + _problems + " 处问题");
        return _problems == 0 ? 0 : 1;
    }

    // ---------- 三类问题 ----------

    private static void Audit(Control root, Control view, string label, List<string> lines)
    {
        var texts = new List<Control>();
        Collect(view, texts);
        var hits = new List<string>();
        // 标签的框比字高（AutoSize 的行高含内部留白），压字要按「墨迹范围」判，
        // 否则行距本来就紧的表单会全是假阳性。
        var ink = new Dictionary<Control, Rectangle>();

        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            foreach (var t in texts)
            {
                string s = t.Text;
                ink[t] = t.Bounds;
                if (string.IsNullOrEmpty(s) || t.AutoSize) continue;

                Size one = TextRenderer.MeasureText(g, s, t.Font, Size.Empty, TextFormatFlags.NoPadding);
                Size wrapped = TextRenderer.MeasureText(g, s, t.Font, new Size(t.Width, 0),
                    TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);

                if (t is Button || t is CheckBox || t is RadioButton)
                {
                    // 按钮/开关是单行绘制（放不下就省略号），不存在折行救场
                    if (one.Width + Theme.K(10) > t.Width)
                        hits.Add("      挤字 " + Desc(t) + " 文字需 " + one.Width + " 只有 " + t.Width + "：" + Clip(s));
                }
                else if (t is Label)
                {
                    if (wrapped.Height > t.Height + 1)
                        hits.Add("      纵裁 " + Desc(t) + " 需 " + wrapped.Height + " 只有 " + t.Height + "：" + Clip(s));
                    // 只有开了 AutoEllipsis 的标签才是单行（放不下就省略号）；
                    // 普通标签会自动折行，宽度不够由上面的「纵裁」兜底。
                    if (((Label)t).AutoEllipsis && one.Width > t.Width + 1)
                        hits.Add("      挤字 " + Desc(t) + " 需 " + one.Width + " 只有 " + t.Width + "：" + Clip(s));
                }

                int bottom = FormRect(t, root).Bottom;
                // AutoScroll 容器里超出可视区的内容**是设计如此**（滚轮/滚动条能到），不算出窗。
                // 只有挂在「不滚动」的区域里才算真看不见——字号放大后这条判据才有意义。
                if (bottom > root.ClientSize.Height + 1 && !Scrollable(t))
                    hits.Add("      出窗 " + Desc(t) + " 窗内底 " + bottom + " > 客户区 " +
                        root.ClientSize.Height + "：" + Clip(s));
            }

            foreach (var t in texts)
            {
                var lb = t as Label;
                if (lb == null || string.IsNullOrEmpty(lb.Text)) continue;
                Size one = TextRenderer.MeasureText(g, lb.Text, lb.Font, Size.Empty, TextFormatFlags.NoPadding);
                ink[t] = new Rectangle(t.Left, t.Top, t.Width, Math.Min(t.Height, one.Height));
            }
        }

        foreach (var p in ParentsOf(texts))
        {
            var kids = new List<Control>();
            foreach (var t in texts) if (t.Parent == p && Visible(t)) kids.Add(t);
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    // 两种压字：文字×文字，以及标签文字压进输入框/下拉框
                    bool both = HasText(kids[i]) && HasText(kids[j]);
                    bool overBox = (HasText(kids[i]) && IsBox(kids[j])) || (HasText(kids[j]) && IsBox(kids[i]));
                    if (!both && !overBox) continue;
                    var a = Rectangle.Intersect(ink[kids[i]], ink[kids[j]]);
                    if (a.Width > 4 && a.Height > 4)
                        hits.Add("      压字 " + Desc(kids[i]) + " × " + Desc(kids[j]) + " 重叠 " +
                            a.Width + "x" + a.Height + "：" + Clip(kids[i].Text) + " / " + Clip(kids[j].Text));
                }
        }

        _problems += hits.Count;
        lines.Add((hits.Count == 0 ? "  [OK ] " : "  [BAD] ") + label.PadRight(22) +
            "客户区 " + root.ClientSize.Width + "x" + root.ClientSize.Height +
            " · 控件 " + texts.Count + " 个" + (hits.Count == 0 ? "" : " · " + hits.Count + " 处"));
        lines.AddRange(hits);
    }

    // ---------- 会话气泡 ----------

    private static void Bubbles()
    {
        var samples = new[]
        {
            "对 https://example.com 做一次完整的渗透测试评估，先摸清资产再逐个验证。",
            "# 结论\n\n发现 SQL 注入点：`/item?id=1`，布尔盲注可分。\n\n- 步骤一\n- 步骤二\n\n```sql\nSELECT 1\n```\n",
            "短",
        };
        var hits = new List<string>();
        int width = Theme.K(900);
        foreach (var role in new[] { BubbleRole.User, BubbleRole.Assistant, BubbleRole.System })
            foreach (var s in samples)
            {
                ChatBubble b = null;
                try
                {
                    b = new ChatBubble(role, s, width);
                    int need = Theme.K(5) + Theme.K(32);   // 徽章顶边 + 边长
                    if (role == BubbleRole.Assistant && b.Height < need)
                        hits.Add("      气泡容不下徽章 高 " + b.Height + " < " + need + "：" + Clip(s));
                    if (b.Width > width + Theme.K(2))
                        hits.Add("      气泡超宽 " + role + " 宽 " + b.Width + " > " + width + "：" + Clip(s));
                }
                catch (Exception ex) { hits.Add("      气泡构造失败 " + role + "：" + ex.GetType().Name); }
                finally { if (b != null) b.Dispose(); }
            }
        _problems += hits.Count;
        Console.WriteLine(hits.Count == 0
            ? "  [OK ] bubbles                会话气泡 9 个尺寸自洽"
            : "  [BAD] bubbles                " + hits.Count + " 处");
        foreach (var h in hits) Console.WriteLine(h);
    }

    // ---------- 工具 ----------

    private static void Shot(Form f, string name)
    {
        if (string.IsNullOrEmpty(_shotDir)) return;
        try
        {
            using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                bmp.Save(System.IO.Path.Combine(_shotDir, name + ".png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex) { Console.WriteLine("      离屏渲染失败 " + name + "：" + ex.GetType().Name); }
    }

    private static Form TryCreateDialog(Type t)
    {
        // 两轮：先给能默认构造的参数填真值，再把引用参数留 null ——
        // 有的对话框拿到「空对象」走的是首次新建分支，反而能正常建出界面。
        for (int pass = 0; pass < 2; pass++)
            foreach (var ctor in t.GetConstructors())
            {
                var ps = ctor.GetParameters();
                var args = new object[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt == typeof(string)) args[i] = "示例";
                    else if (pt == typeof(string[])) args[i] = new[] { "model-a", "model-b" };
                    else if (pt == typeof(List<SpiritScout.Core.Session>)) args[i] = new List<SpiritScout.Core.Session>();
                    else if (pt == typeof(SpiritScout.Llm.ModelRegistry))
                    {
                        try
                        {
                            args[i] = new SpiritScout.Llm.ModelRegistry(SpiritScout.Core.AppPaths.ModelsFile,
                                new SpiritScout.Llm.SecretStore(SpiritScout.Core.AppPaths.SecretsFile));
                        }
                        catch { ok = false; }
                    }
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else if (pass == 0 && pt.GetConstructor(Type.EmptyTypes) != null)
                    {
                        try { args[i] = Activator.CreateInstance(pt); } catch { args[i] = null; }
                    }
                    else args[i] = null;
                }
                if (!ok) continue;
                try
                {
                    var f = ctor.Invoke(args) as Form;
                    if (f != null) return f;
                }
                catch { }
            }
        return null;
    }

    private static List<IPage> Pages()
    {
        var list = new List<IPage>();
        foreach (var t in typeof(MainForm).Assembly.GetTypes())
        {
            if (t.IsAbstract || t.IsInterface) continue;
            if (!typeof(IPage).IsAssignableFrom(t)) continue;
            if (t.GetConstructor(Type.EmptyTypes) == null) continue;
            try
            {
                var p = Activator.CreateInstance(t) as IPage;
                if (p != null) list.Add(p);
            }
            catch { }
        }
        list.Sort(delegate (IPage a, IPage b) { return string.CompareOrdinal(a.Id, b.Id); });
        return list;
    }

    private static void Collect(Control c, List<Control> outList)
    {
        if (!(c is Form)) outList.Add(c);   // 窗体自身不算控件：客户区本来就比窗口小一圈
        foreach (Control k in c.Controls) Collect(k, outList);
    }

    private static List<Control> ParentsOf(List<Control> ts)
    {
        var r = new List<Control>();
        foreach (var t in ts) if (t.Parent != null && !r.Contains(t.Parent)) r.Add(t.Parent);
        return r;
    }

    private static Rectangle FormRect(Control c, Control root)
    {
        int x = c.Left, y = c.Top;
        var p = c.Parent;
        while (p != null && p != root) { x += p.Left; y += p.Top; p = p.Parent; }
        return new Rectangle(x, y, c.Width, c.Height);
    }

    /// <summary>
    /// 自顶向下递归逼一次布局。页头的右侧按钮定位挂在 Dock=Top 面板的 Resize 上，
    /// 离屏窗体不会自己走这一遍；不铺到位就会拿「构建时宽度」算出来的 X 去判压字，
    /// 报出按钮跑到标题上面、甚至 X 为负的假案。
    /// </summary>
    private static void NudgeLayout(Control c)
    {
        c.PerformLayout();
        foreach (Control k in c.Controls) NudgeLayout(k);
    }

    /// <summary>祖先里有没有可滚动容器（AutoScroll）。滚动容器外的内容才叫「看不见」。</summary>
    private static bool Scrollable(Control c)
    {
        var p = c.Parent;
        while (p != null)
        {
            var sc = p as ScrollableControl;
            if (sc != null && sc.AutoScroll) return true;
            p = p.Parent;
        }
        return false;
    }

    private static bool HasText(Control c)
    {
        if (string.IsNullOrEmpty(c.Text)) return false;
        return c is Label || c is Button || c is CheckBox || c is RadioButton;
    }

    private static bool IsBox(Control c)
    {
        string n = c.GetType().Name;
        return c is TextBoxBase || c is ComboBox || c is ListBox || c is DateTimePicker
            || n.IndexOf("Select", StringComparison.Ordinal) >= 0
            || n.IndexOf("Combo", StringComparison.Ordinal) >= 0;
    }

    private static bool Visible(Control c) { return c.Width > 2 && c.Height > 2; }

    private static string Desc(Control c)
    {
        return c.GetType().Name + "@" + c.Left + "," + c.Top + " " + c.Width + "x" + c.Height;
    }

    private static string Short(string fullName)
    {
        int i = fullName.LastIndexOf('.');
        return i < 0 ? fullName : fullName.Substring(i + 1);
    }

    private static string Clip(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 16 ? s : s.Substring(0, 16) + "…";
    }
}
