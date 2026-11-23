// 灵枢桌面版 —— 页面：模型
//
// 这是整个程序能不能用起来的关键一页：配好供应商（端点 + 密钥）、加一个模型、
// 点「测试连通」，之后会话才能调模型。
//
// 布局：
//   上：供应商列表（名称 / 协议 / 端点 / 密钥状态）
//   下：模型列表（名称 / 归属供应商 / 模型 ID / 上下文）
//   底：连通性测试 + 结果

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Llm;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class ModelsPage : IPage
    {
        public string Id { get { return "models"; } }
        public string Title { get { return "模型"; } }
        public PageGroup Group { get { return PageGroup.Agent; } }

        private ModelRegistry _registry;
        private DataTable _providerGrid;
        private FlowLayoutPanel _modelTiles;
        private FlowLayoutPanel _namingChain;
        private FlowLayoutPanel _namingPool;
        private Label _namingHint;
        private DataTable _modelGrid;
        private Label _hint;

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var addProvider = Theme.Primary("+ 供应商", 104);
            addProvider.Click += (s, e) => AddProvider();

            var addModel = Theme.Primary("+ 模型", 96);
            addModel.Click += (s, e) => AddModel();

            var test = Theme.Ghost("测试连通", 96);
            test.Click += (s, e) => UiSafety.Fire(TestSelectedAsync(), "测试连通");

            var refresh = Theme.Ghost("刷新", 72);
            refresh.Click += (s, e) => UiSafety.Fire(RefreshAsync(), "刷新模型");

            // 之前只有「加」，没有「删」——加错的供应商/模型只能靠改名绕过，
            // 而改名内部调的正是 DeleteModel，等于是绕了一圈。
            var del = Theme.DangerBtn("删除", 76);
            del.Click += (s, e) => DeleteSelected();

            // 模型顺序 = 默认模型的优先级（第一个 enabled 的当默认）。
            // web 用拖拽；桌面用两个按钮，见 ModelRegistry.MoveModel 的注释。
            var up = Theme.Ghost("↑ 上移", 76);
            up.Click += (s, e) => UiSafety.Fire(MoveSelectedModelAsync(-1), "上移模型");

            var down = Theme.Ghost("↓ 下移", 76);
            down.Click += (s, e) => UiSafety.Fire(MoveSelectedModelAsync(1), "下移模型");

            var head = Theme.PageHeader("模型",
                "先加供应商（端点 + 密钥），再在其中添加模型；「上移/下移」决定默认用哪个",
                addProvider, addModel, up, down, del, test, refresh);

            // web 的 naming-box：会话自动命名候选链。
            // 按顺序尝试模型，第一个能用的拿来起新会话标题；
            // 候选池里点一下就加入候选链，再点一次移除。
            var namingBox = new SectionCard("会话自动命名",
                "按顺序尝试命名模型；留空时用目标的原始名字",
                Theme.Accent, "◈")
            { Dock = DockStyle.Top, Height = Theme.K(150), Margin = new Padding(0, 0, 0, 14) };

            _namingChain = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Theme.K(34),
                BackColor = Color.Transparent,
                Padding = new Padding(18, 2, 18, 0),
                WrapContents = true,
            };
            _namingPool = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Theme.K(70),
                BackColor = Color.Transparent,
                Padding = new Padding(18, 2, 18, 0),
                WrapContents = true,
            };
            _namingHint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = Theme.K(20),
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(18, 0, 18, 0),
                BackColor = Color.Transparent,
            };

            // 链在上（有序），池在下（候选），提示在最底
            namingBox.Body.Controls.Add(_namingChain);
            namingBox.Body.Controls.Add(_namingPool);
            namingBox.Body.Controls.Add(_namingHint);

            // 两个分区块上下排：供应商 / 模型
            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
            };
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));

            _providerGrid = new DataTable { Dock = DockStyle.Fill };
            _providerGrid.AddColumn("name", "供应商", 1.6f);
            _providerGrid.AddColumn("protocol", "协议", 1.2f);
            _providerGrid.AddColumn("baseurl", "端点", 3.0f);
            _providerGrid.AddColumn("key", "密钥", 1.0f);
            _providerGrid.MakeChipColumn("key");   // 状态列画成胶囊
            _providerGrid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditProvider(e.RowIndex); };
            // 选中哪行供应商，下面就显示谁的模型（对齐 web 的从属关系）
            _providerGrid.SelectionChanged += (s, e) =>
            {
                try { RefillModelTiles(Registry()); } catch { }
            };

            _modelGrid = new DataTable { Dock = DockStyle.Fill };
            _modelGrid.AddColumn("name", "模型", 1.8f);
            _modelGrid.AddColumn("provider", "供应商", 1.4f);
            _modelGrid.AddColumn("modelid", "模型 ID", 2.6f);
            _modelGrid.AddColumn("ctx", "上下文", 1.0f, DataGridViewContentAlignment.MiddleRight);

            // 这条原来是写在 `_modelGrid` 上的 —— 但内容是「还没有供应商」，
            // 而 **供应商在上面那张表**。于是：上面那张空表格没有任何提示，
            // 下面「模型」表格却在讲供应商的事，用户要多想一步才明白。
            // 各归各位：
            _providerGrid.EmptyText = "还没有供应商。点「+ 供应商」开始——填端点地址和 API Key。";
            _modelGrid.EmptyText = "还没有模型。选中上面的供应商，再点「+ 模型」。";
            _modelGrid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditModel(e.RowIndex); };

            // web 的 providers-list 是**每个供应商一块**：
            //   pb-head（供应商行）→ mgrid（该供应商的模型磁贴网格 + [+添加]）
            // 桌面原来两个平铺表格 —— 模型和供应商平级，看不出从属。
            //
            // 现在：供应商卡在**上面**，每张卡下面挂一个「该供应商的模型」磁贴行
            // （FlowLayoutPanel + FlatButton 磁贴）。点哪个供应商，
            // 下面就显示谁的模型 —— 从属关系一眼明白。
            var providerBox = new SectionCard("供应商", "端点 + 密钥 · 双击行编辑",
                Theme.Accent, "◈")
            { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
            providerBox.Body.Controls.Add(_providerGrid);

            // 每个供应商下面的小磁贴区（先放一行「还没有模型」占位，
            // RefreshAsync 里按供应商填）
            _modelTiles = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Theme.K(58),
                BackColor = Color.Transparent,
                AutoScroll = true,
                WrapContents = false,          // 单行横排，不够滚
                Padding = new Padding(0, 0, 0, 2),
            };
            _modelTiles.Margin = new Padding(0, 8, 0, 0);
            providerBox.Body.Controls.Add(_modelTiles);
            providerBox.Body.Controls.SetChildIndex(_providerGrid, 0);   // 表格在上

            var modelBox = new SectionCard("模型", "顺序即默认优先级 · 双击行编辑",
                Theme.Violet, "✦")
            { Dock = DockStyle.Fill };
            modelBox.Body.Controls.Add(_modelGrid);

            split.Controls.Add(providerBox, 0, 0);
            split.Controls.Add(modelBox, 0, 1);

            // naming-box 加在 split 上面（Dock.Top，在 head 之下）
            root.Controls.Add(namingBox);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(split);
            root.Controls.Add(_hint);
            root.Controls.Add(head);
            return root;
        }

        /// <summary>
        /// 填命名链和候选池（web 的 naming-box）。
        ///
        /// chain = AppConfig.NamingChain 里按顺序的模型名；
        /// pool  = reg.Models() 里所有可用模型（点加入，再点移除）。
        /// </summary>
        private void RefillNaming(ModelRegistry reg)
        {
            if (_namingChain == null || _namingChain.IsDisposed) return;
            var cfg = new Core.ConfigManager();
            var chain = cfg.Current.NamingChain ?? new List<string>();

            // ---- 链：有序芯片 ----
            _namingChain.SuspendLayout();
            _namingChain.Controls.Clear();
            if (chain.Count == 0)
            {
                _namingChain.Controls.Add(new Label
                {
                    Text = "未配置 —— 新会话直接用目标的原始名字",
                    Font = Theme.FontTiny,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new Padding(0, Theme.K(4), 0, 0),
                });
            }
            for (int i = 0; i < chain.Count; i++)
            {
                var name = chain[i];
                var chip = new Widgets.FlatButton
                {
                    Text = (i + 1) + "  " + name + "  ✕",
                    Kind = Widgets.ButtonKind.Ghost,
                    Size = new Size(Theme.K(190), Theme.K(30)),
                    Font = Theme.FontSmall,
                    Margin = new Padding(0, 0, Theme.K(6), 0),
                    Tag = i,
                };
                int idx = i;
                chip.Click += delegate { RemoveNaming(idx); };
                _namingChain.Controls.Add(chip);
            }
            _namingChain.ResumeLayout();

            // ---- 池：候选模型 ----
            _namingPool.SuspendLayout();
            _namingPool.Controls.Clear();
            var allModels = reg.Models();
            if (allModels.Count == 0)
            {
                _namingPool.Controls.Add(new Label
                {
                    Text = "还没有模型 —— 先在下面添加",
                    Font = Theme.FontTiny,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                });
            }
            foreach (var m in allModels)
            {
                bool on = chain.Contains(m.Name);
                var btn = new Widgets.FlatButton
                {
                    Text = (on ? "● " : "○ ") + m.Name,
                    Kind = Widgets.ButtonKind.Ghost,
                    Size = new Size(Theme.K(200), Theme.K(30)),
                    Font = Theme.FontSmall,
                    Margin = new Padding(0, 0, Theme.K(6), Theme.K(6)),
                    Enabled = !on,
                };
                if (on) btn.ForeColor = Theme.Accent;
                var name = m.Name;
                btn.Click += delegate { AddNaming(name); };
                _namingPool.Controls.Add(btn);
            }
            _namingPool.ResumeLayout();

            if (_namingHint != null)
            {
                _namingHint.Text = chain.Count == 0
                    ? "按点击顺序加入候选链；第一个能用的模型负责起新会话标题。"
                    : "点链上的芯片移除；改动即刻生效。";
            }
        }

        private void AddNaming(string name)
        {
            var cfg = new Core.ConfigManager();
            var chain = cfg.Current.NamingChain ?? new List<string>();
            if (!chain.Contains(name)) chain.Add(name);
            cfg.Current.NamingChain = chain;
            cfg.Save();
            RefillNaming(Registry());
        }

        private void RemoveNaming(int idx)
        {
            var cfg = new Core.ConfigManager();
            var chain = cfg.Current.NamingChain ?? new List<string>();
            if (idx >= 0 && idx < chain.Count) chain.RemoveAt(idx);
            cfg.Current.NamingChain = chain;
            cfg.Save();
            RefillNaming(Registry());
        }

        /// <summary>
        /// 按当前选中的供应商填磁贴。没选中就填第一个供应商的。
        /// </summary>
        private void RefillModelTiles(ModelRegistry reg)
        {
            if (_modelTiles == null || _modelTiles.IsDisposed) return;
            _modelTiles.SuspendLayout();
            _modelTiles.Controls.Clear();

            string prov = null;
            if (_providerGrid.SelectedRows.Count > 0)
                prov = _providerGrid.SelectedRows[0].Cells[0].Value as string;
            if (string.IsNullOrEmpty(prov))
            {
                var all = reg.Providers();
                if (all.Count > 0) prov = all[0].Name;
            }
            if (string.IsNullOrEmpty(prov)) { _modelTiles.ResumeLayout(); return; }

            bool any = false;
            foreach (var m in reg.Models())
            {
                if (m.ProviderName != prov) continue;
                any = true;
                var t = new Widgets.FlatButton
                {
                    Text = m.Name,
                    Kind = Widgets.ButtonKind.Ghost,
                    Size = new Size(Theme.K(150), Theme.K(34)),
                    Font = Theme.FontSmall,
                    Margin = new Padding(0, 0, Theme.K(8), Theme.K(8)),
                };
                var cap = m;
                t.Click += delegate { /* 点磁贴编辑模型 */ };
                _modelTiles.Controls.Add(t);
            }
            if (!any)
            {
                _modelTiles.Controls.Add(new Label
                {
                    Text = prov + " 下还没有模型。点「+ 模型」添加。",
                    Font = Theme.FontTiny,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new Padding(0, Theme.K(4), 0, 0),
                });
            }
            _modelTiles.ResumeLayout();
        }

        private ModelRegistry Registry()
        {
            if (_registry == null)
            {
                _registry = new ModelRegistry(
                    Core.AppPaths.ModelsFile,
                    new SecretStore(Core.AppPaths.SecretsFile));
            }
            return _registry;
        }

        public async Task RefreshAsync()
        {
            if (_providerGrid == null || _providerGrid.IsDisposed) return;

            var reg = Registry();
            var provRows = new List<Dictionary<string, object>>();
            foreach (var p in reg.Providers())
            {
                provRows.Add(new Dictionary<string, object>
                {
                    { "name", p.Name },
                    { "protocol", p.Protocol == ApiProtocol.AnthropicCompatible ? "Anthropic" : "OpenAI 兼容" },
                    { "baseurl", p.BaseUrl },
                    { "key", reg.HasKey(p.Name) ? "已配置" : "未配置" },
                });
            }
            _providerGrid.Fill(provRows);
            _providerGrid.Colorize("key", v => v == "已配置" ? Theme.Ok : Theme.Warn);

            // 磁贴区按当前选中供应商填（没有选中就填第一个供应商的）
            RefillModelTiles(reg);

            var modelRows = new List<Dictionary<string, object>>();
            foreach (var m in reg.Models())
            {
                modelRows.Add(new Dictionary<string, object>
                {
                    { "name", m.Name },
                    { "provider", m.ProviderName },
                    { "modelid", m.ModelId },
                    { "ctx", m.ContextWindow > 0 ? m.ContextWindow.ToString() : "—" },
                });
            }
            _modelGrid.Fill(modelRows);

            RefillNaming(reg);

            _hint.Text = provRows.Count == 0
                ? "还没有供应商。点「+ 供应商」开始——填端点地址和 API Key。"
                : string.Format("{0} 个供应商 · {1} 个模型 · 双击表格行可编辑",
                    provRows.Count, modelRows.Count);

            await Task.CompletedTask;
        }

        // ---------- 供应商 ----------

        private void AddProvider()
        {
            using (var dlg = new ProviderDialog(null, Registry()))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    UiSafety.Fire(RefreshAsync(), "刷新列表");
                }
            }
        }

        private void EditProvider(int rowIndex)
        {
            string name = _providerGrid.CellText(rowIndex, "name");
            var spec = Registry().FindProvider(name);
            if (spec == null) return;
            using (var dlg = new ProviderDialog(spec, Registry()))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    UiSafety.Fire(RefreshAsync(), "刷新列表");
                }
            }
        }

        // ---------- 模型 ----------

        private void AddModel()
        {
            if (Registry().Providers().Count == 0)
            {
                MessageBox.Show(FindForm(), "先添加一个供应商，再往里加模型。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new ModelDialog(null, Registry()))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    UiSafety.Fire(RefreshAsync(), "刷新列表");
                }
            }
        }

        /// <summary>
        /// 把选中的模型往上/下挪一位。
        ///
        /// 挪完要**重新选中它** —— `RefreshAsync` 会整表重填，
        /// 行的 Index 变了，不重选的话用户会看到"选中跳到了别的行"。
        /// </summary>
        private async Task MoveSelectedModelAsync(int delta)
        {
            if (_modelGrid == null || _modelGrid.IsDisposed
                || _modelGrid.SelectedRows.Count == 0)
            {
                _hint.Text = "先在下面的「模型」表里选中一行。";
                _hint.ForeColor = Theme.Warn;
                return;
            }

            int row = _modelGrid.SelectedRows[0].Index;
            string name = _modelGrid.CellText(row, "name");
            if (string.IsNullOrEmpty(name)) return;

            if (!Registry().MoveModel(name, delta))
            {
                _hint.Text = delta < 0 ? "「" + name + "」已经是第一个了。" 
                                       : "「" + name + "」已经是最后一个了。";
                _hint.ForeColor = Theme.Warn;
                return;
            }

            await RefreshAsync().ConfigureAwait(true);

            // 重新选中被挪动的那一行
            if (_modelGrid != null && !_modelGrid.IsDisposed)
            {
                for (int i = 0; i < _modelGrid.Rows.Count; i++)
                {
                    if (string.Equals(_modelGrid.CellText(i, "name"), name, StringComparison.OrdinalIgnoreCase))
                    {
                        _modelGrid.ClearSelection();
                        _modelGrid.Rows[i].Selected = true;
                        _modelGrid.CurrentCell = _modelGrid.Rows[i].Cells[0];
                        break;
                    }
                }
            }
            _hint.Text = "「" + name + "」已" + (delta < 0 ? "上移" : "下移") + "一位";
            _hint.ForeColor = Theme.Ok;
        }

        private void EditModel(int rowIndex)
        {
            string name = _modelGrid.CellText(rowIndex, "name");
            var spec = Registry().FindModel(name);
            if (spec == null) return;
            using (var dlg = new ModelDialog(spec, Registry()))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    UiSafety.Fire(RefreshAsync(), "刷新列表");
                }
            }
        }

        /// <summary>
        /// 删除选中的模型或供应商。
        ///
        /// 优先看模型表（它在下面、更常用）；模型表没选中才看供应商表。
        /// 删供应商会连带删掉它下面的所有模型（ModelRegistry 里是这么做的），
        /// 所以确认框要把数量说清楚。
        /// </summary>
        private void DeleteSelected()
        {
            var reg = Registry();

            // 先看模型表
            if (_modelGrid != null && _modelGrid.SelectedRows.Count > 0)
            {
                string mname = _modelGrid.CellText(_modelGrid.SelectedRows[0].Index, "name");
                if (string.IsNullOrEmpty(mname)) return;
                if (!UiSafety.Confirm(FindForm(), "删除模型「" + mname + "」？")) return;
                reg.DeleteModel(mname);
                _hint.Text = "已删除模型「" + mname + "」";
                _hint.ForeColor = Theme.Ok;
                UiSafety.Fire(RefreshAsync(), "刷新列表");
                return;
            }

            // 再看供应商表
            if (_providerGrid != null && _providerGrid.SelectedRows.Count > 0)
            {
                string pname = _providerGrid.CellText(_providerGrid.SelectedRows[0].Index, "name");
                if (string.IsNullOrEmpty(pname)) return;

                int modelCount = 0;
                foreach (var m in reg.Models())
                {
                    if (string.Equals(m.ProviderName, pname, StringComparison.OrdinalIgnoreCase)) modelCount++;
                }
                string warn = modelCount > 0
                    ? "删除供应商「" + pname + "」？\n\n它下面的 " + modelCount + " 个模型也会一起删掉。"
                    : "删除供应商「" + pname + "」？";
                if (!UiSafety.Confirm(FindForm(), warn)) return;

                reg.DeleteProvider(pname);
                _hint.Text = "已删除供应商「" + pname + "」"
                    + (modelCount > 0 ? "（连带 " + modelCount + " 个模型）" : "");
                _hint.ForeColor = Theme.Ok;
                UiSafety.Fire(RefreshAsync(), "刷新列表");
                return;
            }

            MessageBox.Show(FindForm(), "先在上面的表里选中一行。", "灵枢",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task TestSelectedAsync()
        {
            if (_modelGrid == null || _modelGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show(FindForm(), "先在下面的「模型」表里选中一行。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string name = _modelGrid.CellText(_modelGrid.SelectedRows[0].Index, "name");
            _hint.Text = "正在测试 " + name + " …";
            _hint.ForeColor = Theme.Accent;

            var result = await Registry().TestAsync(name).ConfigureAwait(true);
            _hint.Text = Theme.OneLine((result.Key ? "✓ " : "✗ ") + result.Value, 200);
            _hint.ForeColor = result.Key ? Theme.Ok : Theme.Danger;
        }

        private Form FindForm()
        {
            Control c = _modelGrid;
            while (c != null)
            {
                var f = c as Form;
                if (f != null) return f;
                c = c.Parent;
            }
            return null;
        }
    }
}
