// 灵枢桌面版 —— 程序入口
//
// 启动序列：
//   1. 单实例检查（同一个人开两个会抢数据文件）
//   2. 初始化数据目录（%APPDATA%\SpiritScout，已存在则原样保留）
//   3. 注册页面
//   4. 显示主窗口
//
// 没有端口、没有子进程、没有外部依赖——这台机器上装了 .NET Framework 4.8
// 就能跑，而那是 Windows 10/11 自带的。

using System;
using System.Threading;
using System.Windows.Forms;

namespace SpiritScout.Ui
{
    internal static class Program
    {

        // ---- 高 DPI 运行时兜底 ----
        // manifest 里已声明 dpiAware=true（系统 DPI aware）。单文件 exe 没有
        // app.config，.NET 4.8 的 PerMonitorV2 通路不可靠（实测 DeviceDpi 恒 96、
        // 自动缩放不触发），所以选系统 aware：一次会话一个比例，启动时按屏幕
        // 缩放自适应；比例由 Theme.InitScale 读真实 DPI，控件树统一 Scale。
        // 这里再调一次做双保险；老系统没有 shcore.dll，调不到就静默跳过。
        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static void EnableDpiAwareness()
        {
            try { SetProcessDpiAwareness(1); return; }   // 1 = PROCESS_SYSTEM_DPI_AWARE
            catch { }
            try { SetProcessDPIAware(); }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {

            // 允许多开。
            //
            // 原来用 Mutex 挡第二个实例，理由是「同一个人开两个会抢数据文件」。
            // 那个担心是对的，但解法不对——直接不让开太粗暴（用户可能想并排看两个会话）。
            //
            // 现在改成：**允许多开，靠存储层保证不打架**。
            //   · JsonStore 的临时文件名带唯一后缀（不会抢同一个 .tmp）
            //   · 落盘失败重试 12 次（跨进程并发是暂时的，等几十毫秒就好）
            //   · 进程内还有静态 WriteLock 兜底
            //
            // 多开时两个窗口共享同一份数据目录，所以一个窗口改了东西，
            // 另一个要刷新才看得到（1 秒的 List 缓存也在这个尺度内）。
            // 这是「共享同一个数据目录」的必然结果，不是 bug。
            // 全局 TLS 1.2 兜底。
            //
            // .NET Framework 4.8 的默认值是 `Ssl3, Tls`（取决于注册表/系统策略），
            // 不含 TLS 1.2。任何走 HttpClient 的 HTTPS 请求都会在百毫秒内被服务端掐断。
            // 这里在程序入口统一设一次，比每个工具各设一遍可靠。
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12;
            }
            catch { }

            EnableDpiAwareness();
            Theme.InitScale();

            // 无障碍：尝试启用 WinForms 的新一代 UIA provider。
            //
            // .NET Framework 4.7.1 起 WinForms 有了新的无障碍实现，开关放在
            // app.config 的 AppContextSwitchOverrides 里。本程序是单文件 exe、
            // 没有 app.config，所以在入口用代码设。
            //
            // **实测在本项目环境下无效**（124 轮）：设了之后跨进程 UI Automation
            // 仍然把自绘控件报成 `ControlType.Pane`。根因不在开关——
            // 进程内查 `AccessibilityObject.Role` 返回的**确实是 PushButton**、
            // `DefaultAction` 是「按」，与原生 Button 完全一致；
            // 退化成 Pane 的是 **UIA→MSAA 桥**那一层（跨进程时桥没采用 accRole）。
            //
            // 保留这段是因为它零成本、且换到有 app.config 的部署形态时开关本就该在；
            // 但**别指望它修好 UIA**——真要支持读屏，得自实现
            // IRawElementProviderSimple（工程量大，当前无此需求）。
            try
            {
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures", false);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.2", false);
                AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyAccessibilityFeatures.3", false);
            }
            catch { }

            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 未处理异常：写日志 + 提示，而不是静默消失
                Application.ThreadException += (s, e) =>
                    ReportCrash("界面线程异常", e.Exception);

