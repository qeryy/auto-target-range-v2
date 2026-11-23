// 灵枢桌面版 —— 页面：技能
//
// 技能分两层（见 Knowledge/SkillLibrary.cs）：
//   · 内置 288 条（编进 exe，只读，升级跟着版本走）
//   · 用户自定义（%APPDATA%\SpiritScout\my-skills.json，可增删改，升级不动）
//
// 页面能力：按领域筛选、搜索、看正文；**导入 / 新建 / 删除用户技能**；
// 内置技能不能删（会提示"内置技能只读"）。
//
// 新增/改动的用户技能会立刻被 kb_search 工具检索到——两边走同一个 SkillLibrary。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Knowledge;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class SkillsPage : IPage
    {
        public string Id { get { return "skills"; } }
        public string Title { get { return "技能"; } }
        public PageGroup Group { get { return PageGroup.Knowledge; } }

        private DataTable _grid;
        private TextBox _search;
        private ComboBox _domain;
        private TextBox _body;
        private Label _hint;
        private Label _title;

        /// <summary>当前显示的行对应的技能。</summary>
        private List<SkillEntry> _visible = new List<SkillEntry>();

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            _search = Theme.Input();
            _search.Width = 200;
            _search.TextChanged += (s, e) => ApplyFilter();

            _domain = Theme.Select("全部领域");
            _domain.Width = 140;
            _domain.SelectedIndexChanged += (s, e) => ApplyFilter();

            var addBtn = Theme.Primary("+ 新建技能", 108, 32);
            addBtn.Click += (s, e) => NewSkill();

            var importBtn = Theme.Ghost("导入", 72, 32);
            importBtn.Click += (s, e) => ImportSkills();

            var expBtn = Theme.Ghost("导出用户技能", 112, 32);
            expBtn.Click += (s, e) => ExportUserSkills();

            var delBtn = Theme.DangerBtn("删除", 72, 32);
            delBtn.Click += (s, e) => DeleteSelected();

            var head = Theme.PageHeader("技能",
                "内置 288 条 + 你自己的；写进这里的技能 agent 解题时会检索到",
                addBtn, importBtn, expBtn, delBtn, _domain, _search);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.Transparent,
                SplitterWidth = 14,
            };
            UiSafety.SafeSplit(split, 440);

            var left = new SectionCard("技能库", "领域 · 技能 · 来源", Theme.Accent, "✦")
            { Dock = DockStyle.Fill };
            _grid = new DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("domain", "领域", 0.9f);
            _grid.AddColumn("skill", "技能", 2.0f);
            _grid.AddColumn("src", "来源", 0.8f);
            _grid.MakeChipColumn("src");   // 状态列画成胶囊

            _grid.EmptyText = "还没有技能。技能库是内嵌的，如果你看到这条说明加载失败了。";
            _grid.SelectionChanged += (s, e) => ShowSelected();
            left.Body.Controls.Add(_grid);

            var right = new SectionCard("技能正文", "选中左侧一行查看", Theme.Violet, "✎")
            { Dock = DockStyle.Fill };
            _title = new Label
            {
                Text = "选一个技能查看正文",
                Font = Theme.FontHeading,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _body = new TextBox
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
            right.Body.Controls.Add(_body);
            right.Body.Controls.Add(_title);

            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);

            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
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

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;

            SkillLibrary.Invalidate();
            var all = SkillLibrary.All();

            // 领域下拉
            var domains = new List<string>();
            foreach (var s in all)
            {
                string d = string.IsNullOrEmpty(s.Domain) ? "未分类" : s.Domain;
                if (!domains.Contains(d)) domains.Add(d);
            }
            domains.Sort();
            if (_domain.Items.Count <= 1)
            {
                _domain.Items.Clear();
                _domain.Items.Add("全部领域");
                foreach (var d in domains) _domain.Items.Add(d);
                _domain.SelectedIndex = 0;
            }

            int builtin, user;
            SkillLibrary.Stats(out builtin, out user);

            if (all.Count == 0)
            {
                _hint.Text = "技能库为空。";
            }
            else
            {
                _hint.Text = "内置 " + builtin + " 条 + 自有 " + user + " 条 = " + all.Count
                    + " 条 · " + domains.Count + " 个领域 · 自有的可编辑";
            }

            ApplyFilter();
            await Task.CompletedTask;
        }

        private void ApplyFilter()
        {
            if (_grid == null || _grid.IsDisposed) return;
            var all = SkillLibrary.All();

            string kw = (_search.Text ?? "").Trim();
            string domain = _domain.SelectedItem == null ? "全部领域" : _domain.SelectedItem.ToString();

            var rows = new List<Dictionary<string, object>>();
            _visible = new List<SkillEntry>();

            foreach (var s in all)
            {
                string d = string.IsNullOrEmpty(s.Domain) ? "未分类" : s.Domain;
                if (domain != "全部领域" && !string.Equals(d, domain, StringComparison.OrdinalIgnoreCase))
                    continue;
                // **把 Body 也纳入搜索**。
                // 原来只搜 Id 和 Description，用户在技能正文里搜关键词什么都搜不到；
                // 而 `kb_search` 工具是搜正文的 —— 两边行为不一致。
                if (!Core.Text.Hit(kw, s.Id, s.Description, s.Body))
                    continue;

                rows.Add(new Dictionary<string, object>
                {
                    { "domain", d },
                    { "skill", s.Description.Length > 0 ? s.Description : s.Id },
                    { "src", s.Source == "user" ? "自有" : "内置" },
                });
                _visible.Add(s);
            }

            _grid.Fill(rows);
            _grid.Colorize("src", v => v == "自有" ? (Color?)Theme.Ok : null);
            if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;

            _hint.Text = "显示 " + _visible.Count + " / " + all.Count + " 条";
        }

        private void ShowSelected()
        {
            if (_grid == null || _grid.SelectedRows.Count == 0) return;
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= _visible.Count) return;

            var item = _visible[idx];
            _title.Text = item.Id + "    [" + (string.IsNullOrEmpty(item.Domain) ? "未分类" : item.Domain)
                + "]    " + (item.Source == "user" ? "自有" : "内置");
            _body.Text = string.IsNullOrEmpty(item.Body)
                ? item.Description
                : item.Description + "\r\n\r\n" + item.Body;
            _body.SelectionStart = 0;
            _body.ScrollToCaret();
        }

        // ---------- 增删改 ----------

        private void NewSkill()
        {
            using (var dlg = new SkillDialog(null))
            {
                if (dlg.ShowDialog(Shell()) != DialogResult.OK) return;
                var list = SkillLibrary.LoadUser();

                // 同名覆盖
                int i = list.FindIndex(x => string.Equals(x.Id, dlg.Result.Id,
                    StringComparison.OrdinalIgnoreCase));
                if (i >= 0) list[i] = dlg.Result; else list.Add(dlg.Result);

                SkillLibrary.SaveUser(list);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
                _hint.Text = "已保存技能「" + dlg.Result.Id + "」";
                _hint.ForeColor = Theme.Ok;
            }
        }

        private void ImportSkills()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "JSON 文件|*.json|所有文件|*.*";
                dlg.Title = "导入技能（skills.json 格式）";
                if (dlg.ShowDialog(Shell()) != DialogResult.OK) return;

                try
                {
                    var doc = Core.Json.AsDict(Core.Json.Parse(
                        System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8)));
                    var arr = Core.Json.Array(doc, "skills") ?? Core.Json.AsList(doc);
                    if (arr == null || arr.Count == 0)
                    {
                        _hint.Text = "文件里没有技能条目";
                        _hint.ForeColor = Theme.Warn;
                        return;
                    }

                    var list = SkillLibrary.LoadUser();
                    int added = 0, updated = 0;
                    foreach (var it in arr)
                    {
                        var d = Core.Json.AsDict(it);
                        if (d == null) continue;
                        string id = Core.Json.Str(d, "skill_id");
                        if (id.Length == 0) id = Core.Json.Str(d, "id");
                        if (id.Length == 0) continue;

                        var entry = new SkillEntry
                        {
                            Id = id,
                            Domain = Core.Json.Str(d, "domain", "未分类"),
                            Description = Core.Json.Str(d, "description"),
                            Body = Core.Json.Str(d, "body"),
                            Tags = Core.Json.Str(d, "tags"),
                            Source = "user",
                        };
                        int i = list.FindIndex(x => string.Equals(x.Id, id,
                            StringComparison.OrdinalIgnoreCase));
                        if (i >= 0) { list[i] = entry; updated++; }
                        else { list.Add(entry); added++; }
                    }

                    SkillLibrary.SaveUser(list);
                    UiSafety.Fire(RefreshAsync(), "刷新列表");
                    _hint.Text = "导入完成：新增 " + added + " 条，覆盖 " + updated + " 条";
                    _hint.ForeColor = Theme.Ok;
                }
                catch (Exception ex)
                {
                    _hint.Text = "导入失败：" + Theme.OneLine(ex.Message, 100);
                    _hint.ForeColor = Theme.Danger;
                }
            }
        }

        private void ExportUserSkills()
        {
            var list = SkillLibrary.LoadUser();
            if (list.Count == 0)
            {
                _hint.Text = "还没有自有技能可导出";
                _hint.ForeColor = Theme.Warn;
                return;
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "JSON 文件|*.json";
                dlg.FileName = "my-skills-" + DateTime.Now.ToString("yyyyMMdd") + ".json";
                if (dlg.ShowDialog(Shell()) != DialogResult.OK) return;
                try
                {
                    var export = new List<Dictionary<string, object>>();
                    foreach (var s in list)
                    {
                        export.Add(new Dictionary<string, object>
                        {
                            { "skill_id", s.Id },
                            { "domain", s.Domain },
                            { "description", s.Description },
                            { "body", s.Body },
                            { "tags", s.Tags },
                        });
                    }
                    Core.JsonStore.Write(dlg.FileName, new Dictionary<string, object>
                    {
                        { "skills", export },
                    });
                    _hint.Text = "已导出 " + list.Count + " 条到 " + dlg.FileName;
                    _hint.ForeColor = Theme.Ok;
                }
                catch (Exception ex)
                {
                    _hint.Text = "导出失败：" + Theme.OneLine(ex.Message, 100);
                    _hint.ForeColor = Theme.Danger;
                }
            }
        }

        private void DeleteSelected()
        {
            if (_grid == null || _grid.SelectedRows.Count == 0) return;
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= _visible.Count) return;

            var item = _visible[idx];
            if (item.Source != "user")
            {
                MessageBox.Show(Shell(),
                    "「" + item.Id + "」是内置技能，只读。\n\n"
                    + "要改成你自己的版本，可以新建一条同 id 的技能覆盖它。",
                    "灵枢", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!UiSafety.Confirm(Shell(), "删除自有技能「" + item.Id + "」？")) return;

            var list = SkillLibrary.LoadUser();
            list.RemoveAll(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            SkillLibrary.SaveUser(list);
            UiSafety.Fire(RefreshAsync(), "刷新列表");
            _hint.Text = "已删除「" + item.Id + "」";
            _hint.ForeColor = Theme.Ok;
        }

        private Form Shell()
        {
            return _grid == null ? null : _grid.FindForm();
        }
    }
}
