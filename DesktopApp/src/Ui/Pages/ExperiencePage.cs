// 灵枢桌面版 —— 页面：经验
//
// 经验库：从会话里提炼出的可复用洞察。支持按模式筛选、搜索、提炼新的、
// 删除、导出/导入 JSON。
//
// 提炼是这套系统「越用越聪明」的地方：一场任务跑完，提炼几条通用信号进来，
// 下次同类场景的会话会把它们召回进提示词。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Knowledge;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class ExperiencePage : IPage
    {
        public string Id { get { return "exp"; } }
        public string Title { get { return "经验"; } }
        public PageGroup Group { get { return PageGroup.Knowledge; } }

        private DataTable _grid;
        private ComboBox _mode;
        private TextBox _search;
        private Label _hint;
        private ExperienceStore _store;
        private List<Core.Experience> _items = new List<Core.Experience>();

        private string FilePath { get { return Core.AppPaths.ExperienceFile; } }

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var distill = Theme.Primary("从会话提炼", 112);
            distill.Click += async (s, e) => await DistillAsync().ConfigureAwait(true);

            var del = Theme.DangerBtn("删除", 72);
            del.Click += (s, e) => DeleteSelected();

            var export = Theme.Ghost("导出", 72);
            export.Click += (s, e) => Export();

            var import = Theme.Ghost("导入", 72);
            import.Click += (s, e) => Import();

            var refresh = Theme.Ghost("刷新", 72);
            refresh.Click += (s, e) => UiSafety.Fire(RefreshAsync(), "刷新经验");

            // 查重：提炼是模型生成的，同一件事可能被写成三条。
            // 只**建议**，删除由用户在对话框里逐组确认（相似不等于重复）。
            var dedup = Theme.Ghost("查重", 72);
            dedup.Click += (s, e) => Dedup();

            _mode = Theme.Select("全部", "CTF", "SRC 渗透", "智能体竞赛");
            _mode.Width = 130;
            _mode.SelectedIndexChanged += (s, e) => ApplyFilter();

            _search = Theme.Input();
            _search.Width = 200;
            _search.TextChanged += (s, e) => ApplyFilter();

            var head = Theme.PageHeader("经验",
                "从实战里提炼的可复用洞察；同类场景会召回进提示词",
                distill, del, export, import, dedup, refresh);

            var filterBar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.Transparent };
            filterBar.Controls.Add(new Label
            {
                Text = "模式",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(0, 9),
            });
            _mode.Location = new Point(40, 5);
            filterBar.Controls.Add(_mode);
            filterBar.Controls.Add(new Label
            {
                Text = "搜索",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(186, 9),
            });
            _search.Location = new Point(226, 5);
            filterBar.Controls.Add(_search);

            _grid = new DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("insight", "洞察", 5f);
            _grid.AddColumn("tags", "标签", 1.4f);
            _grid.AddColumn("mode", "模式", 0.9f);
            _grid.AddColumn("date", "时间", 1.1f);


            _grid.EmptyText = "还没有经验。跑完一个任务后用「从会话提炼」把洞察收进来。";

            var gridCard = new SectionCard("经验库", "洞察 · 标签 · 模式 · 时间",
                Theme.Violet, "✦")
            { Dock = DockStyle.Fill };
            gridCard.Body.Controls.Add(_grid);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(gridCard);
            root.Controls.Add(filterBar);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }

        private ExperienceStore Store()
        {
            if (_store == null) _store = new ExperienceStore(FilePath);
            return _store;
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;
            _items = Store().All();
            ApplyFilter();

            var counts = Store().CountByMode();
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add(LabelOf(kv.Key) + " " + kv.Value);
            _hint.Text = _items.Count == 0
                ? "还没有经验。跑完一个任务后用「从会话提炼」把洞察收进来。"
                : "共 " + _items.Count + " 条（" + string.Join(" · ", parts) + "）";
            await Task.CompletedTask;
        }

        private static string LabelOf(string mode)
        {
            if (mode == "src") return "SRC";
            if (mode == "competition") return "竞赛";
            return "CTF";
        }

        private static string ModeValue(int index)
        {
            switch (index)
            {
                case 1: return "ctf";
                case 2: return "src";
                case 3: return "competition";
                default: return "";
            }
        }

        private void ApplyFilter()
        {
            if (_grid == null || _grid.IsDisposed) return;

            string mode = ModeValue(_mode.SelectedIndex);
            string kw = (_search.Text ?? "").Trim();

            var rows = new List<Dictionary<string, object>>();
            var kept = new List<Core.Experience>();
            foreach (var e in _items)
            {
                if (mode.Length > 0 && !string.Equals(e.Mode, mode, StringComparison.OrdinalIgnoreCase))
                    continue;
                // 原来是「拼成一个 haystack 再找」，关键词跨字段边界时会误命中
                // （「洞察A 标签B」能匹配 "A 标签"）。改成逐字段判。
                if (!Core.Text.Hit(kw, e.Insight, e.Tags))
                    continue;

                rows.Add(new Dictionary<string, object>
                {
                    // 同上：只防超长，不切断正常内容
                    { "insight", Core.Text.OneLine(e.Insight, 2000) },
                    { "tags", e.Tags },
                    { "mode", LabelOf(e.Mode) },
                    { "date", e.CreatedAt.ToString("MM-dd HH:mm") },
                });
                kept.Add(e);
            }
            _grid.Fill(rows);
            if (kept.Count == 0 && _items.Count > 0)
                _hint.Text = "当前筛选下没有条目。";
        }

        /// <summary>
        /// 查重：找出可能重复的经验，弹对话框逐组确认。
        ///
        /// 不自动删——相似度是启发式的，误删比留着更糟。
        /// </summary>
        private void Dedup()
        {
            if (_items == null || _items.Count < 2)
            {
                MessageBox.Show(FindShell(), "经验太少，还谈不上重复。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var groups = ExperienceDedup.FindSimilar(_items, ExperienceDedup.DefaultThreshold);
            if (groups.Count == 0)
            {
                MessageBox.Show(FindShell(),
                    "没有发现相似经验（阈值 " + ExperienceDedup.DefaultThreshold.ToString("F2") + "）。",
                    "灵枢", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new DedupDialog(_store, _items, groups))
            {
                dlg.ShowDialog(FindShell());
                if (dlg.Changed)
                {
                    // 删过就重新读一遍再刷新表格（不要只改内存，避免和磁盘不一致）
                    UiSafety.Fire(RefreshAsync(), "刷新经验列表");
                }
            }
        }

        // ---------- 提炼 ----------

        private async Task DistillAsync()
        {
            var sessions = new Core.SessionStore().List();
            if (sessions.Count == 0)
            {
                MessageBox.Show(FindShell(), "还没有会话可以提炼。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string modelName = FirstModel();
            if (string.IsNullOrEmpty(modelName))
            {
                MessageBox.Show(FindShell(), "还没有可用模型。先去「模型」页配置。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new DistillDialog(sessions))
            {
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                var chosen = dlg.SelectedSession;
                if (chosen == null) return;

                _hint.Text = "正在提炼「" + chosen.Target + "」…";
                _hint.ForeColor = Theme.Accent;

                try
                {
                    var registry = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                        new Llm.SecretStore(Core.AppPaths.SecretsFile));
                    var distiller = new ExperienceDistiller(
                        new Core.SessionStore(), registry, Store());
                    var result = await distiller.DistillAsync(
                        chosen.Id, modelName, Core.RunModeText.ToValue(chosen.Mode))
                        .ConfigureAwait(true);
                    registry.Dispose();

                    _hint.Text = result.Key >= 0
                        ? "✓ " + result.Value
                        : "✗ " + Theme.OneLine(result.Value, 150);
                    _hint.ForeColor = result.Key >= 0 ? Theme.Ok : Theme.Danger;
                }
                catch (Exception ex)
                {
                    _hint.Text = "提炼失败：" + Theme.OneLine(ex.Message, 120);
                    _hint.ForeColor = Theme.Danger;
                }
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private string FirstModel()
        {
            try
            {
                var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                string name = "";
                foreach (var m in reg.Models())
                {
                    if (m.Enabled) { name = m.Name; break; }
                }
                reg.Dispose();
                return name;
            }
            catch { return ""; }
        }

        // ---------- 增删导 ----------

        private void DeleteSelected()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(FindShell(), "先选中一行。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string insight = _grid.CellText(_grid.SelectedRows[0].Index, "insight");
            if (!UiSafety.Confirm(FindShell(), "删除这条经验？\n\n" + Theme.OneLine(insight, 80))) return;

            var target = _items.Find(e => e.Insight == insight);
            if (target != null) Store().Delete(target.Id);
            UiSafety.Fire(RefreshAsync(), "刷新列表");
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "JSON 文件|*.json";
                dlg.FileName = "experience-" + DateTime.Now.ToString("yyyyMMdd") + ".json";
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                try
                {
                    Core.JsonStore.Write(dlg.FileName, Store().All());
                    _hint.Text = "已导出到 " + dlg.FileName;
                    _hint.ForeColor = Theme.Ok;
                }
                catch (Exception ex)
                {
                    _hint.Text = "导出失败：" + Theme.OneLine(ex.Message, 100);
                    _hint.ForeColor = Theme.Danger;
                }
            }
        }

        private void Import()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "JSON 文件|*.json";
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                try
                {
                    var incoming = Core.JsonStore.Read<List<Core.Experience>>(dlg.FileName, null);
                    if (incoming == null || incoming.Count == 0)
                    {
                        _hint.Text = "文件里没有经验条目。";
                        _hint.ForeColor = Theme.Warn;
                        return;
                    }
                    var all = Store().All();
                    int added = 0;
                    foreach (var e in incoming)
                    {
                        if (string.IsNullOrWhiteSpace(e.Insight)) continue;
                        bool dup = all.Any(x => string.Equals(
                            (x.Insight ?? "").Trim(), e.Insight.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (dup) continue;
                        if (string.IsNullOrEmpty(e.Id))
                            e.Id = "exp-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                        all.Insert(0, e);
                        added++;
                    }
                    Store().Save(all);
                    _hint.Text = "导入 " + added + " 条（跳过重复 " + (incoming.Count - added) + " 条）";
                    _hint.ForeColor = Theme.Ok;
                    UiSafety.Fire(RefreshAsync(), "刷新列表");
                }
                catch (Exception ex)
                {
                    _hint.Text = "导入失败：" + Theme.OneLine(ex.Message, 100);
                    _hint.ForeColor = Theme.Danger;
                }
            }
        }

        private Form FindShell()
        {
            Control c = _grid;
            while (c != null)
            {
                var f = c as Form;
                if (f != null) return f;
                c = c.Parent;
            }
            return null;
        }
    }

    /// <summary>选择要提炼的会话。</summary>
    public sealed class DistillDialog : Form
    {
        public Core.Session SelectedSession { get; private set; }

        private readonly ListBox _list;
        private readonly List<Core.Session> _sessions;

        public DistillDialog(List<Core.Session> sessions)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            _sessions = sessions;
            Text = "选择要提炼的会话";
            ClientSize = new Size(600, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            Controls.Add(new Label
            {
                Text = "从哪个会话提炼经验？（建议选已经跑完、有实质内容的）",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(20, 16),
            });

            _list = new ListBox
            {
                Location = new Point(20, 42),
                Size = new Size(ClientSize.Width - 40, 300),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FontBody,
                IntegralHeight = false,
            };
            foreach (var s in sessions)
            {
                _list.Items.Add(string.Format("[{0}] {1}   ({2} · {3})",
                    Core.RunModeText.Label(s.Mode),
                    s.Target,
                    Core.PhaseText.Label(s.Phase),
                    s.UpdatedAt.ToString("MM-dd HH:mm")));
            }
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            Controls.Add(_list);

            var ok = Theme.Primary("开始提炼", 104, 34);
            ok.Location = new Point(ClientSize.Width - 128, ClientSize.Height - 50);
            ok.Click += (s, e) =>
            {
                if (_list.SelectedIndex < 0) return;
                SelectedSession = _sessions[_list.SelectedIndex];
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 34);
            cancel.Location = new Point(ClientSize.Width - 220, ClientSize.Height - 50);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
