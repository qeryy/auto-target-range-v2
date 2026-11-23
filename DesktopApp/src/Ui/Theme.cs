// 灵枢桌面版 —— 主题与控件工厂
//
// 把所有颜色、字体、常用控件收在一处：
//   * 改风格只改这个文件（比如整体换成浅色）；
//   * 各页面不写死颜色，天然一致。
//
// 配色与 web 版深色主题对齐（底 #0d1117 / 面板 #161b22 / 主色 #2f81f7），
// 这样从浏览器切到桌面版是同一个产品的观感。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpiritScout.Ui
{
    public static class Theme
    {
        // ===== SaltUI 风格的圆角与投影 =====
        //
        // SaltUI（Kotlin/Compose）里按钮是**胶囊**（半径 = 高度/2），卡片 12–20，
        // 对话框 20。之前这边按钮半径固定 7、卡片 12、没有阴影，看起来就是
        // 「带圆角的方块」而不是它那种「浮起来的软块」。
        //
        // 这里把三件事补齐：胶囊、分级圆角、柔和投影。

        /// <summary>
        /// 8 个 agent 的标识色。取自 web 版 CSS 的 `--agent-1` … `--agent-8`
        /// （深色主题下的那组）。给每个 agent 一个固定颜色，列表里一眼能分开。
        /// </summary>
        public static Color[] AgentColors =
        {
            Color.FromArgb(0x6e, 0xa8, 0xdc),   // 1 蓝
            Color.FromArgb(0xd0, 0x86, 0x6a),   // 2 橙
            Color.FromArgb(0x74, 0xa9, 0x8c),   // 3 绿
            Color.FromArgb(0xc1, 0xa7, 0x65),   // 4 金
            Color.FromArgb(0xbd, 0x8a, 0xa3),   // 5 粉
            Color.FromArgb(0x82, 0xac, 0x82),   // 6 草
            Color.FromArgb(0x9b, 0x92, 0xcb),   // 7 紫
            Color.FromArgb(0xd0, 0x8b, 0x85),   // 8 砖
        };

        /// <summary>按索引取 agent 色（循环使用）。</summary>
        public static Color AgentColor(int index)
        {
            if (AgentColors.Length == 0) return Accent;
            int i = index % AgentColors.Length;
            if (i < 0) i += AgentColors.Length;
            return AgentColors[i];
        }

        /// <summary>
        /// 紫色强调色（web 版 `--violet: #a371f7`）。
        /// 用来给「灵枢」的回复做标识——用户是蓝、助手是紫，一眼能分。
        /// </summary>
        public static Color Violet = Color.FromArgb(0xa3, 0x71, 0xf7);

        /// <summary>
        /// 混色（对应 CSS 的 `color-mix(in srgb, a Wa%, b)`）。
        /// `weightA` 是 a 的占比(0–1)，剩下给 b。
        ///
        /// 气泡的底色就是这么来的：`Mix(Accent, Surface, 0.11)` —
        /// 不是纯色实心，而是一层很淡的色雾罩在卡片色上。
        /// </summary>
        public static Color Mix(Color a, Color b, double weightA)
        {
            if (weightA < 0) weightA = 0;
            if (weightA > 1) weightA = 1;
            double wb = 1 - weightA;
            return Color.FromArgb(
                (int)Math.Round(a.A * weightA + b.A * wb),
                (int)Math.Round(a.R * weightA + b.R * wb),
                (int)Math.Round(a.G * weightA + b.G * wb),
                (int)Math.Round(a.B * weightA + b.B * wb));
        }

        /// <summary>
        /// 四角不同半径的圆角矩形（左上/右上/右下/左下）。
        ///
        /// 聊天气泡靠这个「不对称圆角」表达方向：说话人那一侧的角收小到 4px，
        /// 其余三个角 13px。四角一样圆的看起来就像个普通卡片。
        /// </summary>
        public static GraphicsPath RoundPathCorners(Rectangle r, int tl, int tr, int br, int bl)
        {
            var path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }

            int half = Math.Min(r.Width, r.Height) / 2;
            tl = Math.Max(0, Math.Min(tl, half));
            tr = Math.Max(0, Math.Min(tr, half));
            br = Math.Max(0, Math.Min(br, half));
            bl = Math.Max(0, Math.Min(bl, half));

            // 每条边先画到「距离角 r」的位置，再决定是弧还是直角
            if (tl > 0) path.AddArc(r.X, r.Y, tl * 2, tl * 2, 180, 90);
            else path.AddLine(r.X, r.Y, r.X, r.Y);

            if (tr > 0) path.AddArc(r.Right - tr * 2, r.Y, tr * 2, tr * 2, 270, 90);
            else path.AddLine(r.Right, r.Y, r.Right, r.Y);

            if (br > 0) path.AddArc(r.Right - br * 2, r.Bottom - br * 2, br * 2, br * 2, 0, 90);
            else path.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);

            if (bl > 0) path.AddArc(r.X, r.Bottom - bl * 2, bl * 2, bl * 2, 90, 90);
            else path.AddLine(r.X, r.Bottom, r.X, r.Bottom);

            path.CloseFigure();
            return path;
        }

        /// <summary>胶囊（半径取高度的一半）。</summary>
        public const int RadiusPill = -1;
        public const int RadiusSm = 8;    // 小控件：标签、chip
        public const int RadiusMd = 6;    // 常规卡片（web: var(--radius) = 6px）
        public const int RadiusLg = 16;   // 大卡片
        public const int RadiusXl = 20;   // 对话框

        /// <summary>
        /// 按半径生成圆角矩形路径。radius &lt; 0 表示胶囊（取 min(w,h)/2）。
        /// **调用方 using 释放**。
        /// </summary>
        public static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }

            int rad = radius < 0 ? Math.Min(r.Width, r.Height) / 2 : radius;
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            if (rad <= 0) { path.AddRectangle(r); return path; }

            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 在路径下方画一层柔和投影（模拟 elevation）。
        ///
        /// 做法：路径四边对称外扩，一圈一圈从外往里叠（GDI+ 没有高斯模糊，
        /// 靠多层半透明黑叠加出近似渐隐）。
        ///
        /// **`room` 是卡片矩形之外真正剩下的像素，不是想要的扩散半径。**
        /// 原来传的是 ShadowSpread（150% 下 13px），而卡面只留了 pad/2（7px）——
        /// 投影被控件边界硬裁一刀，贴卡边留下一条近黑的带（「框旁边有黑边」），
        /// 而且环只往下偏，顶边那条留白压根没画。按可用留白定环数、四边对称后，
        /// 卡边最深约 18%、外沿正好淡出到 5%，没有硬边。
        /// </summary>
        public static void DrawShadow(Graphics g, Rectangle r, int radius, int room, int alpha)
        {
            if (room <= 0 || alpha <= 0) return;
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                for (int i = room; i >= 1; i--)
                {
                    var sr = new Rectangle(r.X - i, r.Y - i, r.Width + 2 * i, r.Height + 2 * i);
                    // 每环覆盖它自己以内的全部面积，所以最里侧是逐环累加：
                    // 按 i 归一后总强度≈alpha，且从卡边到外沿单调递减。
                    int a = Math.Max(1, alpha * 2 * i / (room * room));
                    using (var path = RoundPath(sr, radius < 0 ? -1 : radius + i))
                    using (var b = new SolidBrush(Color.FromArgb(a, 0, 0, 0)))
                    {
                        g.FillPath(b, path);
                    }
                }
            }
            finally { g.SmoothingMode = mode; }
        }

        /// <summary>
        /// 给对话框套上 Win11 圆角。**在构造函数里调一次即可**——
        /// 内部订阅 HandleCreated，句柄还没建也能用。
        ///
        /// 用法：`public MyDialog() { ...; Theme.ApplyWin11(this); }`
        /// </summary>
        public static void ApplyWin11(Form form)
        {
            if (form == null) return;
            if (form.Icon == null) form.Icon = AppIcon();
            if (form.IsHandleCreated) Win11.RoundCorners(form.Handle);
            else form.HandleCreated += delegate { Win11.RoundCorners(form.Handle); };
            DarkScrollTree(form);
            // 对话框的控件在构造函数里建（ApplyWin11 可能在首行也可能在末行），
            // Load 时整棵树已建好且尚未显示 —— 统一缩放的唯一可靠时机。
            form.Load += delegate
            {
                ScaleTree(form);
                FitWindow(form);
            };
            form.Shown += delegate { EnsureOnScreen(form); };
        }

        // ---------- 程序图标 ----------

        private static Icon _appIcon;

        /// <summary>
        /// 灵枢图标（窗口标题栏、任务栏、托盘气泡共用一个实例，别 Dispose）。
        ///
        /// **`-win32icon` 只管 exe 文件图标，运行时拿不到** —— 窗口/任务栏
        /// 不显式设 `Form.Icon` 就是 WinForms 默认的那个通用图标，用户看到的就是
        /// 「图标还是什么都没有」。所以构建脚本除了 `/win32icon:` 还要
        /// `/resource:assets\icon.ico,icon.ico` 把它嵌成清单资源。
        ///
        /// 取不到嵌入资源时退到 exe 自身关联图标，再退到系统应用图标。
        /// </summary>
        public static Icon AppIcon()
        {
            if (_appIcon != null) return _appIcon;
            try
            {
                var st = typeof(Theme).Assembly.GetManifestResourceStream("icon.ico");
                if (st != null) { using (st) _appIcon = new Icon(st); }
            }
            catch { _appIcon = null; }
            if (_appIcon == null)
            {
                try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { _appIcon = null; }
            }
            if (_appIcon == null) _appIcon = SystemIcons.Application;
            return _appIcon;
        }

        // ---------- 窗口分辨率自适应 ----------

        /// <summary>
        /// 把窗口夹进所在屏幕的工作区：尺寸上限、MinimumSize、以及夹小后
        /// 让客户区可滚动，最后按剩余空间重新摆放。
        ///
        /// 为什么必须做：对话框的 `ClientSize` / `MinimumSize` 写的是设计像素，
        /// `ScaleTree` 之后整体乘 UiScale —— 150% 屏上 SkillDialog 是 1140x930、
        /// DedupDialog 1320x780（物理）。大屏放得下，1366x768 的旧笔记本
        /// 去掉任务栏只剩 728 高就放不下：窗口底边和「确定/取消」在屏幕外，
        /// 而 MinimumSize 比屏幕还高时连拖拽改变都做不到（窗口卡死在裁切态）。
        /// </summary>
        public static void FitWindow(Form form) { FitWindow(form, Rectangle.Empty); }

        /// <summary>
        /// 同上，但可用第二个参数伪造工作区（自检/探针传小屏尺寸用）；
        /// 传 Rectangle.Empty 表示用真实屏幕。
        /// </summary>
        internal static void FitWindow(Form form, Rectangle workingAreaOverride)
        {
            if (form == null || form.IsDisposed) return;
            var wa = (workingAreaOverride.Width > 0 && workingAreaOverride.Height > 0)
                ? workingAreaOverride
                : SafeWorkingArea(form);

            // 上下左右各留 8 设计像素：贴屏幕边时 Win11 圆角会被切，
            // 而且贴边的窗口拖动手势不好使。
            int maxW = Math.Max(K(320), wa.Width - K(16));
            int maxH = Math.Max(K(240), wa.Height - K(16));

            // MinimumSize 比工作区还大 → 先夹住，否则窗口永远放不下且无法缩放
            var min = form.MinimumSize;
            if (min.Width > maxW || min.Height > maxH)
                form.MinimumSize = new Size(Math.Min(min.Width, maxW), Math.Min(min.Height, maxH));

            if (form.Width <= maxW && form.Height <= maxH) return;

            int cw = form.ClientSize.Width, ch = form.ClientSize.Height;
            form.Size = new Size(Math.Min(form.Width, maxW), Math.Min(form.Height, maxH));

            // 夹小之后原来绝对定位在底部的控件会掉到客户区外。开 AutoScroll
            // 并把内容尺寸登记成夹小前的大小，用户滚一下就能够着。
            if (form.ClientSize.Width < cw || form.ClientSize.Height < ch)
            {
                form.AutoScroll = true;
                form.AutoScrollMinSize = new Size(cw, ch);
            }

            Core.ConfigManager.Log("窗口自适应: 工作区 " + wa.Width + "x" + wa.Height
                + " @ " + UiScale.ToString("0.##") + "x → " + form.Text
                + " 收到 " + form.Width + "x" + form.Height
                + (form.AutoScroll ? "（内容可滚动）" : ""));
            EnsureOnScreen(form);
        }

        /// <summary>
        /// 保证整个窗口落在所在屏工作区内（CenterParent 在多屏/父窗靠边时
        /// 会算出负坐标，标题栏跑到屏幕外就没法拖动）。
        /// </summary>
        public static void EnsureOnScreen(Form form)
        {
            if (form == null || form.IsDisposed) return;
            if (form.WindowState != FormWindowState.Normal) return;
            var wa = SafeWorkingArea(form);
            int x = form.Left, y = form.Top;
            if (form.Width <= wa.Width) x = Math.Min(Math.Max(x, wa.Left), wa.Right - form.Width);
            else x = wa.Left;
            if (form.Height <= wa.Height) y = Math.Min(Math.Max(y, wa.Top), wa.Bottom - form.Height);
            else y = wa.Top;
            if (x != form.Left || y != form.Top) form.Location = new Point(x, y);
        }

        private static Rectangle SafeWorkingArea(Form form)
        {
            try
            {
                var b = Screen.FromControl(form).WorkingArea;
                if (b.Width > 0 && b.Height > 0) return b;
            }
            catch { }
            return Screen.PrimaryScreen.WorkingArea;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        /// <summary>
        /// 原生滚动条压成深色（Win10 1809+ / Win11 的 explorer 暗主题）。
        /// AutoScroll 面板、表格、多行文本框的白条滚动栏贴在深色软面上像贴了白纸。
        /// 整棵控件树递归挂：之后才加进来的子控件由 ControlAdded 接住。
        /// </summary>
        public static void DarkScrollTree(Control c)
        {
            if (c == null) return;
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else c.HandleCreated += delegate { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); };
            c.ControlAdded += delegate(object s, ControlEventArgs e) { DarkScrollTree(e.Control); };
            foreach (Control ch in c.Controls) DarkScrollTree(ch);
        }

        // ---------- 配色 ----------
        // ===== 配色取自 SaltUI（Moriafly/SaltUI）的暗色主题 =====
        //
        // SaltUI 是 Kotlin/Compose Multiplatform 的 UI 库，**不能直接引用**到 C# WinForms。
        // 这里按它的 SaltColors.defaultDark() 逐值对齐：
        //   highlight    = #0088FF   → Accent
        //   text         = #EBEEF1   → Text
        //   subText      = #E1E6EB@75% over bg → Muted（推导值 #B1B4B8）
        //   background   = #202020   → Bg
        //   subBackground= #1C1C1E   → Surface
        //
        // 派生（Material 3 的 surfaceContainer 逐级提亮）：SurfaceAlt/Hover/HoverRow
        // **Line 没跟 SaltUI**：它的 stroke 是 subText@10%（≈#333434），
        // 分隔线几乎看不见；这里保留更亮的 #444d58。
        //
        // 对比度实测：text/bg 13.99（AAA）、muted/bg 7.83（AAA，比原来 6.15 更好）、
        //            accent/bg 4.63（AA）。

        // **色阶必须逐级变亮：Bg < Surface < SurfaceAlt。**
    //
    // 2026-11-19 用户第二次说「界面还是太丑」，查出来是这个：
    //   原来 Bg(#202020, 亮度 32) > Surface(#1C1C1E, 亮度 28)
    //   —— **卡片比页面背景还暗，看起来是凹进去的**，而不是浮起来。
    //
    // 对比 web 版（GitHub Dark 体系）：
    //   --bg #0d1117(16.8) < --surface #161b22(26.4) < --surface-2 #21262d(37.1)
    //   逐级变亮，卡片自然浮起。
    //
    // 之前照 SaltUI 抄了 background(#202020) / subBackground(#1C1C1E)，
    // 但 **subBackground 是给输入框这类「内凹」元素用的次级背景，不是卡片色**。
    // 我把它当卡片色用了，层次就反了。
    public static Color Bg = Color.FromArgb(17, 17, 19);
        public static Color Surface = Color.FromArgb(32, 32, 32);
        // 交替行。原来 (19,24,31) 与 Surface (22,27,34) 只差 3 个色阶，
        // 对比度 1.03:1 —— 斑马纹等于没有。提到 (31,39,49) 后是 1.15:1，
        // 扫长表格时能跟上行，同时不刺眼。
        public static Color SurfaceAlt = Color.FromArgb(40, 40, 42);
        // 悬停/选中。必须与 SurfaceAlt 拉开（否则斑马纹行悬停时看不出变化），
        // 但也不能太亮——(44,52,64) 会让次要文字对比掉到 4.08。
        // (38,45,56) 是两者之间的平衡点：与交替行 1.09:1，次要文字 4.51:1。
        public static Color Hover = Color.FromArgb(44, 44, 46);

        /// <summary>
        /// 侧栏底色。介于 Bg(17) 与 Surface(32) 之间：比内容区亮一点形成「侧栏是
        /// 一块面」的层次，又比卡片暗，不会抢内容。原来侧栏直接用 Surface，
        /// 和卡片同色，整条侧栏像一块灰板压在黑底上。
        /// </summary>
        public static Color Sidebar = Color.FromArgb(23, 23, 26);

        /// <summary>
        /// 鼠标悬停在表格行上的底色。
        ///
        /// 要介于「斑马行」（SurfaceAlt 31,39,49）和「选中行」（Hover 38,45,56）之间：
        /// 比斑马行亮才看得出悬停，比选中色暗才不会被误认为「选中了」。
        /// </summary>
        public static Color HoverRow = Color.FromArgb(40, 40, 42);
        public static Color Accent = Color.FromArgb(0, 136, 255);
        public static Color AccentHover = Color.FromArgb(26, 149, 255);
        /// <summary>
        /// 亮蓝文字色（web 版 `--accent-ink: #539bf5`）。
        /// 用在「软高亮底 + 亮蓝字」的选中态（导航/会话项），比纯 Accent 更柔和、
        /// 在深色软底上对比也更好。实心主按钮仍用 Accent。
        /// </summary>
        public static Color AccentInk = Color.FromArgb(0x53, 0x9b, 0xf5);
        public static Color Text = Color.FromArgb(235, 238, 241);
        public static Color Muted = Color.FromArgb(177, 180, 184);
        // 分隔线 / 表格网格线的颜色。
        //
        // 原来是 (48,54,61)，在 Surface 上对比度只有 1.42 —— 表格线几乎看不见。
        // 按 WCAG 算了一下：文字部分全部达标（正文 AAA 16.02，说明文字 AA 6.15），
        // 只有线不达标。线不是文字，WCAG 对装饰元素没有硬要求，但 1.42 确实太淡。
        //
        // 调到 (68,77,88)：对比度 2.02（在 Surface 上）/ 2.21（在 Bg 上），
        // 比原来亮 40%，看得见轮廓但不抢眼。参考 GitHub 暗色主题的边框约 1.6，
        // 这里略高一点是因为本项目的表格是主要内容区，网格需要更清楚。
        public static Color Line = Color.FromArgb(68, 77, 88);

        /// <summary>
        /// 更暗的分隔线（web 版 `--line: #30363d`）。
        ///
        /// `Line` 现在对应 web 的 `--line-strong: #444c56`。卡片常态用这根暗的，
        /// 悬停时切到亮的 `Line` —— 就是 web 版 `.card:hover { border-color: var(--line-strong) }`。
        /// **表格网格线仍用 `Line`**：表格需要看得清，不能跟着变暗。
        /// </summary>
        public static Color LineDark = Color.FromArgb(48, 54, 61);
        public static Color Ok = Color.FromArgb(63, 185, 80);

        // ===== 「浅色背景」版的状态色 =====
        //
        // web 版 CSS 里有 --ok-soft / --warn-soft / --bad-soft / --accent-soft，
        // 用法是「浅色底 + 同色字」的胶囊标签。深色主题下这些 soft 就是
        // 同色 12-15% 的 alpha —— 在 #1C1C1E 上透出来是一层很淡的色块。
        public static Color OkSoft = Color.FromArgb(30, 63, 185, 80);
        public static Color WarnSoft = Color.FromArgb(30, 210, 153, 34);
        public static Color DangerSoft = Color.FromArgb(30, 248, 81, 73);
        public static Color AccentSoft = Color.FromArgb(38, 0, 136, 255);
        public static Color MutedSoft = Color.FromArgb(26, 177, 180, 184);
        public static Color Warn = Color.FromArgb(210, 153, 34);
        public static Color Danger = Color.FromArgb(248, 81, 73);

        // ================= 主题 =================
        //
        // `ApplyTheme(false)` = 浅色（web 的 :root 变量）
        // `ApplyTheme(true)`  = 深色（web 的 dark 变量，桌面原本这套）
        //
        // 颜色常量全部换成 static（非 readonly）就是为了这一刻能重赋。
        // 引用点全部拿到运行时值 —— 主题切换即刻生效，不用重启。

        /// <summary>当前是不是深色。切换按钮、状态栏显示用。</summary>
        /// <summary>
        /// 当前是不是深色。
        ///
        /// **默认 true** —— 因为字段里预置的那套配色就是深色。以前默认 false，
        /// 于是「深色启动」时这个标志是错的（配色深、标志说浅），
        /// 点一下主题按钮反而切到深色（看起来没反应），再点才到浅色。
        /// </summary>
        public static bool IsDark { get; private set; }

        static Theme()
        {
            IsDark = true;   // 预置配色 = 深色，标志必须跟着对上
        }

        /// <summary>
        /// 应用主题。**调用后要让全窗口 Invalidate**（颜色变了得重画）。
        /// </summary>
        public static void ApplyTheme(bool dark)
        {
            ApplyTheme(dark, null);
        }

        /// <summary>
        /// 切主题，并（可选）把已有控件的 BackColor/ForeColor 从旧角色色搬到新角色色。
        ///
        /// **为什么需要这一步**：控件建的时候写 `BackColor = Theme.Sidebar`，
        /// BackColor 是值类型，赋值那一刻就把色值拷死了。静态字段 Theme.Sidebar
        /// 后来变了，已赋值的控件属性不跟着变 —— 只有自绘控件（每次 OnPaint
        /// 重读 Theme.*）例外。所以运行时切主题必须回头遍历一遍。
        ///
        /// 做法不是「每个控件记住自己该用什么色」，而是**按值反查角色**：
        /// 切前抓一份旧色表，切后抓一份新色表，控件现在的颜色正好等于旧表里
        /// 某一项，就说明它当初取的是那个角色，换成新表同下标的值即可。
        /// 这样不用改任何一处构建代码，新加的控件也自动被覆盖。
        /// </summary>
        public static void ApplyTheme(bool dark, Control root)
        {
            var swAll = System.Diagnostics.Stopwatch.StartNew();
            Color[] oldRoles = SnapshotRoles();
            IsDark = dark;
            // **清掉按旧主题烘焙的渐变缓存** —— 否则深色切浅色后
            // 侧栏/页头还是带着深色光晕（_sharedAmbient 是静态 Bitmap，
            // 它按第一次画的色存着，不 Invalidate 就一直用）。
            AmbientPanel.ClearCache();
            if (dark)
            {
                // ---- 深色（web @media dark 的 :root，桌面原值）----
                Bg          = Color.FromArgb(17, 17, 19);      // #111113
                Surface     = Color.FromArgb(32, 32, 32);      // #202020
                SurfaceAlt  = Color.FromArgb(40, 40, 42);      // #28282A
                Hover       = Color.FromArgb(44, 44, 46);      // #2C2C2E
                Sidebar     = Color.FromArgb(23, 23, 26);      // #17171A
                HoverRow    = Color.FromArgb(40, 40, 42);
                Accent      = Color.FromArgb(0, 136, 255);     // #0088FF
                AccentHover = Color.FromArgb(26, 149, 255);    // #1A95FF
                AccentInk   = Color.FromArgb(0x53, 0x9b, 0xf5);
                Text        = Color.FromArgb(235, 238, 241);   // #EBEEF1
                Muted       = Color.FromArgb(177, 180, 184);   // #B1B4B8
                Line        = Color.FromArgb(68, 77, 88);      // #444D58
                LineDark    = Color.FromArgb(48, 54, 61);      // #30363D
                Ok          = Color.FromArgb(63, 185, 80);     // #3FB950
                OkSoft      = Color.FromArgb(30, 63, 185, 80);
                WarnSoft    = Color.FromArgb(30, 210, 153, 34);
                DangerSoft  = Color.FromArgb(30, 248, 81, 73);
                AccentSoft  = Color.FromArgb(38, 0, 136, 255);
                MutedSoft   = Color.FromArgb(26, 177, 180, 184);
                Warn        = Color.FromArgb(210, 153, 34);    // #D29922
                Danger      = Color.FromArgb(248, 81, 73);     // #F85149
                Violet      = Color.FromArgb(0xa3, 0x71, 0xf7);
            }
            else
            {
                // ---- 浅色（web :root 变量，1:1 抄）----
                Bg          = Color.FromArgb(0xf6, 0xf8, 0xfa); // #f6f8fa
                Surface     = Color.FromArgb(0xff, 0xff, 0xff); // #ffffff
                SurfaceAlt  = Color.FromArgb(0xf2, 0xf4, 0xf7); // #f2f4f7
                Hover       = Color.FromArgb(0xe8, 0xea, 0xed);
                Sidebar     = Color.FromArgb(0xf0, 0xf2, 0xf5);
                HoverRow    = Color.FromArgb(0xe8, 0xea, 0xed);
                Accent      = Color.FromArgb(0x09, 0x69, 0xda); // #0969da
                AccentHover = Color.FromArgb(0x07, 0x57, 0xba); // #0757ba
                AccentInk   = Color.FromArgb(0x09, 0x69, 0xda);
                Text        = Color.FromArgb(0x1f, 0x23, 0x28); // #1f2328
                Muted       = Color.FromArgb(0x59, 0x63, 0x6e); // #59636e
                Line        = Color.FromArgb(0xd8, 0xde, 0xe4); // #d8dee4
                LineDark    = Color.FromArgb(0xbc, 0xc4, 0xcd); // #bcc4cd
                Ok          = Color.FromArgb(0x1a, 0x7f, 0x37); // #1a7f37
                OkSoft      = Color.FromArgb(26, 26, 127, 55);
                WarnSoft    = Color.FromArgb(26, 154, 103, 0);
                DangerSoft  = Color.FromArgb(26, 207, 34, 46);
                AccentSoft  = Color.FromArgb(23, 9, 105, 218);
                MutedSoft   = Color.FromArgb(13, 89, 99, 110);
                Warn        = Color.FromArgb(0x9a, 0x67, 0x00); // #9a6700
                Danger      = Color.FromArgb(0xcf, 0x22, 0x2e); // #cf222e
                Violet      = Color.FromArgb(0x82, 0x50, 0xdf); // #8250df
            }

            long msPalette = swAll.ElapsedMilliseconds;

            // 界面已经在的话，把控件属性搬到新色（见方法注释）。
            int nCtl = 0;
            if (root != null)
                nCtl = RecolorTree(root, oldRoles, SnapshotRoles());

            // 切主题本来就是低频动作，但用户报了两次「卡」—— 留一行数字，
            // 下次再报就不用猜是调色板、遍历还是重绘。
            if (root != null)
            {
                try
                {
                    Core.ConfigManager.Log("[theme] 切换 → " + (dark ? "深色" : "浅色")
                        + "：调色板 " + msPalette + "ms · 重着色 " + nCtl + " 控件 "
                        + (swAll.ElapsedMilliseconds - msPalette) + "ms · 合计 "
                        + swAll.ElapsedMilliseconds + "ms");
                }
                catch { }
            }
        }

        /// <summary>
        /// 把「另一套主题」的氛围底先烘好（约 100ms）。启动后调一次，
        /// 之后切主题就只是换指针，不用再算那张三团光晕 + 噪点的位图。
        ///
        /// 转发给 `AmbientPanel` —— 底图缓存归它管（它才是绘制方）。
        /// </summary>
        public static void PrewarmOtherAmbient(int w, int h)
        {
            AmbientPanel.PrewarmOtherAmbient(w, h);
        }

        /// <summary>
        /// 22 个「角色色」按固定顺序抓成数组。切主题前后各抓一次，靠下标对应。
        ///
        /// **顺序绝对不能改** —— 它就是前后两份快照的对应关系。
        /// 加新角色色时只能往后追加，不能插在中间。
        /// </summary>
        private static Color[] SnapshotRoles()
        {
            return new Color[]
            {
                Bg, Surface, SurfaceAlt, Hover, Sidebar, HoverRow,   // 0-5  面/底
                Accent, AccentHover, AccentInk, AccentSoft,          // 6-9  主色
                Text, Muted, MutedSoft,                              // 10-12 字
                Line, LineDark,                                      // 13-14 线
                Ok, OkSoft, Warn, WarnSoft, Danger, DangerSoft,      // 15-20 语义
                Violet,                                              // 21   紫
            };
        }

        /// <summary>按值反查角色下标；不是角色色就返回 -1。</summary>
        private static int RoleIndex(Color[] roles, Color c)
        {
            int argb = c.ToArgb();
            for (int i = 0; i < roles.Length; i++)
                if (roles[i].ToArgb() == argb) return i;
            return -1;
        }

        /// <summary>
        /// 遍历控件树，把「旧角色色」换成「新角色色」。
        ///
        /// 跳过 A==0（Transparent / Empty）—— 那是「没设色」，不能换成实色，
        /// 否则所有透明控件会变成一块块实心，比原来的 bug 还难看。
        ///
        /// 已知小瑕疵：深色下 SurfaceAlt 和 HoverRow 都是 #28282A（设计上本来
        /// 就不区分），反查会命中 SurfaceAlt；浅色下两者不同（#f2f4f7 / #e8eaed），
        /// 于是原本的 HoverRow 会拿到 SurfaceAlt 的浅灰。差 2% 灰度，肉眼不分。
        /// </summary>
        private static int RecolorTree(Control root, Color[] from, Color[] to)
        {
            int visited = 0;
            var stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control c = stack.Pop();

                Color b = c.BackColor;
                if (b.A != 0)
                {
                    int i = RoleIndex(from, b);
                    if (i >= 0 && to[i].ToArgb() != b.ToArgb()) c.BackColor = to[i];
                }

                Color f = c.ForeColor;
                if (f.A != 0)
                {
                    int i = RoleIndex(from, f);
                    if (i >= 0 && to[i].ToArgb() != f.ToArgb()) c.ForeColor = to[i];
                }

                visited++;
                for (int k = 0; k < c.Controls.Count; k++)
                    stack.Push(c.Controls[k]);
            }
            return visited;
        }

        // ---------- 字体 ----------
        //
        // 字体名不能写死。`new Font("不存在的字体", ...)` 不会抛异常，而是**静默回退到
        // Microsoft Sans Serif**——那是西文字体，中文会显示得又扁又难看。英文版
        // Windows、精简版系统、Win7 上就可能没有「Microsoft YaHei UI」。
        //
        // 所以这里先探测已装字体，按优先级挑第一个可用的：
        //   界面：雅黑 UI（Win8+ 自带）→ 雅黑（Win7）→ 微软正黑（繁中）→
        //         苹方/思源（若有）→ Segoe UI（西文兜底）→ 系统默认
        //   等宽：Consolas（Win7+ 自带）→ Cascadia Mono（Win Terminal）→
        //         Courier New → 系统默认
        internal static readonly string UiFamily = PickFamily(
            "Microsoft YaHei UI", "Microsoft YaHei", "Microsoft JhengHei UI",
            "Microsoft JhengHei", "PingFang SC", "Source Han Sans SC",
            "Noto Sans CJK SC", "Segoe UI");

        private static readonly string MonoFamily = PickFamily(
            "Consolas", "Cascadia Mono", "Cascadia Code", "Courier New");

        /// <summary>按优先级挑第一个已装字体；都没有就用系统默认。</summary>
        private static string PickFamily(params string[] candidates)
        {
            try
            {
                var installed = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var f in FontFamily.Families) installed.Add(f.Name);
                foreach (var c in candidates)
                {
                    if (installed.Contains(c)) return c;
                }
            }
            catch { }
            try { return SystemFonts.DefaultFont.Name; }
            catch { return "Microsoft Sans Serif"; }
        }

        // **正文字号原来对齐 web，现在整体再加两个字号。**
    // web 的主力字号是 12px(127 次) / 11px(93) / 13px(63)，桌面原来取 10pt = 13.3px
    // 贴住 web。2026-11-20 用户要求「文本放大两个字号」，改为在 web 口径上统一
    // 加 FontBump —— 观感和 web 不再逐像素一致，这是有意的取舍。
    //
    // **像素单位 + UiScale（2026-11-19 DPI 修复）**：point 字体会跟着 GDI 的设备 DPI
    // 自己放大，而布局常量不会 —— 150% 屏上字大盒小，全界面挤成一团。
    // 改成像素单位后字号完全由 UiScale 决定，与 Control.Scale 缩放后的盒尺寸同比例。
    // 懒加载：UiScale 在 Program 入口 InitScale() 里定，首次取字体时才建缓存。
    //
    // **全局字号增量 FontBump（2026-11-20）**：应用户要求「文本整体放大两个字号」。
    // 全站字体都从 `F(pt)` 出去（等宽与楷体 em 走 `Fpx`，同一增量），
    // 所以整体调大或调回只改这一个常量，不用逐页翻。
    // 下面各行注释的 px 是**加上 FontBump 之后**的设计像素（100% 屏；150% 屏再乘 1.5）。
    // **全局字体增量**：所有 Font* 都在它的基础上再 +Bump。
    // 2F → 3F：+1pt ≈ +1.3px，正文 16px → 17.3px，和 web 的 13px 一致偏上一档，
    // 也满足「更大更舒适」的要求。
    public const float FontBump = 3F;

    public static Font FontBody { get { return _fontBody ?? (_fontBody = F(10F)); } }        // 16px
        public static Font FontSmall { get { return _fontSmall ?? (_fontSmall = F(8.5F)); } }   // 14px
        public static Font FontTiny { get { return _fontTiny ?? (_fontTiny = F(8F)); } }      // 13.3px
        public static Font FontTitle { get { return _fontTitle ?? (_fontTitle = F(16F, FontStyle.Bold)); } }    // 24px
        public static Font FontHeading { get { return _fontHeading ?? (_fontHeading = F(12F, FontStyle.Bold)); } }  // 18.7px
        public static Font FontSection { get { return _fontSection ?? (_fontSection = F(8.5F, FontStyle.Bold)); } } // 区块名 14px
        public static Font FontSession { get { return _fontSession ?? (_fontSession = F(9.5F, FontStyle.Bold)); } } // 侧栏会话标题 15.3px
        public static Font FontMetric { get { return _fontMetric ?? (_fontMetric = F(18F, FontStyle.Bold)); } }   // 26.7px
        public static Font FontMono { get { return _fontMono ?? (_fontMono = Fpx(MonoFamily, 9.5F, FontStyle.Regular)); } }  // 15.3px

        private static Font _fontBody, _fontSmall, _fontTiny, _fontTitle, _fontHeading,
            _fontSection, _fontSession, _fontMetric, _fontMono, _fontHero, _fontHeroEm;

        // ===== 首页 hero 大标题（对齐 web .lead / .lead-em）=====
        // web 用 clamp(32,3.5vw,48) 的黑体大标题 + 楷体渐变「灵枢」。
        // 桌面取 30pt 黑体、34pt 楷体 em（加 FontBump 后 42.7 / 48px），静态缓存（GDI 句柄坑）。
        public static Font FontHero { get { return _fontHero ?? (_fontHero = F(30F, FontStyle.Bold)); } }
        private static readonly string EmFamily = PickFamily(
            "STKaiti", "KaiTi", "Kaiti SC", "FangSong", "Microsoft YaHei UI");
        public static Font FontHeroEm { get { return _fontHeroEm ?? (_fontHeroEm = Fpx(EmFamily, 34F, FontStyle.Bold)); } }

        // ---------- DPI 自适应 ----------
        //
        // 真值取自屏幕 DC 的 LOGPIXELSX：150% 缩放 = 144。WinForms 的 DeviceDpi /
        // AutoScale 在无 app.config 的单文件 exe 上不可靠（实测恒 96），所以自己读。
        // 进程声明为系统 DPI aware（manifest dpiAware=true），一次会话一个比例；
        // 所有控件树构造完后统一 Control.Scale(UiScale)，自绘常量走 K()/Kf()。
        public static float UiScale = 1f;

        public static int K(int v) { return (int)Math.Round(v * UiScale); }
        public static float Kf(float v) { return v * UiScale; }

        /// <summary>设计 pt 字号 → 当前比例下的像素字体（含全局增量 FontBump）。</summary>
        public static Font F(float pt) { return F(pt, FontStyle.Regular); }
        public static Font F(float pt, FontStyle style)
        {
            return Fpx(UiFamily, pt, style);
        }

        /// <summary>指定字体名的版本：等宽、楷体 em 用，字号口径与 `F()` 完全一致。</summary>
        private static Font Fpx(string family, float pt, FontStyle style)
        {
            return new Font(family, Kf((pt + FontBump) * 96F / 72F), style, GraphicsUnit.Pixel);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        /// <summary>入口调用一次：读真实屏幕 DPI，定 UiScale。必须在任何 UI 构造前。</summary>
        public static void InitScale()
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int dpi = GetDeviceCaps(dc, 88);   // LOGPIXELSX
                ReleaseDC(IntPtr.Zero, dc);
                if (dpi >= 48 && dpi <= 384) UiScale = dpi / 96f;
            }
            catch { }
        }

        /// <summary>
        /// 控件树构造完后统一缩放一次（bounds/Padding/Margin/TL 绝对行高都随缩；
        /// 字体已是像素×UiScale，DataGridView 行高在各控件里自行 K()）。
        /// 只在 UiScale != 1 时动手，100% 屏上零开销零变化。
        /// </summary>
        public static void ScaleTree(Control c)
        {
            if (c == null || UiScale == 1f) return;
            object done;
            if (_scaled.TryGetValue(c, out done)) return;   // 同一棵树不缩第二次
            // 缩放过程中 ClientSizeChanged/Resize 会同步触发：那些回调里按
            // 「物理像素」算的布局（如侧栏宽）若在此刻插一手，会被再乘一次缩放。
            IsScaling = true;
            try { c.Scale(new SizeF(UiScale, UiScale)); }
            finally { IsScaling = false; }
            _scaled.Add(c, null);
        }

        /// <summary>ScaleTree 正在缩放中控件树。布局回调见此标志应直接返回。</summary>
        public static bool IsScaling { get; private set; }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>
            _scaled = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>();

        /// <summary>控件所在的树是否已被 ScaleTree 缩放过（往上找根）。</summary>
        public static bool IsScaled(Control c)
        {
            while (c != null)
            {
                object v;
                if (_scaled.TryGetValue(c, out v)) return true;
                c = c.Parent;
            }
            return false;
        }

        /// <summary>
        /// 布局回调的单位系数：树已缩放 → UiScale（回调拿到物理尺寸，常量要 K）；
        /// 还没缩放 → 1（回调拿到设计尺寸，常量用设计值，之后随 ScaleTree 一起缩）。
        /// 配合 IsScaling 跳过缩放中途的回调，避免「半缩放态算物理值再被缩一次」。
        /// </summary>
        public static float UnitOf(Control c)
        {
            return IsScaled(c) ? UiScale : 1f;
        }

        /// <summary>当前实际用的界面字体名（关于页/自检可以展示）。</summary>
        public static string UiFontName { get { return UiFamily; } }
        public static string MonoFontName { get { return MonoFamily; } }

        // ---------- 控件工厂 ----------

        /// <summary>
        /// 页面标题区（标题 + 说明 + 右侧动作按钮）。透明底，浮在氛围背景上。
        ///
        /// 动作条从右边缘往左排，**排到标题右边就换行**：字号放大两个字号后按钮变宽，
        /// 「本地工具」这种 5 件套（4 按钮 + 搜索框）在 150% 屏的页面宽度里一行放不下，
        /// 老写法会把最左的按钮顶到标题上、窗口窄时甚至顶出负坐标
        /// （fit_check 抓到「技能」「本地工具」两页）。
        /// 说明行永远排在标题与动作条两者更低的下边，行高跟着动作行数长。
        /// </summary>
        public static Panel PageHeader(string title, string subtitle, params Control[] actions)
        {
            bool hasSub = !string.IsNullOrEmpty(subtitle);
            var head = new Panel
            {
                Dock = DockStyle.Top,
                Height = hasSub ? 72 : 56,
                BackColor = Color.Transparent,
            };

            var lbl = new Label
            {
                Text = title,
                Font = FontTitle,
                ForeColor = Text,
                AutoSize = true,
                Location = new Point(0, 0),
                BackColor = Color.Transparent,
            };
            head.Controls.Add(lbl);

            Label sub = null;
            if (hasSub)
            {
                sub = new Label
                {
                    Text = subtitle,
                    Font = FontSmall,
                    ForeColor = Muted,
                    AutoSize = true,
                    Location = new Point(2, Math.Max(36, lbl.Bottom + 2)),
                    BackColor = Color.Transparent,
                };
                head.Controls.Add(sub);
            }

            if (actions != null && actions.Length > 0)
            {
                foreach (var a in actions)
                {
                    if (a != null) head.Controls.Add(a);
                }
                Action layout = () =>
                {
                    if (IsScaling) return;
                    float u = UnitOf(head);
                    int gap = (int)Math.Round(8 * u);
                    int top = (int)Math.Round(10 * u);

                    int btnH = 0;
                    foreach (var a in actions) if (a != null && a.Height > btnH) btnH = a.Height;

                    // 标题占了多宽，动作条就只能用剩下的部分（留一个间距）
                    int left = lbl.Right + gap;
                    int x = head.Width;
                    int row = 0;
                    for (int i = actions.Length - 1; i >= 0; i--)
                    {
                        var a = actions[i];
                        if (a == null) continue;
                        // 这一行放不下（再往左就压到标题）→ 起新行，仍靠右排
                        if (x - a.Width < left) { row++; x = head.Width; }
                        x -= a.Width + gap;
                        a.Location = new Point(x + gap, top + row * (btnH + gap));
                    }
                    int rows = row + 1;
                    int contentBottom = Math.Max(top + rows * btnH + (rows - 1) * gap, lbl.Bottom);
                    if (hasSub)
                    {
                        sub.Location = new Point(2, contentBottom + 4);
                        contentBottom = sub.Bottom;
                    }
                    int need = Math.Max(contentBottom + 12, 56);
                    if (head.Height != need) head.Height = need;
                };
                head.Resize += (s, e) => layout();
                head.HandleCreated += (s, e) => layout();
                layout();
            }
            return head;
        }

        /// <summary>
        /// 区块小标题（「按模式」「服务器」这种）。加粗小字 + 下留白，
        /// 读起来是「一段内容的名字」，不是一条表格抬头。
        /// </summary>
        public static Label Section(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = Muted,
                Font = FontSection,
                TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(2, 0, 0, 8),
                BackColor = Color.Transparent,
            };
        }

        /// <summary>
        /// 表单里一行的说明标签（放在输入框左边）。
        ///
        /// 原来 McpPage / ModelDialogs（两个内部类）/ ProjectsPage / SkillDialog
        /// 五处各写了一份**一字不差**的 Row() 方法，现在收到这里。
        /// </summary>
        public static Label FormLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y + 3),
                AutoSize = true,
                ForeColor = Muted,
                Font = FontSmall,
            };
        }

        /// <summary>主按钮（蓝色实心、圆角，与 web 端一致）。</summary>
        public static Button Primary(string text, int width = 104, int height = 34)
        {
            return Widgets.FlatButtons.Primary(text, width, height);
        }

        /// <summary>次按钮（描边、圆角）。</summary>
        public static Button Ghost(string text, int width = 88, int height = 34)
        {
            return Widgets.FlatButtons.Ghost(text, width, height);
        }

        /// <summary>危险按钮（删除，红字红边、圆角）。</summary>
        public static Button DangerBtn(string text, int width = 88, int height = 34)
        {
            return Widgets.FlatButtons.Danger(text, width, height);
        }

        /// <summary>输入框。</summary>
        public static TextBox Input(bool multiline = false, int height = 26)
        {
            // DarkTextBox 继承 TextBox：调用点类型不变，但边框换成深色、
            // 且支持 Placeholder（见 Widgets/DarkTextBox.cs）。
            var t = new Widgets.DarkTextBox
            {
                BackColor = Surface,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = FontBody,
                Multiline = multiline,
            };
            if (multiline)
            {
                t.Height = height;
                t.ScrollBars = ScrollBars.Vertical;
            }
            return t;
        }

        /// <summary>下拉框。</summary>
        /// <summary>
        /// 可编辑下拉（DropDown 风格）：既能从候选里选，也能手填。
        /// 用于「模型 ID」这种——常见值给候选，但端点上的真实名字可能不在列表里。
        /// </summary>
        public static ComboBox Combo(bool editable = true)
        {
            var c = new DarkCombo
            {
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                BackColor = Surface,
                ForeColor = Text,
                FlatStyle = FlatStyle.Flat,
                Font = FontBody,
                IntegralHeight = false,
                DropDownHeight = K(220),
            };
            DarkenCombo(c);
            return c;
        }

        public static ComboBox Select(params string[] items)
        {
            var c = new DarkCombo
            {
                BackColor = Surface,
                ForeColor = Text,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = FontBody,
            };
            DarkenCombo(c);
            if (items != null && items.Length > 0)
            {
                c.Items.AddRange(items);
                c.SelectedIndex = 0;
            }
            return c;
        }

        /// <summary>
        /// DropDownList 的显示区由系统主题画（白底黑字），BackColor 管不到它。
        /// 只有 OwnerDraw 才能把显示区和列表项一起压成深色。
        /// </summary>
        private static void DarkenCombo(ComboBox c)
        {
            c.DrawMode = DrawMode.OwnerDrawFixed;
            // ItemHeight 不被 Control.Scale 缩放，直接给物理值
            c.ItemHeight = K(24);
            c.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                var box = (ComboBox)sender;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                Color back = sel ? Mix(Accent, Surface, 0.22) : Surface;
                using (var b = new SolidBrush(back))
                    e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index < 0 || e.Index >= box.Items.Count) return;
                Color fore = (e.State & DrawItemState.Disabled) != 0 ? Muted : Text;
                TextRenderer.DrawText(e.Graphics, box.Items[e.Index].ToString(), box.Font,
                    new Rectangle(e.Bounds.X + K(8), e.Bounds.Y, e.Bounds.Width - K(12), e.Bounds.Height),
                    fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        /// <summary>
        /// 全自绘下拉。DropDownList 的显示区和箭头按钮都由系统主题画（白底），
        /// BackColor 管显示区要靠 OwnerDraw，管箭头按钮则毫无办法——
        /// 所以接管 WM_PAINT / WM_PRINT，整张脸自己画；弹出列表仍走原生 + OwnerDraw 项。
        /// DropDown（可编辑）风格的编辑框是原生子窗口，会自己盖在脸上面，不受影响。
        /// </summary>
        private sealed class DarkCombo : ComboBox
        {
            private const int WM_PAINT = 0x000F;
            private const int WM_ERASEBKGND = 0x0014;
            private const int WM_PRINT = 0x0317;
            private const int WM_PRINTCLIENT = 0x0318;

            [StructLayout(LayoutKind.Sequential)]
            private struct PaintStruct
            {
                public IntPtr hdc;
                public bool fErase;
                public int left, top, right, bottom;
                public bool fRestore;
                public bool fIncUpdate;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                public byte[] reserved;
            }

            [DllImport("user32.dll")]
            private static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct ps);
            [DllImport("user32.dll")]
            private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct ps);

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
                if (m.Msg == WM_PAINT)
                {
                    PaintStruct ps;
                    IntPtr hdc = BeginPaint(Handle, out ps);
                    using (var g = Graphics.FromHdc(hdc))
                        PaintFace(g, DropDownStyle == ComboBoxStyle.DropDownList, true);
                    EndPaint(Handle, ref ps);
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINT || m.Msg == WM_PRINTCLIENT)
                {
                    // 先让原生把子控件（可编辑风格的编辑框）印进 DC，再叠自己的脸。
                    // 可编辑风格只叠按钮条，不然会盖掉编辑框印出来的文字。
                    base.WndProc(ref m);
                    bool list = DropDownStyle == ComboBoxStyle.DropDownList;
                    using (var g = Graphics.FromHdc(m.WParam))
                        PaintFace(g, list, list);
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }

            private void PaintFace(Graphics g, bool withText, bool fullFace)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int w = Width, h = Height;
                int btn = SystemInformation.VerticalScrollBarWidth;
                using (var b = new SolidBrush(BackColor))
                {
                    if (fullFace) g.FillRectangle(b, 0, 0, w, h);
                    else g.FillRectangle(b, w - btn - 1, 0, btn + 1, h);
                }
                if (withText)
                {
                    string text = SelectedItem == null ? "" : SelectedItem.ToString();
                    TextRenderer.DrawText(g, text, Font,
                        new Rectangle(K(8), 0, w - btn - K(12), h), Enabled ? ForeColor : Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                using (var pen = new Pen(Enabled ? Muted : LineDark, Kf(1.6f)))
                {
                    float cx = w - btn / 2f, cy = h / 2f;
                    g.DrawLines(pen, new[] {
                        new PointF(cx - Kf(4), cy - Kf(2)),
                        new PointF(cx, cy + Kf(2.5f)),
                        new PointF(cx + Kf(4), cy - Kf(2)) });
                }
                if (Focused)
                    using (var pen = new Pen(Mix(Accent, LineDark, 0.5), Kf(1)))
                        g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            }
        }




        /// <summary>格式化 token 数（1.2M / 8.3K）。</summary>
        /// <summary>
        /// 按 token 总量与单价估算成本。
        ///
        /// 单价为 0（没填）时返回空串——调用方据此决定要不要显示这一列。
        /// </summary>
        public static string Cost(long tokens, double pricePerMillionCny)
        {
            if (pricePerMillionCny <= 0 || tokens <= 0) return "";
            double cny = tokens / 1000000.0 * pricePerMillionCny;
            if (cny < 0.01) return "≈¥" + cny.ToString("F4");
            if (cny < 1) return "≈¥" + cny.ToString("F3");
            return "≈¥" + cny.ToString("F2");
        }

        public static string Tokens(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("F2") + "M";
            if (n >= 1000) return (n / 1000.0).ToString("F1") + "K";
            return n.ToString();
        }

        /// <summary>
        /// 压成一行（状态栏/日志用）。
        ///
        /// 实现挪到了 `Core.Text.OneLine` —— 这层原来和另外三个层各有一份。
        /// 这里保留是为了不改 UI 层的调用点，是个转发。
        /// </summary>
        public static string OneLine(string s, int max = 160)
        {
            return Core.Text.OneLine(s, max);
        }
    }

    /// <summary>
    /// 软面卡片：大圆角、**默认无描边**、柔投影、顶部可选色晕。
    ///
    /// 2026-11-19 用户第三次反馈「页面全是方框，要柔顺一点」。方框感的来源是
    /// 1px 描边 + 12px 小圆角 + 直角表格线。这里把语言换掉：
    ///   · 描边默认不画（BorderWidth=0），靠「比背景亮一档 + 投影」浮起来；
    ///   · 圆角 16；悬停不亮边框，而是投影加深 + 一圈极淡白光（抬升感）；
    ///   · Tint 给顶部一层 12% 的色晕，让「板块」有分类色而不是一圈彩边。
    ///
    /// 全部画在 OnPaintBackground：透明子控件（Label/透明 Panel）模拟透明时
    /// 采样的是父控件这一层，画在 OnPaint 里它们底下会露纯色补丁。
    /// </summary>
    public class Card : Panel
    {
        public int Radius { get; set; }
        public Color BorderColor { get; set; }

        /// <summary>顶部色晕的颜色（Color.Empty = 不画）。板块分类色用它。</summary>
        public Color Tint { get; set; }

        /// <summary>色晕高度（像素），往下渐变回卡片底色。</summary>
        public int TintHeight { get; set; }

        public Card()
        {
            // web 的 .card 是 border-radius: var(--radius) = 6px。
            // RadiusLg（16）留给了真的「大卡片」（ModeCard 的模式卡、对话框等），
            // 普通 Card 全用 RadiusMd（6）—— 卡片的圆角本来就该是小小的那种。
            Radius = Theme.RadiusMd;
            BorderColor = Theme.LineDark;
            Tint = Color.Empty;
            TintHeight = 76;
            ShadowAlpha = 46;
            ShadowSpread = 9;
            HighlightTop = true;
            BorderWidth = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Surface;
        }

        /// <summary>
        /// 描边宽度。1 = 常态，2 = 选中（对应 web 版 `.home-mode-card.on` 里
        /// `box-shadow: 0 0 0 1px inset` 那种「加粗内描边」）。
        /// </summary>
        public int BorderWidth { get; set; }

        /// <summary>顶边是否画一条淡高光（默认开）。</summary>
        public bool HighlightTop { get; set; }

        /// <summary>投影强度（0 = 不画）。默认给一点，让卡片「浮」起来。</summary>
        public int ShadowAlpha { get; set; }
        /// <summary>投影扩散像素。</summary>
        public int ShadowSpread { get; set; }

        /// <summary>自绘子控件铺底：采样同一幅卡面（含色晕），圆角外才无缝。</summary>
        public static void PaintBase(Control c, Graphics g)
        {
            AmbientPanel.PaintBase(c, g);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintSurface(e.Graphics);
        }

        internal void PaintSurface(Graphics g)
        {
            // 卡面由（尺寸 + 外观参数）唯一决定，构造后基本不变：
            // 大卡缓存成位图，每帧只剩一次 blit（多层投影叠加是主要开销）；
            // 小卡直接画，建缓存反而更贵。
            int key = AppearanceKey();
            bool cached = _surface != null && _scW == Width && _scH == Height && _scKey == key;
            if (cached)
            {
                g.DrawImageUnscaled(_surface, 0, 0);
                return;
            }
            if ((long)Width * Height >= SurfaceCacheMinPx && Width > 1 && Height > 1)
            {
                if (_surface != null)
                {
                    _surface.Dispose();
                    _surface = null;
                }
                var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var gg = Graphics.FromImage(bmp))
                    RenderSurface(gg);
                _surface = bmp;
                _scW = Width;
                _scH = Height;
                _scKey = key;
                g.DrawImageUnscaled(_surface, 0, 0);
                return;
            }
            _surface = null;
            _scW = -1;
            _scH = -1;
            RenderSurface(g);
        }

        private const long SurfaceCacheMinPx = 60000;   // 约 300x200 以下不缓存
        private Bitmap _surface;
        private int _scW = -1, _scH = -1, _scKey;

        private int AppearanceKey()
        {
            int k = BackColor.ToArgb();
            k = k * 397 ^ (Tint.IsEmpty ? 0 : Tint.ToArgb());
            k = k * 397 ^ BorderColor.ToArgb();
            k = k * 397 ^ ShadowAlpha;
            k = k * 397 ^ ShadowSpread;
            k = k * 397 ^ Radius;
            k = k * 397 ^ BorderWidth;
            k = k * 397 ^ (HighlightTop ? 1 : 0);
            return k;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _surface != null)
            {
                _surface.Dispose();
                _surface = null;
            }
            base.Dispose(disposing);
        }

        private void RenderSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 留出投影的空间（不然会被卡片自己的边界裁掉）
            int spread = Theme.K(ShadowSpread);
            int radius = Theme.K(Radius);
            int pad = ShadowAlpha > 0 ? spread + 1 : 0;
            var rect = new Rectangle(pad / 2, pad / 2, Width - 1 - pad, Height - 1 - pad);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // **先把整张画布铺成所在面的底色**，卡面才是全不透明的。
            // 不铺底时投影环是「半透明黑」留在透明画布上，控件每次重绘都被
            // 再合成一遍，贴卡边的暗带越刷越深（实测最深 43%，单次叠加只有 17%）
            // ——就是用户说的「框旁边有黑边」。铺底后底色自己就是 255 alpha，
            // 重绘等于覆盖，暗带深浅只由 ShadowAlpha 一处决定。
            AmbientPanel.PaintAncestorBase(this, g);

            if (ShadowAlpha > 0)
                Theme.DrawShadow(g, rect, radius, pad / 2, ShadowAlpha);

            using (var path = Theme.RoundPath(rect, radius))
            using (var fill = new SolidBrush(BackColor))
            {
                g.FillPath(fill, path);
            }

            // 顶部色晕：分类色 12% 罩在卡面上，往下渐隐。板块的「身份」靠它，
            // 不靠一圈彩边——彩边就是用户说的「方框」。
            if (!Tint.IsEmpty && rect.Height > Theme.K(8))
            {
                int th = Math.Min(Theme.K(TintHeight), rect.Height);
                var tr = new Rectangle(rect.X, rect.Y, rect.Width, th);
                using (var clip = Theme.RoundPath(rect, radius))
                {
                    var old = g.Clip;
                    g.SetClip(clip, CombineMode.Intersect);
                    using (var gb = new LinearGradientBrush(tr,
                        Theme.Mix(Tint, BackColor, 0.13), BackColor, LinearGradientMode.Vertical))
                        g.FillRectangle(gb, tr);
                    g.Clip = old;
                }
            }

            // 描边只在显式要求时画（选中态 / composer 聚焦态）。常态无描边：
            // 卡面靠「亮一档 + 投影 + 色晕」浮起来，不靠一圈线。
            if (BorderWidth > 0)
            {
                using (var pen = new Pen(BorderColor, Theme.Kf(BorderWidth)))
                {
                    pen.Alignment = PenAlignment.Inset;
                    using (var path = Theme.RoundPath(rect, radius))
                        g.DrawPath(pen, path);
                }
            }

            // 顶部一条很淡的高光：模拟「光从上面来」。
            if (HighlightTop && rect.Width > radius * 2 + Theme.K(4))
            {
                int hx = rect.X + radius;
                int hw = rect.Width - radius * 2;
                if (hw > Theme.K(2))
                {
                    using (var gb = new LinearGradientBrush(
                        new Rectangle(hx, rect.Y + 1, hw, Theme.K(3)),
                        Color.FromArgb(22, 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255),
                        LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(gb, hx, rect.Y + 1, hw, Theme.K(2));
                    }
                }
            }
        }
    }

    /// <summary>
    /// 氛围背景面板：纯黑底 + 三团极淡的色光（蓝/紫/金）+ 细噪点。
    ///
    /// 首页内容只占中间一列，四周原本是大片死黑——「空」而不是「留白」。
    /// 色光让空白处有空气感，噪点去掉纯色的塑料感。alpha 都压在 22 以下，
    /// 远看仍是深色底，不会抢内容。
    ///
    /// **画在 OnPaintBackground 而不是 OnPaint**：WinForms 的透明子控件
    /// （Label 等）模拟透明时调的是父控件的 OnPaintBackground；画在 OnPaint
    /// 里透明标签底下会露出一块纯色补丁。
    /// 自绘控件（ModeCard/FlatButton/HeroTitle）的矩形四角则通过
    /// <see cref="PaintBase"/> 采样同一幅背景，保证圆角外无缝。
    /// </summary>
    public sealed class AmbientPanel : Panel
    {
        private static Bitmap _noise;

        public AmbientPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Bg;
        }

        /// <summary>
        /// 自绘子控件铺底：向上找最近的氛围面板或软卡，采样同一幅背景，
        /// 圆角/色晕之外才无缝。中间隔着的透明 Panel 会把偏移累加进去。
        /// </summary>
        public static void PaintBase(Control c, Graphics g)
        {
            PaintBaseFrom(c, c, g, 0, 0);
        }

        /// <summary>
        /// 软卡**自己**铺底用：铺祖先那一层。不能走 <see cref="PaintBase"/>——
        /// 它的循环从控件本身起步，Card 传进去第一步就命中自己，
        /// PaintSurface → PaintBase → PaintSurface 无限递归。
        /// </summary>
        public static void PaintAncestorBase(Control c, Graphics g)
        {
            PaintBaseFrom(c, c.Parent, g, c.Left, c.Top);
        }

        private static void PaintBaseFrom(Control c, Control from, Graphics g, int ox, int oy)
        {
            for (Control p = from; p != null; p = p.Parent)
            {
                var amb = p as AmbientPanel;
                var card = p as Card;
                if (amb != null || card != null)
                {
                    var st = g.Save();
                    g.TranslateTransform(-ox, -oy);
                    if (amb != null) amb.PaintAmbient(g);
                    else card.PaintSurface(g);
                    g.Restore(st);
                    return;
                }
                ox += p.Left;
                oy += p.Top;
            }
            // 没有软面祖先（对话框、侧栏头部按钮等）：沿父链取第一个不透明底色。
            // 注意从 Parent 起步：自绘按钮自己的 BackColor 是 Button 默认值
            // （SystemColors.Control，浅灰），拿它铺底会露出一块浅色补丁。
            Color back = Theme.Bg;
            for (Control p = c.Parent; p != null; p = p.Parent)
            {
                if (p.BackColor != Color.Transparent) { back = p.BackColor; break; }
            }
            using (var b = new SolidBrush(back))
                g.FillRectangle(b, c.ClientRectangle);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintAmbient(e.Graphics);
        }

        /// <summary>
        /// 画氛围底（背景色 + 三团光晕 + 噪点）。
        ///
        /// **深/浅各缓存一份，切主题只换指针、不重算。**
        ///
        /// 为什么不是按 (w,h) 缓存一份就够：三团 PathGradient 大椭圆 + 整屏噪点
        /// 是软件渲染，150% 缩放下实测 ~100ms/张。以前只有一份，切主题就
        /// ClearCache，于是每次切都要重付这 100ms（侧栏和内容区尺寸不同 = 两张）。
        /// 用户报过两次「切主题好卡」，根因就在这儿 —— Theme.ApplyTheme 本身
        /// 只花 2ms（46 个控件重着色），慢的全是这里。
        ///
        /// 内存代价：每张 w×h×4 字节，1400×900 下约 5 MB。两份 = 10 MB，换
        /// 「切主题零等待」值。
        /// </summary>
        private void PaintAmbient(Graphics g)
        {
            int w = Width, h = Height;
            if (w < 4 || h < 4) return;

            if (Theme.IsDark)
            {
                if (_ambientDark == null || _wDark != w || _hDark != h)
                {
                    if (_ambientDark != null) _ambientDark.Dispose();
                    _ambientDark = BakeAmbient(w, h, true);
                    _wDark = w; _hDark = h;
                }
                g.DrawImageUnscaled(_ambientDark, 0, 0);
            }
            else
            {
                if (_ambientLight == null || _wLight != w || _hLight != h)
                {
                    if (_ambientLight != null) _ambientLight.Dispose();
                    _ambientLight = BakeAmbient(w, h, false);
                    _wLight = w; _hLight = h;
                }
                g.DrawImageUnscaled(_ambientLight, 0, 0);
            }
        }

        private static Bitmap BakeAmbient(int w, int h, bool dark)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var gg = Graphics.FromImage(bmp))
                RenderAmbient(gg, w, h, dark);
            try
            {
                Core.ConfigManager.Log("[theme] 烘氛围底 " + w + "x" + h
                    + (dark ? " 深色" : " 浅色") + "：" + sw.ElapsedMilliseconds + "ms");
            }
            catch { }
            return bmp;
        }

        private static Bitmap _ambientDark;
        private static Bitmap _ambientLight;
        private static int _wDark = -1, _hDark = -1;
        private static int _wLight = -1, _hLight = -1;

        /// <summary>
        /// 切主题时**不需要清缓存** —— 两份按 IsDark 分开存，天然隔离。
        /// 保留这个方法是为了不动调用点；真正的失效条件是尺寸变化，
        /// 由 PaintAmbient 自己判断。
        /// </summary>
        public static void ClearCache()
        {
        }

        /// <summary>
        /// 启动后把**另一套主题**的氛围底先烘好，这样第一次切主题也不用等。
        /// 只烘当前不在用的那套，避免启动多做无用功。
        ///
        /// 注意：RenderAmbient 读的是调色板静态字段（Bg / Accent / Violet / Warn），
        /// 所以不能只翻 IsDark —— 要真的 ApplyTheme 到另一套，烘完再切回来。
        /// 全程在 UI 线程（由 Timer 触发），不会和绘制打架。
        /// </summary>
        public static void PrewarmOtherAmbient(int w, int h)
        {
            if (w < 4 || h < 4) return;
            bool keep = Theme.IsDark;
            if (keep && _ambientLight != null && _wLight == w && _hLight == h) return;
            if (!keep && _ambientDark != null && _wDark == w && _hDark == h) return;
            try
            {
                // 直接按目标主题烘 —— 不切全局调色板，界面不会闪。
                Bitmap bmp = BakeAmbient(w, h, !keep);
                if (keep)
                {
                    if (_ambientLight != null) _ambientLight.Dispose();
                    _ambientLight = bmp; _wLight = w; _hLight = h;
                }
                else
                {
                    if (_ambientDark != null) _ambientDark.Dispose();
                    _ambientDark = bmp; _wDark = w; _hDark = h;
                }
            }
            catch (Exception ex)
            {
                try { Core.ConfigManager.Log("[theme] 预热另一套氛围底失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 按指定主题烘一张氛围底。
        ///
        /// **不读 Theme 当前字段**，而是按 dark 参数取色 —— 这样预热「另一套」
        /// 时不用临时切全局调色板，也就不会有「界面闪一下另个配色」的风险。
        /// 只用 4 个色，和 Theme 的字段一一对应（改配色时两边一起改）。
        /// </summary>
        private static void RenderAmbient(Graphics g, int w, int h, bool dark)
        {
            Color bg     = dark ? Color.FromArgb(0x11, 0x11, 0x13) : Color.FromArgb(0xf6, 0xf8, 0xfa);
            Color accent = dark ? Color.FromArgb(0x00, 0x88, 0xFF) : Color.FromArgb(0x09, 0x69, 0xda);
            Color violet = dark ? Color.FromArgb(0xA3, 0x71, 0xF7) : Color.FromArgb(0x82, 0x50, 0xdf);
            Color warn   = dark ? Color.FromArgb(0x9A, 0x67, 0x00) : Color.FromArgb(0x9a, 0x67, 0x00);

            var full = new Rectangle(0, 0, w, h);
            using (var b = new SolidBrush(bg))
                g.FillRectangle(b, full);
            int span = Math.Max(w, h);
            Glow(g, (int)(w * 0.22), (int)(h * 0.04), (int)(span * 0.85), accent, 20);
            Glow(g, (int)(w * 0.96), (int)(h * 1.04), (int)(span * 0.70), violet, 15);
            Glow(g, (int)(w * 0.60), (int)(h * 0.52), (int)(span * 0.45), warn, 6);
            using (var tb = new TextureBrush(Noise()))
                g.FillRectangle(tb, full);
        }

        private static void Glow(Graphics g, int cx, int cy, int radius, Color hue, int alpha)
        {
            if (radius <= 0) return;
            var r = new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var b = new PathGradientBrush(path))
                {
                    b.CenterColor = Color.FromArgb(alpha, hue);
                    b.SurroundColors = new[] { Color.FromArgb(0, hue) };
                    g.FillEllipse(b, r);
                }
            }
        }

        private static Bitmap Noise()
        {
            if (_noise != null) return _noise;
            var bmp = new Bitmap(96, 96);
            var rnd = new Random(20261119);
            for (int y = 0; y < 96; y++)
                for (int x = 0; x < 96; x++)
                    bmp.SetPixel(x, y, Color.FromArgb(rnd.Next(0, 5), 255, 255, 255));
            _noise = bmp;
            return bmp;
        }


    }
}
