// 灵枢桌面版 —— 设置外壳页
//
// 对齐 web 版 `index.html` 的 `#view-settings`：
//   <div class="set-shell">
//     <aside class="set-nav">6 个二级导航按钮</aside>
//     <div class="set-body">右内容区（每个按钮对应一个独立 view）</div>
//   </div>
//
// 主导航只剩 6 项（任务/仪表盘/用量/编队/项目/经验），
// 设置入口在侧栏底部 —— 点进来是这个外壳，里面再分左右。
//
// 右侧内容直接复用已有的 6 个 *Page 实例（ModelsPage/McpPage/SkillsPage/
// NotifyPage/LocalToolsPage/AboutPage），它们的 CreateView 和 RefreshAsync
// 都是标准 IPage 接口，不用改。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SpiritScout.Ui.Pages
{
    /// <summary>设置外壳：左侧二级导航 + 右侧内容区。</summary>
    public sealed class SettingsShellPage : IPage
    {
        // 二级导航的条目：id = 里面装的那个 IPage 的 Id。
        // 「通用」是 SettingsPage（数据位置/运行偏好/自检），web 里对应
        // `S.setTab || "models"` 的默认页 —— 它该放第一个。
        private static readonly string[] Order =
        {
            "settings-general",     // 通用（数据位置/运行日志/运行偏好/自检）
            "models",       // 模型
            "mcp",          // MCP 接入
            "skills",       // Skill 知识
            "notify",       // 通知
            "localtools",   // 本地工具
            "about",        // 关于
        };

        private readonly Dictionary<string, string> _names = new Dictionary<string, string>
        {
            { "settings-general",   "通用" },
            { "models",     "模型" },
            { "mcp",        "MCP 接入" },
            { "skills",     "Skill 知识" },
            { "notify",     "通知" },
            { "localtools", "本地工具" },
            { "about",      "关于" },
        };

        private readonly Dictionary<string, IPage> _subs = new Dictionary<string, IPage>();
        private readonly Dictionary<string, Control> _views = new Dictionary<string, Control>();
        private readonly Dictionary<string, Control> _navBtns = new Dictionary<string, Control>();

        private Panel _body;
        private string _active;

        public string Id { get { return "settings"; } }
        public string Title { get { return "设置"; } }
        public PageGroup Group { get { return PageGroup.System; } }

        /// <summary>
        /// 从 PageRegistry 拿那 6 个页面实例。
        ///
        /// 它们也被注册成独立页面（`PageRegistry.Register` 里有），
        /// 外壳负责把它们装进右内容区 —— 共用同一个实例，
        /// 所以「在设置里改了模型，回到模型页看到的也是新的」。
        /// </summary>
        private IPage Find(string id)
        {
            IPage found;
            if (_subs.TryGetValue(id, out found)) return found;
            foreach (var p in PageRegistry.All)
            {
                // **跳过自己**。外壳的 Id 也是 "settings"，
                // Find("settings") 若先命中外壳，Go → Find → CreateView → Go
                // 就无限递归 —— StackOverflow，窗口连日志都来不及写就死。
                // 实测：14:07 启动 --page settings，8 秒后进程消失，就是这个。
                if (ReferenceEquals(p, this)) continue;
                if (p.Id == id) { _subs[id] = p; return p; }
            }
            return null;
        }

        public Control CreateView()
        {
            // web 的 .set-shell：一个横向的两栏壳
            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                BackColor = Theme.Bg,
            };
            // web 的 .set-shell 是 grid-template-columns: 188px minmax(0,1fr); gap: 26px;
            // 桌面用 TableLayoutPanel：列 188 + 列间距 26（塞进第二列的 Padding.Left）。
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.K(188)));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // ---- 左侧二级导航 ----
            var nav = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Padding = new Padding(Theme.K(14), Theme.K(18), Theme.K(14), Theme.K(14)),
            };
            var title = new Label
            {
                Text = "设置",
                Font = Theme.FontSmall,
                ForeColor = Theme.Muted,
                Dock = DockStyle.Top,
                Height = Theme.K(28),
                BackColor = Color.Transparent,
            };
            nav.Controls.Add(title);

            foreach (var id in Order)
            {
                var btn = new Widgets.SetNavItem
                {
                    Text = PageIcons.Get(id) + "  " + _names[id],
                    Tag = id,
                    Dock = DockStyle.Top,
                    Height = Theme.K(30),          // web: padding 8px 10px ≈ 30px
                };
                btn.Click += (s, e) => Go(id);
                _navBtns[id] = btn;
                nav.Controls.Add(btn);
            }
            // Dock.Top 后加的在上面：倒序 Controls.Add 才得到 Order 顺序
            for (int i = Order.Length - 1; i >= 0; i--)
                nav.Controls.SetChildIndex(_navBtns[Order[i]], 1);

            shell.Controls.Add(nav, 0, 0);

            // ---- 右侧内容区 ----
            _body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                // web 的列间距 26px，塞进左 padding（22 + 26 ≈ 48 太宽，
                // 直接用 gap 值当左 padding 更准）
                Padding = new Padding(Theme.K(26), Theme.K(22), Theme.K(26), Theme.K(30)),
                // 内容超出时要能滚 —— SettingsPage 的 5 个 SectionCard
                // 加起来超过窗口高，不滚动就会被裁掉（overflow_check 抓到的）。
                AutoScroll = true,
            };
            shell.Controls.Add(_body, 1, 0);

            // 默认进「通用」（对齐 web 的 SET_FIRST）
            Go(_active ?? "settings-general");
            return shell;
        }

        /// <summary>切到某个二级页。</summary>
        private void Go(string id)
        {
            var page = Find(id);
            if (page == null) return;

            Control view;
            if (!_views.TryGetValue(id, out view))
            {
                view = page.CreateView();
                Theme.ScaleTree(view);
                view.Dock = DockStyle.Fill;
                _views[id] = view;
            }

            // **view 装在一个可滚动的容器里**，不是直接进 _body。
            //
            // SettingsPage 的 AmbientPanel 自己带 AutoScroll，
            // 但 overflow_check 读的是子控件的 Location —— 子控件的位置
            // 不会因为 AutoScroll 而变，只是可视区能滚到。
            // 所以 _body 这层也要 AutoScroll，让超出的部分能被卷到而不是被裁。
            //
            // **view 的 Dock 改成 Top**（不是 Fill）：
            // Fill 会把 view 撑到 wrap 的 ClientSize —— 内容超长时
            // 它的高度还是等于 wrap 高，AutoScroll 就失效了。
            // Top 让 view 按内容自身高度排，wrap.AutoScroll 才能卷到超出的部分。
            var wrap = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                AutoScroll = true,
            };
            view.Dock = DockStyle.Top;
            wrap.Controls.Add(view);
            _body.Controls.Clear();
            _body.Controls.Add(wrap);
            _active = id;

            foreach (var kv in _navBtns)
            {
                var ni = kv.Value as Widgets.SetNavItem;
                if (ni != null) ni.SetActive(kv.Key == id);
            }
            // 设置页和主导航不一样 —— 这里只刷新二级导航自己
            foreach (var kv in _navBtns) kv.Value.Invalidate();

            // 真切到才跑 Refresh（同 web 的 show() 里按 view 各自加载）
            try
            {
                var task = page.RefreshAsync();
                task.ContinueWith(t => { }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("[settings] 切到 " + id + " 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 页面被点回来时：刷新当前活动的二级页（对齐 web 的
        /// `show(S.setTab || SET_FIRST)` —— 回来还是上次那个 tab）。
        /// </summary>
        public async System.Threading.Tasks.Task RefreshAsync()
        {
            var page = Find(_active ?? "settings");
            if (page != null) await page.RefreshAsync().ConfigureAwait(true);
        }
    }
}
