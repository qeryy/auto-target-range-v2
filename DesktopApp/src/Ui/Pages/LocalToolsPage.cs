// 灵枢桌面版 —— 页面：本地工具
//
// 把用户自己那堆工具（nmap / sqlmap / binwalk / 自编译的小程序…）建个索引：
// 选一个或多个目录，扫一遍，按名字和大小列出来，双击直接在资源管理器里定位。
//
// 与 web 版的差别：web 版是「后端跑工具」，桌面版这里只做**索引与定位**——
// 真正执行由 agent 的 shell 工具完成（那是同一个权限，没必要绕一层）。
//
// 配置存 local-tools.json，索引每次刷新重扫（几千个文件秒级完成）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    /// <summary>本地工具配置。</summary>
    public sealed class LocalToolsConfig
    {
        public List<string> Directories { get; set; }
        public LocalToolsConfig() { Directories = new List<string>(); }
    }

    public sealed class LocalToolsPage : IPage
    {
        public string Id { get { return "localtools"; } }
        public string Title { get { return "本地工具"; } }
        public PageGroup Group { get { return PageGroup.Knowledge; } }

        private DataTable _grid;
        private Label _hint;
        private TextBox _search;
        private List<ToolEntry> _entries = new List<ToolEntry>();

        private string ConfigPath
        {
            get { return Path.Combine(Core.AppPaths.DataRoot, "local-tools.json"); }
        }

        private sealed class ToolEntry
        {
            public string Name;
            public string Dir;
            public string Path;
            public long Size;
            public DateTime Modified;
        }

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var addDir = Theme.Primary("+ 添加目录", 112);
            addDir.Click += (s, e) => AddDirectory();

            var rescan = Theme.Ghost("重新索引", 96);
            rescan.Click += async (s, e) => await RefreshAsync().ConfigureAwait(true);

            var openDir = Theme.Ghost("打开位置", 92);
            openDir.Click += (s, e) => OpenSelected();

            var delDir = Theme.DangerBtn("移除目录", 104);
            delDir.Click += (s, e) => RemoveDirectory();

            _search = Theme.Input();
            _search.Width = 200;
            _search.TextChanged += (s, e) => ApplyFilter();

            var head = Theme.PageHeader("本地工具",
                "把你常用的工具目录建个索引，双击一行直接在资源管理器里定位",
                addDir, rescan, openDir, delDir, _search);

            _grid = new DataTable { Dock = DockStyle.Fill };
            _grid.AddColumn("name", "名称", 2.4f);
            _grid.AddColumn("size", "大小", 0.9f, DataGridViewContentAlignment.MiddleRight);
            _grid.AddColumn("modified", "修改时间", 1.2f);
            _grid.AddColumn("dir", "所在目录", 3f);

            _grid.EmptyText = "还没有扫描到工具。点「+ 添加目录」把工具箱的位置选进来。";
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OpenSelected(); };

            var gridCard = new SectionCard("工具索引", "双击一行在资源管理器里定位",
                Theme.Warn, "◈")
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
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }

        private LocalToolsConfig LoadConfig()
        {
            return Core.JsonStore.Read<LocalToolsConfig>(ConfigPath, null) ?? new LocalToolsConfig();
        }

        private void SaveConfig(LocalToolsConfig cfg)
        {
            Core.JsonStore.Write(ConfigPath, cfg);
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;

            var cfg = LoadConfig();
            if (cfg.Directories.Count == 0)
            {
                _hint.Text = "还没有配置工具目录。点「+ 添加目录」把你放工具的地方选进来。";
                _grid.Fill(new List<Dictionary<string, object>>());
                _entries.Clear();
                return;
            }

            _hint.Text = "正在索引 " + cfg.Directories.Count + " 个目录…";
            _entries.Clear();

            foreach (var dir in cfg.Directories)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    // 只扫两层：工具目录一般不会更深，扫太深会把整个磁盘拖进来
                    ScanDir(dir, 0, _entries);
                }
                catch (Exception ex)
                {
                    Core.ConfigManager.Log("索引目录失败 " + dir + ": " + ex.Message);
                }
            }

            ApplyFilter();
            await Task.CompletedTask;
        }

        private void ScanDir(string dir, int depth, List<ToolEntry> into)
        {
            if (depth > 1 || into.Count > 5000) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (into.Count > 5000) return;
                    var fi = new FileInfo(f);
                    into.Add(new ToolEntry
                    {
                        Name = fi.Name,
                        Dir = dir,
                        Path = f,
                        Size = fi.Length,
                        Modified = fi.LastWriteTime,
                    });
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    if (into.Count > 5000) return;
                    ScanDir(d, depth + 1, into);
                }
            }
            catch { }
        }

        private void ApplyFilter()
        {
            if (_grid == null || _grid.IsDisposed) return;

            string kw = (_search.Text ?? "").Trim();
            var rows = new List<Dictionary<string, object>>();
            foreach (var e in _entries)
            {
                if (kw.Length > 0 &&
                    e.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Dir.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;

                rows.Add(new Dictionary<string, object>
                {
                    { "name", e.Name },
                    { "size", Core.Text.Size(e.Size) },
                    { "modified", e.Modified.ToString("yyyy-MM-dd HH:mm") },
                    { "dir", e.Dir },
                });
            }
            _grid.Fill(rows);

            if (_entries.Count == 0)
                _hint.Text = "配置的目录里没有找到文件。检查路径是否还在。";
            else
                _hint.Text = "索引 " + _entries.Count + " 个文件 · 显示 " + rows.Count
                    + " 个 · 双击打开所在位置";
        }

        /// <summary>
        /// 把选中的那一行所属的**目录**从索引里去掉。
        ///
        /// 表格里显示的是单个工具文件，但用户想移除的是「那个目录」——
        /// 所以要找到这一行属于哪个配置目录，从 cfg.Directories 里删。
        /// 只删配置，不动磁盘上的文件。
        /// </summary>
        private void RemoveDirectory()
        {
            if (_grid == null || _grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(FindShell(), "先在表里选中一行（它所在的目录会被移除）。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string file = _grid.CellText(_grid.SelectedRows[0].Index, "name");
            string full = _grid.CellText(_grid.SelectedRows[0].Index, "path");
            if (string.IsNullOrEmpty(full))
            {
                MessageBox.Show(FindShell(), "这一行没有路径信息。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 找出这个文件属于哪个已配置目录
            var cfg = LoadConfig();
            string owner = null;
            foreach (var d in cfg.Directories)
            {
                if (full.StartsWith(d, StringComparison.OrdinalIgnoreCase)) { owner = d; break; }
            }
            if (owner == null)
            {
                MessageBox.Show(FindShell(), "找不到这一行对应的目录。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!UiSafety.Confirm(FindShell(), "从索引里移除目录？\n\n" + owner + "\n\n只删索引，磁盘上的文件不动。")) return;

            cfg.Directories.Remove(owner);
            SaveConfig(cfg);
            _hint.Text = "已移除索引目录：" + owner;
            _hint.ForeColor = Theme.Ok;
            UiSafety.Fire(RefreshAsync(), "刷新列表");
        }

        private void AddDirectory()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择放工具的地方（会索引两层子目录）";
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;

                var cfg = LoadConfig();
                if (cfg.Directories.Contains(dlg.SelectedPath))
                {
                    MessageBox.Show(FindShell(), "这个目录已经加过了。", "灵枢",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                cfg.Directories.Add(dlg.SelectedPath);
                SaveConfig(cfg);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private void OpenSelected()
        {
            if (_grid == null || _grid.SelectedRows.Count == 0) return;
            int idx = _grid.SelectedRows[0].Index;
            string name = _grid.CellText(idx, "name");
            string dir = _grid.CellText(idx, "dir");

            // 用 /select 让资源管理器直接亮出这个文件
            string full = Path.Combine(dir, name);
            try
            {
                if (File.Exists(full))
                    Process.Start("explorer.exe", "/select,\"" + full + "\"");
                else if (Directory.Exists(dir))
                    Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindShell(), "打开失败：" + ex.Message, "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
}
