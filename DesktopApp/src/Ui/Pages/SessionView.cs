// 灵枢桌面版 —— 页面：会话详情（聊天）
//
// 这一页对应 web 端「会话」标签的核心：消息流 + 输入框，能和模型对话。
// 其余标签（拓扑/漏洞/Flag/事件/记录）后续轮次逐步加，每加一个在这里加一个
// TabPage 即可，不影响已有部分。
//
// 布局：
//   顶部：标题 + 元信息（模式/状态/token）+ 操作按钮
//   中间：消息流（可滚动，自绘气泡）
//   底部：输入框 + 发送（Ctrl+Enter 也可发送）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Llm;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    /// <summary>
    /// 会话详情。它不是 IPage（不进侧栏），而是由任务页打开的独立页。
    /// </summary>
    public sealed class SessionView : UserControl
    {
        private readonly Core.SessionStore _store;
        private readonly ModelRegistry _registry;
        private readonly string _sessionId;

        private TabControl _tabs;
        private Label _titleLabel;

        /// <summary>已接入的 MCP 长连接（关闭会话时释放）。</summary>
        private List<Tools.McpConnection> _mcpConns;
        private RichFlowPanel _flow;
        private TextBox _input;
        private Button _send;
        private Label _meta;
        private Label _hint;
        private Widgets.DarkSelect _modelPicker;
        private CancellationTokenSource _cts;
        private readonly Tools.ToolRegistry _tools;
        private bool _busy;

        // 子 agent 旁路对话（web 的 agent-side-chat）
        private Panel _sidePanel;
        private RichFlowPanel _sideFlow;
        private DarkTextBox _sideInput;
        private Label _sideTitle;
        private Widgets.FlatButton _sideSend;
        private string _sideSubId;

        /// <summary>上次按哪个 flow 宽度量的气泡。宽度变了要重量（见 _flow.Resize）。</summary>
        private int _bubbleFlowW;

        public event Action Back;

        /// <summary>
        /// **只给检查脚本用**：把文字填进输入框并触发发送，等价于点一下「发送」按钮。
        ///
        /// 为什么需要这个入口：`DarkTextBox` / `FlatButton` 虽然是标准 TextBox/Button
        /// 派生，但这个程序的 UIA 树只暴露到容器层（实测 43 个元素全是 Pane），
        /// 拿不到 Edit/Button，ValuePattern / InvokePattern 都用不上。
        /// 硬做坐标点击又怕分辨率变化就失效。
        ///
        /// 用 `--send-text` 走这条路：脚本只判结果（会话文件里的消息和阶段），
        /// 不碰界面细节。和 `--theme-cycle` / `--dump-ui` 是同一类调试钩子。
        /// </summary>
        public void SendForCheck(string text)
        {
            if (_input == null || string.IsNullOrEmpty(text)) return;
            _input.Text = text;
            UiSafety.Fire(SendAsync(), "自检发送");
        }

        public SessionView(Core.SessionStore store, ModelRegistry registry, string sessionId)
        {
            _store = store;
            _registry = registry;
            _sessionId = sessionId;
            _tools = Tools.ToolRegistry.CreateDefault();
            // 异步装配 MCP（不 await：不能卡住界面构建，装配好之后下一轮模型就能看到）
            var mountTask = MountMcpAsync();
            mountTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Core.ConfigManager.Log("MCP 装配任务异常: " + t.Exception.GetBaseException().Message);
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            // 历史气泡不在构造时画：那时树还没被 ScaleTree 缩放、消息流宽度也没落定。
            // 首次 _flow.Resize（布局落定、缩放之后）会触发 LoadHistory，
            // 气泡按物理宽度直接构造，避免二次缩放。
        }

        /// <summary>
        /// 把「已启用的 MCP 服务器」装配进工具表。异步做——MCP 拉子进程要时间，
        /// 不能卡住界面构建。装配好之后模型的下一轮就能看到这些工具。
        /// </summary>
        private async Task MountMcpAsync()
        {
            try
            {
                var repo = new Tools.McpRepository(
                    System.IO.Path.Combine(Core.AppPaths.DataRoot, "mcp.json"));
                var enabled = repo.List().FindAll(s => s.Enabled);
                if (enabled.Count == 0) return;

                _mcpConns = await Tools.McpToolLoader.MountAsync(repo, _tools)
                    .ConfigureAwait(true);

                if (_mcpConns.Count > 0)
                {
                    AddBubble("system", "已接入 " + _mcpConns.Count + " 个 MCP 服务器（"
                        + _tools.Count + " 个工具可用）");
                    ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("装配 MCP 失败: " + ex.Message);
            }
        }

        private void Build()
        {
            var session = _store.Load(_sessionId);
            string title = session != null && session.Session != null ? session.Session.Target : _sessionId;

            // --- 顶部 ---
            var head = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Bg };

            var back = Theme.Ghost("← 返回", 82, 32);
            back.Location = new Point(0, 6);
            back.Click += (s, e) => { var h = Back; if (h != null) h(); };

            _titleLabel = new Label
            {
                Text = title,
                Font = Theme.FontHeading,
                ForeColor = Theme.Text,
                AutoSize = false,
                AutoEllipsis = true,
                Height = 28,
                Width = 300,
                Location = new Point(94, 4),
            };
            _meta = new Label
            {
                Text = "",
                Font = Theme.FontTiny,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Location = new Point(96, 30),
            };

            // 模型选择（深色自绘下拉，系统 ComboBox 的按钮和边框是白的）
            var models = _registry.Models();
            var names = new List<string>();
            foreach (var m in models) { if (m.Enabled) names.Add(m.Name); }
            string want = "";
            try
            {
                var cfg = new Core.ConfigManager();
                cfg.Load();
                want = cfg.Current.DefaultModel;

                // **NamingChain 优先于 DefaultModel。**
                //
                // web 的 naming_chain 是「模型优先级链」——第一条就是首选。
                // 配了它，这个会话的默认模型就该是链头，不是全局默认模型。
                var chain = cfg.Current.NamingChain;
                if (chain != null && chain.Count > 0 && names.IndexOf(chain[0]) >= 0)
                    want = chain[0];
            }
            catch { }
            _modelPicker = new Widgets.DarkSelect();
            _modelPicker.SetItems(names,
                names.IndexOf(want) >= 0 ? want : (names.Count > 0 ? names[0] : ""));
            _modelPicker.Width = 180;
            _modelPicker.Height = 30;
            _modelPicker.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var regen = Theme.Ghost("重新生成", 96, 32);
            regen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            regen.Click += (s4, e4) => Regenerate();

            var copyAll = Theme.Ghost("复制全文", 88, 32);
            copyAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            copyAll.Click += (s3, e3) => CopyAllText();

            var exportBtn = Theme.Ghost("导出 MD", 88, 32);
            exportBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            exportBtn.Click += (s2, e2) => ExportMarkdown();

            head.Controls.Add(back);
            _titleLabel.Cursor = Cursors.IBeam;
            _titleLabel.DoubleClick += (s5, e5) => RenameSession();
            head.Controls.Add(_titleLabel);
            head.Controls.Add(_meta);
            head.Controls.Add(_modelPicker);
            head.Controls.Add(exportBtn);
            head.Controls.Add(copyAll);
            head.Controls.Add(regen);
            head.Resize += (s, e) =>
            {
                if (Theme.IsScaling) return;
                float u = Theme.UnitOf(head);
                regen.Location = new Point(head.Width - regen.Width - (int)Math.Round(4 * u), (int)Math.Round(9 * u));
                copyAll.Location = new Point(
                    head.Width - regen.Width - copyAll.Width - (int)Math.Round(10 * u), (int)Math.Round(9 * u));
                exportBtn.Location = new Point(
                    head.Width - regen.Width - copyAll.Width - exportBtn.Width - (int)Math.Round(16 * u), (int)Math.Round(9 * u));
                int pickerLeft = head.Width - regen.Width - copyAll.Width - exportBtn.Width
                    - _modelPicker.Width - (int)Math.Round(24 * u);
                _modelPicker.Location = new Point(pickerLeft, (int)Math.Round(8 * u));
                // 标题让位给右侧控件，窄窗口下出省略号而不是压到下拉框上
                _titleLabel.Width = Math.Max((int)Math.Round(140 * u), pickerLeft - (int)Math.Round(94 * u) - (int)Math.Round(12 * u));
            };

            // --- 消息流 ---
            _flow = new RichFlowPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8, 8, 18, 8),
            };

            // 气泡宽度在 AddBubble 里按 _flow.Width 量，但构造时 docking 还没布局，
            // 拿到的是初始小宽度——气泡会被挤成窄条且尺寸缓存后不再变。
            // 布局落定后按真实宽度重测一次；之后窗宽明显变化也重测。
            _flow.Resize += (s, e) =>
            {
                if (Theme.IsScaling) return;
                float u = Theme.UnitOf(_flow);
                int hw = Math.Max((int)Math.Round(200 * u), _flow.ClientSize.Width - (int)Math.Round(26 * u));
                foreach (Control c in _flow.Controls)
                    if (c is Panel) c.Width = hw;
                if (_busy) return;
                if (Math.Abs(_flow.Width - _bubbleFlowW) < (int)Math.Round(48 * u)) return;
                _bubbleFlowW = _flow.Width;
                LoadHistory();
            };

            // ---- 子 agent 旁路对话区（web 的 agent-side-chat）----
            //
            // web 的 view-session 是「主对话 + 右侧子 agent 栏」：
            //   side-chat-head（agent 名 + 来源任务下拉）
            //   side-chat-messages（消息区）
            //   side-chat-compose（输入框 + 发送）
            //
            // 桌面做成**右侧可收合面板**：有子 agent 在跑时展开，
            // 平时收起（不占主对话空间）。宽度 310px（web 的 #tab-chat grid-template-columns 1fr 310px）。
            _sidePanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = Theme.K(310),
                BackColor = Theme.Surface,
                Visible = false,
                Padding = new Padding(Theme.K(10), Theme.K(10), Theme.K(10), Theme.K(10)),
            };
            _sidePanel.Paint += (s2, e2) =>
            {
                using (var pen = new Pen(Theme.LineDark))
                    e2.Graphics.DrawLine(pen, 0, 0, 0, _sidePanel.Height);
            };

            _sideTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = Theme.K(30),
                ForeColor = Theme.Text,
                Font = Theme.FontBody,
                BackColor = Color.Transparent,
            };

            var sideHead = new Panel { Dock = DockStyle.Top, Height = Theme.K(34), BackColor = Color.Transparent };
            var sideClose = new Widgets.FlatButton
            {
                Text = "✕",
                Kind = Widgets.ButtonKind.Ghost,
                Size = new Size(Theme.K(26), Theme.K(26)),
                Dock = DockStyle.Right,
            };
            sideClose.Click += delegate { _sidePanel.Visible = false; };
            sideHead.Controls.Add(sideClose);
            sideHead.Controls.Add(_sideTitle);
            _sideTitle.Dock = DockStyle.Fill;

            _sideFlow = new RichFlowPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(Theme.K(6)),
            };

            var sideBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Theme.K(96),
                BackColor = Color.Transparent,
                Padding = new Padding(0, Theme.K(8), 0, 0),
            };
            _sideInput = new Widgets.DarkTextBox
            {
                Multiline = true,
                Dock = DockStyle.Top,
                Height = Theme.K(56),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FontBody,
            };
            _sideSend = new Widgets.FlatButton
            {
                Text = "发送",
                Kind = Widgets.ButtonKind.Primary,
                Size = new Size(Theme.K(72), Theme.K(30)),
                Dock = DockStyle.Right,
            };
            _sideSend.Click += (s2, e2) => UiSafety.Fire(SendSideAsync(), "发送旁路消息");
            var sideHint = new Label
            {
                Text = "给子 agent 发一条消息（会追加到它的会话）",
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                Dock = DockStyle.Left,
                Width = Theme.K(200),
            };
            sideBottom.Controls.Add(_sideInput);
            sideBottom.Controls.Add(_sideSend);
            sideBottom.Controls.Add(sideHint);

            _sidePanel.Controls.Add(_sideFlow);
            _sidePanel.Controls.Add(sideBottom);
            _sidePanel.Controls.Add(sideHead);

            // --- 输入区 ---
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 100, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 8) };

            _input = Theme.Input(true, 44);
            _input.Dock = DockStyle.Fill;
            _input.Font = Theme.FontBody;
            // 与首页 composer 同款无缝：输入框不自己带边框，整卡一个圆角面
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = Theme.Surface;
            // 系统多行滚动条是浅色的，深色输入区里很刺眼；关掉（滚轮仍可滚）。
            _input.ScrollBars = ScrollBars.None;
            var inputBox = _input as Widgets.DarkTextBox;
            if (inputBox != null) inputBox.Placeholder = "给 agent 下达指令，或补充目标信息…";
            _input.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    UiSafety.Fire(SendAsync(), "操作");
                }
            };

            _send = Theme.Primary("发送", 92, 44);
            _send.Dock = DockStyle.Right;
            // 同一个按钮两种角色：空闲时发送，运行中变成「停止」。
            // 长任务（多轮工具调用）可能要跑几十秒，没有中断就只能干等或强杀进程。
            _send.Click += (s, e) =>
            {
                if (_busy)
                {
                    CancelRun();
                }
                else
                {
                    UiSafety.Fire(SendAsync(), "操作");
                }
            };

            var inputRow = new Card
            {
                Dock = DockStyle.Top,
                Height = 60,
                Radius = 12,
                ShadowAlpha = 40,
                ShadowSpread = 5,
                Padding = new Padding(10, 8, 10, 8),
            };
            inputRow.Controls.Add(_input);
            inputRow.Controls.Add(_send);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Ctrl+Enter 发送",
            };

            bottom.Controls.Add(inputRow);
            bottom.Controls.Add(_hint);

            // ---- 标签容器（深色自绘，见 Widgets.DarkTabs）----
            var tabs = new Widgets.DarkTabs
            {
                Dock = DockStyle.Fill,
                // TabControl.Padding 是标签格内边距，不被 Control.Scale 缩放
                Padding = new Point(14, 5),   // 设计值：TabControl.Padding 随 ScaleTree 缩
            };

            var chatTab = new TabPage("会话") { BackColor = Theme.Bg, Padding = new Padding(0) };
            chatTab.Controls.Add(_flow);
            chatTab.Controls.Add(bottom);
            tabs.TabPages.Add(chatTab);

            var evTab = new TabPage("事件") { BackColor = Theme.Bg, Padding = new Padding(0) };
            evTab.Controls.Add(SessionTabs.BuildEvents(_store, _sessionId));
            tabs.TabPages.Add(evTab);

            // 拓扑：主 Agent 派出去的子任务链。
            // 放在「事件」后面 —— 事件是「工具做了什么」，拓扑是「谁做了哪一块」，
            // 先看动作再看分工，顺序上顺。
            var topoTab = new TabPage("拓扑") { BackColor = Theme.Bg, Padding = new Padding(0) };
            topoTab.Controls.Add(SessionTabs.BuildTopology(_store, _sessionId));
            tabs.TabPages.Add(topoTab);

            // 文件：这个会话里 file 工具动过哪些文件/目录。
            // 放在拓扑后面 —— 拓扑是「谁做了哪块」，文件是「具体碰了哪些东西」。
            var fileTab = new TabPage("文件") { BackColor = Theme.Bg, Padding = new Padding(0) };
            fileTab.Controls.Add(SessionTabs.BuildFiles(_store, _sessionId));
            tabs.TabPages.Add(fileTab);

            var flagTab = new TabPage("Flag") { BackColor = Theme.Bg, Padding = new Padding(0) };
            flagTab.Controls.Add(SessionTabs.BuildFlags(_store, _sessionId));
            tabs.TabPages.Add(flagTab);

            var expTab = new TabPage("经验") { BackColor = Theme.Bg, Padding = new Padding(0) };
            expTab.Controls.Add(SessionTabs.BuildExperience(_store, _sessionId));
            tabs.TabPages.Add(expTab);

            // 切到某个标签时刷新它的数据（各标签自己带一个 Action）
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedTab == null) return;
                foreach (Control c in tabs.SelectedTab.Controls)
                {
                    var refresh = c.Tag as Action;
                    if (refresh != null) { refresh(); break; }
                }
            };

            _tabs = tabs;

            // Dock 顺序：_sidePanel（Right）要在 tabs（Fill）之前加，
            // 这样 tabs 的 Fill 会自动给它留出右边 310px。
            if (_sidePanel != null) Controls.Add(_sidePanel);
            Controls.Add(tabs);
            Controls.Add(head);

            UpdateMeta();
        }

        private void UpdateMeta()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null || sf.Session == null) return;
            var s = sf.Session;
            _meta.Text = string.Format("{0} · {1} · {2} 条消息 · {3} tokens",
                Core.RunModeText.Label(s.Mode),
                Core.PhaseText.Label(s.Phase),
                sf.Messages.Count,
                Theme.Tokens(s.Tokens));
        }

        // ---------- 渲染历史 ----------

        /// <summary>
        /// 首屏最多画这么多条气泡。
        ///
        /// FlowLayoutPanel 每加一个子控件都会重新排一遍布局，所以总耗时是
        /// **O(n²)** 的。实测：100 条 0.6 秒，500 条 1.5 秒，1000 条 **8.3 秒**，
        /// 2000 条 **48 秒** —— 用户打开一个跑久了的会话会以为程序卡死。
        ///
        /// 这里只画最近 150 条。更早的内容仍然在会话文件里，
        /// 需要时点「加载更早」补齐。
        /// </summary>
        private const int InitialBubbles = 150;

        /// <summary>当前已经画了多少条（用于「加载更早」）。</summary>
        private int _rendered = 0;

        private void LoadHistory()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null)
            {
                // 会话文件没了（被删/手改 id）：给一句能看懂的空态，而不是一片黑
                AddBubble("system", "找不到会话「" + _sessionId + "」。它可能已被删除，返回任务页新建一个吧。");
                return;
            }
            _flow.SuspendLayout();
            _flow.Controls.Clear();
            _rendered = 0;

            // 从末尾往前取 InitialBubbles 条
            int from = Math.Max(0, sf.Messages.Count - InitialBubbles);
            for (int i = from; i < sf.Messages.Count; i++)
            {
                AddBubble(sf.Messages[i].Role, sf.Messages[i].Content);
                _rendered++;
            }

            // 刚建好还没跑过的会话：中间不该是一片黑，给一句下一步指引
            if (_rendered == 0)
                AddBubble("system", "会话已创建，还没有消息。在下方输入第一条指令或补充目标信息，"
                    + "Ctrl+Enter 发送后 agent 开始执行。");

            _flow.ResumeLayout();

            // 被省略的部分给个提示（放在最上面）
            if (from > 0)
            {
                var more = Theme.Ghost("↑ 更早的 " + from + " 条已折叠（点这里加载 50 条）", Theme.K(300), Theme.K(30));
                more.Margin = new Padding(0, 0, 0, Theme.K(4));
                more.Click += (s2, e2) => LoadOlder();
                _flow.Controls.Add(more);
                _flow.Controls.SetChildIndex(more, 0);
            }

            ScrollToEnd();
            UpdateMeta();
            RefreshTabs();
        }

        /// <summary>往上补一批更早的消息。</summary>
        private void LoadOlder()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null) return;

            int shown = Math.Min(_rendered, sf.Messages.Count);
            int fromOld = Math.Max(0, sf.Messages.Count - shown);   // 当前最上面那条的位置
            int take = Math.Min(50, fromOld);
            if (take <= 0) return;

            _flow.SuspendLayout();
            // 把「加载更早」那个按钮先摘掉
            Control moreBtn = null;
            foreach (Control c in _flow.Controls)
            {
                if (c.GetType() == typeof(Button)) { moreBtn = c; break; }
            }
            if (moreBtn != null) _flow.Controls.Remove(moreBtn);

            // 把这一批插到最前面（先加到末尾再逐个前移太慢，直接倒序插入）
            int start = fromOld - take;
            for (int i = fromOld - 1; i >= start; i--)
            {
                AddBubble(sf.Messages[i].Role, sf.Messages[i].Content);
                _rendered++;
                // 移到最前
                _flow.Controls.SetChildIndex(_flow.Controls[_flow.Controls.Count - 1], 0);
            }

            int stillLeft = start;
            if (stillLeft > 0)
            {
                var more = Theme.Ghost("↑ 更早的 " + stillLeft + " 条已折叠（点这里加载 50 条）", Theme.K(300), Theme.K(30));
                more.Margin = new Padding(0, 0, 0, Theme.K(4));
                more.Click += (s2, e2) => LoadOlder();
                _flow.Controls.Add(more);
                _flow.Controls.SetChildIndex(more, 0);
            }
            _flow.ResumeLayout();
        }

        /// <summary>
        /// 重新生成：把最后一条用户消息之后的内容全部丢掉，重发。
        ///
        /// 为什么是"重发上一条"而不是"重试"：模型答得不好时，用户想要的
        /// 通常是同一个问题的另一个答案，而不是让它在错答案上继续改。
        /// </summary>
        private void Regenerate()
        {
            if (_busy)
            {
                _hint.Text = "正在运行，先停止再重新生成";
                _hint.ForeColor = Theme.Warn;
                return;
            }

            var sf = _store.Load(_sessionId);
            if (sf == null || sf.Messages.Count == 0) return;

            // 从后往前找最后一条 user 消息
            int lastUser = -1;
            for (int i = sf.Messages.Count - 1; i >= 0; i--)
            {
                if (sf.Messages[i].Role == "user") { lastUser = i; break; }
            }
            if (lastUser < 0)
            {
                _hint.Text = "这个会话还没有用户消息";
                _hint.ForeColor = Theme.Muted;
                return;
            }

            string text = sf.Messages[lastUser].Content;
            if (!UiSafety.Confirm(ShellForm(), "重新生成会丢掉最后一条提问之后的全部内容（含工具记录）。\n\n"
                    + "提问：「" + Theme.OneLine(text, 60) + "」\n\n继续？", "重新生成")) return;

            // 截断到那条提问（含）
            if (lastUser + 1 < sf.Messages.Count)
                sf.Messages.RemoveRange(lastUser + 1, sf.Messages.Count - lastUser - 1);
            _store.Save(sf);

            // 重画
            LoadHistory();
            _input.Text = text;
            _input.Focus();
            _hint.Text = "已截断到上一条提问，按发送或 Ctrl+Enter 重跑";
            _hint.ForeColor = Theme.Ok;
        }

        /// <summary>双击标题改名。</summary>
        private void RenameSession()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null) return;

            string current = string.IsNullOrEmpty(sf.Session.Title)
                ? sf.Session.Target : sf.Session.Title;

            using (var dlg = new InputDialog("重命名会话", "新的标题", current))
            {
                if (dlg.ShowDialog(ShellForm()) != DialogResult.OK) return;
                string name = (dlg.Value ?? "").Trim();
                if (name.Length == 0) return;
                sf.Session.Title = name;
                _store.Save(sf);
                _titleLabel.Text = name;
                UpdateMeta();
                _hint.Text = "已改名";
                _hint.ForeColor = Theme.Ok;
            }
        }

        /// <summary>
        /// 把整个会话的对话复制到剪贴板。
        /// 气泡是自绘的选不中，所以给一个"一次全拿走"的入口。
        /// </summary>
        private void CopyAllText()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (var m in sf.Messages)
            {
                string who = m.Role == "user" ? "我"
                    : (m.Role == "assistant" ? "灵枢"
                    : (m.Role == "tool" ? "[" + m.Name + "]" : "·"));
                sb.Append(who).Append("：").Append(m.Content).Append("\r\n\r\n");
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                _hint.Text = "已复制 " + sf.Messages.Count + " 条消息到剪贴板";
                _hint.ForeColor = Theme.Ok;
            }
            catch (Exception ex)
            {
                _hint.Text = "复制失败：" + Theme.OneLine(ex.Message, 80);
                _hint.ForeColor = Theme.Danger;
            }
        }

        /// <summary>切换发送按钮的两种状态。</summary>
        private void SetSendButton(bool running)
        {
            if (_send == null || _send.IsDisposed) return;
            _send.Enabled = true;                 // 运行中也要能点（那是"停止"）
            _send.Text = running ? "■ 停止" : "发送";
            var flat = _send as Widgets.FlatButton;
            if (flat != null)
            {
                flat.Kind = running ? Widgets.ButtonKind.Danger : Widgets.ButtonKind.Primary;
                flat.Invalidate();
            }
            else
            {
                _send.ForeColor = running ? Theme.Danger : Color.White;
                _send.BackColor = running ? Theme.Surface : Theme.Accent;
            }
        }

        /// <summary>用户点了停止。</summary>
        private void CancelRun()
        {
            try
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    _hint.Text = "正在停止…";
                    _hint.ForeColor = Theme.Warn;
                }
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("取消失败: " + ex.Message);
            }
        }

        /// <summary>刷新所有标签的数据（发完消息后调，事件/Flag/经验都会变）。</summary>
        private void RefreshTabs()
        {
            if (_tabs == null || _tabs.IsDisposed) return;
            foreach (TabPage tab in _tabs.TabPages)
            {
                foreach (Control c in tab.Controls)
                {
                    var refresh = c.Tag as Action;
                    if (refresh != null) { refresh(); break; }
                }
            }
        }

        private void AddBubble(string role, string content)
        {
            BubbleRole r;
            switch (role)
            {
                case "user": r = BubbleRole.User; break;
                case "assistant": r = BubbleRole.Assistant; break;
                default: r = BubbleRole.System; break;
            }

            int maxWidth = Math.Max(Theme.K(360), _flow.Width - Theme.K(60));
            var bubble = new ChatBubble(r, content, (int)(maxWidth * 0.78));

            // 用户靠右、助手靠左
            var holder = new Panel
            {
                Width = Math.Max(Theme.K(200), _flow.ClientSize.Width - Theme.K(26)),
                Height = bubble.Height + Theme.K(8),
                BackColor = Theme.Bg,
                Margin = new Padding(0, 0, 0, 0),
            };
            holder.Controls.Add(bubble);
            bubble.Location = r == BubbleRole.User
                ? new Point(Math.Max(0, holder.Width - bubble.Width), Theme.K(4))
                : new Point(0, Theme.K(4));
            holder.Resize += (s, e) =>
            {
                bubble.Location = r == BubbleRole.User
                    ? new Point(Math.Max(0, holder.Width - bubble.Width), Theme.K(4))
                    : new Point(0, Theme.K(4));
            };

            _flow.Controls.Add(holder);
        }

        private void ScrollToEnd()
        {
            if (_flow.Controls.Count == 0) return;
            _flow.ScrollControlIntoView(_flow.Controls[_flow.Controls.Count - 1]);
        }

        // ---------- 发送 ----------

        /// <summary>
        /// 打开子 agent 旁路对话区（右侧栏），显示指定子会话的消息。
        ///
        /// 子 agent 的会话在 `sessionId + "-sub-" + name` 里（Orchestrator 建的），
        /// 旁路栏读它 —— 不动主对话区。
        /// </summary>
        private void OpenSideChat(string subId, string agentName)
        {
            if (_sidePanel == null || _sidePanel.IsDisposed) return;
            _sideSubId = subId;
            _sideTitle.Text = "⇢ " + agentName;
            _sidePanel.Visible = true;
            LoadSideHistory();
        }

        /// <summary>读子 agent 会话的消息，画进旁路区。</summary>
        private void LoadSideHistory()
        {
            if (_sideFlow == null || _sideFlow.IsDisposed || string.IsNullOrEmpty(_sideSubId)) return;
            _sideFlow.SuspendLayout();
            _sideFlow.Controls.Clear();
            var sf = _store.Load(_sideSubId);
            if (sf != null && sf.Messages != null)
            {
                foreach (var m in sf.Messages)
                {
                    var role = m.Role == "user" ? BubbleRole.User
                             : m.Role == "assistant" ? BubbleRole.Assistant
                             : BubbleRole.System;
                    var b = new ChatBubble(role, m.Content ?? "", Theme.K(270));
                    var holder = new Panel
                    {
                        Width = Theme.K(290),
                        Height = b.Height + Theme.K(6),
                        BackColor = Theme.Bg,
                    };
                    b.Dock = DockStyle.Top;
                    holder.Controls.Add(b);
                    _sideFlow.Controls.Add(holder);
                }
            }
            _sideFlow.ResumeLayout();
            _sideFlow.ScrollControlIntoView(_sideFlow.Controls.Count > 0
                ? _sideFlow.Controls[_sideFlow.Controls.Count - 1] : _sideFlow);
        }

        /// <summary>给子 agent 会话发一条消息（追加到它的消息列表）。</summary>
        private async System.Threading.Tasks.Task SendSideAsync()
        {
            if (string.IsNullOrEmpty(_sideSubId) || _sideInput == null) return;
            var text = (_sideInput.Text ?? "").Trim();
            if (text.Length == 0) return;

            _sideInput.Text = "";
            _sideSend.Enabled = false;
            try
            {
                // 子 agent 的消息走它的会话存储，不走主对话
                _store.Append(_sideSubId, "user", text, "");
                LoadSideHistory();

                // 子 agent 的回复暂时没法真调（那是 Orchestrator 内部的事），
                // 但消息已经写进它的会话 —— 下次 Orchestrator 派给它时能看到。
                // 这就是「旁路对话」的本质：给用户一个直接对子 agent 说话的口。
                AddSideSystem("已发送给「" + (_sideTitle.Text ?? "") + "」。它的回复会出现在这里。");
            }
            catch (Exception ex)
            {
                AddSideSystem("发送失败：" + ex.Message);
            }
            finally { _sideSend.Enabled = true; }
            await System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>往旁路区加一行系统提示（不是气泡）。</summary>
        private void AddSideSystem(string text)
        {
            if (_sideFlow == null || _sideFlow.IsDisposed) return;
            var lbl = new Label
            {
                Text = text,
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                AutoSize = false,
                Width = Theme.K(280),
                Height = Theme.K(36),
                BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _sideFlow.Controls.Add(lbl);
            _sideFlow.ScrollControlIntoView(lbl);
        }

        private async Task SendAsync()
        {
            if (_busy) return;
            string text = (_input.Text ?? "").Trim();
            if (text.Length == 0) return;

            string modelName = _modelPicker.SelectedItem == null
                ? "" : _modelPicker.SelectedItem.ToString();
            if (string.IsNullOrEmpty(modelName))
            {
                MessageBox.Show(ShellForm(), "还没有可用模型。\n\n请先到「模型」页添加供应商和模型。",
                    "灵枢", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _busy = true;
            SetSendButton(true);
            _input.Text = "";
            _hint.Text = "正在等待模型回复…";
            _hint.ForeColor = Theme.Accent;

            // 先把用户消息画出来（写盘由 AgentLoop 做）
            AddBubble("user", text);
            MarkExecuting();
            ScrollToEnd();

            _cts = new CancellationTokenSource();
            try
            {
                // 用编排器：主 Agent 可以 delegate 给方向 agent
                var agents = new Agents.AgentRepository(
                    System.IO.Path.Combine(Core.AppPaths.DataRoot, "agents.json"));
                var sf0 = _store.Load(_sessionId);
                string mode = sf0 != null && sf0.Session != null
                    ? Core.RunModeText.ToValue(sf0.Session.Mode) : "ctf";

                var orch = new Session.Orchestrator(_store, _registry, _tools, agents, modelName);
                orch.Experience = new Knowledge.ExperienceStore(Core.AppPaths.ExperienceFile);
                // 召回条数按设置来（0 = 关闭召回）
                try
                {
                    var pref = new Core.ConfigManager();
                    pref.Load();
                    orch.RecallLimit = pref.Current.RecallLimit;
                }
                catch { }
                orch.SubAgentStarted += (name, task) =>
                {
                    AddBubble("system", "⇢ 派给「" + name + "」：" + Theme.OneLine(task, 110));
                    ScrollToEnd();
                    // 打开子 agent 旁路对话栏
                    OpenSideChat(_sessionId + "-sub-" + name, name);
                };
                orch.SubAgentFinished += (name, ok, summary) =>
                {
                    AddBubble("system", (ok ? "✓ " : "✗ ") + name + " 交回：" + summary);
                    ScrollToEnd();
                };

                var loop = orch.BuildMasterLoop(_sessionId, mode);
                int toolCount = 0;
                loop.ToolStarted += ev =>
                {
                    toolCount++;
                    // delegate 的显示交给 SubAgentStarted，这里不重复
                    if (ev.Name != "delegate")
                    {
                        AddBubble("system", "▸ 执行 " + ev.Name + "：" + Theme.OneLine(ev.Arguments, 120));
                        ScrollToEnd();
                    }
                };
                loop.ToolFinished += ev =>
                {
                    if (ev.Name != "delegate")
                    {
                        AddBubble("system", (ev.Ok ? "✓ " : "✗ ") + ev.Summary);
                        ScrollToEnd();
                    }
                };

                var result = await loop.SendAsync(_sessionId, text, _cts.Token).ConfigureAwait(true);

                if (result.Ok)
                {
                    AddBubble("assistant", result.Reply);
                    _hint.Text = "完成 · " + result.Tokens + " tokens"
                        + (result.ToolCalls > 0 ? " · " + result.ToolCalls + " 次工具调用" : "");
                    _hint.ForeColor = Theme.Muted;
                }
                else
                {
                    AddBubble("system", "失败：" + result.Error);
                    _hint.Text = "失败 —— 详见上方消息";
                    _hint.ForeColor = Theme.Danger;
                }
                ScrollToEnd();
                UpdateMeta();
                RefreshTabs();
            }
            catch (OperationCanceledException)
            {
                // 中断不只是「显示已取消」——会话状态也得跟着变，
                // 否则列表页还写着「执行中」，下次进来会以为它还在跑。
                AddBubble("system", "已取消");
                _hint.Text = "已取消";
                _hint.ForeColor = Theme.Muted;
                try
                {
                    var sf2 = _store.Load(_sessionId);
                    if (sf2 != null && sf2.Session != null)
                    {
                        sf2.Session.Phase = Core.SessionPhase.Stopped;
                        _store.Save(sf2);
                        UpdateMeta();
                    }
                }
                catch (Exception ex)
                {
                    Core.ConfigManager.Log("标记停止状态失败: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                AddBubble("system", "异常：" + ex.Message);
                _hint.Text = "异常：" + Theme.OneLine(ex.Message, 100);
                _hint.ForeColor = Theme.Danger;
            }
            finally
            {
                _busy = false;
                SetSendButton(false);
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                _input.Focus();
            }
        }

        /// <summary>首次发消息时把会话标成执行中，任务列表能看到状态变化。</summary>
        private void MarkExecuting()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null || sf.Session == null) return;
            if (sf.Session.Phase == Core.SessionPhase.Pending)
            {
                sf.Session.Phase = Core.SessionPhase.Executing;
                _store.Save(sf);
                UpdateMeta();
            }
        }

        /// <summary>导出为 Markdown。</summary>
        private void ExportMarkdown()
        {
            var sf = _store.Load(_sessionId);
            if (sf == null)
            {
                MessageBox.Show(ShellForm(), "会话不存在。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Markdown 文件|*.md|所有文件|*.*";
                dlg.FileName = Session.SessionExporter.SuggestFileName(sf);
                if (dlg.ShowDialog(ShellForm()) != DialogResult.OK) return;
                try
                {
                    Session.SessionExporter.ExportToFile(sf, dlg.FileName);
                    var flags = Session.SessionExporter.ExtractFlags(sf);
                    _hint.Text = "已导出到 " + dlg.FileName
                        + (flags.Count > 0 ? "（含 " + flags.Count + " 个 flag）" : "");
                    _hint.ForeColor = Theme.Ok;
                }
                catch (Exception ex)
                {
                    _hint.Text = "导出失败：" + Theme.OneLine(ex.Message, 100);
                    _hint.ForeColor = Theme.Danger;
                }
            }
        }

        /// <summary>释放 MCP 长连接（子进程）。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _mcpConns != null)
            {
                foreach (var c in _mcpConns)
                {
                    try { c.Dispose(); } catch { }
                }
                _mcpConns = null;
            }
            base.Dispose(disposing);
        }

        private Form ShellForm()
        {
            Control c = this;
            while (c != null)
            {
                var f = c as Form;
                if (f != null) return f;
                c = c.Parent;
            }
            return null;
        }
    }

    /// <summary>
    /// 消息流容器：重排子控件宽度、支持滚轮。
    /// FlowLayoutPanel 在 WrapContents=false + TopDown 时会把子控件宽度锁死，
    /// 这里重写让它跟随可用宽度。
    /// </summary>
    public sealed class RichFlowPanel : FlowLayoutPanel
    {
        public RichFlowPanel()
        {
            DoubleBuffered = true;
            Resize += (s, e) => Relayout();
        }

        private void Relayout()
        {
            int w = Math.Max(Theme.K(200), ClientSize.Width - Padding.Horizontal - Theme.K(18));
            SuspendLayout();
            foreach (Control c in Controls)
            {
                c.Width = w;
            }
            ResumeLayout();
        }
    }
}
