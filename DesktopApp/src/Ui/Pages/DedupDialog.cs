// 灵枢桌面版 —— 经验查重对话框
//
// ## 交互设计
//
// 「查重」是个**危险动作**：相似不等于重复，自动删可能删掉有用的东西。
// 所以这里做成：
//
//   · **只读列出**所有相似组，默认不动任何数据
//   · 用户**逐组**点「删除这组的重复项」才动手
//   · 删除规则固定：组内**最旧的那条保留**，其余删掉
//     （先提炼的通常更完整；这条规则在对话框上明确写出来）
//
// 不做「一键删除全部」——相似度阈值是启发式的，批量删的风险用户没法控制。
//
// ## 阈值可以当场调
//
// 不同经验库合适的阈值不一样：措辞规范的（都按「现象 → 手法 → 结论」写）
// 0.50 就够；随手记的短句得 0.75 才不误报。
//
// 所以对话框顶部放了个下拉（0.50 / 0.62 / 0.75），**改完立即重算**，
// 不用关掉重开。删除之后的重新计算也用**当前**阈值，不会跳回默认值。
//
// ## 数据来源
//
// `ExperienceDedup.FindSimilar(...)` 返回的组，纯计算，不写盘。
// 删除走 `ExperienceStore.Delete(id)`，和页面上的「删除」按钮同一条通路。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SpiritScout.Knowledge;

namespace SpiritScout.Ui.Pages
{
    public sealed class DedupDialog : Form
    {
        private readonly List<SimilarGroup> _groups;
        private readonly ExperienceStore _store;
        private readonly List<Core.Experience> _all;
        private Widgets.DataTable _grid;
        /// <summary>相似度阈值下拉。改它就重算，不用关掉重开。</summary>
        private ComboBox _threshold;
        private Label _hint;
        private Button _delBtn;   // Theme.DangerBtn 返回 Button（不一定是 FlatButton）

        /// <summary>删过东西就置 true，调用方据此决定要不要刷新列表。</summary>
        public bool Changed { get; private set; }

        public DedupDialog(ExperienceStore store, List<Core.Experience> all, List<SimilarGroup> groups)
        {
            _store = store;
            _all = all;
            _groups = groups ?? new List<SimilarGroup>();

            Text = "经验查重";
            Theme.ApplyWin11(this);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(880, 520);
            MinimumSize = new Size(680, 400);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            BuildUi();
            Fill();
        }

        private void BuildUi()
        {
            var root = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Padding = new Padding(16, 14, 16, 12),
            };

            var title = new Label
            {
                Text = "相似经验",
                Dock = DockStyle.Top,
                Height = 24,
                Font = Theme.FontHeading,
                ForeColor = Theme.Text,
                BackColor = Theme.Bg,
            };

            // 阈值栏：相似度是启发式的，不同经验库合适的阈值不一样
            // （措辞规范的低一点就够，随手记的高一点才不误报）。
            // 放在这里让人当场调，而不是回去改代码重编。
            var thBar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Bg };
            var thLabel = new Label
            {
                Text = "相似度阈值",
                Location = new Point(0, 8),
                AutoSize = true,
                Font = Theme.FontSmall,
                ForeColor = Theme.Muted,
                BackColor = Theme.Bg,
            };
            _threshold = Theme.Select("0.50 · 宽松（多报）", "0.62 · 默认", "0.75 · 严格（少报）");
            _threshold.Width = 168;
            _threshold.Location = new Point(84, 4);
            _threshold.SelectedIndex = 1;
            _threshold.SelectedIndexChanged += delegate
            {
                if (_recomputing) return;      // 构造期间设 SelectedIndex 会触发一次
                Recompute();
            };
            thBar.Controls.Add(_threshold);
            thBar.Controls.Add(thLabel);

            var sub = new Label
            {
                Text = "相似不等于重复。下面是按措辞相似度归出来的组，"
                     + "每组**最旧的那条建议保留**（先提炼的通常更完整），其余是候选删除项。"
                     + "确认哪组确实重复，再点那一组。",
                Dock = DockStyle.Top,
                Height = 44,
                Font = Theme.FontSmall,
                ForeColor = Theme.Muted,
                BackColor = Theme.Bg,
            };

