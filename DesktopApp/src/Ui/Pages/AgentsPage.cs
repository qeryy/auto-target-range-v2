// 灵枢桌面版 —— 页面：编队
//
// 列出所有 agent，给每个指派模型。指派存 agents.json，只存「agentKey → 模型名」，
// 角色定义仍在代码里（AgentLibrary），这样升级 exe 不会冲掉用户的指派。
//
// 双击一行改模型；也可以选中后点「指派模型」。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Agents;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class AgentsPage : IPage
    {
        public string Id { get { return "agents"; } }
        public string Title { get { return "编队"; } }
        public PageGroup Group { get { return PageGroup.Agent; } }

        // 卡片网格（替代原来的表格+详情分割）
        private FlowLayoutPanel _cards;
        private readonly Dictionary<Control, int> _cardIndex = new Dictionary<Control, int>();
        private int _selectedIndex = -1;
        private TextBox _detail;
        private Label _hint;
        private AgentRepository _repo;
        private List<AgentProfile> _roster;

        private string FilePath
        {
            get { return System.IO.Path.Combine(Core.AppPaths.DataRoot, "agents.json"); }
        }

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var assign = Theme.Primary("指派模型", 104);
            assign.Click += (s, e) => AssignSelected();

            var refresh = Theme.Ghost("刷新", 72);
            // 走 UiSafety.Fire：RefreshAsync 内部没有 try/catch，
            // 直接 await 的话异常会落到全局兜底，弹出「程序遇到了没预料到的错误」
            // —— 而实际可能只是一次网络失败。
            refresh.Click += (s, e) => UiSafety.Fire(RefreshAsync(), "刷新编队");

            var head = Theme.PageHeader("编队",
                "每个方向一个 agent；点卡片看详情，双击指派模型",
                assign, refresh);

            // ===== 卡片网格（对齐 web 版的 agents-grid）=====
            _cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                AutoScroll = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 4),
            };

            _detail = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = Theme.FontBody,
                Dock = DockStyle.Fill,
                WordWrap = true,
            };

            // 详情区：一张板块卡放在网格下面
            var detailCard = new SectionCard("agent 详情", "什么时候用 · 可用工具 · 系统提示词",
                Theme.Violet, "✦")
            {
                Dock = DockStyle.Bottom,
                Height = 168,
            };
            detailCard.Body.Controls.Add(_detail);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            body.Controls.Add(_cards);
            body.Controls.Add(detailCard);
            body.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(body);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }

        private AgentRepository Repo()
        {
            if (_repo == null) _repo = new AgentRepository(FilePath);
            return _repo;
        }

        public async Task RefreshAsync()
        {
            if (_cards == null || _cards.IsDisposed) return;

            _roster = Repo().WithAssignments();
            var defaultModel = DefaultModelName();

            _cards.SuspendLayout();
            try
            {
                // 清掉旧卡片（保留 detailCard 之外的所有控件）
                _cards.Controls.Clear();
                _cardIndex.Clear();

                int idx = 0;
                foreach (var a in _roster)
                {
                    var card = BuildAgentCard(a, idx, defaultModel);
                    // 卡片在刷新时（缩放之后）重建，子树按设计值补一次缩放
                    Theme.ScaleTree(card);
                    _cards.Controls.Add(card);
                    _cardIndex[card] = idx;
                    idx++;
                }
            }
            finally { _cards.ResumeLayout(); }

            _selectedIndex = _roster.Count > 0 ? 0 : -1;
            HighlightCard();
            ShowDetail();

            _hint.Text = _roster.Count + " 个 agent · 点卡片看详情 · 双击指派模型 · 指派存在 "
                + Core.AppPaths.DataRoot;
            await Task.CompletedTask;
        }

        /// <summary>
        /// 一张 agent 卡：色晕软面 + 首字头像块 + 代号/Key + 方向两行 + 底部小字。
        /// 262×150，够放三行正文又不至于让网格散开。
        /// </summary>
        private Card BuildAgentCard(Agents.AgentProfile a, int index, string defaultModel)
        {
            Color tint = Theme.AgentColor(index);
            var card = new Card
            {
                Size = new Size(262, 150),
                Margin = new Padding(0, 0, 14, 14),
                Tint = tint,
                TintHeight = 56,
                Cursor = Cursors.Hand,
                Tag = index,
            };

            // 首字头像块：分类色软底 + 同色字，比一个 8px 色点更有「身份」
            var avatar = new HueTile
            {
                Hue = tint,
                Glyph = string.IsNullOrEmpty(a.Name) ? "◈" : a.Name.Substring(0, 1),
                Size = new Size(30, 30),
                Location = new Point(14, 12),
            };

            var name = new Label
            {
                Text = a.Name,
                Location = new Point(52, 12),
                AutoSize = true,
                Font = Theme.FontHeading,
                ForeColor = tint,
                BackColor = Color.Transparent,
            };
            var key = new Label
            {
                Text = a.Key,
                Location = new Point(0, 16),
                AutoSize = true,
                Font = Theme.FontTiny,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
            };
            // key 靠右对齐：等卡片尺寸定下来再摆
            key.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var dir = new Label
            {
                Text = a.Direction,
                Location = new Point(14, 50),
                Size = new Size(234, 62),
                Font = Theme.FontSmall,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
            };

            string model = string.IsNullOrEmpty(a.Model)
                ? (string.IsNullOrEmpty(defaultModel) ? "未指派" : "默认 · " + defaultModel)
                : a.Model;
            var foot = new Label
            {
                Text = ModesText(a.Modes) + " · " + model,
                Location = new Point(14, 116),
                Size = new Size(234, 20),
                Font = Theme.FontTiny,
                ForeColor = string.IsNullOrEmpty(a.Model) && string.IsNullOrEmpty(defaultModel)
                    ? Theme.Warn : Theme.Muted,
                BackColor = Color.Transparent,
            };

            card.Controls.Add(foot);
            card.Controls.Add(dir);
            card.Controls.Add(key);
            card.Controls.Add(name);
            card.Controls.Add(avatar);

            // 布局落定后把 key 挪到右上
            card.Layout += (s, e) =>
            {
                if (Theme.IsScaling) return;
                key.Left = Math.Max(Theme.K(60), card.Width - key.Width - Theme.K(14));
            };

            EventHandler click = delegate
            {
                _selectedIndex = index;
                HighlightCard();
                ShowDetail();
            };
            card.Click += click;
            // 子控件也要转发，否则点文字不触发
            foreach (Control c in card.Controls) c.Click += click;

            card.DoubleClick += delegate { _selectedIndex = index; AssignSelected(); };
            foreach (Control c in card.Controls)
            {
                c.DoubleClick += delegate { _selectedIndex = index; AssignSelected(); };
            }
            return card;
        }

        /// <summary>选中的卡片加一圈强调色描边，其余保持无描边软面。</summary>
        private void HighlightCard()
        {
            if (_cards == null) return;
            foreach (Control c in _cards.Controls)
            {
                var card = c as Card;
                if (card == null) continue;
                int i;
                if (!_cardIndex.TryGetValue(card, out i)) continue;
                bool on = (i == _selectedIndex);
                card.BorderColor = Theme.Accent;
                card.BorderWidth = on ? 2 : 0;
                card.Invalidate();
            }
        }

        private string DefaultModelName()
        {
            try
            {
                var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                var models = reg.Models();
                reg.Dispose();
                foreach (var m in models)
                {
                    if (m.Enabled) return m.Name;
                }
                return models.Count > 0 ? models[0].Name : "";
            }
            catch { return ""; }
        }

        private static string ModesText(string[] modes)
        {
            if (modes == null || modes.Length == 0) return "全部";
            var parts = new List<string>();
            foreach (var m in modes)
            {
                if (m == "all") return "全部";
                if (m == "src") parts.Add("SRC");
                else if (m == "ctf") parts.Add("CTF");
                else if (m == "competition") parts.Add("竞赛");
                else parts.Add(m);
            }
            return string.Join("/", parts);
        }

        private void ShowDetail()
        {
            if (_detail == null || _detail.IsDisposed) return;
            if (_roster == null) _roster = Repo().WithAssignments();

            int idx = _selectedIndex;
            if (idx < 0 || idx >= _roster.Count) { _detail.Text = ""; return; }
            var a = _roster[idx];

            _detail.Text =
                a.Name + "（" + a.Key + "）\n"
                + a.Direction + "\n\n"
                + "【什么时候用】\n" + a.WhenToUse + "\n\n"
                + "【可用工具】\n"
                + (a.Tools == null || a.Tools.Length == 0 ? "全部" : string.Join(", ", a.Tools)) + "\n\n"
                + "【系统提示词】\n" + a.SystemPrompt;
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
        }

        private void AssignSelected()
        {
            if (_roster == null) _roster = Repo().WithAssignments();
            int idx = _selectedIndex;
            if (idx < 0 || idx >= _roster.Count)
            {
                MessageBox.Show(FindShell(), "先点一张卡片选中 agent。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var agent = _roster[idx];

            var models = new List<string>();
            try
            {
                var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                foreach (var m in reg.Models()) { if (m.Enabled) models.Add(m.Name); }
                reg.Dispose();
            }
            catch { }

            if (models.Count == 0)
            {
                MessageBox.Show(FindShell(),
                    "还没有可用模型。\n\n先去「模型」页添加供应商和模型。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new AssignDialog(agent.Name, models.ToArray(), agent.Model))
            {
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                Repo().SetModel(agent.Key, dlg.Selected);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private Form FindShell()
        {
            Control c = _cards;
            while (c != null)
            {
                var f = c as Form;
                if (f != null) return f;
                c = c.Parent;
            }
            return null;
        }
    }

    /// <summary>指派模型的小对话框。</summary>
    public sealed class AssignDialog : Form
    {
        public string Selected { get; private set; }

        private readonly ComboBox _combo;

        public AssignDialog(string agentName, string[] models, string current)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            Text = "给「" + agentName + "」指派模型";
            ClientSize = new Size(460, 170);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            Controls.Add(new Label
            {
                Text = "选择这个方向使用的模型",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(20, 18),
            });

            var options = new List<string> { "（用全局默认）" };
            options.AddRange(models);
            _combo = Theme.Select(options.ToArray());
            _combo.Location = new Point(20, 44);
            _combo.Width = ClientSize.Width - 40;
            if (!string.IsNullOrEmpty(current))
            {
                int i = Array.IndexOf(models, current);
                if (i >= 0) _combo.SelectedIndex = i + 1;
            }
            Controls.Add(_combo);

            var ok = Theme.Primary("确定", 92, 34);
            ok.Location = new Point(ClientSize.Width - 116, ClientSize.Height - 48);
            ok.Click += (s, e) =>
            {
                int idx = _combo.SelectedIndex;
                Selected = idx <= 0 ? "" : _combo.SelectedItem.ToString();
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 34);
            cancel.Location = new Point(ClientSize.Width - 208, ClientSize.Height - 48);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
