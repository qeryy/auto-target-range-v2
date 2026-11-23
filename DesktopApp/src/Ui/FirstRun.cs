// 灵枢桌面版 —— 首次使用引导 + 窗口状态记忆
//
// ## 首次引导
//
// 新用户第一次打开，看到的是一堆空表格——不知道该先干什么。真正需要他做的
// 只有一件事：**配一个模型**。没模型什么都干不了。
//
// 这里在主窗口弹出前做一次检查：没有可用模型，就引导到「模型」页并显示一条
// 说明；已经配过就正常进任务页。只在新装时打扰一次，之后不再提。
//
// ## 窗口状态记忆
//
// 桌面软件的标配：记住窗口大小、位置、最大化状态、上次停留的页面。
// 下次打开接着上次用。存 window-state.json。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SpiritScout.Ui
{
    /// <summary>窗口状态。</summary>
    public sealed class WindowState
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public bool Maximized { get; set; }
        public string LastPage { get; set; }
        public bool TutorialSeen { get; set; }

        public WindowState()
        {
            // 默认尺寸按屏幕工作区算，别写死——小屏上写死会放不下
            int w = 1400, h = 900;
            try
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                int dpi = 96;
                try
                {
                    using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    {
                        int d = (int)Math.Round(g.DpiX);
                        if (d >= 72 && d <= 480) dpi = d;
                    }
                }
                catch { }
                float scale = dpi / 96f;
                w = Math.Min(1400, (int)(wa.Width / scale * 0.92));
                h = Math.Min(900, (int)(wa.Height / scale * 0.92));
            }
            catch { }
            Width = w;
            Height = h;
            Left = -1;
            Top = -1;
            Maximized = false;
            LastPage = "home";
            TutorialSeen = false;
        }
    }

    public static class WindowStateStore
    {
        public static string PathFile
        {
            get { return System.IO.Path.Combine(Core.AppPaths.DataRoot, "window-state.json"); }
        }

        public static WindowState Load()
        {
            var s = Core.JsonStore.Read<WindowState>(PathFile, null) ?? new WindowState();
            // 防御：尺寸不能大到超出屏幕，否则窗口会跑到看不见的地方
            // Screen.WorkingArea 是**物理**像素，换算成逻辑再和窗口尺寸比
            int dpi = ScreenDpi();
            float scale = dpi / 96f;
            var waPhys = Screen.PrimaryScreen.WorkingArea;
            int logicalW = (int)Math.Round(waPhys.Width / scale);
            int logicalH = (int)Math.Round(waPhys.Height / scale);

            // 尺寸下限放宽到 720x480：小屏笔记本的合法尺寸不该被当成非法值重置
            if (s.Width < 720 || s.Width > logicalW * 2)
                s.Width = Math.Min(1400, (int)(logicalW * 0.92));
            if (s.Height < 480 || s.Height > logicalH * 2)
                s.Height = Math.Min(900, (int)(logicalH * 0.92));

            // 位置校验放在 Apply 里做——那里拿到了窗口的 DeviceDpi，
            // 才能把 Screen 的**物理**坐标换算成窗口用的**逻辑**坐标。
            // （DPI 感知模式下 Screen.AllScreens 返回物理像素，而 WinForms
            //   控件的 Location/Bounds 是逻辑像素，直接比会得出反向结论。）
            return s;
        }

        /// <summary>
        /// 取屏幕的真实 DPI。
        ///
        /// **不能用 form.DeviceDpi**：窗口还没显示时它返回默认值 96，
        /// 而系统可能是 150%。用错了会让「物理→逻辑」换算完全不生效——
        /// 表现就是「窗口在屏幕外，程序却认为在屏内」。
        /// 用屏幕 DC 的 DPI 最可靠（Graphics.FromHwnd(IntPtr.Zero) 拿的是主屏 DC）。
        /// </summary>
        private static int ScreenDpi()
        {
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    int d = (int)Math.Round(g.DpiX);
                    if (d >= 72 && d <= 480) return d;
                }
            }
            catch { }
            return 96;
        }

        /// <summary>
        /// 窗口与任意屏幕的重叠面积是否够大（至少 96x96 可见，且占窗口面积 > 10%）。
        /// 传入的 left/top/width/height 与 Screen.* 一样都是**物理**像素
        /// （系统 DPI 感知下窗口 Bounds 不做虚拟化），同单位直接比。
        /// </summary>
        private static bool HasEnoughOverlap(int left, int top, int width, int height, int dpi)
        {
            try
            {
                long windowArea = (long)Math.Max(1, width) * Math.Max(1, height);
                long best = 0;

                foreach (var scr in Screen.AllScreens)
                {
                    var wa = scr.WorkingArea;
                    int aL = wa.Left;
                    int aT = wa.Top;
                    int aR = wa.Right;
                    int aB = wa.Bottom;

                    int x1 = Math.Max(left, aL);
                    int y1 = Math.Max(top, aT);
                    int x2 = Math.Min(left + width, aR);
                    int y2 = Math.Min(top + height, aB);
                    int w = x2 - x1;
                    int h = y2 - y1;
                    if (w < 96 || h < 96) continue;   // 连标题栏都放不下，等于不可见
                    long area = (long)w * h;
                    if (area > best) best = area;
                }

                return best > 0 && best * 10 >= windowArea;
            }
            catch
            {
                return false;
            }
        }

        public static void Save(WindowState state)
        {
            if (state == null) return;
            Core.JsonStore.Write(PathFile, state);
        }

        /// <summary>把当前窗口状态写回存储。</summary>
        public static void Capture(Form form, string currentPage, bool tutorialSeen)
        {
            if (form == null) return;
            try
            {
                var s = Load();
                // 最大化时记录还原后的尺寸，否则下次取消最大化会是个奇怪的大小
                var bounds = form.WindowState == FormWindowState.Maximized
                    ? form.RestoreBounds
                    : form.Bounds;
                // 存「客户区」设计单位：自管 DPI 缩放后 Bounds 是物理像素，
                // 直接存物理值会让下次启动（或换缩放比例）恢复出错误尺寸。
                // 统一客户区口径，和 WindowState 默认值 / ApplyAdaptiveSize 一致。
                var nc = form.Bounds.Size - form.ClientSize;
                s.Width = (int)Math.Round((bounds.Width - nc.Width) / Theme.UiScale);
                s.Height = (int)Math.Round((bounds.Height - nc.Height) / Theme.UiScale);
                s.Left = (int)Math.Round(bounds.Left / Theme.UiScale);
                s.Top = (int)Math.Round(bounds.Top / Theme.UiScale);
                s.Maximized = form.WindowState == FormWindowState.Maximized;
                if (!string.IsNullOrEmpty(currentPage)) s.LastPage = currentPage;
                s.TutorialSeen = tutorialSeen;
                Save(s);
            }
            catch { }
        }

        /// <summary>把存储的状态应用到窗口上。</summary>
        public static void Apply(Form form, WindowState state)
        {
            if (form == null || state == null) return;
            try
            {
                form.StartPosition = FormStartPosition.Manual;
                // 存储是客户区设计单位，窗口客户区是物理像素
                form.ClientSize = new Size(Theme.K(state.Width), Theme.K(state.Height));
                // 恢复尺寸不该超出主屏工作区（旧版存过物理值/换过显示器时兜底）
                var waP = Screen.PrimaryScreen.WorkingArea;
                if (form.Width > waP.Width || form.Height > waP.Height)
                    form.Size = new Size(Math.Min(form.Width, waP.Width), Math.Min(form.Height, waP.Height));

                int dpi = ScreenDpi();

                // state.Left/Top 的 -1 是「从来没保存过位置」的默认值（首次启动、
                // 或上次退出时窗口没落盘）。这不是异常，安静居中即可——
                // 原来这种情形也走下面的日志，日志里就出现
                // 「窗口位置 -1,-1 与任何屏幕的重叠都不足」这种误导性表述，
                // 排查时像出了大事，其实只是第一次打开。
                bool neverSaved = state.Left < 0 || state.Top < 0;
                bool placed = false;
                if (!neverSaved
                    && HasEnoughOverlap(Theme.K(state.Left), Theme.K(state.Top),
                        Theme.K(state.Width), Theme.K(state.Height), dpi))
                {
                    form.Location = new Point(Theme.K(state.Left), Theme.K(state.Top));
                    placed = true;
                }

                if (!placed)
                {
                    // 没有记录位置、或位置已经不在任何屏幕上 → 居中到主屏
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    float scale = dpi / 96f;
                    int lw = (int)Math.Round(wa.Width / scale);
                    int lh = (int)Math.Round(wa.Height / scale);
                    form.Location = new Point(
                        Theme.K(Math.Max(0, (lw - state.Width) / 2)),
                        Theme.K(Math.Max(0, (lh - state.Height) / 2)));
                    // 只有「存过但放不下」（换显示器/分辨率变了）才值得记一笔
                    if (!neverSaved)
                        Core.ConfigManager.Log("窗口位置 " + state.Left + "," + state.Top
                            + " 与任何屏幕的重叠都不足（DPI " + dpi + "，可能换了显示器），已回到主屏中央 "
                            + form.Left + "," + form.Top);
                }

                if (state.Maximized) form.WindowState = FormWindowState.Maximized;
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("Apply 失败: " + ex.Message);
            }
        }
    }

    /// <summary>首次使用检测。</summary>
    public static class FirstRun
    {
        /// <summary>是否已经配了至少一个可用模型。</summary>
        public static bool HasModel()
        {
            try
            {
                var reg = new Llm.ModelRegistry(
                    Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                bool any = false;
                foreach (var m in reg.Models())
                {
                    if (!m.Enabled) continue;
                    // 有模型还不够，供应商和密钥也得在
                    var p = reg.FindProvider(m.ProviderName);
                    if (p != null && reg.HasKey(p.Name)) { any = true; break; }
                }
                reg.Dispose();
                return any;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 决定首屏：回到上次停留的页面，没有就落「任务」首页。
        ///
        /// 不再因为「没配模型」强制跳到模型页——首页本身就是引导入口
        /// （hero + 模式卡 + 描述 composer），没模型时点「开始执行」会就地
        /// 提示去配模型。首屏直接落在产品主界面，和 web 版一致。
        /// </summary>
        public static string DecideStartupPage(string lastPage)
        {
            // **首屏总是落「任务」首页，不再记上次位置。**
            //
            // 用户原话（2026-11-22）：「默认开启时进入的界面改为任务界面」。
            // 原来是「记上次停在哪儿回哪儿」，用户改了主意 ——
            // 每次打开都从任务页起，和 web 版行为一致（web 也是固定 /tasks）。
            //
            // `--page xxx` 或 `--page session:xxx` 不受影响（那是显式指定，不进这里）。
            return "home";
        }
    }
}