            _grid = new Widgets.DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("no", "组", 0.35f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("count", "条数", 0.4f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("score", "相似度", 0.6f);
            _grid.AddColumn("keep", "建议保留", 3f);
            _grid.AddColumn("drop", "候选删除", 0.7f);
            _grid.MakeChipColumn("count");
            _grid.EmptyText = "没有发现相似经验。经验库看起来挺干净。";
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.SelectionChanged += delegate { UpdateButtons(); };
            _grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0) ShowPreview(e.RowIndex);
            };

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                Font = Theme.FontSmall,
                ForeColor = Theme.Muted,
                BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Bg };

            _delBtn = Theme.DangerBtn("删除这组的重复项", 150, 34);
            _delBtn.Location = new Point(0, 6);
            _delBtn.Click += delegate { DeleteSelectedGroup(); };
            bar.Controls.Add(_delBtn);

            var close = Theme.Ghost("关闭", 84, 34);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(bar.Width - 84, 6);
            bar.Resize += delegate
            {
                if (Theme.IsScaling) return;
                float u = Theme.UnitOf(bar);
                close.Location = new Point(bar.Width - (int)Math.Round(84 * u), (int)Math.Round(6 * u));
            };
            close.Click += delegate { Close(); };
            bar.Controls.Add(close);

            root.Controls.Add(_grid);
            root.Controls.Add(sub);
            root.Controls.Add(thBar);
            root.Controls.Add(title);
            root.Controls.Add(_hint);
            root.Controls.Add(bar);
            Controls.Add(root);
        }

        /// <summary>构造期间设 SelectedIndex 会触发 SelectedIndexChanged，用它挡掉。</summary>
        private bool _recomputing;

        /// <summary>当前选中的阈值。索引越界退回默认。</summary>
        private double CurrentThreshold()
        {
            int i = (_threshold == null || _threshold.IsDisposed) ? 1 : _threshold.SelectedIndex;
            if (i == 0) return 0.50;
            if (i == 2) return 0.75;
            return ExperienceDedup.DefaultThreshold;
        }

        /// <summary>按新阈值重算分组并刷新表格。</summary>
        private void Recompute()
        {
            _recomputing = true;
            try
            {
                var again = ExperienceDedup.FindSimilar(_all, CurrentThreshold());
                _groups.Clear();
                _groups.AddRange(again);
            }
            finally { _recomputing = false; }

            Fill();
            _hint.Text = "阈值 " + CurrentThreshold().ToString("F2") + " · 共 " + _groups.Count
                + " 组 · 合计 " + CountDuplicates() + " 条候选删除项 · 双击一行看完整内容";
        }

        private void Fill()
        {
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                string keep = g.Keep == null ? "" : Core.Text.OneLine(g.Keep.Insight, 70);
                int drop = g.Duplicates.Count;
                rows.Add(new Dictionary<string, object>
                {
                    { "no", i + 1 },
                    { "count", g.Items.Count + " 条" },
                    { "score", g.MinScore.ToString("F2") },
                    { "keep", keep },
                    { "drop", drop + " 条" },
                });
            }
            _grid.Fill(rows);
            if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;

            _hint.Text = _groups.Count == 0
                ? "共 0 组 · 经验库没有明显重复"
                : "共 " + _groups.Count + " 组 · 合计 "
                  + CountDuplicates() + " 条候选删除项 · 双击一行看完整内容";
            UpdateButtons();
        }

        private int CountDuplicates()
        {
            int n = 0;
            foreach (var g in _groups) n += g.Duplicates.Count;
            return n;
        }

        private void UpdateButtons()
        {
            if (_delBtn == null || _delBtn.IsDisposed) return;
            int i = SelectedIndex();
            _delBtn.Enabled = i >= 0 && i < _groups.Count;
        }

        private int SelectedIndex()
        {
            if (_grid == null || _grid.IsDisposed) return -1;
            return _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1;
        }

        private void ShowPreview(int index)
        {
            if (index < 0 || index >= _groups.Count) return;
            var g = _groups[index];

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < g.Items.Count; i++)
            {
                var e = g.Items[i];
                sb.Append(i == 0 ? "【保留】" : "【候选删除】")
                  .Append(e.CreatedAt.ToString("yyyy-MM-dd"));
                if (!string.IsNullOrEmpty(e.Mode)) sb.Append(" · ").Append(e.Mode);
                if (!string.IsNullOrEmpty(e.Tags)) sb.Append(" · ").Append(e.Tags);
                sb.Append("\n").Append(e.Insight ?? "").Append("\n\n");
            }
            MessageBox.Show(this, sb.ToString(),
                "第 " + (index + 1) + " 组（相似度 " + g.MinScore.ToString("F2") + "）",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeleteSelectedGroup()
        {
            int i = SelectedIndex();
            if (i < 0 || i >= _groups.Count) return;

            var g = _groups[i];
            var drops = g.Duplicates;
            if (drops.Count == 0) return;

            // 删除前把「要删什么、留什么」摆出来，不做静默批量删除
            var sb = new System.Text.StringBuilder();
            sb.Append("将删除 ").Append(drops.Count).Append(" 条：\n\n");
            foreach (var d in drops)
                sb.Append("· ").Append(Core.Text.OneLine(d.Insight, 80)).Append("\n");
            sb.Append("\n保留（最旧那条）：\n· ")
              .Append(g.Keep == null ? "" : Core.Text.OneLine(g.Keep.Insight, 80));

            // 走统一封装：标题、按钮文案、图标和其它 11 处确认框一致。
            if (!UiSafety.Confirm(this, sb.ToString(), "确认删除")) return;

            // 逐条删，**失败的要说出来**。
            // 原来这里 `catch { }` 吞掉异常，用户看到「已删除 2 条」但列表里还剩一条，
            // 只会觉得程序不听使唤。
            int deleted = 0;
            var failures = new List<string>();
            foreach (var d in drops)
            {
                try
                {
                    _store.Delete(d.Id);
                    _all.Remove(d);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failures.Add(Core.Text.OneLine(d.Insight, 40)
                        + "（" + Core.Text.OneLine(ex.Message, 60) + "）");
                }
            }
            if (deleted > 0) Changed = true;
            _groups.RemoveAt(i);

            // 删完重算一遍：删掉一条可能让某一组只剩 1 条（不再是重复组）。
            // **用当前阈值**，不然用户调过阈值之后一次删除又跳回默认值。
            var again = ExperienceDedup.FindSimilar(_all, CurrentThreshold());
            _groups.Clear();
            _groups.AddRange(again);

            Fill();
            if (failures.Count == 0)
            {
                _hint.Text = "已删除 " + deleted + " 条 · 重新计算后剩 " + _groups.Count + " 组";
            }
            else
            {
                _hint.Text = "已删除 " + deleted + " 条，**" + failures.Count + " 条失败** · 剩 "
                    + _groups.Count + " 组";
                MessageBox.Show(this,
                    "有 " + failures.Count + " 条没删掉：\n\n"
                    + string.Join("\n", failures.ToArray())
                    + "\n\n它们还在列表里。常见原因是文件被占用或没有写权限。",
                    "部分删除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
