// 灵枢桌面版 —— 页面：项目
//
// 一个「项目」把若干会话聚合在一起（比如同一个靶场、同一个客户）。
// 数据存 projects.json：
//   [{ "id":..., "name":..., "note":..., "sessions":["sess-xxx", ...] }]
//
// web 端的项目和资产台账是分开的两层；桌面版先做「项目」这一层，
// 资产（域名/IP/端口）等真用起来再补——空着比做假的好。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    /// <summary>一个项目。</summary>
    public sealed class ProjectItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Note { get; set; }
        public List<string> Sessions { get; set; }
        public DateTime CreatedAt { get; set; }

        public ProjectItem()
        {
            Id = "";
            Name = "";
            Note = "";
            Sessions = new List<string>();
            CreatedAt = DateTime.Now;
        }
    }

    public sealed class ProjectFile
    {
        public List<ProjectItem> Projects { get; set; }
        public ProjectFile() { Projects = new List<ProjectItem>(); }
    }

    public sealed class ProjectsPage : IPage
    {
        public string Id { get { return "projects"; } }
        public string Title { get { return "项目"; } }
        public PageGroup Group { get { return PageGroup.Agent; } }

        private DataTable _grid;
        private DataTable _sessionGrid;
        private Label _hint;
        private List<ProjectItem> _projects;

        private string FilePath
        {
            get { return System.IO.Path.Combine(Core.AppPaths.DataRoot, "projects.json"); }
        }

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var add = Theme.Primary("+ 新建项目", 112);
            add.Click += (s, e) => AddProject();

            var edit = Theme.Ghost("编辑", 72);
            edit.Click += (s, e) => EditSelected();

            var del = Theme.DangerBtn("删除", 72);
            del.Click += (s, e) => DeleteSelected();

            var refresh = Theme.Ghost("刷新", 72);
            refresh.Click += (s, e) => UiSafety.Fire(RefreshAsync(), "刷新项目");

            var head = Theme.PageHeader("项目", "把若干会话归到一个项目下",
                add, edit, del, refresh);

            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
            };
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));

            _grid = new DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("name", "项目", 2f);
            _grid.AddColumn("count", "会话", 0.7f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("note", "备注", 3f);
            _grid.AddColumn("created", "创建", 1f);
            // 空表得有话说。原来这里什么都没有 —— 一个项目都没有时，
            // 上面那半屏是一张空表格，「项目」小标题下面一片黑，
            // 用户不知道是该点哪里。下面的会话表格反倒有提示，对比更明显。
            _grid.EmptyText = "还没有项目。点「+ 新建项目」把相关会话归到一起。";
            _grid.SelectionChanged += (s, e) => ShowSessions();
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSelected(); };

            var top = new SectionCard("项目", "双击行编辑 · 选中看下面的会话",
                Theme.Accent, "◫")
            { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
            top.Body.Controls.Add(_grid);

            _sessionGrid = new DataTable { Dock = DockStyle.Fill };
            _sessionGrid.AddColumn("target", "会话目标", 2.4f);
            _sessionGrid.AddColumn("mode", "模式", 0.9f);
            _sessionGrid.AddColumn("phase", "状态", 0.9f);
            _sessionGrid.MakeChipColumn("phase");   // 状态列画成胶囊
            _sessionGrid.AddColumn("updated", "更新", 1.1f);


            _sessionGrid.EmptyText = "还没有项目。点「+ 新建项目」把相关会话归到一起。";
            var bottom = new SectionCard("选中项目下的会话", "状态胶囊 · 按更新时间排列",
                Theme.Ok, "◷")
            { Dock = DockStyle.Fill };
            bottom.Body.Controls.Add(_sessionGrid);

            split.Controls.Add(top, 0, 0);
            split.Controls.Add(bottom, 0, 1);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            root.Controls.Add(split);
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }


        // ---------- 数据 ----------

        private List<ProjectItem> Load()
        {
            var f = Core.JsonStore.Read<ProjectFile>(FilePath, null);
            return f == null || f.Projects == null ? new List<ProjectItem>() : f.Projects;
        }

        private void Save(List<ProjectItem> projects)
        {
            Core.JsonStore.Write(FilePath, new ProjectFile { Projects = projects ?? new List<ProjectItem>() });
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;
            _projects = Load();

            // 清理悬空引用：会话被删掉之后，项目里还留着它的 id。
            // 不清理的话文件会越攒越多无效条目，而且「会话」列的数字会虚高。
            //
            // 用 List() 一次拿到所有存活 id 做 HashSet，别对每个引用调 Load()——
            // 那是一个项目 50 个引用就要读 50 次文件。
            PruneDangling();

            var rows = new List<Dictionary<string, object>>();
            foreach (var p in _projects)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "name", p.Name },
                    { "count", p.Sessions == null ? 0 : p.Sessions.Count },
                    { "note", p.Note },
                    { "created", p.CreatedAt.ToString("MM-dd HH:mm") },
                });
            }
            _grid.Fill(rows);
            if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;

            _hint.Text = _projects.Count == 0
                ? "还没有项目。点「+ 新建项目」把相关会话归到一起。"
                : _projects.Count + " 个项目 · 双击一行可编辑";
            await Task.CompletedTask;
        }

        /// <summary>把指向已删除会话的引用去掉。有变更才写盘。</summary>
        private void PruneDangling()
        {
            try
            {
                var alive = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var s in new Core.SessionStore().List())
                {
                    if (!string.IsNullOrEmpty(s.Id)) alive.Add(s.Id);
                }

                bool changed = false;
                foreach (var p in _projects)
                {
                    if (p.Sessions == null) continue;
                    int before = p.Sessions.Count;
                    p.Sessions.RemoveAll(id => string.IsNullOrEmpty(id) || !alive.Contains(id));
                    if (p.Sessions.Count != before) changed = true;
                }

                if (changed)
                {
                    Save(_projects);
                    Core.ConfigManager.Log("项目页：已清理指向已删除会话的引用");
                }
            }
            catch (Exception ex)
            {
                Core.ConfigManager.Log("清理项目引用失败: " + ex.Message);
            }
        }

        private void ShowSessions()
        {
            if (_grid == null || _grid.SelectedRows.Count == 0) return;
            if (_projects == null) _projects = Load();

            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= _projects.Count) return;

            var project = _projects[idx];
            var want = new HashSet<string>(project.Sessions ?? new List<string>());
            var store = new Core.SessionStore();

            var rows = new List<Dictionary<string, object>>();
            foreach (var s in store.List())
            {
                if (!want.Contains(s.Id)) continue;
                rows.Add(new Dictionary<string, object>
                {
                    { "target", s.Target },
                    { "mode", Core.RunModeText.Label(s.Mode) },
                    { "phase", Core.PhaseText.Label(s.Phase) },
                    { "updated", s.UpdatedAt.ToString("MM-dd HH:mm") },
                });
            }
            _sessionGrid.EmptyText = rows.Count == 0
                ? "「" + project.Name + "」下还没有会话。去任务页跑一个，再编辑项目把它归进来。"
                : "";
            _sessionGrid.Fill(rows);
            _sessionGrid.Colorize("phase", v =>
            {
                if (v == "已完成") return Theme.Ok;
                if (v == "执行中") return Theme.Accent;
                if (v == "失败" || v == "已停止") return Theme.Danger;
                return null;
            });
        }

        private void AddProject()
        {
            using (var dlg = new ProjectDialog(null))
            {
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                var all = Load();
                all.Add(dlg.Result);
                Save(all);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private void EditSelected()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var all = Load();
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= all.Count) return;

            using (var dlg = new ProjectDialog(all[idx]))
            {
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                all[idx] = dlg.Result;
                Save(all);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private void DeleteSelected()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var all = Load();
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= all.Count) return;

            if (!UiSafety.Confirm(FindShell(), "删除项目「" + all[idx].Name + "」？\n\n只删项目分组，里面的会话不受影响。")) return;

            all.RemoveAt(idx);
            Save(all);
            _sessionGrid.Fill(new List<Dictionary<string, object>>());
            UiSafety.Fire(RefreshAsync(), "刷新列表");
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

    /// <summary>项目编辑对话框（含勾选要归入的会话）。</summary>
    public sealed class ProjectDialog : Form
    {
        public ProjectItem Result { get; private set; }

        private readonly ProjectItem _src;
        private readonly bool _isNew;
        private TextBox _name;
        private TextBox _note;
        private CheckedListBox _sessions;
        private Label _error;

        public ProjectDialog(ProjectItem existing)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            _isNew = existing == null;
            _src = existing ?? new ProjectItem();

            Text = _isNew ? "新建项目" : "编辑项目";
            ClientSize = new Size(620, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;

            Build();
        }

        private void Build()
        {
            int x = 22, y = 18, fx = 96;
            int fw = ClientSize.Width - fx - 30;

            Row("名称 *", x, y);
            _name = Theme.Input();
            _name.Text = _src.Name;
            _name.Location = new Point(fx, y);
            _name.Width = fw;
            Controls.Add(_name);
            y += 38;

            Row("备注", x, y);
            _note = Theme.Input();
            _note.Text = _src.Note;
            _note.Location = new Point(fx, y);
            _note.Width = fw;
            Controls.Add(_note);
            y += 38;

            Controls.Add(new Label
            {
                Text = "归入这个项目的会话（勾选）",
                ForeColor = Theme.Muted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(x, y),
            });
            y += 22;

            _sessions = new CheckedListBox
            {
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - x * 2, 280),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true,
                Font = Theme.FontBody,
            };

            var wanted = new HashSet<string>(_src.Sessions ?? new List<string>());
            var all = new Core.SessionStore().List();
            foreach (var s in all)
            {
                string label = "[" + Core.RunModeText.Label(s.Mode) + "] " + s.Target
                             + "   (" + s.Id + ")";
                _sessions.Items.Add(new SessionEntry(s.Id, label), wanted.Contains(s.Id));
            }
            Controls.Add(_sessions);
            y += 288;

            _error = new Label
            {
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - 44, 26),
                ForeColor = Theme.Danger,
                Font = Theme.FontSmall,
            };
            Controls.Add(_error);

            var ok = Theme.Primary("保存", 104, 36);
            ok.Location = new Point(ClientSize.Width - 134, ClientSize.Height - 56);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 36);
            cancel.Location = new Point(ClientSize.Width - 230, ClientSize.Height - 56);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Row(string text, int x, int y)
        {
            Controls.Add(Theme.FormLabel(text, x, y));
        }

        private void Submit()
        {
            _error.Text = "";
            string name = (_name.Text ?? "").Trim();
            if (name.Length == 0) { _error.Text = "名称不能为空。"; _name.Focus(); return; }

            var picked = new List<string>();
            foreach (var item in _sessions.CheckedItems)
            {
                var entry = item as SessionEntry;
                if (entry != null) picked.Add(entry.Id);
            }

            Result = new ProjectItem
            {
                Id = _isNew ? "proj-" + Guid.NewGuid().ToString("N").Substring(0, 10) : _src.Id,
                Name = name,
                Note = (_note.Text ?? "").Trim(),
                Sessions = picked,
                CreatedAt = _isNew ? DateTime.Now : _src.CreatedAt,
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class SessionEntry
        {
            public string Id { get; private set; }
            private readonly string _label;
            public SessionEntry(string id, string label) { Id = id; _label = label; }
            public override string ToString() { return _label; }
        }
    }
}
