// 灵枢桌面版 —— 主窗口外壳
//
// 只做三件事：
//   1. 从 PageRegistry 自动生成左侧导航（不硬编码页面名）；
//   2. 在右侧容器里切换页面；
//   3. 底部状态栏显示提示。
//
// 加页面不用动这个文件。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Ui
{
    public sealed class MainForm : Form
    {
        private readonly AppServices _services;
        private readonly PageContext _ctx;

        private Panel _nav;
        private Panel _host;
        private Panel _sessList;
        private Label _status;
        private readonly Dictionary<string, Button> _navButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Control> _views = new Dictionary<string, Control>();
        private readonly List<Widgets.SessionItem> _sessItems = new List<Widgets.SessionItem>();
        private string _activeSession;
        private IPage _current;
        private Timer _statusTimer;
        private string _statusDefault = "就绪";
        private Panel _dock;
        private Label _dockCount;
        private FlowLayoutPanel _dockRows;
        private Timer _dockTimer;

        /// <summary>启动时停在哪个页面（--page 参数）。</summary>
        public string StartupPage { get; set; }

        /// <summary>
        /// `--screenshot &lt;路径&gt;`：渲染完成后把窗口画成 PNG 再退出。
        ///
        /// 加这个是为了重拍 `docs/screenshots/` 那批图 —— 以前是临时改代码
        /// `DrawToBitmap` 截完再撤，每次都提心吊胆怕忘了撤。
        /// 做成正式参数之后，截图是个可复跑的动作。
        ///
        /// 用 `DrawToBitmap` 而不是抓屏：抓屏会把别的窗口、鼠标、任务栏一起带上，
        /// 而且受缩放影响。`DrawToBitmap` 只画这个窗体的客户区。
        /// </summary>
        public string ShotPath { get; set; }
        public string DumpUiPath { get; set; }

        /// <summary>
        /// 截图前先切一次主题。**只给检查脚本用**，验证「运行时切换」能把
        /// 已有控件的 BackColor 搬到新色（只 Invalidate 重绘是不够的）。
        /// </summary>
        public bool ThemeCycle { get; set; }

        /// <summary>
        /// `--send-text &lt;文字&gt;`：打开会话后自动发一条。**只给检查脚本用**。
        ///
        /// 存在的理由见 `SessionView.SendForCheck` 的注释：这个程序的 UIA 树
        /// 只暴露到容器层，没法用 ValuePattern/InvokePattern 驱动输入框和按钮。
        /// 用钩子发消息，脚本只判结果，不碰界面细节。
        /// </summary>
        public string SendText { get; set; }

        /// <summary>
        /// 无人值守模式：只截图 / 只 dump 控件树，没有人在看。
        /// **这种模式下绝对不能弹模态框** —— 框会把 UI 线程堵死，
        /// 计时器永远不 tick，进程活着但什么也不产出（排查过两次：
        /// session 页建不出来、dump-ui 不出文件，都是这个）。
        /// </summary>
        private bool Unattended
        {
            get { return !string.IsNullOrEmpty(ShotPath) || !string.IsNullOrEmpty(DumpUiPath); }
        }

        /// <summary>
        /// `--no-focus`：窗口照常显示、UIA 照常可查，但**不激活、不抢前台**。
        /// 检查脚本（smoke/layout/clip/overflow/_shots）后台跑时用，
        /// 免得每起一次窗口就把用户的前台（游戏/编辑器）踢掉。
        /// </summary>
        public bool NoFocus { get; set; }

        protected override bool ShowWithoutActivation
        {
            get { return NoFocus || base.ShowWithoutActivation; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_NOACTIVATE：连鼠标点一下都不激活 —— 检查期间窗口可能被
                // 别的自动动作碰到，保持「可见但永不吃焦点」。
                if (NoFocus) cp.ExStyle |= 0x08000000;
                return cp;
            }
        }

        /// <summary>当前停在哪个页面（退出时记下来，下次接着用）。</summary>
        public string CurrentPageId
        {
            get { return _current != null ? _current.Id : (StartupPage ?? "home"); }
        }

        public MainForm(AppServices services)
        {
            // DPI 自适应不走 WinForms AutoScale（单文件 exe 无 app.config 时实测不触发），
            // 由 Theme.UiScale + 构造后 ScaleTree 统一缩放。
            AutoScaleMode = AutoScaleMode.None;
            _services = services;
            _ctx = new PageContext(services, this, Toast);
            _ctx.Navigate = id => Fire(GoAsync(id), "跳转页面");

            Text = "灵枢 · SpiritScout";
            // 任务栏/标题栏图标：`-win32icon` 只影响 exe 文件图标，
            // 不显式设 Icon 就是 WinForms 的通用图标。
            Icon = Theme.AppIcon();
            ApplyAdaptiveSize();
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            DoubleBuffered = true;

            // **主题必须先于 BuildShell** —— 这不是风格问题，是正确性问题。
            //
            // 控件建的时候写 `BackColor = Theme.Sidebar`，BackColor 是值类型，
            // 赋值那一刻就把当时的色值拷死了。如果 BuildShell 先跑、ApplyTheme 后跑，
            // 那些面板会永远停在深色 #17171A —— 静态字段 Theme.Sidebar 变了，
            // 但已经赋过的控件属性不会跟着变（自绘控件因为每次 OnPaint 重读
            // Theme.Sidebar 才没这个毛病，普通 Panel 全中招）。
            //
            // 所以：先决定主题 → 再建界面。建出来的东西一次就是对的色。
            try
            {
                var cfgTheme = new Core.ConfigManager();
                cfgTheme.Load();
                Core.ConfigManager.Log("[theme] ThemeMode=" + (cfgTheme.Current.ThemeMode ?? "null"));
                if (string.Equals(cfgTheme.Current.ThemeMode, "light", StringComparison.OrdinalIgnoreCase))
                    Theme.ApplyTheme(false);
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("[theme] 读 ThemeMode 失败: " + ex.Message);
            }

            BuildShell();

            Theme.ScaleTree(this);
            // 会话项在缩放之后建（RefreshSessions 里直接给物理行高），
            // 之后新建/删除触发的重建也走同一套物理值，不会被二次缩放。
            RefreshSessions();
            InitShortcuts();
            // 换过显示器/分辨率后恢复的位置可能整窗在屏外，标题栏够不到就没法拖动。
            Shown += delegate
            {
                Theme.EnsureOnScreen(this);
                // 启动 1.6 秒后把**另一套主题**的氛围底先烘好（约 100ms）。
                //
                // 不烘的话，用户第一次切主题要等这一下 —— 这正是「切换好卡」的
                // 主因：Theme.ApplyTheme 本身只 2ms，慢的是重建那张
                // 三团 PathGradient + 噪点的位图。烘在启动后，切换就只剩换指针。
                var warmAmbient = new Timer { Interval = 1600 };
                warmAmbient.Tick += delegate
                {
                    warmAmbient.Stop();
                    warmAmbient.Dispose();
                    try
                    {
                        var host = _host;
                        if (host != null && host.ClientSize.Width >= 4)
                            Theme.PrewarmOtherAmbient(host.ClientSize.Width, host.ClientSize.Height);
                    }
                    catch { }
                };
                warmAmbient.Start();
            };
            Load += async (s, e) =>
            {
                // 支持启动时指定首屏（--page models），没指定就取第一个。
                // 也支持 --page session:<id> 直接打开某个会话（做快捷方式用）。
                string want = StartupPage;
                if (!string.IsNullOrEmpty(want) && want.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
                {
                    string sid = want.Substring("session:".Length).Trim();
                    // OpenSessionByIdAsync 内部没有 try/catch：
                    // 启动时指定了一个打不开的会话，原来会弹「程序遇到了没预料到的错误」。
                    // 走 Fire 能得到「「打开会话」失败：…」这种能看懂的提示。
                    // 截图模式下要**等它开完**再截，所以不能走 Fire（那是即发即忘）。
                    // 非截图模式仍然走 Fire —— 那里不需要等。
                    if (Unattended)
                    {
                        try { await OpenSessionByIdAsync(sid).ConfigureAwait(true); }
                        catch (Exception ex) { _ctx.Error("打开会话", ex); }
                        if (!string.IsNullOrEmpty(ShotPath)) ScheduleShot(); else ScheduleDump();
                    }
                    else
                    {
                        UiSafety.Fire(OpenSessionByIdAsync(sid), "打开会话");
                    }
                    return;
                }
                var target = string.IsNullOrEmpty(want) ? null : PageRegistry.Find(want);
                if (target == null && PageRegistry.All.Count > 0) target = PageRegistry.All[0];
                if (target != null)
                {
                    if (Unattended)
                    {
                        // 截图/树 dump 模式必须等页面建完再动手，保持同步。
                        await GoAsync(target.Id).ConfigureAwait(true);
                        if (!string.IsNullOrEmpty(ShotPath)) ScheduleShot(); else ScheduleDump();
                    }
                    else
                    {
                        // 正常启动：先让 shell 出首帧（窗口立刻可见），
                        // 首屏页面推到下一帧再建 —— 用户感知从「几秒白等」
                        // 变成「秒出窗口 + 内容随即填充」。
                        var timer = new Timer { Interval = 1 };
                        timer.Tick += delegate
                        {
                            timer.Stop();
                            timer.Dispose();
                            UiSafety.Fire(GoAsync(target.Id), "打开首屏");
                        };
                        timer.Start();

                        // 首屏页面是即发即忘建的，等它建完再预热其余页。
                        // 1.2s 是经验值：够首页建完，又不至于预热太晚。
                        var warm = new Timer { Interval = 1200 };
                        warm.Tick += delegate
                        {
                            warm.Stop();
                            warm.Dispose();
                            PrewarmPages();
                        };
                        warm.Start();
                    }
                }
                else if (!string.IsNullOrEmpty(ShotPath)) ScheduleShot();
                else if (!string.IsNullOrEmpty(DumpUiPath)) ScheduleDump();
            };
        }

        /// <summary>
        /// 等一帧渲染完再截图，然后退出。
        ///
        /// 为什么要等：`GoAsync` 返回时页面控件刚 add 进去，布局和首帧绘制还没走完，
        /// 这时候 `DrawToBitmap` 拿到的是空白。700ms 是实测够用的值。
        /// </summary>
        private void ScheduleShot()
        {
            // --theme-cycle：先切一次主题，等重绘稳定了再截。
            // 用来验证 Theme.ApplyTheme(dark, root) 的控件重着色真的生效。
            if (ThemeCycle)
            {
                // 2200ms：等启动后的预热（1600ms）先跑完，这样下面这次切换
                // 应该是「命中缓存、不烘图」——检查脚本靠日志证明这一点。
                var flip = new Timer { Interval = 2200 };
                flip.Tick += delegate
                {
                    flip.Stop();
                    flip.Dispose();
                    Theme.ApplyTheme(!Theme.IsDark, this);
                    Invalidate(true);
                    var after = new Timer { Interval = 700 };
                    after.Tick += delegate
                    {
                        after.Stop();
                        after.Dispose();
                        CaptureNow();
                    };
                    after.Start();
                };
                flip.Start();
                return;
            }

            var timer = new Timer { Interval = 700 };
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                CaptureNow();
            };
            timer.Start();
        }

        /// <summary>把当前窗口客户区画成 PNG 存到 ShotPath。</summary>
        private void CaptureNow()
        {
                try
                {
                    using (var full = new Bitmap(Width, Height))
                    {
                        // 全窗口重画一次的价格 —— 切主题后 Invalidate(true) 付的就是它。
                        // 记下来，用户再报「卡」时能分清是 Theme 层还是重绘层。
                        var swPaint = System.Diagnostics.Stopwatch.StartNew();
                        DrawToBitmap(full, new Rectangle(0, 0, Width, Height));
                        swPaint.Stop();
                        Core.ConfigManager.Log("[paint] 全窗口重画 " + Width + "x" + Height
                            + "：" + swPaint.ElapsedMilliseconds + "ms");
                        // **只留客户区。** DrawToBitmap 走 WM_PRINT，标题栏会被画成浅色，
                        // 而真实窗口是 DWM 深色——那条边留在图里就不是用户看到的样子，
                        // 以前靠截图脚本事后裁，现在一次出对（docs/screenshots 直接可用）。
                        var cr = RectangleToScreen(ClientRectangle);
                        int cx = Math.Max(0, cr.X - Left);
                        int cy = Math.Max(0, cr.Y - Top);
                        int cw = Math.Min(cr.Width, Width - cx);
                        int ch = Math.Min(cr.Height, Height - cy);
                        if (cw < 8 || ch < 8) { cx = 0; cy = 0; cw = Width; ch = Height; }
                        using (var client = new Bitmap(cw, ch))
                        {
                            using (var g = Graphics.FromImage(client))
                            {
                                g.DrawImage(full, new Rectangle(0, 0, cw, ch),
                                    new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
                            }
                            client.Save(ShotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Core.ConfigManager.Log("[screenshot] " + ShotPath + " " + Width + "x" + Height
                            + " → 客户区 " + cw + "x" + ch);
                    }
                }
                catch (Exception ex)
                {
                    Core.ConfigManager.Log("[screenshot] 失败: " + ex.Message);
                }
                finally { Close(); }
        }

        /// <summary>
        /// --dump-ui &lt;path&gt;：把整棵控件树的类型/边界/可见性写进文本文件后退出。
        /// 排查「实屏残影」用：WGC 截屏里导航行被别处内容反复压印，但 DrawToBitmap
        /// 干净，说明要么控件几何错了、要么是系统 blit 的历史像素——边界树能分辨。
        /// </summary>
        private void ScheduleDump()
        {
            var timer = new Timer { Interval = 700 };
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("ClientSize=" + ClientSize + " UiScale=" + Theme.UiScale);
                    DumpTree(this, 0, sb);
                    System.IO.File.WriteAllText(DumpUiPath, sb.ToString());
                    Core.ConfigManager.Log("[dump-ui] " + DumpUiPath);
                }
                catch (Exception ex)
                {
                    Core.ConfigManager.Log("[dump-ui] 失败: " + ex.Message);
                }
                finally { Close(); }
            };
            timer.Start();
        }

        private static void DumpTree(Control c, int depth, System.Text.StringBuilder sb)
        {
            sb.AppendLine(new string(' ', depth * 2) + c.GetType().Name
                + " b=(" + c.Bounds.X + "," + c.Bounds.Y + "," + c.Bounds.Width + "," + c.Bounds.Height + ")"
                + " vis=" + c.Visible
                + " back=" + (c.BackColor.IsEmpty ? "-" : c.BackColor.Name)
                + " txt=" + (c.Text.Length > 12 ? c.Text.Substring(0, 12) : c.Text).Replace("\n", " "));
            foreach (Control ch in c.Controls) DumpTree(ch, depth + 1, sb);
        }

        private void BuildShell()
        {
            _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // 侧栏是一整面渐变软面（SidePanel），里面的组/列表/底全部透明底，
            // 自绘项（NavItem/SessionItem/BrandPanel）采样同一幅渐变。
            _nav = new Widgets.SidePanel
            {
                Dock = DockStyle.Left,
                Width = 238,
            };

            // 侧栏对齐 web 版（p2）：品牌 → 6 个主导航 → 「会话」列表 → 配置组 → 底部设置/版本。
            var brand = new Widgets.BrandPanel();

            var primary = BuildNavBlock(
                new[] { "home", "dash", "usage", "agents", "projects", "exp" }, 34, null, DockStyle.Top);

            // 「会话」标题行 + 右侧「+ 新任务」（web .side-label + #btn-new）
            var sessHead = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Sidebar };
            sessHead.Controls.Add(new Label
            {
                Text = "会话",
                Font = Theme.FontSection,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Location = new Point(18, 14),
                BackColor = Color.Transparent,
            });
            var newTask = new Widgets.FlatButton
            {
                Text = "+ 新任务",
                Kind = Widgets.ButtonKind.Ghost,
                Size = new Size(82, 24),
                Font = Theme.FontTiny,
            };
            newTask.Click += (s, e) => Fire(GoAsync("home"), "新建任务");
            sessHead.Controls.Add(newTask);
            sessHead.Resize += (s, e) =>
            {
                if (Theme.IsScaling) return;
                float u = Theme.UnitOf(sessHead);
                newTask.Location = new Point(sessHead.Width - (int)Math.Round(92 * u), (int)Math.Round(7 * u));
            };
            newTask.Location = new Point(238 - 92, 7);

            // 会话列表（填满中间，可滚动）
            //
            // **不能用 Transparent + AutoScroll**：滚动是 ScrollWindowEx 把旧像素
            // 整体平移，新暴露的条带走「父窗模拟透明」补绘，补出来的渐变和已烘焙
            // 在旧像素里的渐变错位——文字就会拖影/残影。
            // 改实底侧栏色（这块区域离顶部光晕远，肉眼与渐变无异），
            // 并在每次滚动后整表重绘一次，保证行内采样的渐变跟随行位置对齐。
            _sessList = new SessListPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Sidebar,
                AutoScroll = true,
            };

            // web 没有「配置」这一层 —— 模型/MCP/Skill/通知/本地工具/关于
            // 全部收在设置页里，由设置外壳的二级导航切换。

            // 底部：设置 + 版本号（web .side-foot：一根顶线 + 两端对齐）
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.Sidebar };
            foot.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.LineDark))
                    e.Graphics.DrawLine(pen, 0, 0, foot.Width, 0);
            };
            var settingsBtn = new Widgets.NavItem
            {
                Text = "  " + PageIcons.Get("settings") + "   设置",
                Tag = "settings",
                Dock = DockStyle.Left,
                Width = 140,
                Height = 40,
            };
            settingsBtn.Click += (s, e) => Fire(GoAsync("settings"), "切换到「设置」");
            _navButtons["settings"] = settingsBtn;
            // 主题切换按钮（web 的 btn-theme：侧栏底部）
            var themeBtn = new Widgets.FlatButton
            {
                Text = Theme.IsDark ? "☾ 深色" : "☀ 浅色",
                Kind = Widgets.ButtonKind.Ghost,
                Size = new Size(Theme.K(82), Theme.K(26)),
                Dock = DockStyle.Left,
                Margin = new Padding(Theme.K(4), Theme.K(8), 0, Theme.K(8)),
                Cursor = Cursors.Hand,
            };
            themeBtn.Click += delegate
            {
                // 传 this：让 Theme 遍历整棵树，把控件的 BackColor/ForeColor
                // 从旧角色色搬到新角色色（BackColor 是值类型，静态字段变了
                // 不会自动带着控件走）。只 Invalidate 重绘是不够的 ——
                // 重绘画的还是那个过期的 BackColor。
                var swSwitch = System.Diagnostics.Stopwatch.StartNew();
                Theme.ApplyTheme(!Theme.IsDark, this);
                themeBtn.Text = Theme.IsDark ? "☾ 深色" : "☀ 浅色";
                // 存进 config，下次启动还是这个主题 —— **后台异步写**，
                // 同步写在 UI 线程会卡（config.json 读写锁 + IO）。
                try
                {
                    var cfg = new Core.ConfigManager();
                    cfg.Load();
                    cfg.Current.ThemeMode = Theme.IsDark ? "dark" : "light";
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { cfg.Save(); } catch { }
                    });
                }
                catch { }
                // 全窗口重画（颜色全变了）。
                // 比逐个控件 Invalidate 慢一点点，但结构简单、稳。
                // 卡顿的真正原因如果是它，再回来做这个优化。
                Invalidate(true);   // 递归重绘已覆盖侧栏，不用再单独 _nav.Invalidate
            };
            foot.Controls.Add(themeBtn);

            foot.Controls.Add(settingsBtn);
            foot.Controls.Add(new Label
            {
                Text = "v" + Core.AppInfo.Version,
                Dock = DockStyle.Right,
                Width = 70,
                Font = Theme.FontTiny,
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 14, 0),
                BackColor = Color.Transparent,
            });

            // Dock 占位顺序：Fill 先加；Bottom 后加的在最下；Top 后加的在最上。
            _nav.Controls.Add(_sessList);     // Fill
            _nav.Controls.Add(foot);          // Bottom（最底）
            _nav.Controls.Add(sessHead);      // Top
            _nav.Controls.Add(primary);       // Top（品牌之下）
            _nav.Controls.Add(brand);         // Top（最顶）

            _status = new Label
            {
                Text = _statusDefault,
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 8, 0),
                BackColor = Theme.Bg,
            };
            _status.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.LineDark))
                    e.Graphics.DrawLine(pen, 0, 0, _status.Width, 0);
            };

            // ---- 任务坞（web 的 .task-dock）：左下角浮动提示 ----
            //
            // web 的规则是「只有 view === "session" 时才显示」，
            // 桌面改成**全局** —— 用户要的是「在后台跑，我能看见」，
            // 在哪个页面都得知道（web 切走会话也收起来，那是它的问题）。
            _dock = new Panel
            {
                BackColor = Theme.Surface,
                Width = Theme.K(380),
                Height = Theme.K(34),
                Visible = false,
                Cursor = Cursors.Hand,
            };
            _dock.Paint += (s2, e2) =>
            {
                var g = e2.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = Theme.RoundPath(
                           new Rectangle(0, 0, _dock.Width - 1, _dock.Height - 1), Theme.RadiusMd))
                using (var pen = new Pen(Theme.LineDark, 1))
                    g.DrawPath(pen, path);
            };
            _dockCount = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Theme.K(12), 0, Theme.K(8), 0),
                Cursor = Cursors.Hand,
            };
            _dockRows = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 0,
                BackColor = Color.Transparent,
                WrapContents = false,
                Padding = new Padding(Theme.K(12), 0, Theme.K(8), Theme.K(8)),
            };
            _dock.Controls.Add(_dockRows);
            _dock.Controls.Add(_dockCount);
            _dockCount.Click += (s2, e2) => ToggleDock();
            _dock.Click += (s2, e2) => ToggleDock();

            // 3 秒扫一次正在跑的会话。Timer 在 UI 线程跑，不用封送。
            _dockTimer = new Timer { Interval = 3000 };
            _dockTimer.Tick += (s2, e2) => RefreshDock();
            _dockTimer.Start();

            Controls.Add(_dock);           // 浮层先加，Dock=Bottom 的 _status 之后再加
            Controls.Add(_host);
            Controls.Add(_nav);
            Controls.Add(_status);

            // 侧栏宽度跟窗口走（web: clamp(212px, 17vw, 300px)）
            ClientSizeChanged += (s, e) => { LayoutSideWidth(); LayoutDock(); };
            LayoutSideWidth();
            Theme.DarkScrollTree(this);
        }

        /// <summary>展开/收起任务坞。</summary>
        private void ToggleDock()
        {
            if (_dock == null || _dock.IsDisposed) return;
            bool open = _dockRows.Height > 0;
            _dockRows.Height = open ? 0 : Theme.K(96);
            _dock.Height = Theme.K(34) + _dockRows.Height;
            LayoutDock();
        }

        /// <summary>
        /// 扫一遍正在跑的会话，更新任务坞。
        ///
        /// `Phase == Executing` 即「在后台跑」——
        /// AgentLoop 开始时置 Executing，成功返回时置 Completed，
        /// 失败/停止时会置 Stopped/Failed。
        /// </summary>
        private void RefreshDock()
        {
            if (_dock == null || _dock.IsDisposed) return;
            try
            {
                var running = new List<Core.Session>();
                foreach (var sess in new Core.SessionStore().List())
                    if (sess.Phase == Core.SessionPhase.Executing) running.Add(sess);

                if (running.Count == 0)
                {
                    _dock.Visible = false;
                    _dockRows.Height = 0;
                    _dock.Height = Theme.K(34);
                    return;
                }
                Core.ConfigManager.Log("[dock] 发现 " + running.Count + " 个执行中的任务");

                _dock.Visible = true;
                _dockCount.Text = running.Count + " 个任务在后台运行";

                _dockRows.SuspendLayout();
                _dockRows.Controls.Clear();
                foreach (var sess in running)
                {
                    var row = new Widgets.FlatButton
                    {
                        Text = "↳ " + (string.IsNullOrEmpty(sess.Title) ? sess.Id : sess.Title)
                             + "   " + Core.PhaseText.Label(sess.Phase),
                        Kind = Widgets.ButtonKind.Ghost,
                        Size = new Size(Theme.K(350), Theme.K(26)),
                        Font = Theme.FontSmall,
                        Margin = new Padding(0, 0, 0, Theme.K(6)),
                        Tag = sess.Id,
                        Cursor = Cursors.Hand,
                    };
                    var sid = sess.Id;
                    row.Click += delegate { UiSafety.Fire(OpenSessionByIdAsync(sid), "切到任务"); };
                    _dockRows.Controls.Add(row);
                }
                _dockRows.ResumeLayout();
                LayoutDock();
            }
            catch { }
        }

        /// <summary>把任务坞贴在左下角（状态栏之上）。</summary>
        private void LayoutDock()
        {
            if (_dock == null || _dock.IsDisposed) return;
            _dock.Location = new Point(
                _nav.Width + Theme.K(18),
                _host.Bottom - _dock.Height - Theme.K(12));
        }

        /// <summary>侧栏宽度 = clamp(212, 17% 窗宽, 300)，对齐 web 的 #app 栅格（设计像素×比例）。</summary>
        private void LayoutSideWidth()
        {
            if (_nav == null || _nav.IsDisposed) return;
            // ScaleTree 期间 ClientSize 是半缩放态，此刻算宽会被再乘一次缩放
            if (Theme.IsScaling) return;
            float u = Theme.UnitOf(this);
            int design = Math.Max(212, Math.Min(300, (int)(ClientSize.Width / u * 0.17)));
            int w = (int)Math.Round(design * u);
            if (_nav.Width != w) _nav.Width = w;
            // 尺寸/宽度一变，整条侧栏连子控件一起重绘：系统 resize 时只做像素平移，
            // 平移会把别处的旧内容拖进导航块（实测最大化后导航区出现压扁的窗口残条），
            // 全量重绘不留任何旧像素。
            _nav.Invalidate(true);
        }

        /// <summary>建一组导航项（可选组标题）。返回的 Panel 已设好 Dock 与高度。</summary>
        private Panel BuildNavBlock(string[] ids, int rowH, string label, DockStyle dock)
        {
            int headH = string.IsNullOrEmpty(label) ? 0 : 28;
            var block = new Panel
            {
                Dock = dock,
                Height = ids.Length * rowH + headH + 6,
                BackColor = Theme.Sidebar,
            };
            // 倒序加入：Dock.Top 后加的在上面，保证 ids[0] 在最顶
            for (int i = ids.Length - 1; i >= 0; i--)
            {
                var page = PageRegistry.Find(ids[i]);
                if (page == null) continue;
                var btn = new Widgets.NavItem
                {
                    Text = "  " + PageIcons.Get(page.Id) + "   " + page.Title,
                    Tag = page.Id,
                    Dock = DockStyle.Top,
                    Height = rowH,
                };
                string id = page.Id;
                btn.Click += (s, e) => Fire(GoAsync(id), "切换到「" + page.Title + "」");
                _navButtons[id] = btn;
                block.Controls.Add(btn);
            }
            if (headH > 0)
            {
                block.Controls.Add(new Label
                {
                    Text = label,
                    Dock = DockStyle.Top,
                    Height = headH,
                    Font = Theme.FontSection,
                    ForeColor = Theme.Muted,
                    TextAlign = ContentAlignment.BottomLeft,
                    Padding = new Padding(18, 0, 0, 6),
                    BackColor = Color.Transparent,
                });
            }
            return block;
        }

        /// <summary>刷新侧栏会话列表（新建/删除/打开后调）。</summary>
        public void RefreshSessions()
        {
            if (_sessList == null || _sessList.IsDisposed) return;
            _sessList.SuspendLayout();
            foreach (var it in _sessItems) { _sessList.Controls.Remove(it); it.Dispose(); }
            _sessItems.Clear();
            // 清掉空状态提示等遗留控件
            for (int i = _sessList.Controls.Count - 1; i >= 0; i--)
            {
                var c = _sessList.Controls[i];
                _sessList.Controls.RemoveAt(i);
                c.Dispose();
            }

            List<Core.Session> sessions;
            try { sessions = new Core.SessionStore().List(); }
            catch { sessions = new List<Core.Session>(); }

            if (sessions.Count == 0)
            {
                _sessList.Controls.Add(new Label
                {
                    Text = "还没有任务",
                    Dock = DockStyle.Top,
                    Height = Theme.K(40),
                    Font = Theme.FontSmall,
                    ForeColor = Theme.Muted,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(Theme.K(18), 0, 0, 0),
                    BackColor = Color.Transparent,
                });
            }
            else
            {
                // 倒序加入，让最新的会话停在最上面
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    var s = sessions[i];
                    var item = new Widgets.SessionItem
                    {
                        SessionId = s.Id,
                        Target = string.IsNullOrEmpty(s.Target) ? "(未命名)" : s.Target,
                        ModeLabel = Core.RunModeText.Label(s.Mode),
                        Dock = DockStyle.Top,
                        Height = Theme.K(52),
                    };
                    item.SetActive(s.Id == _activeSession);
                    string sid = s.Id;
                    item.OpenRequested += (ss, ee) =>
                    {
                        _activeSession = sid;
                        HighlightSession();
                        Fire(OpenSessionByIdAsync(sid), "打开会话");
                    };
                    item.DeleteRequested += (ss, ee) => DeleteSession(sid, item.Target);
                    _sessItems.Add(item);
                    _sessList.Controls.Add(item);
                }
            }
            _sessList.ResumeLayout();
        }

        private void HighlightSession()
        {
            foreach (var it in _sessItems) it.SetActive(it.SessionId == _activeSession);
            _nav.Invalidate(true);
        }

        private void DeleteSession(string id, string target)
        {
            if (!UiSafety.Confirm(this,
                    "确定删除任务「" + target + "」？\n\n这会同时删掉它的会话文件，无法撤销。",
                    "确认删除"))
                return;
            try
            {
                new Core.SessionStore().Delete(id);
                if (_activeSession == id) _activeSession = null;
                RefreshSessions();
                Toast("已删除任务", true);
            }
            catch (Exception ex)
            {
                _ctx.Error("删除任务", ex);
            }
        }

        /// <summary>切换某个导航项的选中态。`NavItem` 自绘胶囊；万一被换成普通
        /// Button（外部改过）就退回改颜色。</summary>
        private void SetNavActive(string id, bool active)
        {
            Button b;
            if (!_navButtons.TryGetValue(id, out b) || b == null) return;
            var ni = b as Widgets.NavItem;
            if (ni != null) { ni.SetActive(active); return; }
            b.ForeColor = active ? Theme.Accent : Theme.Muted;
            b.BackColor = active ? Theme.Hover : Theme.Surface;
        }

        /// <summary>建一页并放进 _views 缓存。GoAsync 和预热都走这里。</summary>
        private Control BuildView(IPage page)
        {
            var view = page.CreateView();
            Theme.ScaleTree(view);
            view.Dock = DockStyle.Fill;
            _views[page.Id] = view;
            return view;
        }

        /// <summary>
        /// 启动后**趁首屏还稳着，把其它页面在 UI 线程里逐页预建**。
        ///
        /// 原来 `GoAsync` 是「点哪页建哪页」—— 首次切页要现建几十个控件，
        /// 在 UI 线程同步做完，就是用户感觉到的「卡一下」。
        ///
        /// CreateView 必须 UI 线程（new 控件），所以用 Timer 逐页建：
        /// 每 60ms 建一页，首屏显示后 1 秒内把全部页面建完，
        /// 用户再点侧栏就是秒开（只是 `Controls.Add` 一次）。
        ///
        /// 建页时**不跑 RefreshAsync** —— 那是数据填充，预热只做「建架子」。
        /// 真切到某页才 Refresh，所以预热的开销只是建控件本身。
        /// </summary>
        private void PrewarmPages()
        {
            // 只预热「不在 _views 里、且不是当前页」的页面；
            // 当前页刚被 GoAsync 建过，跳过。
            var pending = new System.Collections.Generic.Queue<IPage>();
            foreach (var p in PageRegistry.All)
            {
                if (_views.ContainsKey(p.Id)) continue;
                if (_current != null && p.Id == _current.Id) continue;
                pending.Enqueue(p);
            }
            Core.ConfigManager.Log("[prewarm] 计划预热 " + pending.Count + " 页（已建 " + _views.Count + "）");
            if (pending.Count == 0) return;

            var timer = new Timer { Interval = 60 };
            timer.Tick += delegate
            {
                timer.Stop();
                if (pending.Count == 0) { timer.Dispose(); return; }
                var page = pending.Dequeue();
                try
                {
                    BuildView(page);
                    Core.ConfigManager.Log("[prewarm] " + page.Id + " 已建（缓存 " + _views.Count + "）");
                }
                catch (Exception ex)
                {
                    Core.ConfigManager.Log("[prewarm] " + page.Id + " 失败: " + ex.Message);
                }
                // 继续排下一页
                timer.Start();
            };
            timer.Start();
        }

        private async Task GoAsync(string pageId)
        {
            var page = PageRegistry.Find(pageId);
            if (page == null) return;

            if (_current != null && _current.Id == pageId)
            {
                // 点当前页 = 刷新。这里也要兜异常——不然刷新失败会静默过去，
                // 用户看到的是「点了没反应」。
                try
                {
                    await page.RefreshAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _ctx.Error(page.Title + "刷新", ex);
                }
                return;
            }

            Control view;
            if (!_views.TryGetValue(pageId, out view))
            {
                try
                {
                    view = BuildView(page);
                }
                catch (Exception ex)
                {
                    _ctx.Error("构建" + page.Title + "页面", ex);
                    return;
                }
            }

            _host.SuspendLayout();
            _host.Controls.Clear();
            _host.Controls.Add(view);
            _host.ResumeLayout();

            _current = page;
            _activeSession = null;
            HighlightSession();
            foreach (var kv in _navButtons)
                SetNavActive(kv.Key, kv.Key == pageId);
            // 切页会改一堆导航/会话项的状态；整条侧栏全量重绘一次，
            // 杜绝任何「旧像素没被盖掉」的残影（实测只靠子控件自绘会留影）。
            _nav.Invalidate(true);

            try
            {
                await page.RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _ctx.Error(page.Title + "加载", ex);
            }
        }

        /// <summary>按 id 打开会话详情（任务页双击、--page session:xxx 都走这里）。</summary>
        public async Task OpenSessionByIdAsync(string sessionId)
        {
            var store = new Core.SessionStore();
            var sf = store.Load(sessionId);
            if (sf == null)
            {
                // **截图模式不能弹模态框。**
                // 2026-11-19 排查 `--page session:<id>` 「启动后窗口建不出来」：
                // 根因就是这里 —— 会话不存在（或没配模型，见下）时弹了个 MessageBox，
                // 而窗口还没显示出来，那个框就成了「看不见但挡住一切」的东西：
                // 进程活着、标题为空、连日志都没有。
                if (Unattended)
                {
                    // 无人值守模式不弹框，但也不能留一片纯黑：照样进会话页，
                    // 由 SessionView 的空态说明「会话不存在」。
                    Core.ConfigManager.Log("[screenshot] 找不到会话 " + sessionId);
                    var reg0 = new Llm.ModelRegistry(
                        Core.AppPaths.ModelsFile, new Llm.SecretStore(Core.AppPaths.SecretsFile));
                    var missing = new Pages.SessionView(store, reg0, sessionId);
                    missing.Back += () => Fire(PopViewAsync(), "返回上一页");
                    PushView(missing);
                    return;
                }
                MessageBox.Show(this, "找不到会话：" + sessionId, "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var registry = new Llm.ModelRegistry(
                Core.AppPaths.ModelsFile, new Llm.SecretStore(Core.AppPaths.SecretsFile));
            if (registry.Models().Count == 0)
            {
                // 截图模式下**没有模型也照样打开** —— 历史消息是只读的，不需要模型。
                // 非截图模式仍然提示并跳去模型页（那是正确的引导）。
                if (!Unattended)
                {
                    registry.Dispose();
                    MessageBox.Show(this,
                        "还没有配置模型。\n\n先去「模型」页添加供应商和模型，才能对话。",
                        "灵枢", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    var m = PageRegistry.Find("models");
                    if (m != null) await GoAsync(m.Id).ConfigureAwait(true);
                    return;
                }
                Core.ConfigManager.Log("[screenshot] 未配置模型，仍以只读方式打开会话");
            }

            var view = new Pages.SessionView(store, registry, sessionId);
            view.Back += () => Fire(PopViewAsync(), "返回上一页");
            _activeSession = sessionId;
            HighlightSession();
            PushView(view);

            // --send-text：界面建好之后立刻发一条（检查脚本用）。
            // 放在 PushView 之后 —— 发送会往消息流里加气泡，得先有容器。
            if (!string.IsNullOrEmpty(SendText))
            {
                Core.ConfigManager.Log("[自检] 自动发送：" + SendText);
                view.SendForCheck(SendText);
            }

            await Task.CompletedTask;
        }

        // ---------- 临时视图（会话详情等非 IPage 页） ----------

        private Control _pushedView;
        private string _pushedFrom;

        /// <summary>
        /// 把一个临时视图盖在内容区上（会话详情用）。返回时回到原页。
        /// 加这类页不用注册到 PageRegistry，也不会出现在侧栏。
        /// </summary>
        public void PushView(Control view)
        {
            if (view == null) return;
            _pushedFrom = _current != null ? _current.Id : null;
            _pushedView = view;
            Theme.ScaleTree(view);
            view.Dock = DockStyle.Fill;

            _host.SuspendLayout();
            _host.Controls.Clear();
            _host.Controls.Add(view);
            _host.ResumeLayout();

            // 侧栏取消高亮（当前不在任何 IPage 上）
            foreach (var kv in _navButtons)
                SetNavActive(kv.Key, false);
        }

        /// <summary>从临时视图返回。</summary>
        public async Task PopViewAsync()
        {
            if (_pushedView == null) return;
            _host.Controls.Remove(_pushedView);
            _pushedView.Dispose();
            _pushedView = null;

            string back = _pushedFrom;
            _pushedFrom = null;
            _current = null;
            if (!string.IsNullOrEmpty(back)) await GoAsync(back).ConfigureAwait(true);
        }

        /// <summary>
        /// 起一个异步任务并保证异常不会丢。
        ///
        /// **`task.GetAwaiter()` 不会抛异常**——它只返回 awaiter，任务失败了对它
        /// 没有任何影响，异常就此静默消失。要让它冒出来必须 `.GetResult()`，
        /// 或者像这里一样挂一个只处理失败的延续。
        /// </summary>
        private void Fire(Task task, string what)
        {
            if (task == null) return;
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    var ex = t.Exception.GetBaseException();
                    try { _ctx.Error(what, ex); } catch { }
                }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }

        // ---------- 尺寸自适应 ----------

        /// <summary>
        /// 按屏幕工作区决定初始尺寸与最小尺寸。
        ///
        /// 原来写死 ClientSize=1400x900 / MinimumSize=1160x740，在 1366x768 的笔记本上
        /// 放不下——去掉任务栏只剩 728 高，比最小高度还小，窗口会被裁掉一截。
        /// 而 1366x768 恰恰是存量最大的旧笔记本分辨率。
        /// </summary>
        private void ApplyAdaptiveSize()
        {
            // 工作区是物理像素；先换回设计像素再取上限，缩放后才不会超出屏幕。
            var wa = Screen.PrimaryScreen.WorkingArea;
            int waW = (int)(wa.Width / Theme.UiScale);
            int waH = (int)(wa.Height / Theme.UiScale);
            int maxW = Math.Max(800, (int)(waW * 0.92));
            int maxH = Math.Max(560, (int)(waH * 0.92));

            // 目标 1400x900，但不超过工作区的 92%
            int w = Math.Min(1400, maxW);
            int h = Math.Min(900, maxH);
            ClientSize = new Size(w, h);

            // 最小尺寸：侧栏固定区约 590px（品牌+主导航+会话标题+配置组+底部），
            // 高度留 640 才放得下侧栏且会话区还有余量；宽度能放下侧栏 232 + 内容。
            int minW = Math.Min(980, waW - 40);
            int minH = Math.Min(680, waH - 40);
            MinimumSize = new Size(Math.Max(760, minW), Math.Max(640, minH));

            Core.ConfigManager.Log("窗口自适应：屏幕 " + wa.Width + "x" + wa.Height
                + " @ " + Theme.UiScale.ToString("0.##") + "x"
                + " → 初始 " + w + "x" + h + " · 最小 " + MinimumSize.Width + "x" + MinimumSize.Height);
        }

        // ---------- 快捷键 ----------

        private void InitShortcuts()
        {
            KeyPreview = true;
            KeyDown += async (s, e) =>
            {
                // F5 / Ctrl+R：刷新当前页
                if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.R))
                {
                    e.SuppressKeyPress = true;
                    if (_current != null)
                    {
                        try { await _current.RefreshAsync().ConfigureAwait(true); }
                        catch (Exception ex) { _ctx.Error(_current.Title + "刷新", ex); }
                    }
                }
                // Esc：从会话详情返回
                else if (e.KeyCode == Keys.Escape)
                {
                    if (_pushedView != null)
                    {
                        e.SuppressKeyPress = true;
                        await PopViewAsync().ConfigureAwait(true);
                    }
                }
                // Ctrl+1..9：快速切页（只到 9，页面多于 9 个时后面的够不着——
                // 所以另配 Ctrl+Tab 循环，见下）
                else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
                {
                    int idx = e.KeyCode - Keys.D1;
                    if (idx < PageRegistry.All.Count)
                    {
                        e.SuppressKeyPress = true;
                        await GoAsync(PageRegistry.All[idx].Id).ConfigureAwait(true);
                    }
                }
                // Ctrl+Tab / Ctrl+Shift+Tab：顺序循环切页。
                // Ctrl+数字覆盖前 9 页，本程序有 13 页——后 4 页（本地工具/通知/
                // 设置/关于）没有数字键可映射，用循环切补上，
                // 「Ctrl+数字切页」这句状态栏承诺才算完整兑现。
                // 会话详情（_pushedView）打开时不响应——那是另一个上下文，
                // 循环切页会绕开 Esc 返回的预期。
                else if (e.Control && e.KeyCode == Keys.Tab && _pushedView == null)
                {
                    var all = PageRegistry.All;
                    if (all.Count > 0)
                    {
                        e.SuppressKeyPress = true;
                        int cur = _current != null ? all.IndexOf(_current) : -1;
                        int next = e.Shift
                            ? (cur <= 0 ? all.Count - 1 : cur - 1)
                            : (cur + 1) % all.Count;
                        await GoAsync(all[next].Id).ConfigureAwait(true);
                    }
                }
            };
        }

        // ---------- 状态栏 ----------

        public void Toast(string message, bool ok)
        {
            if (_status == null || _status.IsDisposed) return;
            _status.Text = message;
            _status.ForeColor = ok ? Theme.Ok : Theme.Danger;

            if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); }
            _statusTimer = new Timer { Interval = 6000 };
            _statusTimer.Tick += (s, e) =>
            {
                _statusTimer.Stop();
                if (_status != null && !_status.IsDisposed)
                {
                    _status.Text = _statusDefault;
                    _status.ForeColor = Theme.Muted;
                }
            };
            _statusTimer.Start();
        }

        public void SetStatusDefault(string text)
        {
            _statusDefault = text ?? "就绪";
            if (_status != null && !_status.IsDisposed) _status.Text = _statusDefault;
        }

        /// <summary>
        /// Win11 原生圆角窗口（对应 SaltUI 的 dialogCorner 视觉效果）。
        /// 在 Win10 及更早系统上 DwmSetWindowAttribute 返回非 0，窗口保持直角——
        /// 不报错、不用判断版本。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win11.RoundCorners(Handle);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); }
        }

        /// <summary>
        /// 会话列表容器：每次滚动后整表重绘。
        /// 残影来自 ScrollWindowEx 平移旧像素 + 行内采样的渐变按旧位置烘焙，
        /// 重绘让每一行按当前屏幕位置重新采样背景，拖影即消。
        /// </summary>
        private sealed class SessListPanel : Panel
        {
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x020A || m.Msg == 0x0115)   // WM_MOUSEWHEEL / WM_VSCROLL
                    Invalidate();
            }
        }
    }
}
