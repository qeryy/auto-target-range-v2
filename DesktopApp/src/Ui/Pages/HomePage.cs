// 灵枢桌面版 —— 页面：任务（首页 hero composer）
//
// 对齐 web 版首页（p2）：eyebrow「新建任务」+ 渐变大标题「下达任务，剩下的交给灵枢」
// + 三张模式卡（SRC/CTF/竞赛）+ 任务描述 composer（附件/项目/开始执行）
// + 示例 chips + 授权提示。会话列表移到侧栏「会话」区（见 MainForm）。
//
// 这里不再是表格：首页是「下达任务」的入口，历史会话在侧栏点开。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class HomePage : IPage
    {
        public string Id { get { return "home"; } }
        public string Title { get { return "任务"; } }
        public PageGroup Group { get { return PageGroup.Main; } }

        private Panel _root;
        private Panel _head;
        private HeroTitle _hero;
        private ModeCard[] _cards;
        private Card _composer;
        private TextBox _mission;
        private Panel _divider;
        private Widgets.DarkSelect _project;
        private FlatButton _go;
        private Label _notice;
        private Label _intro;
        private Label _exLabel;
        private FlatButton[] _chips;
        private int _dy;

        private Core.RunMode _mode = Core.RunMode.Src;
        private List<ProjectItem> _projects = new List<ProjectItem>();

        private string ProjectFilePath
        {
            get { return Path.Combine(Core.AppPaths.DataRoot, "projects.json"); }
        }

        public Control CreateView()
        {
            // 氛围底：四周的留白不再是死黑（见 Theme.AmbientPanel）
            _root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
            };

            // eyebrow：复用 PageHeader 工厂，再收成小字亮蓝（对齐 web .eyebrow）
            _head = Theme.PageHeader("新建任务", null);
            _head.Dock = DockStyle.None;
            _head.Height = 24;
            _head.BackColor = Color.Transparent;
            var eyebrow = _head.Controls[0] as Label;
            if (eyebrow != null)
            {
                eyebrow.Font = Theme.FontSmall;
                eyebrow.ForeColor = Theme.AccentInk;
                eyebrow.Location = new Point(0, 2);
            }
            _root.Controls.Add(_head);

            _hero = new HeroTitle { Width = 860 };
            _root.Controls.Add(_hero);

            _intro = new Label
            {
                Text = "选好模式，写下目标。从这里开始。",
                Font = Theme.FontSmall,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
            };
            _root.Controls.Add(_intro);

            // ---- 三张模式卡 ----
            _cards = new ModeCard[3];
            _cards[0] = MakeCard(ModeKind.Src, "SRC 渗透测试", "网站、资产与业务安全", Core.RunMode.Src, 0);
            _cards[1] = MakeCard(ModeKind.Ctf, "CTF 解题", "题目入口与附件分析", Core.RunMode.Ctf, 1);
            _cards[2] = MakeCard(ModeKind.Competition, "智能体竞赛", "按赛方规则连续执行", Core.RunMode.Competition, 2);
            foreach (var c in _cards) _root.Controls.Add(c);
            _cards[0].Selected = true;

            // ---- composer：单卡无缝（输入框不再自带一圈边框）----
            _composer = new Card
            {
                Width = 860,
                Height = 214,
                Radius = 14,
                BorderColor = Theme.LineDark,
                BorderWidth = 0,
                ShadowAlpha = 60,
                ShadowSpread = 8,
            };

            var cLabel = new Label
            {
                Text = "任务描述",
                Font = Theme.FontSmall,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(18, 14),
            };
            _composer.Controls.Add(cLabel);
            _composer.Controls.Add(new Label
            {
                Text = "支持粘贴网址、题目描述或测试要求",
                Font = Theme.FontTiny,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                // 留足余量：字体回退变宽时也不裁（clip_check 按保守字体量）
                AutoSize = false,
                Width = 300,
                Height = 18,
                Location = new Point(100, 16),
            });

            _mission = Theme.Input(true, 104);
            _mission.Location = new Point(18, 40);
            _mission.Width = 824;
            // 无缝：输入框与卡同底、无自己的边框；滚动条关掉（滚轮仍可滚），
            // 占位提示由 DarkTextBox 自绘。
            _mission.BorderStyle = BorderStyle.None;
            _mission.BackColor = Theme.Surface;
            _mission.ScrollBars = ScrollBars.None;
            var missionBox = _mission as Widgets.DarkTextBox;
            if (missionBox != null) missionBox.Placeholder = "写下目标地址、题目描述或测试要求…";
            // 聚焦时整卡描边提亮，代替输入框自己那圈边框
            _mission.Enter += (s, e) =>
            {
                _composer.BorderColor = Theme.Mix(Theme.Accent, Theme.LineDark, 0.45);
                _composer.BorderWidth = 1;
                _composer.Invalidate();
            };
            _mission.Leave += (s, e) =>
            {
                _composer.BorderWidth = 0;
                _composer.Invalidate();
            };
            _composer.Controls.Add(_mission);

            _divider = new Panel { Height = 1, BackColor = Theme.LineDark, Location = new Point(18, 156), Width = 824 };
            _composer.Controls.Add(_divider);

            var attach = new FlatButton
            {
                Text = "📎",
                Kind = ButtonKind.Ghost,
                Size = new Size(38, 30),
                Location = new Point(18, 168),
                Font = Theme.FontBody,
            };
            attach.Click += (s, e) => Attach();
            _composer.Controls.Add(attach);

            _project = new Widgets.DarkSelect
            {
                Location = new Point(64, 168),
                Width = 150,
                Height = 30,
            };
            _project.SetItems(new[] { "未分类" }, "未分类");
            _composer.Controls.Add(_project);

            var addProj = new FlatButton
            {
                Text = "+ 项目",
                Kind = ButtonKind.Ghost,
                Size = new Size(76, 30),
                Location = new Point(222, 168),
                Font = Theme.FontSmall,
            };
            addProj.Click += (s, e) => AddProject();
            _composer.Controls.Add(addProj);

            _go = FlatButtons.Primary("开始执行  →", 138, 36);
            _go.Location = new Point(860 - 156, 164);
            _go.Click += (s, e) => Start();
            _composer.Controls.Add(_go);

            _root.Controls.Add(_composer);

            // ---- 示例 ----
            _exLabel = new Label
            {
                Text = "也可以从示例开始",
                Font = Theme.FontSmall,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
            };
            _root.Controls.Add(_exLabel);

            _chips = new FlatButton[3];
            _chips[0] = MakeChip("✦ 测试一个网站", 0, delegate
            {
                SelectMode(Core.RunMode.Src);
                _mission.Text = "对 https://example.com 做一次完整的渗透测试评估";
            });
            _chips[1] = MakeChip("✦ 分析一道 CTF 题", 1, delegate
            {
                SelectMode(Core.RunMode.Ctf);
                _mission.Text = "解一道 CTF 题目：分析入口与附件，找到 flag";
            });
            _chips[2] = MakeChip("✦ 参加智能体竞赛", 2, delegate
            {
                SelectMode(Core.RunMode.Competition);
                _mission.Text = "在智能体靶场中按赛方规则连续解题拿分";
            });
            foreach (var ch in _chips) _root.Controls.Add(ch);

            // ---- 授权提示 ----
            _notice = new Label
            {
                Text = "🔒 仅用于已获授权的目标和赛事。操作记录保存在审计日志中。",
                Font = Theme.FontTiny,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                AutoSize = false,
                Width = 520,
                Height = 18,
            };
            _root.Controls.Add(_notice);

            _root.Resize += (s, e) => Layout();
            // 不在这里 Layout：此刻树还没被 ScaleTree 缩放，量出来的都是设计单位。
            // 挂载 dock 时的 Resize 会在缩放落定后跑第一次。
            return _root;
        }

        private ModeCard MakeCard(ModeKind kind, string caption, string sub, Core.RunMode mode, int index)
        {
            var card = new ModeCard
            {
                Kind = kind,
                Caption = caption,
                Sub = sub,
                Height = 96,
            };
            card.Click += (s, e) => SelectMode(mode);
            return card;
        }

        private FlatButton MakeChip(string text, int index, Action fill)
        {
            var chip = new FlatButton
            {
                Text = text,
                Kind = ButtonKind.Ghost,
                Size = new Size(150, 30),
                Font = Theme.FontSmall,
            };
            chip.Click += delegate { fill(); };
            return chip;
        }

        private void SelectMode(Core.RunMode mode)
        {
            _mode = mode;
            for (int i = 0; i < _cards.Length; i++)
                _cards[i].Selected = (ModeOf(i) == mode);
        }

        private static Core.RunMode ModeOf(int i)
        {
            if (i == 0) return Core.RunMode.Src;
            if (i == 1) return Core.RunMode.Ctf;
            return Core.RunMode.Competition;
        }

        /// <summary>
        /// 居中列版式（对齐 web 首页的居中 max-width 列）：水平居中、垂直按余量平衡，
        /// 不再左上角堆一块、右边和底下大片死黑。模式卡与 composer 跟着列宽伸缩。
        /// </summary>
        private void Layout()
        {
            if (_root == null || _hero == null) return;
            if (Theme.IsScaling) return;
            // Resize 回调在 ScaleTree 之后跑，拿到的是物理宽度，常量一律 K()
            int avail = _root.ClientSize.Width;
            int availH = _root.ClientSize.Height;
            // 内容宽度上限 1020，下限跟着可视区走（不再钉死 620）。
            //
            // 原来下限写死 620：窗口缩到 ~760px（侧栏 238 + 内容区 522）时
            // composer 右边 ~100px 会被裁掉 —— 用户说「适配性不好」多半指这个。
            // 现在下限 = 内容区可用宽度 - 边距，下限本身不设最小值，
            // 由 `AutoScrollMinSize` 保证比可用区小时出滚动条而不是裁掉。
            int w = Math.Min(Theme.K(1020), Math.Max(Theme.K(380), avail - Theme.K(96)));
            int left = Math.Max(Theme.K(24), (avail - w) / 2);
            // hero 一行/两行随列宽自适应；下方所有区块按高度差整体平移，避免压字或留洞
            _hero.Width = w;
            _hero.FitWidth(w);
            _dy = (int)Math.Ceiling(_hero.Height / Theme.UiScale) - 132;
            int dy = Theme.K(_dy);
            int contentH = Theme.K(640) + dy;
            int top = Math.Max(Theme.K(24), Math.Min(Theme.K(110), (availH - contentH) / 2));

            _head.Location = new Point(left, top);
            _hero.Location = new Point(left, top + Theme.K(30));
            _intro.Location = new Point(left + Theme.K(2), top + Theme.K(172) + dy);

            // 三张模式卡：宽够时一排三张，不够时竖排三张。
            //
            // web 的 `.home-modes` 是 `repeat(3, 1fr)`，<860px 时折成 1fr 单列
            // （`@media(max-width:860px)`）。桌面这边原来是固定一排三张，
            // 窗口一窄（<~700px 内容区）每张就剩 ~110px，文字挤成一团。
            //
            // 阈值 600：比 web 的 860 低一档 —— web 那 860 是整个视口宽，
            // 这里的 w 是**内容区宽**（视口 - 侧栏 - 边距），所以要低一些。
            int gap = Theme.K(14);
            bool narrow = w < Theme.K(600);
            if (narrow)
            {
                // 竖排：一列三张，高度不变
                int cardH = _cards[0].Height;
                for (int i = 0; i < _cards.Length; i++)
                {
                    _cards[i].Width = w;
                    _cards[i].Location = new Point(
                        left, top + Theme.K(202) + dy + i * (cardH + Theme.K(10)));
                }
                // 下方的 composer 往下让（三张卡从一行变三行，要多让 ~2×（96+10））
                _composer.Location = new Point(
                    left, top + Theme.K(202) + dy + 3 * cardH + 2 * Theme.K(10) + gap);
            }
            else
            {
                int cw = (w - gap * 2) / 3;
                for (int i = 0; i < _cards.Length; i++)
                {
                    _cards[i].Width = cw;
                    _cards[i].Location = new Point(left + i * (cw + gap), top + Theme.K(202) + dy);
                }
                _composer.Location = new Point(left, top + Theme.K(312) + dy);
            }
            _composer.Width = w;
            _mission.Width = w - Theme.K(36);
            _divider.Width = w - Theme.K(36);
            _go.Location = new Point(w - Theme.K(156), Theme.K(164));

            // 下游元素（示例 / 提示）跟着 composer 走——
            // 窄屏竖排时 composer 挪下去 ~230px，它们也得跟着挪。
            int below = _composer.Location.Y + _composer.Height + Theme.K(20);
            _exLabel.Location = new Point(left + Theme.K(2), below);
            for (int i = 0; i < _chips.Length; i++)
                _chips[i].Location = new Point(
                    left + Theme.K(2) + i * Theme.K(162), below + Theme.K(24));
            _notice.Location = new Point(left + Theme.K(2), below + Theme.K(58));

            int bottom = _notice.Location.Y + Theme.K(30);
            _root.AutoScrollMinSize = new Size(w + Theme.K(48), bottom);
        }

        // ---------- 数据 ----------

        public async Task RefreshAsync()
        {
            if (_project == null || _project.IsDisposed) return;
            LoadProjects(_project.SelectedItem);
            await Task.CompletedTask;
        }

        private void LoadProjects(string keep)
        {
            try
            {
                var f = Core.JsonStore.Read<ProjectFile>(ProjectFilePath, null);
                _projects = f == null || f.Projects == null ? new List<ProjectItem>() : f.Projects;
            }
            catch { _projects = new List<ProjectItem>(); }

            var names = new List<string> { "未分类" };
            foreach (var p in _projects) names.Add(p.Name);
            _project.SetItems(names, string.IsNullOrEmpty(keep) ? "未分类" : keep);
        }

        private void SaveProjects()
        {
            Core.JsonStore.Write(ProjectFilePath, new ProjectFile { Projects = _projects });
        }

        // ---------- 动作 ----------

        private void Attach()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择附件";
                dlg.Filter = "所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(Shell()) != DialogResult.OK) return;
                string line = "[附件] " + dlg.FileName;
                _mission.Text = string.IsNullOrEmpty(_mission.Text) ? line : _mission.Text + "\n" + line;
            }
        }

        private void AddProject()
        {
            using (var dlg = new InputDialog("新建项目", "项目名称", ""))
            {
                if (dlg.ShowDialog(Shell()) != DialogResult.OK) return;
                var p = new ProjectItem
                {
                    Id = "proj-" + Guid.NewGuid().ToString("N").Substring(0, 10),
                    Name = dlg.Value,
                    CreatedAt = DateTime.Now,
                };
                _projects.Add(p);
                SaveProjects();
                LoadProjects(p.Name);
            }
        }

        private void Start()
        {
            string mission = (_mission.Text ?? "").Trim();
            if (mission.Length == 0)
            {
                _notice.Text = "先写下目标地址、题目描述或测试要求，再开始执行。";
                _notice.ForeColor = Theme.Warn;
                _mission.Focus();
                return;
            }
            _notice.Text = "🔒 仅用于已获授权的目标和赛事。操作记录保存在审计日志中。";
            _notice.ForeColor = Theme.Muted;

            string target = DeriveTarget(mission);
            string id = "sess-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-"
                      + Guid.NewGuid().ToString("N").Substring(0, 4);

            var session = new Core.Session
            {
                Id = id,
                Target = target,
                Mode = _mode,
                Mission = mission,
                FlagFormat = @"flag\{[a-z0-9_]+\}",
                Title = target,
                Phase = Core.SessionPhase.Pending,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };

            try
            {
                new Core.SessionStore().SaveSession(session);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Shell(), "保存任务失败：\n\n" + ex.Message, "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 归入所选项目
            string projName = _project.SelectedItem ?? "未分类";
            if (projName != "未分类")
            {
                foreach (var p in _projects)
                {
                    if (p.Name == projName)
                    {
                        if (p.Sessions == null) p.Sessions = new List<string>();
                        p.Sessions.Add(id);
                        break;
                    }
                }
                try { SaveProjects(); } catch { }
            }

            var shell = Shell() as MainForm;
            if (shell != null)
            {
                shell.RefreshSessions();
                UiSafety.Fire(shell.OpenSessionByIdAsync(id), "打开会话");
            }
        }

        /// <summary>从描述里提取一个可读的目标名：优先 URL，否则首行截断。</summary>
        private static string DeriveTarget(string mission)
        {
            foreach (var token in mission.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return token.Length > 80 ? token.Substring(0, 80) : token;
            }
            string first = mission.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return first.Length > 60 ? first.Substring(0, 60) + "…" : first;
        }

        private Form Shell()
        {
            return _root == null ? null : _root.FindForm();
        }
    }
}