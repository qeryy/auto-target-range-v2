// 灵枢桌面版 —— 页面：MCP 接入
//
// 管理外部 MCP 服务器：添加（stdio 命令 / http / sse 端点）、启停、测试连通、
// 查看对方暴露了哪些工具。
//
// 数据存 mcp.json；密钥类信息（http 的 Authorization）暂不落盘，
// 等用到的时候再按 SecretStore 的方式加密。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpiritScout.Tools;
using SpiritScout.Ui.Widgets;

namespace SpiritScout.Ui.Pages
{
    public sealed class McpPage : IPage
    {
        public string Id { get { return "mcp"; } }
        public string Title { get { return "MCP 接入"; } }
        public PageGroup Group { get { return PageGroup.Knowledge; } }

        private DataTable _grid;
        private DataTable _toolGrid;
        private Label _hint;
        private McpRepository _repo;

        public Control CreateView()
        {
            var root = new AmbientPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(26, 22, 26, 40),
            };

            var add = Theme.Primary("+ 添加服务器", 128);
            add.Click += (s, e) => Add();

            var edit = Theme.Ghost("编辑", 72);
            edit.Click += (s, e) => EditSelected();

            var test = Theme.Ghost("测试连接", 96);
            test.Click += (s, e) => UiSafety.Fire(TestSelectedAsync(), "测试 MCP 连接");

            var del = Theme.DangerBtn("删除", 72);
            del.Click += (s, e) => DeleteSelected();

            var refresh = Theme.Ghost("刷新", 72);
            refresh.Click += (s, e) => UiSafety.Fire(RefreshAsync(), "刷新 MCP");

            var head = Theme.PageHeader("MCP 接入",
                "外部工具服务器：连接后其工具可被智能体调用",
                add, edit, test, del, refresh);

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
            _grid.AddColumn("name", "名称", 1.4f);
            _grid.AddColumn("transport", "传输", 1f);
            _grid.AddColumn("target", "命令 / 端点", 3.2f);
            _grid.AddColumn("status", "状态", 0.9f);
            _grid.MakeChipColumn("status");   // 状态列画成胶囊

            _grid.EmptyText = "还没有 MCP 服务器。点「+ 添加服务器」开始——最常见的是一条 stdio 命令。";
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSelected(); };

            var top = new SectionCard("服务器", "stdio 命令 / http / sse 端点 · 双击行编辑",
                Theme.Accent, "◈")
            { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
            top.Body.Controls.Add(_grid);

            _toolGrid = new DataTable { Dock = DockStyle.Fill };
            _toolGrid.AddColumn("tool", "工具", 1.6f);
            _toolGrid.AddColumn("desc", "说明", 4.4f);

            var bottom = new SectionCard("暴露的工具", "测试连接后显示对方提供的工具",
                Theme.Ok, "✦")
            { Dock = DockStyle.Fill };
            bottom.Body.Controls.Add(_toolGrid);

            split.Controls.Add(top, 0, 0);
            split.Controls.Add(bottom, 0, 1);

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
            root.Controls.Add(head);
            root.Controls.Add(_hint);
            return root;
        }


        private McpRepository Repo()
        {
            if (_repo == null)
                _repo = new McpRepository(System.IO.Path.Combine(Core.AppPaths.DataRoot, "mcp.json"));
            return _repo;
        }

        public async Task RefreshAsync()
        {
            if (_grid == null || _grid.IsDisposed) return;
            var servers = Repo().List();
            var rows = new List<Dictionary<string, object>>();
            foreach (var s in servers)
            {
                string target = string.Equals(s.Transport, "stdio", StringComparison.OrdinalIgnoreCase)
                    ? (s.Command + " " + s.Args).Trim()
                    : s.Url;
                rows.Add(new Dictionary<string, object>
                {
                    { "name", s.Name },
                    { "transport", s.Transport },
                    { "target", target },
                    { "status", s.Enabled ? "已启用" : "已停用" },
                });
            }
            _grid.Fill(rows);
            _grid.Colorize("status", v => v == "已启用" ? Theme.Ok : Theme.Muted);

            _hint.Text = servers.Count == 0
                ? "还没有 MCP 服务器。点「+ 添加服务器」开始——最常见的是一条 stdio 命令。"
                : servers.Count + " 个服务器 · 双击一行可编辑";
            await Task.CompletedTask;
        }