                // 非 UI 线程（后台任务）的异常：进程会被直接结束。
                // 原来这里**只写日志不提示**，用户看到的是窗口突然没了、不知道发生了什么。
                // 加一个提示框——虽然进程马上就退，但至少让人知道刚才不是自己点错了。
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    ReportCrash("后台线程异常（程序即将退出）", e.ExceptionObject);
                };

                // 初始化数据目录
                var services = new AppServices();
                try
                {
                    var cfg = new Core.ConfigManager();
                    cfg.EnsureInitialized();
                    services.Config = cfg.Current;
                    services.Sessions = new Core.SessionRepository();
                    services.Ready = true;
                    SafeLog("启动 v" + AppVersion + "，数据目录 " + Core.AppPaths.DataRoot);
                }
                catch (Exception ex)
                {
                    SafeLog("初始化失败 · v" + AppVersion + ":\n" + ex);
                    MessageBox.Show(
                        "启动失败：数据目录用不了。\n\n"
                        + "【版本】" + AppVersion + "\n"
                        + "【错误】" + ex.Message + "\n\n"
                        + "【目录】" + Core.AppPaths.DataRoot + "\n\n"
                        + "【怎么办】\n"
                        + "  · 这个目录可能被占用了（另开了一个灵枢窗口）、只读，\n"
                        + "    或者当前账户没有写权限；\n"
                        + "  · 关掉其它灵枢窗口再试；\n"
                        + "  · 还不行就把 exe 放到一个你有写权限的目录里再双击，\n"
                        + "    它会自动改用 exe 旁边的 data\\ 存数据。",
                        "灵枢 · SpiritScout", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 注册页面（增删功能只改这一段）
                RegisterPages();

                var form = new MainForm(services);

                // 窗口状态：记住大小/位置/最大化
                var state = WindowStateStore.Load();
                WindowStateStore.Apply(form, state);

                // 首屏：没配模型就去「模型」页，配了就回上次那页
                if (string.IsNullOrEmpty(form.StartupPage))
                {
                    form.StartupPage = FirstRun.DecideStartupPage(state.LastPage);
                }
                // 首次引导：没配模型时状态栏给一句指引
                if (!FirstRun.HasModel())
                {
                    form.SetStatusDefault("先配一个模型：选供应商 → 填端点 → 填 API Key");
                }

                // --page <id>：直接打开某一页（调试与快捷方式用）
                // --screenshot <path>：渲染完成后把窗口画成 PNG 再退出（截图工具用）
                // --no-focus：窗口显示但不激活、不抢前台（检查脚本后台跑用，不打扰用户）
                foreach (string a in args)
                {
                    if (string.Equals(a, "--no-focus", StringComparison.OrdinalIgnoreCase))
                        form.NoFocus = true;
                    else if (string.Equals(a, "--theme-cycle", StringComparison.OrdinalIgnoreCase))
                        form.ThemeCycle = true;   // 截图前先切一次主题，验运行时换色
                }
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--page", StringComparison.OrdinalIgnoreCase))
                        form.StartupPage = args[i + 1];
                    else if (string.Equals(args[i], "--screenshot", StringComparison.OrdinalIgnoreCase))
                        form.ShotPath = args[i + 1];
                    else if (string.Equals(args[i], "--dump-ui", StringComparison.OrdinalIgnoreCase))
                        form.DumpUiPath = args[i + 1];
                    else if (string.Equals(args[i], "--send-text", StringComparison.OrdinalIgnoreCase))
                        form.SendText = args[i + 1];   // 打开会话后自动发一条（检查脚本用）
                }
                form.SetStatusDefault("就绪 · F5 刷新 · Ctrl+1-9 直达前 9 页 · Ctrl+Tab 循环切页 · 数据在 " + Core.AppPaths.DataRoot);
                Application.Run(form);

                Pages.NotifyCenter.Shutdown();
                // 截图模式不记窗口状态 —— 那是调试用的临时尺寸，别覆盖用户的位置
                if (string.IsNullOrEmpty(form.ShotPath) && string.IsNullOrEmpty(form.DumpUiPath))
                    WindowStateStore.Capture(form, form.CurrentPageId, true);
                Core.ConfigManager.Log("退出");
            }
        }

        /// <summary>
        /// 页面注册表。加一个功能页 = 在这里加一行。
        /// 顺序即侧栏顺序（同组内）。
        /// </summary>
        /// <summary>
        /// 写日志，**自身不再抛**。
        ///
        /// `ConfigManager.Log` 在日志目录不可写时会抛（磁盘满、权限、只读介质）。
        /// 在异常处理器里再抛一次就等于什么都没有了。
        /// </summary>
        private static void SafeLog(string line)
        {
            try { Core.ConfigManager.Log(line); }
            catch { }
        }

        /// <summary>
        /// 崩溃时统一出口：写日志 + 弹一个**人能看懂**的提示框。
        ///
        /// 提示里必须有三样东西，否则用户只能干瞪眼：
        ///   1. **发生了什么**（一句人话）
        ///   2. **日志在哪**（可以截图发给别人）
        ///   3. **下一步做什么**（不是「请稍后再试」这种废话）
        /// </summary>
        private static void ReportCrash(string where, object ex)
        {
            string detail;
            string message;
            var e = ex as Exception;
            if (e != null) { detail = e.ToString(); message = e.Message; }
            else { detail = ex == null ? "（无异常对象）" : ex.ToString(); message = detail; }

            SafeLog(where + " · v" + AppVersion + ":\n" + detail);

            try
            {
                MessageBox.Show(
                    "程序遇到了一个没预料到的错误。\n\n"
                    + "【位置】" + where + "\n"
                    + "【版本】" + AppVersion + "\n"
                    + "【错误】" + Core.Text.OneLine(message, 300) + "\n\n"
                    + "【日志】" + Core.AppPaths.LogDir + "\n"
                    + "        最新那个 app-YYYYMMDD.log 就是这次的记录。\n\n"
                    + "【下一步】\n"
                    + "  · 如果只是偶发，关掉重开通常能继续用；\n"
                    + "  · 如果每次都在同一步出错，把那份日志发给开发者——\n"
                    + "    里面有完整的调用栈。",
                    "灵枢 · SpiritScout", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        /// <summary>版本号（唯一来源是 Core\AppInfo.cs）。文案里要带上，方便对上日志。</summary>
        private const string AppVersion = Core.AppInfo.Version;

        private static void RegisterPages()
        {
            PageRegistry.Clear();

            // 主功能
            PageRegistry.Register(new Pages.HomePage());
            PageRegistry.Register(new Pages.DashPage());
            PageRegistry.Register(new Pages.UsagePage());

            // 智能体
            PageRegistry.Register(new Pages.AgentsPage());
            PageRegistry.Register(new Pages.ModelsPage());
            PageRegistry.Register(new Pages.ProjectsPage());

            // 知识
            PageRegistry.Register(new Pages.ExperiencePage());
            PageRegistry.Register(new Pages.SkillsPage());
            PageRegistry.Register(new Pages.McpPage());
            PageRegistry.Register(new Pages.LocalToolsPage());

            // 系统
            PageRegistry.Register(new Pages.NotifyPage());
            // web 的 `#view-settings` 是一个外壳：左 set-nav + 右 set-body，
            // 模型/MCP/Skill/通知/本地工具/关于全在里面走二级导航切换。
            // SettingsShellPage 里装了那 6 个 *Page 实例（Id 照旧）。
            // SettingsPage（通用设置：数据位置/日志/偏好/自检）也要注册 ——
            // SettingsShellPage 的「通用」tab 要靠它装内容，
            // 不注册的话外壳的 Find("settings") 找不到它，那一格是空的。
            PageRegistry.Register(new Pages.SettingsPage());
            PageRegistry.Register(new Pages.SettingsShellPage());
            PageRegistry.Register(new Pages.AboutPage());
        }
    }
}