        private void Add()
        {
            using (var dlg = new McpDialog(null))
            {
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                Repo().Upsert(dlg.Server);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private void EditSelected()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(FindShell(), "先选中一行。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string name = _grid.CellText(_grid.SelectedRows[0].Index, "name");
            var existing = Repo().List().Find(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return;

            using (var dlg = new McpDialog(existing))
            {
                if (dlg.ShowDialog(FindShell()) != DialogResult.OK) return;
                Repo().Upsert(dlg.Server);
                UiSafety.Fire(RefreshAsync(), "刷新列表");
            }
        }

        private void DeleteSelected()
        {
            if (_grid.SelectedRows.Count == 0) return;
            string name = _grid.CellText(_grid.SelectedRows[0].Index, "name");
            if (!UiSafety.Confirm(FindShell(), "删除服务器「" + name + "」？")) return;
            Repo().Delete(name);
            _toolGrid.Fill(new List<Dictionary<string, object>>());
            // 说清楚生效时机：已经打开的会话还握着旧连接（它是按会话装配的），
            // 要等重新打开才会按新配置装。不说明的话用户会以为没删掉。
            _hint.Text = "已删除「" + name + "」——重新打开会话后生效";
            _hint.ForeColor = Theme.Ok;
            UiSafety.Fire(RefreshAsync(), "刷新列表");
        }

        private async Task TestSelectedAsync()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(FindShell(), "先选中一行。", "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string name = _grid.CellText(_grid.SelectedRows[0].Index, "name");
            var server = Repo().List().Find(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (server == null) return;

            _hint.Text = "正在连接 " + name + " …";
            _hint.ForeColor = Theme.Accent;
            _toolGrid.Fill(new List<Dictionary<string, object>>());

            var probe = await McpClient.ProbeAsync(server).ConfigureAwait(true);

            var rows = new List<Dictionary<string, object>>();
            foreach (var t in probe.Tools)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "tool", t.Name },
                    { "desc", t.Description },
                });
            }
            _toolGrid.Fill(rows);

            _hint.Text = Theme.OneLine((probe.Ok ? "✓ " : "✗ ") + probe.Message, 200);
            _hint.ForeColor = probe.Ok ? Theme.Ok : Theme.Danger;
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

    /// <summary>MCP 服务器编辑对话框。</summary>
    public sealed class McpDialog : Form
    {
        public McpServer Server { get; private set; }

        private readonly bool _isNew;
        private TextBox _name;
        private ComboBox _transport;
        private TextBox _command;
        private TextBox _args;
        private TextBox _url;
        private TextBox _env;
        private TextBox _timeout;
        private CheckBox _enabled;
        private Label _error;

        public McpDialog(McpServer existing)
        {
            Theme.ApplyWin11(this);   // Win11 原生圆角
            _isNew = existing == null;
            Server = existing ?? new McpServer();

            Text = _isNew ? "添加 MCP 服务器" : "编辑 MCP 服务器";
            ClientSize = new Size(660, 570);
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
            int x = 24, y = 20, fx = 108;
            int fw = ClientSize.Width - fx - 32;

            Row("名称 *", x, y);
            _name = Theme.Input();
            _name.Text = Server.Name;
            _name.Location = new Point(fx, y);
            _name.Width = fw;
            Controls.Add(_name);
            y += 40;

            Row("传输 *", x, y);
            _transport = Theme.Select("stdio（本地子进程）", "http（Streamable HTTP）", "sse（旧版 SSE）");
            if (Server.Transport == "http") _transport.SelectedIndex = 1;
            else if (Server.Transport == "sse") _transport.SelectedIndex = 2;
            _transport.Location = new Point(fx, y);
            _transport.Width = 300;
            _transport.SelectedIndexChanged += (s, e) => SyncTransport();
            Controls.Add(_transport);
            y += 40;

            Row("命令", x, y);
            _command = Theme.Input();
            _command.Text = Server.Command;
            _command.Location = new Point(fx, y);
            _command.Width = fw;
            Controls.Add(_command);
            Hint("stdio 用：可执行文件路径，如 C:\\Python311\\python.exe 或 npx", fx, y + 26);
            y += 56;

            Row("参数", x, y);
            _args = Theme.Input();
            _args.Text = Server.Args;
            _args.Location = new Point(fx, y);
            _args.Width = fw;
            Controls.Add(_args);
            Hint("空格分隔，如 -m my_mcp_server --stdio", fx, y + 26);
            y += 56;

            Row("端点 URL", x, y);
            _url = Theme.Input();
            _url.Text = Server.Url;
            _url.Location = new Point(fx, y);
            _url.Width = fw;
            Controls.Add(_url);
            Hint("http / sse 用，如 http://127.0.0.1:9000/mcp", fx, y + 26);
            y += 56;

            Row("环境变量", x, y);
            _env = Theme.Input(true, 68);
            _env.Text = Server.Env;
            _env.Location = new Point(fx, y);
            _env.Width = fw;
            _env.ScrollBars = ScrollBars.Vertical;
            Controls.Add(_env);
            Hint("每行一个 KEY=VALUE，可留空；很多服务器靠它传 PYTHONPATH / 数据目录", fx, y + 70);
            y += 100;

            Row("超时(秒)", x, y);
            _timeout = Theme.Input();
            _timeout.Text = Server.TimeoutSeconds.ToString();
            _timeout.Location = new Point(fx, y);
            _timeout.Width = 100;
            Controls.Add(_timeout);
            y += 40;

            _enabled = new Widgets.Switch
            {
                Text = "启用（连接后其工具可被智能体调用）",
                Checked = Server.Enabled,
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(fx, y),
            };
            Controls.Add(_enabled);
            y += 36;

            _error = new Label
            {
                Location = new Point(x, y),
                Size = new Size(ClientSize.Width - 48, 30),
                ForeColor = Theme.Danger,
                Font = Theme.FontSmall,
            };
            Controls.Add(_error);

            var ok = Theme.Primary("保存", 104, 36);
            ok.Location = new Point(ClientSize.Width - 136, ClientSize.Height - 58);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);

            var cancel = Theme.Ghost("取消", 84, 36);
            cancel.Location = new Point(ClientSize.Width - 232, ClientSize.Height - 58);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            SyncTransport();
        }

        private void SyncTransport()
        {
            bool stdio = _transport.SelectedIndex == 0;
            _command.Enabled = stdio;
            _args.Enabled = stdio;
            _url.Enabled = !stdio;
        }

        private void Row(string text, int x, int y)
        {
            Controls.Add(Theme.FormLabel(text, x, y));
        }

        private void Hint(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = Theme.FontTiny,
            });
        }

        private void Submit()
        {
            _error.Text = "";
            string name = (_name.Text ?? "").Trim();
            if (name.Length == 0) { _error.Text = "名称不能为空。"; _name.Focus(); return; }

            bool stdio = _transport.SelectedIndex == 0;
            if (stdio && (_command.Text ?? "").Trim().Length == 0)
            {
                _error.Text = "stdio 传输必须填命令。";
                _command.Focus();
                return;
            }
            if (!stdio && (_url.Text ?? "").Trim().Length == 0)
            {
                _error.Text = "http / sse 传输必须填端点 URL。";
                _url.Focus();
                return;
            }

            int timeout;
            if (!int.TryParse((_timeout.Text ?? "").Trim(), out timeout) || timeout <= 0) timeout = 60;

            Server = new McpServer
            {
                Name = name,
                Transport = stdio ? "stdio" : (_transport.SelectedIndex == 1 ? "http" : "sse"),
                Command = (_command.Text ?? "").Trim(),
                Args = (_args.Text ?? "").Trim(),
                Url = (_url.Text ?? "").Trim(),
                Env = (_env.Text ?? "").Trim(),
                TimeoutSeconds = timeout,
                Enabled = _enabled.Checked,
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
